namespace Engram.Core;

public sealed record DeepSymbol(string Name, string Kind, string Declaration, string? Doc);

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

        // Partial classes declare one name twice; one address holds one declaration fact.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in analysis.Symbols)
        {
            if (symbol.Name.Length == 0 || !seen.Add(symbol.Name))
            {
                continue;
            }

            var symbolPath = CodePaths.ForSymbol(fileEntityPath, symbol.Name);
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
}
