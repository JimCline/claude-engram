using Engram.Core;

namespace Engram.Cli;

internal static class StatusCommand
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
        var initialized = File.Exists(home.ConfigPath);

        var lifecycle = new ServerLifecycle(new ProcessInspector(), new HttpServerHealthChecker(), new ProcessServerLauncher());
        var status = lifecycle.Status(home, executablePath, EngramVersion.Current, ServerLifecycleTimeouts.Default.HealthCheckTimeout);

        stdout.WriteLine($"home: {home.Root} ({(initialized ? "initialised" : "not initialised")})");

        switch (status.Kind)
        {
            case ServerStatusKind.Running:
                var health = status.Health!;
                var uptime = DateTimeOffset.UtcNow - health.StartTimeUtc;
                stdout.WriteLine("server: running");
                stdout.WriteLine($"pid: {health.Pid}");
                stdout.WriteLine($"port: {health.Port}");
                stdout.WriteLine($"version: {health.Version}");
                stdout.WriteLine($"uptime: {FormatUptime(uptime)}");
                return 0;

            case ServerStatusKind.Stale:
                stdout.WriteLine("server: not running (stale pid file)");
                return 1;

            case ServerStatusKind.Wedged:
                stdout.WriteLine($"server: not running (pid {status.Recorded!.Pid} is not answering)");
                return 1;

            case ServerStatusKind.Reused:
                stdout.WriteLine("server: not running (pid file referred to a different process)");
                return 1;

            case ServerStatusKind.NotRunning:
            default:
                stdout.WriteLine("server: not running");
                return 1;
        }
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }

        return uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
            : $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
    }
}
