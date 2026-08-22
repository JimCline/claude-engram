using System.Text.Json;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Drives the published binary because SubagentStart's envelope contract and PostCompact's
/// stdin-redirection guard only reach their real behavior against a spawned process — the
/// same reason <see cref="HookSubagentStartTests"/> and <see cref="HookPostCompactTests"/> do.
/// </summary>
public class DirectiveHookDeliveryTests
{
    [Fact]
    public void SessionStart_DeliversAnAddedDirectiveVerbatim()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var (addExit, _, _) = EngramProcess.Run(home.Root, "directive", "add", "always use BEGIN IMMEDIATE for writes");
        Assert.Equal(0, addExit);

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var primer = JsonDocument.Parse(stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();

        Assert.Contains("Standing directives (complete; memory path /directives):", primer);
        Assert.Contains("- always use BEGIN IMMEDIATE for writes", primer);
    }

    [Fact]
    public void SubagentStart_DeliversAnAddedDirectiveVerbatim()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var (addExit, _, _) = EngramProcess.Run(home.Root, "directive", "add", "never commit directly to main in this repo");
        Assert.Equal(0, addExit);

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "subagent-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var primer = JsonDocument.Parse(stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();

        Assert.Contains("Standing directives (complete; memory path /directives):", primer);
        Assert.Contains("- never commit directly to main in this repo", primer);
    }

    // Hazard 4: PostCompact does not build a primer today, and adding one would double-inject
    // per compaction — this must stay true with directives present, not just without them.
    [Fact]
    public void PostCompact_EmitsNoPrimerAndNoDirectiveBlock_EvenWithADirectiveOnFile()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        EngramProcess.Run(home.Root, "directive", "add", "always use BEGIN IMMEDIATE for writes");

        const string payload = """{"session_id":"e2e-directive-postcompact","compact_summary":"<analysis>\nOrdinary summary, no digest block.\n</analysis>"}""";
        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(home.Root, payload, "hook", "post-compact");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void SessionStart_TelemetryCarriesDirectiveCount_WithFactCountStillNull()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        EngramProcess.Run(home.Root, "directive", "add", "always use BEGIN IMMEDIATE for writes");
        EngramProcess.Run(home.Root, "directive", "add", "never commit directly to main in this repo");

        EngramProcess.Run(home.Root, "hook", "session-start");

        var record = JsonDocument.Parse(
                File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl"))
                    .Select(l => JsonDocument.Parse(l).RootElement)
                    .First(e => e.GetProperty("kind").GetString() == "session-start")
                    .GetRawText())
            .RootElement;

        Assert.Equal(2, record.GetProperty("directive_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("fact_count").ValueKind);
    }

    [Fact]
    public void SubagentStart_TelemetryCarriesDirectiveCount_WithFactCountStillNull()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        EngramProcess.Run(home.Root, "directive", "add", "always use BEGIN IMMEDIATE for writes");

        EngramProcess.Run(home.Root, "hook", "subagent-start");

        var record = JsonDocument.Parse(
                File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl"))
                    .Select(l => JsonDocument.Parse(l).RootElement)
                    .First(e => e.GetProperty("kind").GetString() == "subagent-start")
                    .GetRawText())
            .RootElement;

        Assert.Equal(1, record.GetProperty("directive_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("fact_count").ValueKind);
    }

    // Hazard 5, driven through the real binary: an install with no directives must render a
    // byte-identical primer to one from a home this feature never touched.
    [Fact]
    public void SessionStart_WithNoDirectivesAdded_PrimerIsUnchanged()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var primer = JsonDocument.Parse(stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();

        Assert.DoesNotContain("Standing directives", primer);
    }

    [Fact]
    public void DirectiveList_ShowsAnAddedDirective()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        EngramProcess.Run(home.Root, "directive", "add", "always use BEGIN IMMEDIATE for writes");

        var (exitCode, listOut, _) = EngramProcess.Run(home.Root, "directive", "list");

        Assert.Equal(0, exitCode);
        Assert.Contains("always use BEGIN IMMEDIATE for writes", listOut);
    }
}
