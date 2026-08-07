using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

internal static class EngramProcess
{
    public static (int ExitCode, string Stdout, string Stderr) Run(string home, params string[] args) =>
        RunWithStdin(home, stdin: null, args);

    public static (int ExitCode, string Stdout, string Stderr) RunWithStdin(string home, string? stdin, params string[] args) =>
        Execute(EndToEndBinary.Path!, home, stdin, args);

    internal static (int ExitCode, string Stdout, string Stderr) Execute(string binary, string home, string? stdin, string[] args)
    {
        var startInfo = new ProcessStartInfo(binary)
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

        // Drain both pipes concurrently while the bounded wait runs. Reading on this
        // thread first would trade one deadlock for another: a full pipe blocks the
        // child (GitFileLister documents that half), but ReadToEnd blocks until the
        // pipe's WRITE end closes, and that is not the same event as the child
        // exiting. Windows CI measured the gap — a crashed binary left its console
        // host holding the redirected handle, EOF never came, and the 10-second
        // bound below was never reached. Seven runs burned to the job cap on it.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("engram process did not exit within 10 seconds.");
        }

        // The process is gone; the readers may not be done — or may never finish, if
        // something it spawned inherited the pipe and outlived it. A bounded join is
        // what keeps one wedged invocation from hanging the whole suite.
        if (!Task.WaitAll([stdoutTask, stderrTask], 5_000))
        {
            throw new TimeoutException(
                "engram exited but its output pipes never closed — something it spawned still holds them.");
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
