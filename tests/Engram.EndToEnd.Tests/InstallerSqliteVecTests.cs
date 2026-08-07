using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The sqlite-vec step, which installs by default and opts out with <c>--no-sqlite-vec</c>.
/// Before this step existed, <c>fetch-vec0.sh</c> was a script nobody was told to run —
/// the vector lane's extension only arrived on machines whose owner read the docs, which
/// is the opt-in-nobody-types failure the install-everything default exists to close.
/// </summary>
public class InstallerSqliteVecTests
{
    private static (int ExitCode, string Stdout, string Stderr) Install(InstallerTestHome home, params string[] extra) =>
        RunScript(
            "install.sh",
            home.Root,
            ["--binary", EndToEndBinary.Path!, "--prefix", home.Prefix, "--no-plugin", "--no-tree-sitter", .. extra]);

    [Fact]
    public void ByDefault_DryRun_PrintsTheStepAndFetchesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubCurl();

        var (exitCode, stdout, stderr) = Install(home, "--dry-run");

        Assert.True(exitCode == 0, $"dry run failed: {stderr}");
        Assert.Contains("would: install the sqlite-vec extension", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(log), "a dry run must not fetch anything");
    }

    [Fact]
    public void WithNoSqliteVec_TheInstallerSkipsTheStepAndNothingFetches()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubCurl();

        var (exitCode, stdout, stderr) = Install(home, "--apply", "--no-sqlite-vec");

        Assert.True(exitCode == 0, $"install failed: {stderr}");
        Assert.False(File.Exists(log), "nothing may fetch under --no-sqlite-vec");
        Assert.Contains("Vector search (sqlite-vec): skipped", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tri-state rule, third instance: a failing fetch reports through the summary,
    /// the steps after it still run, and the exit code stays zero because the
    /// installation that was asked for did happen.
    /// </summary>
    [Fact]
    public void ByDefault_WhenTheFetchFails_TheInstallStillFinishesAndSaysWhatBroke()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var log = home.StubCurl();

        var (exitCode, stdout, stderr) = Install(home, "--apply", "--grant-permissions");

        Assert.True(exitCode == 0, $"a failed sqlite-vec step must not fail the install: {stderr}");

        Assert.True(File.Exists(log), "the step should have invoked curl");
        Assert.Contains("Vector search (sqlite-vec): NOT installed", stdout, StringComparison.Ordinal);

        // fetch-vec0.sh stages through .partial and verifies before writing, so a failed
        // fetch leaves nothing for the vector lane to load.
        Assert.Empty(Directory.GetFiles(home.Root, "vec0.*", SearchOption.AllDirectories));

        Assert.Contains("MCP tool permissions: granted", stdout, StringComparison.Ordinal);
    }
}
