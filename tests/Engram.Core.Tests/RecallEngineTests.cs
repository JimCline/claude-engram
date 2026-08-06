using Engram.Core;

namespace Engram.Core.Tests;

public class RecallEngineTests
{
    private static readonly IReadOnlyList<CannedFact> Facts =
    [
        new("f001", "aot-packaging", "measured", "Native AOT publish is zero-warning for the MCP SDK.", "code", "topic", 0),
        new("f002", "aot-packaging", "decided", "The core stays AOT; Roslyn ships as a sidecar.", "project", "topic", 0),
        new("f003", "roslyn-sidecar", "decided", "Roslyn never opens the database directly.", "project", "topic", 0),
        new("f004", "unrelated-topic", "states", "Salience recomputes lazily on read.", "code", "topic", 0),
    ];

    [Fact]
    public void Rank_OrdersByMatchCountDescendingThenIdAscending()
    {
        var ranked = RecallEngine.Rank("aot packaging roslyn", Facts);

        Assert.True(ranked.Count >= 2);
        Assert.True(ranked[0].Score >= ranked[^1].Score);
        for (var i = 1; i < ranked.Count; i++)
        {
            Assert.True(
                ranked[i - 1].Score > ranked[i].Score ||
                (ranked[i - 1].Score == ranked[i].Score &&
                 string.CompareOrdinal(ranked[i - 1].Fact.Id, ranked[i].Fact.Id) < 0));
        }
    }

    [Fact]
    public void Rank_IsCaseInsensitive()
    {
        var lower = RecallEngine.Rank("aot packaging", Facts);
        var upper = RecallEngine.Rank("AOT PACKAGING", Facts);

        Assert.Equal(lower.Select(r => r.Fact.Id), upper.Select(r => r.Fact.Id));
    }

    [Fact]
    public void Rank_NoOverlap_ReturnsEmpty()
    {
        var ranked = RecallEngine.Rank("zzqqxxnonexistentquery12345", Facts);

        Assert.Empty(ranked);
    }

    [Theory]
    [InlineData(0, 0, RecallCoverage.None)]
    [InlineData(1, 0, RecallCoverage.Partial)]
    [InlineData(2, 1, RecallCoverage.Partial)]
    [InlineData(3, 3, RecallCoverage.High)]
    [InlineData(10, 4, RecallCoverage.High)]
    public void ClassifyCoverage_UsesLaneAgreementThresholds(int matchedCount, int corroborated, RecallCoverage expected)
    {
        Assert.Equal(expected, RecallEngine.ClassifyCoverage(matchedCount, corroborated));
    }

    /// <summary>
    /// A pile of single-lane hits is not coverage, however tall it is.
    /// </summary>
    /// <remarks>
    /// The case this rule exists for, measured on the author's store: "weekend saturday personal
    /// activity outing" returned seven candidates, six of them engineering notes about lint tests
    /// and <c>BEGIN IMMEDIATE</c> that bm25 reached through a shared stem, and the count called it
    /// <c>high</c> — which is the value that suppresses the <c>gaps:</c> line. The model was told
    /// memory had this covered.
    /// </remarks>
    [Fact]
    public void ClassifyCoverage_ManyCandidatesButNoLaneAgrees_IsNotHigh()
    {
        Assert.Equal(RecallCoverage.Partial, RecallEngine.ClassifyCoverage(matchedFactCount: 7, corroboratedCount: 1));
    }

    /// <summary>
    /// <c>none</c> still means the store said nothing, not that nothing was corroborated.
    /// </summary>
    /// <remarks>
    /// It selects a different response shape — under five lines, per the spec — so returning facts
    /// beneath it would be a worse lie than the one lane agreement was introduced to fix.
    /// </remarks>
    [Fact]
    public void ClassifyCoverage_UncorroboratedCandidatesAreStillCandidates()
    {
        Assert.Equal(RecallCoverage.Partial, RecallEngine.ClassifyCoverage(matchedFactCount: 2, corroboratedCount: 0));
        Assert.Equal(RecallCoverage.None, RecallEngine.ClassifyCoverage(matchedFactCount: 0, corroboratedCount: 0));
    }

    /// <summary>
    /// One lane is not agreement. This is the boundary the whole rule rests on.
    /// </summary>
    [Fact]
    public void Corroborated_CountsOnlyCandidatesMoreThanOneLaneFound()
    {
        RecallCandidate Found(long id, int? overlap, int? lexical, int? vector) => new(
            FactId: id, Handle: $"f{id}", Line: "x", Fused: 0.1,
            OverlapRank: overlap, LexicalRank: lexical, VectorRank: vector,
            Origin: FactOrigin.LongTerm, Tokens: 4, Packed: true);

        // The measured shape of a thin result: a pile of lone bm25 hits behind one real match.
        var candidates = new List<RecallCandidate>
        {
            Found(1, overlap: 1, lexical: 1, vector: null),
            Found(2, overlap: null, lexical: 2, vector: null),
            Found(3, overlap: null, lexical: 3, vector: null),
            Found(4, overlap: null, lexical: 4, vector: null),
            Found(5, overlap: 2, lexical: null, vector: null),
        };

        Assert.Equal(1, RecallEngine.Corroborated(candidates));
        Assert.Equal(
            RecallCoverage.Partial,
            RecallEngine.ClassifyCoverage(candidates.Count, RecallEngine.Corroborated(candidates)));
    }

    [Fact]
    public void LanesThatFound_CountsEachLaneOnce()
    {
        var candidate = new RecallCandidate(
            FactId: 1, Handle: "f1", Line: "x", Fused: 0.1,
            OverlapRank: 1, LexicalRank: 2, VectorRank: null,
            Origin: FactOrigin.LongTerm, Tokens: 4, Packed: true);

        Assert.Equal(2, RecallEngine.LanesThatFound(candidate));
        Assert.Equal(0, RecallEngine.LanesThatFound(candidate with { OverlapRank = null, LexicalRank = null }));
    }

    [Fact]
    public void Pack_NonsenseQuery_ReturnsNoneCoverageInUnderFiveLines()
    {
        var result = RecallEngine.Pack("zzqqxxnonexistentquery12345", Facts, RecallEngine.DefaultBudgetTokens);

        Assert.Equal(RecallCoverage.None, result.Coverage);
        Assert.Equal(0, result.FactCount);
        Assert.True(result.Text.Split('\n').Length < 5);
        Assert.Contains("coverage: none", result.Text);
    }

    [Fact]
    public void Pack_MatchingQuery_IncludesHandleAndCoverage()
    {
        var result = RecallEngine.Pack("aot packaging and roslyn", Facts, RecallEngine.DefaultBudgetTokens);

        Assert.True(result.FactCount > 0);
        Assert.Contains("[f", result.Text);
        Assert.Contains("coverage:", result.Text);
    }

    [Fact]
    public void Pack_TruncatesToBudget_NeverExceedingIt()
    {
        var manyFacts = Enumerable.Range(1, 20)
            .Select(i => new CannedFact($"f{i:D3}", "aot-packaging", "decided", $"AOT packaging fact number {i} about roslyn sidecars.", "project", "topic", 0))
            .ToList();

        var result = RecallEngine.Pack("aot packaging roslyn", manyFacts, budgetTokens: 50);

        Assert.True(result.TokensUsed <= 50);
        Assert.True(result.FactCount < manyFacts.Count);
    }

    [Fact]
    public void Pack_ZeroBudget_IncludesNoFactLines()
    {
        var result = RecallEngine.Pack("aot packaging roslyn", Facts, budgetTokens: 0);

        Assert.Equal(0, result.FactCount);
        Assert.Equal(0, result.TokensUsed);
    }

    [Fact]
    public void Pack_CoverageBelowHigh_IncludesGapsLine()
    {
        var result = RecallEngine.Pack("roslyn sidecar", Facts, RecallEngine.DefaultBudgetTokens);

        if (result.Coverage != RecallCoverage.High)
        {
            Assert.Contains("gaps:", result.Text);
        }
    }

    [Fact]
    public void Rank_QueryWithStopword_ExcludesFactsMatchedSolelyByTheStopword()
    {
        var stopwordFacts = new List<CannedFact>
        {
            new("f101", "topic-a", "states", "Hooks and plugins interact through the settings file.", "user", "topic", 0),
            new("f102", "topic-b", "states", "The settings file is read at startup and cached.", "user", "topic", 0),
        };

        var ranked = RecallEngine.Rank("hooks and plugins", stopwordFacts);

        var matchedIds = ranked.Select(r => r.Fact.Id).ToHashSet();
        Assert.Contains("f101", matchedIds);
        Assert.DoesNotContain("f102", matchedIds);
    }

    [Fact]
    public void Rank_QueryOfOnlyStopwords_FallsBackToUnfilteredTermsRatherThanCrashing()
    {
        var ranked = RecallEngine.Rank("the a of", CannedFacts.All);

        Assert.NotEmpty(ranked);
    }

    [Fact]
    public void Pack_SessionFactAlwaysRanksAboveLongTermFact_EvenWhenLongTermFactScoresHigher()
    {
        var longTermFacts = new List<CannedFact>
        {
            new("f900", "wal-starvation", "decided", "WAL starvation retry backoff decided after incident review.", "project", "topic", 0),
        };
        var sessionFacts = new List<SessionFact>
        {
            new(FactId: 901, SessionId: 1, "Checked the WAL theory, not the cause.", Subject: null, Agent: null, AgeDays: 0),
        };

        var ranked = RecallEngine.RankSessionFacts("wal starvation retry backoff", sessionFacts);
        var rankedLongTerm = RecallEngine.Rank("wal starvation retry backoff", longTermFacts);
        Assert.True(rankedLongTerm[0].Score > ranked[0].Score, "fixture must have the long-term fact score higher than the session fact");

        var result = RecallEngine.Pack("wal starvation retry backoff", longTermFacts, sessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Equal(1, result.SessionFactCount);
        Assert.Equal(1, result.LongTermFactCount);

        var sessionIndex = result.Text.IndexOf("[f901]", StringComparison.Ordinal);
        var longTermIndex = result.Text.IndexOf("[f900]", StringComparison.Ordinal);

        Assert.True(sessionIndex >= 0);
        Assert.True(longTermIndex >= 0);
        Assert.True(sessionIndex < longTermIndex);
    }

    [Fact]
    public void Pack_SessionFactLine_ShowsSessionScopeAndAgentName()
    {
        var sessionFacts = new List<SessionFact>
        {
            new(FactId: 901, SessionId: 1, "Ran the migration dry-run against staging.", Subject: null, Agent: "migration-worker", AgeDays: 0),
        };

        var result = RecallEngine.Pack("migration dry-run staging", [], sessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Contains("[f901] Ran the migration dry-run against staging. (session · migration-worker)", result.Text);
    }

    [Fact]
    public void Pack_CurrentSessionFactAlwaysRanksAbovePriorSessionFact_EvenWhenPriorSessionFactScoresHigher()
    {
        var currentSessionFacts = new List<SessionFact>
        {
            new(FactId: 901, SessionId: 1, "Checked the WAL theory, not the cause.", Subject: null, Agent: null, AgeDays: 0),
        };
        var priorSessionFacts = new List<SessionFact>
        {
            new(FactId: 902, SessionId: 2, "WAL starvation retry backoff decided after incident review.", Subject: null, Agent: null, AgeDays: 34),
        };

        var rankedCurrent = RecallEngine.RankSessionFacts("wal starvation retry backoff", currentSessionFacts);
        var rankedPrior = RecallEngine.RankSessionFacts("wal starvation retry backoff", priorSessionFacts);
        Assert.True(rankedPrior[0].Score > rankedCurrent[0].Score, "fixture must have the prior-session fact score higher than the current-session fact");

        var result = RecallEngine.Pack("wal starvation retry backoff", [], currentSessionFacts, priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Equal(1, result.SessionFactCount);
        Assert.Equal(1, result.PriorSessionFactCount);

        var currentIndex = result.Text.IndexOf("[f901]", StringComparison.Ordinal);
        var priorIndex = result.Text.IndexOf("[f902]", StringComparison.Ordinal);

        Assert.True(currentIndex >= 0);
        Assert.True(priorIndex >= 0);
        Assert.True(currentIndex < priorIndex);
    }

    [Fact]
    public void Pack_PriorSessionFactCanOutrankAWeaklyMatchingLongTermFact()
    {
        var longTermFacts = new List<CannedFact>
        {
            new("f900", "storage-engine", "states", "The WAL is flushed to disk before commit.", "code", "topic", 0),
        };
        var priorSessionFacts = new List<SessionFact>
        {
            new(FactId: 901, SessionId: 2, "WAL starvation retry backoff decided after incident review.", Subject: null, Agent: null, AgeDays: 34),
        };

        var rankedLongTerm = RecallEngine.Rank("wal starvation retry backoff", longTermFacts);
        var rankedPrior = RecallEngine.RankSessionFacts("wal starvation retry backoff", priorSessionFacts);
        Assert.True(rankedPrior[0].Score > rankedLongTerm[0].Score, "fixture must have the prior-session fact score higher than the long-term fact");

        var result = RecallEngine.Pack("wal starvation retry backoff", longTermFacts, [], priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        var priorIndex = result.Text.IndexOf("[f901]", StringComparison.Ordinal);
        var longTermIndex = result.Text.IndexOf("[f900]", StringComparison.Ordinal);

        Assert.True(priorIndex >= 0);
        Assert.True(longTermIndex >= 0);
        Assert.True(priorIndex < longTermIndex);
    }

    [Fact]
    public void Pack_PriorSessionFactLine_ShowsAgeInDaysAndAgentName()
    {
        var priorSessionFacts = new List<SessionFact>
        {
            new(FactId: 901, SessionId: 2, "Ran the migration dry-run against staging.", Subject: null, Agent: "migration-worker", AgeDays: 3),
        };

        var result = RecallEngine.Pack("migration dry-run staging", [], [], priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Contains("[f901] Ran the migration dry-run against staging. (session · p1 · migration-worker · 3d)", result.Text);
    }

    // Handles are globally unique now, so they no longer collide the way "s001" in two
    // sessions did — but they also no longer say which notes came from one sitting, which is
    // the whole reason the discriminator survived the move onto the store.
    [Fact]
    public void Pack_PriorSessionDiscriminator_GroupsNotesBySessionRatherThanByFact()
    {
        var priorSessionFacts = new List<SessionFact>
        {
            new(FactId: 901, SessionId: 7, "Alpha session noted the backup window.", Subject: null, Agent: null, AgeDays: 4),
            new(FactId: 902, SessionId: 7, "Alpha session also timed the backup window.", Subject: null, Agent: null, AgeDays: 4),
            new(FactId: 903, SessionId: 9, "Beta session widened the backup window.", Subject: null, Agent: null, AgeDays: 3),
        };

        var result = RecallEngine.Pack("backup window", [], [], priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Contains("[f901] Alpha session noted the backup window. (session · p1 · 4d)", result.Text);
        Assert.Contains("[f902] Alpha session also timed the backup window. (session · p1 · 4d)", result.Text);
        Assert.Contains("[f903] Beta session widened the backup window. (session · p2 · 3d)", result.Text);
        Assert.Equal(3, result.PriorSessionFactCount);
    }
}
