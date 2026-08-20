namespace Engram.EndToEnd.Tests;

/// <summary>
/// docs/memory-expansion/03-tool-profiles-spec.md, tier-3 test: a live MCP connection against
/// the published binary sees the tool list a profile actually advertises.
/// </summary>
public class ToolProfileEndToEndTests
{
    [Fact]
    public async Task McpServer_UnderDefaultProfile_AdvertisesExactlyEightTools()
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

            var toolNames = await ToolNamesAsync(client, cancellationToken);

            Assert.Equal(
                ["engram_browse", "engram_expand", "engram_forget", "engram_index_repo", "engram_judge", "engram_recall", "engram_remember", "engram_revise"],
                toolNames);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    // Falsify: hardcode `.WithTools<EngramServerTools>()` unconditionally in ServeCommand and
    // confirm this test starts failing (the count would read 11 rather than 8).
    [Fact]
    public async Task McpServer_UnderFullProfile_AdvertisesAllElevenTools()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (setExit, _, setErr) = EngramProcess.Run(home.Root, "profile", "set", "full");
        Assert.True(setExit == 0, $"engram profile set full failed: {setErr}");

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            var toolNames = await ToolNamesAsync(client, cancellationToken);

            Assert.Equal(
                ["engram_browse", "engram_expand", "engram_forget", "engram_index_repo", "engram_judge", "engram_recall", "engram_remember", "engram_revise", "engram_start", "engram_status", "engram_stop"],
                toolNames);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public async Task ProfileShow_ReflectsWhatProfileSetLastWrote()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (showBeforeExit, showBeforeOut, _) = EngramProcess.Run(home.Root, "profile", "show");
        Assert.Equal(0, showBeforeExit);
        Assert.Contains("default", showBeforeOut);

        var (setExit, _, setErr) = EngramProcess.Run(home.Root, "profile", "set", "full");
        Assert.True(setExit == 0, $"engram profile set full failed: {setErr}");

        var (showAfterExit, showAfterOut, _) = EngramProcess.Run(home.Root, "profile", "show");
        Assert.Equal(0, showAfterExit);
        Assert.Contains("full", showAfterOut);
    }

    private static async Task<string[]> ToolNamesAsync(HttpMcpClient client, CancellationToken cancellationToken)
    {
        var toolsNode = await client.ListToolsAsync(cancellationToken);
        return toolsNode!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }
}
