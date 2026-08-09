namespace Engram.EndToEnd.Tests;

public class HookPreCompactTests
{
    // A tier-3 test on purpose: this is a stdout-format contract with an out-of-process
    // consumer (the summarizer), and CI green on the JIT build proves nothing about what the
    // published binary actually writes to that channel.
    [Fact]
    public void PreCompact_ExitsZero_EmitsDigestSentinelsOnBareStdout()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "pre-compact");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        Assert.Contains("<engram-digest v=\"1\">", stdout, StringComparison.Ordinal);
        Assert.Contains("</engram-digest>", stdout, StringComparison.Ordinal);

        // hookSpecificOutput is measured to be REJECTED on this event; a JSON envelope here
        // would mean the emitter regressed to the SessionStart/SubagentStart pattern.
        Assert.False(stdout.TrimStart().StartsWith('{'), "PreCompact stdout must be bare, not a JSON envelope");
    }
}
