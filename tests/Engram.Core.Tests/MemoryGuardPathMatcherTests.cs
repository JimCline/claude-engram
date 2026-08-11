using Engram.Core;

namespace Engram.Core.Tests;

public class MemoryGuardPathMatcherTests
{
    private static readonly string ProjectsDir =
        Path.Combine(Path.GetTempPath(), "memory-guard-matcher-tests", ".claude", "projects");

    private static string PathUnder(params string[] segments) =>
        Path.Combine([ProjectsDir, .. segments]);

    [Fact]
    public void OrdinaryMemoryFile_Matches()
    {
        var path = PathUnder("my-project", "memory", "note.md");

        Assert.True(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }

    // The load-bearing exemption: dropping it must make this red.
    [Fact]
    public void IndexFile_IsExempt()
    {
        var path = PathUnder("my-project", "memory", "MEMORY.md");

        Assert.False(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }

    [Fact]
    public void LowercaseIndexFileName_Matches()
    {
        var path = PathUnder("my-project", "memory", "memory.md");

        Assert.True(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }

    [Fact]
    public void NonMarkdownExtension_DoesNotMatch()
    {
        var path = PathUnder("my-project", "memory", "note.txt");

        Assert.False(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }

    [Fact]
    public void NestedUnderMemoryDirectory_DoesNotMatch()
    {
        var path = PathUnder("my-project", "memory", "sub", "note.md");

        Assert.False(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }

    [Fact]
    public void NoMemoryDirectory_DoesNotMatch()
    {
        var path = PathUnder("note.md");

        Assert.False(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }

    [Fact]
    public void PathOutsideProjectsDirContainingMemorySegment_DoesNotMatch()
    {
        var outside = Path.Combine(Path.GetTempPath(), "memory-guard-matcher-tests-elsewhere", "memory", "note.md");

        Assert.False(MemoryGuardPathMatcher.IsFileBasedMemoryFile(outside, ProjectsDir));
    }

    [Fact]
    public void TwoSegmentsDeepBeforeMemoryDirectory_DoesNotMatch()
    {
        var path = PathUnder("a", "b", "memory", "note.md");

        Assert.False(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }

    // Proves the match is on path segments, not on the substring "memory" appearing anywhere in
    // the path text (adjudication 3's own example: a project slug that merely contains the word).
    [Fact]
    public void SlugContainingTheWordMemory_ButNoMemoryDirectory_DoesNotMatch()
    {
        var path = PathUnder("my-memory-project", "notes.md");

        Assert.False(MemoryGuardPathMatcher.IsFileBasedMemoryFile(path, ProjectsDir));
    }
}
