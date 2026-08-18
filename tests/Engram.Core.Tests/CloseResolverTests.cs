using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// The four-case close-resolution decision and the retry-ceiling transition
/// (docs/gp-adoption/01-sync-spec.md), tested as the pure functions they are — no database.
/// </summary>
public class CloseResolverTests
{
    private static SyncCloseRecord Record(
        string subject = "note/x",
        string predicate = "favorite_color",
        string body = "green",
        long validFrom = 100,
        long validTo = 200,
        string? supersededByBody = null,
        long? supersededByValidFrom = null) =>
        new(subject, predicate, body, validFrom, validTo, supersededByBody, supersededByValidFrom);

    /// <summary>Case 2: the slot's live fact IS the named fact, content-identical — apply the close.</summary>
    [Fact]
    public void ALiveExactMatch_Applies()
    {
        var rows = new List<LocalFactRow> { new("green", 100, IsLive: true) };

        Assert.Equal(CloseResolution.Apply, CloseResolver.Resolve(rows, Record()));
    }

    /// <summary>
    /// Case 4: the slot has a live fact, but it is not the one the close names — the target
    /// authored something else here, and a close may never touch it (D8).
    /// </summary>
    [Fact]
    public void ALiveFactThatDiffersFromTheRecord_Conflicts()
    {
        var rows = new List<LocalFactRow> { new("blue", 150, IsLive: true) };

        Assert.Equal(CloseResolution.Conflict, CloseResolver.Resolve(rows, Record()));
    }

    /// <summary>
    /// Case 3: no live fact in the slot, but a closed row already matches the record exactly —
    /// this close was already applied (or arrived pre-closed via the fact record itself).
    /// </summary>
    [Fact]
    public void NoLiveRowButAMatchingClosedRow_IsAlreadyPresent()
    {
        var rows = new List<LocalFactRow> { new("green", 100, IsLive: false) };

        Assert.Equal(CloseResolution.AlreadyPresent, CloseResolver.Resolve(rows, Record()));
    }

    /// <summary>Case 1: the slot has no row at all yet — the fact this close names has not synced here.</summary>
    [Fact]
    public void AnEmptySlot_Defers()
    {
        Assert.Equal(CloseResolution.Defer, CloseResolver.Resolve([], Record()));
    }

    /// <summary>Also defers when the slot holds only unrelated closed rows.</summary>
    [Fact]
    public void OnlyUnrelatedClosedRows_Defer()
    {
        var rows = new List<LocalFactRow> { new("blue", 50, IsLive: false) };

        Assert.Equal(CloseResolution.Defer, CloseResolver.Resolve(rows, Record()));
    }

    /// <summary>
    /// Named falsification #1 (spec): deleting the live-row exact-match comparison collapses the
    /// live branch to always-Apply, which would let a close overwrite a fact the target authored
    /// independently — exactly the D8 violation case 4 exists to prevent. This test fails red if
    /// that comparison is removed, proving the branch is load-bearing rather than decorative.
    /// </summary>
    [Fact]
    public void Falsification_RemovingTheLiveExactMatchCheck_WouldMisclassifyAConflictAsApply()
    {
        var rows = new List<LocalFactRow> { new("blue", 150, IsLive: true) };

        var outcome = CloseResolver.Resolve(rows, Record());

        Assert.NotEqual(CloseResolution.Apply, outcome);
        Assert.Equal(CloseResolution.Conflict, outcome);
    }

    [Theory]
    [InlineData(0, 20, "deferred")]
    [InlineData(19, 20, "deferred")]
    [InlineData(20, 20, "stalled")]
    [InlineData(21, 20, "stalled")]
    public void NextDeferredStatus_MovesToStalledAtTheCeiling(int retryCount, int ceiling, string expected) =>
        Assert.Equal(expected, CloseResolver.NextDeferredStatus(retryCount, ceiling));

    /// <summary>
    /// Named falsification #2 (spec): deleting the ceiling comparison collapses
    /// <c>NextDeferredStatus</c> to always "deferred", which would retry a permanently orphaned
    /// close forever. This test fails red under that deletion.
    /// </summary>
    [Fact]
    public void Falsification_RemovingTheCeilingCheck_WouldNeverReachStalled()
    {
        var status = CloseResolver.NextDeferredStatus(1_000_000, CloseResolver.DefaultRetryCeiling);

        Assert.Equal("stalled", status);
    }

    [Fact]
    public void DefaultRetryCeiling_Is20()
    {
        Assert.Equal(20, CloseResolver.DefaultRetryCeiling);
    }
}
