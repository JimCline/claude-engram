using System.ComponentModel;
using Engram.Core;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace Engram.Cli;

public sealed record ServerIdentity(int Pid, int Port, string Version, DateTimeOffset StartTimeUtc)
{
    public HealthResponsePayload ToHealthPayload() => new(Pid, Port, Version, StartTimeUtc);

    public PidFileRecord ToPidFileRecord() => new(Pid, Port, Version, StartTimeUtc);
}

/// <summary>
/// Lifecycle tools. These answer from this process's own state rather than by probing
/// over HTTP: reaching this code at all is proof the server is up, so a health check
/// would only be this process asking itself a question it already knows the answer to.
/// </summary>
/// <remarks>
/// Cold start is structurally out of reach from here — a tool call cannot arrive when
/// there is no server to receive it. Starting the daemon is the SessionStart hook's job;
/// these exist to report, repair a disagreeing pid file, and shut down on request.
/// </remarks>
[McpServerToolType]
public sealed class EngramServerTools
{
    [McpServerTool(Name = "engram_status")]
    [Description(
        "Report whether the Engram memory server is running, on what port, and for how long. " +
        "Receiving any answer at all means it is running.")]
    public static string Status(EngramHome home, ServerIdentity identity)
    {
        var uptime = DateTimeOffset.UtcNow - identity.StartTimeUtc;
        var recorded = PidFile.Read(home);
        var agreement = recorded is null
            ? " No pid file is recorded, so `engram status` from a shell would report it as not running; call engram_start to repair that."
            : recorded.Pid == identity.Pid
                ? string.Empty
                : $" The pid file records pid {recorded.Pid} instead, which means another instance wrote it; call engram_start to repair that.";

        return $"engram is running: pid {identity.Pid}, port {identity.Port}, version {identity.Version}, " +
               $"up {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s.{agreement}";
    }

    [McpServerTool(Name = "engram_start")]
    [Description(
        "Ensure the Engram memory server is running. If this call is answered the server is already " +
        "running, so this only repairs a missing or disagreeing pid file. It never starts a second server.")]
    public static string Start(EngramHome home, ServerIdentity identity)
    {
        var recorded = PidFile.Read(home);
        if (recorded is not null && recorded.Pid == identity.Pid)
        {
            return $"engram is already running (pid {identity.Pid}, port {identity.Port}); nothing to do.";
        }

        PidFile.Write(home, identity.ToPidFileRecord());
        return recorded is null
            ? $"engram was already running (pid {identity.Pid}, port {identity.Port}) but had no pid file recorded; wrote one."
            : $"engram was already running (pid {identity.Pid}, port {identity.Port}) but the pid file recorded pid {recorded.Pid}; corrected it.";
    }

    [McpServerTool(Name = "engram_stop")]
    [Description(
        "Stop the Engram memory server. Memory tools stop working for the rest of this session unless " +
        "something starts it again, so only call this when explicitly asked to.")]
    public static string Stop(ServerIdentity identity, IHostApplicationLifetime lifetime)
    {
        // Shutting down inline would tear down the connection carrying this reply, so the
        // caller would see a transport error rather than an answer. Let the response
        // finish first.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            lifetime.StopApplication();
        });

        return $"engram (pid {identity.Pid}, port {identity.Port}) is shutting down. " +
               "Memory tools will be unavailable until it is started again from a shell with `engram start`.";
    }
}
