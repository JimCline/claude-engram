using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 (D9) guard for the root-path fix in <see cref="MemoryBrowser.Browse"/>
/// (docs/memory-expansion/05a-browse-root-fix-spec.md): before this fix, no direct child of
/// root ever matched the substr boundary check, so <c>Browse(connection, "/", depth)</c>
/// against real data always returned null.
/// </summary>
public class MemoryBrowserRootTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private static void SeedThreeFacts(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        FactStore.Remember(
            connection,
            new FactWrite("/people/jim/preferences", "note", "states", "prefers dark mode", "notes", "stated"),
            T0);
        FactStore.Remember(
            connection,
            new FactWrite("/people/ada", "note", "states", "wrote the first algorithm", "notes", "stated"),
            T0.AddSeconds(1));
        FactStore.Remember(
            connection,
            new FactWrite("/code/Auth.cs#ValidateToken", "note", "states", "checks the token signature", "notes", "stated"),
            T0.AddSeconds(2));
    }

    [Fact]
    public void Browse_AtRoot_ReturnsANonNullNodeNamedAndPathedAsRoot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedThreeFacts(connection);

        var node = MemoryBrowser.Browse(connection, "/", depth: 1);

        Assert.NotNull(node);
        Assert.Equal("/", node!.Path);
        Assert.Equal("/", node.Name);
        Assert.Equal(0, node.FactsHere);
    }

    [Fact]
    public void Browse_AtRoot_ListsExactlyTheTopLevelSegments_SortedAndFullyPathed()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedThreeFacts(connection);

        var node = MemoryBrowser.Browse(connection, "/", depth: 1)!;

        // Asserting only non-null, or only a count, is exactly the assertion a Fold-unfixed
        // build (SQL patched but Fold's own off-by-one left standing) still passes: it can
        // return a non-null root with the right child *count* built from garbage paths.
        Assert.Equal(["code", "people"], node.Children.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(["/code", "/people"], node.Children.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Browse_AtRoot_FactsUnderMatchesAnIndependentCount()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedThreeFacts(connection);

        var node = MemoryBrowser.Browse(connection, "/", depth: 1)!;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM fact WHERE valid_to IS NULL;";
        var liveFactCount = (long)command.ExecuteScalar()!;

        Assert.Equal(liveFactCount, node.FactsUnder);
    }

    [Fact]
    public void Browse_AtRoot_DepthThree_ReachesLeafSegmentsAcrossBothSlashAndHashBoundaries()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedThreeFacts(connection);

        var node = MemoryBrowser.Browse(connection, "/", depth: 3)!;

        var people = Assert.Single(node.Children, c => c.Name == "people");
        var jim = Assert.Single(people.Children, c => c.Path == "/people/jim");
        Assert.Equal(1, jim.FactsUnder);

        var code = Assert.Single(node.Children, c => c.Name == "code");
        var authCs = Assert.Single(code.Children, c => c.Path == "/code/Auth.cs");
        Assert.Contains(authCs.Children, c => c.Path == "/code/Auth.cs#ValidateToken");
    }

    [Theory]
    [InlineData("")]
    [InlineData("//")]
    public void Browse_WithAnEmptyOrDoubleSlashPath_NormalizesTheSameAsALiteralRoot(string path)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedThreeFacts(connection);

        var viaLiteralRoot = MemoryBrowser.Browse(connection, "/", depth: 1)!;
        var viaAlias = MemoryBrowser.Browse(connection, path, depth: 1)!;

        Assert.Equal(viaLiteralRoot.Path, viaAlias.Path);
        Assert.Equal(viaLiteralRoot.Children.Select(c => c.Path), viaAlias.Children.Select(c => c.Path));
    }

    [Fact]
    public void Browse_AtRoot_OnAnEmptyStore_StillReturnsNull()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var node = MemoryBrowser.Browse(connection, "/", depth: 1);

        Assert.Null(node);
    }

    [Fact]
    public void Browse_OnANonRootPath_IsUnaffectedByTheRootFix()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedThreeFacts(connection);

        var node = MemoryBrowser.Browse(connection, "/people", depth: 1)!;

        Assert.Equal("/people", node.Path);
        Assert.Equal("people", node.Name);
        Assert.Equal(["ada", "jim"], node.Children.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
    }
}
