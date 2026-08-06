using Engram.Core;

namespace Engram.Core.Tests;

public class PathGlobTests
{
    // The case naive translation gets wrong: "**/bin/**" becomes ".*/bin/.*", which needs a
    // leading directory and so misses the top-level "bin" the pattern was written for.
    [Theory]
    [InlineData("**/bin/**", "bin/app.dll", true)]
    [InlineData("**/bin/**", "src/bin/app.dll", true)]
    [InlineData("**/bin/**", "src/a/b/bin/Debug/app.dll", true)]
    [InlineData("**/bin/**", "src/binding/app.cs", false)]
    [InlineData("**/bin/**", "src/cabin/app.cs", false)]
    public void DoubleStar_MatchesAtEveryDepthIncludingNone(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));
    }

    [Theory]
    [InlineData("**/node_modules/**", "node_modules/react/index.js", true)]
    [InlineData("**/node_modules/**", "web/node_modules/react/index.js", true)]
    [InlineData("**/.git/**", ".git/HEAD", true)]
    [InlineData("**/obj/**", "src/Engram.Core/obj/project.assets.json", true)]
    public void TheShippedDefaults_MatchWhatTheyName(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));
    }

    // gitignore's rule, and the one people assume: a pattern with no slash is about the file
    // name, at any depth.
    [Theory]
    [InlineData("*.min.js", "app.min.js", true)]
    [InlineData("*.min.js", "web/static/app.min.js", true)]
    [InlineData("*.min.js", "app.js", false)]
    [InlineData("package-lock.json", "web/package-lock.json", true)]
    public void APatternWithNoSlash_MatchesTheFileNameAtAnyDepth(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));
    }

    [Theory]
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/deep/Program.cs", false)]
    public void ASingleStar_StopsAtASeparator(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));
    }

    [Theory]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file12.txt", false)]
    public void AQuestionMark_MatchesOneCharacter(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));
    }

    // The directory itself matches, not only what is under it — that is what lets the walk prune
    // at the directory instead of testing every file inside it.
    [Theory]
    [InlineData("target/", "target/debug/app", true)]
    [InlineData("target/", "target", true)]
    [InlineData("target/", "src/target/debug/app", false)]
    public void ATrailingSlash_MeansTheDirectoryAndItsContents(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));
    }

    [Theory]
    [InlineData("**/bin/**", "bin", true)]
    [InlineData("**/bin/**", "src/bin", true)]
    public void ADoubleStarSuffix_AlsoMatchesTheDirectoryItself(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Parse(pattern).Matches(path));
    }

    [Fact]
    public void APatternIsNotARegex()
    {
        Assert.False(PathGlob.Parse("a+b.txt").Matches("aab.txt"));
        Assert.True(PathGlob.Parse("a+b.txt").Matches("a+b.txt"));
    }

    // A dot in a pattern must not match any character, or "*.cs" would also exclude "acs".
    [Fact]
    public void ADot_IsLiteral()
    {
        Assert.False(PathGlob.Parse("*.cs").Matches("axcs"));
    }

    [Fact]
    public void WindowsSeparators_AreNormalisedOnBothSides()
    {
        Assert.True(PathGlob.Parse(@"**\bin\**").Matches(@"src\bin\app.dll"));
        Assert.True(PathGlob.Parse("**/bin/**").Matches(@"src\bin\app.dll"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(PathGlob.Parse("**/bin/**").Matches("src/Bin/app.dll"));
    }
}
