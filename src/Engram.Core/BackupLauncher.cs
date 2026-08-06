using System.Diagnostics;
using System.Text;

namespace Engram.Core;

/// <summary>
/// Starts <c>engram backup take --if-due</c> as a detached child and returns without waiting.
/// </summary>
/// <remarks>
/// <para><b>Why a child at all.</b> A snapshot is a <c>VACUUM INTO</c> over the whole store — tens
/// of milliseconds today and more as the store grows, against a hook that has a budget. Doing it
/// inline would put an unbounded operation on the path of every session start. Forking costs the
/// parent one <c>fork</c>/<c>exec</c> and nothing else: it never waits, never reads the child's
/// output, and does not care whether the child succeeded.</para>
///
/// <para><b>Why session start.</b> It is the moment that correlates with facts about to be
/// written, and the moment a person is not waiting on a result. A timer would need a daemon that
/// is not always running — hooks write facts whether or not the server is up — and a cron entry
/// would be a second installation surface to own and uninstall (D28).</para>
///
/// <para>Routed through <c>/bin/sh</c> with its descriptors redirected for the same reason
/// <see cref="ProcessServerLauncher"/> is: a child inheriting a piped stdout holds that pipe open,
/// so a harness reading the parent's output waits for an EOF that the child is still holding.</para>
/// </remarks>
public static class BackupLauncher
{
    public static void SpawnIfDue(string executablePath, string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        var command = new StringBuilder("exec ")
            .Append(ShellQuote(executablePath))
            .Append(" backup take --if-due --home ")
            .Append(ShellQuote(homeRoot))
            .Append(" </dev/null >/dev/null 2>&1")
            .ToString();

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
