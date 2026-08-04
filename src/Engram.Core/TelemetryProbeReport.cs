using System.Globalization;
using System.Text.Json.Serialization;

namespace Engram.Core;

public sealed record TelemetryDateRange(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To);

public sealed record TelemetryAdoptionStat(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("percent")] double Percent);

public sealed record TelemetryCoverageStat(
    [property: JsonPropertyName("high_count")] int HighCount,
    [property: JsonPropertyName("high_percent")] double HighPercent,
    [property: JsonPropertyName("partial_count")] int PartialCount,
    [property: JsonPropertyName("partial_percent")] double PartialPercent,
    [property: JsonPropertyName("none_count")] int NoneCount,
    [property: JsonPropertyName("none_percent")] double NonePercent);

public sealed record TelemetryQueryCount(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("count")] int Count);

public sealed record TelemetryHookGapWarning(
    [property: JsonPropertyName("hook_sessions")] int HookSessions,
    [property: JsonPropertyName("mcp_sessions")] int McpSessions,
    [property: JsonPropertyName("difference")] int Difference,
    [property: JsonPropertyName("message")] string Message);

public sealed record TelemetryProbeReport(
    [property: JsonPropertyName("date_range")] TelemetryDateRange DateRange,
    [property: JsonPropertyName("total_records")] int TotalRecords,
    [property: JsonPropertyName("skipped_lines")] int SkippedLines,
    [property: JsonPropertyName("mcp_sessions")] int McpSessions,
    [property: JsonPropertyName("hook_sessions")] int HookSessions,
    [property: JsonPropertyName("hook_gap_warning")] TelemetryHookGapWarning? HookGapWarning,
    [property: JsonPropertyName("sessions_with_recall")] TelemetryAdoptionStat SessionsWithRecall,
    [property: JsonPropertyName("sessions_with_remember")] TelemetryAdoptionStat SessionsWithRemember,
    [property: JsonPropertyName("sessions_with_digest")] TelemetryAdoptionStat SessionsWithDigest,
    [property: JsonPropertyName("median_recalls_per_session")] double MedianRecallsPerSession,
    [property: JsonPropertyName("max_recalls_per_session")] int MaxRecallsPerSession,
    [property: JsonPropertyName("coverage")] TelemetryCoverageStat Coverage,
    [property: JsonPropertyName("mean_tokens_per_recall")] double MeanTokensPerRecall,
    [property: JsonPropertyName("median_tokens_per_recall")] double MedianTokensPerRecall,
    [property: JsonPropertyName("top_queries")] IReadOnlyList<TelemetryQueryCount> TopQueries);

public sealed record TelemetryProbeEmptyReport(
    [property: JsonPropertyName("has_records")] bool HasRecords,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("skipped_lines")] int SkippedLines);

[JsonSerializable(typeof(TelemetryProbeReport))]
[JsonSerializable(typeof(TelemetryProbeEmptyReport))]
public sealed partial class TelemetryProbeJsonContext : JsonSerializerContext;

public static class TelemetrySummarizer
{
    public static TelemetryProbeReport? Summarize(IReadOnlyList<TelemetryRecord> records, int skippedLines)
    {
        if (records.Count == 0)
        {
            return null;
        }

        var timestamps = records.Select(r => ParseTimestamp(r.Timestamp)).ToList();
        var dateRange = new TelemetryDateRange(timestamps.Min(), timestamps.Max());

        var mcpSessionIds = records
            .Where(r => r.Kind == TelemetryEventKind.ServerStart)
            .Select(r => r.SessionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var mcpSessionSet = new HashSet<string>(mcpSessionIds, StringComparer.Ordinal);
        var mcpSessionCount = mcpSessionIds.Count;

        var hookSessionCount = records
            .Where(r => r.Kind == TelemetryEventKind.SessionStart)
            .Select(r => r.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var recalls = records.Where(r => r.Kind == TelemetryEventKind.Recall).ToList();
        var remembers = records.Where(r => r.Kind == TelemetryEventKind.Remember).ToList();
        var digests = records.Where(r => r.Kind == TelemetryEventKind.Digest).ToList();

        var recallsBySession = recalls
            .GroupBy(r => r.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var recallCountsPerSession = mcpSessionIds
            .Select(id => recallsBySession.TryGetValue(id, out var count) ? count : 0)
            .ToList();

        var coverageHigh = 0;
        var coveragePartial = 0;
        var coverageNone = 0;
        foreach (var recall in recalls)
        {
            switch (recall.Coverage)
            {
                case "high":
                    coverageHigh++;
                    break;
                case "partial":
                    coveragePartial++;
                    break;
                case "none":
                    coverageNone++;
                    break;
            }
        }

        var coverageTotal = coverageHigh + coveragePartial + coverageNone;

        var tokenValues = recalls
            .Where(r => r.TokensReturned.HasValue)
            .Select(r => r.TokensReturned!.Value)
            .ToList();

        var topQueries = recalls
            .Where(r => !string.IsNullOrEmpty(r.Query))
            .GroupBy(r => r.Query!, StringComparer.Ordinal)
            .Select(g => new TelemetryQueryCount(g.Key, g.Count()))
            .OrderByDescending(q => q.Count)
            .ThenBy(q => q.Query, StringComparer.Ordinal)
            .Take(10)
            .ToList();

        var hookGapWarning = hookSessionCount > mcpSessionCount
            ? new TelemetryHookGapWarning(
                HookSessions: hookSessionCount,
                McpSessions: mcpSessionCount,
                Difference: hookSessionCount - mcpSessionCount,
                Message: $"{hookSessionCount - mcpSessionCount} session(s) ran without Engram's MCP server reachable; memory was unavailable in those sessions.")
            : null;

        return new TelemetryProbeReport(
            DateRange: dateRange,
            TotalRecords: records.Count,
            SkippedLines: skippedLines,
            McpSessions: mcpSessionCount,
            HookSessions: hookSessionCount,
            HookGapWarning: hookGapWarning,
            SessionsWithRecall: AdoptionStat(recalls, mcpSessionSet, mcpSessionCount),
            SessionsWithRemember: AdoptionStat(remembers, mcpSessionSet, mcpSessionCount),
            SessionsWithDigest: AdoptionStat(digests, mcpSessionSet, mcpSessionCount),
            MedianRecallsPerSession: Median(recallCountsPerSession),
            MaxRecallsPerSession: recallCountsPerSession.Count == 0 ? 0 : recallCountsPerSession.Max(),
            Coverage: new TelemetryCoverageStat(
                coverageHigh, Percent(coverageHigh, coverageTotal),
                coveragePartial, Percent(coveragePartial, coverageTotal),
                coverageNone, Percent(coverageNone, coverageTotal)),
            MeanTokensPerRecall: tokenValues.Count == 0 ? 0 : Math.Round(tokenValues.Average(), 1, MidpointRounding.AwayFromZero),
            MedianTokensPerRecall: Median(tokenValues),
            TopQueries: topQueries);
    }

    private static TelemetryAdoptionStat AdoptionStat(List<TelemetryRecord> kindRecords, HashSet<string> mcpSessionIds, int mcpSessionCount)
    {
        var count = kindRecords
            .Select(r => r.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count(mcpSessionIds.Contains);
        return new TelemetryAdoptionStat(count, Percent(count, mcpSessionCount));
    }

    private static double Percent(int part, int whole) =>
        whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1, MidpointRounding.AwayFromZero);

    private static double Median(List<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        return Math.Round(median, 1, MidpointRounding.AwayFromZero);
    }

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
