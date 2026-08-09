using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// Rebuilds derived state — dry-run by default, like everything that changes the store.
/// The report is the same detection that <c>--apply</c> acts on, stopped before the writes.
/// </summary>
internal static class RepairCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var apply = false;
        var tokens = false;

        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--apply":
                    apply = true;
                    break;
                case "--tokens":
                    tokens = true;
                    break;
                default:
                    stderr.WriteLine($"error: unexpected argument '{argument}'");
                    return 1;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        // Repair never creates a store: an empty database has nothing derived to rebuild,
        // and --apply materializing one would turn a typo'd --home into a new instance.
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

            if (tokens)
            {
                RunTokensOnly(connection, home, apply, stdout);
                return 0;
            }

            var report = StoreRepairer.Repair(connection, home, apply, DateTimeOffset.UtcNow);

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

    /// <summary>
    /// <c>--tokens</c> rebuilds only <c>fact_token</c> — no snapshot, no WAL checkpoint, no
    /// VACUUM, no FTS rebuild, no path re-derivation, no salience deletion. It exists so the
    /// session-start maintenance child (<see cref="MaintenanceLauncher"/>) can keep the overlap
    /// lane current for a tokenizer bump without paying for a whole-store repair on every
    /// session — the ordinary <c>--apply</c> path below still does that in full.
    /// </summary>
    /// <remarks>
    /// It reads the readiness stamp and nothing else. Row-level desync detection —
    /// <c>CountMissing</c> and <c>CountExtra</c>, which scan the whole token table — belongs to
    /// the full <c>repair</c> verb, which someone runs when they suspect a problem. Running it
    /// here instead would put an unbounded scan on the session-start path, which is the exact
    /// cost this mode exists to avoid, and it is where the FTS detector already sits.
    /// </remarks>
    private static void RunTokensOnly(SqliteConnection connection, EngramHome home, bool apply, TextWriter stdout)
    {
        var liveFacts = Scalar(connection, "SELECT count(*) FROM fact WHERE valid_to IS NULL;");
        var needsRebuild = !FactTokenIndex.IsReady(connection);

        var rebuilt = false;
        if (apply && needsRebuild)
        {
            using var transaction = EngramDatabase.BeginWrite(connection);
            FactTokenIndex.Rebuild(connection, transaction);
            transaction.Commit();
            rebuilt = true;
        }

        stdout.WriteLine(home.DatabasePath);
        stdout.WriteLine(needsRebuild
            ? $"  tokens: unbuilt or stale — {(rebuilt ? string.Empty : "would be ")}rebuilt from the {Count(liveFacts, "live fact")}"
            : $"  tokens: in sync — {Count(liveFacts, "live fact")} indexed");

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine(needsRebuild
                ? "Dry run only — nothing was written. Re-run with --apply --tokens to build it."
                : "Dry run only — nothing needs rebuilding.");
        }
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(long)command.ExecuteScalar()!;
    }

    private static void Print(RepairReport report, EngramHome home, TextWriter stdout)
    {
        stdout.WriteLine(home.DatabasePath);

        var verb = report.Applied ? string.Empty : "would be ";

        stdout.WriteLine(report.FtsNeedsRebuild
            ? $"  fts: {Describe(report)} — {verb}rebuilt from the {Count(report.LiveFacts, "live fact")}"
            : $"  fts: in sync — {Count(report.LiveFacts, "live fact")} indexed");

        stdout.WriteLine(report.TokenIndexNeedsRebuild
            ? $"  tokens: unbuilt or stale — {verb}rebuilt from the {Count(report.LiveFacts, "live fact")}"
            : $"  tokens: in sync — {Count(report.LiveFacts, "live fact")} indexed");

        stdout.WriteLine(report.PathsDrifted > 0
            ? $"  paths: {Count(report.PathsDrifted, "fact")} disagree{(report.PathsDrifted == 1 ? "s" : string.Empty)} "
                + $"with their entity — {verb}re-derived"
            : "  paths: every fact agrees with its entity");

        stdout.WriteLine(report.OrphanSalience > 0
            ? $"  salience: {Count(report.OrphanSalience, "orphan row")} {verb}deleted"
            : "  salience: no orphan rows");

        stdout.WriteLine(report.Applied
            ? $"  wal: checkpointed ({Mb(report.WalBytes)}); store vacuumed"
            : $"  wal: {Mb(report.WalBytes)} would be checkpointed; store would be vacuumed");

        stdout.WriteLine(report.Applied
            ? $"  snapshot: {report.SnapshotName} taken first"
            : "  snapshot: would be taken first, unconditionally");

        if (!report.Applied)
        {
            stdout.WriteLine();
            stdout.WriteLine(report.AnythingToFix
                ? "Dry run only — nothing was written. Re-run with --apply to repair."
                : "Dry run only — nothing needs rebuilding. --apply would still checkpoint and vacuum.");
        }
    }

    private static string Describe(RepairReport report)
    {
        var parts = new List<string>(3);
        if (report.FtsMissing > 0)
        {
            parts.Add($"{report.FtsMissing} missing");
        }

        if (report.FtsExtra > 0)
        {
            parts.Add($"{report.FtsExtra} extra");
        }

        if (report.FtsCorrupt)
        {
            parts.Add("index corrupt");
        }

        return string.Join(", ", parts);
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
