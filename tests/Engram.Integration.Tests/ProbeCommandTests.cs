using System.Text.Json;
using System.Text.RegularExpressions;
using Engram.Cli;

namespace Engram.Integration.Tests;

public class ProbeCommandTests
{
    private static readonly string[] FixtureLines =
    [
        """{"timestamp":"2026-07-20T07:59:00Z","session_id":"h1","kind":"session-start"}""",
        """{"timestamp":"2026-07-20T08:00:00Z","session_id":"m1","kind":"session-open"}""",
        """{"timestamp":"2026-07-20T08:01:00Z","session_id":"m1","kind":"recall","query":"alpha","fact_count":3,"tokens_returned":100,"coverage":"high"}""",
        """{"timestamp":"2026-07-20T08:02:00Z","session_id":"m1","kind":"recall","query":"alpha","fact_count":3,"tokens_returned":200,"coverage":"high"}""",
        """{"timestamp":"2026-07-20T08:03:00Z","session_id":"m1","kind":"remember"}""",
        """{"timestamp":"2026-07-20T08:04:00Z","session_id":"m1","kind":"digest"}""",
        """{"timestamp":"2026-07-21T08:59:00Z","session_id":"h2","kind":"session-start"}""",
        """{"timestamp":"2026-07-21T09:00:00Z","session_id":"m2","kind":"session-open"}""",
        """{"timestamp":"2026-07-21T09:01:00Z","session_id":"m2","kind":"recall","query":"beta","fact_count":1,"tokens_returned":50,"coverage":"partial"}""",
        """{"timestamp":"2026-07-22T09:59:00Z","session_id":"h3","kind":"session-start"}""",
        """{"timestamp":"2026-07-22T10:00:00Z","session_id":"m3","kind":"session-open"}""",
        """{"timestamp":"2026-07-22T10:01:00Z","session_id":"m3","kind":"recall","query":"gamma","fact_count":0,"tokens_returned":10,"coverage":"none"}""",
        """{"timestamp":"2026-07-22T10:02:00Z","session_id":"m3","kind":"remember"}""",
        """{"timestamp":"2026-07-23T10:59:00Z","session_id":"h4","kind":"session-start"}""",
        """{"timestamp":"2026-07-23T11:00:00Z","session_id":"m4","kind":"session-open"}""",
        """{"timestamp":"2026-07-24T11:59:00Z","session_id":"h5","kind":"session-start"}""",
        """{"timestamp":"2026-07-24T12:00:00Z","session_id":"m5","kind":"session-open"}""",
        """{"timestamp":"2026-07-24T12:01:00Z","session_id":"m5","kind":"recall","query":"alpha","fact_count":3,"tokens_returned":150,"coverage":"high"}""",
        """{"timestamp":"2026-07-24T12:02:00Z","session_id":"m5","kind":"recall","query":"beta","fact_count":1,"tokens_returned":90,"coverage":"partial"}""",
        """{"timestamp":"2026-07-24T12:03:00Z","session_id":"m5","kind":"digest"}""",
        """{"timestamp":"2026-07-25T09:00:00Z","session_id":"h6","kind":"session-start"}""",
    ];

    private static string TelemetryPath(SandboxHome sandbox) => Path.Combine(sandbox.Home.Root, "telemetry.jsonl");

    private static void WriteFixture(SandboxHome sandbox, IEnumerable<string> lines) =>
        File.WriteAllLines(TelemetryPath(sandbox), lines);

    [Fact]
    public void Probe_HandComputedFixture_TextOutput_ReportsExactNumbers()
    {
        using var sandbox = new SandboxHome();
        WriteFixture(sandbox, FixtureLines);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        var text = stdout.ToString();
        Assert.Contains("80.0% of MCP sessions called recall (4/5)", text);
        Assert.Contains("Sessions: 5 MCP · 6 hook", text);
        Assert.Contains("disjoint id spaces", text);

        // Six hook sessions against five MCP ones is the ordinary case: one session did not call a
        // memory tool. This fixture used to assert the opposite in so many words.
        Assert.DoesNotContain("WARNING", text);
        Assert.Contains("median 1.0", text);
        Assert.Contains("max 2", text);
        Assert.Contains("mean 100.0", text);
        Assert.Contains("median 95.0", text);
        Assert.Matches(new Regex(@"high\s+3\s+\(50\.0%\)"), text);
        Assert.Matches(new Regex(@"partial\s+2\s+\(33\.3%\)"), text);
        Assert.Matches(new Regex(@"none\s+1\s+\(16\.7%\)"), text);
        Assert.Contains("\"alpha\"  3", text);
    }

    [Fact]
    public void Probe_HandComputedFixture_JsonOutput_MatchesExactNumbers()
    {
        using var sandbox = new SandboxHome();
        WriteFixture(sandbox, FixtureLines);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe", "--json"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;

        Assert.Equal(21, root.GetProperty("total_records").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped_lines").GetInt32());
        Assert.Equal(5, root.GetProperty("mcp_sessions").GetInt32());
        Assert.Equal(6, root.GetProperty("hook_sessions").GetInt32());

        Assert.False(root.GetProperty("memory_never_reached").GetBoolean());

        // The key that carried the subtraction is gone, not merely unset. A consumer reading
        // hook_gap_warning.difference was reading a number with no referent, and leaving the
        // property in place as null would keep that reading available.
        Assert.False(root.TryGetProperty("hook_gap_warning", out _));

        var sessionsWithRecall = root.GetProperty("sessions_with_recall");
        Assert.Equal(4, sessionsWithRecall.GetProperty("count").GetInt32());
        Assert.Equal(80.0, sessionsWithRecall.GetProperty("percent").GetDouble());

        var sessionsWithRemember = root.GetProperty("sessions_with_remember");
        Assert.Equal(2, sessionsWithRemember.GetProperty("count").GetInt32());
        Assert.Equal(40.0, sessionsWithRemember.GetProperty("percent").GetDouble());

        var sessionsWithDigest = root.GetProperty("sessions_with_digest");
        Assert.Equal(2, sessionsWithDigest.GetProperty("count").GetInt32());
        Assert.Equal(40.0, sessionsWithDigest.GetProperty("percent").GetDouble());

        Assert.Equal(1.0, root.GetProperty("median_recalls_per_session").GetDouble());
        Assert.Equal(2, root.GetProperty("max_recalls_per_session").GetInt32());

        var coverage = root.GetProperty("coverage");
        Assert.Equal(3, coverage.GetProperty("high_count").GetInt32());
        Assert.Equal(50.0, coverage.GetProperty("high_percent").GetDouble());
        Assert.Equal(2, coverage.GetProperty("partial_count").GetInt32());
        Assert.Equal(33.3, coverage.GetProperty("partial_percent").GetDouble());
        Assert.Equal(1, coverage.GetProperty("none_count").GetInt32());
        Assert.Equal(16.7, coverage.GetProperty("none_percent").GetDouble());

        Assert.Equal(100.0, root.GetProperty("mean_tokens_per_recall").GetDouble());
        Assert.Equal(95.0, root.GetProperty("median_tokens_per_recall").GetDouble());

        var topQueries = root.GetProperty("top_queries").EnumerateArray().ToList();
        Assert.Equal(3, topQueries.Count);
        Assert.Equal("alpha", topQueries[0].GetProperty("query").GetString());
        Assert.Equal(3, topQueries[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public void Probe_MissingTelemetryFile_ExitsZeroWithClearMessage()
    {
        using var sandbox = new SandboxHome();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("no telemetry", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_EmptyFile_ExitsZeroWithoutDividingByZero()
    {
        using var sandbox = new SandboxHome();
        File.WriteAllText(TelemetryPath(sandbox), string.Empty);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("no telemetry", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_ZeroServerStartRecords_TextOutput_ReportsNoMcpSessionsClearly()
    {
        using var sandbox = new SandboxHome();
        WriteFixture(sandbox, new[]
        {
            """{"timestamp":"2026-07-20T08:00:00Z","session_id":"h1","kind":"session-start"}""",
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        var text = stdout.ToString();
        Assert.Contains("no MCP sessions recorded", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NaN", text);
        Assert.DoesNotContain("Infinity", text);
    }

    [Fact]
    public void Probe_EqualHookAndMcpSessionCounts_TextOutput_NoWarningLine()
    {
        using var sandbox = new SandboxHome();
        WriteFixture(sandbox, new[]
        {
            """{"timestamp":"2026-07-20T08:00:00Z","session_id":"h1","kind":"session-start"}""",
            """{"timestamp":"2026-07-20T08:01:00Z","session_id":"m1","kind":"session-open"}""",
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.DoesNotContain("WARNING", stdout.ToString());
    }

    /// <summary>
    /// Sessions ran and none reached the server — flagged, and worded as a question.
    /// </summary>
    /// <remarks>
    /// The line it replaced claimed memory "was unavailable", which the telemetry cannot know: no
    /// record here observes the server at all, only whether a tool was called. This one names what
    /// was counted and hands off to doctor, which can actually look.
    /// </remarks>
    [Fact]
    public void Probe_NoSessionEverCalledAMemoryTool_WarnsAndSendsYouToDoctor()
    {
        using var sandbox = new SandboxHome();
        WriteFixture(sandbox, new[]
        {
            """{"timestamp":"2026-07-20T08:00:00Z","session_id":"h1","kind":"session-start"}""",
            """{"timestamp":"2026-07-20T08:02:00Z","session_id":"h2","kind":"session-start"}""",
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe"], stdout, stderr);

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();
        Assert.Contains("WARNING: 2 session(s) started and not one called a memory tool.", text);
        Assert.Contains("engram doctor", text);
        Assert.DoesNotContain("unavailable", text);
    }

    [Fact]
    public void Probe_TwoMalformedLinesAmongGoodOnes_ReportsExactlyTwoSkipped()
    {
        using var sandbox = new SandboxHome();
        var lines = new List<string>(FixtureLines)
        {
            "this is not json at all {{{",
            """{"session_id":"s6","kind":"recall"}""",
        };
        WriteFixture(sandbox, lines);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe", "--json"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(2, doc.RootElement.GetProperty("skipped_lines").GetInt32());
        Assert.Equal(21, doc.RootElement.GetProperty("total_records").GetInt32());
    }

    [Fact]
    public void Probe_Since7d_ExcludesOlderRecords()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        var oldTimestamp = now.AddDays(-30).ToString("O");
        var recentTimestamp = now.AddDays(-1).ToString("O");

        var lines = new[]
        {
            $$"""{"timestamp":"{{oldTimestamp}}","session_id":"old-session","kind":"session-start"}""",
            $$"""{"timestamp":"{{oldTimestamp}}","session_id":"old-session","kind":"recall","query":"stale","fact_count":1,"tokens_returned":40,"coverage":"partial"}""",
            $$"""{"timestamp":"{{recentTimestamp}}","session_id":"recent-session","kind":"session-start"}""",
            $$"""{"timestamp":"{{recentTimestamp}}","session_id":"recent-session","kind":"recall","query":"fresh","fact_count":3,"tokens_returned":80,"coverage":"high"}""",
        };
        WriteFixture(sandbox, lines);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe", "--json", "--since", "7d"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("total_records").GetInt32());
        Assert.Equal(1, root.GetProperty("hook_sessions").GetInt32());
        Assert.Equal(0, root.GetProperty("mcp_sessions").GetInt32());

        var topQueries = root.GetProperty("top_queries").EnumerateArray().ToList();
        Assert.Single(topQueries);
        Assert.Equal("fresh", topQueries[0].GetProperty("query").GetString());
    }

    [Fact]
    public void Probe_UnknownOption_PrintsUsageAndExitsOne()
    {
        using var sandbox = new SandboxHome();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "probe", "--bogus"], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("usage:", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
