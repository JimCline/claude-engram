using System.Text.Json;
using Engram.Cli;

namespace Engram.Integration.Tests;

public class HookCommandTests
{
    [Fact]
    public void SessionStart_ExitsZero_EmitsSessionStartJson()
    {
        using var sandbox = new SandboxHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var doc = JsonDocument.Parse(stdout.ToString());
        var hookSpecificOutput = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hookSpecificOutput.GetProperty("hookEventName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(hookSpecificOutput.GetProperty("additionalContext").GetString()));
    }

    [Fact]
    public void SessionStart_MissingHomeDirectory_ExitsZeroAndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        Directory.Delete(sandbox.Home.Root, recursive: true);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void SessionStart_InitialisedHome_RecordsTelemetry()
    {
        using var sandbox = new SandboxHome();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        var telemetryPath = Path.Combine(sandbox.Home.Root, "telemetry.jsonl");
        Assert.True(File.Exists(telemetryPath));
        Assert.Contains("\"kind\":\"session-start\"", File.ReadAllText(telemetryPath));
    }

    [Fact]
    public void UninitialisedHome_AllHookEvents_ExitZeroSilentlyAndCreateNoFiles()
    {
        using var sandbox = new SandboxHome(initialize: false);

        foreach (var eventName in new[] { "session-start", "file-touched", "pre-compact" })
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", eventName], stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }

        Assert.Empty(Directory.GetFileSystemEntries(sandbox.Home.Root));
    }

    [Fact]
    public void PreCompact_ExitsZero_WritesNothingToStdout()
    {
        using var sandbox = new SandboxHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "pre-compact"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void FileTouched_ExitsZero_CreatesOneSpoolFilePerInvocation()
    {
        using var sandbox = new SandboxHome();

        for (var i = 0; i < 5; i++)
        {
            var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "file-touched"], new StringWriter(), new StringWriter());
            Assert.Equal(0, exitCode);
        }

        var spoolFiles = Directory.GetFiles(sandbox.Home.QueueDir);
        Assert.Equal(5, spoolFiles.Length);
    }

    [Fact(Timeout = 300_000)]
    public async Task FileTouched_FiftyConcurrentInProcessCalls_ProduceFiftyDistinctSpoolFiles()
    {
        using var sandbox = new SandboxHome();

        var runs = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => CliApp.Run(["--home", sandbox.Home.Root, "hook", "file-touched"], new StringWriter(), new StringWriter())));
        var exitCodes = await Task.WhenAll(runs);

        Assert.All(exitCodes, code => Assert.Equal(0, code));

        var spoolFiles = Directory.GetFiles(sandbox.Home.QueueDir);
        Assert.Equal(50, spoolFiles.Length);

        var distinctNames = spoolFiles.Select(Path.GetFileName).Distinct().Count();
        Assert.Equal(50, distinctNames);
    }

    [Fact]
    public void FileTouched_MissingHomeDirectory_DoesNotThrow()
    {
        using var sandbox = new SandboxHome();
        Directory.Delete(sandbox.Home.Root, recursive: true);

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "file-touched"], new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
    }
}
