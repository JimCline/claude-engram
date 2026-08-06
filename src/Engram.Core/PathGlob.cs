namespace Engram.Core;

/// <summary>
/// One ignore pattern, in the subset of gitignore syntax people actually type.
/// </summary>
/// <remarks>
/// <para>Supports <c>*</c> (within a segment), <c>?</c> (one character within a segment), and
/// <c>**</c> (any number of segments). Two shapes are special-cased because they are the ones
/// that break naive translation:</para>
///
/// <list type="bullet">
/// <item><c>**/bin/**</c> must match <c>bin/x.dll</c> as well as <c>src/bin/x.dll</c>, and must
/// match the directory <c>bin</c> itself — that last one is what lets the walk prune at the
/// directory instead of testing every file underneath it.</item>
/// <item>A pattern with no slash at all matches the file name at any depth, which is gitignore's
/// rule: <c>*.min.js</c> means every minified file, not one in the root.</item>
/// </list>
///
/// <para><b>Matched by hand rather than by translating to a regular expression.</b> The regex
/// version was written first and passed exactly these tests. It cost <b>630,240 bytes</b> of
/// published binary — measured by publishing both — from linking
/// <c>System.Text.RegularExpressions</c> into a binary that had not needed it, and binary size is
/// a latency decision here because the <c>file-touched</c> budget's remaining headroom is all
/// process start. Measured differentially through one harness, the 248,720 bytes this work does
/// ship cost <b>+0.16 ms</b> at p50; the regex version was two and a half times that growth again,
/// for no behaviour the forty lines below do not have. Neither is measurable against the scan
/// itself, which is dominated by <c>stat</c> and <c>read</c>.</para>
///
/// <para>Matching is case-insensitive. git is case-sensitive by default, but this list is written
/// by a person and the cost of the two mistakes is not symmetric — a pattern that over-matches
/// excludes a file that can be recovered by editing the list, while one that under-matches puts
/// junk facts in the store that <c>compact</c> is forbidden to remove (D8).</para>
/// </remarks>
public sealed class PathGlob
{
    private readonly string[] segments;

    private PathGlob(string pattern, string[] segments, bool nameOnly)
    {
        Pattern = pattern;
        this.segments = segments;
        NameOnly = nameOnly;
    }

    public string Pattern { get; }

    /// <summary>True when the pattern applies to the file name rather than the whole path.</summary>
    public bool NameOnly { get; }

    public static PathGlob Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var trimmed = pattern.Trim().Replace('\\', '/');

        // A trailing slash means "this directory and everything under it".
        if (trimmed.EndsWith('/'))
        {
            trimmed += "**";
        }

        return new PathGlob(
            pattern,
            trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries),
            nameOnly: !trimmed.Contains('/', StringComparison.Ordinal));
    }

    public bool Matches(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var normalized = relativePath.Replace('\\', '/');
        if (NameOnly)
        {
            var slash = normalized.LastIndexOf('/');
            normalized = slash < 0 ? normalized : normalized[(slash + 1)..];
        }

        return MatchSegments(0, normalized.Split('/', StringSplitOptions.RemoveEmptyEntries), 0);
    }

    private bool MatchSegments(int patternIndex, string[] path, int pathIndex)
    {
        while (patternIndex < segments.Length)
        {
            if (segments[patternIndex] == "**")
            {
                // Zero segments first, which is what makes "**/bin/**" match a top-level "bin"
                // and the directory "bin" itself rather than only files inside it.
                for (var skip = pathIndex; skip <= path.Length; skip++)
                {
                    if (MatchSegments(patternIndex + 1, path, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pathIndex >= path.Length || !MatchSegment(segments[patternIndex], path[pathIndex]))
            {
                return false;
            }

            patternIndex++;
            pathIndex++;
        }

        return pathIndex == path.Length;
    }

    /// <summary>
    /// Matches one path segment against one pattern segment, where <c>*</c> stops at a separator.
    /// </summary>
    /// <remarks>
    /// Backtracks on the most recent <c>*</c> rather than recursing, so a pathological pattern
    /// costs quadratic time instead of exponential.
    /// </remarks>
    private static bool MatchSegment(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool Same(char a, char b) =>
        a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
