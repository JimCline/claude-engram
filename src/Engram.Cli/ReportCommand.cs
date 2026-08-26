using System.Globalization;
using System.Text;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// <c>engram report</c> — a readable, untruncated Markdown report of every fact the store holds,
/// closed and superseded facts included (D22).
/// </summary>
internal static class ReportCommand
{
    private const string CliSessionId = "cli";
    private const string ReportsDirName = "reports";

    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string? outPath = null;
        var authoredOnly = false;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("error: --out requires a value");
                        return 2;
                    }

                    outPath = args[++i];
                    break;
                case "--authored-only":
                    authoredOnly = true;
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    stderr.WriteLine($"error: unknown option {args[i]}");
                    CliApp.PrintUsage(stderr);
                    return 2;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        // Pure read (§3.3): never OpenInitialized, which migrates on open and, by D31, snapshots
        // first — a report of what is stored may not alter what is stored.
        using var connection = EngramDatabase.Open(home);

        int schemaVersion;
        try
        {
            schemaVersion = EngramDatabase.ReadSchemaVersion(connection);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            stderr.WriteLine($"error: no Engram store found at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        if (schemaVersion < EngramDatabase.SchemaVersion)
        {
            stderr.WriteLine(
                $"error: store schema is version {schemaVersion}, this binary expects "
                + $"{EngramDatabase.SchemaVersion} — run 'engram serve' to migrate it, then re-run report");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        var result = MemoryReport.Render(
            connection, home.DatabasePath, schemaVersion, authoredOnly, now, TimeZoneInfo.Local);

        if (outPath == "-")
        {
            stdout.Write(result.Document);
            EmitTelemetry(home, result, now);
            return 0;
        }

        string destination;
        if (outPath is not null)
        {
            destination = outPath;
            if (File.Exists(destination) && !force)
            {
                stderr.WriteLine($"error: {destination} already exists — pass --force to overwrite");
                return 1;
            }
        }
        else
        {
            var directory = Path.Combine(home.Root, ReportsDirName);
            Directory.CreateDirectory(directory);
            destination = Path.Combine(
                directory,
                $"engram-report-{now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md");
        }

        try
        {
            File.WriteAllText(destination, result.Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: could not write {destination}: {ex.Message}");
            return 1;
        }

        stdout.WriteLine(destination);
        EmitTelemetry(home, result, now);
        return 0;
    }

    /// <summary>
    /// One instant event, after the document exists, success only (§3.6 rules 2-3). Never sets
    /// <c>fact_count</c> — that field means facts returned to the model on a <c>recall</c> record
    /// (rule 4); the counts here ride fields of their own.
    /// </summary>
    private static void EmitTelemetry(EngramHome home, MemoryReportResult result, DateTimeOffset now)
    {
        var bytesWritten = Encoding.UTF8.GetByteCount(result.Document);

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: now.ToString("o", CultureInfo.InvariantCulture),
            SessionId: CliSessionId,
            Kind: TelemetryEventKind.Report,
            ReportTotalFacts: result.Total,
            ReportLiveFacts: result.Live,
            ReportClosedFacts: result.Closed,
            ReportExcludedFacts: result.ExcludedRegenerable,
            ReportBytesWritten: bytesWritten));
    }
}
