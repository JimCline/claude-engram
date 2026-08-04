using System.Text;

namespace Engram.Core;

public enum RecallCoverage
{
    None,
    Partial,
    High,
}

public sealed record RankedFact(CannedFact Fact, int Score);

public sealed record RecallPackResult(string Text, int FactCount, int TokensUsed, RecallCoverage Coverage);

public static class RecallEngine
{
    public const int DefaultBudgetTokens = 500;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "and", "or", "the", "a", "an", "of", "to", "in", "for", "is", "are", "was", "were",
        "be", "on", "with", "that", "this", "it", "as", "at", "by", "from", "but", "not",
        "all", "any", "how", "what", "when", "where", "which", "who", "why", "do", "does",
        "did", "can", "should", "would", "will",
    };

    public static IReadOnlyList<RankedFact> Rank(string query, IReadOnlyList<CannedFact> facts)
    {
        var queryTerms = TokenizeQuery(query);
        if (queryTerms.Count == 0)
        {
            return [];
        }

        var scored = new List<RankedFact>();
        foreach (var fact in facts)
        {
            var factTerms = Tokenize(fact.Subject + " " + fact.Body);
            var score = queryTerms.Count(factTerms.Contains);
            if (score > 0)
            {
                scored.Add(new RankedFact(fact, score));
            }
        }

        return scored
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Fact.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static RecallCoverage ClassifyCoverage(int matchedFactCount) => matchedFactCount switch
    {
        0 => RecallCoverage.None,
        1 or 2 => RecallCoverage.Partial,
        _ => RecallCoverage.High,
    };

    public static RecallPackResult Pack(string query, IReadOnlyList<CannedFact> facts, int budgetTokens)
    {
        var ranked = Rank(query, facts);
        var coverage = ClassifyCoverage(ranked.Count);

        var included = new List<CannedFact>();
        var tokensUsed = 0;
        foreach (var candidate in ranked)
        {
            var line = FormatFactLine(candidate.Fact);
            var lineTokens = TokenEstimator.Estimate(line);
            if (tokensUsed + lineTokens > budgetTokens)
            {
                break;
            }

            included.Add(candidate.Fact);
            tokensUsed += lineTokens;
        }

        var lines = new List<string>
        {
            $"RECALL \"{query}\" · {included.Count} facts · {tokensUsed}/{budgetTokens} tokens · coverage: {ToText(coverage)}",
        };
        lines.AddRange(included.Select(FormatFactLine));

        if (coverage != RecallCoverage.High)
        {
            lines.Add($"gaps: {GapsMessage(query, coverage)}");
        }

        lines.Add("→ engram_remember what you discover · engram_digest before session ends");

        return new RecallPackResult(string.Join('\n', lines), included.Count, tokensUsed, coverage);
    }

    public static string ToText(RecallCoverage coverage) => coverage switch
    {
        RecallCoverage.High => "high",
        RecallCoverage.Partial => "partial",
        _ => "none",
    };

    private static string FormatFactLine(CannedFact fact) =>
        $"[{fact.Id}] {fact.Body} ({fact.Scope} · {fact.AgeDays}d)";

    private static string GapsMessage(string query, RecallCoverage coverage) => coverage switch
    {
        RecallCoverage.None => $"no facts matched \"{query}\" — discover and engram_remember what you find",
        _ => $"only partial matches for \"{query}\" — verify before relying on this",
    };

    private static HashSet<string> TokenizeQuery(string query)
    {
        var terms = Tokenize(query);

        var filtered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (term.Length >= 3 && !Stopwords.Contains(term))
            {
                filtered.Add(term);
            }
        }

        return filtered.Count > 0 ? filtered : terms;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return terms;
    }
}
