namespace Engram.Core;

/// <summary>
/// One symbol as a deep tier saw it. <paramref name="Scope"/> is the pre-joined chain of
/// containing type names (<c>Outer/Inner</c>) or null at top level; <paramref name="Params"/>
/// is the parameter list exactly as written, parentheses included, or null for anything
/// that has none. Both are raw material: <see cref="DeepTier.Fragments"/> is what turns
/// them into an address, so neither tier ever composes one.
/// </summary>
public sealed record DeepSymbol(
    string Name,
    string Kind,
    string Declaration,
    string? Doc,
    string? Scope = null,
    string? Params = null);

/// <summary>One deeper-tier view of one file: what it saw, or why it could not look.</summary>
public sealed record DeepAnalysis(
    string Path,
    IReadOnlyList<DeepSymbol> Symbols,
    IReadOnlyList<string> Imports,
    string? Error);

/// <summary>
/// How any deeper tier's view lands on tier 0's (D24): one implementation, shared by the
/// tree-sitter extractor and the Roslyn sidecar, because the invariants below are about
/// cross-tier supersession and two copies of them would drift.
/// </summary>
public static class DeepTier
{
    /// <summary>
    /// The one implementation of grammar v2's fragment rules (D48). Scope and name join
    /// with <c>/</c>; when several symbols in one file share that base, each appends its
    /// parameter list — whitespace runs collapsed to a single space, otherwise as written,
    /// which is why two tiers reading the same bytes cannot spell one symbol two ways. The
    /// suffix appears only on collision, so a unique name keeps its stable bare form.
    /// </summary>
    public static IReadOnlyList<(string Fragment, DeepSymbol Symbol)> Fragments(
        IReadOnlyList<DeepSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var bases = new List<(string Base, DeepSymbol Symbol)>(symbols.Count);
        foreach (var symbol in symbols)
        {
            if (symbol.Name.Length == 0)
            {
                continue;
            }

            bases.Add((
                symbol.Scope is { Length: > 0 } scope ? scope + "/" + symbol.Name : symbol.Name,
                symbol));
        }

        var collisions = bases
            .GroupBy(b => b.Base, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var fragments = new List<(string, DeepSymbol)>(bases.Count);
        foreach (var (baseFragment, symbol) in bases)
        {
            fragments.Add((
                collisions.Contains(baseFragment) && symbol.Params is { Length: > 0 } parameters
                    ? baseFragment + CollapseWhitespace(parameters)
                    : baseFragment,
                symbol));
        }

        return fragments;
    }

    /// <summary>
    /// A deeper tier replaces what it can see better and keeps what it cannot: symbols and
    /// imports come from the deep analysis, the file-level impression stays tier 0's, and a
    /// per-file error keeps tier 0's candidates wholesale. The imports body is built with
    /// the same prefix and separator tier 0 uses, so handing a store from one tier to
    /// another supersedes nothing that did not actually change.
    /// </summary>
    public static IReadOnlyList<CodeCandidate> Merge(
        string fileEntityPath,
        IReadOnlyList<CodeCandidate> tierZero,
        DeepAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(tierZero);
        ArgumentNullException.ThrowIfNull(analysis);

        if (analysis.Error is not null)
        {
            return tierZero;
        }

        var fileName = fileEntityPath[(fileEntityPath.LastIndexOf('/') + 1)..];
        var merged = tierZero
            .Where(c => c.EntityPath == fileEntityPath && c.Predicate == "about")
            .ToList();

        // One address holds one declaration fact, first declaration wins: partial classes
        // declare one name twice, and a residual overload collision — same scope, name,
        // and written parameter list — is one address by the same rule (D48).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fragment, symbol) in Fragments(analysis.Symbols))
        {
            if (!seen.Add(fragment))
            {
                continue;
            }

            var symbolPath = CodePaths.ForSymbol(fileEntityPath, fragment);
            merged.Add(new CodeCandidate(
                symbolPath, "symbol", symbol.Name, "declared-as", CodeAnalyzer.Cap(symbol.Declaration)));

            if (!string.IsNullOrWhiteSpace(symbol.Doc))
            {
                merged.Add(new CodeCandidate(
                    symbolPath, "symbol", symbol.Name, "about", CodeAnalyzer.Cap(symbol.Doc)));
            }
        }

        if (analysis.Imports.Count > 0)
        {
            var modules = new SortedSet<string>(analysis.Imports, StringComparer.Ordinal);
            merged.Add(new CodeCandidate(
                fileEntityPath,
                "file",
                fileName,
                "imports",
                CodeAnalyzer.Cap("imports " + string.Join(", ", modules))));
        }

        return merged;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
