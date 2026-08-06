using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class EngramMcpToolsTests
{
    private static readonly McpHomeState Initialized = new(true);

    /// <summary>
    /// A runtime that will never be asked to start anything: these homes configure no provider,
    /// so the vector lane refuses before a model is ever considered. Constructing one launches
    /// nothing, which is the property that makes it safe to hand over and drop.
    /// </summary>
    private static LocalRuntime NoRuntime(EngramHome home) => new(home, _ => null);

    // Recall's long-term tier now comes from SQLite rather than a hardcoded list. Every other
    // test here exercises session facts, so without this one the whole store could return
    // nothing and the suite would still pass.
    [Fact]
    public void Recall_ReturnsLongTermFactsReadFromTheStore()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-longterm");

        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "BEGIN IMMEDIATE transaction");

        // Assert on words from the fact's BODY that do not appear in the query. Recall echoes
        // the query in its header line and again in its gap message, so asserting on a query
        // term passes even when the store returns nothing at all — checked, by emptying it.
        Assert.Contains("SQLITE_BUSY_SNAPSHOT", result, StringComparison.Ordinal);
        Assert.DoesNotContain("0 facts", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Remember_ThenRecallSameSession_ReturnsTheNoteInTheSessionTier()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The build pipeline retries flaky uploads three times before failing."));

        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "flaky uploads retries");

        Assert.Contains($"[{handle}]", result);
        Assert.Contains("three times before failing", result);
        Assert.Contains("(session)", result);
    }

    [Fact]
    public void Remember_ThenRecallDifferentSession_ReturnsThePriorSessionNoteWithItsSessionMarked()
    {
        using var sandbox = new SandboxHome();
        var writer = new McpSessionId("session-a");
        var reader = new McpSessionId("session-b");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, writer, Initialized, "The build pipeline retries flaky uploads three times before failing."));

        var result = EngramMcpTools.Recall(sandbox.Home, reader, Initialized, NoRuntime(sandbox.Home), "flaky uploads retries");

        Assert.Contains($"[{handle}]", result);
        Assert.Contains("session · p1 ·", result);
        Assert.Contains("three times before failing", result);
        Assert.DoesNotContain("coverage: none", result);
    }

    [Fact]
    public void Recall_CurrentSessionNoteRanksAboveAPriorSessionNote_AndBothAreDistinguishable()
    {
        using var sandbox = new SandboxHome();
        var priorSession = new McpSessionId("session-old");
        var currentSession = new McpSessionId("session-new");

        var priorHandle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, priorSession, Initialized, "The nightly backup job runs at 2am UTC."));
        var currentHandle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, currentSession, Initialized, "The nightly backup job now also verifies checksums."));

        var result = EngramMcpTools.Recall(sandbox.Home, currentSession, Initialized, NoRuntime(sandbox.Home), "nightly backup job");

        var currentHandleIndex = result.IndexOf($"[{currentHandle}]", StringComparison.Ordinal);
        var priorHandleIndex = result.IndexOf($"[{priorHandle}]", StringComparison.Ordinal);

        Assert.True(currentHandleIndex >= 0, $"current-session handle [{currentHandle}] should be present");
        Assert.True(priorHandleIndex >= 0, $"prior-session handle [{priorHandle}] should be present");
        Assert.True(currentHandleIndex < priorHandleIndex, "current-session fact must rank above the prior-session fact");
    }

    // The reason session notes moved onto the store. In the JSONL format there was no way to
    // express a retracted note, so engram_forget refused them outright and a mistaken note
    // stayed recallable for good.
    [Fact]
    public void Forget_RetractsASessionNoteAndItStopsBeingRecalled()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The build pipeline retries flaky uploads three times before failing."));

        var response = EngramMcpTools.Forget(sandbox.Home, session, Initialized, handle);
        Assert.Contains("Retracted", response);

        // On the body, not the query: recall echoes the query in its header and again in the
        // gap message, so asserting the query terms are gone passes even when nothing was
        // retracted at all.
        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "flaky uploads retries");
        Assert.DoesNotContain("three times before failing", result);
        Assert.DoesNotContain($"[{handle}]", result);
    }

    [Fact]
    public void Remember_ReturnsAFactHandleInResponseText()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var response = EngramMcpTools.Remember(sandbox.Home, session, Initialized, "Statement one.");

        Assert.Matches(@"^\[f\d+\] remembered:", response);
    }

    [Fact]
    public void Remember_WithAgentName_AttributesTheNoteToThatAgent()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        EngramMcpTools.Remember(sandbox.Home, session, Initialized, "Ran the migration dry-run against staging.", agent: "migration-worker");

        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "migration dry-run staging");

        Assert.Contains("session · migration-worker", result);
    }

    [Fact]
    public void Remember_UninitialisedHome_DoesNotPersist()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var session = new McpSessionId("session-a");

        var response = EngramMcpTools.Remember(sandbox.Home, session, new McpHomeState(false), "Statement one.");

        Assert.DoesNotContain("remembered:", response);
        Assert.False(File.Exists(sandbox.Home.DatabasePath));
    }

    /// <summary>
    /// The handle out of a tool response, so these assert on the id the model was actually
    /// handed rather than on one guessed from a counter that no longer exists.
    /// </summary>
    private static string HandleOf(string response)
    {
        var close = response.IndexOf(']', StringComparison.Ordinal);
        Assert.True(response.StartsWith('[') && close > 1, $"expected a bracketed handle, got: {response}");
        return response[1..close];
    }
}
