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
    private static RetrievalExplanation Explain(
        SandboxHome sandbox,
        SqliteConnection connection,
        string query,
        int budget = 500) =>
        RetrievalExplainer.Explain(connection, sandbox.Home, query, budget, null, T0, _ => null);

    private static long Write(SqliteConnection connection, string slug, string body, string learnedVia = "stated") =>
        FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/" + slug, "note", "states", body, "project", learnedVia),
            T0).FactId;

    [Fact]
    public void Explain_ReportsTheTermOverlapLaneAsTheOneThatRanks()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragmas.");

        var lane = Assert.Single(Explain(sandbox, connection, "pragmas").Lanes, l => l.Name == "term overlap");

        Assert.Equal(LaneState.Ranking, lane.State);
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
    /// The row that justifies the whole command. FTS5 stems with porter, the shipped ranker
    /// matches literal tokens, so a plural in the query finds a fact the ranker never scores —
    /// and recall answers "nothing matched" while the answer sits one lane over.
    /// </summary>
    [Fact]
    public void Explain_NamesFactsALaneFoundThatTheRankerNeverConsidered()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = Write(connection, "pragma", "Every connection sets its own pragma.");

        var explanation = Explain(sandbox, connection, "pragmas");

        Assert.Empty(explanation.Recall.Candidates);
        var missed = Assert.Single(explanation.Missed);
        Assert.Equal(id, missed.FactId);
        Assert.Equal("fts5", missed.Lane);
        Assert.Contains("pragma", missed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_WhenALaneAgreesWithTheRanker_ReportsNothingMissed()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "pragma", "Every connection sets its own pragma.");

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

    [Fact]
    public void Explain_ReportsFusionAsUnbuiltUntilSomethingFuses()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var lane = Assert.Single(Explain(sandbox, connection, "anything").Lanes, l => l.Name == "RRF fusion");

        Assert.Equal(LaneState.Unbuilt, lane.State);
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
            connection, sandbox.Home, "pragmas", 500, "no-such-session", T0, _ => null);

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
