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

        if (!result.FileExists)
        {
            WriteEmpty(stdout, json, "no telemetry recorded yet", skippedLines: 0);
            return 0;
        }

        var report = TelemetrySummarizer.Summarize(result.Records, result.SkippedLines);
        if (report is null)
        {
            WriteEmpty(stdout, json, "no telemetry records found in the selected window", result.SkippedLines);
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

    private static void WriteEmpty(TextWriter stdout, bool json, string message, int skippedLines)
    {
        if (json)
        {
            var payload = new TelemetryProbeEmptyReport(HasRecords: false, Message: message, SkippedLines: skippedLines);
            stdout.WriteLine(JsonSerializer.Serialize(payload, TelemetryProbeJsonContext.Default.TelemetryProbeEmptyReport));
            return;
        }

        stdout.WriteLine(char.ToUpperInvariant(message[0]) + message[1..] + ".");
        if (skippedLines > 0)
        {
            stdout.WriteLine($"{skippedLines} malformed line(s) skipped.");
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
