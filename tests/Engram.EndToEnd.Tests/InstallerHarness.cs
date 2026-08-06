using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Runs <c>scripts/install.sh</c> and <c>scripts/uninstall.sh</c> against a disposable home.
/// </summary>
/// <remarks>
/// Shared rather than private to one test class because the installer has more than one story to
/// tell — the round trip, and what <c>--with-plugin</c> does — and a second copy of this harness
/// would be free to drift on the one detail that makes it safe, which is the pinned
/// <see cref="RunScript"/> environment.
/// </remarks>
internal static class InstallerHarness
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static (int ExitCode, string Stdout, string Stderr) RunScript(string scriptName, string home, params string[] args)
    {
        var scriptPath = Path.Combine(RepoRoot, "scripts", scriptName);

        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["HOME"] = home;
        startInfo.Environment["SHELL"] = "/bin/zsh";
        startInfo.Environment.Remove("ENGRAM_HOME");
        // install.sh's PATH-symlink step only considers directories that are
        // actually on PATH; pinning PATH to the sandboxed home plus the bare
        // minimum system directories keeps every test hermetic and makes
        // sure a test can never create or touch a symlink under the real
        // /usr/local/bin, no matter what the host machine's PATH contains.
        // It is also what lets a test decide whether `claude` exists.
        startInfo.Environment["PATH"] = $"{home}/bin:/usr/bin:/bin";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {scriptName}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{scriptName} did not exit within 60 seconds.");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CLAUDE.md")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException($"could not locate repo root (CLAUDE.md) above {AppContext.BaseDirectory}");
        }

        return current.FullName;
    }
}

internal sealed class InstallerTestHome : IDisposable
{
    public string Root { get; }
    public string Prefix { get; }
    public string BinaryPath { get; }
    public string ZshrcPath { get; }

    public InstallerTestHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "engram-installer-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Prefix = Path.Combine(Root, ".local", "bin");
        BinaryPath = Path.Combine(Prefix, "engram");
        ZshrcPath = Path.Combine(Root, ".zshrc");
    }

    /// <summary>
    /// Puts a stand-in <c>claude</c> on the sandboxed PATH that records how it was called.
    /// </summary>
    /// <remarks>
    /// The installer's plugin step is two <c>claude</c> invocations and nothing else, so the only
    /// way to assert it ran them — and ran them with the right arguments — is to be the thing it
    /// invoked. Returns the path to the log, which does not exist until <c>claude</c> is called;
    /// its absence is the assertion for the cases where nothing should have run it.
    /// </remarks>
    public string StubClaude(string? failWhenArgsContain = null)
    {
        var bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(bin);

        var log = Path.Combine(Root, "claude-argv.log");
        var script = Path.Combine(bin, "claude");

        var failure = failWhenArgsContain is null
            ? string.Empty
            : $"case \"$*\" in *{failWhenArgsContain}*) echo 'stub claude failing on purpose' >&2; exit 1;; esac\n";

        File.WriteAllText(script, "#!/bin/sh\necho \"$@\" >> " + Quote(log) + "\n" + failure + "exit 0\n");
#pragma warning disable CA1416 // engram only ships for macOS/Linux RIDs; these tests never run on Windows.
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        return log;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
