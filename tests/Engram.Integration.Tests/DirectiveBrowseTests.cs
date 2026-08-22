using Engram.Core;

namespace Engram.Integration.Tests;

// D-9: MemoryBrowser itself is unmodified — its existing path-prefix Browse naturally lists
// /directives children. These tests are the regression guard for that claim, not a change to
// MemoryBrowser.
public class DirectiveBrowseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Browse_OnTheDirectivesRoot_ListsEveryLiveDirectiveAsAChild_WithTheSlugInItsPath()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0);
        DirectiveFacts.Add(connection, "never commit directly to main in this repo", T0.AddSeconds(1));

        var node = MemoryBrowser.Browse(connection, DirectiveFacts.Root, depth: 1);

        Assert.NotNull(node);
        Assert.Equal(2, node!.Children.Count);
        Assert.Contains(node.Children, c => c.Path.Contains("always-use-begin-immediate", StringComparison.Ordinal));
        Assert.Contains(node.Children, c => c.Path.Contains("never-commit-directly-to-main", StringComparison.Ordinal));
    }

    // Falsification for the assertion above: reverting DirectiveFacts.PathFor to a bare
    // fingerprint (dropping the slug) must make this fail, not merely look worse — proven here by
    // asserting on the slug text itself rather than just child count.
    [Fact]
    public void Browse_OnTheDirectivesRoot_ChildPathIsNotABareFingerprint()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0);

        var node = MemoryBrowser.Browse(connection, DirectiveFacts.Root, depth: 1);
        var child = Assert.Single(node!.Children);

        var leaf = child.Path[(DirectiveFacts.Root.Length + 1)..];
        Assert.True(leaf.Length > 9, $"expected a slug plus fingerprint suffix, got a bare-looking leaf: {leaf}");
        Assert.StartsWith("always-use-begin-immediate", leaf, StringComparison.Ordinal);
    }

    [Fact]
    public void Browse_OnANonDirectivePath_IsUnaffectedByDirectivesExisting()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        FactStore.Remember(
            connection,
            new FactWrite("/facts/tabs", "note", "requires", "always use spaces", "user", "stated"),
            T0);

        var before = MemoryBrowser.Browse(connection, "/facts", depth: 2);

        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0.AddSeconds(1));

        var after = MemoryBrowser.Browse(connection, "/facts", depth: 2);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.Path, after!.Path);
        Assert.Equal(before.FactsHere, after.FactsHere);
        Assert.Equal(before.FactsUnder, after.FactsUnder);
        Assert.Equal(before.ChildrenOmitted, after.ChildrenOmitted);
        Assert.Equal(before.Children.Select(c => c.Path), after.Children.Select(c => c.Path));
    }
}
