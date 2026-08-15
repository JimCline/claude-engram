using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// <c>engram repo</c> — record and inspect the user's answer to "should Engram keep this git
/// checkout indexed". <see cref="RepoEnrollment"/> is the one place that decision is read or
/// written; this command reimplements neither the resolution nor the refusal, and
/// <see cref="EngramMcpTools.IndexRepo"/> shares the same helpers rather than a second copy (D1).
/// </summary>
internal static class RepoCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            stderr.WriteLine("error: expected a subcommand — enroll, decline, later, reset, index, or list.");
            return 2;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var rest = args[1..];

        return args[0] switch
        {
            "enroll" => Enroll(home, rest, stdout, stderr),
            "decline" => Decline(home, rest, stdout, stderr),
            "later" => Later(home, rest, stdout, stderr),
            "reset" => Reset(home, rest, stdout, stderr),
            "index" => IndexAll(home, rest, stdout, stderr),
            "list" => List(home, stdout, stderr),
            _ => Unknown(args[0], stderr),
        };
    }

    private static int Enroll(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryResolveCheckout(args, stderr, out var root))
        {
            return 1;
        }

        RepoDecisionResult result;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            result = ApplyDecision(home, connection, root, "enroll", CliSessionId, DateTimeOffset.UtcNow);
        }

        stdout.WriteLine($"Enrolled {root} ({result.Identity}).");

        if (result.IndexSpawned)
        {
            stdout.WriteLine("The first index is running in the background; 'engram repo list' will show its progress.");
        }
        else
        {
            stdout.WriteLine($"warning: could not start the first index automatically — {result.SpawnError}. "
                + $"Run 'engram index --apply --full {root}' by hand.");
        }

        return 0;
    }

    private static int Decline(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryResolveCheckout(args, stderr, out var root))
        {
            return 1;
        }

        RepoDecisionResult result;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            result = ApplyDecision(home, connection, root, "decline", CliSessionId, DateTimeOffset.UtcNow);
        }

        stdout.WriteLine($"Declined {root} ({result.Identity}). Engram will not offer to index it again unless the decision is reset.");
        return 0;
    }

    private static int Later(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryResolveCheckout(args, stderr, out var root))
        {
            return 1;
        }

        RepoDecisionResult result;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            result = ApplyDecision(home, connection, root, "later", CliSessionId, DateTimeOffset.UtcNow);
        }

        stdout.WriteLine($"Deferred {root} ({result.Identity}). Engram will offer again in "
            + $"{(int)RepoEnrollment.DeferralCooldown.TotalDays} days.");
        return 0;
    }

    private static int Reset(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var positional = args.Where(a => a != "--apply").ToArray();

        if (!TryResolveCheckout(positional, stderr, out var root))
        {
            return 1;
        }

        var identity = CodeIndexer.ResolveIdentity(root);

        if (!apply)
        {
            stdout.WriteLine($"Would reset the enrollment decision for {root} ({identity}), returning it to never-asked.");
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to reset.");
            return 0;
        }

        RepoDecisionResult result;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            result = ApplyDecision(home, connection, root, "reset", CliSessionId, DateTimeOffset.UtcNow);
        }

        stdout.WriteLine(result.ResetExisted
            ? $"Reset {root} ({result.Identity}) to never-asked."
            : $"{root} ({result.Identity}) had no recorded decision — nothing to reset.");
        return 0;
    }

    /// <summary>
    /// What <see cref="ApplyDecision"/> did, so a caller can render its own message without
    /// re-deriving any of it.
    /// </summary>
    internal readonly record struct RepoDecisionResult(
        string Identity,
        bool ResetExisted,
        bool IndexSpawned,
        string? SpawnError);

    // spec §6.10: telemetry records this verb's session id as "cli" for the CLI, distinguishing
    // it from the injected McpSessionId the tool passes.
    private const string CliSessionId = "cli";

    /// <summary>
    /// The one place an enrollment decision is resolved, written, and told to telemetry — the
    /// <c>engram repo</c> verb group and <see cref="EngramMcpTools.IndexRepo"/> both call this
    /// rather than re-assembling the same steps independently (spec §6.10).
    /// </summary>
    /// <remarks>
    /// Takes an already-open connection rather than resolving one from <paramref name="home"/>,
    /// because a caller two levels up already holds one; <paramref name="home"/> is needed only
    /// for <see cref="TrySpawnFirstIndex"/>'s detached process launch and for the telemetry write,
    /// neither of which is a database operation.
    /// </remarks>
    internal static RepoDecisionResult ApplyDecision(
        EngramHome home,
        SqliteConnection connection,
        string root,
        string decision,
        string sessionId,
        DateTimeOffset now)
    {
        var identity = CodeIndexer.ResolveIdentity(root);
        var resetExisted = false;
        var spawned = false;
        string? spawnError = null;

        switch (decision)
        {
            case "enroll":
                RepoEnrollment.Enroll(connection, identity, root, now);
                spawned = TrySpawnFirstIndex(home, root, out spawnError);
                break;

            case "decline":
                RepoEnrollment.Decline(connection, identity, root, now);
                break;

            case "later":
                RepoEnrollment.Defer(connection, identity, root, now);
                break;

            case "reset":
                resetExisted = RepoEnrollment.Reset(connection, identity);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "expected enroll, decline, later, or reset.");
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: now.UtcDateTime.ToString("o"),
            SessionId: sessionId,
            Kind: TelemetryEventKind.Enrollment,
            Repo: identity,
            Decision: decision));

        return new RepoDecisionResult(identity, resetExisted, spawned, spawnError);
    }

    private static int List(EngramHome home, TextWriter stdout, TextWriter stderr)
    {
        if (!File.Exists(home.DatabasePath))
        {
            stdout.WriteLine("No store at " + home.DatabasePath + " — nothing enrolled yet.");
            return 0;
        }

        using var connection = EngramDatabase.Open(home);

        IReadOnlyList<RepoEnrollmentRow> rows;
        try
        {
            rows = RepoEnrollment.ListAll(connection);
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("error: this store predates the code index tables. Re-run with --apply,");
            stderr.WriteLine("which migrates after snapshotting, or run 'engram doctor' to see where it stands.");
            return 1;
        }

        if (rows.Count == 0)
        {
            stdout.WriteLine("No repo has an enrollment decision recorded yet.");
            return 0;
        }

        foreach (var row in rows)
        {
            var scan = row.LastFullScanAt is { } last ? MomentText.Local(last) : "never";
            var files = FileStateCount(connection, row.Identity);

            stdout.WriteLine(row.Identity);
            stdout.WriteLine($"  state: {StateText(row.State)} (decided {MomentText.Local(row.DecidedAt)}, via {row.Source})");
            stdout.WriteLine($"  root: {row.LastRoot ?? "(unknown — not seen since the decision was recorded)"}");
            stdout.WriteLine($"  last full scan: {scan} · {files} file(s) indexed");
        }

        return 0;
    }

    /// <summary>
    /// <c>engram repo index --all [--apply]</c> — every enrolled repo <see cref="RepoFreshness"/>
    /// calls due, in its most-neglected-first order, run one after another through
    /// <see cref="RepoIndexRun.Freshen"/> (§4). <c>--all</c> is required so this never collides
    /// with <c>engram index --apply</c>'s bare-invocation meaning, and dry-run is the default like
    /// every other verb that rewrites what is already there (D49).
    /// </summary>
    private static int IndexAll(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!args.Contains("--all"))
        {
            stderr.WriteLine("error: expected --all (usage: engram repo index --all [--apply]).");
            return 2;
        }

        var apply = args.Contains("--apply");

        if (!apply && !File.Exists(home.DatabasePath))
        {
            // Same reasoning as IndexCommand: opening would create an empty database, and a dry
            // run that leaves a file behind is not a dry run.
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        var config = ConfigFile.Load(home.ConfigPath);
        var settings = IndexingSettings.Read(config);
        foreach (var problem in settings.Problems)
        {
            stderr.WriteLine($"warning: {problem}");
        }

        var now = DateTimeOffset.UtcNow;

        using var connection = apply ? EngramDatabase.OpenInitialized(home) : EngramDatabase.Open(home);

        IReadOnlyList<FreshnessCandidate> candidates;
        try
        {
            candidates = RepoFreshness.Due(connection, IndexingSettings.FullScanIntervalMinutes, now, new HashSet<string>());
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("error: this store predates the code index tables. Re-run with --apply,");
            stderr.WriteLine("which migrates after snapshotting, or run 'engram doctor' to see where it stands.");
            return 1;
        }

        var serviced = 0;
        var skippedAbsent = 0;
        var skippedLocked = 0;
        var failed = 0;
        var truncated = 0;

        foreach (var candidate in candidates)
        {
            var identity = candidate.Row.Identity;
            var root = candidate.Root;

            if (!Directory.Exists(root))
            {
                skippedAbsent++;
                stdout.WriteLine($"{identity}: skipped — {root} no longer exists on disk.");
                continue;
            }

            if (apply)
            {
                IndexTelemetry.Note(home, CliSessionId, "started", identity);
            }

            IndexReport report;
            try
            {
                report = RepoIndexRun.Freshen(connection, home, config, settings, root, identity, apply, budget: null, now);
            }
            catch (Exception e)
            {
                failed++;
                if (apply)
                {
                    IndexTelemetry.Note(home, CliSessionId, "failed", identity);
                }

                stdout.WriteLine($"{identity}: failed — {e.Message}");
                continue;
            }

            if (apply)
            {
                IndexTelemetry.Note(home, CliSessionId, "finished", identity);
            }

            // §6.4 (commit E, not yet built): IndexLock reports contention as a zero-count report
            // carrying this note, which every caller of CodeIndexer.Index receives without needing
            // to know a lock exists. Matched here so a commanded run counts and reports it (never
            // silently) the moment IndexLock starts producing it — nothing else in this method
            // needs to change when that lands.
            var lockNote = report.Notes.FirstOrDefault(
                n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));
            if (lockNote is not null)
            {
                skippedLocked++;
                stdout.WriteLine($"{identity}: {lockNote}");
                continue;
            }

            serviced++;
            PrintCandidateReport(identity, report, stdout);

            if (report.Notes.Any(n => n.Contains(
                "skipped deletions, because a partial scan cannot show a file is gone", StringComparison.Ordinal)))
            {
                truncated++;
                stdout.WriteLine("  partial — not marked scanned");
            }
        }

        stdout.WriteLine();
        stdout.WriteLine($"{serviced} serviced, {skippedAbsent} skipped (absent), "
            + $"{skippedLocked} skipped (locked), {failed} failed"
            + (truncated > 0 ? $", {truncated} partial — not marked scanned" : string.Empty));

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to index.");
        }

        return failed > 0 || skippedLocked > 0 ? 1 : 0;
    }

    private static void PrintCandidateReport(string identity, IndexReport report, TextWriter stdout)
    {
        stdout.WriteLine(identity);

        var mode = report.FullScan ? "full scan" : "queue drain";
        var verb = report.Applied ? string.Empty : "would be ";
        stdout.WriteLine($"  {mode}: {report.FilesConsidered} file(s) considered, {report.Analyzed} analyzed, "
            + $"{report.FactsWritten} {verb}written, {report.FactsClosed} {verb}closed");

        foreach (var note in report.Notes)
        {
            stdout.WriteLine($"  {note}");
        }
    }

    /// <summary>
    /// The one implementation of "resolve a requested path to its enclosing git checkout root,
    /// or refuse" — shared by every verb here and by <see cref="EngramMcpTools.IndexRepo"/>, so a
    /// path outside any checkout is refused the same way from both entry points (D53).
    /// </summary>
    internal static string? ResolveCheckoutRoot(string path)
    {
        var full = Path.GetFullPath(path);
        return CodeIndexer.IsGitCheckout(full) ? CodeIndexer.ResolveRoot(full) : null;
    }

    /// <summary>
    /// Starts the enrolled repo's first index the same way session start keeps every enrolled repo
    /// current — a detached spawn via <see cref="MaintenanceLauncher"/>, never a synchronous index
    /// inline in a CLI or MCP call (spec §6.9). A spawn failure is reported but never rolls back
    /// the enrollment decision, which is already durable by the time this runs.
    /// </summary>
    internal static bool TrySpawnFirstIndex(EngramHome home, string root, out string? error)
    {
        try
        {
            MaintenanceLauncher.Spawn(ExecutablePath.Current, home.Root, root, MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryResolveCheckout(string[] args, TextWriter stderr, out string root)
    {
        var requested = args.FirstOrDefault(a => !a.StartsWith('-')) ?? Directory.GetCurrentDirectory();
        var resolved = ResolveCheckoutRoot(requested);
        if (resolved is null)
        {
            stderr.WriteLine($"error: {Path.GetFullPath(requested)} is not inside a git checkout.");
            root = string.Empty;
            return false;
        }

        root = resolved;
        return true;
    }

    private static int FileStateCount(SqliteConnection connection, string identity)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM file_state fs JOIN repo_registry rr ON rr.repo_path = fs.repo_path "
                + "WHERE rr.identity = $identity;";
        command.Parameters.AddWithValue("$identity", identity);
        return (int)(long)command.ExecuteScalar()!;
    }

    private static string StateText(RepoEnrollmentState state) => state switch
    {
        RepoEnrollmentState.Enrolled => "enrolled",
        RepoEnrollmentState.Declined => "declined",
        RepoEnrollmentState.Deferred => "deferred (not now)",
        _ => state.ToString(),
    };

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown repo subcommand '{subcommand}'. Expected enroll, decline, later, reset, index, or list.");
        return 2;
    }
}
