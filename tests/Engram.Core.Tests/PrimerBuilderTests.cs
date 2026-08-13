using Engram.Core;

namespace Engram.Core.Tests;

public class PrimerBuilderTests
{
    // The shipped default, so the budget tests below are measured against what users actually
    // get rather than against the cheapest configuration.
    private const MemoryPrecedence Shipped = MemorySettings.DefaultPrecedence;

    [Fact]
    public void Build_RealCannedFacts_StaysUnderMaxTokens()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
    }

    [Fact]
    public void Build_IncludesCoverageLine()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped);

        Assert.Contains($"Memory holds {CannedFacts.All.Count} facts", primer);
    }

    // D15: standing guidance belongs in the tool descriptions, which persist for the whole
    // session, not in the primer, which is ordinary context and is summarized away by
    // compaction. A tool name appearing in the guidance lines means it has drifted back
    // into the channel that loses it. Example fact bodies are exempt — they are stored
    // content, not instruction, and a fact is allowed to mention a tool.
    //
    // The precedence line is the one argued exception (D51) and is subtracted by identity
    // rather than by pattern: it says which store to write to, which is per-install and so
    // cannot live in a [Description] at all, and it has to name engram_remember because a
    // rule with no verb and no trigger loses to one that has both. Subtracting the exact
    // strings keeps every other way guidance could drift back here failing.
    [Theory]
    [InlineData("engram_recall")]
    [InlineData("engram_remember")]
    [InlineData("engram_digest")]
    [InlineData("engram_index_repo")]
    public void Build_GuidanceLines_DoNotRestateToolDescriptions(string toolName)
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped);

        var precedenceLines = Enum.GetValues<MemoryPrecedence>()
            .Select(MemorySettings.PrimerLine)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var guidance = string.Join(
            '\n',
            primer.Split('\n')
                .TakeWhile(l => !l.StartsWith("Examples:", StringComparison.Ordinal))
                .Where(l => !precedenceLines.Contains(l)));

        Assert.DoesNotContain(toolName, guidance);
    }

    [Fact]
    public void Build_CoverageLine_TotalMatchesCorpusSize()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped);

        Assert.Contains($"holds {CannedFacts.All.Count} facts", primer);
    }

    [Fact]
    public void Build_RealCannedFacts_HasAtMostTwoFactLines()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped);

        var factLineCount = primer.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));

        Assert.True(factLineCount <= 2);
    }

    [Fact]
    public void Build_RealCannedFacts_UsesFullExampleBudgetOfTwo()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped);

        var factLineCount = primer.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));

        Assert.Equal(2, factLineCount);
    }

    // HookCommand relies on this being empty rather than whitespace to decide not to emit
    // additionalContext at all. Only reachable with precedence off — see the pair below.
    [Fact]
    public void Build_NoFactsAndPrecedenceOff_EmitsNothing()
    {
        var primer = PrimerBuilder.Build(Array.Empty<CannedFact>(), MemoryPrecedence.Off);

        Assert.Equal(string.Empty, primer);
    }

    // The load-bearing half of that pair. A store with nothing in it is exactly the session
    // where another memory system wins uncontested, so it is the session that most needs to
    // be told where memory lives — an empty primer here would be the bug, not the feature.
    [Fact]
    public void Build_NoFactsButPrecedenceOn_StillStatesWhereMemoryLives()
    {
        var primer = PrimerBuilder.Build(Array.Empty<CannedFact>(), MemoryPrecedence.EngramFirst);

        Assert.Equal(MemorySettings.PrimerLine(MemoryPrecedence.EngramFirst), primer);
    }

    [Theory]
    [InlineData(MemoryPrecedence.EngramFirst)]
    [InlineData(MemoryPrecedence.EngramOnly)]
    public void Build_PrecedenceOn_LeadsWithIt(MemoryPrecedence precedence)
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, precedence);

        // First, not merely present: TryAppendLine drops whatever overruns the budget, and of
        // the three things in here this is the only one whose absence changes what the agent does.
        Assert.Equal(MemorySettings.PrimerLine(precedence), primer.Split('\n')[0]);
    }

    [Fact]
    public void Build_PrecedenceOff_SaysNothingAboutIt()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, MemoryPrecedence.Off);

        Assert.DoesNotContain("durable memory store", primer, StringComparison.Ordinal);
    }

    // SessionStart never fires for a subagent, so whatever the parent was told about where
    // memory lives reaches a child only if the child is told it again through this path.
    [Theory]
    [InlineData(MemoryPrecedence.EngramFirst)]
    [InlineData(MemoryPrecedence.EngramOnly)]
    public void BuildForSubagent_PrecedenceOn_RepeatsItRatherThanAssumingTheParentsPrimer(MemoryPrecedence precedence)
    {
        var primer = PrimerBuilder.BuildForSubagent(CannedFacts.All, precedence);

        Assert.Contains(MemorySettings.PrimerLine(precedence)!, primer, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildForSubagent_RealCannedFacts_StaysUnderMaxTokens()
    {
        var primer = PrimerBuilder.BuildForSubagent(CannedFacts.All, Shipped);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
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

        var primer = PrimerBuilder.Build(facts, Shipped);

        Assert.True(TokenEstimator.Estimate(primer) <= PrimerBuilder.MaxTokens);
        Assert.DoesNotContain("Examples:", primer);
    }

    [Fact]
    public void Build_NeverExceedsBudgetRegardlessOfInputSize()
    {
        var facts = Enumerable.Range(1, 200)
            .Select(i => new CannedFact($"f{i:D4}", $"subject{i}", "predicate", new string('a', 200), i % 2 == 0 ? "user" : "project", "topic", 0))
            .ToList();

        var primer = PrimerBuilder.Build(facts, Shipped);

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

        var primer = PrimerBuilder.Build(facts, Shipped);

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

        var primer = PrimerBuilder.Build(facts, Shipped);

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

        var primer = PrimerBuilder.Build(facts, Shipped);
        var coverageLine = primer.Split('\n').Single(line => line.StartsWith("Memory holds", StringComparison.Ordinal));

        Assert.True(coverageLine.IndexOf("alpha (2)", StringComparison.Ordinal) < coverageLine.IndexOf("beta (1)", StringComparison.Ordinal));
    }

    [Fact]
    public void CoverageLine_CapsClusterListAndSummarizesTail()
    {
        var facts = Enumerable.Range(1, 8)
            .Select(i => new CannedFact($"f{i:D3}", $"topic{i}-detail", "states", $"body {i}", "user", $"topic{i}", 0))
            .ToList();

        var primer = PrimerBuilder.Build(facts, Shipped);
        var coverageLine = primer.Split('\n').Single(line => line.StartsWith("Memory holds", StringComparison.Ordinal));

        Assert.Contains("+3 more", coverageLine);
    }

    /// <summary>
    /// offerEnrollment defaults to false and every other test here uses the 2-arg call, so
    /// nothing had exercised the line Build emits when a caller actually asks for it.
    /// </summary>
    [Fact]
    public void Build_OfferEnrollmentTrue_IncludesTheEnrollmentLine()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped, offerEnrollment: true);

        Assert.Contains("enroll it", primer, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OfferEnrollmentFalse_OmitsTheEnrollmentLine()
    {
        var primer = PrimerBuilder.Build(CannedFacts.All, Shipped, offerEnrollment: false);

        Assert.DoesNotContain("enroll it", primer, StringComparison.Ordinal);
    }

    /// <summary>
    /// BuildForSubagent takes no offerEnrollment parameter at all — SessionStart, not a
    /// subagent, is what runs inside the checkout the offer would apply to.
    /// </summary>
    [Fact]
    public void BuildForSubagent_NeverIncludesTheEnrollmentLine()
    {
        var primer = PrimerBuilder.BuildForSubagent(CannedFacts.All, Shipped);

        Assert.DoesNotContain("enroll it", primer, StringComparison.Ordinal);
    }
}
