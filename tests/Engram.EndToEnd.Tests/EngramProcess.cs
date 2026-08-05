using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

internal static class EngramProcess
{
    public static (int ExitCode, string Stdout, string Stderr) Run(string home, params string[] args) =>
        RunWithStdin(home, stdin: null, args);

    public static (int ExitCode, string Stdout, string Stderr) RunWithStdin(string home, string? stdin, params string[] args)
    {
        var startInfo = new ProcessStartInfo(EndToEndBinary.Path!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ENGRAM_HOME"] = home;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start engram process.");

        if (stdin is not null)
        {
            try
            {
                process.StandardInput.Write(stdin);
            }
            catch (IOException)
            {
                // A hook that exits before draining stdin is correct behaviour, not a
                // failure: an uninitialised home is meant to be a silent no-op, and the
                // switch that reads the payload is never reached. The write then lands on
                // a closed pipe. Swallowing it here keeps that path testable at all.
            }
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

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
