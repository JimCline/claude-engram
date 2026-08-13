using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// RepoEnrollment's pure policy functions and its one non-decision DB write — until now every
/// assertion about them rode through RepoCommand or CodeIndexer instead of exercising the
/// function directly (spec §8.3 coverage gap, reviewer BLOCKING #2).
/// </summary>
public class RepoEnrollmentTests
{
    private static RepoEnrollmentRow Row(
        RepoEnrollmentState state = RepoEnrollmentState.Enrolled,
        string source = "user",
        string? lastRoot = "/repo",
        long decidedAt = 0,
        long? lastFullScanAt = null) =>
        new("github.com/acme/api", state, source, lastRoot, decidedAt, lastFullScanAt);

    /// <summary>
    /// A newly-enrolled row has never been scanned, so its first index must be a full scan —
    /// §4.9's --drain-all step 3 discard rule is contingent on this holding.
    /// </summary>
    [Fact]
    public void IsFullScanDue_ARowThatHasNeverBeenScanned_IsDue()
    {
        var row = Row(lastFullScanAt: null);

        Assert.True(RepoEnrollment.IsFullScanDue(row, intervalMinutes: 60, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsFullScanDue_ANullRow_IsNotDue()
    {
        Assert.False(RepoEnrollment.IsFullScanDue(null, intervalMinutes: 60, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsFullScanDue_WithinTheInterval_IsNotDue()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(10_000);
        var row = Row(lastFullScanAt: now.ToUnixTimeSeconds() - 60 * 30);

        Assert.False(RepoEnrollment.IsFullScanDue(row, intervalMinutes: 60, now));
    }

    [Fact]
    public void IsFullScanDue_PastTheInterval_IsDue()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(10_000);
        var row = Row(lastFullScanAt: now.ToUnixTimeSeconds() - 60 * 90);

        Assert.True(RepoEnrollment.IsFullScanDue(row, intervalMinutes: 60, now));
    }

    [Fact]
    public void StampFullScan_SetsLastFullScanAtToNow()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        RepoEnrollment.Enroll(connection, "github.com/acme/api", "/repo", DateTimeOffset.FromUnixTimeSeconds(0));

        var now = DateTimeOffset.FromUnixTimeSeconds(50_000);
        RepoEnrollment.StampFullScan(connection, "github.com/acme/api", now);

        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(now.ToUnixTimeSeconds(), row.LastFullScanAt);
    }

    [Fact]
    public void ShouldOfferEnrollment_NoRow_ReturnsTrue()
    {
        Assert.True(RepoEnrollment.ShouldOfferEnrollment(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ShouldOfferEnrollment_EnrolledRow_ReturnsFalse()
    {
        var row = Row(state: RepoEnrollmentState.Enrolled);

        Assert.False(RepoEnrollment.ShouldOfferEnrollment(row, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A decline is forever unless the decision is reset — there is no cooldown that reoffers it.
    /// </summary>
    [Fact]
    public void ShouldOfferEnrollment_DeclinedRow_NeverReoffersOnItsOwn()
    {
        var row = Row(state: RepoEnrollmentState.Declined, decidedAt: 0);

        // Far past any cooldown that would apply to a Deferred row — Declined ignores the
        // cooldown entirely rather than merely outlasting it.
        Assert.False(RepoEnrollment.ShouldOfferEnrollment(row, DateTimeOffset.FromUnixTimeSeconds(4_000_000_000)));
    }

    [Fact]
    public void ShouldOfferEnrollment_DeferredWithinTheCooldown_ReturnsFalse()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var row = Row(state: RepoEnrollmentState.Deferred, decidedAt: now.ToUnixTimeSeconds() - 60);

        Assert.False(RepoEnrollment.ShouldOfferEnrollment(row, now));
    }

    [Fact]
    public void ShouldOfferEnrollment_DeferredPastTheCooldown_ReturnsTrue()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var decidedAt = now.ToUnixTimeSeconds() - (long)RepoEnrollment.DeferralCooldown.TotalSeconds - 60;
        var row = Row(state: RepoEnrollmentState.Deferred, decidedAt: decidedAt);

        Assert.True(RepoEnrollment.ShouldOfferEnrollment(row, now));
    }

    [Fact]
    public void FindCheckoutRoot_AnOrdinaryGitDirectory_ReturnsTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"engram-checkout-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "src", "deep");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        try
        {
            Assert.Equal(PathCanonicalizer.Canonical(root), RepoEnrollment.FindCheckoutRoot(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A worktree's or submodule's .git is a FILE holding a "gitdir:" pointer line, not a
    /// directory — the entire reason FindCheckoutRoot checks File.Exists alongside
    /// Directory.Exists rather than just the ordinary case.
    /// </summary>
    [Fact]
    public void FindCheckoutRoot_DotGitIsAFile_StillReturnsTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"engram-worktree-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "src");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /elsewhere/.git/worktrees/thing\n");

        try
        {
            Assert.Equal(PathCanonicalizer.Canonical(root), RepoEnrollment.FindCheckoutRoot(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
