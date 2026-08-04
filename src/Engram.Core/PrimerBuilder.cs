namespace Engram.Core;

public static class PrimerBuilder
{
    public const int MaxTokens = 300;

    private const string Instruction =
        "Engram memory is available and cheap. Call engram_recall before exploring files; " +
        "use engram_remember for durable facts you learn; flush learnings via engram_digest " +
        "before the session ends.";

    private const string ExamplesHeader = "Examples:";
    private const int MaxClusters = 5;
    private const int MaxExampleFacts = 2;

    private static readonly string[] PreferredScopeOrder = ["user", "project", "code", "session"];

    public static string Build(IReadOnlyList<CannedFact> facts)
    {
        var lines = new List<string> { Instruction };
        var tokens = TokenEstimator.Estimate(Instruction);

        TryAppendLine(lines, ref tokens, CoverageLine(facts));

        AppendExamples(lines, ref tokens, TopFacts(facts, MaxExampleFacts));

        return string.Join('\n', lines);
    }

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

    private static string? CoverageLine(IReadOnlyList<CannedFact> facts)
    {
        if (facts.Count == 0)
        {
            return null;
        }

        var topics = facts
            .GroupBy(f => f.Topic, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

        var parts = topics.Take(MaxClusters).Select(t => $"{t.Key} ({t.Count})").ToList();
        if (topics.Count > MaxClusters)
        {
            parts.Add($"+{topics.Count - MaxClusters} more");
        }

        var noun = facts.Count == 1 ? "fact" : "facts";
        return $"Memory holds {facts.Count} {noun}: {string.Join(", ", parts)}.";
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
