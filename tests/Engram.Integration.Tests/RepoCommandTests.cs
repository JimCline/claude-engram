using System.Diagnostics;
using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class RepoCommandTests
{
    [Fact]
    public void Enroll_ARequestedPathWithNoEnclosingCheckout_Refuses()
    {
        using var sandbox = new SandboxHome();
        var bare = Path.Combine(sandbox.Home.Root, "not-a-checkout");
        Directory.CreateDirectory(bare);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["enroll", bare], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("not inside a git checkout", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_enrollment;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Enroll_ARequestedPathInsideAGitCheckout_RecordsEnrolledAndReportsSuccess()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["enroll", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Enrolled", stdout.ToString(), StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(RepoEnrollmentState.Enrolled, row.State);
    }

    [Fact]
    public void Decline_ARequestedPathInsideAGitCheckout_RecordsDeclined()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["decline", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Declined", stdout.ToString(), StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(RepoEnrollmentState.Declined, row.State);
    }

    [Fact]
    public void Later_ARequestedPathInsideAGitCheckout_RecordsDeferred()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["later", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Deferred", stdout.ToString(), StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(RepoEnrollmentState.Deferred, row.State);
    }

    [Fact]
    public void List_AnEnrolledRepo_PrintsItsStateAndRoot()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(root), root, DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["list"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("state: enrolled", output, StringComparison.Ordinal);
        Assert.Contains(root, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// D49: a dry run must never mutate. Every existing Reset coverage drove the --apply path
    /// through ApplyDecision directly; nothing exercised the branch that returns before it.
    /// </summary>
    [Fact]
    public void Reset_WithoutApply_DoesNotMutateAnExistingDecision()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(root), root, DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["reset", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run only", stdout.ToString(), StringComparison.Ordinal);

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(check));
        Assert.Equal(RepoEnrollmentState.Enrolled, row.State);
    }

    /// <summary>
    /// §7: three enrolled repos, stamped NULL / 2h old / 5m old against the 60-minute freshness
    /// interval — exactly the two due repos are serviced, most-neglected-first (the never-scanned
    /// one, then the 2h-stale one), and the repo scanned 5 minutes ago is not due and never appears.
    /// </summary>
    [Fact]
    public void IndexAll_SelectsDueReposInMostNeglectedFirstOrder_AndSkipsFreshOnes()
    {
        using var sandbox = new SandboxHome();

        var neverScanned = Path.Combine(sandbox.Home.Root, "never-scanned");
        var stale = Path.Combine(sandbox.Home.Root, "stale");
        var fresh = Path.Combine(sandbox.Home.Root, "fresh");
        Directory.CreateDirectory(neverScanned);
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);

        var neverScannedIdentity = CodeIndexer.ResolveIdentity(neverScanned);
        var staleIdentity = CodeIndexer.ResolveIdentity(stale);
        var freshIdentity = CodeIndexer.ResolveIdentity(fresh);
        var now = DateTimeOffset.UtcNow;

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, neverScannedIdentity, neverScanned, now);
            RepoEnrollment.Enroll(connection, staleIdentity, stale, now);
            RepoEnrollment.Enroll(connection, freshIdentity, fresh, now);

            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                RepoEnrollment.StampFullScan(connection, transaction, staleIdentity, now.AddHours(-2));
                RepoEnrollment.StampFullScan(connection, transaction, freshIdentity, now.AddMinutes(-5));
                transaction.Commit();
            }
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["index", "--all"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains(neverScannedIdentity, output, StringComparison.Ordinal);
        Assert.Contains(staleIdentity, output, StringComparison.Ordinal);
        Assert.DoesNotContain(freshIdentity, output, StringComparison.Ordinal);
        Assert.True(
            output.IndexOf(neverScannedIdentity, StringComparison.Ordinal) < output.IndexOf(staleIdentity, StringComparison.Ordinal),
            "the never-scanned repo (null stamp) must be serviced before the 2h-stale one");
        Assert.Contains("2 serviced", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7's "deleted root" scenario, adjusted to what <c>RepoFreshness.IsSelectable</c> (line
    /// 123-127) actually does: it excludes any row whose <c>last_root</c> is absent from disk
    /// before <c>Due()</c> ever returns a candidate, per its own comment ("An enrolled repo whose
    /// checkout is absent is deliberately not a candidate: a missing checkout is not a freshness
    /// problem"). So <c>IndexAll</c>'s own per-candidate <c>Directory.Exists</c>/skipped-absent
    /// handling never fires for a root deleted before the run starts — this proves the reachable
    /// half instead: the run still succeeds, with nothing to service, rather than crashing or
    /// misreporting one candidate as due. Whether IndexAll's own check is meant only for a root
    /// deleted mid-run (after Due() snapshots but before the loop reaches that candidate) is a
    /// question reported upward rather than decided here.
    /// </summary>
    [Fact]
    public void IndexAll_ARepoWhoseEnrolledRootNoLongerExistsOnDisk_IsExcludedUpstreamAndTheRunStillSucceeds()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);

        var identity = CodeIndexer.ResolveIdentity(root);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identity, root, DateTimeOffset.UtcNow);
        }

        Directory.Delete(root, recursive: true);

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["index", "--all"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.DoesNotContain(identity, output, StringComparison.Ordinal);
        Assert.Contains("0 serviced, 0 skipped (absent)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// §4.1/§7: a missing <c>--all</c> is a usage error (exit 2), distinct from an indexing failure
    /// (exit 1) — the split the file's other verbs already draw between "typed it wrong" and "the
    /// work failed", and one a test asserting only "non-zero" cannot catch if it breaks.
    /// </summary>
    [Fact]
    public void IndexAll_WithoutTheAllFlag_ExitsTwo()
    {
        using var sandbox = new SandboxHome();
        var stderr = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["index"], new StringWriter(), stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("--all", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// §7 Commit B: no store on disk and <c>--apply</c> absent exits 1, distinct from both the
    /// usage error above and a successful run — opening would create an empty database, and a dry
    /// run that leaves a file behind is not a dry run (<c>RepoCommand.cs:267-273</c>).
    /// </summary>
    [Fact]
    public void IndexAll_NoStoreOnDiskWithoutApply_ExitsOne()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var stderr = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["index", "--all"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("no store", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// §7 Commit B: a store predating the code-index tables makes <c>RepoFreshness.Due</c> — via
    /// <c>RepoEnrollment.ListAll</c> — throw a <c>SqliteException</c> naming <c>repo_enrollment</c>
    /// as missing (<c>RepoCommand.cs:287-296</c>). <c>EngramDatabase.Open</c> (the non-apply path)
    /// never migrates, so dropping the table after initializing leaves it genuinely absent rather
    /// than merely version-stamped down.
    /// </summary>
    [Fact]
    public void IndexAll_AStorePredatingTheCodeIndexTables_ExitsOne()
    {
        using var sandbox = new SandboxHome();

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE repo_enrollment;";
            command.ExecuteNonQuery();
        }

        var stderr = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["index", "--all"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("predates the code index tables", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// D53's guard, and per spec §7 "the most important test in the commit": a scan that hits its
    /// budget must not stamp <c>last_full_scan_at</c> or derive deletions from what it saw, because
    /// a partial file list cannot tell "not present" from "not yet reached". Falsify by deleting the
    /// <c>if (scan.Truncated)</c> branch in <c>CodeIndexer.Index</c> — both assertions must go red.
    /// </summary>
    [Fact]
    public void Freshen_ATruncatedScan_LeavesLastFullScanAtNullAndDerivesNoDeletions()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);

        // No git init: RepoScanner falls through to Walk for a plain directory, where
        // ScanBudget.MaxFiles binds deterministically. A git-listed scan is never truncated by
        // MaxFiles (D53), so this could not be forced reliably against a git checkout.
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "c.txt"), "c");

        var identity = CodeIndexer.ResolveIdentity(root);
        var now = DateTimeOffset.UtcNow;

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var home = sandbox.Home;
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = IndexingSettings.Read(config);

        RepoEnrollment.Enroll(connection, identity, root, now);

        var report = RepoIndexRun.Freshen(
            connection, home, config, settings, root, identity, apply: true,
            budget: new ScanBudget(TimeSpan.FromSeconds(20), 1), now);

        Assert.Contains(
            report.Notes,
            n => n.Contains("skipped deletions, because a partial scan cannot show a file is gone", StringComparison.Ordinal));
        Assert.Equal(0, report.Deleted);
        Assert.Null(ReadLastFullScanAt(connection, identity));
    }

    /// <summary>
    /// A caller of <see cref="RepoIndexRun.Freshen"/> passes the identity its
    /// <c>repo_enrollment</c> row is keyed under; that must be what gets stamped, never whatever
    /// <see cref="CodeIndexer.ResolveIdentity"/> recomputes from <c>root</c> internally — the two can
    /// disagree (a fresh git remote lookup returning something other than what the row was enrolled
    /// under), and an identity mismatch makes the <c>UPDATE ... WHERE identity = $identity</c> in
    /// <see cref="RepoEnrollment.StampFullScan"/> match no row while the scan itself still reports
    /// success. Falsify by reverting the stamp site in <c>CodeIndexer.Index</c> to stamp under the
    /// locally resolved <c>identity</c> instead of <c>options.EnrolledIdentity ?? identity</c> — this
    /// must go red, because the enrolled identity here is deliberately not what a fresh
    /// <c>ResolveIdentity(root)</c> call on this plain directory would produce.
    /// </summary>
    [Fact]
    public void Freshen_StampsUnderTheEnrolledIdentity_NotARecomputedOne()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");

        // ResolveIdentity(root) on a plain directory with no git remote returns root itself — so an
        // enrollment under any other string reproduces the drift a stale or differently-derived
        // enrolled identity would cause.
        var enrolledIdentity = "mismatched-identity-not-derivable-from-root";
        var now = DateTimeOffset.UtcNow;

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var home = sandbox.Home;
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = IndexingSettings.Read(config);

        RepoEnrollment.Enroll(connection, enrolledIdentity, root, now);

        RepoIndexRun.Freshen(connection, home, config, settings, root, enrolledIdentity, apply: true, budget: null, now);

        Assert.NotNull(ReadLastFullScanAt(connection, enrolledIdentity));

        // The hook-readable mirror of that stamp (code-nav-adoption-spec L1) must land under the
        // same identity, or the lookup nudge reports a repo the table never heard of.
        var stamp = RepoIndexStamp.Read(home.RepoIndexStampPath, root);
        Assert.Equal(enrolledIdentity, stamp?.Identity);
        Assert.Equal(now.ToUnixTimeSeconds(), stamp?.LastIndexedAt);
    }

    /// <summary>
    /// Every enrollment verb writes the file stamp the lookup-nudge hook reads instead of the
    /// table (D4/D66), and it is written here — the one point below both the CLI verb and the MCP
    /// tool — so the file and the table cannot disagree by having different authors.
    /// </summary>
    [Theory]
    [InlineData("enroll", RepoEnrollmentState.Enrolled)]
    [InlineData("decline", RepoEnrollmentState.Declined)]
    [InlineData("later", RepoEnrollmentState.Deferred)]
    public void ApplyDecision_WritesTheFileStamp(string decision, RepoEnrollmentState expected)
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var result = RepoCommand.ApplyDecision(sandbox.Home, connection, root, decision, "cli", now);

        var stamp = RepoIndexStamp.Read(sandbox.Home.RepoIndexStampPath, root);
        Assert.NotNull(stamp);
        Assert.Equal(result.Identity, stamp.Identity);
        Assert.Equal(expected, stamp.State);
        Assert.Equal(now.ToUnixTimeSeconds(), stamp.DecidedAt);
        Assert.Null(stamp.LastIndexedAt);
    }

    [Fact]
    public void ApplyDecision_Reset_ReturnsTheStampToNeverAsked()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RepoCommand.ApplyDecision(sandbox.Home, connection, root, "enroll", "cli", now);
        RepoCommand.ApplyDecision(sandbox.Home, connection, root, "reset", "cli", now.AddMinutes(1));

        var stamp = RepoIndexStamp.Read(sandbox.Home.RepoIndexStampPath, root);
        Assert.NotNull(stamp);
        Assert.Null(stamp.State);
    }

    private static long? ReadLastFullScanAt(SqliteConnection connection, string identity)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_full_scan_at FROM repo_enrollment WHERE identity = $identity;";
        command.Parameters.AddWithValue("$identity", identity);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static bool GitInit(string directory)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The obligation deferred from commit B (§6.4): once <c>IndexLock</c> exists, a locked repo
    /// must count toward <c>skippedLocked</c> and the whole pass must exit non-zero for it, not just
    /// for a genuine failure. Falsifiable by reverting the aggregate back to <c>failed &gt; 0</c>
    /// alone.
    /// </summary>
    [Fact]
    public void IndexAll_ARepoLockedByAnotherProcess_CountsAsSkippedLocked_AndExitsNonZero()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "locked-repo");
        Directory.CreateDirectory(repo);

        // RepoEnrollment stores (and RepoFreshness.Due hands back) the canonicalized root, so the
        // identity claimed here must be resolved from that same canonical form — otherwise this
        // process's held lock and the one CodeIndexer.Index recomputes from the enrolled candidate
        // never collide.
        var identity = CodeIndexer.ResolveIdentity(PathCanonicalizer.Canonical(repo));
        var now = DateTimeOffset.UtcNow;

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identity, repo, now);
        }

        var (held, _) = IndexLock.TryClaim(sandbox.Home, identity, now);
        Assert.NotNull(held);

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["index", "--all", "--apply"], stdout, new StringWriter());

        Assert.Equal(1, exitCode);
        var output = stdout.ToString();
        Assert.Contains(
            $"{identity}: skipped: another process is indexing this repo", output, StringComparison.Ordinal);
        Assert.Contains("0 serviced", output, StringComparison.Ordinal);
        Assert.Contains("1 skipped (locked)", output, StringComparison.Ordinal);

        held!.Dispose();
    }
}
