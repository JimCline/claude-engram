using System.Diagnostics;
using System.Text;

namespace Engram.Core;

/// <summary>
/// Runs the housekeeping a session start owes, as a detached child, and returns without waiting.
/// </summary>
/// <remarks>
/// <para><b>Why a child at all.</b> A snapshot is a <c>VACUUM INTO</c> over the whole store — tens
/// of milliseconds today and more as the store grows, against a hook that has a budget. Doing it
/// inline would put an unbounded operation on the path of every session start. Forking costs the
/// parent one <c>fork</c>/<c>exec</c> and nothing else: it never waits, never reads the child's
/// output, and does not care whether the child succeeded.</para>
///
/// <para><b>Why session start.</b> It is the moment that correlates with facts about to be written,
/// and the moment a person is not waiting on a result. A timer would need a daemon that is not
/// always running — hooks write facts whether or not the server is up — and a cron entry would be a
/// second installation surface to own and uninstall (D28).</para>
///
/// <para><b>Why both jobs share one fork.</b> The queue compaction has the same shape as the
/// snapshot: unbounded in principle, cheap in the common case, and nobody is waiting on it. Giving
/// it a second <see cref="Process.Start"/> would double the one cost the parent actually pays to
/// save nothing, since the children run detached either way. Each job carries its own
/// already-cheap-when-idle guard — <c>--if-due</c> and <c>--if-large</c> — so this can stay
/// unconditional and not grow a policy of its own.</para>
///
/// <para>Routed through <c>/bin/sh</c> with its descriptors redirected for the same reason
/// <see cref="ProcessServerLauncher"/> is: a child inheriting a piped stdout holds that pipe open,
/// so a harness reading the parent's output waits for an EOF that the child is still holding.</para>
/// </remarks>
public static class MaintenanceLauncher
{
    public static void Spawn(string executablePath, string homeRoot, string? indexRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        var engram = ShellQuote(executablePath);
        var home = " --home " + ShellQuote(homeRoot);

        var command = new StringBuilder("{ ")
            .Append(engram).Append(" backup take --if-due").Append(home).Append("; ")
            .Append(engram).Append(" queue compact --apply --if-large").Append(home).Append("; ")
            .Append(engram).Append(" repair --apply --tokens").Append(home).Append("; ");

        // After the compaction on purpose: folding the queue first makes the drain read
        // fewer entries and lose nothing. --auto carries the whole policy — config gate,
        // git-checkout check, store existence — because the child deciding for itself is
        // what lets this stay unconditional, exactly like --if-due and --if-large.
        if (indexRoot is not null)
        {
            command.Append(engram).Append(" index --drain --apply --auto ")
                .Append(ShellQuote(indexRoot)).Append(home).Append("; ");
        }

        var script = command
            .Append("} </dev/null >/dev/null 2>&1")
            .ToString();

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
