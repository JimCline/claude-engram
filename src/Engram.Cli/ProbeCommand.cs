using System.Globalization;
using System.Text.Json;
using Engram.Core;

namespace Engram.Cli;

internal static class ProbeCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var json = false;
        int? sinceDays = null;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;

                case "--since":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine("error: --since requires a value, e.g. --since 7d");
                        return 1;
                    }

                    if (!TryParseSinceDays(rest[++i], out var days))
                    {
                        stderr.WriteLine($"error: invalid --since value '{rest[i]}', expected e.g. '7d'");
                        return 1;
                    }

                    sinceDays = days;
                    break;

                default:
                    CliApp.PrintUsage(stderr);
                    return 1;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var since = sinceDays is { } d ? DateTimeOffset.UtcNow - TimeSpan.FromDays(d) : (DateTimeOffset?)null;
        var result = TelemetryProbeReader.Read(home, since);
        var factsPerSession = ReadFactDensity(home);

        if (!result.FileExists)
        {
            WriteEmpty(stdout, json, "no telemetry recorded yet", skippedLines: 0, factsPerSession);
            return 0;
        }

        var report = TelemetrySummarizer.Summarize(result.Records, result.SkippedLines, factsPerSession);
        if (report is null)
        {
            WriteEmpty(stdout, json, "no telemetry records found in the selected window", result.SkippedLines, factsPerSession);
            return 0;
        }

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(report, TelemetryProbeJsonContext.Default.TelemetryProbeReport));
        }
        else
        {
            var windowLabel = sinceDays is { } n ? $"last {n}d" : "all time";
            ProbeReportFormatter.WriteText(stdout, report, windowLabel);
        }

        return 0;
    }

    /// <summary>
    /// The facts-per-session distribution, or null when there is no store to read it from.
    /// </summary>
    /// <remarks>
    /// probe is a diagnostic and must work on a broken instance, so a store that will not
    /// open costs the section rather than the command. It is also read-only by intent: probe
    /// is the one command a user runs to find out what is wrong, and a diagnostic that
    /// creates the thing it is diagnosing has lied about what it found.
    /// </remarks>
    private static FactsPerSessionStat? ReadFactDensity(EngramHome home)
    {
        if (!File.Exists(home.DatabasePath))
        {
            return null;
        }

        try
        {
            using var connection = EngramDatabase.OpenInitialized(home);
            return FactDensity.Read(connection);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private static void WriteEmpty(
        TextWriter stdout,
        bool json,
        string message,
        int skippedLines,
        FactsPerSessionStat? factsPerSession)
    {
        if (json)
        {
            var payload = new TelemetryProbeEmptyReport(
                HasRecords: false, Message: message, SkippedLines: skippedLines, FactsPerSession: factsPerSession);
            stdout.WriteLine(JsonSerializer.Serialize(payload, TelemetryProbeJsonContext.Default.TelemetryProbeEmptyReport));
            return;
        }

        stdout.WriteLine(char.ToUpperInvariant(message[0]) + message[1..] + ".");
        if (skippedLines > 0)
        {
            stdout.WriteLine($"{skippedLines} malformed line(s) skipped.");
        }

        if (factsPerSession is not null)
        {
            stdout.WriteLine();
            ProbeReportFormatter.WriteFactsPerSession(stdout, factsPerSession);
        }
    }

    private static bool TryParseSinceDays(string value, out int days)
    {
        days = 0;
        if (string.IsNullOrEmpty(value) || value[^1] != 'd')
        {
            return false;
        }

        return int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out days) && days > 0;
    }
}
