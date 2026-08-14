using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3. Two <c>engram index --apply --full</c> runs against one repo, started together, must
/// leave every home satisfying <c>ux_fact_live</c>'s invariant: the index itself still exists with
/// its <c>WHERE valid_to IS NULL</c> predicate intact, and no subject+predicate pair holds more than
/// one live fact.
/// </summary>
/// <remarks>
/// Asserts that property directly, in every home, rather than by comparing the concurrent home's
/// live set against the serial home's: a deterministic bug in shared write code lands identically on
/// both arms, so a cross-arm equality can never move to catch it. Cross-arm comparison — closed-fact-
/// count equality — is a second, narrower assertion below it: NE-1 measured concurrent and serial runs
/// agreeing on the live set while disagreeing on closed count (81 vs 80 in one run), and
/// <c>IndexLock</c> (commit E) is what fixes that disagreement, not the live-set guard above. Modeled
/// on the arm 1a shape proven at <c>/tmp/engram-ne1-run.sh</c>: enroll into two disposable homes, wait
/// out each home's auto-spawned first index, on its lock rather than its stamp, so it cannot hold the
/// lock the controlled runs need, mutate the repo once, then run the two controlled scans concurrently
/// against one home and serially against the other.
/// </remarks>
public class ConcurrentIndexConvergenceTests
{
    private static readonly Lazy<bool> Sqlite3Available = new(() =>
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("sqlite3", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            probe?.WaitForExit(5_000);
            return probe is { HasExited: true, ExitCode: 0 };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    [Fact(Timeout = 300_000)]
    public async Task ConcurrentFullScans_LeaveOneLiveFactPerSubjectAndPredicate()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        Assert.SkipUnless(Sqlite3Available.Value, "sqlite3 is not on PATH");

        using var concurrentHome = new TestHome();
        using var serialHome = new TestHome();

        var repo = BuildTargetRepo();
        try
        {
            EnrollAndWaitForAutoIndex(concurrentHome.Root, repo);
            EnrollAndWaitForAutoIndex(serialHome.Root, repo);

            MutateRepo(repo);

            var concurrentRun1 = Task.Run(() => EngramProcess.Run(concurrentHome.Root, "index", "--apply", "--full", repo));
            var concurrentRun2 = Task.Run(() => EngramProcess.Run(concurrentHome.Root, "index", "--apply", "--full", repo));
            var concurrentResults = await Task.WhenAll(concurrentRun1, concurrentRun2);
            // §6.4: two commanded runs against one identity, so IndexLock lets exactly one proceed
            // and the loser exits 1 with the contention note. Both exiting 0 is legal — the two
            // processes need not overlap — but a 1 that does not carry the note is an ordinary
            // failure wearing the lock's exit code, which is the only way this pair goes green on a
            // broken build. Whether the lock actually blocks is Tier 2's to prove, deterministically;
            // do not tighten this into a flaky restatement of it.
            Assert.All(concurrentResults, r => Assert.True(
                r.ExitCode is 0 or 1,
                $"concurrent run exited {r.ExitCode}, which is neither success nor a lock skip: {r.Stderr}"));
            Assert.Contains(concurrentResults, r => r.ExitCode == 0);
            Assert.All(
                concurrentResults.Where(r => r.ExitCode == 1),
                r => Assert.Contains("another process is indexing this repo", r.Stdout, StringComparison.Ordinal));

            var serialRun1 = EngramProcess.Run(serialHome.Root, "index", "--apply", "--full", repo);
            Assert.True(serialRun1.ExitCode == 0, $"serial run 1 exited {serialRun1.ExitCode}: {serialRun1.Stderr}");
            var serialRun2 = EngramProcess.Run(serialHome.Root, "index", "--apply", "--full", repo);
            Assert.True(serialRun2.ExitCode == 0, $"serial run 2 exited {serialRun2.ExitCode}: {serialRun2.Stderr}");

            AssertUxFactLiveHolds(concurrentHome.Root);
            AssertUxFactLiveHolds(serialHome.Root);

            // The assertion IndexLock exists for (§7): without it, NE-1 measured the concurrent arm
            // closing one more fact than the serial arm (81 vs 80) — a fact one sibling process wrote
            // and the other, racing it, immediately superseded. Equal live sets on both arms cannot
            // see that; only the closed count can.
            Assert.Equal(CountClosedFacts(serialHome.Root), CountClosedFacts(concurrentHome.Root));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static string BuildTargetRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "engram-e2e-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            RunGit(repo, "init", "-q");
            RunGit(repo, "config", "user.email", "t@t.com");
            RunGit(repo, "config", "user.name", "test");

            for (var i = 1; i <= 300; i++)
            {
                var dir = Path.Combine(repo, $"dir{i % 20}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"file{i}.txt"), $"line one\nline two file {i}\nline three\n");
            }

            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-q", "-m", "init");
            return repo;
        }
        catch
        {
            Directory.Delete(repo, recursive: true);
            throw;
        }
    }

    private static void MutateRepo(string repo)
    {
        var tracked = RunGit(repo, "ls-files")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(80);

        foreach (var relative in tracked)
        {
            File.AppendAllText(Path.Combine(repo, relative), $"modified line {DateTime.UtcNow.Ticks}\n");
        }

        for (var i = 1; i <= 20; i++)
        {
            var dir = Path.Combine(repo, $"dir{i % 20}");
            File.WriteAllText(Path.Combine(dir, $"newfile{i}.txt"), $"new file {i}\n");
        }

        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "churn");
    }

    private static void EnrollAndWaitForAutoIndex(string home, string repo)
    {
        var (exitCode, _, stderr) = EngramProcess.Run(home, "repo", "enroll", repo);
        Assert.True(exitCode == 0, $"repo enroll exited {exitCode}: {stderr}");

        var databasePath = Path.Combine(home, "engram.db");
        var stamped = false;
        for (var i = 0; i < 60; i++)
        {
            var stamp = RunSqlite(databasePath, "SELECT last_full_scan_at FROM repo_enrollment LIMIT 1;");
            if (!string.IsNullOrWhiteSpace(stamp))
            {
                stamped = true;
                break;
            }

            Thread.Sleep(500);
        }

        Assert.True(stamped, $"auto-spawned enrollment index never stamped last_full_scan_at within 30s for {home}");

        // The stamp commits inside CodeIndexer.Index before the lock releases in Index's finally,
        // so polling the stamp alone can leave the auto-index still holding the lock — and make
        // both controlled runs below lose it. Poll the lock directory itself instead.
        var locks = Path.Combine(home, "locks");
        var released = false;
        for (var i = 0; i < 60; i++)
        {
            if (!Directory.Exists(locks) || Directory.GetFiles(locks, "*.lock").Length == 0)
            {
                released = true;
                break;
            }

            Thread.Sleep(500);
        }

        Assert.True(released, $"the enrollment index still holds an index lock after 30s in {home}");
    }

    private static void AssertUxFactLiveHolds(string home)
    {
        var databasePath = Path.Combine(home, "engram.db");

        var indexSql = RunSqlite(
            databasePath,
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'ux_fact_live';");
        Assert.True(
            indexSql.Contains("UNIQUE", StringComparison.Ordinal)
                && indexSql.Contains("subject_id", StringComparison.Ordinal)
                && indexSql.Contains("predicate", StringComparison.Ordinal)
                && indexSql.Contains("valid_to IS NULL", StringComparison.Ordinal),
            $"ux_fact_live is missing or weakened in {home}: '{indexSql.Trim()}'");

        var duplicates = RunSqlite(
            databasePath,
            "SELECT subject_id, predicate FROM fact WHERE valid_to IS NULL "
                + "GROUP BY subject_id, predicate HAVING count(*) > 1;");
        Assert.True(
            string.IsNullOrWhiteSpace(duplicates),
            $"duplicate live fact(s) for one subject+predicate in {home}:\n{duplicates}");
    }

    private static int CountClosedFacts(string home)
    {
        var databasePath = Path.Combine(home, "engram.db");
        var count = RunSqlite(databasePath, "SELECT count(*) FROM fact WHERE valid_to IS NOT NULL;");
        return int.Parse(count.Trim());
    }

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start git");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        Assert.True(process.HasExited && process.ExitCode == 0, $"git {string.Join(' ', args)} failed");
        return stdout;
    }

    private static string RunSqlite(string databasePath, string query)
    {
        var info = new ProcessStartInfo("sqlite3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-cmd");
        info.ArgumentList.Add(".timeout 5000");
        info.ArgumentList.Add(databasePath);
        info.ArgumentList.Add(query);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("failed to start sqlite3");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        Assert.True(
            process.HasExited && process.ExitCode == 0,
            $"sqlite3 query failed (exit {process.ExitCode}): {query}\n{stderr}");
        return stdout;
    }
}
