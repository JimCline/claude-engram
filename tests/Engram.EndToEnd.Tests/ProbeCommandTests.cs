using System.Text.Json;
using ModelContextProtocol.Client;

namespace Engram.EndToEnd.Tests;

public class ProbeCommandTests
{
    [Fact]
    public async Task Probe_Json_CountsMatchGeneratedActivity()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var cancellationToken = TestContext.Current.CancellationToken;

        EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-hook-a"}""", "hook", "session-start");
        EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-hook-b"}""", "hook", "session-start");

        var transport = new StdioClientTransport(new()
        {
            Name = "engram-e2e-probe-test",
            Command = EndToEndBinary.Path!,
            Arguments = ["mcp"],
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["ENGRAM_HOME"] = home.Root },
        });

        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken))
        {
            await client.CallToolAsync(
                "engram_recall",
                new Dictionary<string, object?> { ["query"] = "AOT packaging and Roslyn" },
                cancellationToken: cancellationToken);
            await client.CallToolAsync(
                "engram_recall",
                new Dictionary<string, object?> { ["query"] = "AOT packaging and Roslyn" },
                cancellationToken: cancellationToken);
            await client.CallToolAsync(
                "engram_recall",
                new Dictionary<string, object?> { ["query"] = "zzqqxxnonexistentquery12345" },
                cancellationToken: cancellationToken);
            await client.CallToolAsync(
                "engram_remember",
                new Dictionary<string, object?> { ["statement"] = "Test statement from the probe end-to-end test." },
                cancellationToken: cancellationToken);
            await client.CallToolAsync(
                "engram_digest",
                new Dictionary<string, object?> { ["learnings"] = new[] { "probe end-to-end learning" } },
                cancellationToken: cancellationToken);
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
            .Where(r => r.GetProperty("kind").GetString() == "server-start")
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

        Assert.Equal(1, expectedMcpSessions);
        Assert.True(expectedHookSessions > expectedMcpSessions);
        var warning = root.GetProperty("hook_gap_warning");
        Assert.NotEqual(JsonValueKind.Null, warning.ValueKind);
        Assert.Equal(expectedHookSessions - expectedMcpSessions, warning.GetProperty("difference").GetInt32());

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
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new StdioClientTransport(new()
        {
            Name = "engram-e2e-probe-adoption-test",
            Command = EndToEndBinary.Path!,
            Arguments = ["mcp"],
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["ENGRAM_HOME"] = home.Root },
        });

        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken))
        {
            await client.CallToolAsync(
                "engram_recall",
                new Dictionary<string, object?> { ["query"] = "AOT packaging and Roslyn" },
                cancellationToken: cancellationToken);
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
