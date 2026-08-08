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
        var full = false;
        var auto = false;
        string? target = null;

        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--apply":
                    apply = true;
                    break;
                case "--drain":
                    drain = true;
                    break;
                case "--full":
                    full = true;
                    break;
                case "--auto":
                    auto = true;
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

        var root = Path.GetFullPath(target ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            stderr.WriteLine($"error: no directory at {root}");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = IndexingSettings.Read(config);

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
        Note(home, "started");

        try
        {
            using var connection = apply
                ? EngramDatabase.OpenInitialized(home)
                : EngramDatabase.Open(home);

            var report = CodeIndexer.Index(
                connection,
                home,
                config,
                settings,
                new IndexOptions(root, apply, drain, full),
                DateTimeOffset.UtcNow);

            Print(report, stdout);
            Note(home, "finished");
            return 0;
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("error: this store predates the code index tables. Re-run with --apply,");
            stderr.WriteLine("which migrates after snapshotting, or run 'engram doctor' to see where it stands.");
            Note(home, "failed");
            return 1;
        }
    }

    /// <summary>
    /// Records that indexing is under way, so something watching the event stream can say so.
    /// </summary>
    /// <remarks>
    /// The session id is the literal "cli" because this command has no session — D43 already
    /// established that the id spaces here are disjoint and do not combine, so a third honest
    /// value costs nothing and a borrowed one would invite exactly the arithmetic that went wrong
    /// before. A finished phase is what lets a reader stop saying "indexing"; without the pair,
    /// the only alternative is a timer, and a timer is a guess about how long a repo takes.
    /// </remarks>
    private static void Note(EngramHome home, string phase)
    {
        if (File.Exists(home.ConfigPath))
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                SessionId: "cli",
                Kind: TelemetryEventKind.Index,
                Phase: phase));
        }
    }

    private static void Print(IndexReport report, TextWriter stdout)
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

        if (report.QueueConsumed > 0 || report.QueueLeft > 0)
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
}
