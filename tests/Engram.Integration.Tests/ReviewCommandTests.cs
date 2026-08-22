using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 for the review-due marker (docs/memory-expansion/04-lifecycle-spec.md):
/// <c>engram review clear</c> is dry-run by default, like every other destructive verb (D49).
/// </summary>
public class ReviewCommandTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Clear_WithoutApply_LeavesTheReviewMarkerInPlace()
    {
        using var sandbox = new SandboxHome(initialize: false);
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/knowledge/alpha/a", "A belief.");
            FactReview.Set(connection, null, factId, T0.AddDays(1).ToUnixTimeSeconds(), T0.ToUnixTimeSeconds());
        }

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "review", "clear", FactCatalog.HandleFor(factId)],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run only", stdout.ToString());

        using var reopened = EngramDatabase.Open(sandbox.Home);
        Assert.Single(FactReview.ListLive(reopened));
    }

    [Fact]
    public void Clear_WithApply_RemovesTheReviewMarker()
    {
        using var sandbox = new SandboxHome(initialize: false);
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/knowledge/alpha/a", "A belief.");
            FactReview.Set(connection, null, factId, T0.AddDays(1).ToUnixTimeSeconds(), T0.ToUnixTimeSeconds());
        }

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "review", "clear", FactCatalog.HandleFor(factId), "--apply"],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Cleared", stdout.ToString());

        using var reopened = EngramDatabase.Open(sandbox.Home);
        Assert.Empty(FactReview.ListLive(reopened));
    }

    [Fact]
    public void List_WithNothingSet_ReportsNothingSet()
    {
        using var sandbox = new SandboxHome();

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "review", "list"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Nothing has a review date set.", stdout.ToString());
    }

    [Fact]
    public void List_ShowsAFactWhoseDateHasPassed_AsDue()
    {
        using var sandbox = new SandboxHome(initialize: false);
        long factId;
        long reviewAfter;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/knowledge/alpha/a", "A belief.");
            reviewAfter = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            FactReview.Set(connection, null, factId, reviewAfter, T0.ToUnixTimeSeconds());
        }

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "review", "list"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var line = stdout.ToString();
        Assert.Contains($"[{FactCatalog.HandleFor(factId)}]", line);
        Assert.Contains("— due (", line);
        Assert.Contains(MomentText.Local(reviewAfter), line);
    }

    [Fact]
    public void List_ShowsAFactWhoseDateHasNotYetPassed_AsNotYetDue()
    {
        using var sandbox = new SandboxHome(initialize: false);
        long factId;
        long reviewAfter;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/knowledge/alpha/a", "A belief.");
            reviewAfter = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
            FactReview.Set(connection, null, factId, reviewAfter, T0.ToUnixTimeSeconds());
        }

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "review", "list"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var line = stdout.ToString();
        Assert.Contains($"[{FactCatalog.HandleFor(factId)}]", line);
        Assert.Contains("— not yet due (", line);
        Assert.Contains(MomentText.Local(reviewAfter), line);
    }

    private static long Write(Microsoft.Data.Sqlite.SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "note", "notes", body, "user", "stated"),
            T0).FactId;
}
