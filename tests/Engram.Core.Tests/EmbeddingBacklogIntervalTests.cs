using Engram.Core;

namespace Engram.Core.Tests;

public class EmbeddingBacklogIntervalTests
{
    private static BackfillResult Result(BackfillOutcome outcome, int embedded = 0, int remaining = 0) =>
        new(outcome, embedded, Failed: 0, remaining);

    [Fact]
    public void WorkRemaining_PollsAgainSoon()
    {
        Assert.Equal(
            EmbeddingBacklog.BusyInterval,
            EmbeddingBacklog.NextDelay(Result(BackfillOutcome.BatchLimitReached, embedded: 16, remaining: 4)));
    }

    [Fact]
    public void HavingJustEmbedded_PollsAgainSoon()
    {
        // A session that just wrote facts is likely to write more, and the point of the fast
        // interval is that the gap between "remembered" and "semantically findable" is seconds
        // rather than a fixed worst case.
        Assert.Equal(
            EmbeddingBacklog.BusyInterval,
            EmbeddingBacklog.NextDelay(Result(BackfillOutcome.Completed, embedded: 3)));
    }

    [Fact]
    public void AnIdlePass_BacksOff()
    {
        Assert.Equal(
            EmbeddingBacklog.IdleInterval,
            EmbeddingBacklog.NextDelay(Result(BackfillOutcome.Completed)));
    }

    /// <summary>
    /// Both of these mean the next pass would do the identical thing and fail the identical
    /// way. Retrying every two seconds is how a local runtime gets blamed for load Engram
    /// generated.
    /// </summary>
    [Theory]
    [InlineData(BackfillOutcome.StalledOnFailures)]
    [InlineData(BackfillOutcome.SpaceMismatch)]
    public void APassThatCannotProgress_BacksOff(BackfillOutcome outcome)
    {
        Assert.Equal(
            EmbeddingBacklog.IdleInterval,
            EmbeddingBacklog.NextDelay(Result(outcome, remaining: 9)));
    }

    [Fact]
    public void TheBusyIntervalIsShorterThanTheIdleOne()
    {
        Assert.True(EmbeddingBacklog.BusyInterval < EmbeddingBacklog.IdleInterval);
        Assert.True(EmbeddingBacklog.BusyInterval > TimeSpan.Zero);
    }
}
