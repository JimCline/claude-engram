using Engram.Core;

namespace Engram.Core.Tests;

public class RecallEngineTests
{
    private static readonly IReadOnlyList<CannedFact> Facts =
    [
        new("f001", "aot-packaging", "measured", "Native AOT publish is zero-warning for the MCP SDK.", "code", 0),
        new("f002", "aot-packaging", "decided", "The core stays AOT; Roslyn ships as a sidecar.", "project", 0),
        new("f003", "roslyn-sidecar", "decided", "Roslyn never opens the database directly.", "project", 0),
        new("f004", "unrelated-topic", "states", "Salience recomputes lazily on read.", "code", 0),
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
    [InlineData(0, RecallCoverage.None)]
    [InlineData(1, RecallCoverage.Partial)]
    [InlineData(2, RecallCoverage.Partial)]
    [InlineData(3, RecallCoverage.High)]
    [InlineData(10, RecallCoverage.High)]
    public void ClassifyCoverage_UsesMatchedFactCountThresholds(int matchedCount, RecallCoverage expected)
    {
        Assert.Equal(expected, RecallEngine.ClassifyCoverage(matchedCount));
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
            .Select(i => new CannedFact($"f{i:D3}", "aot-packaging", "decided", $"AOT packaging fact number {i} about roslyn sidecars.", "project", 0))
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
            new("f101", "topic-a", "states", "Hooks and plugins interact through the settings file.", "user", 0),
            new("f102", "topic-b", "states", "The settings file is read at startup and cached.", "user", 0),
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
            new("f900", "wal-starvation", "decided", "WAL starvation retry backoff decided after incident review.", "project", 0),
        };
        var sessionFacts = new List<SessionFactRecord>
        {
            new("s001", "2026-08-04T00:00:00Z", "sess-1", "Checked the WAL theory, not the cause."),
        };

        var ranked = RecallEngine.RankSessionFacts("wal starvation retry backoff", sessionFacts);
        var rankedLongTerm = RecallEngine.Rank("wal starvation retry backoff", longTermFacts);
        Assert.True(rankedLongTerm[0].Score > ranked[0].Score, "fixture must have the long-term fact score higher than the session fact");

        var result = RecallEngine.Pack("wal starvation retry backoff", longTermFacts, sessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Equal(1, result.SessionFactCount);
        Assert.Equal(1, result.LongTermFactCount);

        var sessionIndex = result.Text.IndexOf("[s001]", StringComparison.Ordinal);
        var longTermIndex = result.Text.IndexOf("[f900]", StringComparison.Ordinal);

        Assert.True(sessionIndex >= 0);
        Assert.True(longTermIndex >= 0);
        Assert.True(sessionIndex < longTermIndex);
    }

    [Fact]
    public void Pack_SessionFactLine_ShowsSessionScopeAndAgentName()
    {
        var sessionFacts = new List<SessionFactRecord>
        {
            new("s001", "2026-08-04T00:00:00Z", "sess-1", "Ran the migration dry-run against staging.", Agent: "migration-worker"),
        };

        var result = RecallEngine.Pack("migration dry-run staging", [], sessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Contains("[s001] Ran the migration dry-run against staging. (session · migration-worker)", result.Text);
    }

    [Fact]
    public void Pack_CurrentSessionFactAlwaysRanksAbovePriorSessionFact_EvenWhenPriorSessionFactScoresHigher()
    {
        var currentSessionFacts = new List<SessionFactRecord>
        {
            new("s001", "2026-08-04T00:00:00Z", "sess-current", "Checked the WAL theory, not the cause."),
        };
        var priorSessionFacts = new List<SessionFactRecord>
        {
            new("s001", "2026-07-01T00:00:00Z", "sess-prior", "WAL starvation retry backoff decided after incident review."),
        };

        var rankedCurrent = RecallEngine.RankSessionFacts("wal starvation retry backoff", currentSessionFacts);
        var rankedPrior = RecallEngine.RankSessionFacts("wal starvation retry backoff", priorSessionFacts);
        Assert.True(rankedPrior[0].Score > rankedCurrent[0].Score, "fixture must have the prior-session fact score higher than the current-session fact");

        var result = RecallEngine.Pack("wal starvation retry backoff", [], currentSessionFacts, priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Equal(1, result.SessionFactCount);
        Assert.Equal(1, result.PriorSessionFactCount);

        var currentIndex = result.Text.IndexOf("[s001] ", StringComparison.Ordinal);
        var priorIndex = result.Text.IndexOf("[s001@p1]", StringComparison.Ordinal);

        Assert.True(currentIndex >= 0);
        Assert.True(priorIndex >= 0);
        Assert.True(currentIndex < priorIndex);
    }

    [Fact]
    public void Pack_PriorSessionFactCanOutrankAWeaklyMatchingLongTermFact()
    {
        var longTermFacts = new List<CannedFact>
        {
            new("f900", "storage-engine", "states", "The WAL is flushed to disk before commit.", "code", 0),
        };
        var priorSessionFacts = new List<SessionFactRecord>
        {
            new("s001", "2026-07-01T00:00:00Z", "sess-prior", "WAL starvation retry backoff decided after incident review."),
        };

        var rankedLongTerm = RecallEngine.Rank("wal starvation retry backoff", longTermFacts);
        var rankedPrior = RecallEngine.RankSessionFacts("wal starvation retry backoff", priorSessionFacts);
        Assert.True(rankedPrior[0].Score > rankedLongTerm[0].Score, "fixture must have the prior-session fact score higher than the long-term fact");

        var result = RecallEngine.Pack("wal starvation retry backoff", longTermFacts, [], priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        var priorIndex = result.Text.IndexOf("[s001@p1]", StringComparison.Ordinal);
        var longTermIndex = result.Text.IndexOf("[f900]", StringComparison.Ordinal);

        Assert.True(priorIndex >= 0);
        Assert.True(longTermIndex >= 0);
        Assert.True(priorIndex < longTermIndex);
    }

    [Fact]
    public void Pack_PriorSessionFactLine_ShowsAgeInDaysAndAgentName()
    {
        var timestamp = DateTimeOffset.UtcNow.AddDays(-3).ToString("o");
        var priorSessionFacts = new List<SessionFactRecord>
        {
            new("s001", timestamp, "sess-prior", "Ran the migration dry-run against staging.", Agent: "migration-worker"),
        };

        var result = RecallEngine.Pack("migration dry-run staging", [], [], priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Contains("[s001@p1] Ran the migration dry-run against staging. (session · migration-worker · 3d)", result.Text);
    }

    [Fact]
    public void Pack_TwoDistinctPriorSessionsShareAHandleNumber_DiscriminatorsKeepThemDistinguishable()
    {
        var priorSessionFacts = new List<SessionFactRecord>
        {
            new("s001", "2026-08-01T00:00:00Z", "sess-alpha", "Alpha session noted the backup window."),
            new("s001", "2026-08-02T00:00:00Z", "sess-beta", "Beta session also noted the backup window."),
        };

        var result = RecallEngine.Pack("backup window", [], [], priorSessionFacts, RecallEngine.DefaultBudgetTokens);

        Assert.Contains("[s001@p1]", result.Text);
        Assert.Contains("[s001@p2]", result.Text);
        Assert.Equal(2, result.PriorSessionFactCount);
    }
}
