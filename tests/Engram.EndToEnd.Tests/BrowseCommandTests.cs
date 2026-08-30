using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3 (D9): <c>engram browse</c> against the published binary, driven through the plain
/// (redirected-stdio) path — the same path every non-pty CLI test takes
/// (docs/memory-expansion/05-browse-tui-spec.md).
/// </summary>
public class BrowseCommandTests
{
    [Fact]
    public async Task Browse_AfterEveryFactIsForgotten_StillShowsStructureRatherThanNothing()
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

            // engram init unconditionally seeds 45 canned facts (CannedFactSeeder, D10) —
            // retract every one, the strongest case for an empty root.
            for (var id = 1; id <= 45; id++)
            {
                await client.CallToolTextAsync("engram_forget", new JsonObject { ["fact_id"] = "f" + id }, cancellationToken);
            }
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(home.Root, stdin: null, "browse");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        // Facts are append-only (D8): forgetting only closes a fact, it never deletes the
        // entity row that addresses it, so root still matches every entity init ever created.
        // "Nothing in memory under /" is therefore unreachable at root once a store has been
        // through init, even with zero live facts left.
        Assert.Contains("0 facts here, 0 under it", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browse_QuitsCleanly_AfterListingAFact()
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
            await client.CallToolTextAsync(
                "engram_remember", new JsonObject { ["statement"] = "The browse e2e test wrote this fact." }, cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(home.Root, "q\n", "browse");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("browse>", stdout, StringComparison.Ordinal);
    }
}
