using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// The literal-token split, extracted from <see cref="RecallEngine"/> so the ranker and
/// <c>FactTokenIndex</c> share one implementation. These are the same cases
/// <see cref="RecallEngineTests"/> exercised indirectly through <c>Rank</c> before the
/// extraction — the behavior did not change, only where it lives.
/// </summary>
public class TokenizerTests
{
    [Fact]
    public void Tokenize_LowercasesAndSplitsOnNonAlphanumeric()
    {
        var tokens = Tokenizer.Tokenize("Zanzibar-Workflow, v2.1!");

        Assert.Equal(
            new HashSet<string> { "zanzibar", "workflow", "v2", "1" },
            tokens);
    }

    [Fact]
    public void Tokenize_OfEmptyText_ReturnsNoTokens()
    {
        Assert.Empty(Tokenizer.Tokenize(string.Empty));
    }

    [Fact]
    public void Tokenize_DeduplicatesRepeatedWords()
    {
        var tokens = Tokenizer.Tokenize("dvorak dvorak DVORAK");

        Assert.Equal(new HashSet<string> { "dvorak" }, tokens);
    }

    [Theory]
    [InlineData("and")]
    [InlineData("the")]
    [InlineData("it")]
    public void IsIndexable_RejectsStopwords(string stopword)
    {
        Assert.False(Tokenizer.IsIndexable(stopword));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("to")]
    public void IsIndexable_RejectsTokensShorterThanThree(string term)
    {
        Assert.False(Tokenizer.IsIndexable(term));
    }

    [Fact]
    public void IsIndexable_AcceptsAnOrdinaryWord()
    {
        Assert.True(Tokenizer.IsIndexable("zanzibar"));
    }
}
