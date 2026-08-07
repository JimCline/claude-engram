using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The Claude Code plugin step, which installs by default and opts out with
/// <c>--no-plugin</c>.
/// </summary>
/// <remarks>
/// The installer is the first thing anyone runs, so an untested branch in it is the worst place to
/// have one. This step is two <c>claude</c> invocations, which means the only way to assert it
/// did the right thing is to be the <c>claude</c> it invoked — the harness pins PATH to the
/// sandboxed home, so a stand-in there is found and the real one never is. The other two
/// default-on steps are pinned off here so nothing in these tests reaches the network.
/// </remarks>
public class InstallerPluginTests
{
    private static (int ExitCode, string Stdout, string Stderr) Install(InstallerTestHome home, params string[] extra) =>
        RunScript(
            "install.sh",
            home.Root,
            ["--binary", EndToEndBinary.Path!, "--prefix", home.Prefix, "--no-tree-sitter", "--no-sqlite-vec", .. extra]);

    [Fact]
    public void DryRun_PrintsBothCommandsAndRunsNeither()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubClaude();

        var (exitCode, stdout, stderr) = Install(home);

        Assert.True(exitCode == 0, $"dry run failed: {stderr}");
        Assert.Contains($"claude plugin marketplace add {RepoRoot}", stdout, StringComparison.Ordinal);
        Assert.Contains("claude plugin install engram@engram", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(log), "a dry run must not invoke claude");
    }

    /// <summary>
    /// No flag at all is the install everyone runs, so it is the one that must register
    /// the plugin — a default that needs a flag to happen is not a default.
    /// </summary>
    [Fact]
    public void ByDefault_Apply_RegistersTheMarketplaceForThisCheckoutAndInstallsThePlugin()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubClaude();

        var (exitCode, stdout, stderr) = Install(home, "--apply");

        Assert.True(exitCode == 0, $"install failed: {stderr}");

        var invocations = File.ReadAllLines(log);

        // The marketplace is added by path, and it has to be this checkout: a plugin registered
        // from somewhere else would load a different repo's hooks than the binary just installed.
        Assert.Contains($"plugin marketplace add {RepoRoot}", invocations);
        Assert.Contains("plugin install engram@engram", invocations);
        Assert.Contains("Claude Code plugin: registered and installed", stdout, StringComparison.Ordinal);
        Assert.Contains("/reload-plugins", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoPlugin_TheInstallerSkipsTheStepAndNeverRunsClaude()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubClaude();

        var (exitCode, stdout, stderr) = Install(home, "--apply", "--no-plugin");

        Assert.True(exitCode == 0, $"install failed: {stderr}");
        Assert.False(File.Exists(log), "claude must not run under --no-plugin");
        Assert.DoesNotContain("engram@engram", stdout, StringComparison.Ordinal);
        Assert.Contains("Claude Code plugin: skipped", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// No <c>claude</c> on PATH is a normal machine, not a failure.
    /// </summary>
    /// <remarks>
    /// Someone can install Engram before Claude Code, or install it for a different host entirely.
    /// The binary, the PATH entry and the home are all real by this point, so refusing here would
    /// throw away a completed installation over an optional extra.
    /// </remarks>
    [Fact]
    public void ByDefault_WhenClaudeIsNotOnPath_SaysSoAndStillSucceeds()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var (exitCode, stdout, stderr) = Install(home, "--apply");

        Assert.True(exitCode == 0, $"install failed: {stderr}");
        Assert.True(File.Exists(home.BinaryPath), "the binary still installs");
        Assert.Contains("claude is not on PATH", stdout, StringComparison.Ordinal);
        Assert.Contains("Claude Code plugin: NOT installed", stdout, StringComparison.Ordinal);
        Assert.Contains("claude plugin install engram@engram", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failing plugin step must not take the rest of the installation down with it.
    /// </summary>
    /// <remarks>
    /// <para><c>install.sh</c> runs under <c>set -e</c>, so a non-zero <c>claude</c> aborts the
    /// script where it stands. By then the binary is installed, the shell startup file is edited
    /// and the home is initialised — all durable, and none of it reported, because the summary is
    /// printed at the end and never runs. Worse, the MCP permission grant is the step *after* this
    /// one, so a plugin failure silently skips something that has nothing to do with the plugin.
    /// </para>
    ///
    /// <para>So the failure is caught and reported rather than propagated, and the exit code stays
    /// zero: the installation the script was asked for did happen. What did not is named in the
    /// summary, with the commands to finish it by hand.</para>
    /// </remarks>
    [Fact]
    public void ByDefault_WhenClaudeFails_TheInstallStillFinishesAndSaysWhatBroke()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubClaude(failWhenArgsContain: "install");

        var (exitCode, stdout, stderr) = Install(home, "--apply", "--grant-permissions");

        Assert.True(exitCode == 0, $"a failed plugin step must not fail the install: {stderr}");

        // It got as far as trying, and it did not stop at the marketplace step.
        Assert.Contains("plugin install engram@engram", File.ReadAllLines(log));

        // The summary ran at all, which is the thing set -e was destroying.
        Assert.Contains("Claude Code plugin: NOT installed", stdout, StringComparison.Ordinal);

        // And the step after the plugin still happened.
        Assert.Contains("MCP tool permissions: granted", stdout, StringComparison.Ordinal);
    }
}
