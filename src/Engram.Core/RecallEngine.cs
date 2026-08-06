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
/// One fact the ranker considered, in the order it considered it, with each lane's position and
/// whether the budget let it through.
/// </summary>
/// <remarks>
/// A lane's rank is null where that lane did not return the fact at all, which is a different
/// thing from returning it last — and the distinction is the one worth seeing, because a fact
/// only one lane found is exactly what fusion exists to rescue.
/// </remarks>
public sealed record RecallCandidate(
    long? FactId,
    string Handle,
    string Line,
    double Fused,
    int? OverlapRank,
    int? LexicalRank,
    int? VectorRank,
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

/// <summary>One fact in the universe recall ranks over, before any lane has an opinion.</summary>
file sealed record Entry(
    long? FactId,
    string Handle,
    string Line,
    FactOrigin Origin,
    long SessionId,
    int OverlapScore);

public static class RecallEngine
{
    public const int DefaultBudgetTokens = RetrievalSettings.DefaultBudgetTokens;

    /// <summary>
    /// Reciprocal rank fusion's damping constant.
    /// </summary>
    /// <remarks>
    /// <para>60 is the value from the paper the technique comes from and the one §6.1 step 8
    /// names. What it buys: the contribution of rank <i>r</i> is <c>1/(60 + r)</c>, so the gap
    /// between first and second place is small (0.0164 against 0.0161) while the gap between
    /// "one lane found it" and "both lanes found it" is nearly double. Fusion therefore rewards
    /// agreement between lanes far more than a good position within one of them, which is the
    /// property wanted here — the lanes measure different things and neither is authoritative.</para>
    ///
    /// <para>A much smaller k would make each lane's top hit dominate and turn fusion into
    /// whichever lane happened to answer first; a much larger one flattens the ranks until only
    /// lane agreement counts and position stops mattering at all.</para>
    /// </remarks>
    public const int RrfK = 60;

    private static readonly Dictionary<long, int> EmptyRanks = [];

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
            var score = OverlapScore(queryTerms, fact.Subject + " " + fact.Body);
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
            var score = OverlapScore(queryTerms, (fact.Subject ?? string.Empty) + " " + fact.Statement);
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

    /// <summary>
    /// How much of the question the store answered — from lane agreement, not from how many rows
    /// came back.
    /// </summary>
    /// <param name="matchedFactCount">Candidates in total. Only <c>none</c> turns on this.</param>
    /// <param name="corroboratedCount">
    /// Candidates more than one lane found. The spec asks for "lane agreement and score mass";
    /// this is the agreement half, and score mass is deliberately still open — one unmeasured
    /// knob is a rule, two are a preference.
    /// </param>
    /// <remarks>
    /// <para>This used to be the candidate count alone, which is not a measure of whether anything
    /// was <i>found</i>. bm25 will hand back a fact for almost any query: measured on the author's
    /// store, "weekend saturday personal activity outing" returned seven candidates of which six
    /// were engineering notes about lint tests and <c>BEGIN IMMEDIATE</c>, and the count called
    /// that <c>high</c>. That is not a cosmetic mislabel — <c>high</c> is precisely the value that
    /// suppresses the <c>gaps:</c> line, so the model was told memory had this covered and the
    /// discover-then-remember loop the spec builds on that line never fired.</para>
    ///
    /// <para>Measured across all seven queries this instance has ever recorded, the corroborated
    /// count separates them without needing a tuned threshold: 8, 7 and 8 for the three that
    /// returned what was asked for, and 1 for each of the four that did not. Any cutoff in 2..7
    /// gives the same answer, so the existing 3+ boundary is kept rather than fitted.</para>
    ///
    /// <para><c>none</c> stays keyed to the total, because it means the store said nothing and
    /// triggers a different response shape. Returning facts under <c>coverage: none</c> would be a
    /// worse lie than the one this fixes.</para>
    /// </remarks>
    public static RecallCoverage ClassifyCoverage(int matchedFactCount, int corroboratedCount) =>
        matchedFactCount == 0 ? RecallCoverage.None
        : corroboratedCount >= 3 ? RecallCoverage.High
        : RecallCoverage.Partial;

    /// <summary>Lanes that independently found this candidate.</summary>
    public static int LanesThatFound(RecallCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return (candidate.OverlapRank is null ? 0 : 1)
            + (candidate.LexicalRank is null ? 0 : 1)
            + (candidate.VectorRank is null ? 0 : 1);
    }

    /// <summary>Candidates more than one lane found — the input to <see cref="ClassifyCoverage"/>.</summary>
    /// <remarks>
    /// Public because the boundary is the whole rule and it is not reachable from a unit test of
    /// <c>Pack</c>: <see cref="CannedFact"/> carries no numeric id, so the lexical ranks that make
    /// a candidate corroborated only exist against a real store. A <c>&gt;= 1</c> here counts every
    /// candidate and silently restores the count-based classification this replaced.
    /// </remarks>
    public static int Corroborated(IEnumerable<RecallCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates.Count(candidate => LanesThatFound(candidate) > 1);
    }

    /// <summary>Ranks by term overlap alone — the lexical lane contributes nothing.</summary>
    /// <remarks>
    /// Kept for callers that have facts but no store to query, which is every unit test of the
    /// overlap lane and nothing on the recall path. Production goes through the overload taking
    /// <c>lexicalRanks</c>; a caller that has a connection and does not pass them is silently
    /// running the ranker that D30 measured blind to plurals.
    /// </remarks>
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
        int budgetTokens) =>
        Pack(query, facts, currentSessionFacts, priorSessionFacts, EmptyRanks, budgetTokens);

    public static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        IReadOnlyDictionary<long, int> lexicalRanks,
        int budgetTokens) =>
        Pack(query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks, EmptyRanks, budgetTokens);

    public static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        IReadOnlyDictionary<long, int> lexicalRanks,
        IReadOnlyDictionary<long, int> vectorRanks,
        int budgetTokens)
    {
        var candidates = BuildCandidates(
            query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks, vectorRanks);
        var coverage = ClassifyCoverage(candidates.Count, Corroborated(candidates));
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
        int budgetTokens) =>
        Explain(query, facts, currentSessionFacts, priorSessionFacts, EmptyRanks, budgetTokens);

    /// <inheritdoc cref="Explain(string, IReadOnlyList{CannedFact}, IReadOnlyList{SessionFact}, IReadOnlyList{SessionFact}, int)"/>
    public static RecallExplanation Explain(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        IReadOnlyDictionary<long, int> lexicalRanks,
        int budgetTokens) =>
        Explain(query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks, EmptyRanks, budgetTokens);

    public static RecallExplanation Explain(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        IReadOnlyDictionary<long, int> lexicalRanks,
        IReadOnlyDictionary<long, int> vectorRanks,
        int budgetTokens)
    {
        var candidates = BuildCandidates(
            query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks, vectorRanks);
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
            ClassifyCoverage(candidates.Count, Corroborated(candidates)));
    }

    /// <summary>
    /// Every fact either lane found, fused, in the order the budget will see them.
    /// </summary>
    /// <remarks>
    /// <para><b>Both lanes, because each finds what the other cannot.</b> The lexical lane is
    /// FTS5 over <c>body</c> and <c>predicate</c> with porter stemming, so it matches "pragmas"
    /// against "pragma" — which the overlap lane, comparing literal tokens, cannot. The overlap
    /// lane reads the subject name, which <c>fact_fts</c> does not index at all because an
    /// external-content table can only index columns on the content table. Replacing one with the
    /// other trades one class of miss for another; fusing them has neither.</para>
    ///
    /// <para>Reciprocal rank fusion rather than a combined score, because the lanes are not on a
    /// common scale and cannot be put on one: bm25 is an unbounded negative number whose magnitude
    /// depends on corpus statistics, the overlap score is a small count of terms, and a vector
    /// distance is a geometry in a space the other two know nothing about. Any weighting between
    /// them would be a constant nobody could justify. Ranks are comparable by construction, which
    /// is the entire argument for RRF — and it is why adding a third lane needed no retuning of
    /// the first two.</para>
    /// </remarks>
    private static List<RecallCandidate> BuildCandidates(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        IReadOnlyDictionary<long, int> lexicalRanks,
        IReadOnlyDictionary<long, int> vectorRanks)
    {
        var queryTerms = TokenizeQuery(query);
        var discriminators = BuildPriorSessionDiscriminators(priorSessionFacts);
        var entries = new List<Entry>(facts.Count + currentSessionFacts.Count + priorSessionFacts.Count);

        foreach (var fact in facts)
        {
            entries.Add(new Entry(
                FactCatalog.TryParseHandle(fact.Id, out var id) ? id : null,
                fact.Id,
                FormatFactLine(fact),
                FactOrigin.LongTerm,
                0,
                OverlapScore(queryTerms, fact.Subject + " " + fact.Body)));
        }

        foreach (var fact in currentSessionFacts)
        {
            entries.Add(new Entry(
                fact.FactId,
                FactCatalog.HandleFor(fact.FactId),
                FormatSessionFactLine(fact),
                FactOrigin.CurrentSession,
                fact.SessionId,
                OverlapScore(queryTerms, (fact.Subject ?? string.Empty) + " " + fact.Statement)));
        }

        foreach (var fact in priorSessionFacts)
        {
            entries.Add(new Entry(
                fact.FactId,
                FactCatalog.HandleFor(fact.FactId),
                FormatPriorSessionFactLine(fact, discriminators[fact.SessionId]),
                FactOrigin.PriorSession,
                fact.SessionId,
                OverlapScore(queryTerms, (fact.Subject ?? string.Empty) + " " + fact.Statement)));
        }

        // One overlap ranking over the whole universe rather than one per tier, so a rank means
        // the same thing on both sides of the fusion.
        var overlapRanks = new Dictionary<string, int>(StringComparer.Ordinal);
        var overlapOrder = entries
            .Where(e => e.OverlapScore > 0)
            .OrderByDescending(e => e.OverlapScore)
            .ThenBy(e => e.Handle, StringComparer.Ordinal)
            .ThenBy(e => e.SessionId);
        foreach (var entry in overlapOrder)
        {
            overlapRanks[entry.Handle] = overlapRanks.Count + 1;
        }

        var scored = new List<RecallCandidate>();
        foreach (var entry in entries)
        {
            var overlap = overlapRanks.TryGetValue(entry.Handle, out var o) ? o : (int?)null;
            var lexical = entry.FactId is { } id && lexicalRanks.TryGetValue(id, out var l) ? l : (int?)null;
            var vector = entry.FactId is { } vid && vectorRanks.TryGetValue(vid, out var v) ? v : (int?)null;
            if (overlap is null && lexical is null && vector is null)
            {
                continue;
            }

            scored.Add(new RecallCandidate(
                entry.FactId,
                entry.Handle,
                entry.Line,
                Reciprocal(overlap) + Reciprocal(lexical) + Reciprocal(vector),
                overlap,
                lexical,
                vector,
                entry.Origin,
                TokenEstimator.Estimate(entry.Line),
                Packed: false));
        }

        // Working memory first, whole, before anything competes for the remainder — this session's
        // notes outranking everything is a tier decision, not a score, and fusion does not get a
        // vote on it.
        return scored
            .OrderBy(c => c.Origin == FactOrigin.CurrentSession ? 0 : 1)
            .ThenByDescending(c => c.Fused)
            .ThenBy(c => c.Handle, StringComparer.Ordinal)
            .ToList();
    }

    private static double Reciprocal(int? rank) => rank is { } r ? 1d / (RrfK + r) : 0d;

    private static int OverlapScore(HashSet<string> queryTerms, string text) =>
        queryTerms.Count == 0 ? 0 : queryTerms.Count(Tokenize(text).Contains);

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
