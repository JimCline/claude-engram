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

/// <summary>One observed call site: who called, what name they wrote, where.</summary>
public sealed record DeepCall(
    string? EnclosingFragment,
    string Callee,
    int Line);

/// <summary>
/// One observed base-list entry: a top-level type names another type as its base or
/// interface. <paramref name="TypeName"/> is the declaring type's own name, resolved to its
/// fragment by <see cref="DeepTier.Merge"/> against the file's top-level declarations only —
/// a nested type of the same name is silently dropped rather than misattributed, since
/// nested-type declarations are not addressed at all today (§8.6). <paramref name="Predicate"/>
/// is decided by the producer, which is the one place that knows the language's ruling
/// (§8.5.1): <c>inherits</c>/<c>implements</c> where the grammar distinguishes them,
/// <c>derives-from</c> where it cannot.
/// </summary>
public sealed record DeepInherit(
    string TypeName,
    string BaseName,
    string Predicate);

/// <summary>One deeper-tier view of one file: what it saw, or why it could not look.</summary>
/// <remarks>
/// <paramref name="Calls"/> and <paramref name="Inherits"/> have no default value
/// deliberately (§4 of the Phase 3 spec): a producer that is not updated to state them must
/// fail to compile, not silently report "this file has none" — which is indistinguishable
/// from the truth.
/// </remarks>
public sealed record DeepAnalysis(
    string Path,
    IReadOnlyList<DeepSymbol> Symbols,
    IReadOnlyList<string> Imports,
    string? Error,
    IReadOnlyList<DeepCall> Calls,
    IReadOnlyList<DeepInherit> Inherits,
    int Tier);

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
        var fragments = Fragments(analysis.Symbols);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Top-level declarations only (§8.6/D48: nested-type declarations are not addressed
        // today), keyed by name so a base-list or containment edge can find its declaring
        // type's fragment without re-deriving Fragments' collision logic.
        var topLevel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (fragment, symbol) in fragments)
        {
            if (symbol.Scope is null)
            {
                topLevel.TryAdd(symbol.Name, fragment);
            }
        }

        // An overload set (three `probe` signatures, say) shares one name and one container,
        // so it would otherwise produce that many byte-identical `contains` candidates —
        // which the indexer's live-fact key does not distinguish, so writing more than one
        // closes and reinserts a fact identical to itself, a spurious supersession CLAUDE.md's
        // append-only-facts invariant forbids. Dedup before the write, not after.
        var containsSeen = new HashSet<(string Container, string Member)>();

        foreach (var (fragment, symbol) in fragments)
        {
            if (seen.Add(fragment))
            {
                var symbolPath = CodePaths.ForSymbol(fileEntityPath, fragment);
                merged.Add(new CodeCandidate(
                    symbolPath, "symbol", symbol.Name, "declared-as", CodeAnalyzer.Cap(symbol.Declaration),
                    AnalyzerTier: analysis.Tier));

                if (!string.IsNullOrWhiteSpace(symbol.Doc))
                {
                    merged.Add(new CodeCandidate(
                        symbolPath, "symbol", symbol.Name, "about", CodeAnalyzer.Cap(symbol.Doc),
                        AnalyzerTier: analysis.Tier));
                }
            }

            // contains (§8.4): the sidecar/grammar already emits scope, so this writes what
            // it already knows as an object-bearing fact rather than a new extraction.
            // Object side keeps the weak-identity scheme calls/inherits use — a member name
            // as written, not a resolved entity (§2's design sketch).
            if (symbol.Scope is { Length: > 0 } containerName
                && topLevel.TryGetValue(containerName, out var containerFragment)
                && containsSeen.Add((containerFragment, symbol.Name)))
            {
                merged.Add(new CodeCandidate(
                    CodePaths.ForSymbol(fileEntityPath, containerFragment), "symbol", containerName, "contains",
                    CodeAnalyzer.Cap("contains " + symbol.Name),
                    Object: symbol.Name,
                    AnalyzerTier: analysis.Tier));
            }
        }

        foreach (var inherit in analysis.Inherits)
        {
            if (!topLevel.TryGetValue(inherit.TypeName, out var typeFragment))
            {
                continue;
            }

            merged.Add(new CodeCandidate(
                CodePaths.ForSymbol(fileEntityPath, typeFragment), "symbol", inherit.TypeName, inherit.Predicate,
                CodeAnalyzer.Cap(inherit.Predicate + " " + inherit.BaseName),
                Object: inherit.BaseName,
                AnalyzerTier: analysis.Tier));
        }

        var importedModules = new SortedSet<string>(analysis.Imports, StringComparer.Ordinal);
        foreach (var m in importedModules)
        {
            merged.Add(new CodeCandidate(
                fileEntityPath,
                "file",
                fileName,
                "imports",
                CodeAnalyzer.Cap("imports " + m),
                Object: m,
                AnalyzerTier: analysis.Tier));
        }

        foreach (var call in Deduplicate(analysis.Calls))
        {
            var (path, kind, display) = call.EnclosingFragment is { } fragment
                ? (CodePaths.ForSymbol(fileEntityPath, fragment), "symbol", CodePaths.LeafOf(fragment))
                : (fileEntityPath, "file", CodePaths.LeafOf(fileEntityPath));   // §5.2.1

            merged.Add(new CodeCandidate(
                path, kind, display, "calls",
                CodeAnalyzer.Cap("calls " + call.Callee),
                Object: call.Callee,
                AnalyzerTier: analysis.Tier));
        }

        return merged;
    }

    /// <summary>
    /// One fact per (caller, callee), not one per call site (§5.5): three calls to the same
    /// target from one function are one belief. Keeps the lowest line so re-indexing an
    /// unchanged file writes nothing. Null <see cref="DeepCall.EnclosingFragment"/>
    /// participates as its own group, so a file's module-level calls never merge with a
    /// same-named symbol's.
    /// </summary>
    private static IEnumerable<DeepCall> Deduplicate(IReadOnlyList<DeepCall> calls) =>
        calls
            .GroupBy(c => (c.EnclosingFragment, c.Callee))
            .Select(g => g.OrderBy(c => c.Line).First());

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
