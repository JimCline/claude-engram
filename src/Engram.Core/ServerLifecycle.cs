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

    /// <summary>Answering, and answering correctly, from a build that is not this one.</summary>
    VersionMismatch,
}

/// <summary>
/// What is serving this home, and from where.
/// </summary>
/// <param name="LaunchedFrom">
/// The executable the running server was started from, when one could be read. It is reported
/// rather than enforced: the same home is legitimately served by a binary at another path — an
/// installed one while a working copy asks — and a status that called that "not running" was
/// describing itself rather than the server.
/// </param>
public sealed record StatusResult(
    ServerStatusKind Kind,
    PidFileRecord? Recorded,
    HealthResponsePayload? Health,
    string? LaunchedFrom = null)
{
    /// <summary>
    /// Whether a server process is alive on this home, whatever shape it is in.
    /// </summary>
    /// <remarks>
    /// The question anything asks before doing something a live server would fight over. Answering
    /// it with <c>Kind is Running</c> alone is how a caller ends up racing a server it decided was
    /// absent: a wedged one still holds whatever it loaded at startup, and one on another version
    /// is simply up.
    /// </remarks>
    public bool ServerIsAlive =>
        Kind is ServerStatusKind.Running or ServerStatusKind.VersionMismatch or ServerStatusKind.Wedged;
}

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
            else if (RecordedProcess(record) is null)
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

    public StopResult Stop(EngramHome home, ServerLifecycleTimeouts timeouts)
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

        if (RecordedProcess(record) is null)
        {
            PidFile.Delete(home);
            return new StopResult(StopOutcome.NothingRunning, "engram is not running (pid file referred to a different process)");
        }

        TerminateAndWait(record.Pid, timeouts);
        PidFile.Delete(home);
        return new StopResult(StopOutcome.Stopped, "engram stopped");
    }

    public StatusResult Status(EngramHome home, string ourVersion, TimeSpan healthCheckTimeout)
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

        var identity = RecordedProcess(record);
        if (identity is null)
        {
            return new StatusResult(ServerStatusKind.Reused, record, null);
        }

        var outcome = healthChecker.Check(record.Port, healthCheckTimeout);
        if (!IsAnsweringForUs(outcome, record))
        {
            return new StatusResult(ServerStatusKind.Wedged, record, null, identity.ExecutablePath);
        }

        // Answering, and it is ours — the only question left is whether it is the build we are.
        // Collapsing this into Wedged claimed a healthy server was not answering, which sends
        // someone looking for a hang when what they have is a server they have not restarted.
        return outcome.Result!.Version == ourVersion
            ? new StatusResult(ServerStatusKind.Running, record, outcome.Result, identity.ExecutablePath)
            : new StatusResult(ServerStatusKind.VersionMismatch, record, outcome.Result, identity.ExecutablePath);
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
                    var newRecord = new PidFileRecord(
                        health.Pid, health.Port, health.Version, health.StartTimeUtc, health.StartToken);
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

        if (WaitUntilNotRunning(pid, timeouts))
        {
            return;
        }

        if (!processInspector.IsRunning(pid))
        {
            return;
        }

        processInspector.Kill(pid);

        // SIGKILL cannot be ignored, so this second window covers only kernel teardown —
        // but a caller that binds the port right after this returns (Start, composing
        // Stop then Start for restart) loses the bind to a server that still holds it if
        // this returns on the Kill call alone rather than on the pid actually going away.
        WaitUntilNotRunning(pid, timeouts);
    }

    private bool WaitUntilNotRunning(int pid, ServerLifecycleTimeouts timeouts)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeouts.TerminateTimeout)
        {
            if (!processInspector.IsRunning(pid))
            {
                return true;
            }

            Thread.Sleep(timeouts.PollInterval);
        }

        return false;
    }

    /// <summary>
    /// The process this pid file describes, or null if the pid now belongs to something else.
    /// </summary>
    /// <remarks>
    /// <para><b>Start time is the identity; the executable path is not.</b> A pid plus the instant
    /// the kernel started it is unique — that pair is exactly what a recycled pid cannot forge,
    /// because a stranger that inherited the number started at a different moment. The path adds
    /// nothing to that and quietly answered a different question: <i>was this launched from the
    /// same file I am?</i> Two engram binaries legitimately serve one home — an installed one, and
    /// a working copy asking about it — and treating that as a recycled pid was wrong in every
    /// direction it could be. <c>status</c> called a live server dead. <c>stop</c> deleted the pid
    /// file, reported "not running", and left the server running with nothing left to address it
    /// by, so no later <c>stop</c> could find it either. <c>start</c> launched a second server
    /// against a bound port. Measured on the author's instance: the installed binary reported the
    /// server up while a freshly built one reported the same pid file dead, in the same second.
    /// </para>
    ///
    /// <para>The guarantee that mattered is untouched, because it never rested on the path: nothing
    /// is terminated whose start time does not match what was recorded.</para>
    ///
    /// <para><b>"Start time" means the kernel's start token, not .NET's reconstruction of it.</b>
    /// On Linux <c>Process.StartTime</c> adds <c>starttime</c> to a per-process <i>estimate</i> of
    /// boot time, so the value the server reported about itself and the value read back for the
    /// same pid never matched — measured 24 of 24 cross-process reads unequal. Every Linux
    /// <c>status</c> answered <see cref="ServerStatusKind.Reused"/> about a healthy server, which is
    /// the damage above arriving through a mechanism this method's own reasoning did not anticipate.
    /// See <see cref="ProcessStartToken"/> for why no tolerance can repair that.</para>
    ///
    /// <para>The two comparisons below are disjoint and chosen solely by whether the <i>record</i>
    /// carries a token. Nothing may blend them: converting between a token and a wall clock is
    /// <c>bootTime + starttime</c>, and that estimate is the defect itself.</para>
    /// </remarks>
    private ProcessIdentity? RecordedProcess(PidFileRecord record)
    {
        if (processInspector.GetIdentity(record.Pid) is not { } identity)
        {
            return null;
        }

        if (record.StartToken is not { } recorded)
        {
            // Written before tokens existed. Exact, and with no tolerance fallback: a number in the
            // kill path introduced for a population that is empty would outlive the population.
            return identity.StartTimeUtc == record.StartTimeUtc ? identity : null;
        }

        return string.Equals(identity.StartToken, recorded, StringComparison.Ordinal) ? identity : null;
    }

    private static bool IsAnsweringForUs(HealthCheckOutcome outcome, PidFileRecord record) =>
        outcome.Status == HealthCheckStatus.Healthy
        && outcome.Result is { } health
        && health.Pid == record.Pid;

    private static bool IsHealthyMatch(HealthCheckOutcome outcome, PidFileRecord record, string ourVersion) =>
        IsAnsweringForUs(outcome, record) && outcome.Result!.Version == ourVersion;
}
