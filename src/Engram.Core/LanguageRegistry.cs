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

/// <summary>One language: what it is called, which files are its, and what tier-0 can extract.</summary>
/// <remarks>
/// <para><see cref="Tier"/> declares the deepest analysis this language is entitled to
/// (D24: 0 universal, 1 tree-sitter, 2 semantic sidecar). Every row currently executes on
/// the tier-0 mechanism; the column exists from day one so tiers 1 and 2 plug in behind
/// the same rows instead of behind a second table.</para>
///
/// <para>Patterns are data, not code: each declaration pattern must expose a
/// <c>name</c> group, each import pattern a <c>module</c> group. They are deliberately
/// conservative — top-level declarations and imports, nothing an honest line-level read
/// cannot see. The deep tier corrects and extends by adopt/merge (D2), never by this row
/// growing cleverer.</para>
/// </remarks>
public sealed record LanguageDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    int Tier,
    IReadOnlyList<string> DeclarationPatterns,
    IReadOnlyList<string> ImportPatterns,
    bool DocHeadings,
    LanguageFixture? Fixture);

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
                ExpectedImports: ["node:fs", "./config"])),
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
                ExpectedImports: ["path", "./local"])),
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
