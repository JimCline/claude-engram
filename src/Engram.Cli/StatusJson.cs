using System.Text.Json.Serialization;

namespace Engram.Cli;

internal sealed record StatusJson(
    string Home,
    bool Initialised,
    string Server,
    int? Pid = null,
    int? Port = null,
    string? Version = null,
    DateTimeOffset? StartTimeUtc = null,
    long? UptimeSeconds = null,
    string? StartedFrom = null,
    string? ThisBinary = null);

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusJson))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;
