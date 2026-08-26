using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class SymbolResolverTests
{
    [Fact]
    public void Resolve_PrefersExactMatch_OverCaseInsensitiveAndSubstring()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedSymbol(connection, "/projects/p/code/r/a.cs#Widget", "Widget");
        SeedSymbol(connection, "/projects/p/code/r/b.cs#widget", "widget");
        SeedSymbol(connection, "/projects/p/code/r/c.cs#WidgetFactory", "WidgetFactory");

        var matches = SymbolResolver.Resolve(connection, "Widget", 20);

        var match = Assert.Single(matches);
        Assert.Equal("/projects/p/code/r/a.cs#Widget", match.Path);
        Assert.Equal(SymbolMatchTier.Exact, match.Tier);
    }

    [Fact]
    public void Resolve_FallsBackToCaseInsensitive_WhenNoExactMatch()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedSymbol(connection, "/projects/p/code/r/a.cs#Widget", "Widget");

        var matches = SymbolResolver.Resolve(connection, "widget", 20);

        var match = Assert.Single(matches);
        Assert.Equal(SymbolMatchTier.CaseInsensitive, match.Tier);
    }

    [Fact]
    public void Resolve_FallsBackToSubstring_WhenNoExactOrCaseInsensitiveMatch()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedSymbol(connection, "/projects/p/code/r/a.cs#WidgetFactory", "WidgetFactory");

        var matches = SymbolResolver.Resolve(connection, "Widget", 20);

        var match = Assert.Single(matches);
        Assert.Equal(SymbolMatchTier.Substring, match.Tier);
    }

    [Fact]
    public void Resolve_IgnoresNonSymbolEntities()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        SeedEntity(connection, "/projects/p/code/r/Widget.cs", "file", "Widget.cs");

        var matches = SymbolResolver.Resolve(connection, "Widget", 20);

        Assert.Empty(matches);
    }

    [Fact]
    public void Resolve_ReturnsEmpty_ForBlankName()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Empty(SymbolResolver.Resolve(connection, "  ", 20));
    }

    private static void SeedSymbol(SqliteConnection connection, string path, string name) =>
        SeedEntity(connection, path, "symbol", name);

    private static void SeedEntity(SqliteConnection connection, string path, string kind, string name)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        FactStore.EnsureEntity(connection, transaction, path, kind, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), name);
        transaction.Commit();
    }
}
