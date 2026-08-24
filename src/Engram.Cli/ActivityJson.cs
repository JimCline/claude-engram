using System.Text.Json.Serialization;

namespace Engram.Cli;

internal sealed record ActivityJson(
    string Home,
    string? LastKind,
    DateTimeOffset? LastAt,
    long? LastAgeSeconds,
    int? WindowSeconds,
    int WindowCount,
    IReadOnlyList<ActivityKindCount> Kinds,
    int SkippedLines);

internal sealed record ActivityKindCount(string Kind, int Count);

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivityJson))]
internal sealed partial class ActivityJsonContext : JsonSerializerContext;
