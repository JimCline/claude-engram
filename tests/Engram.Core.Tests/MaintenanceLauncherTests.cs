using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// The detached child must not hold the hook's stdout, because Claude Code reads that stdout to
/// receive the primer and a pipe reaches EOF only when its last writer closes.
/// </summary>
/// <remarks>
/// Asserted on the script text rather than by timing a spawn. The defect this guards is a
/// difference of tens to hundreds of milliseconds that depends entirely on how much housekeeping
/// happens to be due, so a timing test would be green on an idle store — which is precisely the
/// store a test starts with.
/// </remarks>
public class MaintenanceLauncherTests
{
    private const string Executable = "/opt/engram/bin/engram";
    private const string Home = "/home/someone/.engram";

    /// <summary>
    /// The redirection has to reach <c>/bin/sh</c> itself, not just the command group. Redirecting
    /// the group leaves the shell holding whatever it inherited for as long as the slowest job
    /// runs, which is the entire failure — and it looks correct, because every job's own output
    /// really is discarded.
    /// </summary>
    [Fact]
    public void TheShellsOwnDescriptorsAreReplaced_BeforeItRunsAnything()
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null);

        Assert.StartsWith(MaintenanceLauncher.Redirect, script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf(Executable, StringComparison.Ordinal)
                > script.IndexOf("exec ", StringComparison.Ordinal),
            $"the shell must replace its own descriptors before the first job: {script}");
    }

    /// <summary>
    /// All three, and stdin among them: a child left holding the hook's stdin can wake up reading
    /// a payload meant for the parent.
    /// </summary>
    [Theory]
    [InlineData("</dev/null")]
    [InlineData(">/dev/null")]
    [InlineData("2>&1")]
    public void EveryStandardDescriptorIsRedirected(string redirection) =>
        Assert.Contains(
            redirection,
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null),
            StringComparison.Ordinal);

    /// <summary>
    /// Each job carries its own already-cheap-when-idle guard, so the spawn stays unconditional
    /// and grows no policy of its own. Named here so removing one is a decision rather than a slip.
    /// </summary>
    [Theory]
    [InlineData("backup take --if-due")]
    [InlineData("queue compact --apply --if-large")]
    [InlineData("repair --apply --tokens")]
    [InlineData("sync import --if-new --apply")]
    [InlineData("sync export --if-due --apply")]
    public void EveryJobRunsWithItsOwnIdleGuard(string job) =>
        Assert.Contains(
            job,
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null),
            StringComparison.Ordinal);

    /// <summary>
    /// `[sync] enabled` defaults to false and creates nothing on its own — the ambient
    /// session-start script must omit all three sync lines entirely when it is off, or the very
    /// first automatic export writes a full unfiltered copy of the store to
    /// <c>&lt;home&gt;/sync/&lt;machine-id&gt;/1.jsonl</c> for anyone who has never touched [sync].
    /// </summary>
    [Theory]
    [InlineData("sync import")]
    [InlineData("sync export")]
    [InlineData("sync compact")]
    public void SyncJobsAreOmittedEntirely_WhenSyncIsNotEnabled(string job) =>
        Assert.DoesNotContain(
            job,
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: false, indexRoot: null),
            StringComparison.Ordinal);

    [Fact]
    public void TheIndexDrainIsAddedOnlyWhenThereIsARootToDrainInto()
    {
        Assert.DoesNotContain(
            "index --drain",
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, indexRoot: null),
            StringComparison.Ordinal);
        Assert.Contains(
            "index --drain-all --apply --auto '/repo'",
            MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, "/repo"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// --skip &lt;indexRoot&gt; must follow the drain-all job, not precede or replace it: the stamp
    /// that would exclude the invoked root from --freshen's own selection only lands once the
    /// drain-all job has run and completed (spec §5.4).
    /// </summary>
    [Fact]
    public void TheFreshenSelfHealFollowsTheDrainAllJob_ForSessionStartOnly()
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, "/repo");

        var drainAllAt = script.IndexOf("index --drain-all --apply --auto '/repo'", StringComparison.Ordinal);
        var freshenAt = script.IndexOf("index --freshen --apply --skip '/repo'", StringComparison.Ordinal);

        Assert.True(drainAllAt >= 0, $"expected the drain-all job: {script}");
        Assert.True(freshenAt > drainAllAt, $"expected --freshen to follow --drain-all: {script}");

        Assert.DoesNotContain(
            "--freshen",
            MaintenanceLauncher.BuildScript(
                Executable, Home, syncEnabled: true, "/repo", MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A path with a quote in it must not end the quoting and become further shell words.
    /// </summary>
    [Fact]
    public void APathCarryingAQuoteStaysOneWord()
    {
        var script = MaintenanceLauncher.BuildScript(Executable, "/home/o'brien/.engram", syncEnabled: true, null);

        Assert.Contains(@"'/home/o'\''brien/.engram'", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// An explicit enroll indexes even with auto_index_on_session_start = false: --auto gates
    /// ambient work and may not gate commanded work (spec §6.9, guard 16).
    /// </summary>
    [Fact]
    public void EnrollmentIndex_ContainsTheIndexJob_WithNoAutoAndNoFull()
    {
        var script = MaintenanceLauncher.BuildScript(
            Executable, Home, syncEnabled: true, "/repo", MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex);

        Assert.Contains("index --drain --apply '/repo'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--auto", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--full", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The enrollment job is the sole command spawned — none of the session-start housekeeping,
    /// which is ambient and gated by its own idle guards, has anything to do with an explicit
    /// enroll (spec §6.9).
    /// </summary>
    [Fact]
    public void EnrollmentIndex_RunsNoSessionStartHousekeeping()
    {
        var script = MaintenanceLauncher.BuildScript(
            Executable, Home, syncEnabled: true, "/repo", MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex);

        Assert.DoesNotContain("backup take", script, StringComparison.Ordinal);
        Assert.DoesNotContain("queue compact", script, StringComparison.Ordinal);
        Assert.DoesNotContain("repair", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sync", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one-descriptor-precedes-first-job property (D55/D56) must hold for both job lists —
    /// a second kind that skipped this would reopen the pipe-vs-file latency bug it fixed.
    /// </summary>
    [Theory]
    [InlineData(MaintenanceLauncher.MaintenanceJobs.SessionStart)]
    [InlineData(MaintenanceLauncher.MaintenanceJobs.EnrollmentIndex)]
    public void RedirectLeadsTheScript_ForEveryJobsValue(MaintenanceLauncher.MaintenanceJobs jobs)
    {
        var script = MaintenanceLauncher.BuildScript(Executable, Home, syncEnabled: true, "/repo", jobs);

        Assert.StartsWith(MaintenanceLauncher.Redirect, script, StringComparison.Ordinal);
    }
}
