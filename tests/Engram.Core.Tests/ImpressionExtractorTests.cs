using Engram.Core;

namespace Engram.Core.Tests;

public class ImpressionExtractorTests
{
    [Fact]
    public void FromProse_TakesLeadSentences_UpToTheBudget()
    {
        var text = "Widgets turn cranks into torque. They ship with three gears. "
            + string.Join(" ", Enumerable.Repeat("Padding sentence about nothing in particular.", 40));

        var impression = ImpressionExtractor.FromProse(text);

        Assert.NotNull(impression);
        Assert.StartsWith("Widgets turn cranks into torque.", impression, StringComparison.Ordinal);
        Assert.True(
            TokenEstimator.Estimate(impression) <= ImpressionExtractor.MaxTokens,
            $"impression exceeds the {ImpressionExtractor.MaxTokens}-token budget: {impression}");
    }

    [Fact]
    public void FromProse_AugmentsTheLeadWithRecurringKeywordsItNeverMentions()
    {
        var text = "Install notes.\n\nThe deployment pipeline stages every artifact, and the "
            + "deployment pipeline checks every artifact against the manifest before anything "
            + "ships to production machines.";

        var impression = ImpressionExtractor.FromProse(text);

        Assert.NotNull(impression);
        Assert.StartsWith("Install notes.", impression, StringComparison.Ordinal);
        Assert.Contains("covers", impression, StringComparison.Ordinal);
        Assert.Contains("deployment", impression, StringComparison.Ordinal);
    }

    [Fact]
    public void FromProse_EmptyInput_YieldsNothing()
    {
        Assert.Null(ImpressionExtractor.FromProse(""));
        Assert.Null(ImpressionExtractor.FromProse("   \n\t  "));
    }

    [Fact]
    public void FromLeadComment_ReadsDocCommentsAndStopsAtCode()
    {
        var source = """
            /// <summary>
            /// Reads and writes facts. The temporal model lives here.
            /// </summary>
            public static class FactStore
            {
                // This comment is not the lead and must not appear.
            }
            """;

        var impression = ImpressionExtractor.FromLeadComment(source);

        Assert.NotNull(impression);
        Assert.Contains("temporal model", impression, StringComparison.Ordinal);
        Assert.DoesNotContain("not the lead", impression, StringComparison.Ordinal);
    }

    [Fact]
    public void FromLeadComment_FileWithoutALeadComment_YieldsNothing()
        // Echoing code back as prose would route recall toward noise; declarations are
        // already their own facts.
        => Assert.Null(ImpressionExtractor.FromLeadComment("public class Widget { }"));

    [Fact]
    public void SameInput_SameImpression()
    {
        const string text = "Determinism is what lets the pipeline diff instead of churn. Twice over.";

        Assert.Equal(ImpressionExtractor.FromProse(text), ImpressionExtractor.FromProse(text));
    }
}
