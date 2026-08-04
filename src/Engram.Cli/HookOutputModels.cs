using System.Text.Json.Serialization;

namespace Engram.Cli;

internal sealed record HookSpecificOutput(
    [property: JsonPropertyName("hookEventName")] string HookEventName,
    [property: JsonPropertyName("additionalContext")] string AdditionalContext);

internal sealed record SessionStartHookOutput(
    [property: JsonPropertyName("hookSpecificOutput")] HookSpecificOutput HookSpecificOutput);

internal sealed record PreCompactHookOutput(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("reason")] string Reason);

[JsonSerializable(typeof(SessionStartHookOutput))]
[JsonSerializable(typeof(PreCompactHookOutput))]
internal sealed partial class HookJsonContext : JsonSerializerContext;
