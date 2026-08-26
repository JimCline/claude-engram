namespace Engram.EndToEnd.Tests;

/// <summary>
/// docs/memory-expansion/03-tool-profiles-spec.md, tier-3 test: a live MCP connection against
/// the published binary sees the tool list a profile actually advertises.
/// </summary>
public class ToolProfileEndToEndTests
{
    // Falsify: hardcode `.WithTools<EngramServerTools>()` unconditionally in ServeCommand and
    // confirm this test starts failing (full's tool set would equal default's). A hardcoded
    // count/array here reddened on every unrelated tool addition (engram_navigate did exactly
    // that), so this asserts the relationship instead: full is default plus exactly the three
    // lifecycle tools, no more, no less.
    [Fact]
    public async Task McpServer_FullProfile_AdvertisesExactlyDefaultPlusTheLifecycleTools()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var cancellationToken = TestContext.Current.CancellationToken;

        var defaultPort = FreeTcpPort.Next();
        var (defaultStartExit, _, defaultStartErr) = EngramProcess.Run(home.Root, "start", "--port", defaultPort.ToString());
        Assert.True(defaultStartExit == 0, $"engram start failed: {defaultStartErr}");

        string[] defaultTools;
        try
        {
            using var client = new HttpMcpClient(defaultPort);
            await client.InitializeAsync(cancellationToken);
            defaultTools = await ToolNamesAsync(client, cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        var (setExit, _, setErr) = EngramProcess.Run(home.Root, "profile", "set", "full");
        Assert.True(setExit == 0, $"engram profile set full failed: {setErr}");

        var fullPort = FreeTcpPort.Next();
        var (fullStartExit, _, fullStartErr) = EngramProcess.Run(home.Root, "start", "--port", fullPort.ToString());
        Assert.True(fullStartExit == 0, $"engram start failed: {fullStartErr}");

        string[] fullTools;
        try
        {
            using var client = new HttpMcpClient(fullPort);
            await client.InitializeAsync(cancellationToken);
            fullTools = await ToolNamesAsync(client, cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        ToolProfileAssertions.AssertFullEqualsDefaultPlusLifecycle(defaultTools, fullTools);
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
