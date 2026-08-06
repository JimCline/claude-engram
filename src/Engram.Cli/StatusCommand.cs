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
        var status = lifecycle.Status(home, EngramVersion.Current, ServerLifecycleTimeouts.Default.HealthCheckTimeout);

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
                WriteLaunchedFrom(status, executablePath, stdout);
                return 0;

            // Answering correctly, from a build that is not this one — which is what an upgraded
            // binary and an un-restarted server look like, and is not the server's fault.
            case ServerStatusKind.VersionMismatch:
                stdout.WriteLine($"server: running version {status.Health!.Version}, but this engram is {EngramVersion.Current}");
                stdout.WriteLine($"pid: {status.Health.Pid}");
                stdout.WriteLine($"port: {status.Health.Port}");
                WriteLaunchedFrom(status, executablePath, stdout);
                stdout.WriteLine("restart it to pick up this build: engram stop, then engram start");
                return 1;

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

    /// <summary>
    /// Names the binary behind the server when it is not the one being asked.
    /// </summary>
    /// <remarks>
    /// Silent when they agree, because then it says nothing. When they differ it is the whole
    /// explanation for a surprising answer — most often a working copy asking about the installed
    /// server — and printing it unconditionally would bury that in noise.
    /// </remarks>
    private static void WriteLaunchedFrom(StatusResult status, string executablePath, TextWriter stdout)
    {
        if (status.LaunchedFrom is { Length: > 0 } launchedFrom
            && !string.Equals(launchedFrom, executablePath, StringComparison.Ordinal))
        {
            stdout.WriteLine($"started from: {launchedFrom} (you are running {executablePath})");
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
