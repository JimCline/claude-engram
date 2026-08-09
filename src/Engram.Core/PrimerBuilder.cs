namespace Engram.Core;

public static class PrimerBuilder
{
    public const int MaxTokens = 300;

    // A subagent's situation differs from the main session's in one way that matters: it
    // reports back through a summary, and a summary is lossy by construction. Anything it
    // learned that did not fit the report is gone at handoff unless it was written down.
    // That is the gap D11's session memory exists to close, so it is what this says.
    private const string SubagentInstruction =
        "Engram memory is shared with the session that spawned you. Call engram_recall before " +
        "exploring files — what you are about to work out may already be recorded. Write anything " +
        "durable you learn with engram_remember before you report back: your report is a summary, " +
        "and summaries lose the details the next agent needs.";

    private const string ExamplesHeader = "Examples:";
    private const int MaxClusters = 5;

    // Internal because PrimerSummary sizes its candidate query against it: the superset it
    // reads has to reach as far into the catalog as TopFacts can.
    internal const int MaxExampleFacts = 2;

    private static readonly string[] PreferredScopeOrder = ["user", "project", "code", "session"];

    /// <summary>
    /// The primer delivered at session start. Carries what a static tool description cannot:
    /// how much is stored right now, what it covers, and the one claim that has to vary per
    /// install — where this agent's durable memory lives (<see cref="MemorySettings"/>). The
    /// rest of the standing guidance — recall before exploring, remember what you learn,
    /// digest before the end — lives in the tool descriptions instead, because those persist
    /// for the whole session while this channel is ordinary context and is summarized away by
    /// compaction (D15).
    /// </summary>
    /// <remarks>
    /// The precedence line goes first because <see cref="TryAppendLine"/> drops whatever does
    /// not fit the budget, and of the three things here it is the only one whose absence
    /// changes what the agent does. It is also why an empty store no longer produces an empty
    /// primer: a fresh install with nothing recorded is precisely the session where a
    /// competing memory system wins by default, so it is the session that most needs telling.
    /// Returns an empty string only when there is nothing stored and precedence is off.
    /// </remarks>
    public static string Build(PrimerSummary summary, MemoryPrecedence precedence)
    {
        var lines = new List<string>();
        var tokens = 0;

        TryAppendLine(lines, ref tokens, MemorySettings.PrimerLine(precedence));
        TryAppendLine(lines, ref tokens, CoverageLine(summary.FactCount, summary.TopicCounts));

        AppendExamples(lines, ref tokens, TopFacts(summary.ExampleCandidates, MaxExampleFacts));

        return string.Join('\n', lines);
    }

    /// <inheritdoc cref="Build(PrimerSummary, MemoryPrecedence)"/>
    public static string Build(IReadOnlyList<CannedFact> facts, MemoryPrecedence precedence) =>
        Build(PrimerSummary.From(facts), precedence);

    /// <summary>
    /// The primer delivered at every subagent spawn. Carries no examples: a subagent's
    /// context is spent on its task, and what it needs is the instruction and the shape
    /// of what is already known, not a demonstration.
    /// </summary>
    /// <remarks>
    /// It repeats the precedence line rather than relying on the parent's, because SessionStart
    /// never fires for a subagent — whatever the parent was told about where memory lives reaches
    /// the child only if the child is told the same thing through this path.
    /// </remarks>
    public static string BuildForSubagent(PrimerSummary summary, MemoryPrecedence precedence)
    {
        var lines = new List<string> { SubagentInstruction };
        var tokens = TokenEstimator.Estimate(SubagentInstruction);

        TryAppendLine(lines, ref tokens, MemorySettings.PrimerLine(precedence));
        TryAppendLine(lines, ref tokens, CoverageLine(summary.FactCount, summary.TopicCounts));

        return string.Join('\n', lines);
    }

    /// <inheritdoc cref="BuildForSubagent(PrimerSummary, MemoryPrecedence)"/>
    public static string BuildForSubagent(IReadOnlyList<CannedFact> facts, MemoryPrecedence precedence) =>
        BuildForSubagent(PrimerSummary.From(facts), precedence);

    private static void TryAppendLine(List<string> lines, ref int tokens, string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var lineTokens = TokenEstimator.Estimate(line);
        if (tokens + lineTokens > MaxTokens)
        {
            return;
        }

        lines.Add(line);
        tokens += lineTokens;
    }

    private static string? CoverageLine(int factCount, IReadOnlyDictionary<string, int> topicCounts)
    {
        if (factCount == 0)
        {
            return null;
        }

        var topics = topicCounts
            .Select(t => (Key: t.Key, Count: t.Value))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

        var parts = topics.Take(MaxClusters).Select(t => $"{t.Key} ({t.Count})").ToList();
        if (topics.Count > MaxClusters)
        {
            parts.Add($"+{topics.Count - MaxClusters} more");
        }

        var noun = factCount == 1 ? "fact" : "facts";
        return $"Memory holds {factCount} {noun}: {string.Join(", ", parts)}.";
    }

    private static IReadOnlyList<CannedFact> TopFacts(IReadOnlyList<CannedFact> facts, int count)
    {
        var result = new List<CannedFact>();

        foreach (var scope in OrderedScopes(facts))
        {
            result.Add(facts.First(f => f.Scope == scope));
            if (result.Count == count)
            {
                return result;
            }
        }

        foreach (var fact in facts)
        {
            if (result.Count == count)
            {
                break;
            }

            if (!result.Contains(fact))
            {
                result.Add(fact);
            }
        }

        return result;
    }

    private static IEnumerable<string> OrderedScopes(IReadOnlyList<CannedFact> facts)
    {
        var present = facts.Select(f => f.Scope).Distinct().ToHashSet(StringComparer.Ordinal);

        foreach (var scope in PreferredScopeOrder)
        {
            if (present.Remove(scope))
            {
                yield return scope;
            }
        }

        foreach (var scope in present.OrderBy(s => s, StringComparer.Ordinal))
        {
            yield return scope;
        }
    }

    private static void AppendExamples(List<string> lines, ref int tokens, IReadOnlyList<CannedFact> facts)
    {
        var factLines = facts.Select(f => $"- {f.Body}").ToList();
        if (factLines.Count == 0)
        {
            return;
        }

        var headerTokens = TokenEstimator.Estimate(ExamplesHeader);
        if (tokens + headerTokens > MaxTokens)
        {
            return;
        }

        var fittingLines = new List<string>();
        var fittingTokens = 0;
        foreach (var line in factLines)
        {
            var lineTokens = TokenEstimator.Estimate(line);
            if (tokens + headerTokens + fittingTokens + lineTokens > MaxTokens)
            {
                break;
            }

            fittingLines.Add(line);
            fittingTokens += lineTokens;
        }

        if (fittingLines.Count == 0)
        {
            return;
        }

        lines.Add(ExamplesHeader);
        lines.AddRange(fittingLines);
        tokens += headerTokens + fittingTokens;
    }
}
