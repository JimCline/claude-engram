namespace Engram.Core;

public static class PrimerBuilder
{
    public const int MaxTokens = 300;

    private const string Instruction =
        "Engram memory is available and cheap. Call engram_recall before exploring files; " +
        "use engram_remember for durable facts you learn; flush learnings via engram_digest " +
        "before the session ends.";

    private static readonly string[] PreferredScopeOrder = ["user", "project", "code", "session"];

    public static string Build(IReadOnlyList<CannedFact> facts)
    {
        var lines = new List<string> { Instruction };
        var tokens = TokenEstimator.Estimate(Instruction);

        foreach (var scope in OrderedScopes(facts))
        {
            var header = char.ToUpperInvariant(scope[0]) + scope[1..] + ":";
            AppendSection(lines, ref tokens, header, facts.Where(f => f.Scope == scope).Take(5));
        }

        return string.Join('\n', lines);
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

    private static void AppendSection(List<string> lines, ref int tokens, string header, IEnumerable<CannedFact> facts)
    {
        var factLines = facts.Select(f => $"- {f.Body}").ToList();
        if (factLines.Count == 0)
        {
            return;
        }

        var headerTokens = TokenEstimator.Estimate(header);
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

        lines.Add(header);
        lines.AddRange(fittingLines);
        tokens += headerTokens + fittingTokens;
    }
}
