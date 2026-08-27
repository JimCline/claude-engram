using System.Text.RegularExpressions;

namespace Engram.Core;

/// <summary>
/// A sample source and what extraction must find in it. Carried on the registry row so the
/// conformance suite iterates rows with zero edits per language (D24): a harness keeping
/// its own fixture list per language is the same defect as a harness keeping its own
/// language list. <see cref="ExpectedSymbols"/> is what tier 0's line-level read must find;
/// <see cref="ExpectedDeepSymbols"/> is the grammar-v2 fragment list the tier-1 queries
/// must produce from the same source (D48), null where the tiers see the same thing.
/// </summary>
public sealed record LanguageFixture(
    string Source,
    IReadOnlyList<string> ExpectedSymbols,
    IReadOnlyList<string> ExpectedImports,
    IReadOnlyList<string>? ExpectedDeepSymbols = null,
    IReadOnlyList<string>? ExpectedInherits = null);

/// <summary>
/// One tree-sitter grammar (D47): which library file carries it, which export produces it,
/// and which of the row's extensions it parses — an empty list claims whatever the row's
/// other grammars did not. TSX is why this is a list: <c>.ts</c> and <c>.tsx</c> are one
/// language row parsed by two grammars.
/// </summary>
public sealed record TreeSitterGrammar(
    string Library,
    string Symbol,
    IReadOnlyList<string> Extensions);

/// <summary>
/// Which inheritance predicate(s) a language's grammar can justify (Ultra-Advisor ruling,
/// §8.5.1): <see cref="Split"/> where extends/implements are syntactically distinct
/// (TypeScript, JavaScript, Java, GDScript's <c>extends</c>), <see cref="DerivesFrom"/>
/// where one base list cannot be told apart (C#, Python), <see cref="None"/> where the
/// language has no base-list construct at all.
/// </summary>
public enum InheritancePredicate
{
    None,
    Split,
    DerivesFrom,
}

/// <summary>One language: what it is called, which files are its, and what tier-0 can extract.</summary>
/// <remarks>
/// <para><see cref="Tier"/> declares the deepest analysis this language is entitled to
/// (D24: 0 universal, 1 tree-sitter, 2 semantic sidecar). Tier 2 runs through the Roslyn
/// sidecar; tier 1 through the grammars and queries on the row (D47); everything else, and
/// every tier-1 row on a machine without the grammars, executes on the tier-0 mechanism.</para>
///
/// <para>Patterns are data, not code: each declaration pattern must expose a
/// <c>name</c> group, each import pattern a <c>module</c> group. They are deliberately
/// conservative — top-level declarations and imports, nothing an honest line-level read
/// cannot see. The deep tier corrects and extends by adopt/merge (D2), never by this row
/// growing cleverer.</para>
///
/// <para>The queries mirror the patterns' contract with captures: every declaration query
/// names its symbols <c>@name</c>, member patterns add <c>@scope</c> (the containing type)
/// and <c>@params</c> (the written parameter list, for overload disambiguation — D48),
/// every import query names its sources <c>@module</c>, and a capture starting with
/// <c>_</c> exists only for a predicate. Each query is verified against the compiled
/// grammar it targets before it lands here (D47) — <c>ts_query_new</c> validates node
/// types per grammar, which is why TypeScript and JavaScript cannot share a declaration
/// query and why a stale one fails loudly instead of matching nothing.</para>
/// </remarks>
public sealed record LanguageDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    int Tier,
    IReadOnlyList<string> DeclarationPatterns,
    IReadOnlyList<string> ImportPatterns,
    bool DocHeadings,
    LanguageFixture? Fixture,
    IReadOnlyList<TreeSitterGrammar>? Grammars = null,
    string? DeclarationQuery = null,
    string? ImportQuery = null,
    string? CallQuery = null,
    InheritancePredicate InheritancePredicate = InheritancePredicate.None,
    string? InheritanceQuery = null,
    bool NestedTypeEdgesDropped = false)
{
    /// <summary>The grammar that parses one of this row's extensions, or null at tier 0/2.</summary>
    public TreeSitterGrammar? GrammarFor(string extension)
    {
        if (Grammars is null)
        {
            return null;
        }

        TreeSitterGrammar? catchAll = null;
        foreach (var grammar in Grammars)
        {
            if (grammar.Extensions.Count == 0)
            {
                catchAll ??= grammar;
                continue;
            }

            foreach (var candidate in grammar.Extensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return grammar;
                }
            }
        }

        return catchAll;
    }
}

/// <summary>
/// The one list of languages (D24). Adding a language is one row here — if it also needs
/// an edit to the analyzer, the CLI, a report, or a test harness, the abstraction has not
/// landed and the row is decoration. A registry test enforces both directions.
/// </summary>
/// <remarks>
/// Static rows, not discovery: Native AOT cannot scan assemblies for implementations, and
/// an enumerable kind should stay enumerable and greppable anyway.
/// </remarks>
public static class LanguageRegistry
{
    // The declaration queries capture every top-level named declaration, exported or not —
    // except const/let/var, which count only when exported: an unexported binding is
    // implementation, not interface. Grammar v2 (D48) adds the member patterns: they are
    // not program-anchored, so a class matches wherever it sits, and each carries @scope
    // beside @name — the binding has no node navigation on purpose, nesting is the
    // pattern's shape, and ts_query_new validates that shape like everything else. @params
    // feeds overload disambiguation. Members are captured at every visibility (all-members
    // spec): a `private` member has no runtime effect in TS/JS and is captured the same as
    // any other. `#name` members are the one true runtime-enforced privacy the language
    // has — the grammar names that name a (private_property_identifier), a distinct node
    // type from (property_identifier), so each member pattern below is duplicated once for
    // it; the `#` is part of the captured text and is kept, since stripping it would
    // collide `#count` with a public `count` in the same class. Verified against the
    // compiled grammars, where the vocabularies differ: TS names classes with
    // (type_identifier) and has interface, enum, type alias and abstract class node types
    // JS does not. method_signature (the bodiless overload-declaration form) pairs with
    // private_property_identifier too (E7, verified against the compiled grammar: a
    // `#name(): void;` / `#name(n: number): void;` overload set parses and each signature
    // is captured at its own address, distinct from the implementation) — without this
    // pairing an overload signature on a `#`-private method matches nothing and only the
    // implementation is emitted. Two pairings stay correctly absent, by construction: an
    // interface cannot declare a `#`-private member (interface_declaration never gets a
    // private_property_identifier row), and `abstract` and `#`-private are mutually
    // exclusive (abstract_method_signature never gets one either).
    private const string TypeScriptDeclarations = """
        (program (function_declaration name: (identifier) @name parameters: (formal_parameters) @params))
        (program (generator_function_declaration name: (identifier) @name parameters: (formal_parameters) @params))
        (program (function_signature name: (identifier) @name parameters: (formal_parameters) @params))
        (program (class_declaration name: (type_identifier) @name))
        (program (abstract_class_declaration name: (type_identifier) @name))
        (program (interface_declaration name: (type_identifier) @name))
        (program (enum_declaration name: (identifier) @name))
        (program (type_alias_declaration name: (type_identifier) @name))
        (program (export_statement declaration: (function_declaration name: (identifier) @name parameters: (formal_parameters) @params)))
        (program (export_statement declaration: (generator_function_declaration name: (identifier) @name parameters: (formal_parameters) @params)))
        (program (export_statement declaration: (function_signature name: (identifier) @name parameters: (formal_parameters) @params)))
        (program (export_statement declaration: (class_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (abstract_class_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (interface_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (enum_declaration name: (identifier) @name)))
        (program (export_statement declaration: (type_alias_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (lexical_declaration (variable_declarator name: (identifier) @name))))
        (program (export_statement declaration: (variable_declaration (variable_declarator name: (identifier) @name))))
        (class_declaration name: (type_identifier) @scope body: (class_body (method_definition name: (property_identifier) @name parameters: (formal_parameters) @params)))
        (class_declaration name: (type_identifier) @scope body: (class_body (method_definition name: (private_property_identifier) @name parameters: (formal_parameters) @params)))
        (class_declaration name: (type_identifier) @scope body: (class_body (method_signature name: (property_identifier) @name parameters: (formal_parameters) @params)))
        (class_declaration name: (type_identifier) @scope body: (class_body (method_signature name: (private_property_identifier) @name parameters: (formal_parameters) @params)))
        (class_declaration name: (type_identifier) @scope body: (class_body (public_field_definition name: (property_identifier) @name)))
        (class_declaration name: (type_identifier) @scope body: (class_body (public_field_definition name: (private_property_identifier) @name)))
        (abstract_class_declaration name: (type_identifier) @scope body: (class_body (method_definition name: (property_identifier) @name parameters: (formal_parameters) @params)))
        (abstract_class_declaration name: (type_identifier) @scope body: (class_body (method_definition name: (private_property_identifier) @name parameters: (formal_parameters) @params)))
        (abstract_class_declaration name: (type_identifier) @scope body: (class_body (method_signature name: (property_identifier) @name parameters: (formal_parameters) @params)))
        (abstract_class_declaration name: (type_identifier) @scope body: (class_body (method_signature name: (private_property_identifier) @name parameters: (formal_parameters) @params)))
        ; no abstract_method_signature + private_property_identifier pairing: an abstract member
        ; must be reachable from a subclass, so TypeScript rejects both `abstract #foo()` and
        ; `private abstract foo()`. The construct does not exist, rather than being skipped.
        ; Confirmed against tsc: TS18019 ('abstract' modifier cannot be used with a private
        ; identifier) and TS1243 ('private' modifier cannot be used with 'abstract' modifier).
        (abstract_class_declaration name: (type_identifier) @scope body: (class_body (abstract_method_signature name: (property_identifier) @name parameters: (formal_parameters) @params)))
        (abstract_class_declaration name: (type_identifier) @scope body: (class_body (public_field_definition name: (property_identifier) @name)))
        (abstract_class_declaration name: (type_identifier) @scope body: (class_body (public_field_definition name: (private_property_identifier) @name)))
        (interface_declaration name: (type_identifier) @scope body: (interface_body (method_signature name: (property_identifier) @name parameters: (formal_parameters) @params)))
        (interface_declaration name: (type_identifier) @scope body: (interface_body (property_signature name: (property_identifier) @name)))
        """;

    private const string JavaScriptDeclarations = """
        (program (function_declaration name: (identifier) @name parameters: (formal_parameters) @params))
        (program (generator_function_declaration name: (identifier) @name parameters: (formal_parameters) @params))
        (program (class_declaration name: (identifier) @name))
        (program (export_statement declaration: (function_declaration name: (identifier) @name parameters: (formal_parameters) @params)))
        (program (export_statement declaration: (generator_function_declaration name: (identifier) @name parameters: (formal_parameters) @params)))
        (program (export_statement declaration: (class_declaration name: (identifier) @name)))
        (program (export_statement declaration: (lexical_declaration (variable_declarator name: (identifier) @name))))
        (program (export_statement declaration: (variable_declaration (variable_declarator name: (identifier) @name))))
        (class_declaration name: (identifier) @scope body: (class_body (method_definition name: (property_identifier) @name parameters: (formal_parameters) @params)))
        (class_declaration name: (identifier) @scope body: (class_body (method_definition name: (private_property_identifier) @name parameters: (formal_parameters) @params)))
        (class_declaration name: (identifier) @scope body: (class_body (field_definition property: (property_identifier) @name)))
        (class_declaration name: (identifier) @scope body: (class_body (field_definition property: (private_property_identifier) @name)))
        """;

    // Shared across TS and JS: import statements, require(), and dynamic import() all use
    // node types common to both grammars. The @_fn capture exists only for the #eq?
    // predicate, which is what keeps fetch("url") from reading as an import.
    private const string ScriptImports = """
        (import_statement source: (string (string_fragment) @module))
        (call_expression function: (identifier) @_fn arguments: (arguments (string (string_fragment) @module)) (#eq? @_fn "require"))
        (call_expression function: (import) arguments: (arguments (string (string_fragment) @module)))
        """;

    // Flat by design (§5.2): a nested query has no way to reach a call inside an if/for/try
    // body (tree-sitter's query language has no descendant operator, and the field-chain
    // patterns above only match a fixed statement shape), so attribution is a parent walk
    // in TreeSitter.cs instead of a query shape. TS and JS get their own compiled query
    // (D47) even though the pattern text is identical, since ts_query_new validates node
    // types per grammar.
    private const string TypeScriptCalls = "(call_expression function: (_) @callee)";

    private const string JavaScriptCalls = "(call_expression function: (_) @callee)";

    // Base-list edges (§8.5, §8.6), top-level declarations only, matching the declaration
    // queries' own scope. @scope names the declaring type itself (not a container), reusing
    // the same capture-name convention the member patterns above use for "the type this
    // belongs to". @base -> inherits, @iface -> implements (§8.5.1's per-language split);
    // wildcards on the base/interface node absorb generic_type/nested_type_identifier/
    // type_identifier without listing each. Verified by dumping tree-sitter-typescript
    // 0.23.2's actual parse tree (ts_node_string) for a fixture exercising every shape
    // below — both the bare and export_statement-wrapped forms are needed, since an
    // exported class/interface nests one level deeper than an unexported one (the same
    // reason the declaration queries above carry both forms).
    //
    // TypeScript interface `extends` is deliberately mapped to @base (inherits), not @iface:
    // it is an interface extending an interface, not a class implementing one (§8.6's
    // flagged, unresolved-by-the-amendment call).
    private const string TypeScriptInheritance = """
        (program (class_declaration name: (type_identifier) @scope (class_heritage (extends_clause value: (_) @base))))
        (program (class_declaration name: (type_identifier) @scope (class_heritage (implements_clause (_) @iface))))
        (program (abstract_class_declaration name: (type_identifier) @scope (class_heritage (extends_clause value: (_) @base))))
        (program (abstract_class_declaration name: (type_identifier) @scope (class_heritage (implements_clause (_) @iface))))
        (program (interface_declaration name: (type_identifier) @scope (extends_type_clause type: (_) @base)))
        (program (export_statement declaration: (class_declaration name: (type_identifier) @scope (class_heritage (extends_clause value: (_) @base)))))
        (program (export_statement declaration: (class_declaration name: (type_identifier) @scope (class_heritage (implements_clause (_) @iface)))))
        (program (export_statement declaration: (abstract_class_declaration name: (type_identifier) @scope (class_heritage (extends_clause value: (_) @base)))))
        (program (export_statement declaration: (abstract_class_declaration name: (type_identifier) @scope (class_heritage (implements_clause (_) @iface)))))
        (program (export_statement declaration: (interface_declaration name: (type_identifier) @scope (extends_type_clause type: (_) @base))))
        """;

    // JavaScript's class_heritage has no extends_clause/implements_clause split — it is a
    // single expression (tree-sitter-javascript 0.25.0's node-types) — and there is no
    // interface construct, so only @base is ever captured. InheritancePredicate.Split still
    // applies (§8.5.1 groups JS with TS/Java/GDScript): the query simply never produces an
    // @iface capture, which is honest rather than a lie, since JS syntax cannot express one.
    private const string JavaScriptInheritance = """
        (program (class_declaration name: (identifier) @scope (class_heritage (_) @base)))
        (program (export_statement declaration: (class_declaration name: (identifier) @scope (class_heritage (_) @base))))
        """;

    /// <summary>Catch-all for files no row claims: impressions only, nothing extracted.</summary>
    public static readonly LanguageDefinition Text = new(
        Id: "text",
        DisplayName: "Text",
        Extensions: [],
        Tier: 0,
        DeclarationPatterns: [],
        ImportPatterns: [],
        DocHeadings: false,
        Fixture: null);

    public static readonly IReadOnlyList<LanguageDefinition> All =
    [
        new(
            Id: "csharp",
            DisplayName: "C#",
            Extensions: [".cs"],
            Tier: 2,
            DeclarationPatterns:
            [
                // Type declarations at namespace-level indentation (0–4 spaces). Nested
                // types and members are the deep tier's to key (grammar v2, D48); tier 0
                // stays top-level, the honest resolution of a line-level read.
                @"^[ ]{0,4}(?:\[[^\]]*\][ ]*)*(?:(?:public|internal|protected|private|static|sealed|abstract|partial|unsafe|file)[ ]+)*(?:class|interface|struct|record(?:[ ]+(?:class|struct))?|enum|delegate[ ]+\S+)[ ]+(?<name>[A-Za-z_]\w*)",
            ],
            ImportPatterns:
            [
                @"^\s*using[ ]+(?:static[ ]+)?(?<module>[A-Za-z_][\w.]*)[ ]*;",
            ],
            DocHeadings: false,
            Fixture: new(
                Source: """
                    using System.Text;
                    using static System.Math;

                    namespace Demo;

                    public sealed class Widget { }
                    internal record struct Point(int X, int Y);
                    enum Color { Red }
                    """,
                ExpectedSymbols: ["Widget", "Point", "Color"],
                ExpectedImports: ["System.Text", "System.Math"]),
            InheritancePredicate: InheritancePredicate.DerivesFrom,
            NestedTypeEdgesDropped: true),
        new(
            Id: "typescript",
            DisplayName: "TypeScript",
            Extensions: [".ts", ".tsx"],
            Tier: 1,
            DeclarationPatterns:
            [
                @"^\s*export[ ]+(?:default[ ]+)?(?:declare[ ]+)?(?:abstract[ ]+)?(?:async[ ]+)?(?:function\*?|class|interface|enum|type|const|let|var)[ ]+(?<name>[A-Za-z_$][\w$]*)",
            ],
            ImportPatterns:
            [
                @"^\s*import\b[^'""]*['""](?<module>[^'""]+)['""]",
                @"\brequire\(\s*['""](?<module>[^'""]+)['""]",
            ],
            DocHeadings: false,
            Fixture: new(
                Source: """
                    import { readFile } from "node:fs";
                    import config from "./config";

                    export interface Options extends BaseOptions { deep: boolean; limit(n: number): void }
                    export async function scan(o: Options): Promise<void> {}
                    const hidden = 1;

                    class BareBoth extends BareBase implements BareIface {}
                    abstract class BareAbstractBoth extends AbsBase implements AbsIface {}
                    interface BareIfaceExt extends BareIface {}
                    export abstract class ExportedAbstract extends AbsBase implements AbsIface {}

                    export class Scanner extends ScannerBase implements Trackable {
                        depth = 1;
                        private cache = "";
                        #id = "";
                        probe(): void;
                        probe(deep: boolean): void;
                        probe(deep?: boolean): void {}
                        private reset(): void;
                        private reset(n: number): void;
                        private reset(n?: number): void {}
                        #clear(): void;
                        #clear(n: number): void;
                        #clear(n?: number): void {}
                        @deprecated
                        private legacy(): void {}
                        get size(): number { return this.depth; }
                    }
                    """,
                ExpectedSymbols: ["Options", "scan", "Scanner", "ExportedAbstract"],
                ExpectedImports: ["node:fs", "./config"],
                ExpectedDeepSymbols:
                [
                    "Options",
                    "Options/deep",
                    "Options/limit",
                    "BareBoth",
                    "BareAbstractBoth",
                    "BareIfaceExt",
                    "ExportedAbstract",
                    "Scanner",
                    "Scanner/depth",
                    "Scanner/cache",
                    "Scanner/#id",
                    "Scanner/probe()",
                    "Scanner/probe(deep: boolean)",
                    "Scanner/probe(deep?: boolean)",
                    "Scanner/reset()",
                    "Scanner/reset(n: number)",
                    "Scanner/reset(n?: number)",
                    "Scanner/#clear()",
                    "Scanner/#clear(n: number)",
                    "Scanner/#clear(n?: number)",
                    "Scanner/legacy",
                    "Scanner/size",
                    "scan",
                ],
                ExpectedInherits:
                [
                    "BareAbstractBoth implements AbsIface",
                    "BareAbstractBoth inherits AbsBase",
                    "BareBoth implements BareIface",
                    "BareBoth inherits BareBase",
                    "BareIfaceExt inherits BareIface",
                    "ExportedAbstract implements AbsIface",
                    "ExportedAbstract inherits AbsBase",
                    "Options inherits BaseOptions",
                    "Scanner implements Trackable",
                    "Scanner inherits ScannerBase",
                ]),
            Grammars:
            [
                new(Library: "typescript", Symbol: "tree_sitter_typescript", Extensions: [".ts"]),
                new(Library: "tsx", Symbol: "tree_sitter_tsx", Extensions: [".tsx"]),
            ],
            DeclarationQuery: TypeScriptDeclarations,
            ImportQuery: ScriptImports,
            CallQuery: TypeScriptCalls,
            InheritancePredicate: InheritancePredicate.Split,
            InheritanceQuery: TypeScriptInheritance,
            NestedTypeEdgesDropped: true),
        new(
            Id: "javascript",
            DisplayName: "JavaScript",
            Extensions: [".js", ".jsx", ".mjs", ".cjs"],
            Tier: 1,
            DeclarationPatterns:
            [
                @"^\s*export[ ]+(?:default[ ]+)?(?:async[ ]+)?(?:function\*?|class|const|let|var)[ ]+(?<name>[A-Za-z_$][\w$]*)",
            ],
            ImportPatterns:
            [
                @"^\s*import\b[^'""]*['""](?<module>[^'""]+)['""]",
                @"\brequire\(\s*['""](?<module>[^'""]+)['""]",
            ],
            DocHeadings: false,
            Fixture: new(
                Source: """
                    import path from "path";
                    const local = require("./local");

                    class BareWorker extends BareBase {}

                    export class Runner extends RunnerBase {
                        limit = 10;
                        #secret = "";
                        run() {}
                        run(times) {}
                    }
                    export default function main() {}
                    """,
                ExpectedSymbols: ["Runner", "main"],
                ExpectedImports: ["path", "./local"],
                ExpectedDeepSymbols:
                [
                    "BareWorker",
                    "Runner",
                    "Runner/limit",
                    "Runner/#secret",
                    "Runner/run()",
                    "Runner/run(times)",
                    "main",
                ],
                ExpectedInherits:
                [
                    "BareWorker inherits BareBase",
                    "Runner inherits RunnerBase",
                ]),
            Grammars: [new(Library: "javascript", Symbol: "tree_sitter_javascript", Extensions: [])],
            DeclarationQuery: JavaScriptDeclarations,
            ImportQuery: ScriptImports,
            CallQuery: JavaScriptCalls,
            InheritancePredicate: InheritancePredicate.Split,
            InheritanceQuery: JavaScriptInheritance,
            NestedTypeEdgesDropped: true),
        new(
            Id: "markdown",
            DisplayName: "Markdown",
            Extensions: [".md", ".markdown"],
            Tier: 0,
            DeclarationPatterns: [],
            ImportPatterns: [],
            DocHeadings: true,
            Fixture: new(
                Source: """
                    # Widget manual

                    Widgets turn cranks into torque.

                    ## Install

                    Run the installer with defaults.
                    """,
                ExpectedSymbols: ["widget-manual/install"],
                ExpectedImports: [])),
    ];

    /// <summary>Resolves by extension; every unclaimed file is <see cref="Text"/>.</summary>
    public static LanguageDefinition Resolve(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (extension.Length == 0)
        {
            return Text;
        }

        foreach (var language in All)
        {
            foreach (var candidate in language.Extensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return language;
                }
            }
        }

        return Text;
    }

    /// <summary>
    /// Patterns compile once per process. Runtime <see cref="Regex"/> construction stays
    /// AOT-safe because these are interpreted, never source-generated — the price of
    /// patterns being registry data instead of code.
    /// </summary>
    public static Regex Compiled(string pattern) =>
        Cache.GetOrAdd(pattern, static p => new Regex(p, RegexOptions.Multiline | RegexOptions.CultureInvariant));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Cache = new();
}
