using System.Diagnostics;
using System.Text;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. Half of this is a real <c>git</c> process against a real checkout, because the claim
/// being made — that git already knows what to exclude — is a claim about git, not about a stub.
/// </summary>
public class RepoScannerTests
{
    private sealed class TempRepo : IDisposable
    {
        public TempRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), $"engram-scan-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void WriteBytes(string relative, byte[] content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        public bool Git(params string[] arguments)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(info);
                if (process is null)
                {
                    return false;
                }

                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(20_000);
                return process.ExitCode == 0;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        public bool InitRepo() =>
            Git("init", "--quiet")
            && Git("config", "user.email", "test@example.invalid")
            && Git("config", "user.name", "Engram Test");

        /// <summary>Removes the checkout, including the parts git deliberately made read-only.</summary>
        /// <remarks>
        /// git marks loose objects and pack files read-only once written. On Unix that is irrelevant
        /// — permission to unlink comes from the directory — but on Windows the read-only attribute
        /// belongs to the file and a recursive delete stops on it with
        /// <c>UnauthorizedAccessException</c>, which this used to let through because it caught only
        /// <c>IOException</c>. Clearing the bit is the fix; the catch is the net, and it still
        /// declines to fail a test over a leftover temp directory.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    private sealed class StubLister(params string[] files) : IFileLister
    {
        public IReadOnlyList<string>? List(string root) => files;
    }

    private sealed class NotACheckout : IFileLister
    {
        public IReadOnlyList<string>? List(string root) => null;
    }

    private static IndexingSettings Settings => IndexingSettings.Default;

    // ---- the git claim, made against real git ----

    /// <summary>
    /// The load-bearing claim: git already excludes build output and dependency directories,
    /// through nested ignore files, without Engram listing any of them.
    /// </summary>
    [Fact]
    public void Scan_InACheckout_ExcludesEverythingGitIgnores()
    {
        using var repo = new TempRepo();
        Assert.SkipUnless(repo.InitRepo(), "git is not available on this machine.");

        repo.Write(".gitignore", "dist/\n*.log\n");
        repo.Write("src/app.ts", "export const a = 1;\n");
        repo.Write("src/.gitignore", "generated.ts\n");
        repo.Write("src/generated.ts", "// machine written\n");
        repo.Write("dist/bundle.js", "console.log(1);\n");
        repo.Write("debug.log", "noise\n");

        var scan = RepoScanner.Scan(repo.Root, Settings with { Ignore = [] });

        Assert.Equal(ScanSource.Git, scan.Source);
        Assert.Contains("src/app.ts", scan.Files);
        Assert.Contains(".gitignore", scan.Files);
        Assert.DoesNotContain("dist/bundle.js", scan.Files);
        Assert.DoesNotContain("debug.log", scan.Files);
        Assert.DoesNotContain("src/generated.ts", scan.Files);
    }

    /// <summary>
    /// A file written five minutes ago is exactly the file an agent is about to be asked about,
    /// so untracked-but-not-ignored counts as the repo's own.
    /// </summary>
    [Fact]
    public void Scan_InACheckout_IncludesUntrackedFilesThatAreNotIgnored()
    {
        using var repo = new TempRepo();
        Assert.SkipUnless(repo.InitRepo(), "git is not available on this machine.");

        repo.Write("committed.cs", "class A;\n");
        Assert.True(repo.Git("add", "committed.cs"));
        Assert.True(repo.Git("commit", "--quiet", "-m", "first"));
        repo.Write("brand-new.cs", "class B;\n");

        var scan = RepoScanner.Scan(repo.Root, Settings with { Ignore = [] });

        Assert.Contains("committed.cs", scan.Files);
        Assert.Contains("brand-new.cs", scan.Files);
    }

    /// <summary>
    /// git's opinion is about what belongs in the repository, not about what is worth reading. A
    /// vendored bundle can be committed deliberately and still be junk to index.
    /// </summary>
    [Fact]
    public void Scan_InACheckout_StillAppliesTheConfiguredGlobs()
    {
        using var repo = new TempRepo();
        Assert.SkipUnless(repo.InitRepo(), "git is not available on this machine.");

        repo.Write("src/app.js", "export const a = 1;\n");
        repo.Write("vendor/jquery.js", "// vendored\n");
        Assert.True(repo.Git("add", "-A"));

        var scan = RepoScanner.Scan(repo.Root, Settings with { Ignore = ["**/vendor/**"] });

        Assert.Contains("src/app.js", scan.Files);
        Assert.DoesNotContain("vendor/jquery.js", scan.Files);
        Assert.Equal(1, scan.Skipped[SkipReason.Ignored]);
    }

    [Fact]
    public void Scan_WithGitDisabled_FallsBackToTheWalkEvenInACheckout()
    {
        using var repo = new TempRepo();
        Assert.SkipUnless(repo.InitRepo(), "git is not available on this machine.");

        repo.Write(".gitignore", "dist/\n");
        repo.Write("src/app.ts", "export const a = 1;\n");
        repo.Write("dist/bundle.js", "console.log(1);\n");

        var scan = RepoScanner.Scan(repo.Root, Settings with { Ignore = [], UseGit = false });

        Assert.Equal(ScanSource.DirectoryWalk, scan.Source);
        Assert.Contains("dist/bundle.js", scan.Files);
    }

    // ---- the fallback walk ----

    [Fact]
    public void Scan_WithoutACheckout_WalksTheDirectory()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.cs", "class A;\n");
        repo.Write("README.md", "# hello\n");

        var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Equal(ScanSource.DirectoryWalk, scan.Source);
        Assert.Equal(["README.md", "src/app.cs"], scan.Files);
    }

    /// <summary>
    /// Pruned at the directory, not filtered at the file — otherwise an ignored
    /// <c>node_modules</c> costs a hundred thousand pattern matches instead of one.
    /// </summary>
    [Fact]
    public void Scan_WithoutACheckout_PrunesIgnoredDirectoriesWholesale()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.js", "const a = 1;\n");
        repo.Write("node_modules/react/index.js", "module.exports = 1;\n");
        repo.Write("node_modules/react/deep/nested/more.js", "module.exports = 2;\n");

        var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Equal(["src/app.js"], scan.Files);

        // Nothing under the pruned directory was even inspected, so nothing was counted.
        Assert.Equal(0, scan.Skipped.GetValueOrDefault(SkipReason.Ignored));
    }

    // ---- content filtering, through the real disk ----

    [Fact]
    public void Scan_SkipsBinaryFilesWhateverTheyAreCalled()
    {
        using var repo = new TempRepo();
        repo.Write("src/real.cs", "class A;\n");
        repo.WriteBytes("src/looks-like-source.cs", [0x00, 0x01, 0x02, 0x03]);
        repo.WriteBytes("assets/logo.png", [0x89, 0x50, 0x4e, 0x47, 0x00, 0x0d]);

        var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Equal(["src/real.cs"], scan.Files);
        Assert.Equal(2, scan.Skipped[SkipReason.Binary]);
    }

    [Fact]
    public void Scan_SkipsFilesOverTheSizeCap()
    {
        using var repo = new TempRepo();
        repo.Write("src/small.cs", "class A;\n");
        repo.Write("data/fixtures.json", new string('a', 5000));

        var scan = RepoScanner.Scan(repo.Root, Settings with { MaxFileBytes = 1000 }, new NotACheckout());

        Assert.Equal(["src/small.cs"], scan.Files);
        Assert.Equal(1, scan.Skipped[SkipReason.TooLarge]);
    }

    [Fact]
    public void Scan_SkipsMinifiedFilesUnderTheSizeCap()
    {
        using var repo = new TempRepo();
        repo.Write("web/app.js", "const a = 1;\nconst b = 2;\n");
        repo.Write("web/app.bundle.js", new string('x', 5000));

        var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Equal(["web/app.js"], scan.Files);
        Assert.Equal(1, scan.Skipped[SkipReason.Generated]);
    }

    // ---- reporting ----

    /// <summary>
    /// Every skip is counted, because an over-eager rule must show up as a number rather than as
    /// a repo that mysteriously has no code facts.
    /// </summary>
    [Fact]
    public void Scan_ReportsWhatItSkippedAndWhy()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.cs", "class A;\n");
        repo.WriteBytes("src/blob.bin", [0x00, 0x01]);
        repo.Write("src/bundle.js", new string('x', 5000));

        var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Equal(2, scan.SkippedTotal);
        Assert.Contains("1 files", scan.Summary(), StringComparison.Ordinal);
        Assert.Contains("binary", scan.Summary(), StringComparison.Ordinal);
        Assert.Contains("generated", scan.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_SaysWhereTheListCameFrom()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A;\n");

        Assert.Contains("git", RepoScanner.Scan(repo.Root, Settings, new StubLister("a.cs")).Summary(), StringComparison.Ordinal);
        Assert.Contains("directory walk", RepoScanner.Scan(repo.Root, Settings, new NotACheckout()).Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_OnAListedFileThatIsGone_CountsItUnreadableRatherThanThrowing()
    {
        using var repo = new TempRepo();
        repo.Write("here.cs", "class A;\n");

        var scan = RepoScanner.Scan(repo.Root, Settings, new StubLister("here.cs", "deleted.cs"));

        Assert.Equal(["here.cs"], scan.Files);
        Assert.Equal(1, scan.Skipped[SkipReason.Unreadable]);
    }

    /// <summary>
    /// git lists an embedded checkout as one bare directory entry — measured: an untracked
    /// clone as <c>inner/</c> with a trailing slash, a committed gitlink as the plain path.
    /// Both are another repository's tree; counting them as unreadable files of this one
    /// was a lie in the report.
    /// </summary>
    [Fact]
    public void Scan_OnAListedEntryThatIsADirectory_CountsAnEmbeddedCheckout()
    {
        using var repo = new TempRepo();
        repo.Write("app.cs", "class A;\n");
        repo.Write("cloned/lib.cs", "class B;\n");
        repo.Write("linked/lib.cs", "class C;\n");

        var scan = RepoScanner.Scan(repo.Root, Settings, new StubLister("app.cs", "cloned/", "linked"));

        Assert.Equal(["app.cs"], scan.Files);
        Assert.Equal(2, scan.Skipped[SkipReason.EmbeddedCheckout]);
        Assert.Equal(0, scan.Skipped.GetValueOrDefault(SkipReason.Unreadable));
    }

    [Fact]
    public void Scan_InACheckout_ReportsAnEmbeddedCheckoutRatherThanIndexingIt()
    {
        using var repo = new TempRepo();
        Assert.SkipUnless(repo.InitRepo(), "git is not available on this machine.");

        repo.Write("app.cs", "class A;\n");
        Assert.True(repo.Git("init", "--quiet", "embedded"));
        repo.Write("embedded/lib.cs", "class B;\n");

        var scan = RepoScanner.Scan(repo.Root, Settings with { Ignore = [] });

        Assert.Equal(ScanSource.Git, scan.Source);
        Assert.Contains("app.cs", scan.Files);
        Assert.DoesNotContain(scan.Files, file => file.StartsWith("embedded", StringComparison.Ordinal));
        Assert.Equal(1, scan.Skipped[SkipReason.EmbeddedCheckout]);
    }

    /// <summary>
    /// The walk stops at a checkout boundary too: those files belong to the inner repo's
    /// own identity, and indexing them here would double them under the wrong paths. The
    /// marker is <c>.git</c> in either shape — a directory in a plain clone, a file in a
    /// worktree or submodule — and it counts once, matching what git reports for the same
    /// tree.
    /// </summary>
    [Fact]
    public void Scan_WalkingANonCheckout_StopsAtAnEmbeddedCheckoutBoundary()
    {
        using var repo = new TempRepo();
        repo.Write("notes.md", "# notes\n");
        repo.Write("cloned/.git/HEAD", "ref: refs/heads/main\n");
        repo.Write("cloned/lib.cs", "class B;\n");
        repo.Write("linked/.git", "gitdir: /elsewhere/worktrees/linked\n");
        repo.Write("linked/lib.cs", "class C;\n");

        var scan = RepoScanner.Scan(repo.Root, Settings with { Ignore = [] }, new NotACheckout());

        Assert.Equal(ScanSource.DirectoryWalk, scan.Source);
        Assert.Equal(["notes.md"], scan.Files);
        Assert.Equal(2, scan.Skipped[SkipReason.EmbeddedCheckout]);
    }

    [Fact]
    public void Scan_ReturnsFilesInAStableOrder()
    {
        using var repo = new TempRepo();
        repo.Write("b.cs", "class B;\n");
        repo.Write("a.cs", "class A;\n");
        repo.Write("c.cs", "class C;\n");

        var scan = RepoScanner.Scan(repo.Root, Settings, new StubLister("c.cs", "a.cs", "b.cs"));

        Assert.Equal(["a.cs", "b.cs", "c.cs"], scan.Files);
    }

    [Fact]
    public void GitFileLister_OnADirectoryThatIsNotACheckout_ReturnsNull()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A;\n");

        Assert.Null(new GitFileLister().List(repo.Root));
    }

    [Fact]
    public void GitFileLister_OnAMissingDirectory_ReturnsNull()
    {
        Assert.Null(new GitFileLister().List(Path.Combine(Path.GetTempPath(), $"engram-absent-{Guid.NewGuid():N}")));
    }

    /// <summary>
    /// A path may legally contain a newline. Splitting output on one would invent two files that
    /// do not exist, which is why the lister asks git for NUL-separated output.
    /// </summary>
    [Fact]
    public void GitFileLister_HandlesAPathContainingANewline()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot contain a newline.");

        using var repo = new TempRepo();
        Assert.SkipUnless(repo.InitRepo(), "git is not available on this machine.");

        var awkward = "src/we\nird.cs";
        var full = Path.Combine(repo.Root, "src", "we\nird.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "class A;\n", Encoding.UTF8);

        var listed = new GitFileLister().List(repo.Root);

        Assert.NotNull(listed);
        Assert.Contains(awkward, listed);
    }

    // ---- the walk is bounded, and says when it stopped ----

    /// <summary>
    /// The ceiling is the memory bound. It stops mid-walk rather than after it, which is the whole
    /// difference: a scan that collects everything and then trims has already paid for everything.
    /// </summary>
    [Fact]
    public void Walk_AtTheFileCeiling_StopsThereAndReportsItAsPartial()
    {
        using var repo = new TempRepo();
        for (var i = 0; i < 50; i++)
        {
            repo.Write($"src/file{i:D2}.cs", "class A;\n");
        }

        var scan = RepoScanner.Scan(
            repo.Root,
            Settings,
            new NotACheckout(),
            new ScanBudget(TimeSpan.FromMinutes(5), MaxFiles: 10));

        Assert.Equal(10, scan.Files.Count);
        Assert.Equal(ScanStop.FileCeiling, scan.Stop);
        Assert.True(scan.Truncated);
    }

    /// <summary>
    /// Zero rather than a small number on purpose: a budget measured against a wall clock makes any
    /// other value a race, and a flaky guard gets deleted rather than fixed.
    /// </summary>
    /// <remarks>
    /// The embedded checkout is what makes this about the <i>walk</i>. Reporting the scan partial is
    /// something the caller could do afterwards, having already walked everything — the original
    /// bug exactly. Only the walk records an embedded checkout, and only by enumerating the
    /// directory holding it, so a skip count of zero is proof it stopped before doing any of that.
    /// </remarks>
    [Fact]
    public void Walk_OutOfTime_StopsBeforeEnumeratingAnything()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.cs", "class A;\n");
        repo.Write("vendored/.git/HEAD", "ref: refs/heads/main\n");

        var scan = RepoScanner.Scan(
            repo.Root,
            Settings,
            new NotACheckout(),
            new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000));

        Assert.Equal(ScanStop.TimeBudget, scan.Stop);
        Assert.True(scan.Truncated);
        Assert.Empty(scan.Files);
        Assert.Equal(0, scan.SkippedTotal);
    }

    /// <summary>The same tree walked without a budget, so the zero-skip assertion above means something.</summary>
    [Fact]
    public void Walk_WithTimeToSpare_DoesReachTheEmbeddedCheckout()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.cs", "class A;\n");
        repo.Write("vendored/.git/HEAD", "ref: refs/heads/main\n");

        var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Equal(ScanStop.Complete, scan.Stop);
        Assert.Equal(1, scan.Skipped.GetValueOrDefault(SkipReason.EmbeddedCheckout));
    }

    /// <summary>
    /// The other half, and the one that would catch a bound set so tight it fires on real work:
    /// an ordinary tree under an ordinary budget is complete, and says so.
    /// </summary>
    [Fact]
    public void Walk_WithinItsBudget_IsComplete()
    {
        using var repo = new TempRepo();
        for (var i = 0; i < 50; i++)
        {
            repo.Write($"src/file{i:D2}.cs", "class A;\n");
        }

        var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Equal(50, scan.Files.Count);
        Assert.Equal(ScanStop.Complete, scan.Stop);
        Assert.False(scan.Truncated);
    }

    /// <summary>
    /// The ceiling bounds the walk's memory and has no business on a git listing: a monorepo that
    /// lists past it is completely enumerated, and calling that partial would disable its deletions
    /// for good.
    /// </summary>
    [Fact]
    public void TheFileCeiling_DoesNotReachAGitListing()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A;\n");
        repo.Write("b.cs", "class B;\n");
        repo.Write("c.cs", "class C;\n");

        var scan = RepoScanner.Scan(
            repo.Root,
            Settings,
            new StubLister("a.cs", "b.cs", "c.cs"),
            new ScanBudget(TimeSpan.FromMinutes(5), MaxFiles: 1));

        Assert.Equal(ScanSource.Git, scan.Source);
        Assert.Equal(3, scan.Files.Count);
        Assert.False(scan.Truncated);
    }

    /// <summary>
    /// The half the first fix missed, caught by publishing it and running the reported command:
    /// the walk stopped at its ceiling in about two seconds and then spent six more classifying
    /// the hundred thousand paths it had collected, because reading each file's head to tell source
    /// from binary is the more expensive half. A budget that stops only the enumeration bounds the
    /// cheaper one.
    /// </summary>
    [Fact]
    public void TheBudget_CoversClassificationAndNotOnlyEnumeration()
    {
        using var repo = new TempRepo();
        for (var i = 0; i < 20; i++)
        {
            repo.Write($"f{i:D2}.cs", "class A;\n");
        }

        // A git listing, so nothing is walked at all and only the classification pass can stop.
        var scan = RepoScanner.Scan(
            repo.Root,
            Settings,
            new StubLister([.. Enumerable.Range(0, 20).Select(i => $"f{i:D2}.cs")]),
            new ScanBudget(TimeSpan.Zero, MaxFiles: 1_000_000));

        Assert.Equal(ScanSource.Git, scan.Source);
        Assert.Equal(ScanStop.TimeBudget, scan.Stop);
        Assert.Empty(scan.Files);
    }

    /// <summary>
    /// The count alone reads as an answer. Whoever prints it has to be told it is a floor, and
    /// which bound produced it — the two have different fixes.
    /// </summary>
    [Fact]
    public void Summary_NamesWhichBoundStoppedTheWalk()
    {
        using var repo = new TempRepo();
        for (var i = 0; i < 20; i++)
        {
            repo.Write($"f{i:D2}.cs", "class A;\n");
        }

        var ceiling = RepoScanner.Scan(
            repo.Root, Settings, new NotACheckout(), new ScanBudget(TimeSpan.FromMinutes(5), 5));
        var clock = RepoScanner.Scan(
            repo.Root, Settings, new NotACheckout(), new ScanBudget(TimeSpan.Zero, 1_000));
        var whole = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

        Assert.Contains("file ceiling", ceiling.Summary(), StringComparison.Ordinal);
        Assert.Contains("ran out of time", clock.Summary(), StringComparison.Ordinal);
        Assert.DoesNotContain("partial", whole.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// doctor is run when something is already wrong, so its budget is short; the budget for work
    /// the user asked for matches what git already gets. Asserted as a relationship rather than as
    /// two numbers, so tuning either one does not break this.
    /// </summary>
    [Fact]
    public void DiagnosticBudget_IsShorterThanTheOneForRequestedWork()
    {
        Assert.True(ScanBudget.Diagnostic.Time < ScanBudget.Default.Time);
        Assert.Equal(GitFileLister.Timeout, ScanBudget.Default.Time);
    }

    // ---- an unreadable directory is skipped and reported, never treated as empty (commit E2) ----

    /// <summary>
    /// A moved, unmounted, or deleted checkout reaches this the same way: GitFileLister.List
    /// returns null for an absent root, so Scan falls through to Walk, whose very first
    /// enumeration throws. Needs no permissions to stage. §13.3's trap is that if Summary()'s
    /// stop switch carries a `_` default, a new ScanStop value renders as nothing — falsify by
    /// deleting the new arm from the switch, or by reverting the catch's write to `stop`.
    /// </summary>
    [Fact]
    public void Summary_NamesTheUnreadableDirectoryAndItsCount()
    {
        using var repo = new TempRepo();
        var missing = Path.Combine(repo.Root, "gone");

        var scan = RepoScanner.Scan(missing, Settings, new NotACheckout());

        Assert.Equal(ScanStop.Unreadable, scan.Stop);
        Assert.True(scan.Truncated);
        Assert.Equal(1, scan.Skipped[SkipReason.UnreadableDirectory]);
        Assert.Contains(missing, scan.Summary(), StringComparison.Ordinal);
        Assert.Contains("could not read a directory", scan.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The only test here needing permissions. Root, and some filesystems, ignore the mode bits,
    /// which would silently stop this from exercising anything — checked from the scan's own
    /// <see cref="ScanStop"/> rather than assumed, and skipped rather than asserting a false pass.
    /// </summary>
    [Fact]
    public void Walk_AnUnreadableSubdirectory_IsSkippedAndReportedRatherThanTreatedAsEmpty()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs POSIX permission bits, which Windows file systems don't have.");
            return;
        }

        using var repo = new TempRepo();
        repo.Write("src/app.cs", "class A;\n");
        var blocked = Path.Combine(repo.Root, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "secret.cs"), "class Secret;\n");

        File.SetUnixFileMode(blocked, UnixFileMode.None);
        try
        {
            var scan = RepoScanner.Scan(repo.Root, Settings, new NotACheckout());

            if (scan.Files.Contains("blocked/secret.cs"))
            {
                // chmod 000 did not block enumeration — running as root, or a filesystem that
                // ignores permission bits. Checked this way rather than via scan.Stop, because
                // scan.Stop is exactly what this test exists to prove: a build with the fix
                // reverted still excludes the blocked file (the catch's `continue` alone does
                // that) but would silently pass a guard keyed on Stop.
                Assert.Skip("chmod 000 did not block enumeration -- running as root, or a filesystem that ignores permission bits.");
            }

            Assert.Equal(ScanStop.Unreadable, scan.Stop);
            Assert.True(scan.Truncated);
            Assert.Contains("src/app.cs", scan.Files);
            Assert.Equal(1, scan.Skipped[SkipReason.UnreadableDirectory]);
            Assert.Contains(blocked, scan.Summary(), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
