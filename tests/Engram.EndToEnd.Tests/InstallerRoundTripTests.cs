using System.Diagnostics;
using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

public class InstallerRoundTripTests
{
    private const string SeedZshrc = "# seeded by InstallerRoundTripTests\nexport SOME_ENGRAM_TEST_VAR=1\n";

    private static readonly string SidecarName =
        OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so";

    [Fact]
    public void Install_Apply_Twice_Then_Uninstall_RoundTrips()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);

        var install1 = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install1.ExitCode == 0, $"first install failed: {install1.Stderr}");

        Assert.True(File.Exists(home.BinaryPath), "binary should exist after install");
#pragma warning disable CA1416 // engram only ships for macOS/Linux RIDs; this test never runs on Windows.
        Assert.True((File.GetUnixFileMode(home.BinaryPath) & UnixFileMode.UserExecute) != 0, "installed binary should be executable");
#pragma warning restore CA1416

        var zshrcAfterFirst = File.ReadAllText(home.ZshrcPath);
        Assert.Equal(1, CountOccurrences(zshrcAfterFirst, "# >>> engram >>>"));

        var backupsAfterFirst = Directory.GetFiles(home.Root, ".zshrc.engram-backup-*");
        Assert.Single(backupsAfterFirst);

        var configPath = Path.Combine(home.Root, ".engram", "config.toml");
        Assert.True(File.Exists(configPath), "config.toml should exist under the redirected home after init");

        var install2 = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install2.ExitCode == 0, $"second install failed: {install2.Stderr}");

        var zshrcAfterSecond = File.ReadAllText(home.ZshrcPath);
        Assert.Equal(1, CountOccurrences(zshrcAfterSecond, "# >>> engram >>>"));

        var uninstall = RunScript("uninstall.sh", home.Root, "--apply", "--prefix", home.Prefix);
        Assert.True(uninstall.ExitCode == 0, $"uninstall failed: {uninstall.Stderr}");

        Assert.False(File.Exists(home.BinaryPath), "binary should be gone after uninstall");
        Assert.Equal(SeedZshrc, File.ReadAllText(home.ZshrcPath));
        Assert.True(Directory.Exists(Path.Combine(home.Root, ".engram")), ".engram home should still exist without --purge");
    }

    // The installed binary has to work from where it was installed, which is not the same
    // claim as "the build works". engram's AOT image resolves SQLite by dlopen against a
    // library beside the executable, so an install that carries only the executable
    // produces something that runs right up until it opens the database — and every check
    // performed in the staging directory passes, because the library is sitting there.
    // This drives the installed copy, from the prefix, at a database-opening command.
    [Fact]
    public void Install_InstalledBinary_OpensItsDatabaseFromThePrefix()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        var probeHome = Path.Combine(home.Root, "probe-home");
        var probe = RunBinary(home.BinaryPath, probeHome, "init");

        Assert.True(probe.ExitCode == 0, $"the installed binary could not initialise a home: {probe.Stderr}");
        Assert.True(File.Exists(Path.Combine(probeHome, "engram.db")), "the installed binary should have created a database");
    }

    // A stray native library left in somebody's bin directory forever is the cost of
    // installing one, so uninstall owes it the same treatment as the binary.
    [Fact]
    public void Uninstall_RemovesTheNativeLibraryItInstalled()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        var sidecarPath = Path.Combine(home.Prefix, SidecarName);
        Assert.True(File.Exists(sidecarPath), $"install should have placed {SidecarName} beside the binary");

        var uninstall = RunScript("uninstall.sh", home.Root, "--apply", "--prefix", home.Prefix);
        Assert.True(uninstall.ExitCode == 0, $"uninstall failed: {uninstall.Stderr}");

        Assert.False(File.Exists(sidecarPath), $"uninstall should have removed {SidecarName}");
    }

    // A binary that answers 'home' but cannot open a database is the exact shape of the
    // failure this installer shipped: 'home' only prints paths, so a pre-flight check
    // written against it green-lights a binary that dies the moment it does real work.
    // Checking with a database-opening command instead means such a build is rejected
    // before the install directory is so much as created. Needs no real engram binary —
    // the point is the installer's reaction, not any particular build's behaviour.
    [Fact]
    public void Install_BinaryThatCannotOpenADatabase_IsRejectedBeforeThePrefixIsCreated()
    {
        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);

        var fakeBinary = Path.Combine(home.Root, "fake-engram");
        File.WriteAllText(fakeBinary, "#!/bin/bash\ncase \"$1\" in\n  home) echo \"Root=$ENGRAM_HOME\"; exit 0 ;;\n  *) echo 'cannot open database' >&2; exit 1 ;;\nesac\n");
#pragma warning disable CA1416 // engram only ships for macOS/Linux RIDs; this test never runs on Windows.
        File.SetUnixFileMode(fakeBinary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", fakeBinary, "--prefix", home.Prefix);

        Assert.True(install.ExitCode != 0, "install should refuse a binary that cannot open a database");
        Assert.False(Directory.Exists(home.Prefix), "a rejected binary must not have caused the install directory to be created");
        Assert.Equal(SeedZshrc, File.ReadAllText(home.ZshrcPath));
    }

    [Fact]
    public void Install_DryRun_ChangesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);

        var result = RunScript("install.sh", home.Root, "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(result.ExitCode == 0, $"dry run failed: {result.Stderr}");

        Assert.False(File.Exists(home.BinaryPath), "dry run must not install the binary");
        Assert.Equal(SeedZshrc, File.ReadAllText(home.ZshrcPath));
        Assert.False(Directory.Exists(Path.Combine(home.Root, ".engram")), "dry run must not create the Engram home");
    }

    // A .zshrc that is itself a symlink into a dotfile repository — stow, chezmoi and
    // yadm all work this way — must come back out of this still a symlink. Replacing it
    // with a regular file detaches the user's config from the repo that manages it, and
    // nothing anywhere would report that it had happened. Writing through the link is
    // what a plain redirect did for free; the atomic replace has to land at the far end
    // of the link rather than on top of it.
    [Fact]
    public void Install_SymlinkedZshrc_WritesThroughTheLinkRatherThanReplacingIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var managedDirectory = Path.Combine(home.Root, "dotfiles");
        Directory.CreateDirectory(managedDirectory);
        var managedFile = Path.Combine(managedDirectory, "zshrc");
        File.WriteAllText(managedFile, SeedZshrc);

        File.Delete(home.ZshrcPath);
        File.CreateSymbolicLink(home.ZshrcPath, managedFile);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        Assert.NotNull(new FileInfo(home.ZshrcPath).LinkTarget);
        Assert.Equal(1, CountOccurrences(File.ReadAllText(managedFile), "# >>> engram >>>"));

        var uninstall = RunScript("uninstall.sh", home.Root, "--apply", "--prefix", home.Prefix);
        Assert.True(uninstall.ExitCode == 0, $"uninstall failed: {uninstall.Stderr}");

        Assert.NotNull(new FileInfo(home.ZshrcPath).LinkTarget);
        Assert.Equal(SeedZshrc, File.ReadAllText(managedFile));
    }

    [Fact]
    public void Uninstall_DryRun_Purge_DeletesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        var engramHome = Path.Combine(home.Root, ".engram");
        Assert.True(Directory.Exists(engramHome), ".engram home should exist after install");

        var uninstall = RunScript("uninstall.sh", home.Root, "--purge", "--prefix", home.Prefix);
        Assert.True(uninstall.ExitCode == 0, $"dry-run uninstall failed: {uninstall.Stderr}");

        Assert.True(Directory.Exists(engramHome), "a dry-run --purge must not delete the Engram home");
        Assert.True(File.Exists(home.BinaryPath), "a dry-run --purge must not remove the installed binary either");
    }

    [Fact]
    public void Uninstall_ApplyPurge_RemovesEngramHome()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        var engramHome = Path.Combine(home.Root, ".engram");
        Assert.True(Directory.Exists(engramHome), ".engram home should exist after install");

        var uninstall = RunScript("uninstall.sh", home.Root, "--apply", "--purge", "--prefix", home.Prefix);
        Assert.True(uninstall.ExitCode == 0, $"uninstall --purge failed: {uninstall.Stderr}");

        Assert.False(Directory.Exists(engramHome), "--apply --purge should remove the Engram home entirely");
    }

    [Fact]
    public void Install_NoPath_TouchesNoStartupFileAndCreatesNoSymlink()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);
        var binDir = Path.Combine(home.Root, "bin");
        Directory.CreateDirectory(binDir);

        var install = RunScript("install.sh", home.Root, "--apply", "--no-path", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install --no-path failed: {install.Stderr}");

        Assert.True(File.Exists(home.BinaryPath), "binary should exist after install");
        Assert.Equal(SeedZshrc, File.ReadAllText(home.ZshrcPath));

        var symlinkPath = Path.Combine(binDir, "engram");
        Assert.False(Path.Exists(symlinkPath), "no symlink should be created under --no-path, even though a valid candidate directory is on PATH");
    }

    [Fact]
    public void Install_Symlinks_When_Prefix_Not_On_Path_But_A_Candidate_Dir_Is()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);
        var binDir = Path.Combine(home.Root, "bin");
        Directory.CreateDirectory(binDir);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        var symlinkPath = Path.Combine(binDir, "engram");
        Assert.True(Path.Exists(symlinkPath), "a symlink should be created in the PATH candidate directory");
        Assert.Equal(home.BinaryPath, new FileInfo(symlinkPath).LinkTarget);

        Assert.Equal(SeedZshrc, File.ReadAllText(home.ZshrcPath));

        var uninstall = RunScript("uninstall.sh", home.Root, "--apply", "--prefix", home.Prefix);
        Assert.True(uninstall.ExitCode == 0, $"uninstall failed: {uninstall.Stderr}");
        Assert.False(Path.Exists(symlinkPath), "uninstall should remove the symlink it created");
    }

    [Fact]
    public void Install_Does_Not_Overwrite_A_PreExisting_File_At_The_Symlink_Location()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, SeedZshrc);
        var binDir = Path.Combine(home.Root, "bin");
        Directory.CreateDirectory(binDir);
        var blockerPath = Path.Combine(binDir, "engram");
        const string blockerContent = "not an engram binary, do not touch";
        File.WriteAllText(blockerPath, blockerContent);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        Assert.Equal(blockerContent, File.ReadAllText(blockerPath));

        var zshrcAfter = File.ReadAllText(home.ZshrcPath);
        Assert.Equal(1, CountOccurrences(zshrcAfter, "# >>> engram >>>"));
    }

    [Fact]
    public void Install_Zshrc_Without_Trailing_Newline_Gets_Exactly_One_Added()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        const string seedWithoutTrailingNewline = "# seeded by InstallerRoundTripTests\nexport SOME_ENGRAM_TEST_VAR=1";
        File.WriteAllText(home.ZshrcPath, seedWithoutTrailingNewline);

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        var zshrcAfter = File.ReadAllText(home.ZshrcPath);

        // install_path_block always terminates the pre-existing content with
        // a single newline before appending the engram block. This
        // normalization only ever ADDS a trailing newline to content that
        // lacked one; it never drops or alters any of the original bytes.
        Assert.True(
            zshrcAfter.StartsWith(seedWithoutTrailingNewline + "\n# >>> engram >>>", StringComparison.Ordinal),
            "the original content should be followed by exactly one newline before the engram block");
    }


    private static (int ExitCode, string Stdout, string Stderr) RunBinary(string binaryPath, string engramHome, params string[] args)
    {
        var startInfo = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Run from a directory that holds nothing of ours, so a native dependency can
            // only be found beside the installed binary — not because the test happened to
            // start the process somewhere that had a copy lying around.
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ENGRAM_HOME"] = engramHome;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {binaryPath}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{binaryPath} did not exit within 60 seconds.");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }


}
