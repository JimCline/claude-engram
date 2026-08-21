using Microsoft.Data.Sqlite;

namespace Engram.Core.Tests;

/// <summary>
/// Tier 1 (D9): <see cref="FactStore.Timeline"/> needs only a schema, no home — a plain in-memory
/// connection is enough to prove the windowing and ordering (docs/memory-expansion/05-browse-tui-spec.md).
/// </summary>
public class FactStoreTimelineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection connection;

    public FactStoreTimelineTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        EngramDatabase.EnsureSchema(connection);
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private long Remember(string subjectPath, string body, DateTimeOffset at) =>
        FactStore.Remember(connection, new FactWrite(subjectPath, "person", "said", body, "user", "stated"), at).FactId;

    [Fact]
    public void Timeline_ReturnsNeighboursFromOtherSubjects_NotJustTheAnchorsOwnThread()
    {
        // Interleaved across three distinct subjects, one second apart. A query scoped to the
        // anchor's own subject — expand history's shape (D57) — would return nothing here, since
        // every neighbour below belongs to a different subject than the anchor.
        Remember("/people/a", "a1", T0);
        Remember("/people/b", "b1", T0.AddSeconds(1));
        var anchorId = Remember("/people/c", "c1", T0.AddSeconds(2));
        Remember("/people/a", "a2", T0.AddSeconds(3));
        Remember("/people/b", "b2", T0.AddSeconds(4));

        var anchor = FactStore.ReadById(connection, anchorId)!;
        var (before, after) = FactStore.Timeline(connection, anchor, before: 5, after: 5);

        Assert.Equal(["a1", "b1"], before.Select(f => f.Body));
        Assert.Equal(["a2", "b2"], after.Select(f => f.Body));
        Assert.Contains(before, f => f.SubjectPath != anchor.SubjectPath);
        Assert.Contains(after, f => f.SubjectPath != anchor.SubjectPath);
    }

    // Falsify: scope ReadNeighbours in FactStore.cs to `e.path = anchor.SubjectPath`, turning it
    // back into expand history's per-entity query, and re-run — the assertion above goes from
    // ["a1", "b1"] to [] and the test fails. This is the exact regression D57's separation exists
    // to prevent (docs/memory-expansion/05-browse-tui-spec.md).

    [Fact]
    public void Timeline_RespectsBeforeAndAfterLimits()
    {
        for (var i = 0; i < 3; i++)
        {
            Remember($"/people/before{i}", $"before{i}", T0.AddSeconds(i));
        }

        var anchorId = Remember("/people/anchor", "anchor", T0.AddSeconds(10));

        for (var i = 0; i < 3; i++)
        {
            Remember($"/people/after{i}", $"after{i}", T0.AddSeconds(20 + i));
        }

        var anchor = FactStore.ReadById(connection, anchorId)!;
        var (before, after) = FactStore.Timeline(connection, anchor, before: 2, after: 1);

        Assert.Equal(["before1", "before2"], before.Select(f => f.Body));
        Assert.Equal(["after0"], after.Select(f => f.Body));
    }

    [Fact]
    public void Timeline_TiesOnValidFrom_BreakByInsertionOrder()
    {
        // Same instant on every row, so valid_from and created_at tie for all three; the total
        // order then falls to id, which is insertion order.
        Remember("/people/a", "first", T0);
        var anchorId = Remember("/people/anchor", "anchor", T0);
        Remember("/people/b", "second", T0);

        var anchor = FactStore.ReadById(connection, anchorId)!;
        var (before, after) = FactStore.Timeline(connection, anchor, before: 5, after: 5);

        Assert.Equal(["first"], before.Select(f => f.Body));
        Assert.Equal(["second"], after.Select(f => f.Body));
    }

    [Fact]
    public void Timeline_WithNoNeighbours_ReturnsEmptyLists()
    {
        var anchorId = Remember("/people/only", "alone", T0);

        var anchor = FactStore.ReadById(connection, anchorId)!;
        var (before, after) = FactStore.Timeline(connection, anchor, before: 5, after: 5);

        Assert.Empty(before);
        Assert.Empty(after);
    }
}
