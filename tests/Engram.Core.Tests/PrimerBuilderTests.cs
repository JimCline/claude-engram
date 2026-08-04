using Engram.Core;

namespace Engram.Core.Tests;

public class PrimerBuilderTests
{
    [Fact]
    public void Build_RealCannedFacts_StaysUnderMaxTokens()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
    }

    [Fact]
    public void Build_IncludesUserAndProjectSections()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        Assert.Contains("User:", primer);
        Assert.Contains("Project:", primer);
        Assert.Contains("engram_recall", primer);
    }

    [Fact]
    public void Build_NeverExceedsBudgetRegardlessOfInputSize()
    {
        var facts = Enumerable.Range(1, 200)
            .Select(i => new CannedFact($"f{i:D4}", "subject", "predicate", new string('a', 200), i % 2 == 0 ? "user" : "project", 0))
            .ToList();

        var primer = PrimerBuilder.Build(facts);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
    }
}
