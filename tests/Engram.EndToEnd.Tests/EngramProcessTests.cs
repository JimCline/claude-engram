using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The harness itself, driven against a stub instead of the published binary. Its bounds
/// have to hold against a child that misbehaves in ways no green suite ever exercises —
/// which is exactly why they were wrong for as long as no test could say so.
/// </summary>
public sealed class EngramProcessTests
{
    // A process can exit while something it spawned keeps the output pipe open — on
    // Windows CI it was the console host of a crashed binary, here it is a backgrounded
    // sleep. ReadToEnd waits for the pipe's last holder, not for the process, so the
    // old read-then-wait order turned this shape into a hang the 10-second bound never
    // saw. The harness must fail it in bounded time and say what actually happened.
    [Fact]
    public void AnExitedProcessWhosePipeIsStillHeld_FailsInBoundedTime_RatherThanHanging()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The pipe-holding stub is a shell script.");

        var dir = Directory.CreateTempSubdirectory("engram-harness-");
        try
        {
            var stub = Path.Combine(dir.FullName, "holds-pipe.sh");
            File.WriteAllText(stub, "#!/bin/sh\nsleep 30 &\nexit 0\n");
#pragma warning disable CA1416 // The SkipWhen above is what makes this unreachable on Windows.
            File.SetUnixFileMode(
                stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416

            var clock = Stopwatch.StartNew();
            var failure = Assert.Throws<TimeoutException>(
                () => EngramProcess.Execute(stub, dir.FullName, stdin: null, args: []));
            clock.Stop();

            Assert.Contains("pipes never closed", failure.Message);

            // The join bound is 5 seconds. Anything near the sleep's 30 means the wait
            // was on the pipe holder after all, and the bound proved nothing.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
