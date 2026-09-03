using System.Text.Json;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The published binary agrees with the JIT build on the <c>tool-observed</c> hook (D73): the
/// short tool name and agent fields survive Native AOT's source-generated JSON both ways.
/// </summary>
public class HookToolObservedTests
{
    private static IReadOnlyList<JsonElement> Records(TestHome home, string kind)
    {
        var path = Path.Combine(home.Root, "telemetry.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(record => record.GetProperty("kind").GetString() == kind)
            .ToList();
    }

    [Fact]
    public void AnEngramToolCall_IsRecordedUnderTheHookSessionId()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            """{"session_id":"hook-session-1","agent_id":"agent-9","agent_type":"task-gopher","tool_name":"mcp__plugin_engram_engram__engram_remember","tool_input":{"statement":"x"}}""",
            "hook", "tool-observed");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);

        var record = Assert.Single(Records(home, "tool-observed"));
        Assert.Equal("hook-session-1", record.GetProperty("session_id").GetString());
        Assert.Equal("remember", record.GetProperty("tool").GetString());
        Assert.Equal("agent-9", record.GetProperty("agent_id").GetString());
        Assert.Equal("task-gopher", record.GetProperty("agent_type").GetString());
        Assert.False(record.TryGetProperty("fact_count", out var count) && count.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void AForeignTool_WritesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.RunWithStdin(
            home.Root, """{"session_id":"hook-session-2","tool_name":"Grep","tool_input":{"pattern":"x"}}""", "hook", "tool-observed");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(Records(home, "tool-observed"));
    }
}
