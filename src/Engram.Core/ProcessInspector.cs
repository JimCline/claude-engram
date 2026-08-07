using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Engram.Core;

/// <summary>Which process, and which run of it, a pid currently refers to.</summary>
/// <remarks>
/// <para><see cref="StartToken"/> is the identity and is required — see
/// <see cref="ProcessStartToken"/> for why the wall clock cannot serve.
/// <see cref="StartTimeUtc"/> survives as display metadata and for pid files written before tokens
/// existed; it decides nothing once a token is present.</para>
///
/// <para><see cref="ExecutablePath"/> is nullable because it is provenance rather than identity
/// (D42), and a platform that will not hand it over must not thereby destroy the rest.</para>
/// </remarks>
public sealed record ProcessIdentity(string? ExecutablePath, DateTimeOffset StartTimeUtc, string StartToken);

public interface IProcessInspector
{
    bool IsRunning(int pid);

    ProcessIdentity? GetIdentity(int pid);

    void Terminate(int pid);

    void Kill(int pid);
}

public sealed class ProcessInspector : IProcessInspector
{
    public bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public ProcessIdentity? GetIdentity(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return null;
            }

            // An identity that cannot be verified is no identity at all: callers act on a match by
            // sending SIGTERM, so an unobtainable token has to fail toward leaving the process
            // alone. An unobtainable path is the opposite case and costs only the provenance line.
            return ProcessStartToken.ForProcess(process) is { } token
                ? new ProcessIdentity(ExecutablePathOf(process), process.StartTime.ToUniversalTime(), token)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Where a process was launched from, or null if this platform will not say.</summary>
    /// <remarks>
    /// Isolated so that failing to answer costs the caller a provenance line and nothing else. Rolled
    /// into <see cref="GetIdentity"/> it did far more: <c>MainModule</c> reads <c>/proc/&lt;pid&gt;/maps</c>
    /// on Linux and is refused for a process the caller does not own, so a null there discarded a start
    /// time that had already been read successfully, <c>Status</c> answered <c>Reused</c>, and a running
    /// server was reported dead. That is the D42 damage exactly — <c>stop</c> deletes the pid file, says
    /// "not running", and leaves a live server with nothing left to address it by.
    /// </remarks>
    private static string? ExecutablePathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public void Terminate(int pid) => PosixSignal.SendTerminate(pid);

    public void Kill(int pid) => PosixSignal.SendKill(pid);
}

internal static partial class PosixSignal
{
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;

    public static void SendTerminate(int pid) => kill(pid, SIGTERM);

    public static void SendKill(int pid) => kill(pid, SIGKILL);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);
}
