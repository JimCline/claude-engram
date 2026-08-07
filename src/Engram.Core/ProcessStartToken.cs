using System.Diagnostics;
using System.Globalization;

namespace Engram.Core;

/// <summary>
/// An opaque token naming which <i>run</i> of a process a pid refers to. Compared by ordinal
/// equality and by nothing else.
/// </summary>
/// <remarks>
/// <para><b>Identity is the kernel's record of when a process started, never .NET's reconstruction
/// of it.</b> On Linux <c>Process.StartTime</c> is the <c>starttime</c> field added to a
/// <i>per-process estimate</i> of boot time, so two processes reading the same pid disagree.
/// Measured in a Linux container: 24 of 24 cross-process reads unequal, by up to 3636 ticks. That
/// skew is not scheduler jitter — it is the difference between two estimates, each taken from the
/// realtime clock at read time, so an NTP step or a VM resume moves it without bound. A tolerance
/// would therefore be either too small (the failure returns as an intermittent flake, worse than
/// the deterministic one) or a number fitted to hoped-for clock behaviour. There is no tolerance
/// here and there may never be one (D42).</para>
///
/// <para><b>Both views come from this one type on purpose.</b> A self-view and a by-pid view
/// written separately are two implementations of a single comparison, and the first time they
/// diverge is a server reporting itself dead.</para>
///
/// <para><b>macOS and Windows keep the value they already used.</b> Both kernels store an absolute
/// process creation time — <c>kinfo_proc.kp_proc.p_starttime</c> and the <c>GetProcessTimes</c>
/// FILETIME — so <see cref="Process.StartTime"/> is already reader-independent there and needs no
/// new mechanism. Leaving those platforms on their existing value is deliberate: Windows cannot be
/// tested on the hardware this was written on, and a fix it does not touch is a fix that cannot
/// regress it.</para>
/// </remarks>
public static class ProcessStartToken
{
    /// <summary>Field 22 of <c>/proc/&lt;pid&gt;/stat</c>, one-indexed, counting from the pid.</summary>
    private const int StarttimeField = 22;

    /// <summary>Where <c>starttime</c> lands once fields 1 and 2 are cut away.</summary>
    /// <remarks>The first field after <c>comm</c> is field 3, so field N sits at N - 3.</remarks>
    private const int StarttimeIndex = StarttimeField - 3;

    private static readonly Lazy<string> LinuxBootId = new(ReadBootId);

    /// <summary>The token for the calling process, or null if this platform will not say.</summary>
    public static string? ForSelf() => ForPid(Environment.ProcessId);

    /// <summary>The token for <paramref name="pid"/>, or null if it cannot be obtained.</summary>
    public static string? ForPid(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            return FromProcFs(pid);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return FromKernelClock(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The token for a process already open, so the caller does not reopen it.</summary>
    internal static string? ForProcess(Process process) =>
        OperatingSystem.IsLinux() ? FromProcFs(process.Id) : FromKernelClock(process);

    private static string? FromKernelClock(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>&lt;boot_id&gt;:&lt;starttime&gt;</c> — both kernel-authored, both identical for every reader.
    /// </summary>
    /// <remarks>
    /// The boot id restores the across-reboot uniqueness the absolute wall clock used to supply:
    /// <c>starttime</c> alone counts from boot, so two processes on either side of a reboot can
    /// share a value within one clock tick.
    /// </remarks>
    private static string? FromProcFs(int pid) =>
        StarttimeOf(pid) is { } starttime ? $"{LinuxBootId.Value}:{starttime}" : null;

    private static string? StarttimeOf(int pid)
    {
        try
        {
            return ParseStarttime(File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Field 22 of a <c>/proc/&lt;pid&gt;/stat</c> line, or null if it is not there.</summary>
    internal static string? ParseStarttime(string line)
    {
        // Cut at the LAST ')', never the first: field 2 is comm, which the process chooses and which
        // may hold spaces and parentheses alike. Splitting the whole line, or cutting at the first
        // ')', is how a process named "(evil) sh" gets to nominate its own start time.
        var afterComm = line.LastIndexOf(')');
        if (afterComm < 0)
        {
            return null;
        }

        var fields = line[(afterComm + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= StarttimeIndex
            || !ulong.TryParse(fields[StarttimeIndex], CultureInfo.InvariantCulture, out var starttime))
        {
            return null;
        }

        // Re-rendered rather than passed through, so the token is canonical whatever the kernel's
        // formatting: ordinal equality is the whole comparison.
        return starttime.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The empty string when unreadable, rather than no token at all.
    /// </summary>
    /// <remarks>
    /// Both sides of every comparison run on the same machine, so a deterministic fallback costs
    /// only the reboot distinction — while returning null would make an unreadable file mean "this
    /// server is dead", which is the failure being fixed.
    /// </remarks>
    private static string ReadBootId()
    {
        try
        {
            return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return string.Empty;
        }
    }
}
