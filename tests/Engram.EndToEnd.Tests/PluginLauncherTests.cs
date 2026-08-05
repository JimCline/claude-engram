using System.Diagnostics;

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
        using var sandbox = new LauncherSandbox();

        var (exitCode, stdout, _) = sandbox.Run("ensure-server.sh");

        Assert.Equal(0, exitCode);
        Assert.Contains("no engram binary was found", stdout);
        Assert.Contains("install.sh", stdout);
    }

    // Every other hook stays silent instead: a hook that fails is worse than one that
    // does nothing, and only SessionStart has a channel that reaches anyone.
    [Fact]
    public void EngramExec_NoBinaryAnywhere_ExitsZeroWithNoOutput()
    {
        using var sandbox = new LauncherSandbox();

        var (exitCode, stdout, stderr) = sandbox.Run("engram-exec.sh", "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void EnsureServer_BinaryInstalled_SaysNothingAtAll()
    {
        using var sandbox = new LauncherSandbox();
        sandbox.InstallStubAt(".local/bin/engram");

        var (exitCode, stdout, _) = sandbox.Run("ensure-server.sh");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Resolve_PrefersEngramBinOverTheDefaultLocation()
    {
        using var sandbox = new LauncherSandbox();
        sandbox.InstallStubAt(".local/bin/engram");
        var overridePath = sandbox.InstallStubAt("elsewhere/engram");

        var (_, stdout, _) = sandbox.Run("resolve-engram.sh", environment: ("ENGRAM_BIN", overridePath));

        Assert.Equal(overridePath, stdout.Trim());
    }

    // A path pointing at something unusable is not a reason to give up on the ones that
    // work — an ENGRAM_BIN left over from a since-removed build should not disable memory.
    [Fact]
    public void Resolve_EngramBinNotExecutable_FallsThroughToTheDefaultLocation()
    {
        using var sandbox = new LauncherSandbox();
        var installed = sandbox.InstallStubAt(".local/bin/engram");
        var notExecutable = Path.Combine(sandbox.Home, "not-executable");
        File.WriteAllText(notExecutable, "");

        var (_, stdout, _) = sandbox.Run("resolve-engram.sh", environment: ("ENGRAM_BIN", notExecutable));

        Assert.Equal(installed, stdout.Trim());
    }

    [Fact]
    public void EngramExec_ForwardsArgumentsIncludingOnesContainingSpaces()
    {
        using var sandbox = new LauncherSandbox();
        sandbox.InstallStubAt(".local/bin/engram", body: """for a in "$@"; do echo "[$a]"; done""");

        var (_, stdout, _) = sandbox.Run("engram-exec.sh", "hook", "session-start", "--home", "/tmp/a path/here");

        Assert.Equal("[hook]\n[session-start]\n[--home]\n[/tmp/a path/here]\n", stdout.Replace("\r\n", "\n"));
    }

    private sealed class LauncherSandbox : IDisposable
    {
        private static readonly string HooksDirectory = LocateHooksDirectory();

        public string Home { get; } =
            Path.Combine(Path.GetTempPath(), "engram-launcher-test-" + Guid.NewGuid().ToString("N"));

        public LauncherSandbox()
        {
            Assert.SkipUnless(!OperatingSystem.IsWindows(), "The plugin launcher is a POSIX shell script.");
            Directory.CreateDirectory(Home);
        }

        public string InstallStubAt(string relativePath, string body = """echo "STUB $*" """)
        {
            var path = Path.Combine(Home, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return path;
        }

        public (int ExitCode, string Stdout, string Stderr) Run(
            string script,
            params string[] args) => Run(script, environment: null, args);

        public (int ExitCode, string Stdout, string Stderr) Run(
            string script,
            (string Key, string Value)? environment,
            params string[] args)
        {
            var startInfo = new ProcessStartInfo(Path.Combine(HooksDirectory, script))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment["HOME"] = Home;

            // A stub reachable only through PATH would make "not installed" untestable,
            // so PATH is stripped to the system directories for every case.
            startInfo.Environment["PATH"] = "/usr/bin:/bin";

            if (environment is { } pair)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {script}.");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{script} did not exit within 10 seconds.");
            }

            return (process.ExitCode, stdout, stderr);
        }

        public void Dispose()
        {
            if (Directory.Exists(Home))
            {
                Directory.Delete(Home, recursive: true);
            }
        }

        private static string LocateHooksDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "plugin", "hooks");
                if (File.Exists(Path.Combine(candidate, "resolve-engram.sh")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate plugin/hooks from the test output directory.");
        }
    }
}
