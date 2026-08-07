using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>install.sh --with-tree-sitter</c> (D47), held to the rule the plugin step paid to
/// learn: an optional step that fails reports through the summary and never discards the
/// finished installation around it.
/// </summary>
public class InstallerTreeSitterTests
{
    private static (int ExitCode, string Stdout, string Stderr) Install(InstallerTestHome home, params string[] extra) =>
        RunScript(
            "install.sh",
            home.Root,
            ["--binary", EndToEndBinary.Path!, "--prefix", home.Prefix, .. extra]);

    [Fact]
    public void WithTreeSitter_DryRun_PrintsTheStepAndFetchesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubCurl();

        var (exitCode, stdout, stderr) = Install(home, "--with-tree-sitter");

        Assert.True(exitCode == 0, $"dry run failed: {stderr}");
        Assert.Contains("would: compile the tree-sitter core and grammars", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(log), "a dry run must not fetch anything");
    }

    [Fact]
    public void WithoutTheFlag_TheInstallerNeverMentionsTreeSitterAndNeverFetches()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubCurl();

        var (exitCode, stdout, stderr) = Install(home, "--apply");

        Assert.True(exitCode == 0, $"install failed: {stderr}");
        Assert.False(File.Exists(log), "nothing may fetch unless --with-tree-sitter was given");
        Assert.DoesNotContain("tree-sitter", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failing fetch must not take the rest of the installation down with it.
    /// </summary>
    /// <remarks>
    /// The same defect class the plugin step had: under <c>set -e</c> a non-zero optional
    /// command aborts the script after everything durable already happened and before the
    /// summary and the permission grant. The step runs inside an <c>if</c> condition, which
    /// is what this test holds — and the staged install inside the fetch script means a
    /// failure leaves no half-written library for <c>TreeSitter.Locate</c> to find.
    /// </remarks>
    [Fact]
    public void WithTreeSitter_WhenTheFetchFails_TheInstallStillFinishesAndSaysWhatBroke()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubCurl();

        var (exitCode, stdout, stderr) = Install(home, "--apply", "--with-tree-sitter", "--grant-permissions");

        Assert.True(exitCode == 0, $"a failed tree-sitter step must not fail the install: {stderr}");

        // It got far enough to fetch, so the failure is the stub's, not a skipped step.
        Assert.True(File.Exists(log), "the step should have invoked curl");

        // The summary ran at all, which is what set -e would have destroyed.
        Assert.Contains("Tier-1 TS/JS analysis: NOT installed", stdout, StringComparison.Ordinal);

        Assert.Empty(Directory.GetFiles(home.Root, "libtree-sitter*", SearchOption.AllDirectories));

        // And the step after the fetch still happened.
        Assert.Contains("MCP tool permissions: granted", stdout, StringComparison.Ordinal);
    }
}
