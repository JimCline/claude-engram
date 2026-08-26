namespace Engram.EndToEnd.Tests;

/// <summary>
/// D-5: full profile is exactly default plus these three lifecycle tools. A test that hardcodes
/// the default or full tool count/array reddens on every unrelated tool addition — this asserts
/// the relationship instead, which stays true regardless of how many non-lifecycle tools exist.
/// </summary>
internal static class ToolProfileAssertions
{
    public static readonly string[] LifecycleToolNames = ["engram_start", "engram_status", "engram_stop"];

    public static void AssertFullEqualsDefaultPlusLifecycle(IReadOnlyList<string> defaultTools, IReadOnlyList<string> fullTools)
    {
        var expectedFull = defaultTools.Concat(LifecycleToolNames)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedFull, fullTools);
        Assert.DoesNotContain(defaultTools, LifecycleToolNames.Contains);
    }

    /// <summary>
    /// Starts a throwaway full-profile server just to read its tool list — the full tool universe
    /// is only observable by asking the binary, since this project deliberately carries no
    /// reference to Engram.Cli/Engram.Core (asserting through the code under test would stop
    /// proving anything about the binary that ships).
    /// </summary>
    public static async Task<string[]> FullProfileToolNamesAsync(CancellationToken cancellationToken)
    {
        using var home = new TestHome();
        var (setExit, _, setErr) = EngramProcess.Run(home.Root, "profile", "set", "full");
        Assert.True(setExit == 0, $"engram profile set full failed: {setErr}");

        var port = FreeTcpPort.Next();
        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);
            var toolsNode = await client.ListToolsAsync(cancellationToken);
            return toolsNode!["result"]!["tools"]!.AsArray()
                .Select(t => t!["name"]!.GetValue<string>())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    public static void AssertIsExactlyTheDefaultProfileToolSet(IReadOnlyList<string> toolNames, IReadOnlyList<string> fullProfileToolNames)
    {
        var expectedDefault = fullProfileToolNames.Except(LifecycleToolNames, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedDefault, toolNames);
    }
}
