using System.Text.Json;
using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// What the published binary tells an agent about where its durable memory lives (D51).
/// </summary>
/// <remarks>
/// Driven through the binary rather than the builder because the defect this closes was never
/// in the string — it was that no channel reaching a top-level agent carried the claim at all.
/// A unit test on <c>PrimerBuilder</c> would have passed throughout.
/// </remarks>
public class MemoryPrecedenceTests
{
    private static string Primer(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("additionalContext")
            .GetString() ?? string.Empty;
    }

    [Fact]
    public void SessionStart_FreshInstall_LeadsWithWhereDurableMemoryLives()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);

        // First line, not merely present. The primer is budget-trimmed from the end, and this is
        // the only line in it whose absence changes what the agent does.
        var first = Primer(stdout).Split('\n')[0];
        Assert.Contains("Engram is this session's durable memory store", first, StringComparison.Ordinal);
        Assert.Contains("engram_remember", first, StringComparison.Ordinal);
    }

    // SessionStart never fires for a subagent, so a spawn inherits its parent's other memory
    // system but not the parent's primer. If this path stopped repeating the claim, subagents
    // would silently go back to whatever they were told elsewhere.
    [Fact]
    public void SubagentStart_RepeatsTheClaimRatherThanRelyingOnTheParent()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "hook", "subagent-start");

        Assert.Equal(0, exitCode);
        Assert.Contains("durable memory store", Primer(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_PrecedenceOff_SaysNothingAboutIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (initExit, _, initErr) = EngramProcess.Run(home.Root, "init", "--memory-precedence", "off");
        Assert.True(initExit == 0, $"init failed: {initErr}");

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("durable memory store", Primer(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_PrecedenceEngramOnly_SaysSoleRatherThanPrimary()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (initExit, _, initErr) = EngramProcess.Run(home.Root, "init", "--memory-precedence", "engram-only");
        Assert.True(initExit == 0, $"init failed: {initErr}");

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Contains("only durable memory store", Primer(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void Init_UnknownPrecedence_IsRefusedRatherThanQuietlyDefaulted()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "init", "--memory-precedence", "engram-frist");

        Assert.Equal(1, exitCode);
        Assert.Contains("engram-frist", stderr, StringComparison.Ordinal);
    }

    // D37: off is a configuration, not a fault, so it must not turn doctor red. A doctor that
    // reports a problem for a choice the user made is one people stop reading.
    [Fact]
    public void Doctor_PrecedenceOff_ReportsItWithoutFailing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        EngramProcess.Run(home.Root, "init", "--memory-precedence", "off");

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "doctor", "--offline", "--no-repo");

        Assert.Equal(0, exitCode);
        Assert.Contains("memory", stdout, StringComparison.Ordinal);
        Assert.Contains("precedence off", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ByDefault_LandsEngramFirstWithoutBeingAsked()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var result = RunScript(
            "install.sh", home.Root,
            "--no-start", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix,
            "--no-plugin", "--no-tree-sitter", "--no-sqlite-vec");

        Assert.True(result.ExitCode == 0, $"install failed: {result.Stderr}");

        // Deliberately not asserted on the summary line. That line is printed from the
        // installer's own catch-all branch without reading anything, so it says "engram-first"
        // whatever the config holds — the hardcoded-report defect this project keeps finding.
        // What is checked instead is the config the install actually produced, and then what a
        // hook does with it, because a piped install answers no questions and the default has to
        // arrive from the shipped config rather than from anything the installer chose.
        var engramHome = Path.Combine(home.Root, ".engram");
        var config = File.ReadAllText(Path.Combine(engramHome, "config.toml"));
        Assert.Contains("precedence = \"engram-first\"", config, StringComparison.Ordinal);

        var (exitCode, stdout, _) = EngramProcess.Run(engramHome, "hook", "session-start");
        Assert.Equal(0, exitCode);
        Assert.Contains("durable memory store", Primer(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_WithMemoryPrecedenceOff_WritesItAndReportsIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var result = RunScript(
            "install.sh", home.Root,
            "--memory-precedence", "off",
            "--no-start", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix,
            "--no-plugin", "--no-tree-sitter", "--no-sqlite-vec");

        Assert.True(result.ExitCode == 0, $"install failed: {result.Stderr}");
        Assert.Contains("Memory precedence: off", result.Stdout, StringComparison.Ordinal);

        var config = File.ReadAllText(Path.Combine(home.Root, ".engram", "config.toml"));
        Assert.Contains("precedence = \"off\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_DryRun_ChangesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();

        var result = RunScript(
            "install.sh", home.Root,
            "--dry-run", "--memory-precedence", "engram-only",
            "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix);

        Assert.True(result.ExitCode == 0, $"dry run failed: {result.Stderr}");
        Assert.Contains("would: set memory precedence", result.Stdout, StringComparison.Ordinal);
        Assert.False(
            File.Exists(Path.Combine(home.Root, ".engram", "config.toml")),
            "a dry run must not have written a config");
    }
}
