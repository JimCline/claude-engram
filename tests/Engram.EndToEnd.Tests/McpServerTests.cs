using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

public class McpServerTests
{
    [Fact]
    public async Task McpServer_InitializeListToolsCallRecallAndWriteTelemetry()
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

            var toolsNode = await client.ListToolsAsync(cancellationToken);
            var toolNames = toolsNode!["result"]!["tools"]!.AsArray()
                .Select(t => t!["name"]!.GetValue<string>())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["engram_browse", "engram_expand", "engram_forget", "engram_recall", "engram_remember", "engram_revise", "engram_start", "engram_status", "engram_stop"], toolNames);

            var hitText = await client.CallToolTextAsync(
                "engram_recall", new JsonObject { ["query"] = "AOT packaging and Roslyn" }, cancellationToken);
            Assert.Contains("[f", hitText);
            Assert.Contains("coverage:", hitText);

            var missText = await client.CallToolTextAsync(
                "engram_recall", new JsonObject { ["query"] = "zzqqxxnonexistentquery12345" }, cancellationToken);
            Assert.Contains("coverage: none", missText);
            Assert.True(missText.Split('\n').Length < 5);

            await client.CallToolTextAsync(
                "engram_remember",
                new JsonObject { ["statement"] = "Test statement written by the end-to-end suite." },
                cancellationToken);

            var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
            Assert.True(File.Exists(telemetryPath));

            var kinds = File.ReadAllLines(telemetryPath)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString())
                .ToList();

            // Counted per kind rather than as a total. This is a shared log and the server writes
            // its own lifecycle records into it, so a total asserts something about the whole file
            // instead of about these four tool calls — the same reason the session-start hook tests
            // stopped counting lines. Nothing unexpected still gets to slip in: the last assertion
            // names every kind this exchange may produce.
            Assert.Equal(1, kinds.Count(k => k == "session-open"));
            Assert.Equal(2, kinds.Count(k => k == "recall"));
            Assert.Equal(1, kinds.Count(k => k == "remember"));
            Assert.Equal(1, kinds.Count(k => k == "server-start"));
            Assert.DoesNotContain(kinds, k =>
                k is not ("session-open" or "recall" or "remember" or "server-start"));
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public async Task McpServer_UninitialisedHome_StillAnswersInitializeAndListTools_WritesNoTelemetryFile()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome(initialize: false);
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            var toolsNode = await client.ListToolsAsync(cancellationToken);
            var toolNames = toolsNode!["result"]!["tools"]!.AsArray()
                .Select(t => t!["name"]!.GetValue<string>())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["engram_browse", "engram_expand", "engram_forget", "engram_recall", "engram_remember", "engram_revise", "engram_start", "engram_status", "engram_stop"], toolNames);

            var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
            Assert.False(File.Exists(telemetryPath));
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public async Task McpServer_SessionId_SharedWithinOneClientSession_DifferentAcrossClientSessions()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var clientA = new HttpMcpClient(port);
            await clientA.InitializeAsync(cancellationToken);
            await clientA.CallToolTextAsync("engram_recall", new JsonObject { ["query"] = "first call" }, cancellationToken);
            await clientA.CallToolTextAsync("engram_recall", new JsonObject { ["query"] = "second call" }, cancellationToken);

            using var clientB = new HttpMcpClient(port);
            await clientB.InitializeAsync(cancellationToken);
            await clientB.CallToolTextAsync("engram_recall", new JsonObject { ["query"] = "third call" }, cancellationToken);

            var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
            var recallSessionIds = File.ReadAllLines(telemetryPath)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .Where(element => element.GetProperty("kind").GetString() == "recall")
                .Select(element => element.GetProperty("session_id").GetString()!)
                .ToList();

            Assert.Equal(3, recallSessionIds.Count);
            Assert.Equal(recallSessionIds[0], recallSessionIds[1]);
            Assert.NotEqual(recallSessionIds[0], recallSessionIds[2]);
            Assert.Equal(clientA.SessionId, recallSessionIds[0]);
            Assert.Equal(clientB.SessionId, recallSessionIds[2]);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public async Task McpServer_Initialize_ReturnsMcpSessionIdHeader_EchoedOnSubsequentRequests()
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
            var initializeHeaders = await client.InitializeAsync(cancellationToken);

            Assert.True(initializeHeaders.TryGetValue("Mcp-Session-Id", out var mintedValues));
            var mintedSessionId = Assert.Single(mintedValues);
            Assert.False(string.IsNullOrWhiteSpace(mintedSessionId));
            Assert.Equal(mintedSessionId, client.SessionId);

            var subsequentHeaders = await client.ListToolsHeadersAsync(cancellationToken);
            Assert.True(subsequentHeaders.TryGetValue("Mcp-Session-Id", out var echoedValues));
            Assert.Equal(mintedSessionId, Assert.Single(echoedValues));
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }
}
