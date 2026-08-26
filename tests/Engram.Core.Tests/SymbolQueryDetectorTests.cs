using Engram.Core;

namespace Engram.Core.Tests;

public class SymbolQueryDetectorTests
{
    // The shapes the nudge exists for: a name only code is spelled with.
    [Theory]
    [InlineData("ProcessFile")]
    [InlineData("processFile")]
    [InlineData("CodeIndexer")]
    [InlineData("process_file")]
    [InlineData("MAX_RETRY_COUNT")]
    [InlineData("CodeIndexer.ProcessFile")]
    [InlineData("engram::recall")]
    public void SymbolShapedQueries_Fire(string query) =>
        Assert.True(SymbolQueryDetector.LooksLikeSymbol(query));

    // The expensive failure mode. Grep and Bash carry most of a session's search traffic, so
    // every one of these must fall through untouched — a detector that fires here taxes ordinary
    // searching to correct a habit that only shows up on symbol lookups.
    [Theory]
    [InlineData("latency")]                   // a plain word is indistinguishable from prose
    [InlineData("TODO")]                      // all caps, no transition: a marker, not a symbol
    [InlineData("Todo")]                      // one leading capital is how English spells a word
    [InlineData("failed to open database")]   // a log line
    [InlineData("**/*.tsx")]                  // a glob path
    [InlineData("busy_timeout=0")]            // a config key with an operator
    [InlineData("HookCommand.cs")]            // a filename, not a qualified member
    [InlineData("foo|bar")]                   // an alternation
    [InlineData("^BEGIN IMMEDIATE")]          // an anchored regex
    [InlineData("db")]                        // too short to carry shape
    [InlineData("")]
    [InlineData(null)]
    public void TextSearches_StaySilent(string? query) =>
        Assert.False(SymbolQueryDetector.LooksLikeSymbol(query));

    [Theory]
    [InlineData("grep -rn ProcessFile src/", "ProcessFile")]
    [InlineData("grep -rn \"ProcessFile\" --include=\"*.cs\"", "ProcessFile")]
    [InlineData("rg CodeIndexer", "CodeIndexer")]
    [InlineData("/usr/bin/grep -n handleRequest app.js", "handleRequest")]
    [InlineData("ls -la && grep -rn ParseHeader .", "ParseHeader")]
    public void ShellSearches_YieldTheirPattern(string command, string expected) =>
        Assert.Equal(expected, SymbolQueryDetector.ExtractSearchPattern(command));

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("git status")]
    [InlineData("ls -la")]
    [InlineData("")]
    [InlineData(null)]
    public void NonSearchCommands_YieldNothing(string? command) =>
        Assert.Null(SymbolQueryDetector.ExtractSearchPattern(command));

    // A separate-value flag donates its value instead of the real pattern. That is a known limit
    // of skipping flags by their leading dash, and it is why the extractor is allowed to be this
    // simple: the value it hands back carries glob syntax, so the classifier rejects it and the
    // hook stays silent. The pair matters more than either half — it pins the direction the
    // heuristic fails in.
    [Fact]
    public void SeparateValueFlag_FailsTowardSilence()
    {
        var extracted = SymbolQueryDetector.ExtractSearchPattern("grep --include \"*.cs\" ProcessFile");

        Assert.Equal("*.cs", extracted);
        Assert.False(SymbolQueryDetector.LooksLikeSymbol(extracted));
    }

    // `git commit -m "add tail support"` is not a search, and neither is a message that happens
    // to name grep. Quoted text after a non-search command must not be mined for a pattern.
    [Fact]
    public void QuotedTextInANonSearchCommand_YieldsNothing() =>
        Assert.Null(SymbolQueryDetector.ExtractSearchPattern("git commit -m \"speed up ProcessFile\""));
}
