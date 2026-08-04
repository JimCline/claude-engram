using System.ComponentModel;
using Engram.Core;
using ModelContextProtocol.Server;

namespace Engram.Cli;

public sealed record McpSessionId(string Value);

[McpServerToolType]
public sealed class EngramMcpTools
{
    [McpServerTool(Name = "engram_recall")]
    [Description(
        "Check memory BEFORE reading files or exploring the repo. Searches Engram's stored facts " +
        "(decisions, conventions, gotchas, contracts) and returns a token-budgeted, ranked digest with " +
        "fact handles and a coverage estimate. Call this first for any question about how this project " +
        "works, what was decided, or why something is the way it is — it is far cheaper than rediscovering " +
        "the answer by reading source.")]
    public static string Recall(
        EngramHome home,
        McpSessionId session,
        [Description("What you want to know, as a few keywords or a short question.")] string query,
        [Description("Maximum tokens to spend on the response. Defaults to 500.")] int? budget_tokens = null)
    {
        var budget = budget_tokens is > 0 ? budget_tokens.Value : RecallEngine.DefaultBudgetTokens;
        var currentSessionFacts = SessionFactStore.ReadAll(home, session.Value);
        var priorSessionFacts = SessionFactStore.ReadAllExcept(home, session.Value);
        var result = RecallEngine.Pack(query, CannedFacts.All, currentSessionFacts, priorSessionFacts, budget);

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Recall,
            Query: query,
            FactCount: result.FactCount,
            TokensReturned: result.TokensUsed,
            Coverage: RecallEngine.ToText(result.Coverage),
            SessionFactCount: result.SessionFactCount,
            LongTermFactCount: result.LongTermFactCount,
            PriorSessionFactCount: result.PriorSessionFactCount));

        return result.Text;
    }

    [McpServerTool(Name = "engram_remember")]
    [Description(
        "Save a durable note to this session's working memory — state you would otherwise have to keep " +
        "repeating in context, and would lose to compaction or to a subagent returning an incomplete " +
        "report. Call it whenever you learn something worth keeping for the rest of this session: a " +
        "decision, a constraint, a partial result, a dead end already ruled out. If you are a subagent, " +
        "pass your own name in `agent` so the note is attributed to whichever worker learned it. " +
        "`engram_recall` surfaces these notes first, ranked above long-term memory, for the rest of the " +
        "session.")]
    public static string Remember(
        EngramHome home,
        McpSessionId session,
        [Description("The fact to remember, as a short, self-contained statement.")] string statement,
        [Description("What or who the fact is about, if not obvious from the statement.")] string? subject = null,
        [Description("Where this was learned — a file path, PR, or command output.")] string? evidence = null,
        [Description("Your own name, if you are a subagent recording this fact rather than the main agent.")] string? agent = null)
    {
        var handle = SessionFactStore.Append(home, session.Value, statement, subject, evidence, agent);

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Remember));

        var subjectText = string.IsNullOrWhiteSpace(subject) ? "(unspecified subject)" : subject;
        var evidenceText = string.IsNullOrWhiteSpace(evidence) ? string.Empty : $" (evidence: {evidence})";
        var agentText = string.IsNullOrWhiteSpace(agent) ? string.Empty : $" [via {agent}]";

        return $"[{handle}] remembered: {subjectText} — \"{statement}\"{evidenceText}{agentText}";
    }

    [McpServerTool(Name = "engram_digest")]
    [Description(
        "Flush the durable learnings from this session in one call, before context is compacted or the " +
        "session ends. Accepts up to 25 short learnings plus an optional summary. This milestone does not " +
        "persist anything yet; it only confirms receipt to measure whether digest fires unprompted.")]
    public static string Digest(
        EngramHome home,
        McpSessionId session,
        [Description("Up to 25 short, self-contained facts learned this session.")] string[] learnings,
        [Description("A one- or two-sentence summary of the session.")] string? session_summary = null)
    {
        var total = learnings.Length;
        var counted = Math.Min(total, 25);
        var overflow = total - counted;

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Digest));

        var summaryNote = string.IsNullOrWhiteSpace(session_summary) ? string.Empty : $" Summary noted: \"{session_summary}\"";
        var overflowNote = overflow > 0 ? $" ({overflow} additional learning(s) beyond the 25-cap were ignored.)" : string.Empty;

        return $"Digest received: {counted} learning(s) recorded.{summaryNote}{overflowNote} " +
               "Not persisted in this milestone (no database yet) — this call only measures whether digest fires at session end.";
    }
}
