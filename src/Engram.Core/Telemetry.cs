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

    /// <summary>
    /// An <c>engram timeline</c> CLI call. A single completion event, no phases — an instant
    /// read rather than a background job, the same shape as <see cref="Recall"/>'s telemetry
    /// (docs/memory-expansion/05-browse-tui-spec.md).
    /// </summary>
    public const string Timeline = "timeline";

    /// <summary>An <c>engram_judge</c> call recording a verdict between two facts.</summary>
    public const string Judge = "judge";

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

    /// <summary>
    /// The PostCompact harvester (D62 2b) wrote at least one session fact from a compaction
    /// digest block.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than <see cref="Remember"/>, for the same reason <see cref="UserPrompt"/>
    /// is: D18 and D43 read <c>remember</c> to answer whether <i>the model</i> reached for memory,
    /// and this path writes facts the model never asked to store. Recorded only when something was
    /// actually written, matching <see cref="UserPrompt"/>'s rule that the event means a fact
    /// landed, not that the hook merely ran.
    /// </remarks>
    public const string PostCompact = "post-compact";

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

    /// <summary>
    /// The <c>memory-guard</c> PreToolUse hook denying a write to Claude Code's file-based
    /// auto-memory directory, once per session.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than <see cref="Remember"/> or <see cref="UserPrompt"/>: this is a
    /// nudge away from a write, not a fact captured, and folding it into either would inflate a
    /// D18/D43 adoption number in the direction that looks like success.
    /// </remarks>
    public const string MemoryGuard = "memory-guard";

    /// <summary>
    /// The <c>lookup-nudge</c> PreToolUse hook deferring a symbol-shaped Grep/Glob/shell search to
    /// <c>engram_navigate</c>, once per session, and only on a checkout the graph has indexed.
    /// Carries <c>phase</c> and <c>repo</c>; count by phase, not by line.
    /// </summary>
    /// <remarks>
    /// Its own kind for the same reason <see cref="MemoryGuard"/> is: a nudge toward a tool is not
    /// evidence the model reached for memory on its own, and D18/D43 read the memory kinds to
    /// answer exactly that. It also makes the classifier's false-positive rate readable after the
    /// fact — the one number that decides whether the matcher is too wide — which is otherwise
    /// unmeasurable, since a nudge that fired wrongly leaves no other trace.
    /// </remarks>
    public const string LookupNudge = "lookup-nudge";

    /// <summary>
    /// A call to one of Engram's own MCP tools, seen from the <c>PostToolUse</c> hook and so
    /// carrying the Claude Code session id rather than the transport's.
    /// </summary>
    /// <remarks>
    /// The server already writes a <c>remember</c>/<c>recall</c>/… record for the same call, keyed
    /// by <c>Mcp-Session-Id</c>; this is the other half of that pair, in the hook's id space, which
    /// is what D43 said nothing could provide. It is its own kind rather than folded into the tool's
    /// — counting both as <c>remember</c> would double the adoption numbers D18/D43 read. Subject is
    /// <see cref="TelemetryRecord.Tool"/>; no count field. Never opens the database.
    /// </remarks>
    public const string ToolObserved = "tool-observed";

    /// <summary>A run of <c>engram index</c>, which had recorded nothing at all until now.</summary>
    public const string Index = "index";

    /// <summary>The embedding backlog moving between idle and working.</summary>
    public const string Embedding = "embedding";

    /// <summary>
    /// A <c>repo enroll</c>/<c>decline</c>/<c>later</c>/<c>reset</c> decision, from either
    /// <c>engram repo</c> or <c>engram_index_repo</c>.
    /// </summary>
    /// <remarks>
    /// A kind declared but never emitted reads as a feature switched off (D56), so this exists
    /// only alongside <see cref="Engram.Cli.RepoCommand.ApplyDecision"/>, its one emission site —
    /// both the CLI verb group and the MCP tool call through it rather than emitting separately.
    /// </remarks>
    public const string Enrollment = "enrollment";

    /// <summary>
    /// A <c>sync export</c>/<c>sync import</c> run moving between started, finished, and failed
    /// (docs/memory-expansion/01-sync-spec.md).
    /// </summary>
    /// <remarks>
    /// A kind declared but never emitted reads as a feature switched off (D56), so this exists
    /// only alongside <see cref="Engram.Cli.SyncCommand"/>, its one emission site. Phases only —
    /// no counts inside the event (D55); counts live in <c>sync_chunk_state</c> and the CLI's own
    /// report.
    /// </remarks>
    public const string Sync = "sync";

    /// <summary>An <c>engram_navigate</c> call (spec §7.2) — the deterministic-lookup surface D71
    /// rests its D6-override justification on, so it must be instrumented from Phase 1.</summary>
    /// <remarks>
    /// Its own kind rather than <see cref="Recall"/>: navigation is not a relevance question,
    /// folding it in would inflate a D18/D43 recall-adoption number with calls recall never
    /// serviced.
    /// </remarks>
    public const string Navigate = "navigate";

    /// <summary>An <c>engram report</c> run (D22 §3.6) — a human-typed audit verb, never folded
    /// into <see cref="Recall"/> or <see cref="Remember"/>, whose adoption numbers it must not
    /// move.</summary>
    public const string Report = "report";

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
        Recall, Remember, Digest, Browse, Expand, Revise, Timeline, Judge,
        SessionStart, ServerStart, ServerStop, SessionOpen, SubagentStart, PreCompact, PostCompact,
        FileTouched, UserPrompt, MemoryGuard, LookupNudge, ToolObserved, Index, Embedding, Enrollment,
        Sync, Navigate, Report,
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
    /// Where a piece of work stands: <c>started</c>, <c>finished</c>, <c>failed</c> for kinds that
    /// have a duration; <c>nudged</c> for a <see cref="TelemetryEventKind.LookupNudge"/> deny, whose
    /// other end — <c>overridden</c> — is the same session re-issuing the same query. A reader counting
    /// <c>lookup-nudge</c> lines must therefore filter by phase, not kind alone. It is deliberately not a count — a nearby number in a field
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
    [property: JsonPropertyName("path")] string? Path = null,

    /// <summary>
    /// The repository an event is about. <c>index</c>, <c>enrollment</c> and <c>lookup-nudge</c>
    /// set it — the last so a nudge on an indexed checkout can be told from one that could never
    /// have been answered.
    /// </summary>
    /// <remarks>
    /// It is the event's subject, not a count, so D56's no-counts rule does not bar it — without
    /// it, <c>repo list</c> cannot show which run belongs to which repo. Carries
    /// <see cref="CodeIndexer.ResolveIdentity"/>'s identity, the same key <see cref="RepoEnrollment"/>
    /// and <c>repo list</c> already address a repo by — not the registry's shortened
    /// <c>repo_path</c>, which is a different, later-assigned name for the same repo.
    /// </remarks>
    [property: JsonPropertyName("repo")] string? Repo = null,

    /// <summary>
    /// The Engram tool a <see cref="TelemetryEventKind.ToolObserved"/> event saw called, by short
    /// name: <c>remember</c>, <c>recall</c>, <c>navigate</c>, … Only that kind sets it.
    /// </summary>
    [property: JsonPropertyName("tool")] string? Tool = null,

    /// <summary>
    /// The enrollment verb this event records: <c>enroll</c>, <c>decline</c>, <c>later</c>, or
    /// <c>reset</c>. Only <see cref="TelemetryEventKind.Enrollment"/> sets it.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Phase"/> — a nearby number in a field meaning something else is
    /// how D43 happened, and reusing that field for a different four-value vocabulary is the same
    /// trap. And deliberately not the three <c>repo_enrollment.state</c> values (<c>enrolled</c>,
    /// <c>declined</c>, <c>deferred</c>): this records the action taken, not the state that
    /// resulted, and <c>reset</c> produces no resulting state at all — it deletes the row.
    /// Harmonizing the two vocabularies would make <c>reset</c> unrepresentable, so do not.
    /// </remarks>
    [property: JsonPropertyName("decision")] string? Decision = null,

    /// <summary>
    /// How many directives a primer delivered. Only <see cref="TelemetryEventKind.SessionStart"/>
    /// and <see cref="TelemetryEventKind.SubagentStart"/> set it, and never alongside
    /// <see cref="FactCount"/> — a primer record's <see cref="FactCount"/> stays null (D46), and
    /// this is a distinct number for a distinct delivery channel (D-1), not a substitute for it.
    /// </summary>
    [property: JsonPropertyName("directive_count")] int? DirectiveCount = null,

    /// <summary>
    /// The tool profile this connection registered under (<c>default</c> or <c>full</c>). Only
    /// <see cref="TelemetryEventKind.SessionOpen"/> sets it, stamped from the same config read
    /// that selected the profile for the connection rather than a fresh read at record-write
    /// time — the two can disagree if config changes in between, and a stamp that disagrees with
    /// the live connection is worse than no stamp (docs/memory-expansion/03-tool-profiles-spec.md).
    /// </summary>
    [property: JsonPropertyName("tool_profile")] string? ToolProfile = null,

    /// <summary>
    /// The relation an <c>engram_navigate</c> call resolved: <c>defined_at</c>, <c>imports</c>,
    /// <c>callers</c>, <c>callees</c>, <c>neighbors</c>, or an unrecognized value verbatim. Only
    /// <see cref="TelemetryEventKind.Navigate"/> sets it.
    /// </summary>
    [property: JsonPropertyName("relation")] string? Relation = null,

    /// <summary>
    /// Whether an <c>engram_navigate</c> call had anything to answer with. Only
    /// <see cref="TelemetryEventKind.Navigate"/> sets it — this, not <see cref="FactCount"/>,
    /// is D71's adoption signal; <see cref="FactCount"/> means facts returned by a recall-shaped
    /// call and must stay null here (D46's rule about a nearby number in the wrong field).
    /// </summary>
    [property: JsonPropertyName("found")] bool? Found = null,

    /// <summary>
    /// Comma-separated match tiers among the rows an <c>engram_navigate</c> call returned (e.g.
    /// <c>"Exact"</c> or <c>"Exact,Substring"</c>), empty when nothing was found. Only
    /// <see cref="TelemetryEventKind.Navigate"/> sets it.
    /// </summary>
    [property: JsonPropertyName("tiers")] string? Tiers = null,

    /// <summary>
    /// Comma-separated extraction tiers among the rows an <c>engram_navigate</c> call returned
    /// (e.g. <c>"regex"</c> or <c>"regex,semantic"</c>), empty when nothing was found. This is a
    /// separate axis from <see cref="Tiers"/> (match confidence) and must never be folded into
    /// it — the D43 failure mode this guards against. Only <see cref="TelemetryEventKind.Navigate"/>
    /// sets it.
    /// </summary>
    [property: JsonPropertyName("extraction_tiers")] string? ExtractionTiers = null,

    /// <summary>
    /// How many facts an <c>engram report</c> run enumerated, total. Only
    /// <see cref="TelemetryEventKind.Report"/> sets this — never <see cref="FactCount"/>, which
    /// means facts returned to the model on a <c>recall</c> record and nothing on a primer; a
    /// nearby number in that field is exactly what D43 traced a wrong conclusion back to.
    /// </summary>
    [property: JsonPropertyName("report_total_facts")] int? ReportTotalFacts = null,

    /// <summary>How many of those facts were live. Only <see cref="TelemetryEventKind.Report"/> sets this.</summary>
    [property: JsonPropertyName("report_live_facts")] int? ReportLiveFacts = null,

    /// <summary>How many of those facts were closed. Only <see cref="TelemetryEventKind.Report"/> sets this.</summary>
    [property: JsonPropertyName("report_closed_facts")] int? ReportClosedFacts = null,

    /// <summary>
    /// How many regenerable facts <c>--authored-only</c> excluded; zero when the flag was not
    /// passed. Only <see cref="TelemetryEventKind.Report"/> sets this.
    /// </summary>
    [property: JsonPropertyName("report_excluded_facts")] int? ReportExcludedFacts = null,

    /// <summary>
    /// Size of the rendered document in bytes — the cheap signal of whether it is growing past
    /// usefulness. Only <see cref="TelemetryEventKind.Report"/> sets this.
    /// </summary>
    [property: JsonPropertyName("report_bytes_written")] int? ReportBytesWritten = null);

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
