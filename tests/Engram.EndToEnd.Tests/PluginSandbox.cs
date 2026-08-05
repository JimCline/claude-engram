using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Runs one of the plugin's shell scripts against a disposable HOME, with PATH stripped
/// to the system directories so a stub is only ever found where the test put it.
/// Script paths are relative to <c>plugin/</c>, because the launchers under
/// <c>hooks/</c> and the command wrapper under <c>scripts/</c> share a resolver and
/// need to be exercised the same way.
/// </summary>
internal sealed class PluginSandbox : IDisposable
{
    public static string PluginDirectory { get; } = LocatePluginDirectory();

    public string Home { get; } =
        Path.Combine(Path.GetTempPath(), "engram-launcher-test-" + Guid.NewGuid().ToString("N"));

    public PluginSandbox()
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
        var startInfo = new ProcessStartInfo(Path.Combine(PluginDirectory, script))
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

    private static string LocatePluginDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plugin");
            if (File.Exists(Path.Combine(candidate, "hooks", "resolve-engram.sh")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate plugin/ from the test output directory.");
    }
}
