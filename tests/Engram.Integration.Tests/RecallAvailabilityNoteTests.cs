using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Spec §3.3: the falsifications for the availability note (§2.0.1) — the D44 consequence of a lane
/// that did not run, stated in the RECALL digest the model actually reads.
/// </summary>
/// <remarks>
/// <para>The note exists because coverage is computed from lane <i>agreement</i> (D44), so a lane
/// that did not run silently deflates it: with one lane the corroboration arithmetic degenerates to
/// <c>(rank IS NOT NULL) &gt; 1</c>, which is false for every row, and an overlap-only fact drops out
/// of the result entirely. The digest then reads <c>coverage: none · gaps: no facts matched</c> for a
/// question the store can answer — a false negative that looks exactly like an empty store, which is
/// the reading that ends the discover-then-remember loop before it starts.</para>
///
/// <para>These are separate from <see cref="RecallRankerEquivalenceTests"/> on purpose. That suite
/// asks whether the SQL ranker reproduces the object ranker; this one asks what recall <i>says</i>
/// when a lane is missing, which the object ranker never said at all.</para>
/// </remarks>
public class RecallAvailabilityNoteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private const string OverlapUnbuiltNote = "overlap lane did not run (token index not built yet)";
    private const string OverlapStaleNote = "overlap lane did not run (token index built by an older tokenizer)";

    [Fact]
    public void Digest_NamesTheOverlapLane_WhenTheTokenIndexWasNeverBuilt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");
        Unbuild(connection);

        Assert.Equal(FactTokenIndexState.Unbuilt, FactTokenIndex.ReadState(connection));
        Assert.Contains(OverlapUnbuiltNote, Header(Pack(connection, "kestrel loopback")), StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_NamesTheOverlapLane_WhenTheTokenIndexIsAVersionBehind()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");
        Outdate(connection);

        Assert.Equal(FactTokenIndexState.VersionMismatch, FactTokenIndex.ReadState(connection));
        Assert.Contains(OverlapStaleNote, Header(Pack(connection, "kestrel loopback")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The half that keeps the note worth reading: a store where every lane that could run did says
    /// nothing extra. A note on every recall is a note nobody reads (D37).
    /// </summary>
    [Fact]
    public void Digest_SaysNothingAboutLanes_WhenEveryLaneThatCouldRunDid()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");

        Assert.Equal(FactTokenIndexState.Ready, FactTokenIndex.ReadState(connection));
        var result = Pack(connection, "kestrel loopback");

        Assert.DoesNotContain("did not run", Header(result), StringComparison.Ordinal);

        // EndsWith, not DoesNotContain alone: the note is appended after the coverage word, so
        // asserting the header stops there is what proves nothing was appended — including a note
        // worded differently from the two this file names.
        Assert.EndsWith(
            "coverage: " + RecallEngine.ToText(result.Coverage), Header(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Off</c> is a supported configuration (D18), not a fault. Folding it in would fire the note
    /// on every recall in every store with embeddings switched off — which is most of them — and
    /// D37 is explicit that a diagnostic reporting a choice as a fault is one people stop reading.
    /// </summary>
    [Fact]
    public void Digest_DoesNotNameTheVectorLane_WhenItIsMerelyOff()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");

        var off = VectorLaneQuery.Stopped(VectorLaneState.Off, "no embedding provider configured");
        Assert.DoesNotContain("did not run", Header(Pack(connection, "kestrel loopback", off)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The deliberate extension beyond the overlap lane: a vector lane that was <i>asked for</i> and
    /// stopped costs corroboration the same way, so it gets the same note — carrying the lane's own
    /// reason rather than a generic one, because "sqlite-vec is not installed" and "no index in this
    /// store" are different problems with different fixes (D36).
    /// </summary>
    [Fact]
    public void Digest_NamesTheVectorLane_WhenItWasAskedForAndStopped()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");

        const string Reason = "sqlite-vec is not installed";
        var stopped = VectorLaneQuery.Stopped(VectorLaneState.Unavailable, Reason);
        var header = Header(Pack(connection, "kestrel loopback", stopped));

        Assert.Contains("vector lane did not run (" + Reason + ")", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_NamesBothLanes_WhenNeitherRan()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");
        Unbuild(connection);

        var stopped = VectorLaneQuery.Stopped(VectorLaneState.Unavailable, "no index in this store");
        var header = Header(Pack(connection, "kestrel loopback", stopped));

        Assert.Contains(OverlapUnbuiltNote, header, StringComparison.Ordinal);
        Assert.Contains("vector lane did not run (no index in this store)", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// Keyed to lane <i>state</i>, never to hit count. A query that matches nothing is the ordinary
    /// empty answer and must stay distinguishable from a lane that could not look — those call for
    /// opposite responses, one "go and find out" and one "fix the index".
    /// </summary>
    /// <remarks>
    /// The query is the §2.5 stopword case, which is where a hit-count-keyed note would fire hardest:
    /// nothing tokenizes to an indexable term, so both lanes return empty while both are healthy.
    /// </remarks>
    [Fact]
    public void Digest_SaysNothingAboutLanes_WhenHealthyLanesSimplyFoundNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");

        var result = Pack(connection, "the of and");

        Assert.Equal(RecallCoverage.None, result.Coverage);
        Assert.DoesNotContain("did not run", Header(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the note exists for: an overlap-only fact, an overlap lane that did not run, and a
    /// digest that would otherwise state <c>coverage: none</c> about a store that holds the answer.
    /// </summary>
    /// <remarks>
    /// The token rows survive here — only the readiness stamp is moved — so this also pins the note
    /// to lane state rather than to whether <c>fact_token</c> happens to be populated. A ranker that
    /// consulted the rows regardless of the stamp would return the fact and read healthy, which is
    /// the failure spec ruling 3 forbids: no scanning fallback, report the lane instead.
    /// </remarks>
    [Fact]
    public void Digest_ReportsWhyItSaidNone_WhenTheOnlyLaneThatCouldHaveAnsweredDidNotRun()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        const string SubjectToken = "zzzoverlaponly";
        WriteWithSubjectName(connection, "planted-note", SubjectToken, "This note explains nothing distinctive.");

        // The fact is reachable while the lane is healthy — otherwise this proves nothing about the
        // lane, only that the corpus never held an answer.
        var healthy = Pack(connection, SubjectToken);
        Assert.NotEqual(RecallCoverage.None, healthy.Coverage);
        Assert.Equal(1, healthy.FactCount);

        Outdate(connection);
        var broken = Pack(connection, SubjectToken);

        Assert.Equal(RecallCoverage.None, broken.Coverage);
        Assert.Equal(0, broken.FactCount);
        Assert.Contains(OverlapStaleNote, Header(broken), StringComparison.Ordinal);
    }

    /// <summary>
    /// The arithmetic behind all of the above, as a controlled pair: coverage is lane agreement
    /// (D44), so one lane cannot corroborate itself and a store whose overlap lane did not run can
    /// never report <c>high</c> no matter how many facts the lexical lane found.
    /// </summary>
    /// <remarks>
    /// Stated as a pair because either half alone is consistent with a broken corpus — three facts
    /// that reach <c>high</c> intact and <c>partial</c> with the stamp moved is the only shape that
    /// shows the lane, rather than the query, is what changed. The matched count is asserted equal
    /// across both arms for the same reason: what moved is agreement, not reach.
    /// </remarks>
    [Fact]
    public void Coverage_CannotReachHigh_WhenOnlyOneLaneRan()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-a", "Kestrel binds loopback only.");
        Write(connection, "kestrel-b", "The kestrel loopback binding is deliberate.");
        Write(connection, "kestrel-c", "Loopback binding for kestrel, noted again.");

        const string Query = "kestrel loopback binding";

        var intact = Rank(connection, Query);
        Assert.Equal(RecallCoverage.High, intact.Coverage);
        Assert.True(intact.CorroboratedTotal >= 3, $"expected 3+ corroborated, got {intact.CorroboratedTotal}");

        Unbuild(connection);
        var oneLane = Rank(connection, Query);

        Assert.Equal(RecallCoverage.Partial, oneLane.Coverage);
        Assert.Equal(0, oneLane.CorroboratedTotal);
        Assert.Equal(intact.MatchedTotal, oneLane.MatchedTotal);
        Assert.Contains(OverlapUnbuiltNote, Header(Pack(connection, Query)), StringComparison.Ordinal);
    }

    /// <summary>
    /// D44's coverage boundary, re-proven against the SQL ranker rather than inherited from the
    /// object ranker's unit tests: <c>none</c> keyed to the total, <c>high</c> at three corroborated
    /// facts, <c>partial</c> everywhere between.
    /// </summary>
    /// <remarks>
    /// The <c>3+</c> boundary is kept rather than fitted (D44) — this asserts the boundary the
    /// statement implements is the one the decision names, on both sides of it. Only the two
    /// overlap-variant statements are exercised: the vector variants need sqlite-vec loaded and a
    /// populated index, which no integration test can assume (D36).
    /// </remarks>
    [Theory]
    [InlineData(0, RecallCoverage.None)]
    [InlineData(1, RecallCoverage.Partial)]
    [InlineData(2, RecallCoverage.Partial)]
    [InlineData(3, RecallCoverage.High)]
    [InlineData(4, RecallCoverage.High)]
    public void Coverage_TurnsOnLaneAgreement_AtTheBoundaryD44Names(int matchingFacts, RecallCoverage expected)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        for (var i = 0; i < matchingFacts; i++)
        {
            Write(connection, "kestrel-" + i, "Kestrel loopback binding, note " + i + ".");
        }

        var outcome = Rank(connection, "kestrel loopback binding");

        Assert.Equal(matchingFacts, outcome.CorroboratedTotal);
        Assert.Equal(expected, outcome.Coverage);
    }

    private static string Header(RecallPackResult result) => result.Text.Split('\n')[0];

    private static RecallPackResult Pack(SqliteConnection connection, string query, VectorLaneQuery? vectorQuery = null) =>
        RecallRanker.Pack(
            connection, query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            currentSessionId: null, T0, vectorQuery ?? VectorLaneQuery.Stopped(VectorLaneState.Off, "off"));

    private static RankOutcome Rank(SqliteConnection connection, string query) =>
        RecallRanker.Rank(
            connection, query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            currentSessionId: null, T0, VectorLaneQuery.Stopped(VectorLaneState.Off, "off"));

    private static long Write(SqliteConnection connection, string slug, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/" + slug, "note", "states", body, "project", "stated"),
            T0).FactId;

    /// <summary>
    /// Mirrors <see cref="RecallRankerEquivalenceTests"/>'s helper: a subject display name chosen
    /// independently of the path slug, so the fact is reachable by the overlap lane alone —
    /// <c>fact_fts</c> indexes <c>path</c>, so a name equal to its own slug is lexically findable.
    /// </summary>
    private static void WriteWithSubjectName(
        SqliteConnection connection, string slug, string subjectName, string body)
    {
        var id = Write(connection, slug, body);

        using var transaction = EngramDatabase.BeginWrite(connection);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE entity SET name = $name WHERE path = $path;";
            command.Parameters.AddWithValue("$name", subjectName);
            command.Parameters.AddWithValue("$path", "/knowledge/testing/" + slug);
            command.ExecuteNonQuery();
        }

        FactTokenIndex.Remove(connection, transaction, id);
        FactTokenIndex.Add(connection, transaction, id);
        transaction.Commit();
    }

    private static void Unbuild(SqliteConnection connection) =>
        Execute(connection, "DELETE FROM schema_meta WHERE key = 'fact_token_version';");

    private static void Outdate(SqliteConnection connection) =>
        Execute(connection, "UPDATE schema_meta SET value = '0' WHERE key = 'fact_token_version';");

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
