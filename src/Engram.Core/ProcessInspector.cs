using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Engram.Core;

public sealed record ProcessIdentity(string ExecutablePath, DateTimeOffset StartTimeUtc);

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

            var executablePath = process.MainModule?.FileName;
            return executablePath is null
                ? null
                : new ProcessIdentity(executablePath, process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
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
