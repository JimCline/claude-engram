using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The <c>tool-observed</c> PostToolUse hook (D73): one telemetry record per Engram MCP tool call,
/// in the hook's session-id space, never touching the store.
/// </summary>
[Collection(ConsoleStdinCollection.Name)]
public class ToolObservedHookTests
{
    private const string Remember = "mcp__plugin_engram_engram__engram_remember";

    private static string BuildPayload(string? sessionId, string? toolName, string? agentId = null, string? agentType = null)
    {
        var root = new JsonObject { ["tool_input"] = new JsonObject { ["statement"] = "x" } };
        if (sessionId is not null)
        {
            root["session_id"] = sessionId;
        }

        if (toolName is not null)
        {
            root["tool_name"] = toolName;
        }

        if (agentId is not null)
        {
            root["agent_id"] = agentId;
        }

        if (agentType is not null)
        {
            root["agent_type"] = agentType;
        }

        return root.ToJsonString();
    }

    // Console.SetIn is process-global, not per-test — see ConsoleStdinCollection.
    private static (int ExitCode, string Stdout, string Stderr) Run(SandboxHome sandbox, string payload)
    {
        Console.SetIn(new StringReader(payload));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "tool-observed"], stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static IReadOnlyList<TelemetryRecord> ObservedRecords(SandboxHome sandbox)
    {
        var path = Telemetry.ResolvePath(sandbox.Home);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(Telemetry.TryParse)
            .Where(record => record is not null && record.Kind == TelemetryEventKind.ToolObserved)
            .Select(record => record!)
            .ToList();
    }

    /// <summary>Every file in the home except the telemetry log, by size and mtime — the doctor pattern.</summary>
    private static Dictionary<string, (long Length, DateTime Written)> Snapshot(SandboxHome sandbox)
    {
        var telemetry = Telemetry.ResolvePath(sandbox.Home);
        return Directory.EnumerateFiles(sandbox.Home.Root, "*", SearchOption.AllDirectories)
            .Where(file => file != telemetry)
            .ToDictionary(file => file, file => (new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)));
    }

    private static void AssertSilent((int ExitCode, string Stdout, string Stderr) result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public void EngramTool_WritesOneRecordWithTheShortName_AndTouchesNothingElse()
    {
        using var sandbox = new SandboxHome();
        Assert.True(File.Exists(sandbox.Home.DatabasePath));
        var before = Snapshot(sandbox);

        var result = Run(sandbox, BuildPayload("session-a", Remember, "agent-1", "ah:implementor"));

        AssertSilent(result);
        var record = Assert.Single(ObservedRecords(sandbox));
        Assert.Equal("session-a", record.SessionId);
        Assert.Equal("remember", record.Tool);
        Assert.Equal("agent-1", record.AgentId);
        Assert.Equal("ah:implementor", record.AgentType);
        Assert.Null(record.FactCount);
        Assert.Null(record.Phase);
        Assert.Equal(before, Snapshot(sandbox));
    }

    [Theory]
    [InlineData("mcp__plugin_engram_engram__engram_recall", "recall")]
    [InlineData("mcp__plugin_engram_engram__engram_navigate", "navigate")]
    public void EveryEngramTool_IsObserved_NotJustRemember(string toolName, string expected)
    {
        using var sandbox = new SandboxHome();

        AssertSilent(Run(sandbox, BuildPayload("session-b", toolName)));

        Assert.Equal(expected, Assert.Single(ObservedRecords(sandbox)).Tool);
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("mcp__other_server__engram_remember")]
    [InlineData("mcp__plugin_engram_engram__engram_")]
    public void ForeignOrEmptyToolName_WritesNothing(string toolName)
    {
        using var sandbox = new SandboxHome();

        AssertSilent(Run(sandbox, BuildPayload("session-c", toolName)));

        Assert.Empty(ObservedRecords(sandbox));
    }

    [Fact]
    public void NoToolName_WritesNothing()
    {
        using var sandbox = new SandboxHome();

        AssertSilent(Run(sandbox, BuildPayload("session-d", toolName: null)));

        Assert.Empty(ObservedRecords(sandbox));
    }

    /// <summary>
    /// No synthetic id: a record with a minted session id is exactly the unjoinable row D73 exists
    /// to remove.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingSessionId_WritesNothing(string? sessionId)
    {
        using var sandbox = new SandboxHome();

        AssertSilent(Run(sandbox, BuildPayload(sessionId, Remember)));

        Assert.Empty(ObservedRecords(sandbox));
    }

    [Fact]
    public void ToolObserved_IsADeclaredKind()
    {
        Assert.Contains(TelemetryEventKind.ToolObserved, TelemetryEventKind.All);
    }
}
