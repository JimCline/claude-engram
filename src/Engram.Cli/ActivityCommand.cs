using System.Globalization;
using System.Text.Json;
using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// Reports when Engram last did anything, and how much has happened lately, from
/// <c>telemetry.jsonl</c> alone. Never opens the database (D4-adjacent: a read-only diagnostic
/// with its own budget to keep, not just the append side's).
/// </summary>
internal static class ActivityCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var json = false;
        TimeSpan? since = null;

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
                        stderr.WriteLine("error: --since requires a value, e.g. --since 10s");
                        return 1;
                    }

                    if (!TimeWindow.TryParse(rest[++i], out var window))
                    {
                        stderr.WriteLine($"error: invalid --since value '{rest[i]}', expected e.g. '10s', '5m', '2h', '1d'");
                        return 1;
                    }

                    since = window;
                    break;

                default:
                    CliApp.PrintUsage(stderr);
                    return 1;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        // ponytail: TelemetryProbeReader.Read reads the whole file every invocation — fine at
        // today's sizes, but a caller polling `activity --since Ns` on every status-line render
        // against an unbounded telemetry.jsonl is a real cost ceiling. Upgrade path if it bites:
        // a bounded backward read of the file's last N bytes, not a cursor or a database.
        var result = TelemetryProbeReader.Read(home, since: null);

        var now = DateTimeOffset.UtcNow;
        var cutoff = since is { } w ? now - w : (DateTimeOffset?)null;
        var windowRecords = cutoff is { } c
            ? RecordsSince(result.Records, c)
            : result.Records;

        if (json)
        {
            WriteJson(stdout, home, result, windowRecords, since, now);
            return 0;
        }

        WriteText(stdout, result, windowRecords, since, now);
        return 0;
    }

    private static void WriteJson(
        TextWriter stdout,
        EngramHome home,
        TelemetryProbeReadResult result,
        IReadOnlyList<TelemetryRecord> windowRecords,
        TimeSpan? since,
        DateTimeOffset now)
    {
        TelemetryRecord? last = result.Records.Count > 0 ? result.Records[^1] : null;
        var lastAt = last is not null ? ParseTimestamp(last.Timestamp) : (DateTimeOffset?)null;

        var payload = new ActivityJson(
            Home: home.Root,
            LastKind: last?.Kind,
            LastAt: lastAt,
            LastAgeSeconds: lastAt is { } at ? ClampedSeconds(now - at) : null,
            WindowSeconds: since is { } w ? (int)w.TotalSeconds : null,
            WindowCount: windowRecords.Count,
            Kinds: KindCounts(windowRecords).Select(kc => new ActivityKindCount(kc.Kind, kc.Count)).ToList(),
            SkippedLines: result.SkippedLines);

        stdout.WriteLine(JsonSerializer.Serialize(payload, ActivityJsonContext.Default.ActivityJson));
    }

    private static void WriteText(
        TextWriter stdout,
        TelemetryProbeReadResult result,
        IReadOnlyList<TelemetryRecord> windowRecords,
        TimeSpan? since,
        DateTimeOffset now)
    {
        if (result.Records.Count == 0)
        {
            stdout.WriteLine("no activity recorded yet");
            WriteSkipped(stdout, result.SkippedLines);
            return;
        }

        var last = result.Records[^1];
        var lastAt = ParseTimestamp(last.Timestamp);
        var age = ClampedSpan(now - lastAt);
        stdout.WriteLine($"last: {last.Kind} {FormatDuration(age)} ago ({lastAt.UtcDateTime:o})");

        if (since is { } w)
        {
            if (windowRecords.Count == 0)
            {
                stdout.WriteLine($"window: no activity in the last {FormatDuration(w)}");
            }
            else
            {
                stdout.WriteLine($"window: {windowRecords.Count} event(s) in the last {FormatDuration(w)} — {FormatKindCounts(windowRecords)}");
            }
        }

        WriteSkipped(stdout, result.SkippedLines);
    }

    private static void WriteSkipped(TextWriter stdout, int skippedLines)
    {
        if (skippedLines > 0)
        {
            stdout.WriteLine($"{skippedLines} malformed line(s) skipped.");
        }
    }

    private static string FormatKindCounts(IReadOnlyList<TelemetryRecord> records)
    {
        const int cap = 5;
        var counts = KindCounts(records);
        var shown = counts.Take(cap).Select(kc => $"{kc.Kind} {kc.Count}");
        var text = string.Join(", ", shown);
        return counts.Count > cap ? $"{text}, +{counts.Count - cap} more" : text;
    }

    private static List<(string Kind, int Count)> KindCounts(IReadOnlyList<TelemetryRecord> records) =>
        [.. records
            .GroupBy(r => r.Kind, StringComparer.Ordinal)
            .Select(g => (Kind: g.Key, Count: g.Count()))
            .OrderByDescending(kc => kc.Count)
            .ThenBy(kc => kc.Kind, StringComparer.Ordinal)];

    /// <summary>
    /// The window slice of an append-ordered log: scan backward from the end and stop at the
    /// first record older than <paramref name="cutoff"/>. Parses only the records the window
    /// actually holds (plus the one that ends it), not the whole file — <c>TelemetryLineParser</c>
    /// already validated every timestamp once on the way in, so a second full-corpus parse here
    /// would be pure waste, and it showed up as one: a narrower <c>--since</c> window costs the
    /// same full scan as a wide one until this only walks the window.
    /// </summary>
    private static IReadOnlyList<TelemetryRecord> RecordsSince(IReadOnlyList<TelemetryRecord> records, DateTimeOffset cutoff)
    {
        var start = records.Count;
        for (var i = records.Count - 1; i >= 0; i--)
        {
            if (ParseTimestamp(records[i].Timestamp) < cutoff)
            {
                break;
            }

            start = i;
        }

        return start == records.Count ? [] : records.Skip(start).ToList();
    }

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static TimeSpan ClampedSpan(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

    private static long ClampedSeconds(TimeSpan span) => (long)Math.Max(0, span.TotalSeconds);

    /// <summary>
    /// <c>FormatUptime</c>'s shape (<see cref="StatusCommand"/>), extended with a sub-minute form
    /// so a ten-second window can say <c>4s</c> rather than <c>0h 0m 4s</c>.
    /// </summary>
    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.Days > 0)
        {
            return $"{span.Days}d {span.Hours}h {span.Minutes}m";
        }

        if (span.Hours > 0)
        {
            return $"{span.Hours}h {span.Minutes}m {span.Seconds}s";
        }

        if (span.Minutes > 0)
        {
            return $"{span.Minutes}m {span.Seconds}s";
        }

        return $"{span.Seconds}s";
    }
}
