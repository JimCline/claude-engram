using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class IndexCommandTests
{
    // --auto is the detached maintenance child. Everything it declines, it must decline
    // as silent success: a hook's housekeeping choosing not to run is not an error, and
    // any output would go to a descriptor nobody reads.

    [Fact]
    public void Auto_OutsideAGitCheckout_DeclinesSilently_AndRegistersNothing()
    {
        using var sandbox = new SandboxHome();
        var directory = Path.Combine(sandbox.Home.Root, "just-a-directory");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "not a repo");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", directory, "--drain", "--apply", "--auto"],
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_registry;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Auto_WhenTheConfigDisablesIt_DeclinesSilently_EvenInARealCheckout()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "A real checkout.\n");
        if (!GitInit(repo))
        {
            return;
        }

        // The target being a genuine checkout is what makes this falsifiable: with the
        // config gate deleted, the run would proceed to index and register the repo.
        var config = File.ReadAllText(sandbox.Home.ConfigPath);
        Assert.Contains("auto_index_on_session_start = true", config, StringComparison.Ordinal);
        File.WriteAllText(
            sandbox.Home.ConfigPath,
            config.Replace("auto_index_on_session_start = true", "auto_index_on_session_start = false"));

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", repo, "--drain", "--apply", "--auto"],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_registry;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private static bool GitInit(string directory)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = System.Diagnostics.Process.Start(info);
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

    [Fact]
    public void WithoutAuto_ANonStoreHome_IsARealError()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "index", sandbox.Home.Root],
            new StringWriter(),
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("engram init", stderr.ToString(), StringComparison.Ordinal);
    }
}
