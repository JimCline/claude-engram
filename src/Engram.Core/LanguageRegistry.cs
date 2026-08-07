using System.Text.RegularExpressions;

namespace Engram.Core;

/// <summary>
/// A sample source and what tier-0 extraction must find in it. Carried on the registry row
/// so the conformance suite iterates rows with zero edits per language (D24): a harness
/// keeping its own fixture list per language is the same defect as a harness keeping its
/// own language list.
/// </summary>
public sealed record LanguageFixture(
    string Source,
    IReadOnlyList<string> ExpectedSymbols,
    IReadOnlyList<string> ExpectedImports);

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
/// names its symbols <c>@name</c>, every import query its sources <c>@module</c>, and a
/// capture starting with <c>_</c> exists only for a predicate. Each query is verified
/// against the compiled grammar it targets before it lands here (D47) — <c>ts_query_new</c>
/// validates node types per grammar, which is why TypeScript and JavaScript cannot share a
/// declaration query and why a stale one fails loudly instead of matching nothing.</para>
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
    string? ImportQuery = null)
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
    // implementation, not interface. Verified against the compiled grammars, where the
    // vocabularies differ: TS names classes with (type_identifier) and has interface, enum,
    // type alias and abstract class node types JS does not.
    private const string TypeScriptDeclarations = """
        (program (function_declaration name: (identifier) @name))
        (program (generator_function_declaration name: (identifier) @name))
        (program (class_declaration name: (type_identifier) @name))
        (program (abstract_class_declaration name: (type_identifier) @name))
        (program (interface_declaration name: (type_identifier) @name))
        (program (enum_declaration name: (identifier) @name))
        (program (type_alias_declaration name: (type_identifier) @name))
        (program (export_statement declaration: (function_declaration name: (identifier) @name)))
        (program (export_statement declaration: (generator_function_declaration name: (identifier) @name)))
        (program (export_statement declaration: (class_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (abstract_class_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (interface_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (enum_declaration name: (identifier) @name)))
        (program (export_statement declaration: (type_alias_declaration name: (type_identifier) @name)))
        (program (export_statement declaration: (lexical_declaration (variable_declarator name: (identifier) @name))))
        (program (export_statement declaration: (variable_declaration (variable_declarator name: (identifier) @name))))
        """;

    private const string JavaScriptDeclarations = """
        (program (function_declaration name: (identifier) @name))
        (program (generator_function_declaration name: (identifier) @name))
        (program (class_declaration name: (identifier) @name))
        (program (export_statement declaration: (function_declaration name: (identifier) @name)))
        (program (export_statement declaration: (generator_function_declaration name: (identifier) @name)))
        (program (export_statement declaration: (class_declaration name: (identifier) @name)))
        (program (export_statement declaration: (lexical_declaration (variable_declarator name: (identifier) @name))))
        (program (export_statement declaration: (variable_declaration (variable_declarator name: (identifier) @name))))
        """;

    // Shared across TS and JS: import statements, require(), and dynamic import() all use
    // node types common to both grammars. The @_fn capture exists only for the #eq?
    // predicate, which is what keeps fetch("url") from reading as an import.
    private const string ScriptImports = """
        (import_statement source: (string (string_fragment) @module))
        (call_expression function: (identifier) @_fn arguments: (arguments (string (string_fragment) @module)) (#eq? @_fn "require"))
        (call_expression function: (import) arguments: (arguments (string (string_fragment) @module)))
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
                // types are the deep tier's to key; matching them here would write paths
                // grammar v1 does not define.
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
                ExpectedImports: ["System.Text", "System.Math"])),
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

                    export interface Options { deep: boolean }
                    export async function scan(o: Options): Promise<void> {}
                    const hidden = 1;
                    """,
                ExpectedSymbols: ["Options", "scan"],
                ExpectedImports: ["node:fs", "./config"]),
            Grammars:
            [
                new(Library: "typescript", Symbol: "tree_sitter_typescript", Extensions: [".ts"]),
                new(Library: "tsx", Symbol: "tree_sitter_tsx", Extensions: [".tsx"]),
            ],
            DeclarationQuery: TypeScriptDeclarations,
            ImportQuery: ScriptImports),
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

                    export class Runner {}
                    export default function main() {}
                    """,
                ExpectedSymbols: ["Runner", "main"],
                ExpectedImports: ["path", "./local"]),
            Grammars: [new(Library: "javascript", Symbol: "tree_sitter_javascript", Extensions: [])],
            DeclarationQuery: JavaScriptDeclarations,
            ImportQuery: ScriptImports),
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
