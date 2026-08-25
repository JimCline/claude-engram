using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Drives the real engram-roslyn binary — the ProjectReference builds it, so these tests
/// exercise the process boundary the production indexer crosses, not an in-process
/// approximation of it.
/// </summary>
public class RoslynSidecarTests
{
    private const string WidgetCs =
        """
        using System.Text;
        using static System.Math;

        namespace Demo;

        /// <summary>Holds <see cref="StringBuilder"/> widgets for the demo.</summary>
        public sealed class Widget {
            public sealed class Inner { }

            public StringBuilder Buffer { get; } = new();

            private int count;
        }

        public interface IWidget { }
        """;

    private const string ColorCs =
        """
        namespace Demo;

        public enum Color { Red, Green }
        """;

    // Same-line brace on purpose: tier 0 keeps the whole line, Roslyn stops before the
    // brace, so the two tiers disagree about declared-as and Merge must pick Roslyn's.
    private const string BraceStyleCs =
        """
        // Widgets for the demo.
        using System.Text;
        using static System.Math;

        namespace Demo;

        public sealed class Widget {
        }
        """;

    // Brace on its own line and no doc comment: tier 0 and tier 2 describe this file
    // identically, which is what makes a tier swap that rewrites anything a defect. Two
    // imports on purpose — a one-element join reads identically under any separator, so
    // one import cannot catch a format drift.
    private const string ParityCs =
        """
        // Parity fixture for the tier swap.
        using System.Collections;
        using System.Text;

        namespace Demo;

        public sealed class Parity
        {
        }
        """;

    [Fact]
    public void Locate_HonorsTheOverride_AndAStaleOverrideMeansNoSidecar()
    {
        var real = SidecarBinary();

        Assert.Equal(real, RoslynSidecar.Locate(_ => real));

        // An explicit override that points nowhere is a broken configuration, not a
        // request to fall back to whatever sits beside the binary.
        Assert.Null(RoslynSidecar.Locate(_ => Path.Combine(Path.GetTempPath(), "no-such-sidecar")));

        // The ProjectReference copies the sidecar into this test's output directory —
        // production's layout exactly — so the no-override probe finds it there.
        var beside = RoslynSidecar.Locate(_ => null);
        Assert.NotNull(beside);
        Assert.StartsWith(AppContext.BaseDirectory, beside, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ReadsSymbolsDocsAndImports_ForAWholeBatch()
    {
        var results = RoslynSidecar.Analyze(
            SidecarBinary(),
            [("Widget.cs", WidgetCs), ("Color.cs", ColorCs)],
            TimeSpan.FromSeconds(30));

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);

        var widget = results["Widget.cs"];
        Assert.Null(widget.Error);
        Assert.Equal(["Widget", "Buffer", "Inner", "IWidget"], widget.Symbols.Select(s => s.Name));

        // Surface only (D48): a bare private member is implementation, never emitted.
        Assert.DoesNotContain(widget.Symbols, s => s.Name == "count");

        var declared = widget.Symbols.Single(s => s.Name == "Widget");
        Assert.Equal("class", declared.Kind);
        Assert.Null(declared.Scope);
        Assert.Equal("public sealed class Widget", declared.Declaration);
        Assert.Equal("Holds widgets for the demo.", declared.Doc);

        var nested = widget.Symbols.Single(s => s.Name == "Inner");
        Assert.Equal("class", nested.Kind);
        Assert.Equal("Widget", nested.Scope);

        var property = widget.Symbols.Single(s => s.Name == "Buffer");
        Assert.Equal("property", property.Kind);
        Assert.Equal("Widget", property.Scope);
        Assert.Equal("public StringBuilder Buffer { get; } = new();", property.Declaration);

        Assert.Equal("interface", widget.Symbols.Single(s => s.Name == "IWidget").Kind);
        Assert.Null(widget.Symbols.Single(s => s.Name == "IWidget").Doc);
        Assert.Equal(["System.Math", "System.Text"], widget.Imports);

        var color = results["Color.cs"];
        Assert.Equal("enum", color.Symbols.Single().Kind);
        Assert.Equal("public enum Color", color.Symbols.Single().Declaration);
        Assert.Empty(color.Imports);
    }

    [Fact]
    public void Analyze_KillsAHungSidecar_AndAnswersNull()
    {
        // A real platform guard, not Assert.SkipWhen — CA1416 cannot see through the
        // latter to know SetUnixFileMode is unreachable on Windows.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the hang fixture is a shell script");
            return;
        }

        var script = Path.Combine(Path.GetTempPath(), $"engram-hang-{Guid.NewGuid():N}.sh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 60\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var results = RoslynSidecar.Analyze(
                script, [("a.cs", "class A { }")], TimeSpan.FromMilliseconds(500));

            Assert.Null(results);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void Merge_KeepsTheTierZeroImpression_AndTheImportsBody_ByteForByte()
    {
        var filePath = "/projects/demo/code/repo/src/Widget.cs";
        var language = LanguageRegistry.Resolve("Widget.cs");
        var tierZero = CodeAnalyzer.Analyze(filePath, BraceStyleCs, language);
        var analysis = Analyze(filePath: "Widget.cs", BraceStyleCs);

        var merged = DeepTier.Merge(filePath, tierZero, analysis);

        var tierZeroAbout = tierZero.Single(c => c.Predicate == "about" && c.EntityPath == filePath);
        Assert.Equal(tierZeroAbout.Body, merged.Single(c => c.Predicate == "about" && c.EntityPath == filePath).Body);

        // The imports facts must not move on a tier swap: same addresses, same bodies. Assert
        // Object is non-null on both sides first — the tuple comparison below passes if both
        // sides regressed to null together, which would not catch DeepTier.Merge dropping
        // Object entirely.
        var tierZeroImports = tierZero.Where(c => c.Predicate == "imports").ToList();
        var mergedImports = merged.Where(c => c.Predicate == "imports").ToList();
        Assert.All(tierZeroImports, c => Assert.NotNull(c.Object));
        Assert.All(mergedImports, c => Assert.NotNull(c.Object));
        Assert.Equal(
            tierZeroImports.Select(c => (c.EntityPath, c.Object, c.Body)).OrderBy(t => t.Object, StringComparer.Ordinal),
            mergedImports.Select(c => (c.EntityPath, c.Object, c.Body)).OrderBy(t => t.Object, StringComparer.Ordinal));

        // declared-as legitimately improves — Roslyn drops the brace tier 0 kept — but at
        // the same address, so the entity keeps its history.
        var tierZeroDeclared = tierZero.Single(c => c.Predicate == "declared-as");
        var mergedDeclared = merged.Single(c => c.Predicate == "declared-as");
        Assert.Equal(tierZeroDeclared.EntityPath, mergedDeclared.EntityPath);
        Assert.NotEqual(tierZeroDeclared.Body, mergedDeclared.Body);
        Assert.Equal("public sealed class Widget", mergedDeclared.Body);
    }

    // §10 item 8, DeepTier.Merge's own imports emission site (the second of §5.6's two) —
    // CodeAnalyzer.Analyze's candidates are guarded separately in CodeNavigationPhase2Tests.
    [Fact]
    public void Merge_EveryImportsCandidate_CarriesANonNullObject_StaticGuard()
    {
        var filePath = "/projects/demo/code/repo/src/Widget.cs";
        var tierZero = CodeAnalyzer.Analyze(filePath, BraceStyleCs, LanguageRegistry.Resolve("Widget.cs"));
        var analysis = Analyze(filePath: "Widget.cs", BraceStyleCs);

        var merged = DeepTier.Merge(filePath, tierZero, analysis);
        var imports = merged.Where(c => c.Predicate == "imports").ToList();

        Assert.NotEmpty(imports);
        Assert.All(imports, c => Assert.NotNull(c.Object));
    }

    // §10 item 7: a `calls` candidate survives DeepTier.Merge. No grammar needed — a
    // hand-built DeepCall exercises Merge's calls-emit path directly (§5.4).
    [Fact]
    public void Merge_EmitsACallsCandidateForARealCall()
    {
        var filePath = "/projects/demo/code/repo/src/Widget.cs";
        var tierZero = CodeAnalyzer.Analyze(filePath, BraceStyleCs, LanguageRegistry.Resolve("Widget.cs"));
        var analysis = new DeepAnalysis("Widget.cs", [], [], null, [new DeepCall("Outer", "Inner", 3)], Tier: 2);

        var merged = DeepTier.Merge(filePath, tierZero, analysis);

        var call = Assert.Single(merged, c => c.Predicate == "calls");
        Assert.Equal(CodePaths.ForSymbol(filePath, "Outer"), call.EntityPath);
        Assert.Equal("symbol", call.Kind);
        Assert.Equal("Inner", call.Object);
    }

    // §10 item 4 / §5.2.1: a call with no enclosing declaration attributes to the file
    // entity, kind `file` — never dropped.
    [Fact]
    public void Merge_AttributesAModuleLevelCall_ToTheFileEntity()
    {
        var filePath = "/projects/demo/code/repo/src/Widget.cs";
        var tierZero = CodeAnalyzer.Analyze(filePath, BraceStyleCs, LanguageRegistry.Resolve("Widget.cs"));
        var analysis = new DeepAnalysis("Widget.cs", [], [], null, [new DeepCall(null, "configure", 1)], Tier: 2);

        var merged = DeepTier.Merge(filePath, tierZero, analysis);

        var call = Assert.Single(merged, c => c.Predicate == "calls");
        Assert.Equal(filePath, call.EntityPath);
        Assert.Equal("file", call.Kind);
        Assert.Equal("configure", call.Object);
    }

    // §10 item 3 / §5.5: three calls to one target from one function collapse to one fact.
    [Fact]
    public void Merge_DeduplicatesRepeatedCallsToOneTarget_ToASingleCandidate()
    {
        var filePath = "/projects/demo/code/repo/src/Widget.cs";
        var tierZero = CodeAnalyzer.Analyze(filePath, BraceStyleCs, LanguageRegistry.Resolve("Widget.cs"));
        var analysis = new DeepAnalysis(
            "Widget.cs", [], [], null,
            [new DeepCall("Outer", "Inner", 5), new DeepCall("Outer", "Inner", 2), new DeepCall("Outer", "Inner", 9)],
            Tier: 2);

        var merged = DeepTier.Merge(filePath, tierZero, analysis);

        Assert.Single(merged, c => c.Predicate == "calls");
    }

    // §10 item 8: one unparseable sibling in the same batch does not cost the good file its
    // calls — the sidecar's per-line try/catch (Program.cs's main loop) isolates failures.
    [Fact]
    public void Analyze_OneBadFileInABatch_StillYieldsTheGoodFilesCalls()
    {
        const string good = "namespace Demo; public class Good { public void Outer() { Inner(); } public void Inner() { } }";

        var results = RoslynSidecar.Analyze(
            SidecarBinary(),
            [("Good.cs", good), ("Bad.cs", "this is not valid C# {{{")],
            TimeSpan.FromSeconds(30));

        Assert.NotNull(results);
        var goodResult = results["Good.cs"];
        Assert.Null(goodResult.Error);
        Assert.Contains(goodResult.Calls, c => c.Callee == "Inner");
    }

    [Fact]
    public void Merge_OnAPerFileError_KeepsTierZeroWholesale()
    {
        var filePath = "/projects/demo/code/repo/src/Widget.cs";
        var tierZero = CodeAnalyzer.Analyze(filePath, BraceStyleCs, LanguageRegistry.Resolve("Widget.cs"));

        var merged = DeepTier.Merge(
            filePath, tierZero, new DeepAnalysis("Widget.cs", [], [], "did not parse", [], Tier: 2));

        Assert.Equal(tierZero, merged);
    }

    [Fact]
    public void IndexingAcrossTiers_RewritesNothing_WhenTheFileReadsTheSameToBoth()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "parity-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Parity.cs"), ParityCs);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var first = Index(connection, sandbox, repo, sidecarPath: null, full: false);
        Assert.True(first.FactsWritten >= 2);

        var second = Index(connection, sandbox, repo, sidecarPath: SidecarBinary(), full: true);

        Assert.Contains(second.Notes, note => note.StartsWith("tier 2:", StringComparison.Ordinal));
        Assert.Equal(0, second.FactsWritten);
        Assert.Equal(0, second.FactsClosed);
        Assert.True(second.FactsUnchanged >= 2);
    }

    [Fact]
    public void IndexWithSidecar_WritesDocFacts_AndDropsWhatTierZeroMisread()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "widget-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Widget.cs"), WidgetCs);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var report = Index(connection, sandbox, repo, sidecarPath: SidecarBinary(), full: false);
        Assert.Contains(report.Notes, note => note.StartsWith("tier 2:", StringComparison.Ordinal));

        var facts = FactStore.ReadLive(connection);
        var widgetPath = CodePaths.ForSymbol(CodePaths.ForFile(report.RepoPath, "Widget.cs"), "Widget");

        Assert.Equal(
            "public sealed class Widget",
            facts.Single(f => f.SubjectPath == widgetPath && f.Predicate == "declared-as").Body);
        Assert.Equal(
            "Holds widgets for the demo.",
            facts.Single(f => f.SubjectPath == widgetPath && f.Predicate == "about").Body);

        // Tier 0's file-scoped-namespace blind spot: its 0–4 space indent window reads a
        // nested type as top-level. The sidecar sees the nesting, so the fact lands under
        // the scope chain (grammar v2, D48), never at the bare name tier 0 would have used.
        var innerPath = CodePaths.ForSymbol(CodePaths.ForFile(report.RepoPath, "Widget.cs"), "Inner");
        Assert.DoesNotContain(facts, f => f.SubjectPath == innerPath);
        var nestedPath = CodePaths.ForSymbol(CodePaths.ForFile(report.RepoPath, "Widget.cs"), "Widget/Inner");
        Assert.Contains(facts, f => f.SubjectPath == nestedPath && f.Predicate == "declared-as");
    }

    // code-navigation Phase 4 spec §9 item 7 (C# half): a Roslyn-sidecar-indexed C# file's
    // facts read analyzer_tier = 2 in the database.
    [Fact]
    public void IndexWithSidecar_StampsAnalyzerTierTwo_OnTheDatabaseRow()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "widget-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Widget.cs"), WidgetCs);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var report = Index(connection, sandbox, repo, sidecarPath: SidecarBinary(), full: false);
        var widgetPath = CodePaths.ForSymbol(CodePaths.ForFile(report.RepoPath, "Widget.cs"), "Widget");

        var declared = FactStore.ReadLive(connection)
            .Single(f => f.SubjectPath == widgetPath && f.Predicate == "declared-as");
        Assert.Equal(2, declared.AnalyzerTier);
    }

    [Fact]
    public void Locate_FindsTheInstalledLayout_AndPrefersTheFileBesideTheBinary()
    {
        var root = Directory.CreateTempSubdirectory("engram-locate-").FullName;
        try
        {
            Assert.Null(RoslynSidecar.Locate(_ => null, root));

            var name = OperatingSystem.IsWindows() ? "engram-roslyn.exe" : "engram-roslyn";
            Directory.CreateDirectory(Path.Combine(root, "roslyn"));
            var installed = Path.Combine(root, "roslyn", name);
            File.WriteAllText(installed, "stub");
            Assert.Equal(installed, RoslynSidecar.Locate(_ => null, root));

            // Beside the binary outranks the installed layout: the closer placement is
            // the more deliberate one.
            var beside = Path.Combine(root, name);
            File.WriteAllText(beside, "stub");
            Assert.Equal(beside, RoslynSidecar.Locate(_ => null, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Doctor_CodeAnalysisRow_TreatsAbsenceAsAChoiceAndALyingOverrideAsAFault()
    {
        var tierTwo = Diagnostics.CheckRoslyn(_ => SidecarBinary());
        Assert.Equal(DiagnosisState.Ok, tierTwo.State);
        Assert.Contains("tier 2", tierTwo.Detail, StringComparison.Ordinal);

        var lying = Diagnostics.CheckRoslyn(_ => Path.Combine(Path.GetTempPath(), "no-such-sidecar"));
        Assert.Equal(DiagnosisState.Broken, lying.State);

        var empty = Directory.CreateTempSubdirectory("engram-doctor-").FullName;
        try
        {
            var tierZero = Diagnostics.CheckRoslyn(_ => null, empty);
            Assert.Equal(DiagnosisState.Ok, tierZero.State);
            Assert.Contains("tier 0", tierZero.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private static DeepAnalysis Analyze(string filePath, string content)
    {
        var results = RoslynSidecar.Analyze(
            SidecarBinary(), [(filePath, content)], TimeSpan.FromSeconds(30));

        Assert.NotNull(results);
        return results[filePath];
    }

    private static IndexReport Index(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        SandboxHome sandbox,
        string repo,
        string? sidecarPath,
        bool full)
        => CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: full, SidecarPath: sidecarPath),
            DateTimeOffset.UtcNow);

    // D-code-nav B1/item 24: a ProjectReference back to Engram.Core drags LLamaSharp's
    // per-RID native payload into this publish target (D45), the exact trap the framework-
    // dependent, no-RID setup above exists to avoid. Falsify by restoring the reference.
    [Fact]
    public void SidecarProject_HasNoProjectReferenceToEngramCore()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Engram.Sidecar.Roslyn", "engram-roslyn.csproj"));
        Assert.DoesNotContain("<ProjectReference", text, StringComparison.Ordinal);
    }

    // Microsoft.CodeAnalysis.CSharp's own RID-agnostic build already scaffolds an empty
    // runtimes/<rid>/native/ tree with zero bytes in it, unrelated to D45 — so the guard
    // checks for an actual native payload, not the directory's mere existence.
    [Fact]
    public void SidecarBuildOutput_CarriesNoLlamaNativePayload()
    {
        var dir = Path.GetDirectoryName(SidecarBinary())!;
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();

        Assert.DoesNotContain(files, f =>
            Path.GetFileName(f).Contains("LLamaSharp", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(f).Contains("libllama", StringComparison.OrdinalIgnoreCase));

        var runtimesDir = Path.Combine(dir, "runtimes");
        Assert.False(
            Directory.Exists(runtimesDir) && Directory.EnumerateFiles(runtimesDir, "*", SearchOption.AllDirectories).Any());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "engram-schema.sql")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// The test csproj's ProjectReference builds the sidecar, so absence here is broken
    /// wiring, not a configuration — it fails rather than skips.
    /// </summary>
    private static string SidecarBinary()
    {
        // Anchored on a case-exact file, not the solution: "engram.sln" would resolve on
        // this machine's case-insensitive filesystem and never on Linux ("Engram.sln").
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "engram-schema.sql")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var name = OperatingSystem.IsWindows() ? "engram-roslyn.exe" : "engram-roslyn";
        var configurations = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Release", "Debug" }
            : ["Debug", "Release"];

        foreach (var configuration in configurations)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "Engram.Sidecar.Roslyn", "bin", configuration, "net10.0", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail("engram-roslyn is not built; the ProjectReference in this test project should have built it");
        return null!;
    }
}
