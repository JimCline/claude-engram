using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class LookupNudgeHookTests
{
    private static string BuildPayload(string? sessionId, string? toolName, string? pattern, string? command)
    {
        var toolInput = new JsonObject();
        if (pattern is not null)
        {
            toolInput["pattern"] = pattern;
        }

        if (command is not null)
        {
            toolInput["command"] = command;
        }

        var root = new JsonObject { ["tool_input"] = toolInput };
        if (toolName is not null)
        {
            root["tool_name"] = toolName;
        }

        if (sessionId is not null)
        {
            root["session_id"] = sessionId;
        }

        return root.ToJsonString();
    }

    // Console.SetIn is process-global, not per-test — safe here only because MemoryGuardHookTests
    // is the only other tier-2 consumer of CliApp stdin and xunit does not interleave their reads.
    private static (int ExitCode, string Stdout, string Stderr) Run(
        SandboxHome sandbox, string? sessionId, string? toolName, string? pattern = null, string? command = null)
    {
        Console.SetIn(new StringReader(BuildPayload(sessionId, toolName, pattern, command)));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "lookup-nudge"], stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static IReadOnlyList<TelemetryRecord> LookupNudgeTelemetryRecords(SandboxHome sandbox)
    {
        var path = Telemetry.ResolvePath(sandbox.Home);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(Telemetry.TryParse)
            .Where(record => record is not null && record.Kind == TelemetryEventKind.LookupNudge)
            .Select(record => record!)
            .ToList();
    }

    [Fact]
    public void SymbolShapedGrep_Denies()
    {
        using var sandbox = new SandboxHome();

        var (exitCode, stdout, stderr) = Run(sandbox, "session-a", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var doc = JsonDocument.Parse(stdout);
        var hookSpecificOutput = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", hookSpecificOutput.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", hookSpecificOutput.GetProperty("permissionDecision").GetString());

        var reason = hookSpecificOutput.GetProperty("permissionDecisionReason").GetString();
        Assert.Contains("engram_navigate", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainWordGrep_EmptyStdoutAndNoState()
    {
        using var sandbox = new SandboxHome();

        var (exitCode, stdout, stderr) = Run(sandbox, "session-b", "Grep", pattern: "latency");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.False(File.Exists(sandbox.Home.LookupNudgeStatePath));
    }

    [Fact]
    public void ShellGrep_Denies()
    {
        using var sandbox = new SandboxHome();

        var (exitCode, stdout, stderr) = Run(sandbox, "session-c", "Bash", command: "grep -rn ProcessFile src/");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            "deny",
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void NonSearchBash_EmptyStdout()
    {
        using var sandbox = new SandboxHome();

        var (exitCode, stdout, stderr) = Run(sandbox, "session-d", "Bash", command: "dotnet test");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void SecondCallSameSession_EmptyStdout()
    {
        using var sandbox = new SandboxHome();

        // The first call must be asserted to DENY, not merely run. Asserting only that the second
        // is silent passes just as well when the hook never fired at all, which is what a broken
        // classifier looks like — the once-per-session rule would then be untested by the one test
        // named after it.
        var first = Run(sandbox, "session-e", "Grep", pattern: "ProcessFile");
        using (var doc = JsonDocument.Parse(first.Stdout))
        {
            Assert.Equal(
                "deny",
                doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
        }

        var (exitCode, stdout, stderr) = Run(sandbox, "session-e", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void DifferentSession_DeniesAgain()
    {
        using var sandbox = new SandboxHome();

        Run(sandbox, "session-f1", "Grep", pattern: "ProcessFile");
        var (exitCode, stdout, _) = Run(sandbox, "session-f2", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            "deny",
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void PrecedenceOff_EmptyStdout()
    {
        using var sandbox = new SandboxHome();
        File.AppendAllText(sandbox.Home.ConfigPath, "\n[memory]\nprecedence = \"off\"\n");

        var (exitCode, stdout, stderr) = Run(sandbox, "session-g", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void Deny_WritesLookupNudgeTelemetryRecord()
    {
        using var sandbox = new SandboxHome();

        Run(sandbox, "session-h", "Grep", pattern: "ProcessFile");

        var records = LookupNudgeTelemetryRecords(sandbox);
        Assert.Single(records);
        Assert.Equal("session-h", records[0].SessionId);
    }
}
