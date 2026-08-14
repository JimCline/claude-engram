using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. <see cref="RepoFreshness"/>'s selection filter — the part that needs a real
/// <c>repo_enrollment</c> table and a real filesystem, which is why it is not covered by the pure
/// Tier 1 tests in <c>Engram.Core.Tests</c>.
/// </summary>
public class RepoFreshnessTests
{
    [Fact]
    public void Due_ExcludesDeclinedAndDeferredRepos()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var now = DateTimeOffset.UtcNow;

        RepoEnrollment.Enroll(connection, "enrolled-repo", MakeRepoDir(sandbox), now.AddDays(-1));
        RepoEnrollment.Decline(connection, "declined-repo", MakeRepoDir(sandbox), now.AddDays(-1));
        RepoEnrollment.Defer(connection, "deferred-repo", MakeRepoDir(sandbox), now.AddDays(-1));

        var identities = RepoFreshness.Due(connection, 60, now, EmptySet()).Select(c => c.Row.Identity).ToList();

        Assert.Contains("enrolled-repo", identities);
        Assert.DoesNotContain("declined-repo", identities);
        Assert.DoesNotContain("deferred-repo", identities);
    }

    [Fact]
    public void Due_ExcludesARowWhoseLastRootDoesNotExistOnDisk()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var now = DateTimeOffset.UtcNow;

        var missingRoot = Path.Combine(sandbox.Home.Root, "never-created");
        RepoEnrollment.Enroll(connection, "ghost-repo", missingRoot, now.AddDays(-1));

        var due = RepoFreshness.Due(connection, 60, now, EmptySet());

        Assert.DoesNotContain(due, c => c.Row.Identity == "ghost-repo");
    }

    [Fact]
    public void Due_ExcludesARootInTheExcludeSet()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var now = DateTimeOffset.UtcNow;

        var root = MakeRepoDir(sandbox);
        RepoEnrollment.Enroll(connection, "excluded-repo", root, now.AddDays(-1));

        var exclude = new HashSet<string>(StringComparer.Ordinal) { PathCanonicalizer.Canonical(root) };

        var due = RepoFreshness.Due(connection, 60, now, exclude);

        Assert.DoesNotContain(due, c => c.Row.Identity == "excluded-repo");
    }

    [Fact]
    public void Due_ClassifiesAnUnstampedEnrolledRepoAsUnfulfilledEnrollment()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var now = DateTimeOffset.UtcNow;

        RepoEnrollment.Enroll(connection, "never-scanned", MakeRepoDir(sandbox), now.AddDays(-1));

        var candidate = Assert.Single(RepoFreshness.Due(connection, 60, now, EmptySet()));

        Assert.Equal(FreshnessReason.UnfulfilledEnrollment, candidate.Reason);
    }

    [Fact]
    public void NextDue_WithIncludeAmbientFalse_SkipsAStaleRowAndReturnsNull()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var now = DateTimeOffset.UtcNow;

        // A stamped-but-overdue row classifies as Stale, not UnfulfilledEnrollment — it is
        // ambient work, the same population NextDue's includeAmbient: false must exclude.
        RepoEnrollment.Enroll(connection, "stale-repo", MakeRepoDir(sandbox), now.AddDays(-2));
        RepoEnrollment.StampFullScan(connection, "stale-repo", now.AddDays(-2));

        var withoutAmbient = RepoFreshness.NextDue(connection, 60, now, includeAmbient: false, EmptySet());
        Assert.Null(withoutAmbient);

        var withAmbient = RepoFreshness.NextDue(connection, 60, now, includeAmbient: true, EmptySet());
        Assert.NotNull(withAmbient);
        Assert.Equal("stale-repo", withAmbient!.Row.Identity);
    }

    [Fact]
    public void Neglected_ReturnsAnUnstampedRowOnlyAfterTheEnrollmentGraceElapses()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var now = DateTimeOffset.UtcNow;

        RepoEnrollment.Enroll(connection, "in-flight", MakeRepoDir(sandbox), now.AddMinutes(-5));
        RepoEnrollment.Enroll(
            connection, "abandoned", MakeRepoDir(sandbox), now - RepoFreshness.EnrollmentGrace - TimeSpan.FromMinutes(1));

        var identities = RepoFreshness.Neglected(connection, now).Select(c => c.Row.Identity).ToList();

        Assert.DoesNotContain("in-flight", identities);
        Assert.Contains("abandoned", identities);
    }

    [Fact]
    public void Neglected_ReturnsAStampedRowOnlyAfterNeglectedAfterElapses()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var now = DateTimeOffset.UtcNow;

        RepoEnrollment.Enroll(connection, "recently-scanned", MakeRepoDir(sandbox), now.AddDays(-30));
        RepoEnrollment.StampFullScan(connection, "recently-scanned", now.AddHours(-1));

        RepoEnrollment.Enroll(connection, "long-stale", MakeRepoDir(sandbox), now.AddDays(-30));
        RepoEnrollment.StampFullScan(connection, "long-stale", now - RepoFreshness.NeglectedAfter - TimeSpan.FromHours(1));

        var identities = RepoFreshness.Neglected(connection, now).Select(c => c.Row.Identity).ToList();

        Assert.DoesNotContain("recently-scanned", identities);
        Assert.Contains("long-stale", identities);
    }

    private static string MakeRepoDir(SandboxHome sandbox)
    {
        var root = Path.Combine(sandbox.Home.Root, "repos", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IReadOnlySet<string> EmptySet() => new HashSet<string>(StringComparer.Ordinal);
}
