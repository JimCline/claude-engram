namespace Engram.Core;

/// <summary>
/// Whether a write targets Claude Code's file-based auto-memory directory
/// (<c>&lt;projects&gt;/&lt;slug&gt;/memory/*.md</c>) — the thing the <c>memory-guard</c> hook
/// nudges away from.
/// </summary>
/// <remarks>
/// Segment comparison only, never substring: a project literally named <c>memory</c>, or a path
/// that merely contains <c>/memory/</c> somewhere else in its tree, must not decide this on the
/// strength of the raw text. Single level only — exactly one project segment between the
/// projects directory and <c>memory/</c> — matches the origin glob, and nothing deeper exists in
/// practice.
/// </remarks>
public static class MemoryGuardPathMatcher
{
    private const string MemoryDirectoryName = "memory";

    /// <summary>The index file — pointers, never memory content — exempt by exact ordinal match.</summary>
    private const string IndexFileName = "MEMORY.md";

    public static bool IsFileBasedMemoryFile(string filePath, string claudeProjectsDir)
    {
        if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".md", StringComparison.Ordinal))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);
        var fullProjectsDir = Path.GetFullPath(claudeProjectsDir);

        var relative = Path.GetRelativePath(fullProjectsDir, fullPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length != 3 || !string.Equals(segments[1], MemoryDirectoryName, StringComparison.Ordinal))
        {
            return false;
        }

        return !string.Equals(segments[2], IndexFileName, StringComparison.Ordinal);
    }
}
