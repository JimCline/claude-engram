using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

internal static class EngramProcess
{
    public static (int ExitCode, string Stdout, string Stderr) Run(string home, params string[] args)
    {
        var startInfo = new ProcessStartInfo(EndToEndBinary.Path!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ENGRAM_HOME"] = home;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start engram process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("engram process did not exit within 10 seconds.");
        }

        return (process.ExitCode, stdout, stderr);
    }
}
