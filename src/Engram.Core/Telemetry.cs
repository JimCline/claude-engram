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

    /// <summary>
    /// The server process came up, and its counterpart when it goes down cleanly.
    /// </summary>
    /// <remarks>
    /// <para>Lifecycle, never a session count. D14 retired an earlier <c>server-start</c> record
    /// precisely because one-per-process only meant "a session" back when the transport was stdio;
    /// a daemon mints many sessions over one lifetime, and <see cref="SessionOpen"/> is what counts
    /// them. Nothing may read these two to answer a D18 or D43 adoption question.</para>
    /// <para><c>server-stop</c> is best effort twice over, and its absence proves nothing. A
    /// process killed outright never reaches <c>ApplicationStopping</c> at all; and even on a
    /// clean exit the only thing that delivers events is the webhook service inside that same
    /// process, which is shutting down beside it — the record always reaches the log, but a
    /// subscriber may not get it. So a reader may not infer "still up" from having seen no stop.
    /// Liveness is pid plus start token, which <c>engram status</c> answers (D42). This log says
    /// what happened, not what is.</para>
    /// </remarks>
    public const string ServerStart = "server-start";

    public const string ServerStop = "server-stop";
    public const string SessionOpen = "session-open";

    public const string SubagentStart = "subagent-start";
    public const string PreCompact = "pre-compact";
    public const string FileTouched = "file-touched";

    /// <summary>
    /// The <c>user-prompt</c> hook capturing a statement the user made in passing.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than <see cref="Remember"/>, though both end in a written fact. D18 and
    /// D43 read <c>remember</c> to answer whether <i>the model</i> reached for memory, and this
    /// path fires whether it did or not — folding them together would inflate the one number those
    /// gates turn on, in the direction that looks like success.
    /// </remarks>
    public const string UserPrompt = "user-prompt";

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
        SessionStart, ServerStart, ServerStop, SessionOpen, SubagentStart, PreCompact, FileTouched,
        UserPrompt, Index, Embedding,
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
    [property: JsonPropertyName("phase")] string? Phase = null,

    /// <summary>
    /// The file an event is about. Only <c>file-touched</c> sets it today.
    /// </summary>
    /// <remarks>
    /// Its own field rather than borrowed space in <c>query</c>, which means the text someone
    /// searched for. It carries the path for the same reason the spool entry does: a queue of bare
    /// timestamps answers one bit no matter how long it gets, and a feed of bare edit pings is that
    /// same queue rendered on a screen.
    /// </remarks>
    [property: JsonPropertyName("path")] string? Path = null);

[JsonSerializable(typeof(TelemetryRecord))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;

public static class Telemetry
{
    private const string TelemetryFileName = "telemetry.jsonl";

    private const int MaxRecordBytes = 4096;
    private static readonly TimeSpan AppendRetryBudget = TimeSpan.FromMilliseconds(500);
    private static readonly object AppendLock = new();

    public static string ResolvePath(EngramHome home) => Path.Combine(home.Root, TelemetryFileName);

    public static void Append(EngramHome home, TelemetryRecord record) =>
        Append(home, record, AppendRetryBudget);

    /// <summary>
    /// Appends within a stated retry budget, dropping the record rather than exceeding it.
    /// </summary>
    /// <remarks>
    /// <para>Exists for <c>file-touched</c>, which holds a hard 10 ms budget (D4) against a default
    /// budget of 500 ms — fifty times the hook's whole allowance, spent waiting on a shared file
    /// that every concurrent edit is also trying to open.</para>
    /// <para><see cref="TimeSpan.Zero"/> is the meaningful value there, and it does not mean "retry
    /// briefly": the loop's guard is <c>elapsed &lt; retryBudget</c>, evaluated <i>before</i> the
    /// back-off sleep, so zero takes exactly one attempt and returns. Any small non-zero budget
    /// would be worse than either extreme — one collision costs a sleep of up to 20 ms, twice the
    /// budget it was picked to protect.</para>
    /// <para>Measured over ten rounds each, because the obvious objection is that dropping loses
    /// exactly the events that matter most — the ones from a burst. It costs <b>2.0% at twenty
    /// concurrent editors and 1.6% at fifty</b> (worst round 18 of 20), and in every round
    /// <b>zero torn lines and zero lost spool entries</b>. That is the trade stated precisely: a
    /// status line occasionally one frame short, never a half-written record, never a missing edit
    /// in the queue the indexer actually reads. A single round of this returned 50 of 50, which is
    /// why the rate is quoted from ten and not from one.</para>
    /// </remarks>
    public static void Append(EngramHome home, TelemetryRecord record, TimeSpan retryBudget)
    {
        var payload = BuildPayload(record);
        var path = ResolvePath(home);

        lock (AppendLock)
        {
            DurableAppend.TryAppend(path, payload, retryBudget);
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
