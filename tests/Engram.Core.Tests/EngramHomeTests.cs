namespace Engram.Core.Tests;

public class EngramHomeTests
{
    private const string UserProfile = "/Users/testuser";
    private const string CurrentDirectory = "/current/dir";

    private static IReadOnlyDictionary<string, string?> Env(string? engramHome = null) =>
        new Dictionary<string, string?> { ["ENGRAM_HOME"] = engramHome };

    [Fact]
    public void ExplicitPath_WinsOverEnvironmentAndDefault()
    {
        var home = EngramHome.Resolve("/explicit/path", Env("/env/path"), UserProfile, CurrentDirectory);

        Assert.Equal("/explicit/path", home.Root);
    }

    [Fact]
    public void EnvironmentVariable_WinsOverDefault()
    {
        var home = EngramHome.Resolve(null, Env("/env/path"), UserProfile, CurrentDirectory);

        Assert.Equal("/env/path", home.Root);
    }

    [Fact]
    public void Default_IsUserProfileDotEngram()
    {
        var home = EngramHome.Resolve(null, Env(), UserProfile, CurrentDirectory);

        Assert.Equal("/Users/testuser/.engram", home.Root);
    }

    [Fact]
    public void EmptyOrWhitespaceEnvironmentVariable_FallsThroughToDefault()
    {
        var home = EngramHome.Resolve(null, Env("   "), UserProfile, CurrentDirectory);

        Assert.Equal("/Users/testuser/.engram", home.Root);
    }

    [Fact]
    public void LeadingTilde_ExpandsAgainstUserProfileDirectory()
    {
        var home = EngramHome.Resolve("~/foo", Env(), UserProfile, CurrentDirectory);

        Assert.Equal("/Users/testuser/foo", home.Root);
    }

    [Fact]
    public void LeadingTildeBackslash_ExpandsAgainstUserProfileDirectory()
    {
        var home = EngramHome.Resolve("~\\foo", Env(), UserProfile, CurrentDirectory);

        Assert.Equal("/Users/testuser/foo", home.Root);
    }

    [Fact]
    public void RelativePath_IsMadeAbsoluteAgainstCurrentDirectory()
    {
        var home = EngramHome.Resolve("relative/dir", Env(), UserProfile, CurrentDirectory);

        Assert.Equal("/current/dir/relative/dir", home.Root);
    }

    [Fact]
    public void PathContainingDotDot_IsNormalized()
    {
        var home = EngramHome.Resolve("/a/b/../c", Env(), UserProfile, CurrentDirectory);

        Assert.Equal("/a/c", home.Root);
    }

    [Fact]
    public void TrailingSlash_IsStripped()
    {
        var home = EngramHome.Resolve("/explicit/path/", Env(), UserProfile, CurrentDirectory);

        Assert.Equal("/explicit/path", home.Root);
    }

    [Fact]
    public void FilesystemRoot_IsPreservedRatherThanTrimmedToEmpty()
    {
        var home = EngramHome.Resolve(null, Env("/"), UserProfile, CurrentDirectory);

        Assert.NotEmpty(home.Root);
        Assert.True(Path.IsPathRooted(home.Root));
        Assert.True(Path.IsPathRooted(home.DatabasePath));
        Assert.EndsWith("engram.db", home.DatabasePath);
    }

    [Fact]
    public void DerivedPaths_AreUnderRootWithExpectedFilenames()
    {
        var home = EngramHome.Resolve("/explicit/path", Env(), UserProfile, CurrentDirectory);

        AssertUnderRootWithFileName(home.Root, home.DatabasePath, "engram.db");
        AssertUnderRootWithFileName(home.Root, home.ConfigPath, "config.toml");
        AssertUnderRootWithFileName(home.Root, home.LogPath, "engram.log");
        AssertUnderRootWithFileName(home.Root, home.ModelsDir, "models");
        AssertUnderRootWithFileName(home.Root, home.QueueDir, "queue");
        AssertUnderRootWithFileName(home.Root, home.ReportDir, "report");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespaceUserProfileDirectory_ThrowsArgumentException(string? userProfileDirectory)
    {
        Assert.Throws<ArgumentException>(() => EngramHome.Resolve(null, Env(), userProfileDirectory!, CurrentDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespaceCurrentDirectory_ThrowsArgumentException(string? currentDirectory)
    {
        Assert.Throws<ArgumentException>(() => EngramHome.Resolve(null, Env(), UserProfile, currentDirectory!));
    }

    [Fact]
    public void NullEnvironment_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => EngramHome.Resolve(null, null!, UserProfile, CurrentDirectory));
    }

    [Fact]
    public void Resolve_CreatesNothingOnDisk()
    {
        var uniqueRoot = Path.Combine(Path.GetTempPath(), "engram-resolve-test-" + Guid.NewGuid().ToString("N"));

        var home = EngramHome.Resolve(uniqueRoot, Env(), UserProfile, CurrentDirectory);

        Assert.False(Directory.Exists(home.Root));
        Assert.False(File.Exists(home.DatabasePath));
    }

    private static void AssertUnderRootWithFileName(string root, string derivedPath, string expectedFileName)
    {
        Assert.Equal(root, Path.GetDirectoryName(derivedPath));
        Assert.Equal(expectedFileName, Path.GetFileName(derivedPath));
    }
}
