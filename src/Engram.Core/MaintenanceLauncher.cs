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
/// <para><b>Why the redirection is on the shell and not on the group.</b> A child inheriting a
/// piped stdout holds that pipe open, and a reader of the parent's output waits for an EOF that
/// only the last writer can give. <c>ServerLauncher</c> escapes this by <c>exec</c>-ing its one
/// command, so no shell remains; this runs several commands and cannot, and the adaptation that
/// wrote <c>{ … } >/dev/null</c> instead lost the property it was adapting — the group's
/// descriptors were replaced and the shell's were not, so <c>/bin/sh</c> sat holding the pipe for
/// as long as the slowest job took. Measured on the published binary as the difference between
/// timing the hook through a pipe and through a file, which is exactly that wait:
/// <b>+76.6 ms at 5,308 live facts and +44.0 ms at 50,097</b>, against <b>+0.4 ms</b> for
/// <c>subagent-start</c>, which builds the same primer and forks nothing. With <c>exec</c> the
/// same measurement reads −0.2 and −0.1 ms. This was never a harness artifact: Claude Code reads
/// this hook's stdout to receive the primer, so every session start was waiting on the backup,
/// the queue compaction, the token repair and <c>index --drain</c> — the whole of what detaching
/// exists to avoid. With it gone the fork costs the parent 1.6–3.3 ms, which is what the class
/// always claimed.</para>
/// </remarks>
public static class MaintenanceLauncher
{
    public static void Spawn(
        string executablePath,
        string homeRoot,
        string? indexRoot = null,
        MaintenanceJobs jobs = MaintenanceJobs.SessionStart)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(BuildScript(executablePath, homeRoot, indexRoot, jobs));

        using var process = Process.Start(startInfo);
    }

    /// <summary>
    /// The shell script the detached child runs. Separate from <see cref="Spawn"/> so the
    /// redirection can be asserted without starting a process.
    /// </summary>
    internal static string BuildScript(
        string executablePath,
        string homeRoot,
        string? indexRoot,
        MaintenanceJobs jobs = MaintenanceJobs.SessionStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        var engram = ShellQuote(executablePath);
        var home = " --home " + ShellQuote(homeRoot);

        var command = new StringBuilder("{ ");

        // --auto gates ambient work; it may not gate commanded work (spec §6.9). An enrollment
        // index is someone typing `engram repo enroll` and is never ambient, so it runs none of
        // the idle-guarded housekeeping below and never carries --auto on the index job itself.
        if (jobs == MaintenanceJobs.SessionStart)
        {
            command
                .Append(engram).Append(" backup take --if-due").Append(home).Append("; ")
                .Append(engram).Append(" queue compact --apply --if-large").Append(home).Append("; ")
                .Append(engram).Append(" repair --apply --tokens").Append(home).Append("; ")
                .Append(engram).Append(" sync import --if-new --apply").Append(home).Append("; ")
                .Append(engram).Append(" sync export --if-due --apply").Append(home).Append("; ")
                .Append(engram).Append(" sync compact --apply --if-large").Append(home).Append("; ");
        }

        if (indexRoot is not null)
        {
            // The enrollment job passes neither --auto (see above) nor --full: the first index
            // is full because last_full_scan_at is NULL (§6.3a), and an explicit flag here would
            // permanently disarm guard 1's falsification of that mechanism.
            var indexInvocation = jobs == MaintenanceJobs.EnrollmentIndex
                ? " index --drain --apply "
                : " index --drain-all --apply --auto ";

            command.Append(engram).Append(indexInvocation)
                .Append(ShellQuote(indexRoot)).Append(home).Append("; ");

            // --skip <indexRoot> is not redundant with running this after the drain-all job:
            // the stamp that would make the invoked root ineligible only lands when the setting
            // is on and the scan completes, so with the setting off, or after a truncated scan,
            // the invoked root is still a candidate and would otherwise be freshened twice in
            // the same session start (spec §5.4).
            if (jobs == MaintenanceJobs.SessionStart)
            {
                command.Append(engram).Append(" index --freshen --apply --skip ")
                    .Append(ShellQuote(indexRoot)).Append(home).Append("; ");
            }
        }

        // `exec` before the group, not a redirection after it. Redirecting the group covers the
        // engram children and leaves /bin/sh itself holding whatever it inherited, so the pipe
        // stays open for as long as the slowest job runs — which is the whole of what this
        // redirection exists to prevent. `exec` with no command replaces sh's own descriptors,
        // so the inherited pipe has no writer left the moment the shell starts.
        return Redirect + command.Append("}").ToString();
    }

    /// <summary>
    /// What to run in the detached child. <see cref="EnrollmentIndex"/> exists because
    /// <c>auto_index_on_session_start</c> answers "may Engram index on its own", not "must Engram
    /// obey an instruction" — so an explicit <c>repo enroll</c> must not be silenced by a setting
    /// that only governs ambient work (spec §6.9).
    /// </summary>
    public enum MaintenanceJobs
    {
        /// <summary>
        /// Housekeeping, an --auto --drain-all index of indexRoot, and an --freshen self-heal of one
        /// other neglected repo. The fork itself is never gated — only the jobs inside it are:
        /// --freshen's UnfulfilledEnrollment bypass (spec §5.3) can run one non-ambient scan even
        /// with auto_index_on_session_start off, alongside the otherwise-ambient work around it.
        /// </summary>
        SessionStart,

        /// <summary>The index of indexRoot alone, --drain --apply, with no --auto and no --full.</summary>
        EnrollmentIndex,
    }

    /// <summary>
    /// Replaces the shell's own descriptors before it runs anything.
    /// </summary>
    internal const string Redirect = "exec </dev/null >/dev/null 2>&1; ";

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
