using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// Turns a repository's files into code facts. Dry-run by default, like everything that
/// changes the store: the report is the same analysis that <c>--apply</c> performs,
/// stopped before the writes.
/// </summary>
internal static class IndexCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var apply = false;
        var drain = false;
        var drainAll = false;
        var full = false;
        var auto = false;
        var freshen = false;
        var skip = new List<string>();
        string? target = null;

        for (var i = 0; i < rest.Length; i++)
        {
            var argument = rest[i];
            switch (argument)
            {
                case "--apply":
                    apply = true;
                    break;
                case "--drain":
                    drain = true;
                    break;
                case "--drain-all":
                    drain = true;
                    drainAll = true;
                    break;
                case "--full":
                    full = true;
                    break;
                case "--auto":
                    auto = true;
                    break;
                case "--freshen":
                    freshen = true;
                    break;
                case "--skip":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine("error: --skip requires a directory argument");
                        return 1;
                    }

                    skip.Add(rest[++i]);
                    break;
                default:
                    if (argument.StartsWith('-') || target is not null)
                    {
                        stderr.WriteLine($"error: unexpected argument '{argument}'");
                        return 1;
                    }

                    target = argument;
                    break;
            }
        }

        if (freshen)
        {
            if (drain || drainAll || full || target is not null)
            {
                stderr.WriteLine(
                    "error: --freshen is mutually exclusive with --drain, --drain-all, --full, and a target directory");
                return 1;
            }

            return RunFreshen(homePath, skip, apply, stdout, stderr);
        }

        var root = Path.GetFullPath(target ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            stderr.WriteLine($"error: no directory at {root}");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = IndexingSettings.Read(config);
        var identity = CodeIndexer.ResolveIdentity(root);

        // --auto is the session-start maintenance child asking. The whole policy lives
        // here, like --if-due and --if-large: the config gate, the requirement that the
        // directory actually is a git checkout (a shell that happens to start in $HOME
        // must not index $HOME), and an existing store. Every refusal is silent success —
        // housekeeping declining to run is not an error a hook should surface.
        if (auto)
        {
            if (!settings.AutoIndexOnSessionStart
                || !File.Exists(home.DatabasePath)
                || !CodeIndexer.IsGitCheckout(root))
            {
                return 0;
            }

            // Read-only and scoped to this check alone: --auto must stay a silent refusal,
            // never a migration or a write, and this runs inside the detached maintenance
            // child rather than on the session-start hook's own clock, so the subprocess
            // fallback inside IsEnrolled is affordable here (D4).
            using var enrollmentConnection = EngramDatabase.Open(home);
            if (!RepoEnrollment.IsEnrolled(enrollmentConnection, root))
            {
                return 0;
            }
        }

        foreach (var problem in settings.Problems)
        {
            stderr.WriteLine($"warning: {problem}");
        }

        if (!apply && !File.Exists(home.DatabasePath))
        {
            // Opening would create an empty database, and a dry run that leaves a file
            // behind is not a dry run.
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        // Emitted for a dry run too. The scan is the slow half and it happens either way, so
        // "is Engram busy with the repo" is answered the same for both; what differs is whether
        // anything is written, which the report says.
        IndexTelemetry.Note(home, "cli", "started", identity);

        try
        {
            using var connection = apply
                ? EngramDatabase.OpenInitialized(home)
                : EngramDatabase.Open(home);

            // Peeked once, up front, so a --drain-all pass reads the spool directory a single
            // time and every root — this one and each secondary root below — drains against the
            // same captured list rather than a fresh listing per repo (§6.3e).
            var sharedQueue = drainAll ? SpoolQueue.Peek(home.QueueDir) : null;

            var report = CodeIndexer.Index(
                connection,
                home,
                config,
                settings,
                new IndexOptions(root, apply, drain, full, Queue: sharedQueue),
                DateTimeOffset.UtcNow);

            IndexTelemetry.Note(home, "cli", "finished", identity);

            // §6.4: a command someone typed never silently no-ops on lock contention — print the
            // note and exit non-zero, bypassing the normal report print (which would otherwise
            // dump an all-zero report around it).
            var lockNote = report.Notes.FirstOrDefault(
                n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));
            if (lockNote is not null)
            {
                stdout.WriteLine($"{identity}: {lockNote}");
                return 1;
            }

            // DiscardExcept deletes queue state, so the whole secondary-root pass — including
            // discarding — is gated on --apply the same way Consume is (D49): a dry run must not
            // move state, and printing "N left for other repos" implies a discard that never runs.
            var draining = drainAll && apply && sharedQueue is not null;

            Print(report, stdout, reportQueueLine: !draining);

            if (draining)
            {
                var servicedRoots = DrainOtherEnrolledRoots(
                    connection, home, config, settings, report.Root, apply, sharedQueue!.WithoutPathless(), stdout);
                var discarded = sharedQueue.DiscardExcept(servicedRoots);

                // Pass-level, not per-repo (§6.3e detail 3): LeftBehind(root) would count entries
                // this same pass is about to service under another root as if they were loss.
                // Unreadable is the only population this line reports. A pathless entry has
                // nothing to report here: in drain-all mode Pathless > 0 always forces the
                // invoked root's own scan to full (CodeIndexer.cs:115) and that root always
                // consumes it (:235), so by the time this line runs it is never actually
                // outstanding — CodeIndexer.cs:118 already states the consequence at the root
                // that acted, and a pass-level restatement here would be a second, looser
                // description of the same fact.
                stdout.WriteLine($"  drain-all: {CountEntries(discarded)} discarded "
                    + "for unenrolled or absent repos, "
                    + $"{CountEntries(sharedQueue.Unreadable)} left behind (unreadable)");
            }

            return 0;
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("error: this store predates the code index tables. Re-run with --apply,");
            stderr.WriteLine("which migrates after snapshotting, or run 'engram doctor' to see where it stands.");
            IndexTelemetry.Note(home, "cli", "failed", identity);
            return 1;
        }
    }

    /// <summary>
    /// The bounded self-heal pass (spec §5.2): at most one repo, chosen by
    /// <see cref="RepoFreshness.NextDue"/>, freshened per invocation. Silent refusal throughout —
    /// this runs from the detached session-start child, where nobody can see an error.
    /// </summary>
    private static int RunFreshen(
        string? homePath, IReadOnlyList<string> skip, bool apply, TextWriter stdout, TextWriter stderr)
    {
        var home = EngramHome.ResolveFromProcess(homePath);

        if (!File.Exists(home.DatabasePath) || !File.Exists(home.ConfigPath))
        {
            return 0;
        }

        var config = ConfigFile.Load(home.ConfigPath);
        var settings = IndexingSettings.Read(config);

        foreach (var problem in settings.Problems)
        {
            stderr.WriteLine($"warning: {problem}");
        }

        var exclude = skip
            .Select(s => PathCanonicalizer.Canonical(Path.GetFullPath(s)))
            .ToHashSet(StringComparer.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(home);

        var candidate = RepoFreshness.NextDue(
            connection,
            IndexingSettings.FullScanIntervalMinutes,
            DateTimeOffset.UtcNow,
            includeAmbient: settings.AutoIndexOnSessionStart,
            exclude);

        if (candidate is null)
        {
            return 0;
        }

        var identity = candidate.Row.Identity;
        IndexTelemetry.Note(home, "cli", "started", identity);

        try
        {
            var report = RepoIndexRun.Freshen(
                connection, home, config, settings, candidate.Root, identity, apply, budget: null, DateTimeOffset.UtcNow);

            // §6.4: ambient — stay silent on lock contention rather than printing a note nobody
            // is watching for; the repo is picked up again on a later freshen pass.
            var lockNote = report.Notes.FirstOrDefault(
                n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));
            if (lockNote is null)
            {
                Print(report, stdout);
            }

            IndexTelemetry.Note(home, "cli", "finished", identity);
            return 0;
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("error: this store predates the code index tables. Re-run with --apply,");
            stderr.WriteLine("which migrates after snapshotting, or run 'engram doctor' to see where it stands.");
            IndexTelemetry.Note(home, "cli", "failed", identity);
            return 1;
        }
    }

    /// <summary>
    /// Step 2 of the <c>--drain-all</c> pass (§6.3e): every other enrolled repo whose last known
    /// root still exists on disk drains against the same queue snapshot the invoked root's pass
    /// captured — never a fresh <see cref="SpoolQueue.Peek"/> per repo, and never a full scan,
    /// since a stale-cadence rescan of every enrolled repo at every session start is unbounded in
    /// the number of repos enrolled (§7 Q2). <paramref name="queue"/> has already had its
    /// pathless entry removed by the caller, so at most the invoked root's own pass can act on it
    /// (D41).
    /// </summary>
    /// <returns>
    /// The roots this pass actually drained, including <paramref name="invokedRoot"/> — passed to
    /// <see cref="SpoolQueue.DiscardExcept"/> for step 3, so an entry is discarded only for a root
    /// this pass did not service (an enrolled repo absent from disk is deliberately among those,
    /// since it was never added here; §6.3e).
    /// </returns>
    private static HashSet<string> DrainOtherEnrolledRoots(
        SqliteConnection connection,
        EngramHome home,
        ConfigFile config,
        IndexingSettings settings,
        string invokedRoot,
        bool apply,
        SpoolQueue queue,
        TextWriter stdout)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { PathCanonicalizer.Canonical(invokedRoot) };

        foreach (var row in RepoEnrollment.ListAll(connection))
        {
            if (row.State != RepoEnrollmentState.Enrolled || row.LastRoot is not { } secondaryRoot)
            {
                continue;
            }

            if (!Directory.Exists(secondaryRoot) || !seen.Add(PathCanonicalizer.Canonical(secondaryRoot)))
            {
                continue;
            }

            var secondaryIdentity = CodeIndexer.ResolveIdentity(secondaryRoot);
            IndexTelemetry.Note(home, "cli", "started", secondaryIdentity);

            var report = CodeIndexer.Index(
                connection,
                home,
                config,
                settings,
                new IndexOptions(secondaryRoot, apply, Drain: true, Full: false, AllowFullScanDue: false, Queue: queue),
                DateTimeOffset.UtcNow);

            // §6.4: ambient, same as RunFreshen above — silent on contention, this root's entries
            // stay queued for a later drain rather than reporting a note nobody typed a command to see.
            var lockNote = report.Notes.FirstOrDefault(
                n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));
            if (lockNote is null)
            {
                Print(report, stdout, reportQueueLine: false);
            }

            IndexTelemetry.Note(home, "cli", "finished", secondaryIdentity);
        }

        return seen;
    }

    private static void Print(IndexReport report, TextWriter stdout, bool reportQueueLine = true)
    {
        stdout.WriteLine($"{report.Root} -> {report.RepoPath}");

        var mode = report.FullScan ? "full scan" : "queue drain";
        stdout.WriteLine($"  {mode}: {Count(report.FilesConsidered, "file")} considered, "
            + $"{report.Analyzed} analyzed, {report.Unchanged} unchanged"
            + (report.Renamed > 0 ? $", {report.Renamed} renamed" : string.Empty)
            + (report.Deleted > 0 ? $", {report.Deleted} deleted" : string.Empty));

        var verb = report.Applied ? string.Empty : "would be ";
        stdout.WriteLine($"  facts: {report.FactsWritten} {verb}written, {report.FactsClosed} {verb}closed, "
            + $"{report.FactsUnchanged} unchanged"
            + (report.ProtectedSkipped > 0
                ? $", {report.ProtectedSkipped} left alone (not the indexer's to supersede)"
                : string.Empty));

        if (reportQueueLine && (report.QueueConsumed > 0 || report.QueueLeft > 0))
        {
            stdout.WriteLine($"  queue: {report.QueueConsumed} consumed, {report.QueueLeft} left for other repos");
        }

        foreach (var note in report.Notes)
        {
            stdout.WriteLine($"  {note}");
        }

        if (!report.Applied)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was written. Re-run with --apply to index.");
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string CountEntries(int n) => n == 1 ? "1 entry" : $"{n} entries";
}
