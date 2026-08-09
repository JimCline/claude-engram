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
    [property: JsonPropertyName("tool_input")] HookToolInput? ToolInput = null);

// PostToolUse puts the edited file here, for the four tools file-touched matches on. Only
// file_path is modelled: MultiEdit still names one file, and the rest of tool_input is that
// tool's own business.
internal sealed record HookToolInput(
    [property: JsonPropertyName("file_path")] string? FilePath = null);

[JsonSerializable(typeof(AdditionalContextHookOutput))]
[JsonSerializable(typeof(HookStdinInput))]
internal sealed partial class HookJsonContext : JsonSerializerContext;
