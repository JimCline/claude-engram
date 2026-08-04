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
        var ranked = RecallEngine.Rank("AOT packaging and Roslyn", CannedFacts.All);

        Assert.True(ranked.Count < 14, $"expected fewer than 14 matches now that 'and' is filtered, got {ranked.Count}");

        var matchedIds = ranked.Select(r => r.Fact.Id).ToHashSet();
        var stopwordOnlyMatches = new[] { "f002", "f004", "f008", "f018", "f020", "f022", "f025", "f027", "f028" };
        foreach (var id in stopwordOnlyMatches)
        {
            Assert.DoesNotContain(id, matchedIds);
        }
    }

    [Fact]
    public void Rank_QueryOfOnlyStopwords_FallsBackToUnfilteredTermsRatherThanCrashing()
    {
        var ranked = RecallEngine.Rank("the a of", CannedFacts.All);

        Assert.NotEmpty(ranked);
    }
}
