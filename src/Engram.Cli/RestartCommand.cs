using Engram.Core;

namespace Engram.Cli;

internal static class RestartCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        int? port = null;

        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] != "--port")
            {
                CliApp.PrintUsage(stderr);
                return 1;
            }

            if (i + 1 >= rest.Length)
            {
                stderr.WriteLine("error: --port requires a value");
                return 1;
            }

            if (!int.TryParse(rest[++i], out var parsed) || parsed is <= 0 or > 65535)
            {
                stderr.WriteLine("error: --port must be a valid TCP port number");
                return 1;
            }

            port = parsed;
        }

        var resolvedPort = ServerPort.Resolve(port);
        var home = EngramHome.ResolveFromProcess(homePath);
        var executablePath = ExecutablePath.Current;

        var lifecycle = new ServerLifecycle(new ProcessInspector(), new HttpServerHealthChecker(), new ProcessServerLauncher());

        var stopResult = lifecycle.Stop(home, ServerLifecycleTimeouts.Default);
        stdout.WriteLine(stopResult.Message);

        var result = lifecycle.Start(home, executablePath, EngramVersion.Current, resolvedPort, ServerLifecycleTimeouts.Default);

        switch (result.Outcome)
        {
            // AlreadyRunning here means another invocation won the race to start between
            // our Stop and our Start (ServerLifecycleE2ETests.cs:76 covers that race for
            // plain start; the same window exists here) — a running healthy server is not
            // a failed restart.
            case StartOutcome.Started:
            case StartOutcome.AlreadyRunning:
                stdout.WriteLine($"{result.Message} (pid {result.Server!.Pid}, port {result.Server.Port})");
                return 0;

            default:
                stderr.WriteLine($"error: {result.Message}");
                return 1;
        }
    }
}
