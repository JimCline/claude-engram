using System.Text.Json;
using Engram.Core;

namespace Engram.Cli;

internal static class StatusCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var json = false;

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;

                default:
                    CliApp.PrintUsage(stderr);
                    return 1;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var executablePath = ExecutablePath.Current;
        var initialized = File.Exists(home.ConfigPath);

        var lifecycle = new ServerLifecycle(new ProcessInspector(), new HttpServerHealthChecker(), new ProcessServerLauncher());
        var status = lifecycle.Status(home, EngramVersion.Current, ServerLifecycleTimeouts.Default.HealthCheckTimeout);

        if (json)
        {
            var payload = BuildJson(home, initialized, executablePath, status);
            stdout.WriteLine(JsonSerializer.Serialize(payload, StatusJsonContext.Default.StatusJson));
            return ExitCodeFor(status.Kind);
        }

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
                break;

            // Answering correctly, from a build that is not this one — which is what an upgraded
            // binary and an un-restarted server look like, and is not the server's fault.
            case ServerStatusKind.VersionMismatch:
                stdout.WriteLine($"server: running version {status.Health!.Version}, but this engram is {EngramVersion.Current}");
                stdout.WriteLine($"pid: {status.Health.Pid}");
                stdout.WriteLine($"port: {status.Health.Port}");
                WriteLaunchedFrom(status, executablePath, stdout);
                stdout.WriteLine("restart it to pick up this build: engram stop, then engram start");
                break;

            case ServerStatusKind.Stale:
                stdout.WriteLine("server: not running (stale pid file)");
                break;

            case ServerStatusKind.Wedged:
                stdout.WriteLine($"server: not running (pid {status.Recorded!.Pid} is not answering)");
                break;

            case ServerStatusKind.Reused:
                stdout.WriteLine("server: not running (pid file referred to a different process)");
                break;

            case ServerStatusKind.NotRunning:
            default:
                stdout.WriteLine("server: not running");
                break;
        }

        return ExitCodeFor(status.Kind);
    }

    /// <summary>The one place the 0-vs-1 rule lives, so the human and JSON renderers cannot disagree.</summary>
    internal static int ExitCodeFor(ServerStatusKind kind) => kind == ServerStatusKind.Running ? 0 : 1;

    internal static StatusJson BuildJson(EngramHome home, bool initialized, string executablePath, StatusResult status)
    {
        var health = status.Health;
        var startedFrom = status.LaunchedFrom is { Length: > 0 } launchedFrom ? launchedFrom : null;
        var thisBinary = startedFrom is not null && !string.Equals(startedFrom, executablePath, StringComparison.Ordinal)
            ? executablePath
            : null;

        return new StatusJson(
            Home: home.Root,
            Initialised: initialized,
            Server: status.Kind.ToString(),
            Pid: status.Kind switch
            {
                ServerStatusKind.Running or ServerStatusKind.VersionMismatch => health!.Pid,
                ServerStatusKind.Wedged => status.Recorded!.Pid,
                _ => null,
            },
            Port: health?.Port,
            Version: health?.Version,
            StartTimeUtc: health?.StartTimeUtc,
            UptimeSeconds: status.Kind == ServerStatusKind.Running
                ? (long)Math.Max(0, (DateTimeOffset.UtcNow - health!.StartTimeUtc).TotalSeconds)
                : null,
            StartedFrom: startedFrom,
            ThisBinary: thisBinary);
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
