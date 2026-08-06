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

    // A store that cannot be read must produce silence, not the built-in corpus. Falling
    // back to CannedFacts would restore the divergence that moving the primer onto the
    // store removed, and would do it at the worst possible moment — telling someone who
    // forgot something that it is still remembered.
    //
    // This has to run out of process. Microsoft.Data.Sqlite pools connections, so the same
    // check inside the test host reads the corrupted file from a pooled connection's page
    // cache and reports the full corpus. A hook is a fresh process with an empty pool,
    // which is the situation being tested.
    [Fact]
    public void SessionStart_UnreadableDatabase_AnnouncesNothingRatherThanTheBuiltInCorpus()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var databasePath = Path.Combine(home.Root, "engram.db");
        Assert.True(File.Exists(databasePath), "the test home should have been initialised with a database");

        foreach (var sidecar in Directory.GetFiles(home.Root, "engram.db-*"))
        {
            File.Delete(sidecar);
        }

        File.WriteAllText(databasePath, "this is not a SQLite database");

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain("Memory holds", stdout, StringComparison.Ordinal);
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

    // The primer reaches every session whether or not the model calls a tool, so a record that
    // omits it makes `recall` the only visible read path — and recall is opt-in. That is the
    // measurement D6's gate on M3 and D18's on M4 both need, and neither could be read off the
    // 54 session-start records this instance had accumulated with every memory field null.
    [Fact]
    public void SessionStart_RecordsWhatThePrimerDelivered_NotMerelyThatOneStarted()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "hook", "session-start");
        Assert.Equal(0, exitCode);

        var primer = JsonDocument.Parse(stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();
        Assert.False(string.IsNullOrWhiteSpace(primer));

        var record = JsonDocument.Parse(
            File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl")).Single()).RootElement;

        var longTerm = record.GetProperty("long_term_fact_count");
        Assert.NotEqual(JsonValueKind.Null, longTerm.ValueKind);
        Assert.True(longTerm.GetInt32() > 0, "the seeded home holds facts, so the primer reported some");

        var tokens = record.GetProperty("tokens_returned");
        Assert.NotEqual(JsonValueKind.Null, tokens.ValueKind);
        Assert.InRange(tokens.GetInt32(), 1, 300);
    }

    // fact_count means "facts returned to the model" on a recall record. A primer returns a count
    // line and up to two example bodies, which is not that — and filling the field with something
    // almost-right is how the probe came to subtract two disjoint session counts from each other
    // (D43). Null is the honest value and it has to stay null.
    [Fact]
    public void SessionStart_LeavesFactCountNull_BecauseAPrimerReturnsNoFacts()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        Assert.Equal(0, EngramProcess.Run(home.Root, "hook", "session-start").ExitCode);

        var record = JsonDocument.Parse(
            File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl")).Single()).RootElement;

        Assert.Equal(JsonValueKind.Null, record.GetProperty("fact_count").ValueKind);
    }
}
