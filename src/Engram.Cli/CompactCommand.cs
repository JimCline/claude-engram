using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// Prunes regenerable facts — dry-run by default, like everything that changes the store.
/// The report is the same selection that <c>--apply</c> acts on, stopped before the writes.
/// </summary>
internal static class CompactCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var apply = false;
        string? path = null;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--apply":
                    apply = true;
                    break;
                case "--path":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine("error: --path needs a value");
                        return 1;
                    }

                    path = rest[++i];
                    break;
                default:
                    stderr.WriteLine($"error: unexpected argument '{rest[i]}'");
                    return 1;
            }
        }

        if (path is not null && !path.StartsWith('/'))
        {
            stderr.WriteLine("error: --path takes a rooted memory path, like /projects/acme/code/acme-api");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        // Compact never creates a store: an empty database has nothing to prune, and
        // --apply materializing one would turn a typo'd --home into a new instance.
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        try
        {
            using var connection = apply
                ? EngramDatabase.OpenInitialized(home)
                : EngramDatabase.Open(home);

            var report = StoreCompactor.Compact(connection, home, path, apply, DateTimeOffset.UtcNow);

            Print(report, home, stdout);
            return 0;
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("error: this store predates the current schema. Re-run with --apply,");
            stderr.WriteLine("which migrates after snapshotting, or run 'engram doctor' to see where it stands.");
            return 1;
        }
    }

    private static void Print(CompactReport report, EngramHome home, TextWriter stdout)
    {
        stdout.WriteLine(home.DatabasePath);

        var verb = report.Applied ? string.Empty : "would be ";

        if (report.Path is null)
        {
            stdout.WriteLine(report.ClosedPruned > 0
                ? $"  facts: {Count(report.ClosedPruned, "closed regenerable fact")} {verb}pruned"
                : "  facts: no closed regenerable facts to prune");
        }
        else
        {
            stdout.WriteLine(report.FactsPruned > 0
                ? $"  facts: {report.LivePruned} live + {report.ClosedPruned} closed under {report.Path} {verb}pruned"
                : $"  facts: nothing regenerable under {report.Path}");
        }

        if (report.ProtectedByAuthoredHistory > 0)
        {
            stdout.WriteLine(
                $"  kept: {Count(report.ProtectedByAuthoredHistory, "fact")} revised into authored history — never pruned");
        }

        if (report.SupersessionsRemoved > 0)
        {
            stdout.WriteLine($"  supersessions: {Count(report.SupersessionsRemoved, "row")} {verb}removed with them");
        }

        if (report.VectorsRemoved > 0)
        {
            stdout.WriteLine($"  vectors: {Count(report.VectorsRemoved, "row")} {verb}removed");
        }

        if (report.Path is not null)
        {
            stdout.WriteLine(report.EntitiesRemoved > 0
                ? $"  entities: {Count(report.EntitiesRemoved, "code entity")} {verb}removed"
                : "  entities: none left unreferenced");
            stdout.WriteLine(
                $"  index state: {Count(report.FileStatesRemoved, "file record")} + "
                + $"{Count(report.ReposDeregistered, "repo registration")} {verb}cleared, so a re-index re-reads");
        }

        foreach (var note in report.Notes)
        {
            stdout.WriteLine($"  note: {note}");
        }

        stdout.WriteLine(report.Applied
            ? $"  snapshot: {report.SnapshotName} taken first; store vacuumed"
            : "  snapshot: would be taken first, unconditionally");

        if (!report.Applied)
        {
            stdout.WriteLine();
            stdout.WriteLine(report.AnythingToPrune
                ? "Dry run only — nothing was written. Re-run with --apply to compact."
                : "Dry run only — nothing to prune. --apply would still checkpoint and vacuum.");
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
