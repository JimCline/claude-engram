using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// memory-guard is a PreToolUse hook, so its payload arrives on stdin exactly like
/// file-touched's and user-prompt's — these drive the published binary for the same reason
/// those do: Console.IsInputRedirected only reads true against a real spawned process.
/// </summary>
public class MemoryGuardE2ETests
{
    [Fact]
    public void MemoryGuard_FirstMemoryWrite_DeniesWithReason()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var path = MatchingFilePath();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-memory-guard-1", path), "hook", "memory-guard");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var hookOutput = JsonNode.Parse(stdout)!["hookSpecificOutput"]!;
        Assert.Equal("deny", hookOutput["permissionDecision"]!.GetValue<string>());

        var reason = hookOutput["permissionDecisionReason"]!.GetValue<string>();
        Assert.Contains(path, reason, StringComparison.Ordinal);
        Assert.Contains("engram_remember", reason, StringComparison.Ordinal);
    }

    // Load-bearing: pins the re-run contract. Falsified by removing the state append inside
    // RunMemoryGuard — every call then denies, which reds this test; restored, green.
    [Fact]
    public void MemoryGuard_SecondWriteSameSession_Allows()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var path = MatchingFilePath();

        EngramProcess.RunWithStdin(home.Root, Payload("e2e-memory-guard-2", path), "hook", "memory-guard");
        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-memory-guard-2", path), "hook", "memory-guard");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void MemoryGuard_IndexFile_IsExempt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var path = MatchingFilePath(fileName: "MEMORY.md");

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-memory-guard-3", path), "hook", "memory-guard");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void MemoryGuard_PrecedenceOff_Disarms()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        File.AppendAllText(Path.Combine(home.Root, "config.toml"), "\n[memory]\nprecedence = \"off\"\n");
        var path = MatchingFilePath();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-memory-guard-4", path), "hook", "memory-guard");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    private static string MatchingFilePath(string projectSlug = "e2e-project", string fileName = "note.md") =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects", projectSlug, "memory", fileName);

    private static string Payload(string? sessionId, string? filePath)
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

        return JsonSerializer.Serialize(root);
    }
}
