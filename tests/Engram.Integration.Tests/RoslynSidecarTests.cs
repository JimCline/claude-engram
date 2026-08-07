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
        Assert.Equal(["Widget", "IWidget"], widget.Symbols.Select(s => s.Name));
        Assert.DoesNotContain(widget.Symbols, s => s.Name == "Inner");

        var declared = widget.Symbols.Single(s => s.Name == "Widget");
        Assert.Equal("class", declared.Kind);
        Assert.Equal("public sealed class Widget", declared.Declaration);
        Assert.Equal("Holds widgets for the demo.", declared.Doc);

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

        // The imports fact must not move on a tier swap: same address, same body.
        var tierZeroImports = tierZero.Single(c => c.Predicate == "imports");
        var mergedImports = merged.Single(c => c.Predicate == "imports");
        Assert.Equal(tierZeroImports.EntityPath, mergedImports.EntityPath);
        Assert.Equal(tierZeroImports.Body, mergedImports.Body);

        // declared-as legitimately improves — Roslyn drops the brace tier 0 kept — but at
        // the same address, so the entity keeps its history.
        var tierZeroDeclared = tierZero.Single(c => c.Predicate == "declared-as");
        var mergedDeclared = merged.Single(c => c.Predicate == "declared-as");
        Assert.Equal(tierZeroDeclared.EntityPath, mergedDeclared.EntityPath);
        Assert.NotEqual(tierZeroDeclared.Body, mergedDeclared.Body);
        Assert.Equal("public sealed class Widget", mergedDeclared.Body);
    }

    [Fact]
    public void Merge_OnAPerFileError_KeepsTierZeroWholesale()
    {
        var filePath = "/projects/demo/code/repo/src/Widget.cs";
        var tierZero = CodeAnalyzer.Analyze(filePath, BraceStyleCs, LanguageRegistry.Resolve("Widget.cs"));

        var merged = DeepTier.Merge(
            filePath, tierZero, new DeepAnalysis("Widget.cs", [], [], "did not parse"));

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
        // nested type as top-level. The sidecar sees the nesting, so no fact lands there.
        var innerPath = CodePaths.ForSymbol(CodePaths.ForFile(report.RepoPath, "Widget.cs"), "Inner");
        Assert.DoesNotContain(facts, f => f.SubjectPath == innerPath);
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
