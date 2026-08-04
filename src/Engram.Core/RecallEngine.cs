using System.Globalization;
using System.Text;

namespace Engram.Core;

public enum RecallCoverage
{
    None,
    Partial,
    High,
}

public sealed record RankedFact(CannedFact Fact, int Score);

public sealed record RankedSessionFact(SessionFactRecord Fact, int Score);

public sealed record RecallPackResult(
    string Text,
    int FactCount,
    int TokensUsed,
    RecallCoverage Coverage,
    int SessionFactCount,
    int LongTermFactCount,
    int PriorSessionFactCount);

file enum FactSource
{
    CurrentSession,
    LongTerm,
    PriorSession,
}

file readonly record struct RankedOther(string Line, int Score, string Id, string SessionId, bool IsPriorSessionFact);

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

    public static IReadOnlyList<RankedSessionFact> RankSessionFacts(string query, IReadOnlyList<SessionFactRecord> facts)
    {
        var queryTerms = TokenizeQuery(query);
        if (queryTerms.Count == 0)
        {
            return [];
        }

        var scored = new List<RankedSessionFact>();
        foreach (var fact in facts)
        {
            var factTerms = Tokenize((fact.Subject ?? string.Empty) + " " + fact.Statement);
            var score = queryTerms.Count(factTerms.Contains);
            if (score > 0)
            {
                scored.Add(new RankedSessionFact(fact, score));
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

    public static RecallPackResult Pack(string query, IReadOnlyList<CannedFact> facts, int budgetTokens) =>
        Pack(query, facts, [], [], budgetTokens);

    public static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFactRecord> currentSessionFacts,
        int budgetTokens) =>
        Pack(query, facts, currentSessionFacts, [], budgetTokens);

    public static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFactRecord> currentSessionFacts,
        IReadOnlyList<SessionFactRecord> priorSessionFacts,
        int budgetTokens)
    {
        var rankedCurrentSession = RankSessionFacts(query, currentSessionFacts);
        var rankedLongTerm = Rank(query, facts);
        var rankedPriorSession = RankSessionFacts(query, priorSessionFacts);
        var discriminators = BuildPriorSessionDiscriminators(priorSessionFacts);

        var otherRanked = rankedLongTerm
            .Select(r => new RankedOther(FormatFactLine(r.Fact), r.Score, r.Fact.Id, string.Empty, IsPriorSessionFact: false))
            .Concat(rankedPriorSession.Select(r => new RankedOther(
                FormatPriorSessionFactLine(r.Fact, discriminators[r.Fact.SessionId]),
                r.Score,
                r.Fact.Id,
                r.Fact.SessionId,
                IsPriorSessionFact: true)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ThenBy(r => r.SessionId, StringComparer.Ordinal)
            .ToList();

        var coverage = ClassifyCoverage(rankedCurrentSession.Count + otherRanked.Count);

        var candidates = new List<(string Line, FactSource Source)>(rankedCurrentSession.Count + otherRanked.Count);
        candidates.AddRange(rankedCurrentSession.Select(r => (FormatSessionFactLine(r.Fact), FactSource.CurrentSession)));
        candidates.AddRange(otherRanked.Select(r => (r.Line, r.IsPriorSessionFact ? FactSource.PriorSession : FactSource.LongTerm)));

        var includedLines = new List<string>();
        var tokensUsed = 0;
        var sessionFactCount = 0;
        var longTermFactCount = 0;
        var priorSessionFactCount = 0;
        foreach (var (line, source) in candidates)
        {
            var lineTokens = TokenEstimator.Estimate(line);
            if (tokensUsed + lineTokens > budgetTokens)
            {
                break;
            }

            includedLines.Add(line);
            tokensUsed += lineTokens;
            switch (source)
            {
                case FactSource.CurrentSession:
                    sessionFactCount++;
                    break;
                case FactSource.LongTerm:
                    longTermFactCount++;
                    break;
                case FactSource.PriorSession:
                    priorSessionFactCount++;
                    break;
            }
        }

        var factCount = sessionFactCount + longTermFactCount + priorSessionFactCount;
        var lines = new List<string>
        {
            $"RECALL \"{query}\" · {factCount} facts · {tokensUsed}/{budgetTokens} tokens · coverage: {ToText(coverage)}",
        };
        lines.AddRange(includedLines);

        if (coverage != RecallCoverage.High)
        {
            lines.Add($"gaps: {GapsMessage(query, coverage)}");
        }

        lines.Add("→ engram_remember what you discover · engram_digest before session ends");

        return new RecallPackResult(
            string.Join('\n', lines), factCount, tokensUsed, coverage, sessionFactCount, longTermFactCount, priorSessionFactCount);
    }

    private static Dictionary<string, string> BuildPriorSessionDiscriminators(IReadOnlyList<SessionFactRecord> priorSessionFacts)
    {
        var sessionIds = priorSessionFacts
            .Select(f => f.SessionId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < sessionIds.Count; i++)
        {
            map[sessionIds[i]] = "p" + (i + 1);
        }

        return map;
    }

    public static string ToText(RecallCoverage coverage) => coverage switch
    {
        RecallCoverage.High => "high",
        RecallCoverage.Partial => "partial",
        _ => "none",
    };

    private static string FormatFactLine(CannedFact fact) =>
        $"[{fact.Id}] {fact.Body} ({fact.Scope} · {fact.AgeDays}d)";

    private static string FormatSessionFactLine(SessionFactRecord fact)
    {
        var scope = string.IsNullOrWhiteSpace(fact.Agent) ? "session" : $"session · {fact.Agent}";
        return $"[{fact.Id}] {fact.Statement} ({scope})";
    }

    private static string FormatPriorSessionFactLine(SessionFactRecord fact, string discriminator)
    {
        var ageDays = ComputeAgeDays(fact.Timestamp);
        var scope = string.IsNullOrWhiteSpace(fact.Agent)
            ? $"session · {ageDays}d"
            : $"session · {fact.Agent} · {ageDays}d";
        return $"[{fact.Id}@{discriminator}] {fact.Statement} ({scope})";
    }

    private static int ComputeAgeDays(string timestamp)
    {
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return 0;
        }

        var age = DateTimeOffset.UtcNow - parsed;
        return age.TotalDays > 0 ? (int)age.TotalDays : 0;
    }

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
