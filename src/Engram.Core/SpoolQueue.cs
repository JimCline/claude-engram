namespace Engram.Core;

/// <summary>
/// The indexer's view of the <c>file-touched</c> queue: read everything, consume only what
/// this run actually indexed. <see cref="SpoolReader.Drain"/> deletes before its caller
/// acts and takes every repo's entries with it; this consumer removes an entry only after
/// the commit that made it redundant, and leaves other repos' entries queued — the queue
/// is folded, never pruned (D41).
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

    /// <summary>Repo-relative paths of the entries this root can act on.</summary>
    public IReadOnlyList<string> Under(string root) =>
        entries
            .Where(entry => entry.Kind == Kind.Pathed && Relativize(root, entry.Path!) is not null)
            .Select(entry => Relativize(root, entry.Path!)!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Entries a run rooted here cannot consume: other repos' edits stay queued.</summary>
    public int LeftBehind(string root) =>
        entries.Count(entry =>
            entry.Kind == Kind.Unreadable
            || (entry.Kind == Kind.Pathed && Relativize(root, entry.Path!) is null));

    /// <summary>
    /// Deletes what this run made redundant, and only that. Call after the work has
    /// committed — an entry deleted before its file was re-read is an edit destroyed.
    /// </summary>
    public int Consume(string root, bool consumePathless)
    {
        var consumed = 0;

        foreach (var entry in entries)
        {
            var consumable = entry.Kind switch
            {
                Kind.Pathed => Relativize(root, entry.Path!) is not null,
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

    private static string? Relativize(string root, string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }
}
