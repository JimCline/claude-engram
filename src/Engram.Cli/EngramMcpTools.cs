using System.ComponentModel;
using Engram.Core;
using ModelContextProtocol.Server;

namespace Engram.Cli;

public sealed record McpSessionId(string Value);

public sealed record McpHomeState(bool Initialized);

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
        McpHomeState homeState,
        [Description("What you want to know, as a few keywords or a short question.")] string query,
        [Description("Maximum tokens to spend on the response. Defaults to 500.")] int? budget_tokens = null)
    {
        var budget = budget_tokens is > 0 ? budget_tokens.Value : RecallEngine.DefaultBudgetTokens;

        // One connection, one temporal model. Recall used to assemble its idea of memory at
        // this call site from three stores — a JSON directory, a JSONL file per session, and
        // the database — which is three chances for them to disagree about what is still
        // believed. Every tier below is now a partition of one live-fact read.
        var now = DateTimeOffset.UtcNow;
        using var connection = EngramDatabase.OpenInitialized(home);

        var longTermFacts = FactCatalog.ReadLongTerm(connection, now);
        var (currentSessionFacts, priorSessionFacts) = SessionFacts.Read(connection, session.Value, now);

        var result = RecallEngine.Pack(query, longTermFacts, currentSessionFacts, priorSessionFacts, budget);

        if (homeState.Initialized)
        {
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
        }

        return result.Text;
    }

    [McpServerTool(Name = "engram_remember")]
    [Description(
        "Save a durable note to this session's working memory — state you would otherwise have to keep " +
        "repeating in context, and would lose to compaction or to a subagent returning an incomplete " +
        "report. Call it whenever you learn something worth keeping for the rest of this session: a " +
        "decision, a constraint, a partial result, a dead end already ruled out. If you are a subagent, " +
        "pass your own name in `agent` so the note is attributed to whichever worker learned it. " +
        "`engram_recall` ranks these above long-term memory for the rest of this session, and they stay " +
        "recallable in later sessions too — so this is worth calling for anything that would still be true " +
        "next week, not only for what you need in the next ten minutes.")]
    public static string Remember(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The fact to remember, as a short, self-contained statement.")] string statement,
        [Description("What or who the fact is about, if not obvious from the statement.")] string? subject = null,
        [Description("Where this was learned — a file path, PR, or command output.")] string? evidence = null,
        [Description("Your own name, if you are a subagent recording this fact rather than the main agent.")] string? agent = null,
        [Description(
            "The bracketed id of a raw user-statement capture this restates, e.g. \"f42\". Closes that "
                + "capture so the rewritten version replaces it instead of duplicating it.")] string? supersedes = null)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); this statement was not saved.";
        }

        // A restatement of something the user said replaces the capture in place — same
        // subject, same predicate — so the store's own collision rule closes the original
        // and records why. Writing it to this session's notes instead would leave the
        // rewritten version as the one that expires and the raw one as the one that lasts,
        // which is backwards.
        if (!string.IsNullOrWhiteSpace(supersedes))
        {
            if (!FactCatalog.TryParseHandle(supersedes, out var targetId))
            {
                return $"'{supersedes}' is not a fact handle; they look like 'f42'. Nothing was saved.";
            }

            using var connection = EngramDatabase.OpenInitialized(home);
            var replacementId = UserFacts.Restate(connection, targetId, statement, session.Value, DateTimeOffset.UtcNow);

            if (replacementId is null)
            {
                return $"No live fact with id '{supersedes}' to restate — it may already have been "
                    + "superseded or forgotten. Nothing was saved; call engram_recall to see what stands.";
            }

            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: session.Value,
                Kind: TelemetryEventKind.Remember));

            return $"[{FactCatalog.HandleFor(replacementId.Value)}] replaced capture [{supersedes}]: \"{statement}\"";
        }

        long factId;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            factId = SessionFacts.Append(
                connection, session.Value, statement, subject, evidence, agent, DateTimeOffset.UtcNow);
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Remember));

        var subjectText = string.IsNullOrWhiteSpace(subject) ? "(unspecified subject)" : subject;
        var evidenceText = string.IsNullOrWhiteSpace(evidence) ? string.Empty : $" (evidence: {evidence})";
        var agentText = string.IsNullOrWhiteSpace(agent) ? string.Empty : $" [via {agent}]";

        return $"[{FactCatalog.HandleFor(factId)}] remembered: {subjectText} — \"{statement}\"{evidenceText}{agentText}";
    }

    [McpServerTool(Name = "engram_forget")]
    [Description(
        "Retract a stored fact by its bracketed id (e.g. \"f42\"). Use it whenever the user says something "
            + "stored is wrong, private, or no longer true, and to clear a session note that turned out to be "
            + "mistaken. Retracted facts stop appearing in recall immediately, and stay retracted — nothing "
            + "re-seeds them. Call engram_recall first if you need to find the id.")]
    public static string Forget(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The bracketed id of the fact to retract, e.g. \"f42\".")] string id)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); nothing was retracted.";
        }

        if (!FactCatalog.TryParseHandle(id, out var factId))
        {
            return $"'{id}' is not a fact handle; they look like 'f42'. Nothing was retracted.";
        }

        // Any live fact — seeded, captured from the user, or a note this session took.
        // Refusing to close one because of where it came from would be the store telling a
        // user which of their own memories they are allowed to drop, and once every tier is
        // an ordinary fact there is nothing left to base the refusal on. The seeder already
        // declines to write back anything this store has held before, so a retraction
        // survives a corpus revision.
        using var connection = EngramDatabase.OpenInitialized(home);
        var closed = FactStore.Forget(connection, factId, "retracted by the user", DateTimeOffset.UtcNow);

        // Reporting success for something that was never live would leave the user believing
        // a fact is gone while recall keeps returning it.
        if (!closed)
        {
            return $"No live fact with id '{id}'. It may already have been superseded or retracted; "
                + "call engram_recall to see what stands.";
        }

        return $"Retracted [{id}]. It will not appear in recall again. The retraction is recorded "
            + "rather than the original erased, because facts here are only ever closed, never deleted.";
    }

    [McpServerTool(Name = "engram_digest")]
    [Description(
        "Flush the durable learnings from this session in one call, before context is compacted or the " +
        "session ends. Accepts up to 25 short learnings plus an optional summary. This milestone does not " +
        "persist anything yet; it only confirms receipt to measure whether digest fires unprompted.")]
    public static string Digest(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("Up to 25 short, self-contained facts learned this session.")] string[] learnings,
        [Description("A one- or two-sentence summary of the session.")] string? session_summary = null)
    {
        var total = learnings.Length;
        var counted = Math.Min(total, 25);
        var overflow = total - counted;

        if (homeState.Initialized)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: session.Value,
                Kind: TelemetryEventKind.Digest));
        }

        var summaryNote = string.IsNullOrWhiteSpace(session_summary) ? string.Empty : $" Summary noted: \"{session_summary}\"";
        var overflowNote = overflow > 0 ? $" ({overflow} additional learning(s) beyond the 25-cap were ignored.)" : string.Empty;

        return $"Digest received: {counted} learning(s) recorded.{summaryNote}{overflowNote} " +
               "Not persisted in this milestone (no database yet) — this call only measures whether digest fires at session end.";
    }
}
