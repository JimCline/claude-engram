using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

public class ProbeCommandTests
{
    [Fact]
    public async Task Probe_Json_CountsMatchGeneratedActivity()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-hook-a"}""", "hook", "session-start");
        EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-hook-b"}""", "hook", "session-start");

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            await client.CallToolTextAsync("engram_recall", new JsonObject { ["query"] = "AOT packaging and Roslyn" }, cancellationToken);
            await client.CallToolTextAsync("engram_recall", new JsonObject { ["query"] = "AOT packaging and Roslyn" }, cancellationToken);
            await client.CallToolTextAsync("engram_recall", new JsonObject { ["query"] = "zzqqxxnonexistentquery12345" }, cancellationToken);
            await client.CallToolTextAsync(
                "engram_remember", new JsonObject { ["statement"] = "Test statement from the probe end-to-end test." }, cancellationToken);
            await client.CallToolTextAsync(
                "engram_digest", new JsonObject { ["learnings"] = new JsonArray("probe end-to-end learning") }, cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
        var rawRecords = File.ReadAllLines(telemetryPath)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var expectedTotalRecords = rawRecords.Count;
        var expectedHookSessions = rawRecords
            .Where(r => r.GetProperty("kind").GetString() == "session-start")
            .Select(r => r.GetProperty("session_id").GetString())
            .Distinct()
            .Count();
        var expectedMcpSessions = rawRecords
            .Where(r => r.GetProperty("kind").GetString() == "session-open")
            .Select(r => r.GetProperty("session_id").GetString())
            .Distinct()
            .Count();

        var recallRecords = rawRecords.Where(r => r.GetProperty("kind").GetString() == "recall").ToList();
        var expectedSessionsWithRecall = recallRecords
            .Select(r => r.GetProperty("session_id").GetString())
            .Distinct()
            .Count();

        var expectedCoverageCounts = recallRecords
            .GroupBy(r => r.GetProperty("coverage").GetString())
            .ToDictionary(g => g.Key!, g => g.Count());

        var expectedTopQuery = recallRecords
            .GroupBy(r => r.GetProperty("query").GetString())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "probe", "--json");
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var report = JsonDocument.Parse(stdout);
        var root = report.RootElement;

        Assert.Equal(expectedTotalRecords, root.GetProperty("total_records").GetInt32());
        Assert.Equal(expectedHookSessions, root.GetProperty("hook_sessions").GetInt32());
        Assert.Equal(expectedMcpSessions, root.GetProperty("mcp_sessions").GetInt32());
        Assert.Equal(expectedSessionsWithRecall, root.GetProperty("sessions_with_recall").GetProperty("count").GetInt32());

        // The false-outage case, end to end, against a real server: two hook sessions, one MCP
        // session, and the server was up for the whole run — it answered the tool calls above.
        // The assertion that stood here required the report to call that spare hook session one in
        // which "memory was unavailable", while this very test was using memory through it.
        Assert.Equal(1, expectedMcpSessions);
        Assert.True(expectedHookSessions > expectedMcpSessions);
        Assert.False(root.GetProperty("memory_never_reached").GetBoolean());
        Assert.False(root.TryGetProperty("hook_gap_warning", out _));

        // Disjoint id spaces, asserted where both real issuers are present rather than argued from
        // one instance's data: the hook writes the id Claude Code handed it, the server writes the
        // one its transport minted, and nothing relates them.
        var hookIds = rawRecords
            .Where(r => r.GetProperty("kind").GetString() == "session-start")
            .Select(r => r.GetProperty("session_id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var mcpIds = rawRecords
            .Where(r => r.GetProperty("kind").GetString() == "session-open")
            .Select(r => r.GetProperty("session_id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(hookIds);
        Assert.NotEmpty(mcpIds);
        Assert.Empty(hookIds.Intersect(mcpIds, StringComparer.Ordinal));

        var coverage = root.GetProperty("coverage");
        Assert.Equal(expectedCoverageCounts.GetValueOrDefault("high", 0), coverage.GetProperty("high_count").GetInt32());
        Assert.Equal(expectedCoverageCounts.GetValueOrDefault("partial", 0), coverage.GetProperty("partial_count").GetInt32());
        Assert.Equal(expectedCoverageCounts.GetValueOrDefault("none", 0), coverage.GetProperty("none_count").GetInt32());

        var topQueries = root.GetProperty("top_queries").EnumerateArray().ToList();
        Assert.NotEmpty(topQueries);
        Assert.Equal(expectedTopQuery.Key, topQueries[0].GetProperty("query").GetString());
        Assert.Equal(expectedTopQuery.Count(), topQueries[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Probe_Json_OneMcpSessionWithOneRecall_Reports100PercentAdoption()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);
            await client.CallToolTextAsync("engram_recall", new JsonObject { ["query"] = "AOT packaging and Roslyn" }, cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "probe", "--json");
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var report = JsonDocument.Parse(stdout);
        var root = report.RootElement;

        Assert.Equal(1, root.GetProperty("mcp_sessions").GetInt32());
        Assert.Equal(1, root.GetProperty("sessions_with_recall").GetProperty("count").GetInt32());
        Assert.Equal(100.0, root.GetProperty("sessions_with_recall").GetProperty("percent").GetDouble());
    }
}
