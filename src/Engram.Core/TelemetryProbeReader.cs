using System.Globalization;
using System.Text.Json;

namespace Engram.Core;

public static class TelemetryLineParser
{
    private static readonly TelemetryRecord Invalid = new(string.Empty, string.Empty, string.Empty);

    public static bool TryParse(string line, out TelemetryRecord record)
    {
        TelemetryRecord? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryRecord);
        }
        catch (JsonException)
        {
            record = Invalid;
            return false;
        }

        if (parsed is null
            || string.IsNullOrEmpty(parsed.Timestamp)
            || string.IsNullOrEmpty(parsed.SessionId)
            || string.IsNullOrEmpty(parsed.Kind)
            || !DateTimeOffset.TryParse(parsed.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            record = Invalid;
            return false;
        }

        record = parsed;
        return true;
    }
}

public sealed record TelemetryProbeReadResult(
    bool FileExists,
    IReadOnlyList<TelemetryRecord> Records,
    int SkippedLines);

public static class TelemetryProbeReader
{
    public static TelemetryProbeReadResult Read(EngramHome home, DateTimeOffset? since)
    {
        var path = Telemetry.ResolvePath(home);
        if (!File.Exists(path))
        {
            return new TelemetryProbeReadResult(false, [], 0);
        }

        var records = new List<TelemetryRecord>();
        var skippedLines = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TelemetryLineParser.TryParse(line, out var record))
            {
                skippedLines++;
                continue;
            }

            if (since is { } cutoff && ParseTimestamp(record.Timestamp) < cutoff)
            {
                continue;
            }

            records.Add(record);
        }

        return new TelemetryProbeReadResult(true, records, skippedLines);
    }

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
