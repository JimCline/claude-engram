using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class MemoryGuardHookTests
{
    private static string BuildPayload(string? sessionId, string? filePath)
    {
        var toolInput = new JsonObject();
        if (filePath is not null)
        {
            toolInput["file_path"] = filePath;
        }

        var root = new JsonObject { ["tool_input"] = toolInput };
        if (sessionId is not null)
        {
            root["session_id"] = sessionId;
        }

        return root.ToJsonString();
    }

    // Console.SetIn is process-global, not per-test — safe here only because this class is
    // currently the only tier-2 consumer of CliApp stdin. xunit runs different test classes on
    // parallel threads by default, so a second stdin-reading test class added later would race
    // this one silently rather than fail loudly.
    private static (int ExitCode, string Stdout, string Stderr) Run(SandboxHome sandbox, string? sessionId, string? filePath)
    {
        Console.SetIn(new StringReader(BuildPayload(sessionId, filePath)));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "memory-guard"], stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string MatchingPath(SandboxHome sandbox, string projectSlug = "my-project", string fileName = "note.md") =>
        Path.Combine(sandbox.Home.ClaudeProjectsDir, projectSlug, "memory", fileName);

    private static IReadOnlyList<TelemetryRecord> MemoryGuardTelemetryRecords(SandboxHome sandbox)
    {
        var path = Telemetry.ResolvePath(sandbox.Home);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(Telemetry.TryParse)
            .Where(record => record is not null && record.Kind == TelemetryEventKind.MemoryGuard)
            .Select(record => record!)
            .ToList();
    }

    [Fact]
    public void FirstMemoryWrite_DeniesWithReason()
    {
        using var sandbox = new SandboxHome();
        var path = MatchingPath(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, "session-a", path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var doc = JsonDocument.Parse(stdout);
        var hookSpecificOutput = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", hookSpecificOutput.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", hookSpecificOutput.GetProperty("permissionDecision").GetString());

        var reason = hookSpecificOutput.GetProperty("permissionDecisionReason").GetString();
        Assert.Contains(path, reason, StringComparison.Ordinal);
        Assert.Contains("engram_remember", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondWriteSameSession_Allows()
    {
        using var sandbox = new SandboxHome();
        var path = MatchingPath(sandbox);

        Run(sandbox, "session-b", path);
        var (exitCode, stdout, stderr) = Run(sandbox, "session-b", path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void DifferentSessionId_DeniesAgain()
    {
        using var sandbox = new SandboxHome();
        var path = MatchingPath(sandbox);

        Run(sandbox, "session-c1", path);
        var (exitCode, stdout, _) = Run(sandbox, "session-c2", path);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            "deny",
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void PrecedenceOff_EmptyStdoutOnFreshSession()
    {
        using var sandbox = new SandboxHome();
        File.AppendAllText(sandbox.Home.ConfigPath, "\n[memory]\nprecedence = \"off\"\n");
        var path = MatchingPath(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, "session-d", path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void UninitializedHome_EmptyStdout()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var path = MatchingPath(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, "session-e", path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void MissingSessionId_EmptyStdout()
    {
        using var sandbox = new SandboxHome();
        var path = MatchingPath(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, sessionId: null, path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void MissingFilePath_EmptyStdout()
    {
        using var sandbox = new SandboxHome();

        var (exitCode, stdout, stderr) = Run(sandbox, "session-f", filePath: null);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void Telemetry_ExactlyOneRecordAfterDenyThenAllow()
    {
        using var sandbox = new SandboxHome();
        var path = MatchingPath(sandbox);

        Run(sandbox, "session-g", path);
        Run(sandbox, "session-g", path);

        var records = MemoryGuardTelemetryRecords(sandbox);
        Assert.Single(records);
        Assert.Equal("session-g", records[0].SessionId);
    }

    // Amendment 1's load-bearing falsification for Step 1: nothing on the non-matching path may
    // touch the state file or telemetry before the path-match check runs.
    [Fact]
    public void NonMatchingPath_LeavesNoTrace()
    {
        using var sandbox = new SandboxHome();
        var nonMatchingPath = Path.Combine(sandbox.Home.ClaudeProjectsDir, "my-project", "notes.md");

        var (exitCode, stdout, stderr) = Run(sandbox, "session-h", nonMatchingPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.False(File.Exists(sandbox.Home.MemoryGuardStatePath));
        Assert.Empty(MemoryGuardTelemetryRecords(sandbox));
    }
}
