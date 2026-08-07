using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The embedding step, which is deliberately not like the other optional steps: it stays
/// interactive even when the mode answer was "everything", because provider and model are
/// real tradeoffs the picker explains. What these tests can drive is everything around
/// that terminal: the flag-pinned path, the skip, the no-terminal deferral, and the
/// failure that must not abort a finished install.
/// </summary>
public class InstallerEmbeddingTests
{
    private static (int ExitCode, string Stdout, string Stderr) Install(InstallerTestHome home, params string[] extra) =>
        RunScript(
            "install.sh",
            home.Root,
            ["--binary", EndToEndBinary.Path!, "--prefix", home.Prefix, "--no-plugin", "--no-tree-sitter", "--no-sqlite-vec", .. extra]);

    [Fact]
    public void DryRun_SaysItWouldAsk()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var (exitCode, stdout, stderr) = Install(home, "--dry-run");

        Assert.True(exitCode == 0, $"dry run failed: {stderr}");
        Assert.Contains("would: ask which embedding provider and model to use", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// A piped install has nobody to answer, and a prompt that reads EOF as an answer
    /// would pick on the user's behalf — so the step defers and says how to finish.
    /// </summary>
    [Fact]
    public void ByDefault_WithNoTerminal_DefersAndSaysHowToFinish()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var (exitCode, stdout, stderr) = Install(home, "--apply");

        Assert.True(exitCode == 0, $"install failed: {stderr}");
        Assert.Contains("Embeddings: not configured (no terminal to ask)", stdout, StringComparison.Ordinal);
        Assert.Contains("engram init --with-embeddings", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoEmbeddings_SkipsTheStepEntirely()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var (exitCode, stdout, stderr) = Install(home, "--apply", "--no-embeddings");

        Assert.True(exitCode == 0, $"install failed: {stderr}");
        Assert.Contains("Embeddings: skipped", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flags are how an unattended run answers the questions. "none" is a real answer
    /// (the picker's own rule), and it is the one provider a test can pin without a
    /// network, a model download, or an endpoint.
    /// </summary>
    [Fact]
    public void WithAProviderFlag_ConfiguresWithoutAsking_AndTheConfigSaysSo()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var (exitCode, stdout, stderr) = Install(home, "--apply", "--embedding-provider", "none");

        Assert.True(exitCode == 0, $"install failed: {stderr}");
        Assert.Contains("Embeddings: configured", stdout, StringComparison.Ordinal);

        var config = File.ReadAllText(Path.Combine(home.Root, ".engram", "config.toml"));
        Assert.Contains("provider = \"none\"", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tri-state rule, fourth instance — and this failure needs no stub: a model id
    /// the catalog does not know is rejected by the binary before it downloads anything.
    /// </summary>
    [Fact]
    public void WithABadModel_TheInstallStillFinishesAndSaysWhatBroke()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var (exitCode, stdout, stderr) = Install(
            home, "--apply", "--embedding-provider", "local", "--embedding-model", "no-such-model", "--grant-permissions");

        Assert.True(exitCode == 0, $"a failed embedding step must not fail the install: {stderr}");
        Assert.Contains("Embeddings: NOT configured", stdout, StringComparison.Ordinal);

        // And the step after it still happened.
        Assert.Contains("MCP tool permissions: granted", stdout, StringComparison.Ordinal);
    }
}
