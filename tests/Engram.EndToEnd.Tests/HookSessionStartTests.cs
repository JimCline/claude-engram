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

    [Fact]
    public void SessionStart_NoStdinData_StillExitsZero_AndTelemetryRecordHasNonEmptySessionId()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
        var line = File.ReadAllLines(telemetryPath).Single();
        var sessionId = JsonDocument.Parse(line).RootElement.GetProperty("session_id").GetString();

        Assert.False(string.IsNullOrEmpty(sessionId));
    }

    [Fact]
    public void SessionStart_DifferentStdinSessionIds_ProduceTwoTelemetryRecordsWithThoseTwoIds()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var first = EngramProcess.RunWithStdin(home.Root, """{"session_id":"session-aaa"}""", "hook", "session-start");
        var second = EngramProcess.RunWithStdin(home.Root, """{"session_id":"session-bbb"}""", "hook", "session-start");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);

        var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
        var lines = File.ReadAllLines(telemetryPath);
        Assert.Equal(2, lines.Length);

        var sessionIds = lines
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("session_id").GetString())
            .ToList();

        Assert.Equal(["session-aaa", "session-bbb"], sessionIds);
    }
}
