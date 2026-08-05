using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// UserPromptSubmit is the only place a fact the user states in passing can be caught,
/// so these drive the published binary rather than the JIT build — a classifier that
/// works under test and not in the shipped AOT binary would fail silently and forever.
/// </summary>
public class HookUserPromptTests
{
    // The case the feature exists for: no memory keyword anywhere in the sentence.
    [Fact]
    public void CapturesAPersonalStatementAndAsksTheModelToDateIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("I went to see a Spiderman movie last Saturday"),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var statement = Assert.Single(ReadCapturedStatements(home.Root));
        Assert.Equal("I went to see a Spiderman movie last Saturday", statement);

        // Bare stdout is discarded on some hook events; the envelope is what actually
        // reaches the model, and its hookEventName has to match the event that produced it.
        var output = JsonNode.Parse(stdout)!;
        var hookOutput = output["hookSpecificOutput"]!;
        Assert.Equal("UserPromptSubmit", hookOutput["hookEventName"]!.GetValue<string>());

        var context = hookOutput["additionalContext"]!.GetValue<string>();
        Assert.Contains("Spiderman", context);
        Assert.Contains("supersedes", context);
    }

    [Fact]
    public void SaysAndStoresNothingForAnOrdinaryWorkingPrompt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("run the tests and tell me what fails"), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Sentence granularity is a privacy property, not only a precision one: the working
    // half of a mixed message must never reach disk.
    [Fact]
    public void StoresOnlyTheStatedSentenceFromAMixedMessage()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        EngramProcess.RunWithStdin(
            home.Root,
            Payload("I moved to Seattle in March. Now fix the failing test and push it."),
            "hook", "user-prompt");

        var statement = Assert.Single(ReadCapturedStatements(home.Root));
        Assert.Equal("I moved to Seattle in March.", statement);
        Assert.DoesNotContain("failing test", statement);
    }

    // Same rule as every other hook: an uninitialised home means do nothing, quietly.
    [Fact]
    public void WritesNothingWhenTheHomeWasNeverInitialised()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome(initialize: false);

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I grew up in Fort Collins, Colorado"), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.False(Directory.Exists(Path.Combine(home.Root, "user-facts")));
    }

    private static string Payload(string prompt) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["session_id"] = "e2e-user-prompt",
            ["prompt"] = prompt,
        });

    private static IReadOnlyList<string> ReadCapturedStatements(string root)
    {
        var directory = Path.Combine(root, "user-facts");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, "*.json")
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["statement"]!.GetValue<string>())
            .ToList();
    }
}
