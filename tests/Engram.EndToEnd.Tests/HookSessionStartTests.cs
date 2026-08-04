using System.Text.Json;

namespace Engram.EndToEnd.Tests;

public class HookSessionStartTests
{
    [Fact]
    public void SessionStart_ExitsZero_EmitsValidJsonContract_PrimerUnder300Tokens()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var doc = JsonDocument.Parse(stdout);
        var hookSpecificOutput = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hookSpecificOutput.GetProperty("hookEventName").GetString());

        var primer = hookSpecificOutput.GetProperty("additionalContext").GetString();
        Assert.False(string.IsNullOrWhiteSpace(primer));

        var estimatedTokens = (int)Math.Ceiling(primer!.Length / 3.6);
        Assert.True(estimatedTokens <= 300, $"primer was {estimatedTokens} estimated tokens, expected <= 300");
    }
}
