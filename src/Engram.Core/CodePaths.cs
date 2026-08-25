using System.Text;

namespace Engram.Core;

/// <summary>
/// Builds subject paths for indexed code, per <c>docs/engram-path-grammar.md</c> v2.
/// </summary>
/// <remarks>
/// <para>The grammar document is the authority; this class only mechanizes it. Bump
/// <see cref="GrammarVersion"/> together with that document — the indexer stores the value
/// in <c>schema_meta</c> and forces a full re-index on mismatch, because two grammars
/// interleaved in one store make the prefix scan lie.</para>
///
/// <para>Symbol fragments are composed by <see cref="DeepTier.Fragments"/>, the one
/// implementation of v2's scope-chain and collision rules (D48); <see cref="ForSymbol"/>
/// takes the finished fragment and only attaches it.</para>
/// </remarks>
public static class CodePaths
{
    public const int GrammarVersion = 2;

    /// <summary>Root for a codebase: <c>/projects/&lt;project&gt;/code/&lt;repo&gt;</c> (D27).</summary>
    public static string RepoRoot(string projectSlug, string repoSlug) =>
        $"/projects/{projectSlug}/code/{repoSlug}";

    /// <summary>
    /// Repo-relative path appended verbatim — case preserved, separators normalized to
    /// <c>/</c>. It must round-trip to the file on disk, so it is the one segment that is
    /// never slugged.
    /// </summary>
    public static string ForFile(string repoPath, string relativePath) =>
        $"{repoPath}/{relativePath.Replace('\\', '/')}";

    public static string ForSection(string filePath, string sectionSlug) =>
        $"{filePath}#{sectionSlug}";

    public static string ForSymbol(string filePath, string symbolName) =>
        $"{filePath}#{symbolName}";

    public const string SymbolNameRoot = "/symbol-names";

    /// <summary>Address for a callee/module name as written. Not a location.</summary>
    /// <remarks>
    /// Not slugged — <see cref="Slug"/> lowercases, and case is part of the identity of every
    /// symbol name this indexes. <c>/</c> and <c>%</c> are percent-encoded because module names
    /// legitimately contain slashes (<c>./utils/foo</c>, <c>@scope/pkg</c>), which would
    /// otherwise manufacture path segments that do not exist.
    /// </remarks>
    public static string ForSymbolName(string name) =>
        $"{SymbolNameRoot}/{name.Replace("%", "%25").Replace("/", "%2F")}";

    /// <summary>Inverse of <see cref="ForSymbolName"/>; null if <paramref name="path"/> is not under the root.</summary>
    public static string? SymbolNameOf(string path)
    {
        var prefix = SymbolNameRoot + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return path[prefix.Length..].Replace("%2F", "/").Replace("%25", "%");
    }

    /// <summary>
    /// Last segment of a dotted callee (<c>os.path.join</c> → <c>join</c>) or a fragment
    /// (<c>Outer/Inner</c> → <c>Inner</c>) — the same question either way, so one function
    /// answers it (§6.5). Whichever of <c>.</c> or <c>/</c> occurs last wins; neither present
    /// returns the input unchanged.
    /// </summary>
    public static string LeafOf(string name)
    {
        var parenIndex = name.IndexOf('(');
        var head = parenIndex < 0 ? name : name[..parenIndex];
        var separator = Math.Max(head.LastIndexOf('.'), head.LastIndexOf('/'));
        return separator < 0 ? name : name[(separator + 1)..];
    }

    /// <summary>
    /// Inverse of <see cref="RepoRoot"/>: splits an entity path under a repo root into the
    /// repo root (first 5 <c>/</c>-segments) and the file-relative remainder, dropping any
    /// <c>#fragment</c>. Null if <paramref name="path"/> has fewer than 5 segments.
    /// </summary>
    public static (string RepoPath, string RelativePath)? SplitRepoPath(string path)
    {
        var withoutFragment = path[..(path.IndexOf('#') is var hash && hash >= 0 ? hash : path.Length)];
        var segments = withoutFragment.Split('/');
        if (segments.Length < 6)
        {
            return null;
        }

        var repoPath = string.Join('/', segments[..5]);
        var relativePath = string.Join('/', segments[5..]);
        return (repoPath, relativePath);
    }

    /// <summary>
    /// Lowercased, every run outside <c>[a-z0-9]</c> collapsed to one <c>-</c>, ends
    /// trimmed. Shared by project names, repo names, and doc-section headings so one rule
    /// answers every "how does display text become an address" question.
    /// </summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingDash = false;

        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(ch);
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.Length > 0 ? builder.ToString() : "unnamed";
    }
}
