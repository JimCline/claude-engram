using System.Diagnostics;
using Engram.Core;

namespace Engram.Integration.Tests;

public class IndexLockTests
{
    [Fact]
    public void TryClaim_SecondClaimWhileFirstIsHeld_RefusesWithoutStealing()
    {
        using var sandbox = new SandboxHome();
        const string identity = "repo-under-test";

        var (first, firstBlockedBy) = IndexLock.TryClaim(sandbox.Home, identity, DateTimeOffset.UtcNow);
        Assert.NotNull(first);
        Assert.Null(firstBlockedBy);

        var (second, secondBlockedBy) = IndexLock.TryClaim(sandbox.Home, identity, DateTimeOffset.UtcNow);

        Assert.Null(second);
        Assert.NotNull(secondBlockedBy);
        Assert.Equal(Environment.ProcessId, secondBlockedBy.Pid);
        Assert.True(
            File.Exists(IndexLock.PathFor(sandbox.Home, identity)),
            "a lock naming a still-live process must not be reaped");

        first!.Dispose();
    }

    [Fact]
    public void TryClaim_LockFileNamingADeadProcess_IsReapedAndTheClaimSucceeds()
    {
        using var sandbox = new SandboxHome();
        const string identity = "repo-under-test";
        var deadPid = SpawnAndWaitForExit();

        Directory.CreateDirectory(sandbox.Home.IndexLockDir);
        File.WriteAllText(
            IndexLock.PathFor(sandbox.Home, identity),
            $$"""
            {"pid":{{deadPid}},"identity":"{{identity}}","started_at":"2020-01-01T00:00:00+00:00","start_token":"stale-token-no-longer-live"}
            """);

        var (held, blockedBy) = IndexLock.TryClaim(sandbox.Home, identity, DateTimeOffset.UtcNow);

        Assert.NotNull(held);
        Assert.Null(blockedBy);

        held!.Dispose();
    }

    [Fact]
    public void Index_DryRun_NeitherClaimsTheLockNorIsBlockedByAnExistingOne()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one file");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var identity = CodeIndexer.ResolveIdentity(repo);

        // A lock that names this very process as a live, unstealable holder — so if a dry run ever
        // checked it, this would refuse. It must not check at all (§6.4): a dry run writes nothing,
        // so it may neither block nor be blocked.
        var lockPath = IndexLock.PathFor(sandbox.Home, identity);
        Directory.CreateDirectory(sandbox.Home.IndexLockDir);
        File.WriteAllText(
            lockPath,
            $$"""
            {"pid":{{Environment.ProcessId}},"identity":"{{identity}}","started_at":"2020-01-01T00:00:00+00:00","start_token":"{{ProcessStartToken.ForSelf()}}"}
            """);

        var report = CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: false, Drain: false, Full: false),
            DateTimeOffset.UtcNow);

        Assert.DoesNotContain(
            report.Notes, n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));
        Assert.True(File.Exists(lockPath), "a dry run must not touch an existing lock file");
    }

    [Fact]
    public void Index_SecondClaimWhileFirstIsStillHeld_GetsTheSkipNote_AndTheFirstsReportIsUnaffected()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one file");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var identity = CodeIndexer.ResolveIdentity(repo);

        var firstReport = CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false),
            DateTimeOffset.UtcNow);

        Assert.True(firstReport.Applied);
        Assert.DoesNotContain(
            firstReport.Notes, n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));

        // Simulates a second process still mid-run on the same identity: claimed directly rather
        // than through a second concurrent Index() call, since this one already released its lock
        // (via `using`) the moment it returned above.
        var (heldByAnotherProcess, blockedBy) = IndexLock.TryClaim(sandbox.Home, identity, DateTimeOffset.UtcNow);
        Assert.NotNull(heldByAnotherProcess);
        Assert.Null(blockedBy);

        var secondReport = CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false),
            DateTimeOffset.UtcNow);

        Assert.False(secondReport.Applied);
        Assert.Equal(0, secondReport.FactsWritten);
        Assert.Contains(
            secondReport.Notes, n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));

        heldByAnotherProcess!.Dispose();
    }

    /// <summary>
    /// A pid guaranteed to have exited by the time it is returned — real rather than fabricated, so
    /// <see cref="ProcessStartToken.ForPid"/> reads exactly what it would for any other dead process
    /// on this platform, without a per-OS mock (D42).
    /// </summary>
    private static int SpawnAndWaitForExit()
    {
        var info = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("--version");

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start git");
        var pid = process.Id;
        process.WaitForExit(10_000);
        Assert.True(process.HasExited, "git --version did not exit within 10s");
        return pid;
    }
}
