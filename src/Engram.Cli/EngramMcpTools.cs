using System.ComponentModel;
using Engram.Core;
using ModelContextProtocol.Server;

namespace Engram.Cli;

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
        [Description("What you want to know, as a few keywords or a short question.")] string query,
        [Description("Maximum tokens to spend on the response. Defaults to 500.")] int? budget_tokens = null)
    {
        var budget = budget_tokens is > 0 ? budget_tokens.Value : RecallEngine.DefaultBudgetTokens;
        var result = RecallEngine.Pack(query, CannedFacts.All, budget);

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: Telemetry.CurrentSessionId(home),
            Kind: TelemetryEventKind.Recall,
            Query: query,
            FactCount: result.FactCount,
            TokensReturned: result.TokensUsed,
            Coverage: RecallEngine.ToText(result.Coverage)));

        return result.Text;
    }

    [McpServerTool(Name = "engram_remember")]
    [Description(
        "Record a durable fact you just learned so a future session gets a memory hit instead of " +
        "rediscovering it — call it whenever you learn something worth remembering next time. This " +
        "milestone does not persist facts yet; the call only confirms what would be stored, to measure " +
        "whether the agent writes back at all.")]
    public static string Remember(
        EngramHome home,
        [Description("The fact to remember, as a short, self-contained statement.")] string statement,
        [Description("What or who the fact is about, if not obvious from the statement.")] string? subject = null,
        [Description("Where this was learned — a file path, PR, or command output.")] string? evidence = null)
    {
        var factId = "fnew-" + Guid.NewGuid().ToString("N")[..6];

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: Telemetry.CurrentSessionId(home),
            Kind: TelemetryEventKind.Remember));

        var subjectText = string.IsNullOrWhiteSpace(subject) ? "(unspecified subject)" : subject;
        var evidenceText = string.IsNullOrWhiteSpace(evidence) ? string.Empty : $" (evidence: {evidence})";

        return $"[{factId}] would remember: {subjectText} — \"{statement}\"{evidenceText}\n" +
               "Not persisted in this milestone (no database yet) — this call only measures write-back adoption.";
    }

    [McpServerTool(Name = "engram_digest")]
    [Description(
        "Flush the durable learnings from this session in one call, before context is compacted or the " +
        "session ends. Accepts up to 25 short learnings plus an optional summary. This milestone does not " +
        "persist anything yet; it only confirms receipt to measure whether digest fires unprompted.")]
    public static string Digest(
        EngramHome home,
        [Description("Up to 25 short, self-contained facts learned this session.")] string[] learnings,
        [Description("A one- or two-sentence summary of the session.")] string? session_summary = null)
    {
        var total = learnings.Length;
        var counted = Math.Min(total, 25);
        var overflow = total - counted;

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: Telemetry.CurrentSessionId(home),
            Kind: TelemetryEventKind.Digest));

        var summaryNote = string.IsNullOrWhiteSpace(session_summary) ? string.Empty : $" Summary noted: \"{session_summary}\"";
        var overflowNote = overflow > 0 ? $" ({overflow} additional learning(s) beyond the 25-cap were ignored.)" : string.Empty;

        return $"Digest received: {counted} learning(s) recorded.{summaryNote}{overflowNote} " +
               "Not persisted in this milestone (no database yet) — this call only measures whether digest fires at session end.";
    }
}
