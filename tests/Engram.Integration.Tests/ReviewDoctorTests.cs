using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// docs/memory-expansion/04-lifecycle-spec.md: doctor reports overdue review markers, mirroring
/// <see cref="ToolProfileDoctorTests"/>'s Ok/Warn/malformed structure for its sibling check.
/// </summary>
public class ReviewDoctorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static Diagnosis ReviewCheck(SandboxHome sandbox) => Assert.Single(
        Diagnostics.Run(sandbox.Home, _ => null, reachOut: false).Checks,
        check => check.Name == "review");

    [Fact]
    public void NoStoreYet_ReportsWarnWithNoFault()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var check = ReviewCheck(sandbox);

        Assert.Equal(DiagnosisState.Warn, check.State);
        Assert.Contains("no store", check.Detail);
    }

    [Fact]
    public void NothingDue_ReportsOk()
    {
        using var sandbox = new SandboxHome();

        var check = ReviewCheck(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Contains("nothing due", check.Detail);
    }

    // Falsify: change the DiagnosisState.Warn in CheckReview's due branch to Broken and confirm
    // this fails — a deferred review is a choice, not a fault (D37), same as CheckToolProfile.
    [Fact]
    public void AFactPastItsReviewDate_ReportsWarnNamingTheCount()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var factId = Write(connection, "/knowledge/alpha/a", "A belief.");
            FactReview.Set(connection, null, factId, T0.AddDays(-1).ToUnixTimeSeconds(), T0.ToUnixTimeSeconds());
        }

        var check = ReviewCheck(sandbox);

        Assert.Equal(DiagnosisState.Warn, check.State);
        Assert.Contains("1 fact", check.Detail);
        Assert.Contains("review list", check.Fix);
    }

    private static long Write(Microsoft.Data.Sqlite.SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "note", "notes", body, "user", "stated"),
            T0).FactId;
}
