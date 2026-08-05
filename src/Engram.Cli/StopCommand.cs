using Engram.Core;

namespace Engram.Cli;

internal static class StopCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 0)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var executablePath = ExecutablePath.Current;

        var lifecycle = new ServerLifecycle(new ProcessInspector(), new HttpServerHealthChecker(), new ProcessServerLauncher());
        var result = lifecycle.Stop(home, executablePath, ServerLifecycleTimeouts.Default);

        stdout.WriteLine(result.Message);
        return 0;
    }
}
