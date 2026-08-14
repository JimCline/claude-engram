using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// Tier 1: the pure selection policy behind <see cref="RepoFreshness.Due"/>,
/// <see cref="RepoFreshness.NextDue"/> and <see cref="RepoFreshness.Neglected"/> — ordering and
/// reason classification — exercised without a database through the internal comparer and
/// classifier <c>RepoFreshness</c> exposes to this project via <c>InternalsVisibleTo</c>, the same
/// pattern <c>RecallEngine</c> uses for its equivalence harness.
/// </summary>
public class RepoFreshnessTests
{
    private static RepoEnrollmentRow Row(string identity, string source, long decidedAt, long? lastFullScanAt) =>
        new(identity, RepoEnrollmentState.Enrolled, source, "/repo/" + identity, decidedAt, lastFullScanAt);

    [Theory]
    [InlineData(null, "user", FreshnessReason.UnfulfilledEnrollment)]
    [InlineData(null, "backfill", FreshnessReason.NeverScanned)]
    [InlineData(100L, "user", FreshnessReason.Stale)]
    [InlineData(100L, "backfill", FreshnessReason.Stale)]
    public void ClassifyDueReason_ReadsSourceOnlyWhenNeverScanned(
        long? lastFullScanAt, string source, FreshnessReason expected)
    {
        var row = Row("repo", source, decidedAt: 0, lastFullScanAt);

        Assert.Equal(expected, RepoFreshness.ClassifyDueReason(row));
    }

    [Fact]
    public void IsFullScanDue_ReturnsFalseForARowStampedInsideTheInterval()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(100_000);
        var row = Row("repo", "user", decidedAt: 0, lastFullScanAt: now.ToUnixTimeSeconds() - 60);

        Assert.False(RepoEnrollment.IsFullScanDue(row, intervalMinutes: 60, now));
    }

    [Fact]
    public void DueOrder_PutsNullStampsBeforeStampedOnes()
    {
        var neverScanned = new FreshnessCandidate(
            Row("a", "user", decidedAt: 500, lastFullScanAt: null), "/repo/a", FreshnessReason.UnfulfilledEnrollment);
        var stamped = new FreshnessCandidate(
            Row("b", "user", decidedAt: 1, lastFullScanAt: 1), "/repo/b", FreshnessReason.Stale);

        var ordered = new[] { stamped, neverScanned }.OrderBy(c => c, RepoFreshness.DueOrder).ToList();

        Assert.Equal(["a", "b"], ordered.Select(c => c.Row.Identity));
    }

    [Fact]
    public void DueOrder_WithinNullStamps_SortsByOldestDecidedAtFirst()
    {
        var older = new FreshnessCandidate(
            Row("older", "user", decidedAt: 100, lastFullScanAt: null), "/repo/older", FreshnessReason.UnfulfilledEnrollment);
        var newer = new FreshnessCandidate(
            Row("newer", "user", decidedAt: 200, lastFullScanAt: null), "/repo/newer", FreshnessReason.UnfulfilledEnrollment);

        var ordered = new[] { newer, older }.OrderBy(c => c, RepoFreshness.DueOrder).ToList();

        Assert.Equal(["older", "newer"], ordered.Select(c => c.Row.Identity));
    }

    [Fact]
    public void DueOrder_WithinStampedRows_SortsByOldestLastFullScanAtFirst()
    {
        var older = new FreshnessCandidate(
            Row("older", "user", decidedAt: 0, lastFullScanAt: 100), "/repo/older", FreshnessReason.Stale);
        var newer = new FreshnessCandidate(
            Row("newer", "user", decidedAt: 0, lastFullScanAt: 200), "/repo/newer", FreshnessReason.Stale);

        var ordered = new[] { newer, older }.OrderBy(c => c, RepoFreshness.DueOrder).ToList();

        Assert.Equal(["older", "newer"], ordered.Select(c => c.Row.Identity));
    }

    [Fact]
    public void DueOrder_BreaksExactTiesByIdentity()
    {
        var b = new FreshnessCandidate(
            Row("b", "user", decidedAt: 0, lastFullScanAt: 100), "/repo/b", FreshnessReason.Stale);
        var a = new FreshnessCandidate(
            Row("a", "user", decidedAt: 0, lastFullScanAt: 100), "/repo/a", FreshnessReason.Stale);

        var ordered = new[] { b, a }.OrderBy(c => c, RepoFreshness.DueOrder).ToList();

        Assert.Equal(["a", "b"], ordered.Select(c => c.Row.Identity));
    }
}
