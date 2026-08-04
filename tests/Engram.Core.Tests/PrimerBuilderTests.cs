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
    public void Build_IncludesUserSectionAndInstruction()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        Assert.Contains("User:", primer);
        Assert.Contains("engram_recall", primer);
    }

    [Fact]
    public void Build_IncludesProjectSection()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        Assert.Contains("Project:", primer);
    }

    [Fact]
    public void Build_ScopeWithNoFacts_ProducesNoHeading()
    {
        var facts = new List<CannedFact>
        {
            new("f001", "subject", "predicate", "body", "user", 0),
        };

        var primer = PrimerBuilder.Build(facts);

        Assert.DoesNotContain("Project:", primer);
        Assert.DoesNotContain("Code:", primer);
        Assert.DoesNotContain("Session:", primer);
    }

    [Fact]
    public void Build_NoFactsAtAll_EmitsInstructionAlone()
    {
        var primer = PrimerBuilder.Build(Array.Empty<CannedFact>());

        Assert.DoesNotContain('\n', primer);
        Assert.Contains("engram_recall", primer);
    }

    [Fact]
    public void Build_SectionThatCannotFitEvenOneLine_ProducesNoHeading()
    {
        var facts = new List<CannedFact>
        {
            new("f001", "subject", "predicate", new string('a', 880), "user", 0),
            new("f002", "subject", "predicate", "short project fact", "project", 0),
        };

        var primer = PrimerBuilder.Build(facts);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
        Assert.DoesNotContain("Project:", primer);
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

    [Fact]
    public void Build_AllScopesPopulated_StaysUnderMaxTokens()
    {
        var scopes = new[] { "user", "project", "code", "session", "team" };
        var facts = scopes
            .SelectMany(scope => Enumerable.Range(1, 40)
                .Select(i => new CannedFact($"{scope}{i:D4}", "subject", "predicate", new string('a', 200), scope, 0)))
            .ToList();

        var primer = PrimerBuilder.Build(facts);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
    }
}
