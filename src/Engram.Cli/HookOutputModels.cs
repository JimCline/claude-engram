using System.Text.Json.Serialization;

namespace Engram.Cli;

internal sealed record HookSpecificOutput(
    [property: JsonPropertyName("hookEventName")] string HookEventName,
    [property: JsonPropertyName("additionalContext")] string AdditionalContext);

// Carries additionalContext for both SessionStart and SubagentStart. The shape is the
// same; only hookEventName differs, and it must match the event that produced it.
internal sealed record AdditionalContextHookOutput(
    [property: JsonPropertyName("hookSpecificOutput")] HookSpecificOutput HookSpecificOutput);

// agent_id is the discriminator for "this is a subagent" — agent_type is not, since it
// is also set on a top-level `--agent` session. Note the key name differs by event:
// SubagentStart says agent_type where PreToolUse's tool_input says subagent_type.
internal sealed record HookStdinInput(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("agent_id")] string? AgentId = null,
    [property: JsonPropertyName("agent_type")] string? AgentType = null,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("tool_name")] string? ToolName = null,
    [property: JsonPropertyName("tool_input")] HookToolInput? ToolInput = null,
    // PostCompact carries the whole compaction summary inline, so the harvester (D62 2b)
    // never reads the transcript: no tail-read, no poll for a record a separate write path
    // produces, no race. See docs/session-capture-design.md, "The PostCompact trigger".
    [property: JsonPropertyName("compact_summary")] string? CompactSummary = null,
    // Common to every hook event per Claude Code's own docs, unlike promptSource/origin —
    // those live only on the transcript record this path is used to read. See
    // docs/session-capture-design.md, "The transcript".
    [property: JsonPropertyName("transcript_path")] string? TranscriptPath = null,
    // §6.13: the session-start path prefers this over Directory.GetCurrentDirectory() so the
    // enrollment lookup answers for the directory the session actually started in rather than
    // the hook process's own cwd. Absent on older payloads, which is why RunSessionStart falls
    // back rather than requiring it.
    [property: JsonPropertyName("cwd")] string? Cwd = null);

// PostToolUse puts the edited file here, for the four tools file-touched matches on. Only
// file_path is modelled: MultiEdit still names one file, and the rest of tool_input is that
// tool's own business. pattern and command are lookup-nudge's inputs — Grep and Glob name the
// query in pattern, Bash carries the whole command line and the pattern is dug out of it.
internal sealed record HookToolInput(
    [property: JsonPropertyName("file_path")] string? FilePath = null,
    [property: JsonPropertyName("pattern")] string? Pattern = null,
    [property: JsonPropertyName("command")] string? Command = null);

// PreToolUse's own shape — AdditionalContextHookOutput cannot be reused, since
// hookSpecificOutput.additionalContext is required there and a permission decision carries none.
internal sealed record PreToolUseHookSpecificOutput(
    [property: JsonPropertyName("hookEventName")] string HookEventName,
    [property: JsonPropertyName("permissionDecision")] string PermissionDecision,
    [property: JsonPropertyName("permissionDecisionReason")] string PermissionDecisionReason);

internal sealed record PreToolUseHookOutput(
    [property: JsonPropertyName("hookSpecificOutput")] PreToolUseHookSpecificOutput HookSpecificOutput);

[JsonSerializable(typeof(AdditionalContextHookOutput))]
[JsonSerializable(typeof(HookStdinInput))]
[JsonSerializable(typeof(PreToolUseHookOutput))]
internal sealed partial class HookJsonContext : JsonSerializerContext;
