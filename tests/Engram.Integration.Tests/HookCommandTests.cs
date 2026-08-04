using System.Text.Json;
using Engram.Cli;

namespace Engram.Integration.Tests;

public class HookCommandTests
{
    [Fact]
    public void SessionStart_ExitsZero_EmitsSessionStartJson_AndWritesSessionCurrent()
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

        Assert.True(File.Exists(Path.Combine(sandbox.Home.Root, "session-current")));
    }

    [Fact]
    public void SessionStart_MissingHomeDirectory_StillExitsZeroAndEmitsJson()
    {
        using var sandbox = new SandboxHome();
        Directory.Delete(sandbox.Home.Root, recursive: true);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], stdout, stderr);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("SessionStart", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
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
    public void FileTouched_ExitsZero_AppendsOneLinePerInvocation()
    {
        using var sandbox = new SandboxHome();

        for (var i = 0; i < 5; i++)
        {
            var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "file-touched"], new StringWriter(), new StringWriter());
            Assert.Equal(0, exitCode);
        }

        var spoolFile = Directory.GetFiles(sandbox.Home.QueueDir).Single();
        var lines = File.ReadAllLines(spoolFile);
        Assert.Equal(5, lines.Length);
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
