using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// The explainer's one obligation: describe the ranking that happens, not a second one that
/// resembles it.
/// </summary>
public class RecallExplanationTests
{
    private static readonly IReadOnlyList<CannedFact> Facts =
    [
        new("f11", "pragmas", "states", "Every connection sets its own pragmas, because they are connection-scoped.", "code", "sqlite", 3),
        new("f12", "transactions", "states", "Every write opens BEGIN IMMEDIATE so a deferred upgrade cannot raise BUSY_SNAPSHOT.", "code", "sqlite", 5),
        new("f13", "pragmas", "measured", "Opening the database costs one to one and a half milliseconds.", "code", "sqlite", 9),
        new("f14", "unrelated", "states", "Impressions are extractive by default.", "project", "recall", 40),
    ];

    private static readonly IReadOnlyList<SessionFact> Working =
    [
        new(91, 7, "The pragma guard cannot fail on foreign_keys alone.", "pragmas", null, 0),
    ];

    /// <summary>
    /// The anti-drift guard, and the reason <c>Explain</c> is not a separate implementation. If
    /// the two ever order or cut differently, the explainer is describing a ranking nobody runs —
    /// and it is the tool one would otherwise use to notice.
    /// </summary>
    [Theory]
    [InlineData("pragmas connection scoped", 500)]
    [InlineData("pragmas connection scoped", 60)]
    [InlineData("write transaction immediate", 500)]
    [InlineData("nothing matches this", 500)]
    public void Explain_ReportsExactlyWhatPackReturned(string query, int budget)
    {
        var pack = RecallEngine.Pack(query, Facts, Working, [], budget);
        var explanation = RecallEngine.Explain(query, Facts, Working, [], budget);

        var packedLines = explanation.Candidates.Where(c => c.Packed).Select(c => c.Line).ToList();
        var textLines = pack.Text.Split('\n').Skip(1).Take(pack.FactCount).ToList();

        Assert.Equal(textLines, packedLines);
        Assert.Equal(pack.FactCount, packedLines.Count);
        Assert.Equal(pack.TokensUsed, explanation.TokensUsed);
        Assert.Equal(pack.Coverage, explanation.Coverage);
    }

    [Fact]
    public void Explain_KeepsEveryCandidateTheBudgetCut()
    {
        var generous = RecallEngine.Explain("pragmas connection scoped", Facts, Working, [], 500);
        var mean = RecallEngine.Explain("pragmas connection scoped", Facts, Working, [], 40);

        Assert.Equal(generous.Candidates.Count, mean.Candidates.Count);
        Assert.True(mean.Candidates.Count(c => c.Packed) < generous.Candidates.Count(c => c.Packed));
        Assert.Contains(mean.Candidates, c => !c.Packed);
    }

    /// <summary>
    /// Packing tightly would let a short low-ranked fact jump a long high-ranked one, turning a
    /// ranked digest into a length-sorted one. The cut is a prefix.
    /// </summary>
    /// <remarks>
    /// The fixture is built so the two rules disagree: a long candidate first, a short one after
    /// it, and a budget that fits the short one alone. With ordinary facts they agree on every
    /// budget, which is how a test of this can pass while asserting nothing.
    /// </remarks>
    [Fact]
    public void Explain_ShowsTheBudgetCuttingAPrefix_NotASubset()
    {
        const int Budget = 30;
        IReadOnlyList<SessionFact> longNoteFirst =
        [
            new(99, 7, "Widget " + string.Join(' ', Enumerable.Repeat("elaboration", 40)), "widget", null, 0),
        ];
        IReadOnlyList<CannedFact> shortFactSecond =
        [
            new("f20", "widget", "states", "Widget works.", "code", "topic", 1),
        ];

        var explanation = RecallEngine.Explain("widget", shortFactSecond, longNoteFirst, [], Budget);

        Assert.Equal(2, explanation.Candidates.Count);
        Assert.True(explanation.Candidates[0].Tokens > Budget, "the first candidate must not fit");
        Assert.True(explanation.Candidates[1].Tokens <= Budget, "the second candidate must fit on its own");

        Assert.False(explanation.Candidates[0].Packed);
        Assert.False(explanation.Candidates[1].Packed);
        Assert.Equal(0, explanation.TokensUsed);
    }

    [Fact]
    public void Explain_NamesTheTermsTheFilterDiscarded()
    {
        var explanation = RecallEngine.Explain("what is the pragma", Facts, Working, [], 500);

        Assert.Equal(["pragma"], explanation.QueryTerms);
        Assert.Equal(["is", "the", "what"], explanation.DroppedTerms);
    }

    [Fact]
    public void Explain_WhenEveryTermIsFiltered_ReportsTheFallbackTermsAsUsed()
    {
        // TokenizeQuery falls back to the raw terms rather than matching nothing, so a query of
        // pure stopwords still searches — and nothing was dropped.
        var explanation = RecallEngine.Explain("is the a", Facts, Working, [], 500);

        Assert.NotEmpty(explanation.QueryTerms);
        Assert.Empty(explanation.DroppedTerms);
    }

    [Fact]
    public void Explain_PutsWorkingMemoryFirstRegardlessOfScore()
    {
        var explanation = RecallEngine.Explain("pragmas connection scoped", Facts, Working, [], 500);

        var first = explanation.Candidates[0];
        Assert.Equal(FactOrigin.CurrentSession, first.Origin);
        Assert.DoesNotContain(explanation.Candidates.Skip(1), c => c.Origin == FactOrigin.CurrentSession);
    }

    [Fact]
    public void Explain_CarriesTheStoreIdBehindEachHandle()
    {
        var explanation = RecallEngine.Explain("pragmas connection scoped", Facts, Working, [], 500);

        foreach (var candidate in explanation.Candidates)
        {
            Assert.True(FactCatalog.TryParseHandle(candidate.Handle, out var parsed));
            Assert.Equal(parsed, candidate.FactId);
        }
    }

    [Fact]
    public void Explain_WhenNothingMatches_ReportsNoCandidatesRatherThanFailing()
    {
        var explanation = RecallEngine.Explain("kubernetes ingress", Facts, Working, [], 500);

        Assert.Empty(explanation.Candidates);
        Assert.Equal(RecallCoverage.None, explanation.Coverage);
        Assert.Equal(0, explanation.TokensUsed);
    }

    /// <summary>
    /// The fusion arithmetic, spelled out once so the constant is not a mystery number: each lane
    /// contributes <c>1/(60 + rank)</c> and the lanes are summed.
    /// </summary>
    [Fact]
    public void Explain_ScoresACandidateAsTheSumOfItsReciprocalRanks()
    {
        // f11 is the only fact matching both terms, so it is the overlap lane's first hit.
        var lexical = new Dictionary<long, int> { [11] = 1 };

        var explanation = RecallEngine.Explain("pragmas connection scoped", Facts, [], [], lexical, 500);

        var fused = Assert.Single(explanation.Candidates, c => c.FactId == 11);
        Assert.Equal(1, fused.OverlapRank);
        Assert.Equal(1, fused.LexicalRank);
        Assert.Equal((1d / 61) + (1d / 61), fused.Fused, 12);
    }

    [Fact]
    public void Explain_IncludesAFactOnlyTheLexicalLaneFound()
    {
        // f14 shares no term with the query; only the lexical lane returns it.
        var lexical = new Dictionary<long, int> { [14] = 1 };

        var explanation = RecallEngine.Explain("pragmas connection scoped", Facts, [], [], lexical, 500);

        var rescued = Assert.Single(explanation.Candidates, c => c.FactId == 14);
        Assert.Null(rescued.OverlapRank);
        Assert.Equal(1, rescued.LexicalRank);
        Assert.Equal(1d / 61, rescued.Fused, 12);
    }

    [Fact]
    public void Explain_RanksAgreementAboveAGoodPositionInOneLane()
    {
        // f12 is the lexical lane's top hit and nothing else; f11 is second there and first on
        // overlap. Agreement has to win, or k is doing nothing.
        var lexical = new Dictionary<long, int> { [12] = 1, [11] = 2 };

        var explanation = RecallEngine.Explain("pragmas connection scoped", Facts, [], [], lexical, 500);

        Assert.Equal(11, explanation.Candidates[0].FactId);
        Assert.Equal(12, explanation.Candidates[1].FactId);
    }

    [Fact]
    public void Explain_WithNoLexicalLane_RanksByOverlapAlone()
    {
        var explanation = RecallEngine.Explain("pragmas connection scoped", Facts, [], [], 500);

        Assert.All(explanation.Candidates, c => Assert.Null(c.LexicalRank));
        Assert.Equal(
            explanation.Candidates.OrderBy(c => c.OverlapRank).Select(c => c.Handle),
            explanation.Candidates.Select(c => c.Handle));
    }

    [Fact]
    public void Explain_CountsTokensPerCandidateTheSameWayTheBudgetDoes()
    {
        var explanation = RecallEngine.Explain("pragmas connection scoped", Facts, Working, [], 500);

        Assert.Equal(
            explanation.Candidates.Where(c => c.Packed).Sum(c => c.Tokens),
            explanation.TokensUsed);
    }
}
