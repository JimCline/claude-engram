using Engram.Core;

namespace Engram.Integration.Tests;

internal sealed class FakeProcessInspector : IProcessInspector
{
    private readonly Dictionary<int, ProcessIdentity> _alive = [];
    private readonly HashSet<int> _lingering = [];
    public HashSet<int> DieOnTerminate { get; } = [];

    /// <summary>
    /// Pids that stay observably running for exactly one more <see cref="IsRunning"/> call after
    /// <see cref="Kill"/>, simulating kernel teardown lag after SIGKILL — SIGKILL cannot be
    /// ignored, but it is not synchronous either.
    /// </summary>
    public HashSet<int> LingerAfterKill { get; } = [];

    public List<int> TerminateCalls { get; } = [];
    public List<int> KillCalls { get; } = [];

    /// <summary>Set when a poll observed a lingering pid still running after <see cref="Kill"/>.</summary>
    public bool ObservedRunningAfterKill { get; private set; }

    public void SetAlive(int pid, ProcessIdentity identity) => _alive[pid] = identity;

    public bool IsRunning(int pid)
    {
        if (_lingering.Remove(pid))
        {
            ObservedRunningAfterKill = true;
            return true;
        }

        return _alive.ContainsKey(pid);
    }

    public ProcessIdentity? GetIdentity(int pid) => _alive.GetValueOrDefault(pid);

    public void Terminate(int pid)
    {
        TerminateCalls.Add(pid);
        if (DieOnTerminate.Contains(pid))
        {
            _alive.Remove(pid);
        }
    }

    public void Kill(int pid)
    {
        KillCalls.Add(pid);
        if (LingerAfterKill.Contains(pid))
        {
            _lingering.Add(pid);
        }

        _alive.Remove(pid);
    }
}

internal sealed class FakeServerHealthChecker : IServerHealthChecker
{
    private readonly Queue<HealthCheckOutcome> _responses = new();

    public HealthCheckOutcome Default { get; set; } = new(HealthCheckStatus.NoResponse, null);

    public void Enqueue(HealthCheckOutcome outcome) => _responses.Enqueue(outcome);

    public HealthCheckOutcome Check(int port, TimeSpan timeout) =>
        _responses.Count > 0 ? _responses.Dequeue() : Default;
}

internal sealed class FakeServerLauncher : IServerLauncher
{
    public int LaunchCount { get; private set; }
    public Action? OnLaunch { get; set; }

    public void LaunchDetached(string executablePath, string homeRoot, int port)
    {
        LaunchCount++;
        OnLaunch?.Invoke();
    }
}
