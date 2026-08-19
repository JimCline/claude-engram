using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// <see cref="SyncStaleness.Evaluate"/> (docs/memory-expansion/01-sync-spec.md,
/// "Staleness/liveness detection") — tested as the pure function it is, no database and no
/// filesystem.
/// </summary>
public class SyncStalenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APeerNeverObserved_IsNeverStale()
    {
        var peers = new[] { new PeerObservation("b-machine", LastObservedUtc: null) };

        var result = SyncStaleness.Evaluate(peers, Now, TimeSpan.FromDays(14));

        var verdict = Assert.Single(result);
        Assert.False(verdict.IsStale);
        Assert.Null(verdict.LastObservedUtc);
    }

    [Fact]
    public void APeerObservedWithinTheThreshold_IsNotStale()
    {
        var peers = new[] { new PeerObservation("b-machine", Now - TimeSpan.FromDays(13)) };

        var result = SyncStaleness.Evaluate(peers, Now, TimeSpan.FromDays(14));

        Assert.False(Assert.Single(result).IsStale);
    }

    [Fact]
    public void APeerObservedPastTheThreshold_IsStale()
    {
        var peers = new[] { new PeerObservation("b-machine", Now - TimeSpan.FromDays(15)) };

        var result = SyncStaleness.Evaluate(peers, Now, TimeSpan.FromDays(14));

        Assert.True(Assert.Single(result).IsStale);
    }

    [Fact]
    public void ExactlyAtTheThreshold_IsNotYetStale()
    {
        // The rule is strictly-greater-than (docs' "> staleAfter"), so a peer observed exactly
        // staleAfter ago has not yet crossed it.
        var peers = new[] { new PeerObservation("b-machine", Now - TimeSpan.FromDays(14)) };

        var result = SyncStaleness.Evaluate(peers, Now, TimeSpan.FromDays(14));

        Assert.False(Assert.Single(result).IsStale);
    }

    /// <summary>
    /// Falsification: deleting the <c>LastObservedUtc.HasValue &amp;&amp;</c> guard would let a
    /// freshly enrolled peer with no chunks yet — <c>LastObservedUtc</c> null, so
    /// <c>now - null</c> would not even compile without a fallback, and any fallback that
    /// substitutes a zero or epoch value reads as "observed an eternity ago" — come out stale.
    /// This test fails red under that substitution (e.g. defaulting the null case to
    /// <see cref="DateTimeOffset.MinValue"/> before subtracting).
    /// </summary>
    [Fact]
    public void Falsification_ANeverObservedPeerMustNotBeTreatedAsObservedAtTheEpoch()
    {
        var peers = new[] { new PeerObservation("freshly-enrolled", LastObservedUtc: null) };

        var result = SyncStaleness.Evaluate(peers, Now, TimeSpan.FromDays(14));

        Assert.False(Assert.Single(result).IsStale);
    }

    [Fact]
    public void MultiplePeers_EachEvaluatedIndependently()
    {
        var peers = new[]
        {
            new PeerObservation("fresh", Now - TimeSpan.FromDays(1)),
            new PeerObservation("stale", Now - TimeSpan.FromDays(30)),
            new PeerObservation("never-seen", null),
        };

        var result = SyncStaleness.Evaluate(peers, Now, TimeSpan.FromDays(14));

        Assert.False(result.Single(p => p.MachineId == "fresh").IsStale);
        Assert.True(result.Single(p => p.MachineId == "stale").IsStale);
        Assert.False(result.Single(p => p.MachineId == "never-seen").IsStale);
    }
}
