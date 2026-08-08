using System.Text;
using System.Text.Json.Serialization;

namespace Engram.Core;

public static class TelemetryEventKind
{
    public const string Recall = "recall";
    public const string Remember = "remember";
    public const string Digest = "digest";
    public const string Browse = "browse";
    public const string Expand = "expand";
    public const string Revise = "revise";
    public const string SessionStart = "session-start";
    public const string ServerStart = "server-start";
    public const string SessionOpen = "session-open";

    public const string SubagentStart = "subagent-start";
    public const string PreCompact = "pre-compact";
    public const string FileTouched = "file-touched";

    /// <summary>A run of <c>engram index</c>, which had recorded nothing at all until now.</summary>
    public const string Index = "index";

    /// <summary>The embedding backlog moving between idle and working.</summary>
    public const string Embedding = "embedding";

    /// <summary>
    /// Every kind Engram emits.
    /// </summary>
    /// <remarks>
    /// Enumerated so a subscriber's <c>kinds</c> filter can be checked against something. A kind
    /// added above and not listed here is invisible to that check, which reports a real filter as
    /// a typo — the one way this list can be wrong, and the reason it sits beside the constants.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        Recall, Remember, Digest, Browse, Expand, Revise,
        SessionStart, ServerStart, SessionOpen, SubagentStart, PreCompact, FileTouched,
        Index, Embedding,
    ];
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
    [property: JsonPropertyName("agent_type")] string? AgentType = null,

    /// <summary>
    /// Where a piece of work stands: <c>started</c>, <c>finished</c>, <c>failed</c>. Only kinds
    /// that have a duration set it. It is deliberately not a count — a nearby number in a field
    /// meaning something else is how D43 happened — and detail belongs in the note the doing
    /// process already writes (D54), not duplicated into an append-only log.
    /// </summary>
    [property: JsonPropertyName("phase")] string? Phase = null);

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

    /// <summary>
    /// Reads back one line of the log, or null if it is not a record.
    /// </summary>
    /// <remarks>
    /// Public so a reader of the log — the webhook tail, and whatever reads history later — parses
    /// it through the same source-generated context that wrote it, rather than growing a second
    /// idea of the shape. Malformed is null rather than an exception: a line caught mid-rotation,
    /// or written by an older build, must cost that line and not the read.
    /// </remarks>
    public static TelemetryRecord? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(
                line, TelemetryJsonContext.Default.TelemetryRecord);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
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
