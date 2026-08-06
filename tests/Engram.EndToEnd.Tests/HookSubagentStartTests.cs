using System.Text.Json;

namespace Engram.EndToEnd.Tests;

public class HookSubagentStartTests
{
    // Exit code proves nothing on this event. Bare stdout is silently discarded at
    // SubagentStart — a hook that printed the primer as plain text would exit 0, look
    // healthy, and deliver nothing, which is indistinguishable from a subagent choosing
    // to ignore it. The envelope is the contract, so the envelope is what is asserted.
    [Fact]
    public void SubagentStart_EmitsTheHookSpecificOutputEnvelope_NotBareStdout()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "subagent-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var envelope = JsonDocument.Parse(stdout).RootElement.GetProperty("hookSpecificOutput");

        // hookEventName must match the event that produced it, or the host drops it.
        Assert.Equal("SubagentStart", envelope.GetProperty("hookEventName").GetString());

        var context = envelope.GetProperty("additionalContext").GetString();
        Assert.False(string.IsNullOrWhiteSpace(context));
        Assert.Contains("engram_recall", context);
        Assert.Contains("engram_remember", context);
    }

    // Whether a subagent is handed its parent's session id or one of its own decides
    // whether D11's session memory is shared across the spawn for free or needs a parent
    // id threaded through. Recording what arrived is what lets the first real probe run
    // answer that instead of us guessing now.
    [Fact]
    public void SubagentStart_RecordsTheSessionAndAgentItWasHanded()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        const string payload =
            """{"session_id":"parent-session-123","agent_id":"agent-abc","agent_type":"plugin:task-gopher"}""";

        var (exitCode, _, stderr) = EngramProcess.RunWithStdin(home.Root, payload, "hook", "subagent-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var record = JsonDocument.Parse(
            File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl")).Single()).RootElement;

        Assert.Equal("subagent-start", record.GetProperty("kind").GetString());
        Assert.Equal("parent-session-123", record.GetProperty("session_id").GetString());
        Assert.Equal("agent-abc", record.GetProperty("agent_id").GetString());
        Assert.Equal("plugin:task-gopher", record.GetProperty("agent_type").GetString());
    }

    // This is the larger population by far — 336 spawns against 54 sessions on the instance that
    // prompted the change — so a subagent record that says nothing about memory is most of the
    // evidence there is. The agent fields must survive alongside the primer fields: they are set
    // separately from the shared record, which is exactly the kind of seam that drops one silently.
    [Fact]
    public void SubagentStart_RecordsThePrimerAndTheAgentTogether()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var result = EngramProcess.RunWithStdin(
            home.Root,
            """{"session_id":"parent-session-123","agent_id":"agent-abc","agent_type":"explorer"}""",
            "hook",
            "subagent-start");
        Assert.Equal(0, result.ExitCode);

        var record = JsonDocument.Parse(
            File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl")).Single()).RootElement;

        Assert.True(record.GetProperty("long_term_fact_count").GetInt32() > 0);
        Assert.InRange(record.GetProperty("tokens_returned").GetInt32(), 1, 300);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("fact_count").ValueKind);
        Assert.Equal("agent-abc", record.GetProperty("agent_id").GetString());
        Assert.Equal("explorer", record.GetProperty("agent_type").GetString());
    }
}
