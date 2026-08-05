using Engram.Core;

namespace Engram.Integration.Tests;

internal sealed class FakeProcessInspector : IProcessInspector
{
    private readonly Dictionary<int, ProcessIdentity> _alive = [];
    public HashSet<int> DieOnTerminate { get; } = [];
    public List<int> TerminateCalls { get; } = [];
    public List<int> KillCalls { get; } = [];

    public void SetAlive(int pid, ProcessIdentity identity) => _alive[pid] = identity;

    public bool IsRunning(int pid) => _alive.ContainsKey(pid);

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
