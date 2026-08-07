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
}
