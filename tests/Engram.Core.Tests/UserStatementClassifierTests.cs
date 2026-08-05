using Engram.Core;

namespace Engram.Core.Tests;

public class UserStatementClassifierTests
{
    // The case the whole feature exists for. It carries no memory keyword, no "remember",
    // no "always" — matching on vocabulary would never see it. What marks it is that it is
    // a first-person declarative.
    [Fact]
    public void CapturesAPersonalStatementWithNoMemoryKeywordInIt()
    {
        var result = UserStatementClassifier.Classify("I went to see a Spiderman movie last Saturday");

        var candidate = Assert.Single(result);
        Assert.Equal(UserFactKind.PersonalStatement, candidate.Kind);
        Assert.Equal("I went to see a Spiderman movie last Saturday", candidate.Text);
    }

    [Theory]
    [InlineData("I grew up in Fort Collins, Colorado")]
    [InlineData("My daughter starts high school in the fall")]
    [InlineData("I prefer spaces over tabs in every language")]
    [InlineData("We use Linear for issue tracking at work")]
    [InlineData("I've been playing bass for about twelve years")]
    public void CapturesFirstPersonDeclaratives(string prompt)
    {
        var candidate = Assert.Single(UserStatementClassifier.Classify(prompt));
        Assert.Equal(UserFactKind.PersonalStatement, candidate.Kind);
    }

    [Theory]
    [InlineData("always use BEGIN IMMEDIATE for writes")]
    [InlineData("remember that the staging box reboots nightly")]
    [InlineData("from now on put integration tests in tier 2")]
    [InlineData("never commit directly to main in this repo")]
    public void CapturesStandingInstructions(string prompt)
    {
        var candidate = Assert.Single(UserStatementClassifier.Classify(prompt));
        Assert.Equal(UserFactKind.Directive, candidate.Kind);
    }

    // Everything a prompt is usually made of instead. Storing any of these would fill
    // memory with the transcript of a working session, which is what makes recall useless.
    [Theory]
    [InlineData("run the tests and show me what fails")]
    [InlineData("why is this test failing?")]
    [InlineData("can you refactor the resolver for me")]
    [InlineData("please add a guard for the empty case")]
    [InlineData("what does the probe command actually measure")]
    [InlineData("I need you to fix the build before anything else")]
    [InlineData("let's move on to the next task")]
    [InlineData("I'll take a look at that in a minute")]
    [InlineData("I'm going to rerun the suite now")]
    [InlineData("you should use a different approach here")]
    public void IgnoresQuestionsOrdersAndIntent(string prompt)
    {
        Assert.Empty(UserStatementClassifier.Classify(prompt));
    }

    // Instructions wearing a first-person opening. These are the only ones the request
    // filter actually decides — anything starting "please" or "show me" is already
    // rejected for not being first-person, so those alternatives never change an outcome.
    [Theory]
    [InlineData("I think you should drop the sidecar entirely")]
    [InlineData("I need you to fix the build before anything else")]
    [InlineData("I'd like you to rerun the whole suite")]
    [InlineData("I want you to check the resolver again")]
    public void IgnoresInstructionsThatOpenInFirstPerson(string prompt)
    {
        Assert.Empty(UserStatementClassifier.Classify(prompt));
    }

    // A question can be first-person too, and those are the ones the person filter alone
    // cannot stop: "I should use spaces here, right?" opens exactly like a statement of
    // preference and means the opposite of one.
    [Theory]
    [InlineData("I should use spaces here, right?")]
    [InlineData("My config looks wrong, doesn't it?")]
    [InlineData("I wonder whether we should drop the sidecar?")]
    public void IgnoresFirstPersonQuestions(string prompt)
    {
        Assert.Empty(UserStatementClassifier.Classify(prompt));
    }

    [Theory]
    [InlineData("I agree")]
    [InlineData("my bad")]
    [InlineData("I see")]
    public void IgnoresFragmentsTooShortToBeAFact(string prompt)
    {
        Assert.Empty(UserStatementClassifier.Classify(prompt));
    }

    // Classification is per sentence, so a message that mixes a fact with an instruction
    // contributes the fact and leaves the rest of the message off disk entirely.
    [Fact]
    public void TakesOnlyTheStatementFromAMixedMessage()
    {
        var result = UserStatementClassifier.Classify(
            "I moved to Seattle in March. Now fix the failing test and push it.");

        var candidate = Assert.Single(result);
        Assert.Equal("I moved to Seattle in March.", candidate.Text);
    }

    [Fact]
    public void TakesSeveralStatementsFromOneMessage()
    {
        var result = UserStatementClassifier.Classify(
            "I use a Dvorak keyboard. My main machine is an M3 Max. What time is it?");

        Assert.Equal(2, result.Count);
        Assert.All(result, c => Assert.Equal(UserFactKind.PersonalStatement, c.Kind));
    }

    // A slash command is addressed to the harness. "/engram:remember I like X" would
    // otherwise be captured twice — once by the hook and once by the command itself.
    [Fact]
    public void IgnoresSlashCommands()
    {
        Assert.Empty(UserStatementClassifier.Classify("/engram:remember I prefer spaces over tabs"));
    }

    // Pasted logs and diffs are full of lines that look first-person and are never
    // something the user is asserting.
    [Fact]
    public void IgnoresPastedCodeBlocks()
    {
        Assert.Empty(UserStatementClassifier.Classify("```\nI am a log line that mentions my config\n```"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IgnoresEmptyInput(string? prompt)
    {
        Assert.Empty(UserStatementClassifier.Classify(prompt));
    }
}
