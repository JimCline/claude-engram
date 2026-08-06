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

public sealed record RankedSessionFact(SessionFact Fact, int Score);

public sealed record RecallPackResult(
    string Text,
    int FactCount,
    int TokensUsed,
    RecallCoverage Coverage,
    int SessionFactCount,
    int LongTermFactCount,
    int PriorSessionFactCount);

/// <summary>Which tier of memory a candidate came from.</summary>
public enum FactOrigin
{
    CurrentSession,
    LongTerm,
    PriorSession,
}

/// <summary>
/// One fact the ranker considered, in the order it considered it, and whether the budget let it
/// through.
/// </summary>
public sealed record RecallCandidate(
    long? FactId,
    string Handle,
    string Line,
    int Score,
    FactOrigin Origin,
    int Tokens,
    bool Packed);

/// <summary>
/// What <see cref="RecallEngine.Pack(string, IReadOnlyList{CannedFact}, IReadOnlyList{SessionFact}, IReadOnlyList{SessionFact}, int)"/>
/// did, per candidate.
/// </summary>
/// <remarks>
/// Produced by the same two routines that produce the pack itself, never by a second
/// implementation of the ordering. An explainer that re-derives the ranking is a copy that will
/// drift from it, and the drift is invisible precisely because the explainer is what one would
/// use to notice.
/// </remarks>
public sealed record RecallExplanation(
    string Query,
    IReadOnlyList<string> QueryTerms,
    IReadOnlyList<string> DroppedTerms,
    IReadOnlyList<RecallCandidate> Candidates,
    int BudgetTokens,
    int TokensUsed,
    RecallCoverage Coverage);

file readonly record struct RankedOther(string Line, int Score, string Id, long SessionId, bool IsPriorSessionFact);

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

    public static IReadOnlyList<RankedSessionFact> RankSessionFacts(string query, IReadOnlyList<SessionFact> facts)
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
            .ThenBy(r => r.Fact.FactId)
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
        IReadOnlyList<SessionFact> currentSessionFacts,
        int budgetTokens) =>
        Pack(query, facts, currentSessionFacts, [], budgetTokens);

    public static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        int budgetTokens)
    {
        var candidates = BuildCandidates(query, facts, currentSessionFacts, priorSessionFacts);
        var coverage = ClassifyCoverage(candidates.Count);
        var tokensUsed = ApplyBudget(candidates, budgetTokens);

        var includedLines = new List<string>();
        var sessionFactCount = 0;
        var longTermFactCount = 0;
        var priorSessionFactCount = 0;
        foreach (var candidate in candidates)
        {
            if (!candidate.Packed)
            {
                continue;
            }

            includedLines.Add(candidate.Line);
            switch (candidate.Origin)
            {
                case FactOrigin.CurrentSession:
                    sessionFactCount++;
                    break;
                case FactOrigin.LongTerm:
                    longTermFactCount++;
                    break;
                case FactOrigin.PriorSession:
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

    /// <summary>
    /// Everything <see cref="Pack(string, IReadOnlyList{CannedFact}, IReadOnlyList{SessionFact}, IReadOnlyList{SessionFact}, int)"/>
    /// decided, without building the digest.
    /// </summary>
    /// <remarks>
    /// The point of D21, in one method: the ordering and the budget below are the ones recall
    /// runs, not a reconstruction of them, so a candidate reported as fourth is fourth.
    /// </remarks>
    public static RecallExplanation Explain(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        int budgetTokens)
    {
        var candidates = BuildCandidates(query, facts, currentSessionFacts, priorSessionFacts);
        var tokensUsed = ApplyBudget(candidates, budgetTokens);

        var used = TokenizeQuery(query);
        var dropped = Tokenize(query).Where(t => !used.Contains(t)).Order(StringComparer.Ordinal).ToList();

        return new RecallExplanation(
            query,
            used.Order(StringComparer.Ordinal).ToList(),
            dropped,
            candidates,
            budgetTokens,
            tokensUsed,
            ClassifyCoverage(candidates.Count));
    }

    /// <summary>Every fact that scored, in the order the budget will see them.</summary>
    private static List<RecallCandidate> BuildCandidates(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts)
    {
        var rankedCurrentSession = RankSessionFacts(query, currentSessionFacts);
        var rankedLongTerm = Rank(query, facts);
        var rankedPriorSession = RankSessionFacts(query, priorSessionFacts);
        var discriminators = BuildPriorSessionDiscriminators(priorSessionFacts);

        var otherRanked = rankedLongTerm
            .Select(r => new RankedOther(FormatFactLine(r.Fact), r.Score, r.Fact.Id, 0, IsPriorSessionFact: false))
            .Concat(rankedPriorSession.Select(r => new RankedOther(
                FormatPriorSessionFactLine(r.Fact, discriminators[r.Fact.SessionId]),
                r.Score,
                FactCatalog.HandleFor(r.Fact.FactId),
                r.Fact.SessionId,
                IsPriorSessionFact: true)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ThenBy(r => r.SessionId)
            .ToList();

        var candidates = new List<RecallCandidate>(rankedCurrentSession.Count + otherRanked.Count);

        // Working memory first, whole, before anything competes for the remainder — this
        // session's notes outranking everything is a tier decision, not a score.
        foreach (var ranked in rankedCurrentSession)
        {
            var line = FormatSessionFactLine(ranked.Fact);
            candidates.Add(new RecallCandidate(
                ranked.Fact.FactId,
                FactCatalog.HandleFor(ranked.Fact.FactId),
                line,
                ranked.Score,
                FactOrigin.CurrentSession,
                TokenEstimator.Estimate(line),
                Packed: false));
        }

        foreach (var ranked in otherRanked)
        {
            candidates.Add(new RecallCandidate(
                FactCatalog.TryParseHandle(ranked.Id, out var factId) ? factId : null,
                ranked.Id,
                ranked.Line,
                ranked.Score,
                ranked.IsPriorSessionFact ? FactOrigin.PriorSession : FactOrigin.LongTerm,
                TokenEstimator.Estimate(ranked.Line),
                Packed: false));
        }

        return candidates;
    }

    /// <summary>
    /// Marks the prefix that fits, and returns what it spent.
    /// </summary>
    /// <remarks>
    /// Stops at the first candidate that does not fit rather than skipping it to try smaller ones
    /// further down. Packing tightly would reorder the digest by length, and a model reading a
    /// ranked list is entitled to assume the order means something.
    /// </remarks>
    private static int ApplyBudget(List<RecallCandidate> candidates, int budgetTokens)
    {
        var tokensUsed = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (tokensUsed + candidates[i].Tokens > budgetTokens)
            {
                break;
            }

            candidates[i] = candidates[i] with { Packed = true };
            tokensUsed += candidates[i].Tokens;
        }

        return tokensUsed;
    }

    // p1, p2, … so the model can tell which notes came from the same earlier session. Ids
    // are globally unique now and carry no such grouping on their own, and "these three
    // findings are from one sitting" is information a reader acts on.
    private static Dictionary<long, string> BuildPriorSessionDiscriminators(IReadOnlyList<SessionFact> priorSessionFacts)
    {
        var sessionIds = priorSessionFacts
            .Select(f => f.SessionId)
            .Distinct()
            .Order()
            .ToList();

        var map = new Dictionary<long, string>();
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

    private static string FormatSessionFactLine(SessionFact fact)
    {
        var scope = string.IsNullOrWhiteSpace(fact.Agent) ? "session" : $"session · {fact.Agent}";
        return $"[{FactCatalog.HandleFor(fact.FactId)}] {fact.Statement} ({scope})";
    }

    // The session discriminator sits in the annotation rather than inside the handle, where
    // it used to read "[s001@p1]". A handle is what a tool takes back; overloading it with
    // grouping meant the string the model saw was not the string engram_forget accepts.
    private static string FormatPriorSessionFactLine(SessionFact fact, string discriminator)
    {
        var scope = string.IsNullOrWhiteSpace(fact.Agent)
            ? $"session · {discriminator} · {fact.AgeDays}d"
            : $"session · {discriminator} · {fact.Agent} · {fact.AgeDays}d";
        return $"[{FactCatalog.HandleFor(fact.FactId)}] {fact.Statement} ({scope})";
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
