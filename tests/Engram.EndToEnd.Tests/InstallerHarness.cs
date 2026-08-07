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

    public static (int ExitCode, string Stdout, string Stderr) RunScript(string scriptName, string home, params string[] args) =>
        RunScriptWithEnvironment(scriptName, home, null, args);

    /// <summary>
    /// <see cref="RunScript"/> with extra environment variables layered over the pinned set.
    /// </summary>
    /// <remarks>
    /// For the one thing the sandbox cannot say through arguments: which port the installer's
    /// server step binds. install.sh has no --port of its own to forward, so a test that lets the
    /// step start a real server would take the default port — fighting every other such test and
    /// whatever daemon the developer running the suite already has up. <c>ENGRAM_PORT</c> is the
    /// way in, and a per-test port is what makes starting a real server safe to do at all.
    /// </remarks>
    public static (int ExitCode, string Stdout, string Stderr) RunScriptWithEnvironment(
        string scriptName,
        string home,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        params string[] args)
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

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return Run(startInfo, scriptName);
    }

    /// <summary>
    /// Runs one of the PowerShell scripts under <c>pwsh</c>, with the same pinned
    /// environment as <see cref="RunScript"/>. Callers gate on <see cref="PwshPath"/>.
    /// </summary>
    public static (int ExitCode, string Stdout, string Stderr) RunPwshScript(string scriptName, string home, params string[] args)
    {
        var scriptPath = Path.Combine(RepoRoot, "scripts", scriptName);

        var startInfo = new ProcessStartInfo(PwshPath ?? throw new InvalidOperationException("pwsh is not available"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["HOME"] = home;
        // The bash harness redirects the home by redirecting $HOME, which on Windows
        // redirects nothing — the binary's default resolution asks the profile API. The
        // ps1 scripts are therefore driven with ENGRAM_HOME pinned into the sandbox,
        // because a test that lets `engram init` reach the runner's real %USERPROFILE%
        // is touching the real instance no matter what it asserts.
        startInfo.Environment["ENGRAM_HOME"] = Path.Combine(home, ".engram");

        return Run(startInfo, scriptName);
    }

    /// <summary>Where <c>pwsh</c> lives, or null. Lazy: most tests never ask.</summary>
    public static string? PwshPath { get; } = FindOnSystemPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");

    public const string PwshSkipReason = "pwsh is not installed; skipping the PowerShell installer test.";

    private static string? FindOnSystemPath(string fileName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(ProcessStartInfo startInfo, string scriptName)
    {
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

    /// <summary>
    /// Puts a stand-in <c>curl</c> on the sandboxed PATH that records its argv and fails.
    /// </summary>
    /// <remarks>
    /// The tree-sitter step's first network call is curl, so a failing stub is how the
    /// fetch-fails path is arranged without a network. The other failure branch — no C
    /// compiler — cannot be arranged here at all: the pinned PATH includes /usr/bin, and
    /// absence cannot be planted (the same constraint <see cref="StubDotnet"/> records).
    /// Returns the argv log path; its absence proves nothing fetched.
    /// </remarks>
    public string StubCurl()
    {
        var bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(bin);

        var log = Path.Combine(Root, "curl-argv.log");
        var script = Path.Combine(bin, "curl");
        File.WriteAllText(
            script,
            "#!/bin/sh\necho \"$@\" >> " + Quote(log) + "\necho 'stub curl failing on purpose' >&2\nexit 22\n");
        MarkExecutable(script);
        return log;
    }

    /// <summary>
    /// Puts a stand-in <c>dotnet</c> on the sandboxed PATH that reports exactly these SDKs.
    /// </summary>
    /// <remarks>
    /// A stub rather than PATH surgery, because "no dotnet" cannot be arranged by
    /// subtraction: the pinned PATH already includes <c>/usr/bin</c>, and Ubuntu images
    /// ship <c>/usr/bin/dotnet</c>, so a test that relies on dotnet being absent is a
    /// test that flips with the host. Planting one first on PATH is deterministic
    /// everywhere. Anything but <c>--list-sdks</c> fails loudly, so a test whose script
    /// reaches publish through the stub reports that, not a hang.
    /// </remarks>
    public void StubDotnet(params string[] sdkVersions)
    {
        var bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(bin);

        var lines = string.Join("\n", sdkVersions.Select(v => $"echo '{v} [/stub/sdk]'"));
        var script = Path.Combine(bin, "dotnet");
        File.WriteAllText(
            script,
            "#!/bin/sh\nif [ \"$1\" = \"--list-sdks\" ]; then\n" + lines + "\nexit 0\nfi\necho 'stub dotnet only answers --list-sdks' >&2\nexit 1\n");
        MarkExecutable(script);
    }

    /// <summary>
    /// A stand-in for Microsoft's <c>dotnet-install.sh</c>: records its argv, then plants a
    /// fake 10.x dotnet in whatever <c>--install-dir</c> it was handed, whose <c>publish</c>
    /// fails. Returns the argv log path.
    /// </summary>
    /// <remarks>
    /// The fake's failing publish is the point: the bootstrap test proves the chain —
    /// installer invoked with the right arguments, then the bootstrapped dotnet chosen
    /// for the build — without a network, an SDK download, or a real publish.
    /// </remarks>
    public string StubDotnetInstall()
    {
        var log = Path.Combine(Root, "dotnet-install-argv.log");
        var script = Path.Combine(Root, "stub-dotnet-install.sh");
        File.WriteAllText(
            script,
            """
            #!/bin/sh
            echo "$@" >> LOGPATH
            install_dir=""
            while [ $# -gt 0 ]; do
                if [ "$1" = "--install-dir" ]; then install_dir="$2"; shift 2; else shift; fi
            done
            [ -n "$install_dir" ] || { echo 'stub dotnet-install: no --install-dir' >&2; exit 1; }
            mkdir -p "$install_dir"
            cat > "$install_dir/dotnet" <<'FAKE'
            #!/bin/sh
            if [ "$1" = "--list-sdks" ]; then echo '10.0.100 [/stub/sdk]'; exit 0; fi
            echo 'the bootstrapped stub dotnet cannot publish' >&2
            exit 1
            FAKE
            chmod +x "$install_dir/dotnet"
            """.Replace("LOGPATH", Quote(log), StringComparison.Ordinal) + "\n");
        MarkExecutable(script);
        return log;
    }

    public string StubDotnetInstallPath => Path.Combine(Root, "stub-dotnet-install.sh");

    private static void MarkExecutable(string script)
    {
#pragma warning disable CA1416 // engram's shell installers only run on macOS/Linux; these tests never run on Windows.
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
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
