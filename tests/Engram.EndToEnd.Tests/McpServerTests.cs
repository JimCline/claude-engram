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

    private static string ExtractText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
