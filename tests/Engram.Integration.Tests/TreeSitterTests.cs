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
    /// Rev-3 acceptance items 22-24 (docs/code-graph-all-members-spec.md §6.6.2/§6.7): the
    /// Scanner fixture's private-keyword and `#name` overload sets each disambiguate by
    /// parameter list, not just by name. Falsify: drop <c>@params</c> from the
    /// method_signature+property_identifier pattern and the first assert reddens; drop it
    /// (or remove the method_signature+private_property_identifier pattern entirely) and
    /// the second reddens, while the first stays green — the two capture groups are
    /// independently load-bearing, not one hiding behind the other.
    /// </summary>
    [Fact]
    public void ScannerFixture_OverloadSets_DisambiguateByParameterList_ForPrivateAndHashPrivate()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        using var runtime = TreeSitter.TryCreate(dir!, []);
        var analysis = runtime!.Analyze(TypeScript(), "fixture.ts", TypeScript().Fixture!.Source);
        Assert.NotNull(analysis);
        var fragments = DeepTier.Fragments(analysis!.Symbols).Select(f => f.Fragment).ToHashSet();

        // Item 22: two `private`-keyword overloads of `reset` yield distinct addresses.
        Assert.True(
            fragments.IsSupersetOf(["Scanner/reset()", "Scanner/reset(n: number)", "Scanner/reset(n?: number)"]),
            $"expected three distinct `reset` addresses, got: {string.Join(", ", fragments.Where(f => f.StartsWith("Scanner/reset")))}");

        // Item 23/24: two `#name` overloads of `#clear` yield distinct addresses — the
        // method_signature+private_property_identifier pattern this amendment adds.
        Assert.True(
            fragments.IsSupersetOf(["Scanner/#clear()", "Scanner/#clear(n: number)", "Scanner/#clear(n?: number)"]),
            $"expected three distinct `#clear` addresses, got: {string.Join(", ", fragments.Where(f => f.StartsWith("Scanner/#clear")))}");
    }

    [Fact]
    public void AbstractClassFixture_HashPrivateOverloads_DisambiguateByParameterList()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        const string source = """
            export abstract class Repo {
                abstract fetch(): void;
                plain(): void {}
                #save(): void;
                #save(x: number): void;
                #save(x?: number): void {}
            }
            """;

        using var runtime = TreeSitter.TryCreate(dir!, []);
        var analysis = runtime!.Analyze(TypeScript(), "fixture.ts", source);
        Assert.NotNull(analysis);
        var fragments = DeepTier.Fragments(analysis!.Symbols).Select(f => f.Fragment).ToHashSet();

        // abstract-class-member-parity §5.1: method_signature + private_property_identifier
        // added to abstract_class_declaration — three distinct `#save` overload addresses.
        Assert.True(
            fragments.IsSupersetOf(["Repo/#save()", "Repo/#save(x: number)", "Repo/#save(x?: number)"]),
            $"expected three distinct `#save` addresses, got: {string.Join(", ", fragments.Where(f => f.StartsWith("Repo/#save")))}");

        // Item 3: ordinary (property_identifier) abstract-class members still resolve —
        // §5.1 added coverage, it didn't shadow anything.
        Assert.Contains("Repo/fetch", fragments);
        Assert.Contains("Repo/plain", fragments);
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

    private const string WidgetTs =
        """
        interface Widget {
            count: number;
        }
        """;

    private static IndexReport Index(
        Microsoft.Data.Sqlite.SqliteConnection connection, SandboxHome sandbox, string repo, bool full) =>
        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: full),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Tier-degradation close guard (§5 Guard 1, applied to tier 1 per §1.3 / acceptance item
    /// 2 of docs/tier-degradation-close-guard-spec.md): the identical guard tier 2 gets,
    /// proven on tree-sitter's own fallback path — load-bearing, because a fix scoped to tier
    /// 2 only ships half the defect.
    /// </summary>
    [Fact]
    public void DegradedReindex_DoesNotCloseMemberFacts_WhenGrammarsBecomeUnavailable()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "widget-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "widget.ts"), WidgetTs);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var previous = Environment.GetEnvironmentVariable(TreeSitter.EnvironmentOverride);
        var memberPath = CodePaths.ForSymbol(CodePaths.ForFile(repo, "widget.ts"), "Widget/count");
        var topPath = CodePaths.ForSymbol(CodePaths.ForFile(repo, "widget.ts"), "Widget");
        try
        {
            Environment.SetEnvironmentVariable(TreeSitter.EnvironmentOverride, dir);
            var healthy = Index(connection, sandbox, repo, full: false);
            Assert.Contains(healthy.Notes, note => note.StartsWith("tier 1:", StringComparison.Ordinal));
            Assert.Contains(FactStore.ReadLive(connection), f => f.SubjectPath == memberPath && f.Predicate == "declared-as");

            // Same unchanged tree, but this run cannot reach the grammars — a broken
            // explicit override, not absence, so it goes through TreeSitter.Locate the way a
            // real degraded environment would; that contract is unchanged by this guard.
            var brokenOverride = Path.Combine(sandbox.Home.Root, "no-such-grammars");
            Environment.SetEnvironmentVariable(TreeSitter.EnvironmentOverride, brokenOverride);
            var degraded = Index(connection, sandbox, repo, full: true);

            Assert.Contains(degraded.Notes, note =>
                note.StartsWith("tier 1: no tree-sitter grammars available", StringComparison.Ordinal));
            Assert.True(degraded.ClosesSkipped > 0);

            var facts = FactStore.ReadLive(connection);
            Assert.Contains(facts, f => f.SubjectPath == memberPath && f.Predicate == "declared-as");
            Assert.Contains(facts, f => f.SubjectPath == topPath && f.Predicate == "declared-as");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TreeSitter.EnvironmentOverride, previous);
        }
    }
}
