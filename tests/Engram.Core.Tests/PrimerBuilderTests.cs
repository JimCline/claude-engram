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
    public void Build_IncludesInstructionAndCoverageLine()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        Assert.Contains("engram_recall", primer);
        Assert.Contains($"Memory holds {CannedFacts.All.Count} facts", primer);
    }

    [Fact]
    public void Build_CoverageLine_TotalMatchesCorpusSize()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        Assert.Contains($"holds {CannedFacts.All.Count} facts", primer);
    }

    [Fact]
    public void Build_RealCannedFacts_HasAtMostTwoFactLines()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        var factLineCount = primer.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));

        Assert.True(factLineCount <= 2);
    }

    [Fact]
    public void Build_RealCannedFacts_UsesFullExampleBudgetOfTwo()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        var factLineCount = primer.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));

        Assert.Equal(2, factLineCount);
    }

    [Fact]
    public void Build_NoFactsAtAll_EmitsInstructionAlone()
    {
        var primer = PrimerBuilder.Build(Array.Empty<CannedFact>());

        Assert.DoesNotContain('\n', primer);
        Assert.Contains("engram_recall", primer);
    }

    [Fact]
    public void Build_ExamplesSectionThatCannotFitEvenOneLine_ProducesNoHeading()
    {
        var facts = new List<CannedFact>
        {
            new("f001", "subject", "predicate", new string('a', 880), "user", "topic", 0),
        };

        var primer = PrimerBuilder.Build(facts);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
        Assert.DoesNotContain("Examples:", primer);
    }

    [Fact]
    public void Build_NeverExceedsBudgetRegardlessOfInputSize()
    {
        var facts = Enumerable.Range(1, 200)
            .Select(i => new CannedFact($"f{i:D4}", $"subject{i}", "predicate", new string('a', 200), i % 2 == 0 ? "user" : "project", "topic", 0))
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
                .Select(i => new CannedFact($"{scope}{i:D4}", $"subject{scope}{i}", "predicate", new string('a', 200), scope, "topic", 0)))
            .ToList();

        var primer = PrimerBuilder.Build(facts);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
    }

    [Fact]
    public void CoverageLine_NewTopicInSyntheticCorpus_ChangesWithoutCodeChange()
    {
        var facts = new List<CannedFact>
        {
            new("f001", "widget-alpha", "states", "widget alpha body", "user", "widget", 0),
            new("f002", "widget-beta", "states", "widget beta body", "user", "widget", 0),
            new("f003", "gizmo-gamma", "states", "gizmo gamma body", "project", "gizmo", 0),
        };

        var primer = PrimerBuilder.Build(facts);

        Assert.Contains("widget (2)", primer);
        Assert.Contains("gizmo (1)", primer);
    }

    [Fact]
    public void CoverageLine_ClustersOrderedByCountDescending()
    {
        var facts = new List<CannedFact>
        {
            new("f001", "alpha-one", "states", "alpha body one", "user", "alpha", 0),
            new("f002", "alpha-two", "states", "alpha body two", "user", "alpha", 0),
            new("f003", "beta-one", "states", "beta body one", "user", "beta", 0),
        };

        var primer = PrimerBuilder.Build(facts);
        var coverageLine = primer.Split('\n').Single(line => line.StartsWith("Memory holds", StringComparison.Ordinal));

        Assert.True(coverageLine.IndexOf("alpha (2)", StringComparison.Ordinal) < coverageLine.IndexOf("beta (1)", StringComparison.Ordinal));
    }

    [Fact]
    public void CoverageLine_CapsClusterListAndSummarizesTail()
    {
        var facts = Enumerable.Range(1, 8)
            .Select(i => new CannedFact($"f{i:D3}", $"topic{i}-detail", "states", $"body {i}", "user", $"topic{i}", 0))
            .ToList();

        var primer = PrimerBuilder.Build(facts);
        var coverageLine = primer.Split('\n').Single(line => line.StartsWith("Memory holds", StringComparison.Ordinal));

        Assert.Contains("+3 more", coverageLine);
    }
}
