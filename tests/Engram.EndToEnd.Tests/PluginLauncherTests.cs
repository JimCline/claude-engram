namespace Engram.EndToEnd.Tests;

/// <summary>
/// The plugin ships no binary; its hooks locate an installed one. These drive the shell
/// scripts that do that, with HOME redirected so the real ~/.local/bin is never consulted.
/// </summary>
public class PluginLauncherTests
{
    // The failure this guards is the one a user actually meets: plugin installed from a
    // marketplace, binary never installed. Silence there is indistinguishable from memory
    // simply not working, so SessionStart has to say something a model can pass on.
    [Fact]
    public void EnsureServer_NoBinaryAnywhere_SaysSoOnStdoutAndStillExitsZero()
    {
        using var sandbox = new PluginSandbox();

        var (exitCode, stdout, _) = sandbox.Run("hooks/ensure-server.sh");

        Assert.Equal(0, exitCode);
        Assert.Contains("no engram binary was found", stdout);
        Assert.Contains("install.sh", stdout);
    }

    // Every other hook stays silent instead: a hook that fails is worse than one that
    // does nothing, and only SessionStart has a channel that reaches anyone.
    [Fact]
    public void EngramExec_NoBinaryAnywhere_ExitsZeroWithNoOutput()
    {
        using var sandbox = new PluginSandbox();

        var (exitCode, stdout, stderr) = sandbox.Run("hooks/engram-exec.sh", "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void EnsureServer_BinaryInstalled_SaysNothingAtAll()
    {
        using var sandbox = new PluginSandbox();
        sandbox.InstallStubAt(".local/bin/engram");

        var (exitCode, stdout, _) = sandbox.Run("hooks/ensure-server.sh");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Resolve_PrefersEngramBinOverTheDefaultLocation()
    {
        using var sandbox = new PluginSandbox();
        sandbox.InstallStubAt(".local/bin/engram");
        var overridePath = sandbox.InstallStubAt("elsewhere/engram");

        var (_, stdout, _) = sandbox.Run("hooks/resolve-engram.sh", environment: ("ENGRAM_BIN", overridePath));

        Assert.Equal(overridePath, stdout.Trim());
    }

    // A path pointing at something unusable is not a reason to give up on the ones that
    // work — an ENGRAM_BIN left over from a since-removed build should not disable memory.
    [Fact]
    public void Resolve_EngramBinNotExecutable_FallsThroughToTheDefaultLocation()
    {
        using var sandbox = new PluginSandbox();
        var installed = sandbox.InstallStubAt(".local/bin/engram");
        var notExecutable = Path.Combine(sandbox.Home, "not-executable");
        File.WriteAllText(notExecutable, "");

        var (_, stdout, _) = sandbox.Run("hooks/resolve-engram.sh", environment: ("ENGRAM_BIN", notExecutable));

        Assert.Equal(installed, stdout.Trim());
    }

    [Fact]
    public void EngramExec_ForwardsArgumentsIncludingOnesContainingSpaces()
    {
        using var sandbox = new PluginSandbox();
        sandbox.InstallStubAt(".local/bin/engram", body: """for a in "$@"; do echo "[$a]"; done""");

        var (_, stdout, _) = sandbox.Run(
            "hooks/engram-exec.sh", "hook", "session-start", "--home", "/tmp/a path/here");

        Assert.Equal("[hook]\n[session-start]\n[--home]\n[/tmp/a path/here]\n", stdout.Replace("\r\n", "\n"));
    }
}
