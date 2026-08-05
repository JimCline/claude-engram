using System.Text;
using System.Text.Json.Serialization;

namespace Engram.Core;

public static class TelemetryEventKind
{
    public const string Recall = "recall";
    public const string Remember = "remember";
    public const string Digest = "digest";
    public const string SessionStart = "session-start";
    public const string ServerStart = "server-start";
    public const string SessionOpen = "session-open";

    public const string SubagentStart = "subagent-start";
    public const string PreCompact = "pre-compact";
    public const string FileTouched = "file-touched";
}

public sealed record TelemetryRecord(
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("query")] string? Query = null,
    [property: JsonPropertyName("fact_count")] int? FactCount = null,
    [property: JsonPropertyName("tokens_returned")] int? TokensReturned = null,
    [property: JsonPropertyName("coverage")] string? Coverage = null,
    [property: JsonPropertyName("session_fact_count")] int? SessionFactCount = null,
    [property: JsonPropertyName("long_term_fact_count")] int? LongTermFactCount = null,
    [property: JsonPropertyName("prior_session_fact_count")] int? PriorSessionFactCount = null,
    [property: JsonPropertyName("agent_id")] string? AgentId = null,
    [property: JsonPropertyName("agent_type")] string? AgentType = null);

[JsonSerializable(typeof(TelemetryRecord))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;

public static class Telemetry
{
    private const string TelemetryFileName = "telemetry.jsonl";

    private const int MaxRecordBytes = 4096;
    private static readonly TimeSpan AppendRetryBudget = TimeSpan.FromMilliseconds(500);
    private static readonly object AppendLock = new();

    public static string ResolvePath(EngramHome home) => Path.Combine(home.Root, TelemetryFileName);

    public static void Append(EngramHome home, TelemetryRecord record)
    {
        var payload = BuildPayload(record);
        var path = ResolvePath(home);

        lock (AppendLock)
        {
            DurableAppend.TryAppend(path, payload, AppendRetryBudget);
        }
    }

    private static byte[] BuildPayload(TelemetryRecord record)
    {
        var payload = SerializeLine(record);
        if (payload.Length < MaxRecordBytes || string.IsNullOrEmpty(record.Query))
        {
            return payload;
        }

        var low = 0;
        var high = record.Query.Length;
        var best = SerializeLine(record with { Query = string.Empty });

        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = SerializeLine(record with { Query = record.Query[..mid] });
            if (candidate.Length < MaxRecordBytes)
            {
                best = candidate;
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static byte[] SerializeLine(TelemetryRecord record)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(record, TelemetryJsonContext.Default.TelemetryRecord);
        return Encoding.UTF8.GetBytes(json + "\n");
    }
}
