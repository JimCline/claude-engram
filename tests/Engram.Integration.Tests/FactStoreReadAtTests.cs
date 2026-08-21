using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Equivalence guard for <see cref="FactStore.ReadAt"/> (docs/memory-expansion/05b-browse-depth-bound-spec.md,
/// Change 2), which replaced <see cref="MemoryBrowser.TopFacts"/>'s <see cref="FactStore.ReadSubtree"/>-then-filter
/// with a direct index seek on <c>entity.path</c>.
/// </summary>
public class FactStoreReadAtTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadAt_MatchesTheLegacySubtreeThenFilterApproach()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var offset = 0;
        FactStore.Remember(connection, new FactWrite("/target", "note", "states", "first", "notes", "stated"), T0.AddSeconds(offset++));
        FactStore.Remember(connection, new FactWrite("/target", "note", "confirms", "second", "notes", "stated"), T0.AddSeconds(offset++));

        // A closed fact at the same path: must not appear in either the legacy filter or ReadAt.
        FactStore.Remember(connection, new FactWrite("/target", "note", "retired", "old value", "notes", "stated"), T0.AddSeconds(offset++));
        FactStore.Forget(connection, FactStore.History(connection, "/target", "retired")[0].Id, "no longer true", T0.AddSeconds(offset++));

        // A strict descendant: must not appear at "/target" under either approach — this is
        // exactly what dropping ReadAt's e.path = $exact filter would let leak in.
        FactStore.Remember(connection, new FactWrite("/target/child", "note", "states", "descendant", "notes", "stated"), T0.AddSeconds(offset++));

        // An unrelated sibling that merely shares the prefix as a string.
        FactStore.Remember(connection, new FactWrite("/target-other", "note", "states", "not a descendant", "notes", "stated"), T0.AddSeconds(offset++));

        var legacy = FactStore.ReadSubtree(connection, "/target")
            .Where(f => f.SubjectPath == "/target" && f.ValidTo is null)
            .ToList();
        var viaReadAt = FactStore.ReadAt(connection, "/target");

        Assert.Equal(2, legacy.Count);
        Assert.Equal(legacy, viaReadAt);
    }

    [Fact]
    public void ReadAt_OnAPathWithNoLiveFacts_ReturnsEmpty()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Empty(FactStore.ReadAt(connection, "/nothing/here"));
    }
}
