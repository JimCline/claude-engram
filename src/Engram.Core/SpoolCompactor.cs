namespace Engram.Core;

/// <summary>What a compaction found, and what it removed to get there.</summary>
/// <remarks>
/// <see cref="Before"/> always equals <see cref="Kept"/> + <see cref="Superseded"/> +
/// <see cref="Unparseable"/> + <see cref="Dropped"/>, on a dry run as much as on an apply. A
/// compaction that cannot account for every file it saw has a bug in it, and that is asserted
/// rather than described. <see cref="Unreadable"/> is a subset of <see cref="Kept"/> rather than a
/// fifth term: those files are still on disk, which is the whole point of counting them.
/// </remarks>
public sealed record SpoolCompaction(
    int Before,
    int Kept,
    int Superseded,
    int Unparseable,
    int Dropped,
    int Unreadable,
    int Paths,
    bool Pathless)
{
    /// <summary>Whether anything would change. A dry run and an apply agree on this.</summary>
    public bool Changes => Superseded + Unparseable + Dropped > 0;
}

/// <summary>
/// Folds the <c>file-touched</c> queue down to one entry per file that changed.
/// </summary>
/// <remarks>
/// <para><b>Why the queue needs this at all.</b> <c>file-touched</c> writes one file per edit and
/// never reads, which is what keeps it inside its budget (D39) — but it means the queue grows with
/// editing activity and shrinks only when something drains it. The drain is the code indexer,
/// which is not built. A queue that grows without bound waiting for a consumer that does not exist
/// yet is a defect in the binary that ships today, whenever that consumer arrives; on the author's
/// instance it had reached 1102 entries.</para>
///
/// <para><b>Why folding is lossless where pruning would not be.</b> A consumer of this queue
/// re-reads the file's current content — the queue says <i>which</i> files to look at, never what
/// they said. So for a given path, knowing it was touched at t1, t2 and t3 tells a consumer
/// nothing that t3 does not. Entries carrying no path are the other case, and the argument that
/// justified recording paths settles them too: a bare timestamp answers one bit no matter how many
/// of them there are. One is kept, and it is the <b>oldest</b>, because a bare timestamp's only use
/// is as a watermark — there are unindexed changes at least this old — and the earlier one is the
/// safe reading. For a path the <b>newest</b> is kept, because there the timestamp means last
/// touched and the content is read fresh regardless.</para>
///
/// <para><b>It only ever deletes.</b> Nothing here writes or renames a spool file, which buys
/// concurrency safety by construction rather than by a lock. Two compactions racing converge on the
/// same directory. A <c>file-touched</c> running alongside creates a name no listing here contains,
/// so its entry survives. A consumer draining alongside removes files this would have removed —
/// harmless — or files it would have kept, which is not a loss because draining is consuming
/// them. Surviving names still lead with <c>DateTime.Ticks</c>, so a name-ordered read stays
/// chronological.</para>
///
/// <para><b>A file it could not read is left alone.</b> Unreadable is not the same as unparseable:
/// the writer holds <c>FileShare.None</c> while writing, so a compaction racing an edit can be
/// refused the read on Windows. Deleting on a transient error would discard an edit that was fine.
/// An entry whose bytes were read and made no sense is deleted, because <c>Drain</c> would drop it
/// anyway and leaving it means carrying it forever.</para>
/// </remarks>
public static class SpoolCompactor
{
    /// <summary>
    /// Entry count at or below which compaction does nothing, so the steady state costs one listing
    /// and no reads.
    /// </summary>
    public const int Threshold = 256;

    /// <summary>
    /// Ceiling on distinct paths kept, so the bound does not rest on the assumption that a person
    /// edits few files. Past it the newest are kept and the rest are reported, never dropped
    /// silently.
    /// </summary>
    public const int MaxPaths = 10_000;

    /// <summary>
    /// Removes every entry superseded by a later one for the same file.
    /// </summary>
    /// <param name="queueDir">The spool directory. A missing one is an empty compaction.</param>
    /// <param name="apply">When false nothing is deleted and the result is what would happen.</param>
    /// <param name="force">Compact below <see cref="Threshold"/> too, for a caller that asked.</param>
    /// <param name="maxPaths">
    /// Overrides <see cref="MaxPaths"/>. Present so the ceiling can be tested at a size a test can
    /// afford to write, since a guard proven only by argument is a guard nobody has seen fire.
    /// </param>
    public static SpoolCompaction Compact(string queueDir, bool apply, bool force = false, int maxPaths = MaxPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueDir);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPaths);

        if (!Directory.Exists(queueDir))
        {
            return Empty;
        }

        var files = Directory.GetFiles(queueDir, "*.spool");

        if (!force && files.Length <= Threshold)
        {
            return Empty with { Before = files.Length, Kept = files.Length };
        }

        Array.Sort(files, StringComparer.Ordinal);

        var newestByPath = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        Candidate? oldestPathless = null;
        var doomed = new List<string>();
        var unparseable = 0;
        var unreadable = 0;

        foreach (var file in files)
        {
            if (ReadText(file) is not { } text)
            {
                unreadable++;
                continue;
            }

            if (SpoolReader.Parse(text) is not { } edit)
            {
                doomed.Add(file);
                unparseable++;
                continue;
            }

            var candidate = new Candidate(file, edit.At);

            if (edit.Path is not { Length: > 0 } path)
            {
                oldestPathless = oldestPathless is { } watermark
                    ? Settle(candidate, watermark, keepNewer: false, doomed)
                    : candidate;
                continue;
            }

            newestByPath[path] = newestByPath.TryGetValue(path, out var held)
                ? Settle(candidate, held, keepNewer: true, doomed)
                : candidate;
        }

        var superseded = doomed.Count - unparseable;
        var dropped = 0;

        if (newestByPath.Count > maxPaths)
        {
            var surplus = newestByPath.Values
                .OrderByDescending(entry => entry.At)
                .Skip(maxPaths)
                .ToList();

            doomed.AddRange(surplus.Select(entry => entry.File));
            dropped = surplus.Count;
        }

        if (apply)
        {
            foreach (var file in doomed)
            {
                Delete(file);
            }
        }

        var paths = Math.Min(newestByPath.Count, maxPaths);

        return new SpoolCompaction(
            Before: files.Length,
            Kept: paths + (oldestPathless is null ? 0 : 1) + unreadable,
            Superseded: superseded,
            Unparseable: unparseable,
            Dropped: dropped,
            Unreadable: unreadable,
            Paths: paths,
            Pathless: oldestPathless is not null);
    }

    /// <summary>Settles two entries competing for one slot and dooms the loser.</summary>
    private static Candidate Settle(Candidate challenger, Candidate held, bool keepNewer, List<string> doomed)
    {
        var challengerWins = keepNewer ? challenger.At >= held.At : challenger.At < held.At;

        doomed.Add(challengerWins ? held.File : challenger.File);

        return challengerWins ? challenger : held;
    }

    private static string? ReadText(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SpoolCompaction Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, Pathless: false);

    private readonly record struct Candidate(string File, DateTimeOffset At);
}
