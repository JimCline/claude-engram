using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Engram.EndToEnd.Tests;

public class McpServerTests
{
    [Fact]
    public async Task McpServer_InitializeListToolsCallRecallAndWriteTelemetry()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var transport = new StdioClientTransport(new()
        {
            Name = "engram-e2e-test",
            Command = EndToEndBinary.Path!,
            Arguments = ["mcp"],
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["ENGRAM_HOME"] = home.Root },
        });

        var cancellationToken = TestContext.Current.CancellationToken;

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        Assert.Equal(
            ["engram_digest", "engram_recall", "engram_remember"],
            tools.Select(t => t.Name).OrderBy(name => name, StringComparer.Ordinal));

        var hit = await client.CallToolAsync(
            "engram_recall",
            new Dictionary<string, object?> { ["query"] = "AOT packaging and Roslyn" },
            cancellationToken: cancellationToken);
        var hitText = ExtractText(hit);
        Assert.Contains("[f", hitText);
        Assert.Contains("coverage:", hitText);

        var miss = await client.CallToolAsync(
            "engram_recall",
            new Dictionary<string, object?> { ["query"] = "zzqqxxnonexistentquery12345" },
            cancellationToken: cancellationToken);
        var missText = ExtractText(miss);
        Assert.Contains("coverage: none", missText);
        Assert.True(missText.Split('\n').Length < 5);

        await client.CallToolAsync(
            "engram_remember",
            new Dictionary<string, object?> { ["statement"] = "Test statement written by the end-to-end suite." },
            cancellationToken: cancellationToken);

        await client.CallToolAsync(
            "engram_digest",
            new Dictionary<string, object?> { ["learnings"] = new[] { "learning one from the end-to-end suite" } },
            cancellationToken: cancellationToken);

        var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
        Assert.True(File.Exists(telemetryPath));

        var kinds = File.ReadAllLines(telemetryPath)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString())
            .ToList();

        Assert.Equal(4, kinds.Count);
        Assert.Equal(2, kinds.Count(k => k == "recall"));
        Assert.Equal(1, kinds.Count(k => k == "remember"));
        Assert.Equal(1, kinds.Count(k => k == "digest"));
    }

    [Fact]
    public async Task McpServer_SessionId_SharedWithinProcess_DifferentAcrossProcesses()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        var cancellationToken = TestContext.Current.CancellationToken;

        using var homeA = new TestHome();
        var sessionIdsA = await CallRecallTwiceAndGetSessionIds(homeA.Root, cancellationToken);
        Assert.Equal(2, sessionIdsA.Count);
        Assert.Equal(sessionIdsA[0], sessionIdsA[1]);

        using var homeB = new TestHome();
        var sessionIdsB = await CallRecallTwiceAndGetSessionIds(homeB.Root, cancellationToken);
        Assert.Equal(2, sessionIdsB.Count);
        Assert.Equal(sessionIdsB[0], sessionIdsB[1]);

        Assert.NotEqual(sessionIdsA[0], sessionIdsB[0]);
    }

    private static async Task<List<string>> CallRecallTwiceAndGetSessionIds(string home, CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new()
        {
            Name = "engram-e2e-test",
            Command = EndToEndBinary.Path!,
            Arguments = ["mcp"],
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["ENGRAM_HOME"] = home },
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

        await client.CallToolAsync(
            "engram_recall",
            new Dictionary<string, object?> { ["query"] = "first call" },
            cancellationToken: cancellationToken);
        await client.CallToolAsync(
            "engram_recall",
            new Dictionary<string, object?> { ["query"] = "second call" },
            cancellationToken: cancellationToken);

        var telemetryPath = Path.Combine(home, "telemetry.jsonl");
        var lines = File.ReadAllLines(telemetryPath);

        return lines
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("session_id").GetString()!)
            .ToList();
    }

    private static string ExtractText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
