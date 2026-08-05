using Engram.Core;

namespace Engram.Core.Tests;

public class TelemetrySummarizerTests
{
    [Fact]
    public void Summarize_EmptyRecords_ReturnsNull()
    {
        Assert.Null(TelemetrySummarizer.Summarize([], skippedLines: 0));
    }

    [Fact]
    public void Summarize_HandComputedFixture_ProducesExpectedNumbers()
    {
        var records = new List<TelemetryRecord>
        {
            new("2026-07-20T07:59:00Z", "h1", TelemetryEventKind.SessionStart),
            new("2026-07-20T08:00:00Z", "m1", TelemetryEventKind.SessionOpen),
            new("2026-07-20T08:01:00Z", "m1", TelemetryEventKind.Recall, Query: "alpha", FactCount: 3, TokensReturned: 100, Coverage: "high"),
            new("2026-07-20T08:02:00Z", "m1", TelemetryEventKind.Recall, Query: "alpha", FactCount: 3, TokensReturned: 200, Coverage: "high"),
            new("2026-07-20T08:03:00Z", "m1", TelemetryEventKind.Remember),
            new("2026-07-20T08:04:00Z", "m1", TelemetryEventKind.Digest),

            new("2026-07-21T08:59:00Z", "h2", TelemetryEventKind.SessionStart),
            new("2026-07-21T09:00:00Z", "m2", TelemetryEventKind.SessionOpen),
            new("2026-07-21T09:01:00Z", "m2", TelemetryEventKind.Recall, Query: "beta", FactCount: 1, TokensReturned: 50, Coverage: "partial"),

            new("2026-07-22T09:59:00Z", "h3", TelemetryEventKind.SessionStart),
            new("2026-07-22T10:00:00Z", "m3", TelemetryEventKind.SessionOpen),
            new("2026-07-22T10:01:00Z", "m3", TelemetryEventKind.Recall, Query: "gamma", FactCount: 0, TokensReturned: 10, Coverage: "none"),
            new("2026-07-22T10:02:00Z", "m3", TelemetryEventKind.Remember),

            new("2026-07-23T10:59:00Z", "h4", TelemetryEventKind.SessionStart),
            new("2026-07-23T11:00:00Z", "m4", TelemetryEventKind.SessionOpen),

            new("2026-07-24T11:59:00Z", "h5", TelemetryEventKind.SessionStart),
            new("2026-07-24T12:00:00Z", "m5", TelemetryEventKind.SessionOpen),
            new("2026-07-24T12:01:00Z", "m5", TelemetryEventKind.Recall, Query: "alpha", FactCount: 3, TokensReturned: 150, Coverage: "high"),
            new("2026-07-24T12:02:00Z", "m5", TelemetryEventKind.Recall, Query: "beta", FactCount: 1, TokensReturned: 90, Coverage: "partial"),
            new("2026-07-24T12:03:00Z", "m5", TelemetryEventKind.Digest),

            new("2026-07-25T09:00:00Z", "h6", TelemetryEventKind.SessionStart),
        };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 0);

        Assert.NotNull(report);
        Assert.Equal(21, report!.TotalRecords);
        Assert.Equal(0, report.SkippedLines);
        Assert.Equal(DateTimeOffset.Parse("2026-07-20T07:59:00Z"), report.DateRange.From);
        Assert.Equal(DateTimeOffset.Parse("2026-07-25T09:00:00Z"), report.DateRange.To);

        Assert.Equal(5, report.McpSessions);
        Assert.Equal(6, report.HookSessions);
        Assert.NotNull(report.HookGapWarning);
        Assert.Equal(6, report.HookGapWarning!.HookSessions);
        Assert.Equal(5, report.HookGapWarning.McpSessions);
        Assert.Equal(1, report.HookGapWarning.Difference);

        Assert.Equal(4, report.SessionsWithRecall.Count);
        Assert.Equal(80.0, report.SessionsWithRecall.Percent);
        Assert.Equal(2, report.SessionsWithRemember.Count);
        Assert.Equal(40.0, report.SessionsWithRemember.Percent);
        Assert.Equal(2, report.SessionsWithDigest.Count);
        Assert.Equal(40.0, report.SessionsWithDigest.Percent);

        Assert.Equal(1.0, report.MedianRecallsPerSession);
        Assert.Equal(2, report.MaxRecallsPerSession);

        Assert.Equal(3, report.Coverage.HighCount);
        Assert.Equal(50.0, report.Coverage.HighPercent);
        Assert.Equal(2, report.Coverage.PartialCount);
        Assert.Equal(33.3, report.Coverage.PartialPercent);
        Assert.Equal(1, report.Coverage.NoneCount);
        Assert.Equal(16.7, report.Coverage.NonePercent);

        Assert.Equal(100.0, report.MeanTokensPerRecall);
        Assert.Equal(95.0, report.MedianTokensPerRecall);

        Assert.Equal(3, report.TopQueries.Count);
        Assert.Equal("alpha", report.TopQueries[0].Query);
        Assert.Equal(3, report.TopQueries[0].Count);
        Assert.Equal("beta", report.TopQueries[1].Query);
        Assert.Equal(2, report.TopQueries[1].Count);
        Assert.Equal("gamma", report.TopQueries[2].Query);
        Assert.Equal(1, report.TopQueries[2].Count);
    }

    [Fact]
    public void Summarize_CarriesSkippedLinesThrough()
    {
        var records = new List<TelemetryRecord> { new("2026-07-20T08:00:00Z", "m1", TelemetryEventKind.SessionOpen) };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 2);

        Assert.Equal(2, report!.SkippedLines);
    }

    [Fact]
    public void Summarize_FourServerStartsThreeWithRecall_Reports75PercentAdoptionOverFourMcpSessions()
    {
        var records = new List<TelemetryRecord>
        {
            new("2026-07-20T08:00:00Z", "m1", TelemetryEventKind.SessionOpen),
            new("2026-07-20T08:01:00Z", "m1", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "high"),
            new("2026-07-20T08:02:00Z", "m2", TelemetryEventKind.SessionOpen),
            new("2026-07-20T08:03:00Z", "m2", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "high"),
            new("2026-07-20T08:04:00Z", "m3", TelemetryEventKind.SessionOpen),
            new("2026-07-20T08:05:00Z", "m3", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "high"),
            new("2026-07-20T08:06:00Z", "m4", TelemetryEventKind.SessionOpen),
        };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 0);

        Assert.NotNull(report);
        Assert.Equal(4, report!.McpSessions);
        Assert.Equal(3, report.SessionsWithRecall.Count);
        Assert.Equal(75.0, report.SessionsWithRecall.Percent);
    }

    [Fact]
    public void Summarize_MoreHookSessionsThanMcpSessions_EmitsGapWarningWithCorrectDifference()
    {
        var records = new List<TelemetryRecord>
        {
            new("2026-07-20T08:00:00Z", "h1", TelemetryEventKind.SessionStart),
            new("2026-07-20T08:01:00Z", "h2", TelemetryEventKind.SessionStart),
            new("2026-07-20T08:02:00Z", "h3", TelemetryEventKind.SessionStart),
            new("2026-07-20T08:03:00Z", "m1", TelemetryEventKind.SessionOpen),
        };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 0);

        Assert.NotNull(report);
        Assert.Equal(1, report!.McpSessions);
        Assert.Equal(3, report.HookSessions);
        Assert.NotNull(report.HookGapWarning);
        Assert.Equal(3, report.HookGapWarning!.HookSessions);
        Assert.Equal(1, report.HookGapWarning.McpSessions);
        Assert.Equal(2, report.HookGapWarning.Difference);
    }

    [Fact]
    public void Summarize_EqualHookAndMcpSessionCounts_NoGapWarning()
    {
        var records = new List<TelemetryRecord>
        {
            new("2026-07-20T08:00:00Z", "h1", TelemetryEventKind.SessionStart),
            new("2026-07-20T08:01:00Z", "m1", TelemetryEventKind.SessionOpen),
            new("2026-07-20T08:02:00Z", "h2", TelemetryEventKind.SessionStart),
            new("2026-07-20T08:03:00Z", "m2", TelemetryEventKind.SessionOpen),
        };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 0);

        Assert.NotNull(report);
        Assert.Equal(2, report!.McpSessions);
        Assert.Equal(2, report.HookSessions);
        Assert.Null(report.HookGapWarning);
    }

    [Fact]
    public void Summarize_CompactionSurvivalFixture_CountsExactEventsAndSessions()
    {
        var records = new List<TelemetryRecord>
        {
            new("2026-08-04T08:00:00Z", "m1", TelemetryEventKind.SessionOpen),
            new("2026-08-04T08:01:00Z", "m1", TelemetryEventKind.Remember),
            new("2026-08-04T08:02:00Z", "h1", TelemetryEventKind.PreCompact),
            new("2026-08-04T08:03:00Z", "m1", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "partial", SessionFactCount: 1, LongTermFactCount: 0),
            new("2026-08-04T08:04:00Z", "m1", TelemetryEventKind.Recall, Query: "q", FactCount: 2, TokensReturned: 20, Coverage: "high", SessionFactCount: 2, LongTermFactCount: 0),

            new("2026-08-04T09:00:00Z", "m2", TelemetryEventKind.SessionOpen),
            new("2026-08-04T09:01:00Z", "h2", TelemetryEventKind.PreCompact),
            new("2026-08-04T09:02:00Z", "m2", TelemetryEventKind.Remember),
            new("2026-08-04T09:03:00Z", "m2", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "partial", SessionFactCount: 1, LongTermFactCount: 0),

            new("2026-08-04T10:00:00Z", "m3", TelemetryEventKind.SessionOpen),
            new("2026-08-04T10:01:00Z", "m3", TelemetryEventKind.Remember),
            new("2026-08-04T10:02:00Z", "m3", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "partial", SessionFactCount: 0, LongTermFactCount: 1),

            new("2026-08-04T11:00:00Z", "m4", TelemetryEventKind.SessionOpen),
            new("2026-08-04T11:01:00Z", "m4", TelemetryEventKind.Remember),
            new("2026-08-04T11:02:00Z", "m4", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "partial", SessionFactCount: 1, LongTermFactCount: 0),
        };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 0);

        Assert.NotNull(report);
        Assert.Equal(2, report!.CompactionSurvival.Events);
        Assert.Equal(1, report.CompactionSurvival.Sessions);
        Assert.False(string.IsNullOrWhiteSpace(report.CompactionSurvival.Note));

        Assert.Equal(4, report.SessionsWithSessionFactWrite.Count);
        Assert.Equal(100.0, report.SessionsWithSessionFactWrite.Percent);

        Assert.Equal(3, report.SessionsWithSessionFactRecall.Count);
        Assert.Equal(75.0, report.SessionsWithSessionFactRecall.Percent);
    }

    [Fact]
    public void Summarize_PriorSessionFactRecallAfterPreCompact_DoesNotCountTowardCompactionSurvival()
    {
        var records = new List<TelemetryRecord>
        {
            new("2026-08-04T08:00:00Z", "m1", TelemetryEventKind.SessionOpen),
            new("2026-08-04T08:01:00Z", "m1", TelemetryEventKind.Remember),
            new("2026-08-04T08:02:00Z", "h1", TelemetryEventKind.PreCompact),
            new("2026-08-04T08:03:00Z", "m1", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "partial", SessionFactCount: 0, LongTermFactCount: 0, PriorSessionFactCount: 1),
        };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 0);

        Assert.NotNull(report);
        Assert.Equal(0, report!.CompactionSurvival.Events);
        Assert.Equal(0, report.CompactionSurvival.Sessions);

        Assert.Equal(1, report.SessionsWithPriorSessionFactRecall.Count);
        Assert.Equal(100.0, report.SessionsWithPriorSessionFactRecall.Percent);
    }

    [Fact]
    public void Summarize_ZeroServerStartRecords_McpSessionsZero_NoDivideByZero()
    {
        var records = new List<TelemetryRecord>
        {
            new("2026-07-20T08:00:00Z", "h1", TelemetryEventKind.SessionStart),
        };

        var report = TelemetrySummarizer.Summarize(records, skippedLines: 0);

        Assert.NotNull(report);
        Assert.Equal(0, report!.McpSessions);
        Assert.Equal(1, report.HookSessions);
        Assert.Equal(0, report.SessionsWithRecall.Count);
        Assert.Equal(0.0, report.SessionsWithRecall.Percent);
        Assert.NotNull(report.HookGapWarning);
        Assert.Equal(1, report.HookGapWarning!.Difference);
    }
}
