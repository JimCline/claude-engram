using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The explainer against a real store, where the lanes it reports on actually live.
/// </summary>
public class RetrievalExplainerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    // Schema without the seed corpus: OpenInitialized applies the schema and seeds nothing, so
    // every candidate below is one this test wrote. A seeded sandbox would make each assertion
    // depend on which words the shipped corpus happens to use, which is a test that fails when
    // someone edits a fact body for unrelated reasons.
    // The display limit is the CLI's own default rather than something unbounded, so the tier
    // assertions below run against the bound production uses. These sandboxes are unseeded and
    // write a handful of facts, so nothing here is near it.
    private static RetrievalExplanation Explain(
        SandboxHome sandbox,
        SqliteConnection connection,
        string query,
        int budget = 500) =>
        RetrievalExplainer.Explain(connection, sandbox.Home, query, budget, 20, null, T0, _ => null);

    private static long Write(SqliteConnection connection, string slug, string body, string learnedVia = "stated") =>
        FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/" + slug, "note", "states", body, "project", learnedVia),
            T0).FactId;

    [Fact]
    public void Explain_ReportsFusionAsTheRankerAndBothLexicalLanesAsFeedingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragmas.");

        var lanes = Explain(sandbox, connection, "pragmas").Lanes;

        Assert.Equal(LaneState.Ranking, Assert.Single(lanes, l => l.Name == "RRF fusion").State);
        Assert.Equal(LaneState.Contributing, Assert.Single(lanes, l => l.Name == "term overlap").State);
        Assert.Equal(LaneState.Contributing, Assert.Single(lanes, l => l.Name == "lexical (fts5/bm25)").State);
    }

    [Fact]
    public void Explain_AttachesTheBm25RankAndScoreToEachCandidate()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = Write(connection, "pragma", "Every connection sets its own pragmas.");

        var explained = Assert.Single(
            Explain(sandbox, connection, "pragmas").Candidates,
            c => c.Candidate.FactId == id);

        Assert.NotNull(explained.Lexical);
        Assert.Equal(1, explained.Lexical.Rank);

        // bm25 is negative and smaller is better. Reported as SQLite produces it, so the number
        // a user reads is the number that ranked.
        Assert.True(explained.Lexical.Bm25 < 0);
    }

    /// <summary>
    /// The defect the explainer found, now a regression guard. FTS5 stems with porter and the
    /// overlap lane matches literal tokens, so before fusion a plural query scored nothing and
    /// recall told the model to go rediscover a fact the store already held.
    /// </summary>
    [Theory]
    [InlineData("pragmas")]
    [InlineData("connections")]
    public void Explain_FindsASingularFactFromAPluralQuery(string query)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = Write(connection, "pragma", "Every connection sets its own pragma.");

        var explained = Assert.Single(Explain(sandbox, connection, query).Candidates);

        Assert.Equal(id, explained.Candidate.FactId);
        Assert.NotNull(explained.Candidate.LexicalRank);
        Assert.Null(explained.Candidate.OverlapRank);
        Assert.True(explained.Candidate.Packed);
    }

    /// <summary>
    /// A word that lives only in the subject, queried in the plural — the case neither lane could
    /// serve alone. The overlap lane reads the subject but matches literally; FTS5 stems but,
    /// until <c>path</c> joined the index, could not see the subject at all.
    /// </summary>
    [Theory]
    [InlineData("kestrel")]
    [InlineData("kestrels")]
    public void Explain_FindsAFactByASubjectWordAbsentFromItsBody(string query)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/kestrel", "note", "states", "It binds loopback only.", "project", "stated"),
            T0).FactId;

        var explained = Assert.Single(Explain(sandbox, connection, query).Candidates);

        Assert.Equal(id, explained.Candidate.FactId);
        Assert.NotNull(explained.Candidate.LexicalRank);
    }

    /// <summary>
    /// <c>path</c> follows its entity on rename (D2), and it is the one indexed column that can
    /// change. Without the re-index trigger the fact stays findable at an address it no longer
    /// has, and not findable at the one it does.
    /// </summary>
    [Fact]
    public void Explain_AfterAPathChanges_FindsTheFactAtItsNewAddressOnly()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/kestrel", "note", "states", "It binds loopback only.", "project", "stated"),
            T0).FactId;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE fact SET path = '/knowledge/testing/hummingbird' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        Assert.Empty(FactStore.SearchRanked(connection, "kestrel", 10));
        Assert.Equal(id, Assert.Single(FactStore.SearchRanked(connection, "hummingbird", 10)).FactId);
    }

    /// <summary>
    /// An invariant rather than a feature: both lexical lanes now feed the ranker at the same
    /// depth, so anything FTS5 returns must be a candidate. If this ever reports something, the
    /// two depths have drifted apart.
    /// </summary>
    [Fact]
    public void Explain_ReportsNothingMissedByTheLexicalLane()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragma.");

        Assert.Empty(Explain(sandbox, connection, "pragmas").Missed);
        Assert.Empty(Explain(sandbox, connection, "pragma").Missed);
    }

    [Fact]
    public void Explain_CarriesTheProvenanceTierFromTheStore()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var stated = Write(connection, "stated-one", "Kestrel binds loopback only.", "stated");
        var inferred = Write(connection, "inferred-one", "Kestrel probably binds loopback.", "inferred");

        var explanation = Explain(sandbox, connection, "kestrel loopback binds");

        Assert.Equal("stated", Assert.Single(explanation.Candidates, c => c.Candidate.FactId == stated).Tier);
        Assert.Equal("inferred", Assert.Single(explanation.Candidates, c => c.Candidate.FactId == inferred).Tier);
    }

    /// <summary>
    /// The tier is read for the candidates the caller will print and for no others.
    /// </summary>
    /// <remarks>
    /// The clock cannot hold this. <c>ReadTiers</c> is bounded twice — by the display limit and by
    /// 500-id chunking — and the two overlap in what they cost, so the end-to-end ratio guard fails
    /// only when both are gone: with chunking left in place, restoring the unbounded candidate list
    /// measures 1.89x against a passing 1.3x, and no margin separates those without becoming
    /// intermittent. This asserts the bound itself instead, which is exact and needs no timing.
    /// </remarks>
    [Fact]
    public void Explain_ReadsTheProvenanceTierOnlyAsFarAsTheCallerWillPrint()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        for (var i = 1; i <= 6; i++)
        {
            Write(connection, "bounded-" + i, $"Kestrel binds loopback for listener {i}.");
        }

        const int DisplayLimit = 3;
        var explanation = RetrievalExplainer.Explain(
            connection, sandbox.Home, "kestrel loopback binds", 500, DisplayLimit, null, T0, _ => null);

        Assert.True(
            explanation.Candidates.Count > DisplayLimit,
            $"the store must rank more than {DisplayLimit} candidates or this asserts nothing "
                + $"(it ranked {explanation.Candidates.Count})");

        Assert.All(explanation.Candidates.Take(DisplayLimit), c => Assert.NotNull(c.Tier));
        Assert.All(explanation.Candidates.Skip(DisplayLimit), c => Assert.Null(c.Tier));
    }

    [Fact]
    public void Explain_WithEmbeddingsOff_ReportsTheVectorLaneOffRatherThanBroken()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragmas.");

        var lane = Assert.Single(Explain(sandbox, connection, "pragmas").Lanes, l => l.Name.StartsWith("vector", StringComparison.Ordinal));

        Assert.Equal(LaneState.Off, lane.State);
    }

    [Fact]
    public void Explain_ReportsSalienceAsUnbuiltWhileNothingWritesIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragmas.");

        var lane = Assert.Single(Explain(sandbox, connection, "pragmas").Lanes, l => l.Name == "salience");

        Assert.Equal(LaneState.Unbuilt, lane.State);
    }

    [Fact]
    public void Explain_WhenSalienceHasScores_AttachesThemAndReportsThemUnread()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = Write(connection, "pragma", "Every connection sets its own pragmas.");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO salience (fact_id, score) VALUES ($id, 0.75);";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        var explanation = Explain(sandbox, connection, "pragmas");

        Assert.Equal(0.75, Assert.Single(explanation.Candidates, c => c.Candidate.FactId == id).Salience);
        Assert.Equal(LaneState.Idle, Assert.Single(explanation.Lanes, l => l.Name == "salience").State);
    }

    /// <summary>
    /// Fusion rewards lane agreement over position within one lane, which is the property k=60
    /// buys. A fact both lanes found must beat one only a single lane found, even when the loser
    /// is that lane's top hit.
    /// </summary>
    [Fact]
    public void Explain_RanksAFactBothLanesFoundAboveOneOnlyALaneFound()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var both = Write(connection, "kestrel-both", "Kestrel binds loopback only.");
        var lexicalOnly = Write(connection, "loopback-only", "The listener binds loopback addresses.");

        var explanation = Explain(sandbox, connection, "kestrel loopback");

        var first = Assert.Single(explanation.Candidates, c => c.Candidate.FactId == both);
        var second = Assert.Single(explanation.Candidates, c => c.Candidate.FactId == lexicalOnly);

        Assert.NotNull(first.Candidate.OverlapRank);
        Assert.NotNull(first.Candidate.LexicalRank);
        Assert.True(first.Candidate.Fused > second.Candidate.Fused);
        Assert.Equal(both, explanation.Candidates[0].Candidate.FactId);
    }

    [Fact]
    public void Explain_DrawsEachLaneToTheConfiguredSeedK()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        File.WriteAllText(sandbox.Home.ConfigPath, "[retrieval]\nseed_k = 2\n");
        for (var i = 0; i < 6; i++)
        {
            Write(connection, "loopback-" + i, $"Listener {i} binds loopback addresses only.");
        }

        var explanation = Explain(sandbox, connection, "loopback");

        Assert.Equal(2, explanation.Candidates.Count(c => c.Candidate.LexicalRank is not null));
        Assert.Contains(explanation.Lanes, l => l.Name == "RRF fusion" && l.Detail.Contains("seed_k=2", StringComparison.Ordinal));
    }

    /// <summary>
    /// Read-only means read-only. An explainer that recorded an access would move the ranking it
    /// was asked to explain, and the effect would be invisible because the tool that would show
    /// it is the one causing it.
    /// </summary>
    [Fact]
    public void Explain_WritesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragmas.");

        var before = Counts(connection);
        Explain(sandbox, connection, "pragmas connection");
        Explain(sandbox, connection, "unmatched query text");

        Assert.Equal(before, Counts(connection));
    }

    [Fact]
    public void Explain_HonoursTheBudgetItIsGiven()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        for (var i = 0; i < 8; i++)
        {
            Write(connection, "pragma-" + i, $"Connection number {i} sets its own pragmas exactly once at open.");
        }

        var generous = Explain(sandbox, connection, "connection pragmas", budget: 500);
        var mean = Explain(sandbox, connection, "connection pragmas", budget: 40);

        Assert.Equal(generous.Candidates.Count, mean.Candidates.Count);
        Assert.True(mean.PackedCount < generous.PackedCount);
        Assert.True(mean.Recall.TokensUsed <= 40);
    }

    [Fact]
    public void Explain_TreatsAnUnknownSessionAsHavingNoWorkingMemory()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragmas.");

        var explanation = RetrievalExplainer.Explain(
            connection, sandbox.Home, "pragmas", 500, 20, "no-such-session", T0, _ => null);

        Assert.DoesNotContain(explanation.Candidates, c => c.Candidate.Origin == FactOrigin.CurrentSession);
    }

    private static (long Facts, long Salience, long Supersessions) Counts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM fact), (SELECT COUNT(*) FROM salience), (SELECT COUNT(*) FROM supersession);";
        using var reader = command.ExecuteReader();
        reader.Read();

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
