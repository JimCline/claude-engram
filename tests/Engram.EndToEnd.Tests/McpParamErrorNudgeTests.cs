using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3 (D9): docs/specs/mcp-param-error-nudge.md §6.1 — the CallTool filter fires against the
/// real published binary through a real MCP client, driven by an actual argument-binding
/// failure inside the SDK's own AIFunction marshaller (no unit test reaches that code path,
/// since it runs before any engram tool method is entered). §6.2's pass-through/predicate
/// assertion and §6.4's registration-removal falsification live in
/// Engram.Integration.Tests.McpCallNudgeTests and were run manually per the spec's own
/// falsification methodology (restore-after-redden), not as a committed automated check.
/// </summary>
public class McpParamErrorNudgeTests
{
    [Fact]
    public async Task MissingRequiredArgument_ReturnsTheNudge_NotTheSanitizedSdkMessage()
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

            // engram_recall's only required argument is `query`; omitting it is the SDK's own
            // AIFunction marshaller throwing before EngramMcpTools.Recall is ever entered
            // (spec §1 consequence 1) — the exact failure class Jim reported.
            var text = await client.CallToolTextAsync("engram_recall", new JsonObject(), cancellationToken);

            Assert.Contains("engram_recall", text);
            Assert.Contains("the arguments did not match this tool's schema, so nothing ran", text);
            Assert.DoesNotContain("An error occurred invoking", text);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public async Task WrongArgumentType_EchoesTheReceivedArgumentNames()
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

            var recallText = await client.CallToolTextAsync(
                "engram_recall",
                new JsonObject { ["query"] = "anything" },
                cancellationToken);
            Assert.False(recallText.Contains("did not match this tool's schema", StringComparison.Ordinal));

            // navigate.limit is a non-nullable int; a JSON string fails to deserialize inside the
            // marshaller (spec §1.1's 2b) rather than reaching EngramMcpTools.Navigate.
            var navigateText = await client.CallToolTextAsync(
                "engram_navigate",
                new JsonObject { ["query"] = "anything", ["limit"] = "not-a-number" },
                cancellationToken);

            Assert.Contains("engram_navigate", navigateText);
            // NE-4 (spec §7): request.Params.Arguments survives the round trip through next(...)
            // intact, so the received argument names are exactly what the model actually sent.
            Assert.Contains("Received: query, limit", navigateText);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }
}
