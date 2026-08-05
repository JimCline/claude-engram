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
    public void Build_IncludesCoverageLine()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        Assert.Contains($"Memory holds {CannedFacts.All.Count} facts", primer);
    }

    // D15: standing guidance belongs in the tool descriptions, which persist for the whole
    // session, not in the primer, which is ordinary context and is summarized away by
    // compaction. A tool name appearing in the guidance lines means it has drifted back
    // into the channel that loses it. Example fact bodies are exempt — they are stored
    // content, not instruction, and a fact is allowed to mention a tool.
    [Theory]
    [InlineData("engram_recall")]
    [InlineData("engram_remember")]
    [InlineData("engram_digest")]
    public void Build_GuidanceLines_DoNotRestateToolDescriptions(string toolName)
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        var guidance = string.Join(
            '\n',
            primer.Split('\n').TakeWhile(l => !l.StartsWith("Examples:", StringComparison.Ordinal)));

        Assert.DoesNotContain(toolName, guidance);
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

    // With the standing guidance gone to the tool descriptions, an empty store leaves the
    // primer with nothing to report. HookCommand relies on this being empty rather than
    // whitespace to decide not to emit additionalContext at all.
    [Fact]
    public void Build_NoFactsAtAll_EmitsNothing()
    {
        var primer = PrimerBuilder.Build(Array.Empty<CannedFact>());

        Assert.Equal(string.Empty, primer);
    }

    [Fact]
    public void Build_ExamplesSectionThatCannotFitEvenOneLine_ProducesNoHeading()
    {
        // Sized from the budget rather than hardcoded. A fixed length silently stopped
        // being oversized when the standing guidance moved out of the primer (D15) and
        // handed ~40 tokens back, which turned this into a test that could no longer fail.
        var farOverBudget = new string('a', PrimerBuilder.MaxTokens * 8);

        var facts = new List<CannedFact>
        {
            new("f001", "subject", "predicate", farOverBudget, "user", "topic", 0),
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
