using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class IndexCommandTests
{
    // --auto is the detached maintenance child. Everything it declines, it must decline
    // as silent success: a hook's housekeeping choosing not to run is not an error, and
    // any output would go to a descriptor nobody reads.

    [Fact]
    public void Auto_OutsideAGitCheckout_DeclinesSilently_AndRegistersNothing()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "just-a-directory");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "not a repo");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", directory, "--drain", "--apply", "--auto"],
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_registry;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Auto_WhenTheConfigDisablesIt_DeclinesSilently_EvenInARealCheckout()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "A real checkout.\n");
        if (!GitInit(repo))
        {
            return;
        }

        // The target being a genuine checkout is what makes this falsifiable: with the
        // config gate deleted, the run would proceed to index and register the repo.
        var config = File.ReadAllText(sandbox.Home.ConfigPath);
        Assert.Contains("auto_index_on_session_start = true", config, StringComparison.Ordinal);
        File.WriteAllText(
            sandbox.Home.ConfigPath,
            config.Replace("auto_index_on_session_start = true", "auto_index_on_session_start = false"));

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repo, "--drain", "--apply", "--auto"],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_registry;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Auto_AnUnenrolledCheckout_DeclinesSilently_EvenWithAutoIndexEnabled()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "A real checkout, never enrolled.\n");
        if (!GitInit(repo))
        {
            return;
        }

        // Config stays at its default (auto_index_on_session_start = true) and the checkout is
        // genuine, so only the enrollment conjunct can be what stops this from indexing.
        var stdout = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repo, "--drain", "--apply", "--auto"],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_registry;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private static bool GitInit(string directory)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = System.Diagnostics.Process.Start(info);
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

    private static bool GitRemoteAddOrigin(string directory, string url)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("remote");
            info.ArgumentList.Add("add");
            info.ArgumentList.Add("origin");
            info.ArgumentList.Add(url);

            using var process = System.Diagnostics.Process.Start(info);
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
    /// spec §8.3 guard 12c: <see cref="RepoEnrollment.IsEnrolled"/> runs only behind the
    /// <c>--auto</c> gate (IndexCommand.cs:71-88), so a commanded pass (<c>--apply</c>, no
    /// <c>--auto</c>) exercises nothing on this path but <see cref="CodeIndexer"/>'s own
    /// <c>last_root</c> repair (CodeIndexer.cs:88-92) — singly falsifiable, unlike the original
    /// guard 12, which stayed green when that repair was deleted because <c>IsEnrolled</c> stamps
    /// the same column on the <c>--auto</c> path instead (seventh amendment, spec item 12).
    /// A fake origin remote is required so the checkout's identity survives the move below:
    /// <see cref="CodeIndexer.ResolveIdentity"/> falls back to the raw root path when there is no
    /// remote, which would make the moved checkout resolve to a different identity than the one
    /// it enrolled under and defeat the fixture.
    /// </summary>
    [Fact]
    public void ACommandedPass_RepairsLastRootAfterAMove()
    {
        using var sandbox = new SandboxHome();
        var originalRoot = Path.Combine(sandbox.Home.Root, "checkout");
        var movedRoot = Path.Combine(sandbox.Home.Root, "checkout-moved");
        Directory.CreateDirectory(originalRoot);
        File.WriteAllText(Path.Combine(originalRoot, "README.md"), "A real checkout.\n");
        if (!GitInit(originalRoot) || !GitRemoteAddOrigin(originalRoot, "https://example.invalid/repo.git"))
        {
            return;
        }

        var identity = CodeIndexer.ResolveIdentity(originalRoot);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identity, originalRoot, DateTimeOffset.UtcNow);
        }

        Directory.Move(originalRoot, movedRoot);

        // Pins the fixture: last_root still names the pre-move location, so the cache-only
        // lookup a session-start hook would use misses at the new location until the repair
        // below runs.
        using (var beforeConnection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Assert.Null(RepoEnrollment.ByRoot(beforeConnection, movedRoot));
        }

        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", movedRoot, "--apply"],
            new StringWriter(),
            new StringWriter());
        Assert.Equal(0, exitCode);

        using var afterConnection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.NotNull(RepoEnrollment.ByRoot(afterConnection, movedRoot));
    }

    /// <summary>
    /// spec §8.3 guard 12a: <see cref="HookCommand.ShouldOfferEnrollment"/> answers only from
    /// the cache-only <see cref="RepoEnrollment.ByRoot"/> lookup — it must never repair
    /// <c>last_root</c> itself, since that repair rides the detached maintenance child's
    /// <c>--auto</c> pass, off the session-start hook's own clock (D4). A moved checkout
    /// therefore reprompts every session until that child's next pass lands, which this guard
    /// asserts is the design rather than a defect: the hook is asked twice with no index pass
    /// between the calls, and it must offer enrollment both times with <c>last_root</c> still
    /// unrepaired. Calls <see cref="HookCommand.ShouldOfferEnrollment"/> directly with an
    /// explicit <c>startDirectory</c> (§6.13) rather than through
    /// <c>CliApp.Run("hook", "session-start")</c>: driving the real hook would need either a
    /// process-wide <c>Directory.SetCurrentDirectory</c> — unsafe, since this test class runs
    /// alongside others in the same collection and other cwd readers (<c>DoctorCommand</c>,
    /// <c>IndexCommand</c>, <c>ScanCommand</c>, <c>RepoCommand</c>) would see it too — or a
    /// <c>Console.In</c> swap, the identical hazard.
    /// </summary>
    [Fact]
    public void SessionStart_KeepsAskingAfterAMove_UntilTheDetachedRepairLands()
    {
        using var sandbox = new SandboxHome();
        var originalRoot = Path.Combine(sandbox.Home.Root, "checkout");
        var movedRoot = Path.Combine(sandbox.Home.Root, "checkout-moved");
        Directory.CreateDirectory(originalRoot);
        File.WriteAllText(Path.Combine(originalRoot, "README.md"), "A real checkout.\n");
        if (!GitInit(originalRoot) || !GitRemoteAddOrigin(originalRoot, "https://example.invalid/repo.git"))
        {
            return;
        }

        var identity = CodeIndexer.ResolveIdentity(originalRoot);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RepoEnrollment.Enroll(connection, identity, originalRoot, DateTimeOffset.UtcNow);

        Directory.Move(originalRoot, movedRoot);

        var now = DateTimeOffset.UtcNow;

        Assert.True(HookCommand.ShouldOfferEnrollment(connection, config: null, now, movedRoot));
        Assert.Null(RepoEnrollment.ByRoot(connection, movedRoot));

        Assert.True(HookCommand.ShouldOfferEnrollment(connection, config: null, now, movedRoot));
        Assert.Null(RepoEnrollment.ByRoot(connection, movedRoot));
    }

    /// <summary>
    /// spec §8.3 guard 12b: the promise actually given to the user — a moved checkout stops
    /// reprompting once the detached maintenance child's <c>--auto</c> pass runs. Drives the
    /// exact argv that pass uses (<c>--drain-all --apply --auto</c>) rather than calling
    /// <see cref="CodeIndexer.Index"/> directly, which would bypass the <c>--auto</c> gate
    /// (IndexCommand.cs:71-89) and exercise a path no ordinary session takes. Falsifying this
    /// guard needs deleting <em>both</em> stamps on <c>last_root</c> —
    /// <see cref="RepoEnrollment.IsEnrolled"/>'s (RepoEnrollment.cs:101) and
    /// <see cref="CodeIndexer"/>'s own repair (CodeIndexer.cs:88-92) — because either alone
    /// leaves the other one repairing it and the guard green; that a falsify arm needs two
    /// deletions is recorded, not hidden, as the direct consequence of two stamps with
    /// different reachability (guard 12c isolates the second in a commanded pass, where the
    /// first never runs).
    /// </summary>
    [Fact]
    public void TheAutoPass_StopsThePromptAfterAMove()
    {
        using var sandbox = new SandboxHome();
        var originalRoot = Path.Combine(sandbox.Home.Root, "checkout");
        var movedRoot = Path.Combine(sandbox.Home.Root, "checkout-moved");
        Directory.CreateDirectory(originalRoot);
        File.WriteAllText(Path.Combine(originalRoot, "README.md"), "A real checkout.\n");
        if (!GitInit(originalRoot) || !GitRemoteAddOrigin(originalRoot, "https://example.invalid/repo.git"))
        {
            return;
        }

        var identity = CodeIndexer.ResolveIdentity(originalRoot);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identity, originalRoot, DateTimeOffset.UtcNow);
        }

        Directory.Move(originalRoot, movedRoot);

        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", movedRoot, "--drain-all", "--apply", "--auto"],
            new StringWriter(),
            new StringWriter());
        Assert.Equal(0, exitCode);

        using var connectionAfter = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.NotNull(RepoEnrollment.ByRoot(connectionAfter, movedRoot));

        var now = DateTimeOffset.UtcNow;
        Assert.False(HookCommand.ShouldOfferEnrollment(connectionAfter, config: null, now, movedRoot));
    }

    [Fact]
    public void WithoutAuto_ANonStoreHome_IsARealError()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", sandbox.Home.Root],
            new StringWriter(),
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("engram init", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A pathless spool entry legitimately escalates the invoked root's own pass to a full
    /// scan (CodeIndexerTests pins that mechanism directly). --drain-all's secondary roots must
    /// never see it: DrainOtherEnrolledRoots hands them the queue's WithoutPathless() view
    /// precisely so one pathless entry cannot escalate every enrolled repo in the same pass
    /// (§6.3e, D41).
    /// </summary>
    [Fact]
    public void APathlessQueueEntry_EscalatesTheInvokedRootOnly()
    {
        using var sandbox = new SandboxHome();
        var repoA = Path.Combine(sandbox.Home.Root, "repo-a");
        var repoB = Path.Combine(sandbox.Home.Root, "repo-b");
        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(repoB);
        File.WriteAllText(Path.Combine(repoA, "a.cs"), "class A {}\n");
        File.WriteAllText(Path.Combine(repoB, "b.cs"), "class B {}\n");

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var now = DateTimeOffset.UtcNow;
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repoA), repoA, now);
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repoB), repoB, now);
        }

        // Baselines for both, so the pass under test is what decides fullness — not either
        // repo's own first-index behavior, which forces a full scan on its own account.
        Assert.Equal(0, CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repoA, "--apply", "--full"], new StringWriter(), new StringWriter()));
        Assert.Equal(0, CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repoB, "--apply", "--full"], new StringWriter(), new StringWriter()));

        Spool(sandbox.Home.QueueDir, DateTimeOffset.UtcNow, path: null);

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repoA, "--drain-all", "--apply"],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);

        var output = stdout.ToString();
        var splitAt = output.IndexOf(repoB, StringComparison.Ordinal);
        Assert.True(splitAt > 0, $"expected {repoB} to be reported: {output}");
        var repoASection = output[..splitAt];
        var repoBSection = output[splitAt..];

        Assert.Contains("full scan", repoASection, StringComparison.Ordinal);
        Assert.Contains("queue drain", repoBSection, StringComparison.Ordinal);
        Assert.DoesNotContain("full scan", repoBSection, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mixed backlog drains in one pass (spec §8.3 guard 9) — reproduces the live
    /// 272-entry queue bug in a sandbox. Five roots share one --drain-all pass: two enrolled
    /// and present, one enrolled with its checkout gone, two never enrolled at all, plus one
    /// pathless entry and one unreadable entry — the two entries DiscardExcept must never
    /// destroy on its own, for different reasons. The pathless entry is discharged by the
    /// invoked root's own escalated full scan (CodeIndexer.cs:115, :235) before DiscardExcept
    /// ever runs, so its file is gone by the time the pass ends even though
    /// SpoolQueue.Pathless still counts it — Consume never mutates the snapshot (guard
    /// 15(i)). Only the unreadable entry has no repo that can ever discharge it, so it is
    /// the pass's one genuine survivor.
    /// </summary>
    [Fact]
    public void AMixedBacklog_DrainsInOnePass()
    {
        using var sandbox = new SandboxHome();
        var repoA = Path.Combine(sandbox.Home.Root, "repo-a");
        var repoB = Path.Combine(sandbox.Home.Root, "repo-b");
        var repoC = Path.Combine(sandbox.Home.Root, "repo-c-absent");
        var repoD = Path.Combine(sandbox.Home.Root, "repo-d-unenrolled");
        var repoE = Path.Combine(sandbox.Home.Root, "repo-e-unenrolled");

        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(repoB);
        Directory.CreateDirectory(repoC);
        Directory.CreateDirectory(repoD);
        Directory.CreateDirectory(repoE);
        File.WriteAllText(Path.Combine(repoA, "a.cs"), "class A {}\n");
        File.WriteAllText(Path.Combine(repoB, "b.cs"), "class B {}\n");
        File.WriteAllText(Path.Combine(repoD, "d.cs"), "class D {}\n");
        File.WriteAllText(Path.Combine(repoE, "e.cs"), "class E {}\n");

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var now = DateTimeOffset.UtcNow;
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repoA), repoA, now);
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repoB), repoB, now);
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repoC), repoC, now);
        }

        // Enrolled but the checkout is gone by the time the pass runs — deliberately not
        // serviced (§4.9). This is what makes servicedRoots' accumulate-not-re-derive property
        // (§6.3e property 5) observable: a re-derived set would still contain repoC and its
        // entries would wrongly survive DiscardExcept.
        Directory.Delete(repoC, recursive: true);

        // Baselines, so the pass under test is what decides full vs. drain for repoA/repoB —
        // not either repo's own first-index behavior.
        Assert.Equal(0, CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repoA, "--apply", "--full"], new StringWriter(), new StringWriter()));
        Assert.Equal(0, CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repoB, "--apply", "--full"], new StringWriter(), new StringWriter()));

        // Changed after the baseline, so the pass under test has a genuine reindex to prove —
        // distinct from a file merely considered and found unchanged.
        File.WriteAllText(Path.Combine(repoA, "a.cs"), "class A { void M() {} }\n");
        File.WriteAllText(Path.Combine(repoB, "b.cs"), "class B { void N() {} }\n");

        var queueDir = sandbox.Home.QueueDir;
        Spool(queueDir, DateTimeOffset.UtcNow, Path.Combine(repoA, "a.cs"));
        Spool(queueDir, DateTimeOffset.UtcNow, Path.Combine(repoB, "b.cs"));
        Spool(queueDir, DateTimeOffset.UtcNow, Path.Combine(repoC, "c.cs"));
        Spool(queueDir, DateTimeOffset.UtcNow, Path.Combine(repoD, "d.cs"));
        Spool(queueDir, DateTimeOffset.UtcNow, Path.Combine(repoE, "e.cs"));
        Spool(queueDir, DateTimeOffset.UtcNow, path: null);

        // Seeded directly rather than through Spool: this entry needs to be lockable, not
        // merely pathless. SpoolQueue.Peek classifies a File.ReadAllText failure as
        // Unreadable; malformed-but-readable bytes would classify as Garbage instead, which
        // Consume treats as fair game from any root (AGarbageEntry_IsConsumableFromAnyRoot in
        // SpoolQueueTests) — the wrong fixture for this guard.
        var unreadable = Path.Combine(queueDir, "0000000000-locked.spool");
        File.WriteAllText(unreadable, "irrelevant");
        Assert.Equal(7, Directory.GetFiles(queueDir, "*.spool").Length);

        var stdout = new StringWriter();
        int exitCode;
        using (new FileStream(unreadable, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The lock must span the run itself, not just the seeding — SpoolQueue.Peek reads
            // every file up front, and a lock released before CliApp.Run would let the read
            // through. FileShare.None is a mandatory lock on Windows;
            // SpoolQueueTests.DiscardExcept_DoesNotDeleteAnUnreadableEntry already establishes
            // that a separate File.ReadAllText handle collides with it on this repo's actual
            // CI platforms too. If that ever stopped holding, this entry would be reclassified
            // as Garbage and consumed during repoA's own pass, and the single-survivor
            // assertion below would go to zero rather than pass silently.
            exitCode = CliApp.Run(
                ["--home", sandbox.Home.Root, "index", repoA, "--drain-all", "--apply"],
                stdout,
                new StringWriter());
        }

        Assert.Equal(0, exitCode);

        // The unreadable entry is the pass's one genuine survivor — DiscardExcept never
        // touches it (D41), and no repo's Consume ever can either.
        var remaining = Directory.GetFiles(queueDir, "*.spool");
        Assert.Equal(unreadable, Assert.Single(remaining));

        var output = stdout.ToString();

        // Neither unenrolled root is ever indexed or reported — DrainOtherEnrolledRoots walks
        // RepoEnrollment.ListAll, never the queue's own paths, so an unenrolled root's spool
        // entry can only ever be discarded, never drained.
        Assert.DoesNotContain(repoD, output, StringComparison.Ordinal);
        Assert.DoesNotContain(repoE, output, StringComparison.Ordinal);

        var splitAt = output.IndexOf(repoB, StringComparison.Ordinal);
        Assert.True(splitAt > 0, $"expected {repoB} to be reported: {output}");
        var repoASection = output[..splitAt];
        var repoBSection = output[splitAt..];

        // The pathless entry escalates the invoked root's own pass to a full scan
        // (CodeIndexer.cs:115). "1 analyzed" on each side is the actual reindex evidence — a
        // file merely considered and found unchanged would not appear here.
        Assert.Contains("full scan", repoASection, StringComparison.Ordinal);
        Assert.Contains("1 analyzed", repoASection, StringComparison.Ordinal);
        Assert.Contains("1 analyzed", repoBSection, StringComparison.Ordinal);

        // repoC/repoD/repoE together are the 3 entries DiscardExcept removes as the complement
        // of what this pass serviced. The pathless entry's file is already gone by this point
        // — the invoked root's own Consume call deleted it earlier — so this line has nothing
        // to report for it; only the unreadable count appears. This is the only assertion
        // anywhere on this summary line, so it is what stops a third number regrowing here.
        Assert.Contains(
            "drain-all: 3 entries discarded for unenrolled or absent repos, "
                + "1 entry left behind (unreadable)",
            output,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// DiscardExcept mutates disk state, so the whole secondary-root pass — not just the
    /// reindex — is gated on --apply the same way Consume already was (D49): a dry run must
    /// not move state.
    /// </summary>
    [Fact]
    public void DrainAll_WithoutApply_DeletesNoSpoolFiles()
    {
        using var sandbox = new SandboxHome();
        var repoA = Path.Combine(sandbox.Home.Root, "repo-a");
        var unenrolled = Path.Combine(sandbox.Home.Root, "repo-unenrolled");
        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(unenrolled);
        File.WriteAllText(Path.Combine(repoA, "a.cs"), "class A {}\n");
        File.WriteAllText(Path.Combine(unenrolled, "u.cs"), "class U {}\n");

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(repoA), repoA, DateTimeOffset.UtcNow);
        }

        var queueDir = sandbox.Home.QueueDir;
        Spool(queueDir, DateTimeOffset.UtcNow, Path.Combine(repoA, "a.cs"));
        Spool(queueDir, DateTimeOffset.UtcNow, Path.Combine(unenrolled, "u.cs"));
        var before = Directory.GetFiles(queueDir, "*.spool").Length;

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repoA, "--drain-all"],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(before, Directory.GetFiles(queueDir, "*.spool").Length);
        Assert.Contains("Dry run only", stdout.ToString(), StringComparison.Ordinal);
    }

    private static void Spool(string queueDir, DateTimeOffset at, string? path)
    {
        Directory.CreateDirectory(queueDir);
        var file = Path.Combine(queueDir, $"{at.UtcDateTime.Ticks}-{Environment.ProcessId}-{Guid.NewGuid():N}.spool");
        File.WriteAllText(file, at.ToString("o") + "\n" + (path is null ? string.Empty : path + "\n"));
    }
}
