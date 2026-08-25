using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Engram.Core;

/// <summary>
/// Tier-1 syntactic analysis (D24/D47): tree-sitter and one grammar per language,
/// side-loaded from the home's <c>lib/</c> directory the same way sqlite-vec is, and
/// driven entirely by the query strings each registry row carries. One instance serves one
/// index run: it holds one parser, caches loaded grammars and compiled queries, and
/// reports each downgrade once. Everything degrades to tier 0 — a missing library, a
/// grammar the core refuses, a query the grammar refuses — because an optional tier that
/// can fail an index run is not optional.
/// </summary>
/// <remarks>
/// Not thread-safe: an index run is single-threaded and this holds one native parser.
/// </remarks>
public sealed unsafe class TreeSitter : IDisposable
{
    public const string EnvironmentOverride = "ENGRAM_TREE_SITTER_DIR";

    /// <summary>
    /// The directory holding the core library, or null for "this machine indexes at
    /// tier 0". Same semantics as the Roslyn sidecar's override: an env var that points at
    /// a directory without the library means no tier 1, never a fallback to the default —
    /// a broken explicit configuration should not silently become a different one.
    /// </summary>
    public static string? Locate(Func<string, string?> environment, EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(home);

        if (environment(EnvironmentOverride) is { Length: > 0 } overrideDir)
        {
            return File.Exists(Path.Combine(overrideDir, CoreLibraryFile)) ? overrideDir : null;
        }

        return File.Exists(Path.Combine(home.LibDir, CoreLibraryFile)) ? home.LibDir : null;
    }

    public static string CoreLibraryFile => LibraryFile("tree-sitter");

    public static string GrammarLibraryFile(string library) => LibraryFile("tree-sitter-" + library);

    private static string LibraryFile(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".dll"
        : OperatingSystem.IsMacOS() ? "lib" + baseName + ".dylib"
        : "lib" + baseName + ".so";

    public static TreeSitter? TryCreate(string directory, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(notes);

        try
        {
            return new TreeSitter(directory);
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            notes.Add($"tree-sitter: {CoreLibraryFile} would not load ({e.GetType().Name}); tier 1 unavailable");
            return null;
        }
    }

    private readonly string directory;
    private readonly List<string> downgrades = [];
    private readonly Dictionary<string, IntPtr> languages = new(StringComparer.Ordinal);
    private readonly Dictionary<(IntPtr, string), Compiled?> queries = [];
    private readonly IntPtr parser;

    /// <summary>Why files that could have been tier 1 were not, one line per cause.</summary>
    public IReadOnlyList<string> Downgrades => downgrades;

    // Core exports, bound once. Function pointers rather than delegates because TsNode is
    // returned by value and this must survive Native AOT.
    private readonly delegate* unmanaged[Cdecl]<IntPtr> parserNew;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> parserDelete;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte> parserSetLanguage;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, uint, IntPtr> parserParseString;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> treeDelete;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, TsNode> treeRootNode;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, uint*, int*, IntPtr> queryNew;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> queryDelete;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint> queryPatternCount;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint> queryCaptureCount;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, IntPtr> queryCaptureNameForId;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, IntPtr> queryStringValueForId;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, TsQueryPredicateStep*> queryPredicatesForPattern;
    private readonly delegate* unmanaged[Cdecl]<IntPtr> queryCursorNew;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> queryCursorDelete;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, TsNode, void> queryCursorExec;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, TsQueryMatch*, byte> queryCursorNextMatch;
    private readonly delegate* unmanaged[Cdecl]<TsNode, uint> nodeStartByte;
    private readonly delegate* unmanaged[Cdecl]<TsNode, uint> nodeEndByte;
    private readonly delegate* unmanaged[Cdecl]<TsNode, TsNode> nodeParent;
    private readonly delegate* unmanaged[Cdecl]<TsNode, byte> nodeIsNull;

    private TreeSitter(string directory)
    {
        this.directory = directory;
        var core = NativeLibrary.Load(Path.Combine(directory, CoreLibraryFile));

        parserNew = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(core, "ts_parser_new");
        parserDelete = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(core, "ts_parser_delete");
        parserSetLanguage = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)NativeLibrary.GetExport(core, "ts_parser_set_language");
        parserParseString = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, uint, IntPtr>)NativeLibrary.GetExport(core, "ts_parser_parse_string");
        treeDelete = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(core, "ts_tree_delete");
        treeRootNode = (delegate* unmanaged[Cdecl]<IntPtr, TsNode>)NativeLibrary.GetExport(core, "ts_tree_root_node");
        queryNew = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, uint*, int*, IntPtr>)NativeLibrary.GetExport(core, "ts_query_new");
        queryDelete = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(core, "ts_query_delete");
        queryPatternCount = (delegate* unmanaged[Cdecl]<IntPtr, uint>)NativeLibrary.GetExport(core, "ts_query_pattern_count");
        queryCaptureCount = (delegate* unmanaged[Cdecl]<IntPtr, uint>)NativeLibrary.GetExport(core, "ts_query_capture_count");
        queryCaptureNameForId = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, IntPtr>)NativeLibrary.GetExport(core, "ts_query_capture_name_for_id");
        queryStringValueForId = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, IntPtr>)NativeLibrary.GetExport(core, "ts_query_string_value_for_id");
        queryPredicatesForPattern = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, TsQueryPredicateStep*>)NativeLibrary.GetExport(core, "ts_query_predicates_for_pattern");
        queryCursorNew = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(core, "ts_query_cursor_new");
        queryCursorDelete = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(core, "ts_query_cursor_delete");
        queryCursorExec = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, TsNode, void>)NativeLibrary.GetExport(core, "ts_query_cursor_exec");
        queryCursorNextMatch = (delegate* unmanaged[Cdecl]<IntPtr, TsQueryMatch*, byte>)NativeLibrary.GetExport(core, "ts_query_cursor_next_match");
        nodeStartByte = (delegate* unmanaged[Cdecl]<TsNode, uint>)NativeLibrary.GetExport(core, "ts_node_start_byte");
        nodeEndByte = (delegate* unmanaged[Cdecl]<TsNode, uint>)NativeLibrary.GetExport(core, "ts_node_end_byte");
        nodeParent = (delegate* unmanaged[Cdecl]<TsNode, TsNode>)NativeLibrary.GetExport(core, "ts_node_parent");
        nodeIsNull = (delegate* unmanaged[Cdecl]<TsNode, byte>)NativeLibrary.GetExport(core, "ts_node_is_null");

        parser = parserNew();
    }

    /// <summary>
    /// One file through its row's queries, or null for "this file takes tier 0" — the
    /// reason, if it is news, lands in <see cref="Downgrades"/> once per cause rather than
    /// once per file.
    /// </summary>
    public DeepAnalysis? Analyze(LanguageDefinition language, string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        var extension = Path.GetExtension(relativePath);
        if (language.GrammarFor(extension) is not { } grammar
            || language.DeclarationQuery is null
            || language.ImportQuery is null)
        {
            return null;
        }

        if (LoadLanguage(language.Id, grammar) is not { } lang)
        {
            return null;
        }

        var declarations = Compile(lang, language.Id, "declaration", language.DeclarationQuery);
        var imports = Compile(lang, language.Id, "import", language.ImportQuery);
        if (declarations is null || imports is null)
        {
            return null;
        }

        var source = Encoding.UTF8.GetBytes(content);
        IntPtr tree;
        fixed (byte* s = source)
        {
            tree = parserParseString(parser, IntPtr.Zero, s, (uint)source.Length);
        }

        if (tree == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var root = treeRootNode(tree);

            var symbols = new List<DeepSymbol>();
            var declNodes = new Dictionary<DeepSymbol, TsNode>(ReferenceEqualityComparer.Instance);
            foreach (var captures in Matches(declarations, root, source))
            {
                if (!captures.TryGetValue("name", out var nameNode))
                {
                    continue;
                }

                var line = LineAt(source, nodeStartByte(nameNode));
                var scope = captures.TryGetValue("scope", out var scopeNode)
                    ? Text(scopeNode, source)
                    : null;

                // Grammar v2 (D48): a `private` member is implementation, not surface —
                // the queries cannot express negation, so the modifier is read off the
                // declaration line. The `#name` form never gets this far: the member
                // patterns capture (property_identifier), which a private name is not.
                if (scope is not null && line.StartsWith("private ", StringComparison.Ordinal))
                {
                    continue;
                }

                var symbol = new DeepSymbol(
                    Text(nameNode, source),
                    "symbol",
                    line,
                    Doc: null,
                    Scope: scope,
                    Params: captures.TryGetValue("params", out var paramsNode)
                        ? Text(paramsNode, source)
                        : null);
                symbols.Add(symbol);

                // The declaration node for a call-site parent walk (§5.2) is this capture's
                // own parent: a tree-sitter field never inserts an extra layer, so `@name`'s
                // ts_node_parent is exactly the function_declaration/method_definition/etc.
                // the pattern matched. Recording it here, keyed by the same DeepSymbol
                // Fragments() below will pair a fragment with, is what lets the walk land on
                // a byte-identical address without re-implementing "what is a declaration".
                declNodes[symbol] = nodeParent(nameNode);
            }

            var modules = new List<string>();
            foreach (var captures in Matches(imports, root, source))
            {
                if (captures.TryGetValue("module", out var node))
                {
                    modules.Add(Text(node, source));
                }
            }

            var calls = ExtractCalls(lang, language, root, source, symbols, declNodes);

            return new DeepAnalysis(relativePath, symbols, modules, null, calls, Tier: 1);
        }
        finally
        {
            treeDelete(tree);
        }
    }

    /// <summary>
    /// Query and walk are independent of the declaration/import pair above (C2): a language
    /// with no <see cref="LanguageDefinition.CallQuery"/> (tier 0, or C#, which goes through
    /// the Roslyn sidecar instead) simply contributes no calls, rather than losing the
    /// declarations and imports it already found.
    /// </summary>
    private List<DeepCall> ExtractCalls(
        IntPtr lang,
        LanguageDefinition language,
        TsNode root,
        byte[] source,
        List<DeepSymbol> symbols,
        Dictionary<DeepSymbol, TsNode> declNodes)
    {
        if (language.CallQuery is null)
        {
            return [];
        }

        var callQuery = Compile(lang, language.Id, "call", language.CallQuery);
        if (callQuery is null)
        {
            return [];
        }

        var ranges = new Dictionary<(uint Start, uint End), string>();
        foreach (var (fragment, symbol) in DeepTier.Fragments(symbols))
        {
            if (declNodes.TryGetValue(symbol, out var node))
            {
                ranges[(nodeStartByte(node), nodeEndByte(node))] = fragment;
            }
        }

        var calls = new List<DeepCall>();
        var newlineOffsets = NewlineOffsets(source);
        foreach (var captures in Matches(callQuery, root, source))
        {
            if (!captures.TryGetValue("callee", out var calleeNode))
            {
                continue;
            }

            string? enclosing = null;
            var ancestor = nodeParent(calleeNode);
            while (nodeIsNull(ancestor) == 0)
            {
                if (ranges.TryGetValue((nodeStartByte(ancestor), nodeEndByte(ancestor)), out var fragment))
                {
                    enclosing = fragment;
                    break;
                }

                ancestor = nodeParent(ancestor);
            }

            calls.Add(new DeepCall(enclosing, Text(calleeNode, source), LineNumberAt(newlineOffsets, nodeStartByte(calleeNode))));
        }

        return calls;
    }

    private IntPtr? LoadLanguage(string languageId, TreeSitterGrammar grammar)
    {
        if (languages.TryGetValue(grammar.Symbol, out var cached))
        {
            return cached == IntPtr.Zero ? null : cached;
        }

        var path = Path.Combine(directory, GrammarLibraryFile(grammar.Library));
        IntPtr language;
        try
        {
            var handle = NativeLibrary.Load(path);
            language = ((delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(handle, grammar.Symbol))();
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            downgrades.Add($"tree-sitter: {GrammarLibraryFile(grammar.Library)} is not loadable ({e.GetType().Name}); {languageId} files took tier 0");
            languages[grammar.Symbol] = IntPtr.Zero;
            return null;
        }

        // set_language is the only ABI authority. Measured: one core accepted grammars
        // answering ABI 14 and 15 in the same process, so comparing version numbers here
        // would refuse grammars the library itself is happy with (D47).
        if (parserSetLanguage(parser, language) == 0)
        {
            downgrades.Add($"tree-sitter: this core refuses the {languageId} grammar (ABI); {languageId} files took tier 0 — refetch to rebuild the grammars");
            languages[grammar.Symbol] = IntPtr.Zero;
            return null;
        }

        languages[grammar.Symbol] = language;
        return language;
    }

    private Compiled? Compile(IntPtr language, string languageId, string label, string queryText)
    {
        if (queries.TryGetValue((language, queryText), out var cached))
        {
            return cached;
        }

        var bytes = Encoding.UTF8.GetBytes(queryText);
        uint errorOffset;
        int errorType;
        IntPtr query;
        fixed (byte* q = bytes)
        {
            query = queryNew(language, q, (uint)bytes.Length, &errorOffset, &errorType);
        }

        if (query == IntPtr.Zero)
        {
            // The registry and the grammar disagree — a grammar built from a different
            // generation than the row's query was written against. Loud and specific,
            // because this query matched in conformance and silence here would look like
            // an empty file forever.
            downgrades.Add($"tree-sitter: the {languageId} {label} query is refused at offset {errorOffset} (error {errorType}); {languageId} files took tier 0 — the installed grammar does not match this binary's registry");
            queries[(language, queryText)] = null;
            return null;
        }

        var names = new string[queryCaptureCount(query)];
        for (var i = 0; i < names.Length; i++)
        {
            uint length;
            var name = queryCaptureNameForId(query, (uint)i, &length);
            names[i] = Marshal.PtrToStringUTF8(name, (int)length) ?? string.Empty;
        }

        var predicates = new (uint CaptureId, string Expected)[queryPatternCount(query)][];
        for (var pattern = 0; pattern < predicates.Length; pattern++)
        {
            predicates[pattern] = ParsePredicates(query, (uint)pattern);
        }

        var compiled = new Compiled(query, names, predicates);
        queries[(language, queryText)] = compiled;
        return compiled;
    }

    /// <summary>
    /// The C library only reports predicates; evaluating them is the binding's job. Only
    /// <c>#eq? @capture "literal"</c> is supported, and anything else throws rather than
    /// filters nothing: a predicate the runner ignores is a filter that silently stopped
    /// filtering, which is exactly the failure D47 refuses.
    /// </summary>
    private (uint CaptureId, string Expected)[] ParsePredicates(IntPtr query, uint pattern)
    {
        uint stepCount;
        var steps = queryPredicatesForPattern(query, pattern, &stepCount);
        if (stepCount == 0)
        {
            return [];
        }

        var parsed = new List<(uint, string)>();
        var i = 0u;
        while (i < stepCount)
        {
            if (i + 3 >= stepCount
                || steps[i].Type != StepString || QueryString(query, steps[i].ValueId) != "eq?"
                || steps[i + 1].Type != StepCapture
                || steps[i + 2].Type != StepString
                || steps[i + 3].Type != StepDone)
            {
                throw new InvalidOperationException(
                    "tree-sitter registry queries may only use #eq? @capture \"literal\"");
            }

            parsed.Add((steps[i + 1].ValueId, QueryString(query, steps[i + 2].ValueId)));
            i += 4;
        }

        return [.. parsed];
    }

    /// <summary>
    /// Every surviving match as its named captures, in cursor order. Grammar v2 needs
    /// captures that belong together to stay together — <c>@scope</c> and <c>@params</c>
    /// mean nothing apart from their match's <c>@name</c> — so this is per-match where the
    /// v1 extraction was one flat capture list. Captures starting with <c>_</c> exist only
    /// for predicates and are not returned.
    /// </summary>
    private List<Dictionary<string, TsNode>> Matches(Compiled compiled, TsNode root, byte[] source)
    {
        var matches = new List<Dictionary<string, TsNode>>();
        var cursor = queryCursorNew();
        try
        {
            queryCursorExec(cursor, compiled.Query, root);

            TsQueryMatch match;
            while (queryCursorNextMatch(cursor, &match) != 0)
            {
                if (!PredicatesHold(compiled, match, source))
                {
                    continue;
                }

                var captures = new Dictionary<string, TsNode>(StringComparer.Ordinal);
                for (var i = 0; i < match.CaptureCount; i++)
                {
                    var capture = ((TsQueryCapture*)match.Captures)[i];
                    var name = compiled.CaptureNames[capture.Index];
                    if (!name.StartsWith('_'))
                    {
                        captures.TryAdd(name, capture.Node);
                    }
                }

                matches.Add(captures);
            }
        }
        finally
        {
            queryCursorDelete(cursor);
        }

        return matches;
    }

    private bool PredicatesHold(Compiled compiled, in TsQueryMatch match, byte[] source)
    {
        foreach (var (captureId, expected) in compiled.Predicates[match.PatternIndex])
        {
            var holds = false;
            for (var i = 0; i < match.CaptureCount; i++)
            {
                var capture = ((TsQueryCapture*)match.Captures)[i];
                if (capture.Index == captureId)
                {
                    holds = Text(capture.Node, source) == expected;
                    break;
                }
            }

            if (!holds)
            {
                return false;
            }
        }

        return true;
    }

    private string Text(TsNode node, byte[] source)
    {
        var start = (int)nodeStartByte(node);
        var end = (int)nodeEndByte(node);
        return Encoding.UTF8.GetString(source, start, end - start);
    }

    /// <summary>The full source line holding a byte offset — tier 0's declaration body, found precisely.</summary>
    private static string LineAt(byte[] source, uint offset)
    {
        var start = (int)offset;
        while (start > 0 && source[start - 1] != (byte)'\n')
        {
            start--;
        }

        var end = (int)offset;
        while (end < source.Length && source[end] != (byte)'\n')
        {
            end++;
        }

        return Encoding.UTF8.GetString(source, start, end - start).Trim();
    }

    /// <summary>Byte offsets of every '\n' in <paramref name="source"/>, ascending — built once
    /// per file so <see cref="LineNumberAt"/> can binary-search rather than rescan from 0 for
    /// every call site (was O(N) per call, O(N × call count) per file).</summary>
    private static List<int> NewlineOffsets(byte[] source)
    {
        var offsets = new List<int>();
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == (byte)'\n')
            {
                offsets.Add(i);
            }
        }

        return offsets;
    }

    /// <summary>1-based line number holding a byte offset, via binary search over <paramref name="newlineOffsets"/>.</summary>
    private static int LineNumberAt(List<int> newlineOffsets, uint offset)
    {
        var index = newlineOffsets.BinarySearch((int)offset);
        var newlinesBefore = index >= 0 ? index : ~index;
        return newlinesBefore + 1;
    }

    private string QueryString(IntPtr query, uint id)
    {
        uint length;
        var value = queryStringValueForId(query, id, &length);
        return Marshal.PtrToStringUTF8(value, (int)length) ?? string.Empty;
    }

    public void Dispose()
    {
        foreach (var compiled in queries.Values)
        {
            if (compiled is not null)
            {
                queryDelete(compiled.Query);
            }
        }

        queries.Clear();
        parserDelete(parser);
    }

    private const int StepDone = 0;
    private const int StepCapture = 1;
    private const int StepString = 2;

    private sealed record Compiled(
        IntPtr Query,
        string[] CaptureNames,
        (uint CaptureId, string Expected)[][] Predicates);

    [StructLayout(LayoutKind.Sequential)]
    private struct TsNode
    {
        public uint Context0;
        public uint Context1;
        public uint Context2;
        public uint Context3;
        public IntPtr Id;
        public IntPtr Tree;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TsQueryCapture
    {
        public TsNode Node;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TsQueryMatch
    {
        public uint Id;
        public ushort PatternIndex;
        public ushort CaptureCount;
        public IntPtr Captures;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TsQueryPredicateStep
    {
        public int Type;
        public uint ValueId;
    }
}
