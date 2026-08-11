using System.Globalization;

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
/// <remarks>
/// <para>Holds the fact it came from rather than a rendered line, because the line is only ever
/// wanted for a candidate and most entries never become one: the scoring loop drops every fact no
/// lane found, which on a query matching nothing is all of them. Rendering here would cost one
/// interpolation per live fact whatever the query — measured by ablation at 19 ms over 50,000
/// facts, roughly a quarter of a pack that runs 73–102 ms at that size — so the line is built past
/// that check instead.</para>
///
/// <para>A source reference and not a <c>Func&lt;string&gt;</c>: both fact types are record
/// <i>classes</i>, so carrying one copies a reference and boxes nothing, while a closure per entry
/// would trade the interpolation for an allocation on the same O(corpus) path and buy nothing.
/// Which of the three fields is populated follows from <see cref="FactOrigin"/>, which is what
/// selects the formatter.</para>
/// </remarks>
file sealed record Entry(
    long? FactId,
    string Handle,
    FactOrigin Origin,
    long SessionId,
    int OverlapScore,
    CannedFact? LongTerm,
    SessionFact? Session,
    string? Discriminator);

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

    /// <summary>
    /// A body inline up to this many tokens costs nothing to show whole; past it, truncation
    /// keeps one oversized fact from crowding the rest of the digest out of the budget.
    /// </summary>
    public const int MaxInlineBodyTokens = 120;

    /// <summary>How much of an oversized body is kept before the cut.</summary>
    public const int TruncatedBodyTokens = 100;

    private static readonly Dictionary<long, int> EmptyRanks = [];

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
    /// <remarks>
    /// Demoted from the production ranker to internal test-support (spec ruling 13/§3.4): SQLite
    /// now ranks and bounds (<see cref="RecallRanker"/>), and this in-memory path survives only as
    /// the equivalence harness's oracle. <c>InternalsVisibleTo</c> keeps it reachable from the test
    /// assemblies.
    /// </remarks>
    internal static RecallPackResult Pack(string query, IReadOnlyList<CannedFact> facts, int budgetTokens) =>
        Pack(query, facts, [], [], budgetTokens);

    internal static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        int budgetTokens) =>
        Pack(query, facts, currentSessionFacts, [], budgetTokens);

    internal static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        int budgetTokens) =>
        Pack(query, facts, currentSessionFacts, priorSessionFacts, EmptyRanks, budgetTokens);

    internal static RecallPackResult Pack(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        IReadOnlyDictionary<long, int> lexicalRanks,
        int budgetTokens) =>
        Pack(query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks, EmptyRanks, budgetTokens);

    internal static RecallPackResult Pack(
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

        lines.Add("→ engram_remember what you discover");

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
    internal static RecallExplanation Explain(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        int budgetTokens) =>
        Explain(query, facts, currentSessionFacts, priorSessionFacts, EmptyRanks, budgetTokens);

    /// <inheritdoc cref="Explain(string, IReadOnlyList{CannedFact}, IReadOnlyList{SessionFact}, IReadOnlyList{SessionFact}, int)"/>
    internal static RecallExplanation Explain(
        string query,
        IReadOnlyList<CannedFact> facts,
        IReadOnlyList<SessionFact> currentSessionFacts,
        IReadOnlyList<SessionFact> priorSessionFacts,
        IReadOnlyDictionary<long, int> lexicalRanks,
        int budgetTokens) =>
        Explain(query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks, EmptyRanks, budgetTokens);

    internal static RecallExplanation Explain(
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
        var dropped = Tokenizer.Tokenize(query).Where(t => !used.Contains(t)).Order(StringComparer.Ordinal).ToList();

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
                FactOrigin.LongTerm,
                0,
                OverlapScore(queryTerms, fact.Subject + " " + fact.Body),
                fact,
                null,
                null));
        }

        foreach (var fact in currentSessionFacts)
        {
            entries.Add(new Entry(
                fact.FactId,
                FactCatalog.HandleFor(fact.FactId),
                FactOrigin.CurrentSession,
                fact.SessionId,
                OverlapScore(queryTerms, (fact.Subject ?? string.Empty) + " " + fact.Statement),
                null,
                fact,
                null));
        }

        foreach (var fact in priorSessionFacts)
        {
            entries.Add(new Entry(
                fact.FactId,
                FactCatalog.HandleFor(fact.FactId),
                FactOrigin.PriorSession,
                fact.SessionId,
                OverlapScore(queryTerms, (fact.Subject ?? string.Empty) + " " + fact.Statement),
                null,
                fact,
                discriminators[fact.SessionId]));
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

            var line = FormatLine(entry.Origin, entry.LongTerm, entry.Session, entry.Discriminator);
            scored.Add(new RecallCandidate(
                entry.FactId,
                entry.Handle,
                line,
                Reciprocal(overlap) + Reciprocal(lexical) + Reciprocal(vector),
                overlap,
                lexical,
                vector,
                entry.Origin,
                TokenEstimator.Estimate(line),
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
        queryTerms.Count == 0 ? 0 : queryTerms.Count(Tokenizer.Tokenize(text).Contains);

    /// <summary>
    /// Marks the candidates that fit within the budget, and returns what it spent.
    /// </summary>
    /// <remarks>
    /// Skips a candidate that does not fit and keeps trying smaller ones further down. Relative
    /// order of packed items is preserved — the budget changes selection, never sequence. The
    /// earlier contract stopped at the first misfit to keep the digest a strict rank-prefix, but
    /// that guarantee was stated nowhere a model could read it, and its failure mode — a
    /// top-ranked oversized fact emptying the whole digest and reading as "the store knows
    /// nothing" — misleads worse than a subset does. Formatting-time truncation bounds line
    /// sizes, so skips are rare tail events.
    /// </remarks>
    internal static int ApplyBudget(List<RecallCandidate> candidates, int budgetTokens)
    {
        var tokensUsed = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (tokensUsed + candidates[i].Tokens > budgetTokens)
            {
                continue;
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

    /// <summary>
    /// Renders one surviving candidate through the formatter its tier owns.
    /// </summary>
    /// <remarks>
    /// Takes the fields rather than the entry holding them because <c>Entry</c> is file-local and
    /// C# forbids such a type in the signature of a member of a non-file-local one (CS9051).
    /// </remarks>
    internal static string FormatLine(
        FactOrigin origin,
        CannedFact? longTerm,
        SessionFact? session,
        string? discriminator) => origin switch
    {
        FactOrigin.LongTerm => FormatFactLine(longTerm!),
        FactOrigin.CurrentSession => FormatSessionFactLine(session!),
        FactOrigin.PriorSession => FormatPriorSessionFactLine(session!, discriminator!),
        _ => throw new InvalidOperationException($"no formatter for fact origin {origin}"),
    };

    /// <summary>
    /// The line a model reads a fact off. The version marker is the only signal that a handle
    /// leads anywhere: recall returns live beliefs, so a revised one and one held all along are
    /// the same line, and picking the wrong handle to expand reports "1 version" — which reads
    /// exactly like "never changed". Marking the thread head turns that from luck into a lookup.
    ///
    /// <para>A body past <see cref="MaxInlineBodyTokens"/> is shown truncated rather than whole,
    /// and a "· +N" marker at the end of the paren group reports everything withheld — the
    /// truncated tail plus any <see cref="CannedFact.DetailsChars"/> — so a fact this large is
    /// never invisible-or-poisonous to the rest of the digest (D57's marking pattern: present
    /// only when something is actually withheld, never on a whole, untruncated body).</para>
    /// </summary>
    internal static string FormatFactLine(CannedFact fact)
    {
        var (shownBody, withheldBodyChars) = TruncateBody(fact.Body);
        var marker = MarkerFor(withheldBodyChars + fact.DetailsChars);

        return fact.Versions > 1
            ? $"[{fact.Id}] {shownBody} ({fact.Scope} · {fact.AgeDays}d · v{fact.Versions}{marker})"
            : $"[{fact.Id}] {shownBody} ({fact.Scope} · {fact.AgeDays}d{marker})";
    }

    /// <summary>
    /// Cuts a body at a word boundary once it exceeds <see cref="MaxInlineBodyTokens"/>, and
    /// returns how many characters of the original were left out. Never splits a surrogate pair.
    /// </summary>
    private static (string Shown, int WithheldChars) TruncateBody(string body)
    {
        if (TokenEstimator.Estimate(body) <= MaxInlineBodyTokens)
        {
            return (body, 0);
        }

        var maxChars = (int)(TruncatedBodyTokens * TokenEstimator.CharactersPerToken);
        var end = Math.Min(maxChars, body.Length);

        if (end < body.Length)
        {
            // Back up to the last space only when doing so makes progress — a space at index 0
            // would otherwise cut to nothing (same non-advancing-page class step 3 guards against).
            var lastSpace = body.LastIndexOf(' ', end - 1, end);
            if (lastSpace > 0)
            {
                end = lastSpace;
            }
        }

        if (end < body.Length && char.IsLowSurrogate(body[end]))
        {
            end--;
        }

        return (body[..end] + "…", body.Length - end);
    }

    /// <summary>
    /// The "· +N" suffix reporting withheld content, or empty when nothing was withheld — marking
    /// every line would be as useless as marking none (D57).
    /// </summary>
    private static string MarkerFor(int withheldChars) =>
        withheldChars > 0 ? $" · +{FormatCharCount(withheldChars)}" : string.Empty;

    /// <summary>Formats a character count for the withheld-content marker: "999", "1k", "1.2k".</summary>
    internal static string FormatCharCount(int chars) =>
        chars < 1000
            ? chars.ToString(CultureInfo.InvariantCulture)
            : (chars / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";

    /// <summary>
    /// A session line carries no version marker, and the asymmetry with <see cref="FormatFactLine"/>
    /// is a property of the addressing rather than an omission.
    /// </summary>
    /// <remarks>
    /// A session note's subject path ends in a fingerprint of its own statement, so rewording one
    /// addresses a different path and starts its own history instead of extending the old note's —
    /// there is no earlier belief for the marker to point back at. The single route to a
    /// multi-version session handle is retracting a note and restating it verbatim, which builds a
    /// thread holding one sentence twice; marking that would announce history carrying nothing the
    /// line above it already says. Both halves are pinned by tests, because the cheap reading of
    /// this — "session notes are never superseded" — is false and would justify the wrong fix.
    ///
    /// <para>The withheld-chars marker is not part of that asymmetry: a session statement gets the
    /// same <see cref="TruncateBody"/> + marker treatment as a long-term body, through the same
    /// helpers <see cref="FormatFactLine"/> uses — a hook-captured verbatim statement can be long,
    /// and <c>engram_remember</c>'s own <c>details</c> lands in this tier, so an unmarked session
    /// line would be a hole in the ladder exactly where the model's own writes go.</para>
    /// </remarks>
    internal static string FormatSessionFactLine(SessionFact fact)
    {
        var (shown, withheldBodyChars) = TruncateBody(fact.Statement);
        var marker = MarkerFor(withheldBodyChars + fact.DetailsChars);
        var scope = string.IsNullOrWhiteSpace(fact.Agent) ? "session" : $"session · {fact.Agent}";
        return $"[{FactCatalog.HandleFor(fact.FactId)}] {shown} ({scope}{marker})";
    }

    // The session discriminator sits in the annotation rather than inside the handle, where
    // it used to read "[s001@p1]". A handle is what a tool takes back; overloading it with
    // grouping meant the string the model saw was not the string engram_forget accepts.
    internal static string FormatPriorSessionFactLine(SessionFact fact, string discriminator)
    {
        var (shown, withheldBodyChars) = TruncateBody(fact.Statement);
        var marker = MarkerFor(withheldBodyChars + fact.DetailsChars);
        var scope = string.IsNullOrWhiteSpace(fact.Agent)
            ? $"session · {discriminator} · {fact.AgeDays}d"
            : $"session · {discriminator} · {fact.Agent} · {fact.AgeDays}d";
        return $"[{FactCatalog.HandleFor(fact.FactId)}] {shown} ({scope}{marker})";
    }

    internal static string GapsMessage(string query, RecallCoverage coverage) => coverage switch
    {
        RecallCoverage.None => $"no facts matched \"{query}\" — discover and engram_remember what you find",
        _ => $"only partial matches for \"{query}\" — verify before relying on this",
    };

    internal static HashSet<string> TokenizeQuery(string query)
    {
        var terms = Tokenizer.Tokenize(query);

        var filtered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (Tokenizer.IsIndexable(term))
            {
                filtered.Add(term);
            }
        }

        return filtered.Count > 0 ? filtered : terms;
    }
}
