using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 for the review-due marker (docs/memory-expansion/04-lifecycle-spec.md):
/// <see cref="PrimerSummary.ReviewDueCount"/> reuses <see cref="PrimerSummary.Read"/>'s existing
/// one-query pattern (D46) rather than a separate hook or query.
/// </summary>
public class PrimerSummaryReviewDueCountTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoReviewMarkersSet_CountsZero()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/alpha/a", "A belief.", 1);

        var summary = PrimerSummary.Read(connection, T0.AddDays(1));

        Assert.Equal(0, summary.ReviewDueCount);
    }

    [Fact]
    public void AMarkerWhoseDateHasNotYetPassed_IsNotCounted()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var factId = Write(connection, "/knowledge/alpha/a", "A belief.", 1);
        FactReview.Set(connection, null, factId, T0.AddDays(10).ToUnixTimeSeconds(), T0.ToUnixTimeSeconds());

        var summary = PrimerSummary.Read(connection, T0.AddDays(1));

        Assert.Equal(0, summary.ReviewDueCount);
    }

    [Fact]
    public void AMarkerWhoseDateHasPassed_IsCounted()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var factId = Write(connection, "/knowledge/alpha/a", "A belief.", 1);
        FactReview.Set(connection, null, factId, T0.AddDays(1).ToUnixTimeSeconds(), T0.ToUnixTimeSeconds());

        var summary = PrimerSummary.Read(connection, T0.AddDays(10));

        Assert.Equal(1, summary.ReviewDueCount);
    }

    /// <summary>
    /// A marker on a fact that was since retracted must not count — <see cref="FactReview.CountDue"/>
    /// joins against live facts only, mirroring <see cref="FactReview.ListLive"/>.
    /// </summary>
    [Fact]
    public void AMarkerOnAFactThatWasRetracted_IsNotCounted()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var factId = Write(connection, "/knowledge/alpha/a", "A belief.", 1);
        FactReview.Set(connection, null, factId, T0.AddDays(1).ToUnixTimeSeconds(), T0.ToUnixTimeSeconds());
        FactStore.Forget(connection, factId, "no longer true", T0.AddDays(2));

        var summary = PrimerSummary.Read(connection, T0.AddDays(10));

        Assert.Equal(0, summary.ReviewDueCount);
    }

    private static long Write(Microsoft.Data.Sqlite.SqliteConnection connection, string path, string body, int second) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "note", "notes", body, "user", "stated"),
            T0.AddSeconds(second)).FactId;
}
