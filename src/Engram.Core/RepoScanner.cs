using System.Diagnostics;

namespace Engram.Core;

/// <summary>Lists the files a repository considers its own, or null if it cannot say.</summary>
public interface IFileLister
{
    /// <summary>
    /// Repo-relative paths, or <see langword="null"/> when this lister does not apply — the
    /// directory is not a checkout, or the tool is missing.
    /// </summary>
    IReadOnlyList<string>? List(string root);
}

/// <summary>Where the file list came from. Reported, because it changes what was excluded.</summary>
public enum ScanSource
{
    Git,
    DirectoryWalk,
}

/// <summary>Why a walk stopped. Anything but <see cref="Complete"/> means the count is a floor.</summary>
public enum ScanStop
{
    Complete,
    TimeBudget,
    FileCeiling,
}

/// <summary>What a scan is allowed to spend before it gives up and says so.</summary>
/// <remarks>
/// <para><b>Time bounds the whole scan; the ceiling bounds only the walk.</b> They are not two
/// spellings of one limit. A tree of a million empty directories runs forever under a file ceiling
/// alone, because <c>found</c> never grows — only the clock stops it. The ceiling answers the other
/// resource, memory, and it is deliberately kept off the git path: a monorepo that lists 150,000
/// files through <c>git ls-files</c> is completely enumerated, and calling it partial would disable
/// its deletions permanently. Time still applies there, because classifying those files is
/// Engram's own work rather than git's.</para>
///
/// <para>Measured on the machine this was found on: <c>engram doctor</c> in a home directory sat at
/// 100% of a core and <b>7.8 GB resident</b> after 106 seconds, still walking, having printed
/// nothing. A plain <c>find</c> — C, no globbing, no per-entry allocation — counted 1,318,043 files
/// there in 20 seconds and had not reached the end. The same repository lists 289 files through
/// <c>git ls-files</c> and 4,318 through an unpruned walk, so three hundred times separates the
/// largest plausible target from the accident and the ceiling has room to sit well clear of both.</para>
/// </remarks>
public sealed record ScanBudget(TimeSpan Time, int MaxFiles)
{
    /// <summary>For work the user asked for. Matches what <c>git ls-files</c> already gets.</summary>
    public static ScanBudget Default { get; } = new(GitFileLister.Timeout, 100_000);

    /// <summary>
    /// For <c>doctor</c>, which is run when something is already wrong and has to answer about it
    /// quickly. It reports rather than acts, so a partial count costs it nothing.
    /// </summary>
    public static ScanBudget Diagnostic { get; } = new(TimeSpan.FromSeconds(2), 100_000);
}

public sealed record RepoScan(
    ScanSource Source,
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<SkipReason, int> Skipped,
    ScanStop Stop = ScanStop.Complete)
{
    public int SkippedTotal => Skipped.Values.Sum();

    /// <summary>Whether <see cref="Files"/> is everything, or only what fitted in the budget.</summary>
    public bool Truncated => Stop != ScanStop.Complete;

    /// <summary>A one-line summary for <c>doctor</c> and the indexer's log.</summary>
    public string Summary()
    {
        var detail = Skipped
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{pair.Value} {pair.Key.ToString().ToLowerInvariant()}");

        var source = Source == ScanSource.Git ? "git" : "directory walk";
        var skipped = string.Join(", ", detail);
        var partial = Stop switch
        {
            ScanStop.TimeBudget => ", partial: the walk ran out of time",
            ScanStop.FileCeiling => ", partial: the walk hit its file ceiling",
            _ => string.Empty,
        };

        return skipped.Length == 0
            ? $"{Files.Count} files via {source}{partial}"
            : $"{Files.Count} files via {source}, skipped {skipped}{partial}";
    }
}

/// <summary>
/// Asks git which files belong to a repository, and walks the directory only when it cannot.
/// </summary>
/// <remarks>
/// <para><b>git is the authority wherever there is a checkout.</b> <c>git ls-files</c> already
/// excludes build output, dependency directories, caches and temporary files — correctly, per
/// nested <c>.gitignore</c>, per <c>.git/info/exclude</c>, and per the developer's global ignore
/// file. Every one of those is a decision the developer already made about their own tree, and a
/// hand-maintained exclusion list inside Engram would be a worse, staler copy of a file the repo
/// already ships.</para>
///
/// <para>Tracked <i>and</i> untracked-but-not-ignored, because a file written five minutes ago is
/// exactly the file a coding agent is about to be asked about. Ignored files are the ones left
/// out, which is the whole point.</para>
///
/// <para>The configured globs still apply on top. git's opinion is about what belongs in the
/// repository, not about what is worth reading — a vendored bundle can be committed deliberately
/// and still be junk to index.</para>
/// </remarks>
public static class RepoScanner
{
    /// <summary>How often the clock is read inside a directory. Reading it per entry is itself a cost.</summary>
    private const int ClockInterval = 1024;

    public static RepoScan Scan(
        string root,
        IndexingSettings settings,
        IFileLister? lister = null,
        ScanBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(settings);

        var filter = new IndexFilter(settings);
        var full = Path.GetFullPath(root);
        var allowance = budget ?? ScanBudget.Default;
        var clock = Stopwatch.StartNew();

        var listed = settings.UseGit ? (lister ?? new GitFileLister()).List(full) : null;
        var source = listed is null ? ScanSource.DirectoryWalk : ScanSource.Git;

        var files = new List<string>();
        var skipped = new Dictionary<SkipReason, int>();
        var stop = ScanStop.Complete;
        var candidates = listed ?? Walk(full, filter, skipped, allowance, clock, out stop);

        // One clock across both halves, because finding a candidate is the cheaper half.
        // Classifying one reads its head to tell source from binary from generated, and the run
        // that found this bug spent two seconds walking and then six more inspecting the 100,000
        // paths the walk had already been stopped from exceeding. A budget that covers only
        // enumeration bounds the wrong thing.
        var inspected = 0;

        foreach (var relative in candidates.Distinct(StringComparer.Ordinal))
        {
            // From the first candidate, not only every ClockInterval after it: a walk that has
            // already spent the whole budget must not then start classifying what it found. Written
            // as one check rather than a pre-check plus a periodic one because two checks against
            // the same clock cannot be told apart by a test — whichever runs first answers for both,
            // and the other could be deleted with the suite still green.
            if (inspected++ % ClockInterval == 0 && clock.Elapsed >= allowance.Time)
            {
                stop = ScanStop.TimeBudget;
                break;
            }

            var verdict = filter.Inspect(relative, Path.Combine(full, relative));
            if (verdict.Include)
            {
                files.Add(relative);
            }
            else
            {
                skipped[verdict.Reason] = skipped.GetValueOrDefault(verdict.Reason) + 1;
            }
        }

        files.Sort(StringComparer.Ordinal);

        return new RepoScan(source, files, skipped, stop);
    }

    /// <summary>
    /// The fallback for a directory that is not a checkout.
    /// </summary>
    /// <remarks>
    /// <para>Pruned at the directory rather than filtered at the file, so an ignored
    /// <c>node_modules</c> costs one pattern match instead of a hundred thousand — which is the
    /// difference between a scan that finishes and one that does not.</para>
    ///
    /// <para>It stops at a checkout boundary: a subdirectory holding <c>.git</c> — a directory
    /// in a plain clone, a file in a worktree or submodule — is another repository, whose files
    /// belong to its own identity, and it counts once rather than per file, matching what
    /// <c>git ls-files</c> reports for the same shape. A directory <i>named</i> <c>.git</c> is
    /// the scanned root's own plumbing and is skipped without comment.</para>
    ///
    /// <para><b>It is bounded, and it says when the bound was reached.</b> Pruning is only as good
    /// as the patterns, and none of the defaults describe a home directory — <c>Library</c>,
    /// package caches and download folders are neither checkouts nor <c>node_modules</c>, so
    /// <c>engram doctor</c> run from <c>$HOME</c> walked all of it. The alternative to a bound is
    /// not a slower answer, it is no answer plus an exhausted machine, which is what
    /// <see cref="ScanBudget"/> records. Truncation is reported rather than absorbed: a caller
    /// that treats a partial list as complete draws the opposite conclusion about every path past
    /// the cut.</para>
    /// </remarks>
    private static List<string> Walk(
        string root,
        IndexFilter filter,
        Dictionary<SkipReason, int> skipped,
        ScanBudget budget,
        Stopwatch clock,
        out ScanStop stop)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        var inspected = 0;

        stop = ScanStop.Complete;
        pending.Push(root);

        while (pending.Count > 0)
        {
            if (clock.Elapsed >= budget.Time)
            {
                stop = ScanStop.TimeBudget;
                return found;
            }

            var directory = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                // Also inside the loop, not only per directory: one directory may hold millions of
                // entries, and checking on the pop alone bounds how many directories are visited
                // rather than how much work is done.
                if (++inspected % ClockInterval == 0 && clock.Elapsed >= budget.Time)
                {
                    stop = ScanStop.TimeBudget;
                    return found;
                }

                if (found.Count >= budget.MaxFiles)
                {
                    stop = ScanStop.FileCeiling;
                    return found;
                }

                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');

                if (Directory.Exists(entry))
                {
                    if (Path.GetFileName(entry) == ".git")
                    {
                        continue;
                    }

                    // Match the directory both bare and with a trailing slash, so "**/bin/**"
                    // prunes "bin" itself rather than only the files inside it.
                    if (filter.IsIgnored(relative) || filter.IsIgnored(relative + "/"))
                    {
                        continue;
                    }

                    var gitMarker = Path.Combine(entry, ".git");
                    if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                    {
                        skipped[SkipReason.EmbeddedCheckout] =
                            skipped.GetValueOrDefault(SkipReason.EmbeddedCheckout) + 1;
                        continue;
                    }

                    pending.Push(entry);
                    continue;
                }

                found.Add(relative);
            }
        }

        return found;
    }
}

/// <summary>Runs <c>git ls-files</c> in the target directory.</summary>
public sealed class GitFileLister : IFileLister
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public IReadOnlyList<string>? List(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!Directory.Exists(root))
        {
            return null;
        }

        // -z because a path may legally contain a newline, and splitting on one would invent
        // two files that do not exist.
        var output = Run(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard");

        return output?
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    internal static string? Run(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            // Read before waiting: a large repository fills the pipe buffer, and a process
            // blocked on a full stdout never exits, so waiting first would deadlock until the
            // timeout on exactly the repositories this matters most for.
            var stdout = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            // Not a checkout, or git is unhappy. Either way the caller walks the directory.
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // git is not installed. A perfectly ordinary machine state, not an error.
            return null;
        }
    }
}
