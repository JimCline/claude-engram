using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The D16 gate read off a real store. The arithmetic is tier 1; what needs a database is
/// which rows the question is even about.
/// </summary>
public class FactDensityStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    // The seed corpus is fifty-one facts with no session behind them. Counting them would put
    // the median wherever the corpus size happens to sit and answer a question about Engram's
    // authors rather than about this user's sessions.
    [Fact]
    public void TheSeededCorpusIsNotASession()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var stat = FactDensity.Read(connection);

        Assert.Equal(0, stat.Sessions);
        Assert.Equal(0, stat.Facts);
    }

    [Fact]
    public void EachSessionIsCountedSeparately()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "First.", subject: null, evidence: null, agent: null, Now);
        SessionFacts.Append(connection, "sess-a", "Second.", subject: null, evidence: null, agent: null, Now);
        SessionFacts.Append(connection, "sess-b", "Only one here.", subject: null, evidence: null, agent: null, Now);

        var stat = FactDensity.Read(connection);

        Assert.Equal(2, stat.Sessions);
        Assert.Equal(3, stat.Facts);
        Assert.Equal(1, stat.Min);
        Assert.Equal(2, stat.Max);
        Assert.Equal(1.5, stat.Median);
    }

    // A capture and the model's rewrite of it are two rows at one address. The timeline would
    // show one line for that statement, so counting two would inflate the metric in exactly
    // the direction that wrongly passes the gate.
    [Fact]
    public void ARestatementInTheSameSessionCountsOnce()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var captured = UserFacts.Capture(
            connection, UserFactTopic.AboutYou, "I saw a movie last Saturday", "sess-a", Now);
        UserFacts.Restate(
            connection, captured!.Value, "Saw a Spider-Man film on 2026-08-01", "sess-a", Now);

        var stat = FactDensity.Read(connection);

        Assert.Equal(1, stat.Sessions);
        Assert.Equal(1, stat.Facts);
    }

    // A retracted fact was still something the session produced, and the supersession row that
    // records the retraction is not a second fact.
    [Fact]
    public void ARetractedFactStillCounts()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = SessionFacts.Append(
            connection, "sess-a", "Turned out to be wrong.", subject: null, evidence: null, agent: null, Now);
        FactStore.Forget(connection, factId, "retracted by the user", Now);

        var stat = FactDensity.Read(connection);

        Assert.Equal(1, stat.Sessions);
        Assert.Equal(1, stat.Facts);
    }
}
