using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Drives the real <c>vec0</c> table. Gated on <c>ENGRAM_VEC_EXTENSION</c> because none of this
/// can be faked — the behaviour under test is the extension's, not ours.
/// </summary>
public class VectorIndexTests
{
    private const int Dimensions = 4;
    private static readonly EmbeddingSpace Space = new("test-embedder", Dimensions);

    /// <summary>A unit vector at <paramref name="radians"/> from the query direction.</summary>
    private static float[] At(double radians) =>
        [(float)Math.Cos(radians), (float)Math.Sin(radians), 0f, 0f];

    private static float[] Query => At(0);

    /// <summary>
    /// The schema file does not create this table, even where it could — its DDL is
    /// parameterized by vector width and needs sqlite-vec loaded, so a static statement could
    /// express neither, and every install would pay for a width it may not want. This sandbox
    /// has the extension available, so a table here would mean the schema had grown one.
    /// </summary>
    [Fact]
    public void AFreshDatabase_HasNoIndexAndNoSpace()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();

        Assert.False(VectorIndex.Exists(sandbox.Connection));
        Assert.Null(VectorIndex.ReadSpace(sandbox.Connection));
        Assert.Null(VectorIndex.ReadInputVersion(sandbox.Connection));
    }

    [Fact]
    public void EnsureCreated_PinsTheSpaceAndTheInputComposition()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();

        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        Assert.True(VectorIndex.Exists(sandbox.Connection));
        Assert.Equal(Space, VectorIndex.ReadSpace(sandbox.Connection));
        Assert.Equal(VectorIndex.InputVersion, VectorIndex.ReadInputVersion(sandbox.Connection));
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);
        var factId = sandbox.AddFact("a", "the first fact");
        VectorIndex.Write(sandbox.Connection, transaction: null, factId, At(0));

        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
    }

    [Fact]
    public void EnsureCreated_WithoutTheExtension_Fails()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.Open(sandbox.Home.DatabasePath);

        // No pretending: an index that cannot exist is not silently skipped, because the caller
        // would then write facts believing they were queued for embedding.
        Assert.Throws<SqliteException>(() => VectorIndex.EnsureCreated(connection, Space));
    }

    [Fact]
    public void BackfillBatch_HoldsLiveFactsWithNoVector()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var first = sandbox.AddFact("a", "the first fact");
        var second = sandbox.AddFact("b", "the second fact");

        Assert.Equal(
            [first, second],
            VectorIndex.ReadBackfillBatch(sandbox.Connection, 10).Select(p => p.FactId));

        VectorIndex.Write(sandbox.Connection, transaction: null, first, At(0));

        Assert.Equal(
            [second],
            VectorIndex.ReadBackfillBatch(sandbox.Connection, 10).Select(p => p.FactId));
        Assert.Equal(1, VectorIndex.CountPending(sandbox.Connection));
    }

    [Fact]
    public void BackfillBatch_IgnoresClosedFacts()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        sandbox.Close(sandbox.AddFact("a", "the first belief"));

        Assert.Empty(VectorIndex.ReadBackfillBatch(sandbox.Connection, 10));
        Assert.Equal(0, VectorIndex.CountPending(sandbox.Connection));
    }

    [Fact]
    public void BackfillBatch_HonoursItsLimit()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);
        sandbox.AddFacts(5);

        Assert.Equal(2, VectorIndex.ReadBackfillBatch(sandbox.Connection, 2).Count);
        Assert.Equal(5, VectorIndex.CountPending(sandbox.Connection));
    }

    [Fact]
    public void BackfillBatch_CarriesTheTextThatGetsEmbedded()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);
        sandbox.AddFact("a", "engram stores temporal facts");

        var pending = Assert.Single(VectorIndex.ReadBackfillBatch(sandbox.Connection, 10));
        Assert.Equal(VectorIndex.InputFor("engram stores temporal facts"), pending.Text);
    }

    /// <summary>
    /// <c>INSERT OR REPLACE</c> does not work on a <c>vec0</c> table, and the failure only
    /// appears on the second write for a fact — which is exactly what <c>embed --rebuild</c>
    /// and a re-embedded fact both do.
    /// </summary>
    [Fact]
    public void Write_Twice_ReplacesRatherThanFailing()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var factId = sandbox.AddFact("a", "the first fact");
        VectorIndex.Write(sandbox.Connection, transaction: null, factId, At(0));
        VectorIndex.Write(sandbox.Connection, transaction: null, factId, At(Math.PI / 2));

        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));

        // The second vector is the one that survived: a query along the first direction is now
        // a quarter turn away rather than on top of it.
        var match = Assert.Single(VectorIndex.Search(sandbox.Connection, Query, k: 5));
        Assert.Equal(factId, match.FactId);
        Assert.True(match.Distance > 0.5, $"expected a distant match, got {match.Distance}");
    }

    /// <summary>
    /// Liveness comes from the fact, not the caller — otherwise a fact superseded between the
    /// backfill's read and its write would be indexed as live and rank forever.
    /// </summary>
    [Fact]
    public void Write_ForAClosedFact_MarksItRetired()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var factId = sandbox.AddFact("a", "the first belief");
        sandbox.Close(factId);

        VectorIndex.Write(sandbox.Connection, transaction: null, factId, At(0));

        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
        Assert.Equal(0, VectorIndex.Count(sandbox.Connection, liveOnly: true));
        Assert.Empty(VectorIndex.Search(sandbox.Connection, Query, k: 5));
    }

    [Fact]
    public void Write_ForAFactThatIsGone_WritesNothing()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        VectorIndex.Write(sandbox.Connection, transaction: null, factId: 9999, At(0));

        Assert.Equal(0, VectorIndex.Count(sandbox.Connection));
    }

    [Fact]
    public void Retire_HidesTheVectorWithoutDiscardingIt()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var factId = sandbox.AddFact("a", "the first fact");
        VectorIndex.Write(sandbox.Connection, transaction: null, factId, At(0));

        VectorIndex.Retire(sandbox.Connection, transaction: null, factId);

        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
        Assert.Equal(0, VectorIndex.Count(sandbox.Connection, liveOnly: true));
        Assert.Empty(VectorIndex.Search(sandbox.Connection, Query, k: 5));
    }

    /// <summary>
    /// Supersession is authored truth and embeddings are optional (D18), so the fact path must
    /// not be able to fail because no vector index was ever created.
    /// </summary>
    [Fact]
    public void Retire_WithNoIndex_IsANoOp()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = FactStore.Remember(
            connection,
            new FactWrite("test/a", "concept", "is", "the first fact", "project", "stated"),
            DateTimeOffset.UnixEpoch.AddSeconds(1)).FactId;

        VectorIndex.Retire(connection, transaction: null, factId);
    }

    /// <summary>
    /// The write path deliberately does not retire vectors — it cannot without putting a vec0
    /// statement in front of every <c>remember</c> — so something has to close the gap.
    /// </summary>
    [Fact]
    public void Reconcile_RetiresVectorsWhoseFactWasSuperseded()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var stays = sandbox.AddFact("a", "still believed");
        var goes = sandbox.AddFact("b", "no longer believed");
        VectorIndex.Write(sandbox.Connection, transaction: null, stays, At(0));
        VectorIndex.Write(sandbox.Connection, transaction: null, goes, At(0.01));

        sandbox.Close(goes);
        Assert.Equal(2, VectorIndex.Count(sandbox.Connection, liveOnly: true));

        Assert.Equal(1, VectorIndex.Reconcile(sandbox.Connection));

        Assert.Equal(2, VectorIndex.Count(sandbox.Connection));
        Assert.Equal([stays], VectorIndex.Search(sandbox.Connection, Query, k: 5).Select(m => m.FactId));
    }

    [Fact]
    public void Reconcile_DropsVectorsWhoseFactIsGone()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var factId = sandbox.AddFact("a", "the first fact");
        VectorIndex.Write(sandbox.Connection, transaction: null, factId, At(0));

        using (var command = sandbox.Connection.CreateCommand())
        {
            // What `compact` pruning a fact would leave behind.
            command.CommandText = "DELETE FROM fact WHERE id = $id;";
            command.Parameters.AddWithValue("$id", factId);
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, VectorIndex.Reconcile(sandbox.Connection));
        Assert.Equal(0, VectorIndex.Count(sandbox.Connection));
    }

    [Fact]
    public void Reconcile_WithNothingToFix_TouchesNothing()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);
        VectorIndex.Write(sandbox.Connection, transaction: null, sandbox.AddFact("a", "a fact"), At(0));

        Assert.Equal(0, VectorIndex.Reconcile(sandbox.Connection));
    }

    [Fact]
    public void Reconcile_WithNoIndex_IsANoOp()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(0, VectorIndex.Reconcile(connection));
    }

    /// <summary>
    /// The reason <c>is_live</c> is a column on the index rather than a join to <c>fact</c>.
    /// <c>vec0</c> applies <c>k</c> before any join, so the nearest neighbours are chosen with
    /// no regard to liveness and the join then deletes some of them — a query for five live
    /// facts comes back with one, and nothing about the result says it was truncated.
    /// </summary>
    [Fact]
    public void Search_FilteringInsideTheMatch_DoesNotStarveAsFactsAreSuperseded()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var ids = new long[10];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = sandbox.AddFact($"f{i}", $"fact number {i}");
            VectorIndex.Write(sandbox.Connection, transaction: null, ids[i], At(i * 0.05));
        }

        // Close the four nearest to the query. They are still the four nearest vectors.
        for (var i = 0; i < 4; i++)
        {
            sandbox.Close(ids[i]);
            VectorIndex.Retire(sandbox.Connection, transaction: null, ids[i]);
        }

        Assert.Equal(
            ids[4..9],
            VectorIndex.Search(sandbox.Connection, Query, k: 5).Select(m => m.FactId));

        // The same question asked the wrong way round, to prove the filter is load-bearing and
        // not decoration: k first, liveness second, and four of the five results evaporate.
        Assert.Equal([ids[4]], PostFilteredSearch(sandbox.Connection, Query, k: 5));
    }

    private static IReadOnlyList<long> PostFilteredSearch(SqliteConnection connection, float[] query, int k)
    {
        var bytes = new byte[query.Length * sizeof(float)];
        Buffer.BlockCopy(query, 0, bytes, 0, bytes.Length);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT v.fact_id
            FROM fact_vec v
            JOIN fact f ON f.id = v.fact_id
            WHERE v.embedding MATCH $q AND v.k = $k AND f.valid_to IS NULL
            ORDER BY v.distance;
            """;
        command.Parameters.AddWithValue("$q", bytes);
        command.Parameters.AddWithValue("$k", k);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    [Fact]
    public void Search_OrdersByDistance()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var far = sandbox.AddFact("far", "unrelated");
        var near = sandbox.AddFact("near", "on topic");
        VectorIndex.Write(sandbox.Connection, transaction: null, far, At(Math.PI / 2));
        VectorIndex.Write(sandbox.Connection, transaction: null, near, At(0.01));

        Assert.Equal(
            [near, far],
            VectorIndex.Search(sandbox.Connection, Query, k: 5).Select(m => m.FactId));
    }

    [Fact]
    public void Clear_EmptiesTheIndexAndKeepsItsShape()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var factId = sandbox.AddFact("a", "the first fact");
        VectorIndex.Write(sandbox.Connection, transaction: null, factId, At(0));

        VectorIndex.Clear(sandbox.Connection);

        Assert.Equal(0, VectorIndex.Count(sandbox.Connection));
        Assert.True(VectorIndex.Exists(sandbox.Connection));
        Assert.Equal(Space, VectorIndex.ReadSpace(sandbox.Connection));
        Assert.Equal(1, VectorIndex.CountPending(sandbox.Connection));
    }

    [Fact]
    public void Drop_RemovesTheIndexAndTheSpaceItPinned()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        VectorIndex.Drop(sandbox.Connection);

        Assert.False(VectorIndex.Exists(sandbox.Connection));
        Assert.Null(VectorIndex.ReadSpace(sandbox.Connection));
        Assert.Null(VectorIndex.ReadInputVersion(sandbox.Connection));

        // A different width has to be creatable afterwards, which is the whole point of a drop
        // rather than a clear.
        VectorIndex.EnsureCreated(sandbox.Connection, new EmbeddingSpace("other-embedder", 8));
        Assert.Equal(new EmbeddingSpace("other-embedder", 8), VectorIndex.ReadSpace(sandbox.Connection));
    }

    // --- prune (edge-fact-lane-eligibility.md §3.3) ------------------------------------------

    [Fact]
    public void PruneIneligible_DeletesOnlyVectorsWhoseFactIsNoLongerEligible()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var eligible = sandbox.AddFact("a", "authored belief");
        var callEdge = sandbox.AddEdgeFact("caller", "calls", "callee");
        var importsEdge = sandbox.AddEdgeFact("mod", "imports", "dep");
        VectorIndex.Write(sandbox.Connection, transaction: null, eligible, At(0));
        VectorIndex.Write(sandbox.Connection, transaction: null, callEdge, At(0.1));
        VectorIndex.Write(sandbox.Connection, transaction: null, importsEdge, At(0.2));

        var deleted = VectorIndex.PruneIneligible(sandbox.Connection);

        Assert.Equal(2, deleted);
        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
    }

    [Fact]
    public void CountIneligibleByPredicate_GroupsByPredicate()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var call1 = sandbox.AddEdgeFact("caller1", "calls", "callee1");
        var call2 = sandbox.AddEdgeFact("caller2", "calls", "callee2");
        var importEdge = sandbox.AddEdgeFact("mod", "imports", "dep");
        VectorIndex.Write(sandbox.Connection, transaction: null, call1, At(0));
        VectorIndex.Write(sandbox.Connection, transaction: null, call2, At(0.1));
        VectorIndex.Write(sandbox.Connection, transaction: null, importEdge, At(0.2));

        var counts = VectorIndex.CountIneligibleByPredicate(sandbox.Connection);

        Assert.Equal(2, counts.Count);
        Assert.Contains(counts, c => c.Predicate == "calls" && c.Count == 2);
        Assert.Contains(counts, c => c.Predicate == "imports" && c.Count == 1);
    }

    /// <summary>
    /// Falsification: with the eligibility condition weakened to match everything, the eligible
    /// fact's vector is deleted too — proving the WHERE clause, not just the DELETE, is what the
    /// first test above depends on. Broken and confirmed to redden, then restored.
    /// </summary>
    [Fact]
    public void PruneIneligible_LeavesTheEligibleVectorUntouched_MirrorCase()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        VectorIndex.EnsureCreated(sandbox.Connection, Space);

        var eligible = sandbox.AddFact("a", "authored belief");
        VectorIndex.Write(sandbox.Connection, transaction: null, eligible, At(0));

        VectorIndex.PruneIneligible(sandbox.Connection);

        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
    }
}
