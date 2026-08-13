namespace Engram.EndToEnd.Tests;

public class RepoCommandTests
{
    /// <summary>
    /// auto_index_on_session_start answers "may Engram index on its own", not "must Engram obey
    /// an instruction" — an explicit `repo enroll` is a command, not ambient work, so the setting
    /// that suppresses the session-start scan must not silence this too (spec §6.9, guard 16).
    /// Falsify by restoring --auto on the enrollment job: the index never runs, "1 file(s) indexed"
    /// never appears, and the promise at RepoCommand.cs's enroll message becomes a lie.
    /// </summary>
    [Fact]
    public void Enroll_IndexesEvenWithAutoIndexOnSessionStartDisabled()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var configPath = Path.Combine(home.Root, "config.toml");
        var config = File.ReadAllText(configPath);
        Assert.Contains("auto_index_on_session_start = true", config, StringComparison.Ordinal);
        File.WriteAllText(configPath, config.Replace(
            "auto_index_on_session_start = true", "auto_index_on_session_start = false"));

        var repo = Path.Combine(home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Program.cs"), "class Program {}\n");
        if (!GitInit(repo))
        {
            return;
        }

        var (enrollExit, enrollOut, _) = EngramProcess.Run(home.Root, "repo", "enroll", repo);
        Assert.Equal(0, enrollExit);
        Assert.Contains("first index is running in the background", enrollOut, StringComparison.Ordinal);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var listOutput = string.Empty;
        while (!listOutput.Contains("file(s) indexed", StringComparison.Ordinal) || listOutput.Contains("0 file(s) indexed", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(200);
            (_, listOutput, _) = EngramProcess.Run(home.Root, "repo", "list");
        }

        Assert.Contains("1 file(s) indexed", listOutput, StringComparison.Ordinal);
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
}
