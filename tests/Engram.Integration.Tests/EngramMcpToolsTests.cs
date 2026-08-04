using Engram.Cli;

namespace Engram.Integration.Tests;

public class EngramMcpToolsTests
{
    [Fact]
    public void Remember_ThenRecallSameSession_ReturnsTheFactWithASessionHandle()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        EngramMcpTools.Remember(sandbox.Home, session, "The build pipeline retries flaky uploads three times before failing.");

        var result = EngramMcpTools.Recall(sandbox.Home, session, "flaky uploads retries");

        Assert.Contains("[s001]", result);
        Assert.Contains("flaky uploads", result);
    }

    [Fact]
    public void Remember_ThenRecallDifferentSession_ReturnsThePriorSessionFactWithAQualifiedHandle()
    {
        using var sandbox = new SandboxHome();
        var writer = new McpSessionId("session-a");
        var reader = new McpSessionId("session-b");

        EngramMcpTools.Remember(sandbox.Home, writer, "The build pipeline retries flaky uploads three times before failing.");

        var result = EngramMcpTools.Recall(sandbox.Home, reader, "flaky uploads retries");

        Assert.DoesNotContain("[s001]", result);
        Assert.Contains("[s001@p1]", result);
        Assert.Contains("flaky uploads", result);
        Assert.DoesNotContain("coverage: none", result);
    }

    [Fact]
    public void Recall_PriorAndCurrentSessionShareTheSameHandleNumber_HandlesAreDistinguishableInOutput()
    {
        using var sandbox = new SandboxHome();
        var priorSession = new McpSessionId("session-old");
        var currentSession = new McpSessionId("session-new");

        EngramMcpTools.Remember(sandbox.Home, priorSession, "The nightly backup job runs at 2am UTC.");
        EngramMcpTools.Remember(sandbox.Home, currentSession, "The nightly backup job now also verifies checksums.");

        var result = EngramMcpTools.Recall(sandbox.Home, currentSession, "nightly backup job");

        var currentHandleIndex = result.IndexOf("[s001] ", StringComparison.Ordinal);
        var priorHandleIndex = result.IndexOf("[s001@p1]", StringComparison.Ordinal);

        Assert.True(currentHandleIndex >= 0, "current-session handle [s001] should be present");
        Assert.True(priorHandleIndex >= 0, "prior-session handle [s001@p1] should be present");
        Assert.True(currentHandleIndex < priorHandleIndex, "current-session fact must rank above the prior-session fact");
    }

    [Fact]
    public void Recall_MalformedSessionFileInSessionsDirectory_IsSkippedAndRecallStillSucceeds()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-current");

        var sessionsDir = Path.Combine(sandbox.Home.Root, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(Path.Combine(sessionsDir, "session-corrupt.jsonl"), "{not valid json at all\n");

        var result = EngramMcpTools.Recall(sandbox.Home, session, "anything at all");

        Assert.Contains("RECALL", result);
    }

    [Fact]
    public void Remember_ReturnsRealHandleInResponseText()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var response = EngramMcpTools.Remember(sandbox.Home, session, "Statement one.");

        Assert.Contains("[s001]", response);
    }

    [Fact]
    public void Remember_WithAgentName_AttributesTheNoteToThatAgent()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        EngramMcpTools.Remember(sandbox.Home, session, "Ran the migration dry-run against staging.", agent: "migration-worker");

        var result = EngramMcpTools.Recall(sandbox.Home, session, "migration dry-run staging");

        Assert.Contains("session · migration-worker", result);
    }
}
