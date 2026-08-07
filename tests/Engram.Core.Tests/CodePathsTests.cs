using Engram.Core;

namespace Engram.Core.Tests;

public class CodePathsTests
{
    [Theory]
    [InlineData("Engram", "engram")]
    [InlineData("claude-code hooks", "claude-code-hooks")]
    [InlineData("Acme  API!!", "acme-api")]
    [InlineData("--Install--", "install")]
    [InlineData("§¶•", "unnamed")]
    public void Slug_CollapsesEverythingOutsideLowerAlnumToSingleDashes(string input, string expected) =>
        Assert.Equal(expected, CodePaths.Slug(input));

    [Fact]
    public void RepoRoot_NestsCodeInsideItsProject()
        // D27: a codebase is addressed inside its project, not beside it.
        => Assert.Equal("/projects/engram/code/engram", CodePaths.RepoRoot("engram", "engram"));

    [Fact]
    public void ForFile_PreservesCaseAndNormalizesSeparators()
    {
        var path = CodePaths.ForFile("/projects/p/code/r", @"src\Engram.Core\FactStore.cs");

        Assert.Equal("/projects/p/code/r/src/Engram.Core/FactStore.cs", path);
    }

    [Fact]
    public void Fragments_UseHashAndKeepSymbolNamesVerbatim()
    {
        Assert.Equal("/p/f.md#install/prereqs", CodePaths.ForSection("/p/f.md", "install/prereqs"));
        Assert.Equal("/p/F.cs#FactStore", CodePaths.ForSymbol("/p/F.cs", "FactStore"));
    }
}
