using System.Diagnostics;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class RepoCommandTests
{
    [Fact]
    public void Enroll_ARequestedPathWithNoEnclosingCheckout_Refuses()
    {
        using var sandbox = new SandboxHome();
        var bare = Path.Combine(sandbox.Home.Root, "not-a-checkout");
        Directory.CreateDirectory(bare);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["enroll", bare], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("not inside a git checkout", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_enrollment;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Enroll_ARequestedPathInsideAGitCheckout_RecordsEnrolledAndReportsSuccess()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["enroll", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Enrolled", stdout.ToString(), StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(RepoEnrollmentState.Enrolled, row.State);
    }

    [Fact]
    public void Decline_ARequestedPathInsideAGitCheckout_RecordsDeclined()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["decline", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Declined", stdout.ToString(), StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(RepoEnrollmentState.Declined, row.State);
    }

    [Fact]
    public void Later_ARequestedPathInsideAGitCheckout_RecordsDeferred()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["later", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Deferred", stdout.ToString(), StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(RepoEnrollmentState.Deferred, row.State);
    }

    [Fact]
    public void List_AnEnrolledRepo_PrintsItsStateAndRoot()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(root), root, DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["list"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("state: enrolled", output, StringComparison.Ordinal);
        Assert.Contains(root, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// D49: a dry run must never mutate. Every existing Reset coverage drove the --apply path
    /// through ApplyDecision directly; nothing exercised the branch that returns before it.
    /// </summary>
    [Fact]
    public void Reset_WithoutApply_DoesNotMutateAnExistingDecision()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, CodeIndexer.ResolveIdentity(root), root, DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        var exitCode = RepoCommand.Run(sandbox.Home.Root, ["reset", root], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run only", stdout.ToString(), StringComparison.Ordinal);

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(check));
        Assert.Equal(RepoEnrollmentState.Enrolled, row.State);
    }

    private static bool GitInit(string directory)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
