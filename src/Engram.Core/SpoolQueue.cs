namespace Engram.Core;

/// <summary>
/// The indexer's view of the <c>file-touched</c> queue: read everything, consume only what
/// this run actually indexed. A drain that deletes before its caller acts loses entries to
/// any failure after the read, and takes every repo's entries with it; this consumer
/// removes an entry only after the commit that made it redundant, and leaves other repos'
/// entries queued — the queue is folded, never pruned (D41).
/// </summary>
public sealed class SpoolQueue
{
    public static readonly SpoolQueue Empty = new([]);

    private enum Kind
    {
        Pathed,
        PathlessEntry,
        Garbage,
        Unreadable,
    }

    private sealed record Entry(string File, Kind Kind, string? Path);

    private readonly List<Entry> entries;

    private SpoolQueue(List<Entry> entries) => this.entries = entries;

    public static SpoolQueue Peek(string queueDir)
    {
        if (!Directory.Exists(queueDir))
        {
            return Empty;
        }

        var files = Directory.GetFiles(queueDir, "*.spool");
        Array.Sort(files, StringComparer.Ordinal);

        var entries = new List<Entry>(files.Length);
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Bytes that could not be obtained are not bytes proven meaningless: a
                // FileShare collision with the hook mid-write must not consume the edit.
                entries.Add(new Entry(file, Kind.Unreadable, null));
                continue;
            }

            entries.Add(SpoolReader.Parse(text) switch
            {
                { Path: { } path } => new Entry(file, Kind.Pathed, path),
                { } => new Entry(file, Kind.PathlessEntry, null),
                // Not parseable as an entry at all — the compactor already treats these
                // as removable garbage, not as a watermark.
                null => new Entry(file, Kind.Garbage, null),
            });
        }

        return new SpoolQueue(entries);
    }

    /// <summary>Parsed entries that carry a timestamp and no path: rescan signals.</summary>
    public int Pathless => entries.Count(entry => entry.Kind == Kind.PathlessEntry);

    /// <summary>
    /// Entries no run can ever consume or discard on trust. The pass-level count for a
    /// <c>--drain-all</c> report: unlike <see cref="LeftBehind"/>, which is root-scoped and so
    /// counts entries the same pass is about to service under another root as if they were loss,
    /// this is the one population that is genuinely left behind no matter which roots ran (§6.3e).
    /// </summary>
    public int Unreadable => entries.Count(entry => entry.Kind == Kind.Unreadable);

    /// <summary>
    /// This snapshot with its rescan signals removed. A pathless entry is a statement about the
    /// invocation and not about any one root (D41), so in a pass covering several roots exactly one
    /// may act on it — otherwise a single bare timestamp escalates every enrolled repo to a full
    /// scan, which is unbounded in the number of repos enrolled. Derived from the same captured
    /// list, so N views still cost one directory listing.
    /// </summary>
    public SpoolQueue WithoutPathless() =>
        new(entries.Where(e => e.Kind != Kind.PathlessEntry).ToList());

    /// <summary>Repo-relative paths of the entries this root can act on.</summary>
    public IReadOnlyList<string> Under(string root)
    {
        var canonicalRoot = PathCanonicalizer.Canonical(root);
        return entries
            .Where(entry => entry.Kind == Kind.Pathed && Relativize(canonicalRoot, entry.Path!) is not null)
            .Select(entry => Relativize(canonicalRoot, entry.Path!)!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Entries a run rooted here cannot consume: other repos' edits stay queued.</summary>
    public int LeftBehind(string root)
    {
        var canonicalRoot = PathCanonicalizer.Canonical(root);
        return entries.Count(entry =>
            entry.Kind == Kind.Unreadable
            || (entry.Kind == Kind.Pathed && Relativize(canonicalRoot, entry.Path!) is null));
    }

    /// <summary>
    /// Deletes what this run made redundant, and only that. Call after the work has
    /// committed — an entry deleted before its file was re-read is an edit destroyed.
    /// </summary>
    public int Consume(string root, bool consumePathless)
    {
        var consumed = 0;
        var canonicalRoot = PathCanonicalizer.Canonical(root);

        foreach (var entry in entries)
        {
            var consumable = entry.Kind switch
            {
                Kind.Pathed => Relativize(canonicalRoot, entry.Path!) is not null,
                Kind.PathlessEntry => consumePathless,
                Kind.Garbage => true,
                _ => false,
            };

            if (!consumable)
            {
                continue;
            }

            try
            {
                File.Delete(entry.File);
                consumed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return consumed;
    }

    /// <summary>
    /// Deletes every path-bearing entry that lies under none of <paramref name="servicedRoots"/>.
    /// This is the queue's only bound: file-touched cannot know which repos are enrolled (D4), so it
    /// spools for all of them forever, and SpoolCompactor folds rather than prunes — bounding the
    /// residue by distinct path count, never to zero (§4.9). Discarding is lossless because any repo
    /// whose entries are dropped here is full-scanned before its next drain (§4.9), which is guard 1.
    ///
    /// Pathless and unreadable entries are skipped unconditionally, so this is safe to call on the
    /// full snapshot. Garbage stays Consume's, so this changes no count that anything already reports.
    /// </summary>
    /// <param name="servicedRoots">
    /// The roots actually drained by this pass — accumulated as the loop runs, never re-derived.
    /// </param>
    /// <returns>The number of entries discarded.</returns>
    public int DiscardExcept(IReadOnlyCollection<string> servicedRoots)
    {
        var canonicalRoots = servicedRoots.Select(PathCanonicalizer.Canonical).ToList();
        var discarded = 0;

        foreach (var entry in entries)
        {
            if (entry.Kind != Kind.Pathed
                || canonicalRoots.Any(root => Relativize(root, entry.Path!) is not null))
            {
                continue;
            }

            try
            {
                File.Delete(entry.File);
                discarded++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return discarded;
    }

    private static string? Relativize(string canonicalRoot, string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return null;
        }

        // Both sides canonical, or /tmp and /private/tmp never meet: the hook records the
        // spelling the tool used, git reports the resolved one, and an entry that compares
        // as another repo's is an entry that never drains.
        var relative = Path.GetRelativePath(canonicalRoot, PathCanonicalizer.Canonical(path));
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }
}
