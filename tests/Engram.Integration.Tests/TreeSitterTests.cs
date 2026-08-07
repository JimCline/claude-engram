using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Drives the real tree-sitter core and grammars (D47) when
/// <c>ENGRAM_TEST_TREE_SITTER_DIR</c> points at a directory holding them —
/// <c>fetch-tree-sitter.sh</c> produces exactly that layout. Environment-gated like the
/// vector tests, because a guard that never runs is worth nothing; the registry-shape and
/// downgrade tests below run everywhere.
/// </summary>
public class TreeSitterTests
{
    private static string? GrammarDir()
    {
        var dir = Environment.GetEnvironmentVariable("ENGRAM_TEST_TREE_SITTER_DIR");
        return dir is { Length: > 0 } && File.Exists(Path.Combine(dir, TreeSitter.CoreLibraryFile))
            ? dir
            : null;
    }

    /// <summary>
    /// The conformance walk D24 demands: every tier-1 row's fixture, through every grammar
    /// the row names, extracting exactly what the row promises. A query that the grammar
    /// refuses surfaces here as a downgrade note in the failure message — which is the
    /// guard against a registry query nobody ever ran.
    /// </summary>
    [Fact]
    public void EveryTier1Row_ExtractsItsFixture_ThroughEveryGrammarItNames()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        using var runtime = TreeSitter.TryCreate(dir!, []);
        Assert.NotNull(runtime);

        foreach (var language in LanguageRegistry.All.Where(l => l.Grammars is not null))
        {
            foreach (var grammar in language.Grammars!)
            {
                var extension = grammar.Extensions.Count > 0 ? grammar.Extensions[0] : language.Extensions[0];
                var analysis = runtime.Analyze(language, "fixture" + extension, language.Fixture!.Source);

                Assert.True(
                    analysis is not null,
                    $"{language.Id}{extension}: {string.Join("; ", runtime.Downgrades)}");

                // Compared as grammar-v2 fragments (D48), composed by the same DeepTier
                // routine the indexer uses — the fixture's expectations pin scope chains
                // and overload suffixes, not just names.
                Assert.Equal(
                    (language.Fixture.ExpectedDeepSymbols ?? language.Fixture.ExpectedSymbols).Order(),
                    DeepTier.Fragments(analysis!.Symbols).Select(f => f.Fragment).Order());
                Assert.Equal(
                    language.Fixture.ExpectedImports.Order(),
                    analysis.Imports.Order());
            }
        }

        Assert.Empty(runtime.Downgrades);
    }

    /// <summary>
    /// Registry shape, no native code involved: a tier-1 row missing any of its tier-1
    /// columns would silently index at tier 0 forever, and grammars on a row of another
    /// tier would be decoration nothing executes.
    /// </summary>
    [Fact]
    public void TheRegistry_CarriesTier1Data_ExactlyOnTier1Rows()
    {
        foreach (var language in LanguageRegistry.All)
        {
            if (language.Tier == 1)
            {
                Assert.NotNull(language.Grammars);
                Assert.NotEmpty(language.Grammars);
                Assert.NotNull(language.DeclarationQuery);
                Assert.NotNull(language.ImportQuery);
                Assert.NotNull(language.Fixture);

                foreach (var extension in language.Extensions)
                {
                    Assert.True(
                        language.GrammarFor(extension) is not null,
                        $"{language.Id}: no grammar claims {extension}");
                }
            }
            else
            {
                Assert.Null(language.Grammars);
            }
        }
    }

    // What the deep tier exists to see, on one source: declarations tier 0's export-only
    // regexes miss (non-exported interface/type/enum/abstract class/generator, a default
    // export), imports the regexes cannot reach (dynamic import), and — the predicate at
    // work — require() kept while an arbitrary one-string call is not an import.
    [Fact]
    public void TierOne_SeesWhatTierZeroCannot_AndAnArbitraryCallIsNotAnImport()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        const string source = """
            import type { Thing } from "./types";
            const lazy = await import("./lazy");
            const legacy = require("./legacy");
            const notAnImport = fetch("https://example.com");

            interface Internal { a: number }
            type Alias = string;
            enum Mode { On, Off }
            abstract class Base {}
            function* pump(): Generator<number> {}
            class Plain {}
            export default class Widget {}
            export const answer = 42;
            const notDeclared = 3;
            """;

        using var runtime = TreeSitter.TryCreate(dir!, []);
        var analysis = runtime!.Analyze(TypeScript(), "deep.ts", source);

        Assert.NotNull(analysis);
        Assert.Equal(
            new[] { "Alias", "Base", "Internal", "Internal/a", "Mode", "Plain", "Widget", "answer", "pump" },
            DeepTier.Fragments(analysis!.Symbols).Select(f => f.Fragment).Order());
        Assert.Equal(new[] { "./lazy", "./legacy", "./types" }, analysis.Imports.Order());

        // The declaration body is the full source line, found by byte offset rather than
        // guessed by regex.
        Assert.Equal(
            "export default class Widget {}",
            analysis.Symbols.Single(s => s.Name == "Widget").Declaration);
    }

    [Fact]
    public void Locate_HonorsTheOverride_AndAStaleOverrideMeansNoTierOne()
    {
        using var sandbox = new SandboxHome();
        var home = sandbox.Home;

        // Nothing installed anywhere: no tier 1.
        Assert.Null(TreeSitter.Locate(_ => null, home));

        // Locate answers from the file system alone, so a stand-in file is enough here.
        Directory.CreateDirectory(home.LibDir);
        File.WriteAllText(Path.Combine(home.LibDir, TreeSitter.CoreLibraryFile), "stand-in");
        Assert.Equal(home.LibDir, TreeSitter.Locate(_ => null, home));

        // An explicit override that points at nothing is a broken configuration, not a
        // request to fall back to the home's lib directory.
        var empty = Directory.CreateTempSubdirectory("engram-ts-empty-");
        try
        {
            Assert.Null(TreeSitter.Locate(_ => empty.FullName, home));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void ACorruptCoreLibrary_CostsTierOneWithANote_InsteadOfThrowing()
    {
        var dir = Directory.CreateTempSubdirectory("engram-ts-corrupt-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, TreeSitter.CoreLibraryFile), "not a library");

            var notes = new List<string>();
            Assert.Null(TreeSitter.TryCreate(dir.FullName, notes));
            Assert.Contains(notes, note => note.Contains("would not load"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AMissingGrammar_CostsItsLanguageOnly_AndComplainsOncePerCause()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        var language = TypeScript() with
        {
            Grammars = [new TreeSitterGrammar("no-such-grammar", "tree_sitter_none", [])],
        };

        using var runtime = TreeSitter.TryCreate(dir!, []);
        Assert.Null(runtime!.Analyze(language, "a.ts", "export const a = 1;"));
        Assert.Null(runtime.Analyze(language, "b.ts", "export const b = 2;"));

        var downgrade = Assert.Single(runtime.Downgrades);
        Assert.Contains(TreeSitter.GrammarLibraryFile("no-such-grammar"), downgrade);
    }

    [Fact]
    public void ARefusedQuery_NamesItsOffset_AndTheFileTakesTierZero()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        var language = TypeScript() with { DeclarationQuery = "(no_such_node) @name" };

        using var runtime = TreeSitter.TryCreate(dir!, []);
        Assert.Null(runtime!.Analyze(language, "a.ts", "export const a = 1;"));

        var downgrade = Assert.Single(runtime.Downgrades);
        Assert.Contains("refused at offset", downgrade);
    }

    /// <summary>
    /// The doctor row answers from file existence alone (D37): absence is a choice, a
    /// lying override is a fault, and the state only the doctor can see — core installed,
    /// grammar missing — warns, because at index time that gap is a silent tier-0 downgrade.
    /// </summary>
    [Fact]
    public void Doctor_TreeSitterRow_TreatsAbsenceAsAChoice_AndAHalfInstallAsAWarning()
    {
        using var sandbox = new SandboxHome();
        var home = sandbox.Home;

        var tierZero = Diagnostics.CheckTreeSitter(_ => null, home);
        Assert.Equal(DiagnosisState.Ok, tierZero.State);
        Assert.Contains("tier 0", tierZero.Detail, StringComparison.Ordinal);

        Directory.CreateDirectory(home.LibDir);
        File.WriteAllText(Path.Combine(home.LibDir, TreeSitter.CoreLibraryFile), "stand-in");
        var half = Diagnostics.CheckTreeSitter(_ => null, home);
        Assert.Equal(DiagnosisState.Warn, half.State);
        Assert.Contains(TreeSitter.GrammarLibraryFile("typescript"), half.Detail, StringComparison.Ordinal);

        foreach (var grammar in LanguageRegistry.All
            .Where(l => l.Grammars is not null)
            .SelectMany(l => l.Grammars!))
        {
            File.WriteAllText(
                Path.Combine(home.LibDir, TreeSitter.GrammarLibraryFile(grammar.Library)),
                "stand-in");
        }

        var tierOne = Diagnostics.CheckTreeSitter(_ => null, home);
        Assert.Equal(DiagnosisState.Ok, tierOne.State);
        Assert.Contains("tier 1", tierOne.Detail, StringComparison.Ordinal);

        var empty = Directory.CreateTempSubdirectory("engram-ts-doctor-");
        try
        {
            var lying = Diagnostics.CheckTreeSitter(_ => empty.FullName, home);
            Assert.Equal(DiagnosisState.Broken, lying.State);
            Assert.Contains(TreeSitter.EnvironmentOverride, lying.Detail, StringComparison.Ordinal);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    private static LanguageDefinition TypeScript() =>
        LanguageRegistry.All.Single(l => l.Id == "typescript");
}
