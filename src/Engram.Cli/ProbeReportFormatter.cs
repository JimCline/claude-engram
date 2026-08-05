using Engram.Core;

namespace Engram.Cli;

internal static class ProbeReportFormatter
{
    public static void WriteText(TextWriter stdout, TelemetryProbeReport report, string windowLabel)
    {
        var title = $"Engram Telemetry — {windowLabel}";
        stdout.WriteLine(title);
        stdout.WriteLine(new string('=', title.Length));
        stdout.WriteLine();
        if (report.McpSessions == 0)
        {
            stdout.WriteLine("  ADOPTION: no MCP sessions recorded (0 session-open records — the server may not have run)");
        }
        else
        {
            stdout.WriteLine($"  ADOPTION: {FormatPercent(report.SessionsWithRecall.Percent)} of sessions called recall ({report.SessionsWithRecall.Count}/{report.McpSessions} MCP sessions)");
        }

        stdout.WriteLine();
        if (report.McpSessions == 0)
        {
            stdout.WriteLine("  COMPACTION SURVIVAL: no MCP sessions recorded");
        }
        else
        {
            stdout.WriteLine(
                $"  COMPACTION SURVIVAL: {report.CompactionSurvival.Events} event(s) across " +
                $"{report.CompactionSurvival.Sessions} session(s) — a recall returned a current-session fact after a pre-compact moment");
            stdout.WriteLine($"    wrote a session fact             {report.SessionsWithSessionFactWrite.Count}/{report.McpSessions}  ({FormatPercent(report.SessionsWithSessionFactWrite.Percent)})");
            stdout.WriteLine($"    recalled a current-session fact  {report.SessionsWithSessionFactRecall.Count}/{report.McpSessions}  ({FormatPercent(report.SessionsWithSessionFactRecall.Percent)})");
            stdout.WriteLine($"    recalled a prior-session fact    {report.SessionsWithPriorSessionFactRecall.Count}/{report.McpSessions}  ({FormatPercent(report.SessionsWithPriorSessionFactRecall.Percent)})");
            stdout.WriteLine($"    {report.CompactionSurvival.Note}");
        }

        stdout.WriteLine();
        stdout.WriteLine($"Records:  {report.TotalRecords} total · {report.DateRange.From:O} .. {report.DateRange.To:O}");
        stdout.WriteLine($"Sessions: {report.McpSessions} MCP · {report.HookSessions} hook");
        stdout.WriteLine($"  recall    {report.SessionsWithRecall.Count}/{report.McpSessions}  ({FormatPercent(report.SessionsWithRecall.Percent)})");
        stdout.WriteLine($"  remember  {report.SessionsWithRemember.Count}/{report.McpSessions}  ({FormatPercent(report.SessionsWithRemember.Percent)})");
        stdout.WriteLine($"  digest    {report.SessionsWithDigest.Count}/{report.McpSessions}  ({FormatPercent(report.SessionsWithDigest.Percent)})");

        if (report.HookGapWarning is { } warning)
        {
            stdout.WriteLine();
            stdout.WriteLine($"  WARNING: {warning.Difference} session(s) ran without Engram's MCP server reachable ({warning.HookSessions} hook vs {warning.McpSessions} MCP) — memory was unavailable in those sessions.");
        }

        stdout.WriteLine();
        stdout.WriteLine($"Recalls per session:  median {report.MedianRecallsPerSession:0.0}  ·  max {report.MaxRecallsPerSession}");
        stdout.WriteLine($"Tokens per recall:    mean {report.MeanTokensPerRecall:0.0}  ·  median {report.MedianTokensPerRecall:0.0}");
        stdout.WriteLine();
        stdout.WriteLine("Coverage across all recalls:");
        stdout.WriteLine($"  high     {report.Coverage.HighCount,4}  ({FormatPercent(report.Coverage.HighPercent)})");
        stdout.WriteLine($"  partial  {report.Coverage.PartialCount,4}  ({FormatPercent(report.Coverage.PartialPercent)})");
        stdout.WriteLine($"  none     {report.Coverage.NoneCount,4}  ({FormatPercent(report.Coverage.NonePercent)})");

        if (report.FactsPerSession is { } density)
        {
            stdout.WriteLine();
            WriteFactsPerSession(stdout, density);
        }

        if (report.TopQueries.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine("Top queries:");
            var rank = 1;
            foreach (var query in report.TopQueries)
            {
                stdout.WriteLine($"  {rank,2}. \"{query.Query}\"  {query.Count}");
                rank++;
            }
        }

        if (report.SkippedLines > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine($"{report.SkippedLines} malformed line(s) skipped.");
        }
    }

    public static void WriteFactsPerSession(TextWriter stdout, FactsPerSessionStat density)
    {
        stdout.WriteLine("Facts per session (D16 gate):");

        if (density.Sessions == 0)
        {
            stdout.WriteLine("  no session has written a fact yet — nothing to measure");
            return;
        }

        stdout.WriteLine(
            $"  median {density.Median:0.0}  ·  min {density.Min}  ·  max {density.Max}  " +
            $"({density.Facts} facts across {density.Sessions} session(s))");
        stdout.WriteLine(density.MeetsGate
            ? $"  median is at or above the gate of {density.Gate} — the session-timeline view earns its tokens"
            : $"  median is below the gate of {density.Gate} — D16 lapses; a neighbour window this thin is noise");
        stdout.WriteLine($"  {density.Note}");
    }

    private static string FormatPercent(double percent) => $"{percent:0.0}%";
}
