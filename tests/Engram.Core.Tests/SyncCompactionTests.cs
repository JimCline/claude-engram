using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// <see cref="SyncCompaction.Partition"/> (docs/memory-expansion/01-sync-spec.md, "Chunk
/// retention/pruning") — tested as the pure function it is, over fabricated identity sets and
/// ages, no database and no filesystem.
/// </summary>
public class SyncCompactionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static FactIdentity Identity(string body) => new("/project/a", "states", body, 1_000);

    [Fact]
    public void AStillLiveFact_IsRetainedRegardlessOfHowOldItsOriginalExportWas()
    {
        var identity = Identity("live forever");

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [identity],
            closedAt: new Dictionary<FactIdentity, DateTimeOffset>(),
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.Equal(SyncRetentionBucket.Live, buckets[identity]);
    }

    [Fact]
    public void AFactClosedOneDayAgo_IsRetainedEvenThoughItsClosed()
    {
        var identity = Identity("closed recently");
        var closedAt = new Dictionary<FactIdentity, DateTimeOffset> { [identity] = Now - TimeSpan.FromDays(1) };

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [identity],
            closedAt: closedAt,
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.Equal(SyncRetentionBucket.RetainedClosed, buckets[identity]);
    }

    [Fact]
    public void AFactClosedPastRetainDays_IsDropped()
    {
        var identity = Identity("closed long ago");
        var closedAt = new Dictionary<FactIdentity, DateTimeOffset> { [identity] = Now - TimeSpan.FromDays(91) };

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [identity],
            closedAt: closedAt,
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.Equal(SyncRetentionBucket.Dropped, buckets[identity]);
    }

    [Fact]
    public void ExactlyAtRetainDays_IsNotYetDropped()
    {
        var identity = Identity("closed exactly at the boundary");
        var closedAt = new Dictionary<FactIdentity, DateTimeOffset> { [identity] = Now - TimeSpan.FromDays(90) };

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [identity],
            closedAt: closedAt,
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.Equal(SyncRetentionBucket.RetainedClosed, buckets[identity]);
    }

    /// <summary>
    /// The edge case a real chunk-scan can produce: a fact exported already-closed never appears
    /// in <c>openAtExport</c> at all, and never gets a separate close record either — its own
    /// export was already the closed state. It still ages out like any other closed fact.
    /// </summary>
    [Fact]
    public void AFactExportedAlreadyClosed_WithNoSeparateCloseRecord_StillAgesOutPastRetainDays()
    {
        var identity = Identity("closed before it was ever exported open");
        var closedAt = new Dictionary<FactIdentity, DateTimeOffset> { [identity] = Now - TimeSpan.FromDays(91) };

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [], // never open at export time
            closedAt: closedAt,
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.Equal(SyncRetentionBucket.Dropped, buckets[identity]);
    }

    /// <summary>
    /// A closed identity with no known age (absent from both <c>openAtExport</c> and
    /// <c>closedAt</c>) must not be silently dropped — an unmeasurable age is retained
    /// conservatively rather than guessed at.
    /// </summary>
    [Fact]
    public void AClosedFactWithNoKnownAge_IsRetainedConservatively()
    {
        var identity = Identity("closed, age unknown");

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [],
            closedAt: new Dictionary<FactIdentity, DateTimeOffset>(),
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.Equal(SyncRetentionBucket.RetainedClosed, buckets[identity]);
    }

    [Fact]
    public void MixedIdentities_EachPartitionedIndependently()
    {
        var live = Identity("live");
        var retained = Identity("retained");
        var dropped = Identity("dropped");
        var closedAt = new Dictionary<FactIdentity, DateTimeOffset>
        {
            [retained] = Now - TimeSpan.FromDays(10),
            [dropped] = Now - TimeSpan.FromDays(200),
        };

        var buckets = SyncCompaction.Partition(
            allExported: [live, retained, dropped],
            openAtExport: [live],
            closedAt: closedAt,
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.Equal(SyncRetentionBucket.Live, buckets[live]);
        Assert.Equal(SyncRetentionBucket.RetainedClosed, buckets[retained]);
        Assert.Equal(SyncRetentionBucket.Dropped, buckets[dropped]);
    }

    /// <summary>
    /// Falsification: deleting the live branch (returning early with only the closed-path logic)
    /// would classify every identity by closure age alone, so a still-open fact with an old
    /// original export date would wrongly fall through to Dropped once nothing populates
    /// <c>closedAt</c> for it and any code path treats "no closedAt entry" as "closed forever
    /// ago" instead of "still live". This test fails red under that mutation.
    /// </summary>
    [Fact]
    public void Falsification_RemovingTheLiveCheck_WouldMisclassifyAStillOpenOldFact()
    {
        var identity = Identity("old but still open");

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [identity],
            closedAt: new Dictionary<FactIdentity, DateTimeOffset>(),
            now: Now,
            retain: TimeSpan.FromDays(1)); // a tiny retain window: if live-ness is not checked, this ages out immediately

        Assert.Equal(SyncRetentionBucket.Live, buckets[identity]);
    }

    /// <summary>
    /// Falsification: deleting the within-window-retain branch (i.e. dropping every closed fact
    /// unconditionally) would drop a fact closed one day ago. This test fails red under that
    /// mutation.
    /// </summary>
    [Fact]
    public void Falsification_DroppingEveryClosedFactUnconditionally_WouldLoseARecentlyClosedOne()
    {
        var identity = Identity("closed yesterday");
        var closedAt = new Dictionary<FactIdentity, DateTimeOffset> { [identity] = Now - TimeSpan.FromDays(1) };

        var buckets = SyncCompaction.Partition(
            allExported: [identity],
            openAtExport: [identity],
            closedAt: closedAt,
            now: Now,
            retain: TimeSpan.FromDays(90));

        Assert.NotEqual(SyncRetentionBucket.Dropped, buckets[identity]);
    }
}
