using Engram.Core;

namespace Engram.Cli;

internal static class StartCommand
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
        var result = lifecycle.Start(home, executablePath, EngramVersion.Current, resolvedPort, ServerLifecycleTimeouts.Default);

        switch (result.Outcome)
        {
            case StartOutcome.AlreadyRunning:
            case StartOutcome.Started:
                stdout.WriteLine($"{result.Message} (pid {result.Server!.Pid}, port {result.Server.Port})");
                return 0;

            default:
                stderr.WriteLine($"error: {result.Message}");
                return 1;
        }
    }
}

internal static class ExecutablePath
{
    // Environment.ProcessPath and Process.MainModule.FileName were measured to agree
    // even when the binary is reached through a symlink — the runtime canonicalizes
    // both — so the identity check needs no resolution of its own.
    public static string Current { get; } =
        Environment.ProcessPath ?? throw new InvalidOperationException("Unable to determine the current executable path.");
}
