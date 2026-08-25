using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Acceptance tests for code-navigation Phase 4 (docs/code-navigation-phase4-spec.md §9), the
/// trust-surface half: extraction-tier rendering, telemetry, non-code writes, and the
/// observation-licensed fill/upgrade write path. Seeds facts directly at a chosen
/// <c>analyzer_tier</c>, the same way CodeNavigationPhase3Tests proves query-layer behavior
/// without a live extractor — real extraction reaching the column is covered separately in
/// RoslynSidecarTests (C#) and CodeNavigationPhase3Tests (tree-sitter), item 7.
/// </summary>
public sealed class CodeNavigationPhase4Tests
{
    private static readonly McpSessionId Session = new("test-session");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    // item 8: all rows share one extraction tier, so the header states it once and no row
    // carries its own bracket. Falsify by dropping the `uniformNote is null` guard around the
    // per-row append (always append): this reddens on the DoesNotContain assertion.
    [Fact]
    public void DefinedAt_UniformExtractionTier_StatesItOnceInTheHeader_NoPerRowMarkers()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedDeclared(connection, "/projects/p/code/a/a.cs#Widget", "Widget", "public sealed class Widget", tier: 2);
        SeedDeclared(connection, "/projects/p/code/b/b.cs#Widget", "Widget", "internal class Widget", tier: 2);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("(extraction tier: semantic)", result);
        Assert.DoesNotContain("[semantic]", result);
    }

    // item 9: rows disagree, so each is marked and the header says nothing — both halves in one
    // test, since item 8 alone (asserting only the header) would pass with per-row marking
    // hardcoded off.
    [Fact]
    public void DefinedAt_MixedExtractionTiers_MarksPerRow_AndDropsTheHeaderNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedDeclared(connection, "/projects/p/code/a/a.cs#Widget", "Widget", "public sealed class Widget", tier: 2);
        SeedDeclared(connection, "/projects/p/code/b/b.ts#Widget", "Widget", "class Widget", tier: 0);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.DoesNotContain("extraction tier:", result);
        Assert.Contains("[semantic]", result);
        Assert.Contains("[regex]", result);
    }

    // item 10 / §5.2: NULL never renders as tier 0 — a pre-v14 (NULL) fact says "not recorded",
    // a tier-0 fact says "regex", and the two strings must never collapse. Falsify by coalescing
    // NULL to 0 before rendering (e.g. `tier ?? 0` ahead of the switch).
    [Fact]
    public void DefinedAt_UnrecordedExtractionTier_RendersDistinctlyFromRegex()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedDeclared(connection, "/projects/p/code/a/a.cs#Widget", "Widget", "public sealed class Widget", tier: null);
        SeedDeclared(connection, "/projects/p/code/b/b.ts#Widget", "Widget", "class Widget", tier: 0);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("[not recorded]", result);
        Assert.Contains("[regex]", result);
    }

    // item 11: the retired placeholder header and its append sites are gone from the source —
    // a text guard, since the string can no longer appear in any response once the identifier
    // does not exist. Falsify by reintroducing an `ExtractionTierUnrecordedHeader` constant.
    [Fact]
    public void RetiredExtractionTierUnrecordedHeader_NoLongerExistsInSource()
    {
        var srcRoot = FindSrcRoot();
        var hits = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("ExtractionTierUnrecordedHeader", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(hits);
    }

    // item 12 / D43: telemetry keeps match-tier and extraction-tier in separate fields, never
    // folded into one — falsify by writing ExtractionTiers into the existing Tiers field (or
    // vice versa) at the Telemetry.Append call site.
    [Fact]
    public void Navigate_TelemetryCarriesExtractionTierSeparatelyFromMatchTier()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedDeclared(connection, "/projects/p/code/a/a.cs#Widget", "Widget", "public sealed class Widget", tier: 2);

        EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        var record = File.ReadAllLines(Telemetry.ResolvePath(sandbox.Home))
            .Select(Telemetry.TryParse)
            .Single(r => r?.Kind == TelemetryEventKind.Navigate);

        // Tiers holds the capitalized SymbolMatchTier name; ExtractionTiers holds the lowercase,
        // hand-written label — two independent spaces, so neither can equal the other by coincidence.
        Assert.NotNull(record!.Tiers);
        Assert.DoesNotContain("semantic", record.Tiers, StringComparison.Ordinal);
        Assert.Equal("semantic", record.ExtractionTiers);
        Assert.NotEqual(record.Tiers, record.ExtractionTiers);
    }

    // item 14: a non-code write (engram_remember's own path, FactStore.Remember with no
    // AnalyzerTier) leaves analyzer_tier NULL, never 0 — falsify by defaulting FactWrite's
    // AnalyzerTier to 0 instead of null.
    [Fact]
    public void ARememberedNonCodeFact_HasAnalyzerTierNull_NeverZero()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var id = FactStore.Remember(
            connection,
            new FactWrite("/people/jim/preferences", "preference", "prefers", "terse reports", "user", "stated"),
            T0).FactId;

        Assert.Null(FactStore.ReadById(connection, id)!.AnalyzerTier);
    }

    // item 15a/b/c: the observation write fills a NULL tier, upgrades a shallower one, and
    // never downgrades a deeper one — all three against the shared UpgradeAnalyzerTier
    // primitive, with no new fact rows and no supersession in any case.
    [Fact]
    public void UpgradeAnalyzerTier_FillsANullTier()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = SeedDeclared(connection, "/projects/p/code/a/a.cs#Widget", "Widget", "class Widget", tier: null);
        var liveCountBefore = FactStore.ReadLive(connection).Count;

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactStore.UpgradeAnalyzerTier(connection, transaction, id, 1);
            transaction.Commit();
        }

        var fact = FactStore.ReadById(connection, id)!;
        Assert.Equal(1, fact.AnalyzerTier);
        Assert.Null(fact.SupersededBy);
        Assert.Equal(liveCountBefore, FactStore.ReadLive(connection).Count);
    }

    [Fact]
    public void UpgradeAnalyzerTier_RaisesAShallowerTierToADeeperOne()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = SeedDeclared(connection, "/projects/p/code/a/a.cs#Widget", "Widget", "class Widget", tier: 0);
        var liveCountBefore = FactStore.ReadLive(connection).Count;

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactStore.UpgradeAnalyzerTier(connection, transaction, id, 2);
            transaction.Commit();
        }

        var fact = FactStore.ReadById(connection, id)!;
        Assert.Equal(2, fact.AnalyzerTier);
        Assert.Null(fact.SupersededBy);
        Assert.Equal(liveCountBefore, FactStore.ReadLive(connection).Count);
    }

    /// <summary>
    /// The load-bearing assertion of item 15: fill and upgrade both pass under an unrestricted
    /// <c>UPDATE</c> with no WHERE guard at all, so only this direction — re-observing at a
    /// shallower tier than what is already recorded — proves the monotone predicate is doing
    /// anything. Falsify by dropping the <c>analyzer_tier IS NULL OR analyzer_tier &lt; $tier</c>
    /// clause from <see cref="FactStore.UpgradeAnalyzerTier"/>'s WHERE clause: this test reddens
    /// (tier moves from 2 to 1) while the two tests above stay green, confirming the predicate,
    /// not the two easier directions, is what this guards.
    /// </summary>
    [Fact]
    public void UpgradeAnalyzerTier_NeverDowngradesADeeperTier()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = SeedDeclared(connection, "/projects/p/code/a/a.cs#Widget", "Widget", "class Widget", tier: 2);
        var liveCountBefore = FactStore.ReadLive(connection).Count;

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactStore.UpgradeAnalyzerTier(connection, transaction, id, 1);
            transaction.Commit();
        }

        var fact = FactStore.ReadById(connection, id)!;
        Assert.Equal(2, fact.AnalyzerTier);
        Assert.Null(fact.SupersededBy);
        Assert.Equal(liveCountBefore, FactStore.ReadLive(connection).Count);
    }

    private static long SeedDeclared(
        SqliteConnection connection, string path, string name, string declaration, int? tier) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "symbol", "declared-as", declaration, "code", "observed",
                Regenerable: true, AnalyzerTier: tier),
            T0).FactId;

    private static string FindSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "engram-schema.sql")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src");
    }
}
