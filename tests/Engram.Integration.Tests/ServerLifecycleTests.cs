using Engram.Core;

namespace Engram.Integration.Tests;

public class ServerLifecycleTests
{
    private const string ExePath = "/opt/engram/engram";
    private const string OtherExePath = "/opt/other/some-other-binary";
    private const string Version = "0.1.0";

    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ServerLifecycleTimeouts FastTimeouts = new(
        StartupTimeout: TimeSpan.FromMilliseconds(500),
        SingleHealthCheckTimeout: TimeSpan.FromMilliseconds(20),
        HealthCheckTimeout: TimeSpan.FromMilliseconds(20),
        TerminateTimeout: TimeSpan.FromMilliseconds(100),
        PollInterval: TimeSpan.FromMilliseconds(5));

    private static (ServerLifecycle Lifecycle, FakeProcessInspector Inspector, FakeServerHealthChecker Health, FakeServerLauncher Launcher) CreateLifecycle()
    {
        var inspector = new FakeProcessInspector();
        var health = new FakeServerHealthChecker();
        var launcher = new FakeServerLauncher();
        return (new ServerLifecycle(inspector, health, launcher), inspector, health, launcher);
    }

    private static HealthCheckOutcome Healthy(int pid, int port, string version, DateTimeOffset startTime) =>
        new(HealthCheckStatus.Healthy, new HealthResponsePayload(pid, port, version, startTime));

    // absent | — | — | — | start
    [Fact]
    public void Start_NoPidFile_LaunchesAndWritesNewPidFile()
    {
        using var sandbox = new SandboxHome();
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        health.Enqueue(Healthy(pid: 1234, port: 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);

        var written = PidFile.Read(sandbox.Home);
        Assert.NotNull(written);
        Assert.Equal(1234, written!.Pid);
        Assert.Equal(7433, written.Port);
    }

    // present | dead | — | — | stale file -> remove, start
    [Fact]
    public void Start_StalePidFile_ProcessDead_RemovesAndStarts()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(999, 7433, Version, StartTime));
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        health.Enqueue(Healthy(pid: 4321, port: 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);

        var written = PidFile.Read(sandbox.Home);
        Assert.Equal(4321, written!.Pid);
    }

    // present | alive | ours | answers with our pid+version | already running -> exit 0
    [Fact]
    public void Start_AlreadyRunning_OursAndHealthy_ExitsWithoutLaunchingOrKilling()
    {
        using var sandbox = new SandboxHome();
        var record = new PidFileRecord(555, 7433, Version, StartTime);
        PidFile.Write(sandbox.Home, record);
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        inspector.SetAlive(555, new ProcessIdentity(ExePath, StartTime));
        health.Enqueue(Healthy(555, 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Equal(record, PidFile.Read(sandbox.Home));
    }

    // present | alive | ours | no answer | orphan -> kill, clean up, start
    [Fact]
    public void Start_Orphan_NoAnswer_KillsCleansUpAndStarts()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(777, 7433, Version, StartTime));
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        inspector.SetAlive(777, new ProcessIdentity(ExePath, StartTime));
        inspector.DieOnTerminate.Add(777);
        health.Enqueue(new HealthCheckOutcome(HealthCheckStatus.NoResponse, null));
        health.Enqueue(Healthy(pid: 888, port: 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal([777], inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(888, PidFile.Read(sandbox.Home)!.Pid);
    }

    // present | alive | ours | wrong version | orphan -> kill, clean up, start
    [Fact]
    public void Start_Orphan_WrongVersion_KillsCleansUpAndStarts()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(777, 7433, Version, StartTime));
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        inspector.SetAlive(777, new ProcessIdentity(ExePath, StartTime));
        inspector.DieOnTerminate.Add(777);
        health.Enqueue(Healthy(pid: 777, port: 7433, "0.0.1-stale", StartTime));
        health.Enqueue(Healthy(pid: 888, port: 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal([777], inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Equal(888, PidFile.Read(sandbox.Home)!.Pid);
    }

    [Fact]
    public void Start_Orphan_DoesNotDieOnTerminate_EscalatesToKill()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(777, 7433, Version, StartTime));
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        inspector.SetAlive(777, new ProcessIdentity(ExePath, StartTime));
        health.Enqueue(new HealthCheckOutcome(HealthCheckStatus.NoResponse, null));
        health.Enqueue(Healthy(pid: 888, port: 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal([777], inspector.TerminateCalls);
        Assert.Equal([777], inspector.KillCalls);
        Assert.Equal(1, launcher.LaunchCount);
    }

    // present | alive | not ours | — | PID reused -> remove file, start; never kill.
    // This is the dangerous row: a stray process must never be touched.
    [Fact]
    public void Start_PidReused_DifferentExecutable_RemovesFileButNeverKills()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(888, 7433, Version, StartTime));
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        inspector.SetAlive(888, new ProcessIdentity(OtherExePath, StartTime));
        health.Enqueue(Healthy(pid: 999, port: 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(999, PidFile.Read(sandbox.Home)!.Pid);
    }

    // Same executable path but a different recorded start time is also "not ours":
    // it means the PID was recycled by another instance of engram itself.
    [Fact]
    public void Start_PidReused_SameExecutableDifferentStartTime_RemovesFileButNeverKills()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(888, 7433, Version, StartTime));
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        inspector.SetAlive(888, new ProcessIdentity(ExePath, StartTime.AddMinutes(5)));
        health.Enqueue(Healthy(pid: 999, port: 7433, Version, StartTime));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Equal(999, PidFile.Read(sandbox.Home)!.Pid);
    }

    // — | — | — | port held by a stranger | report clearly; never kill
    [Fact]
    public void Start_PortHeldByStranger_ReportsAndWritesNoPidFile()
    {
        using var sandbox = new SandboxHome();
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        health.Enqueue(new HealthCheckOutcome(HealthCheckStatus.Unrecognized, null));

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.PortHeldByStranger, result.Outcome);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void Start_NeverBecomesHealthy_ReturnsFailedWithoutWritingPidFile()
    {
        using var sandbox = new SandboxHome();
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.Failed, result.Outcome);
        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void Start_CalledTwiceInARow_SecondCallSeesAlreadyRunningAndDoesNotRelaunch()
    {
        using var sandbox = new SandboxHome();
        var (lifecycle, inspector, health, launcher) = CreateLifecycle();
        health.Enqueue(Healthy(pid: 42, port: 7433, Version, StartTime));

        var first = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);
        Assert.Equal(StartOutcome.Started, first.Outcome);

        inspector.SetAlive(42, new ProcessIdentity(ExePath, StartTime));
        health.Enqueue(Healthy(pid: 42, port: 7433, Version, StartTime));

        var second = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.AlreadyRunning, second.Outcome);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
    }

    [Fact]
    public void Stop_NoPidFile_ExitsNothingRunningWithoutKilling()
    {
        using var sandbox = new SandboxHome();
        var (lifecycle, inspector, _, _) = CreateLifecycle();

        var result = lifecycle.Stop(sandbox.Home, ExePath, FastTimeouts);

        Assert.Equal(StopOutcome.NothingRunning, result.Outcome);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
    }

    [Fact]
    public void Stop_StalePidFile_RemovesFileWithoutKilling()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(321, 7433, Version, StartTime));
        var (lifecycle, inspector, _, _) = CreateLifecycle();

        var result = lifecycle.Stop(sandbox.Home, ExePath, FastTimeouts);

        Assert.Equal(StopOutcome.NothingRunning, result.Outcome);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void Stop_Running_Ours_TerminatesAndRemovesPidFile()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(654, 7433, Version, StartTime));
        var (lifecycle, inspector, _, _) = CreateLifecycle();
        inspector.SetAlive(654, new ProcessIdentity(ExePath, StartTime));
        inspector.DieOnTerminate.Add(654);

        var result = lifecycle.Stop(sandbox.Home, ExePath, FastTimeouts);

        Assert.Equal(StopOutcome.Stopped, result.Outcome);
        Assert.Equal([654], inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void Stop_Running_Ours_EscalatesToKillWhenUnresponsive()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(654, 7433, Version, StartTime));
        var (lifecycle, inspector, _, _) = CreateLifecycle();
        inspector.SetAlive(654, new ProcessIdentity(ExePath, StartTime));

        var result = lifecycle.Stop(sandbox.Home, ExePath, FastTimeouts);

        Assert.Equal(StopOutcome.Stopped, result.Outcome);
        Assert.Equal([654], inspector.TerminateCalls);
        Assert.Equal([654], inspector.KillCalls);
        Assert.Null(PidFile.Read(sandbox.Home));
    }

    // The dangerous row for stop too: a reused PID must never be killed.
    [Fact]
    public void Stop_PidReused_RemovesFileButNeverKills()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(321, 7433, Version, StartTime));
        var (lifecycle, inspector, _, _) = CreateLifecycle();
        inspector.SetAlive(321, new ProcessIdentity(OtherExePath, StartTime));

        var result = lifecycle.Stop(sandbox.Home, ExePath, FastTimeouts);

        Assert.Equal(StopOutcome.NothingRunning, result.Outcome);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void Status_NoPidFile_ReturnsNotRunning()
    {
        using var sandbox = new SandboxHome();
        var (lifecycle, inspector, _, launcher) = CreateLifecycle();

        var status = lifecycle.Status(sandbox.Home, ExePath, Version, FastTimeouts.HealthCheckTimeout);

        Assert.Equal(ServerStatusKind.NotRunning, status.Kind);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public void Status_StalePidFile_ReturnsStaleWithoutMutatingAnything()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(1, 7433, Version, StartTime));
        var (lifecycle, inspector, _, launcher) = CreateLifecycle();

        var status = lifecycle.Status(sandbox.Home, ExePath, Version, FastTimeouts.HealthCheckTimeout);

        Assert.Equal(ServerStatusKind.Stale, status.Kind);
        Assert.NotNull(PidFile.Read(sandbox.Home));
        Assert.Empty(inspector.TerminateCalls);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public void Status_Running_ReturnsRunningWithHealthDetails()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(2, 7433, Version, StartTime));
        var (lifecycle, inspector, health, _) = CreateLifecycle();
        inspector.SetAlive(2, new ProcessIdentity(ExePath, StartTime));
        health.Enqueue(Healthy(2, 7433, Version, StartTime));

        var status = lifecycle.Status(sandbox.Home, ExePath, Version, FastTimeouts.HealthCheckTimeout);

        Assert.Equal(ServerStatusKind.Running, status.Kind);
        Assert.Equal(2, status.Health!.Pid);
        Assert.Equal(Version, status.Health.Version);
    }

    [Fact]
    public void Status_Wedged_IdentityOursButUnhealthy_ReturnsWedgedWithoutKilling()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(3, 7433, Version, StartTime));
        var (lifecycle, inspector, health, _) = CreateLifecycle();
        inspector.SetAlive(3, new ProcessIdentity(ExePath, StartTime));
        health.Enqueue(new HealthCheckOutcome(HealthCheckStatus.NoResponse, null));

        var status = lifecycle.Status(sandbox.Home, ExePath, Version, FastTimeouts.HealthCheckTimeout);

        Assert.Equal(ServerStatusKind.Wedged, status.Kind);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
    }

    // A 200 carrying JSON is not proof the responder is us: any JSON object
    // deserializes into the health payload with every field defaulted, so an unrelated
    // local service holding the port would otherwise be written into our pid file and
    // reported as a server we started.
    [Fact]
    public void Start_PortAnswersWithAPayloadThatIsNotOurs_RefusesAndRecordsNothing()
    {
        using var sandbox = new SandboxHome();
        var (lifecycle, inspector, health, _) = CreateLifecycle();
        health.Default = Healthy(999, 7433, "not-the-version-we-are", StartTime);

        var result = lifecycle.Start(sandbox.Home, ExePath, Version, 7433, FastTimeouts);

        Assert.Equal(StartOutcome.PortHeldByStranger, result.Outcome);
        Assert.Null(PidFile.Read(sandbox.Home));
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
    }

    [Fact]
    public void Status_PidReused_ReturnsReusedWithoutKilling()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(4, 7433, Version, StartTime));
        var (lifecycle, inspector, _, _) = CreateLifecycle();
        inspector.SetAlive(4, new ProcessIdentity(OtherExePath, StartTime));

        var status = lifecycle.Status(sandbox.Home, ExePath, Version, FastTimeouts.HealthCheckTimeout);

        Assert.Equal(ServerStatusKind.Reused, status.Kind);
        Assert.Empty(inspector.TerminateCalls);
        Assert.Empty(inspector.KillCalls);
        Assert.NotNull(PidFile.Read(sandbox.Home));
    }
}
