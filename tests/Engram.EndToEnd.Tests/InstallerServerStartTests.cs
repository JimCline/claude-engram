using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The installer's last step: start the server, then confirm it is actually running.
/// </summary>
/// <remarks>
/// Its own class because these are the only installer tests that leave a real daemon behind if
/// anything goes wrong, so they carry the two things that make that safe — a per-test port, and a
/// stop in a finally. Every other installer test passes <c>--no-start</c> for the same reason:
/// thirty of them starting servers on the default port would fight each other, and whatever daemon
/// the developer running the suite already has up.
/// </remarks>
public class InstallerServerStartTests
{
    [Fact]
    public void Install_ByDefault_StartsTheServerAndConfirmsItIsRunning()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var port = FreeTcpPort.Next();
        var engramHome = Path.Combine(home.Root, ".engram");

        var result = RunScriptWithEnvironment(
            "install.sh",
            home.Root,
            new Dictionary<string, string> { ["ENGRAM_PORT"] = port.ToString() },
            "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix,
            "--no-plugin", "--no-tree-sitter", "--no-sqlite-vec");

        try
        {
            Assert.True(result.ExitCode == 0, $"install failed: {result.Stderr}");
            Assert.Contains("Server: running (confirmed by engram status)", result.Stdout, StringComparison.Ordinal);

            // The installer's own check re-asked from outside it. This is the question every
            // later consumer actually puts — a hook, a Claude Code session, doctor — and by D42
            // a separate process reading the pid file is not the same question as the launching
            // process reporting on itself.
            var (statusExit, statusOut, _) = EngramProcess.Run(engramHome, "status");
            Assert.Equal(0, statusExit);
            Assert.Contains("server: running", statusOut, StringComparison.Ordinal);
            Assert.Contains($"port: {port}", statusOut, StringComparison.Ordinal);
        }
        finally
        {
            // Unconditional, including when the assertions above failed: a daemon that outlives
            // the test owns a home that Dispose is about to delete.
            EngramProcess.Run(engramHome, "stop");
        }
    }

    [Fact]
    public void Install_WithNoStart_LeavesTheServerStopped()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var engramHome = Path.Combine(home.Root, ".engram");

        var result = RunScript(
            "install.sh", home.Root,
            "--no-start", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix,
            "--no-plugin", "--no-tree-sitter", "--no-sqlite-vec");

        Assert.True(result.ExitCode == 0, $"install failed: {result.Stderr}");
        Assert.Contains("Server: not started", result.Stdout, StringComparison.Ordinal);

        // The load-bearing half. Asserting only on the summary line would pass just as happily
        // if the step had started a server and mislabelled it.
        var (statusExit, statusOut, _) = EngramProcess.Run(engramHome, "status");
        Assert.Equal(1, statusExit);
        Assert.Contains("not running", statusOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_DryRun_StartsNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var result = RunScript(
            "install.sh", home.Root,
            "--dry-run", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);

        Assert.True(result.ExitCode == 0, $"dry run failed: {result.Stderr}");
        Assert.Contains("start, then confirm", result.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(home.Root, ".engram", "engram.pid")), "a dry run must not have started a server");
    }
}
