using System.Diagnostics;

namespace Engram.Core;

public sealed record ServerLifecycleTimeouts(
    TimeSpan StartupTimeout,
    TimeSpan SingleHealthCheckTimeout,
    TimeSpan HealthCheckTimeout,
    TimeSpan TerminateTimeout,
    TimeSpan PollInterval)
{
    public static ServerLifecycleTimeouts Default { get; } = new(
        StartupTimeout: TimeSpan.FromSeconds(10),
        SingleHealthCheckTimeout: TimeSpan.FromSeconds(2),
        HealthCheckTimeout: TimeSpan.FromSeconds(2),
        TerminateTimeout: TimeSpan.FromSeconds(5),
        PollInterval: TimeSpan.FromMilliseconds(100));
}

public enum StartOutcome
{
    Started,
    AlreadyRunning,
    PortHeldByStranger,
    Failed,
}

public sealed record StartResult(StartOutcome Outcome, PidFileRecord? Server, string Message);

public enum StopOutcome
{
    Stopped,
    NothingRunning,
}

public sealed record StopResult(StopOutcome Outcome, string Message);

public enum ServerStatusKind
{
    Running,
    NotRunning,
    Stale,
    Wedged,
    Reused,
}

public sealed record StatusResult(ServerStatusKind Kind, PidFileRecord? Recorded, HealthResponsePayload? Health);

public sealed class ServerLifecycle(
    IProcessInspector processInspector,
    IServerHealthChecker healthChecker,
    IServerLauncher launcher)
{
    public StartResult Start(EngramHome home, string executablePath, string ourVersion, int port, ServerLifecycleTimeouts timeouts)
    {
        var record = PidFile.Read(home);
        if (record is not null)
        {
            if (!processInspector.IsRunning(record.Pid))
            {
                PidFile.Delete(home);
            }
            else if (!IsOurs(record, executablePath))
            {
                PidFile.Delete(home);
            }
            else
            {
                var outcome = healthChecker.Check(record.Port, timeouts.HealthCheckTimeout);
                if (IsHealthyMatch(outcome, record, ourVersion))
                {
                    return new StartResult(StartOutcome.AlreadyRunning, record, "engram is already running");
                }

                TerminateAndWait(record.Pid, timeouts);
                PidFile.Delete(home);
            }
        }

        return DoStart(home, executablePath, ourVersion, port, timeouts);
    }

    public StopResult Stop(EngramHome home, string executablePath, ServerLifecycleTimeouts timeouts)
    {
        var record = PidFile.Read(home);
        if (record is null)
        {
            return new StopResult(StopOutcome.NothingRunning, "engram is not running");
        }

        if (!processInspector.IsRunning(record.Pid))
        {
            PidFile.Delete(home);
            return new StopResult(StopOutcome.NothingRunning, "engram is not running");
        }

        if (!IsOurs(record, executablePath))
        {
            PidFile.Delete(home);
            return new StopResult(StopOutcome.NothingRunning, "engram is not running (pid file referred to a different process)");
        }

        TerminateAndWait(record.Pid, timeouts);
        PidFile.Delete(home);
        return new StopResult(StopOutcome.Stopped, "engram stopped");
    }

    public StatusResult Status(EngramHome home, string executablePath, string ourVersion, TimeSpan healthCheckTimeout)
    {
        var record = PidFile.Read(home);
        if (record is null)
        {
            return new StatusResult(ServerStatusKind.NotRunning, null, null);
        }

        if (!processInspector.IsRunning(record.Pid))
        {
            return new StatusResult(ServerStatusKind.Stale, record, null);
        }

        if (!IsOurs(record, executablePath))
        {
            return new StatusResult(ServerStatusKind.Reused, record, null);
        }

        var outcome = healthChecker.Check(record.Port, healthCheckTimeout);
        return IsHealthyMatch(outcome, record, ourVersion)
            ? new StatusResult(ServerStatusKind.Running, record, outcome.Result)
            : new StatusResult(ServerStatusKind.Wedged, record, null);
    }

    private StartResult DoStart(EngramHome home, string executablePath, string ourVersion, int port, ServerLifecycleTimeouts timeouts)
    {
        launcher.LaunchDetached(executablePath, home.Root, port);

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeouts.StartupTimeout)
        {
            var outcome = healthChecker.Check(port, timeouts.SingleHealthCheckTimeout);
            switch (outcome.Status)
            {
                case HealthCheckStatus.Healthy when outcome.Result is { } health && health.Version == ourVersion:
                    var newRecord = new PidFileRecord(health.Pid, health.Port, health.Version, health.StartTimeUtc);
                    PidFile.Write(home, newRecord);
                    return new StartResult(StartOutcome.Started, newRecord, "engram started");

                // A 200 carrying JSON is not proof the responder is us: any JSON object
                // deserializes into the health payload with every field defaulted, so an
                // unrelated local service would be recorded in our pid file as our own
                // server. Ownership we cannot prove is ownership we must not claim.
                case HealthCheckStatus.Healthy:
                case HealthCheckStatus.Unrecognized:
                    return new StartResult(
                        StartOutcome.PortHeldByStranger,
                        null,
                        $"port {port} is already in use by something this engram cannot claim; refusing to touch it");

                case HealthCheckStatus.NoResponse:
                default:
                    Thread.Sleep(timeouts.PollInterval);
                    break;
            }
        }

        return new StartResult(StartOutcome.Failed, null, "server did not become healthy in time");
    }

    private void TerminateAndWait(int pid, ServerLifecycleTimeouts timeouts)
    {
        processInspector.Terminate(pid);

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeouts.TerminateTimeout)
        {
            if (!processInspector.IsRunning(pid))
            {
                return;
            }

            Thread.Sleep(timeouts.PollInterval);
        }

        if (processInspector.IsRunning(pid))
        {
            processInspector.Kill(pid);
        }
    }

    private bool IsOurs(PidFileRecord record, string executablePath)
    {
        var identity = processInspector.GetIdentity(record.Pid);
        return identity is not null
            && identity.ExecutablePath == executablePath
            && identity.StartTimeUtc == record.StartTimeUtc;
    }

    private static bool IsHealthyMatch(HealthCheckOutcome outcome, PidFileRecord record, string ourVersion) =>
        outcome.Status == HealthCheckStatus.Healthy
        && outcome.Result is { } health
        && health.Pid == record.Pid
        && health.Version == ourVersion;
}
