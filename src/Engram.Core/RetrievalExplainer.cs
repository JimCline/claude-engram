using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>What a retrieval lane is currently contributing.</summary>
public enum LaneState
{
    /// <summary>This decides the order recall returns.</summary>
    Ranking,

    /// <summary>Feeding the fusion that ranks.</summary>
    Contributing,

    /// <summary>It works and it has answers, but nothing consumes them yet.</summary>
    Idle,

    /// <summary>Deliberately off, by configuration.</summary>
    Off,

    /// <summary>Wanted, and something is missing or broken.</summary>
    Unavailable,

    /// <summary>Not built.</summary>
    Unbuilt,
}

/// <summary>One lane, its state, and a sentence saying why.</summary>
public sealed record LaneReport(string Name, LaneState State, string Detail);

/// <summary>A vector hit, with its position.</summary>
public sealed record VectorHit(long FactId, double Distance, int Rank);

/// <summary>One candidate, with every lane's opinion of it attached.</summary>
public sealed record ExplainedCandidate(
    RecallCandidate Candidate,
    string? Tier,
    LexicalHit? Lexical,
    VectorHit? Vector,
    double? Salience);

public sealed record RetrievalExplanation(
    RecallExplanation Recall,
    IReadOnlyList<ExplainedCandidate> Candidates,
    int MissedCount,
    IReadOnlyList<LaneReport> Lanes)
{
    public int PackedCount => Candidates.Count(c => c.Candidate.Packed);
}

/// <summary>
/// Answers "why did recall return that, in that order, and why not this other thing" (D21).
/// </summary>
/// <remarks>
/// <para><b>Reports what recall does, not what it is planned to do.</b> Every lane's state here
/// is observed rather than assumed, which is what makes the report usable for debugging: when
/// this was first built it showed that four of D21's five signals did not participate in recall
/// at all, and the disagreement it exposed between the lexical lane and the shipped ranker is
/// what produced the fusion those lanes now feed (D30). A lane that stops contributing must
/// therefore start reporting that it has, rather than being described by this comment.</para>
///
/// <para>Read-only, including the vector lane: embedding the query is a network or model call,
/// never a database write, and nothing here touches salience counters. An explainer that
/// recorded an access would change the ranking it was asked to explain.</para>
/// </remarks>
public static class RetrievalExplainer
{
    /// <param name="displayLimit">
    /// How many candidates the caller will actually render. Required rather than defaulted: the
    /// obvious default of <see cref="int.MaxValue"/> reads as "no opinion" and is in fact the
    /// unbounded read this parameter exists to prevent, kept for every caller that forgot to think
    /// about it. Only <see cref="ExplainedCandidate.Tier"/> is affected — beyond this many
    /// candidates it is null, and the report falls back to the origin it already has.
    /// </param>
    public static RetrievalExplanation Explain(
        SqliteConnection connection,
        EngramHome home,
        string query,
        int budgetTokens,
        int displayLimit,
        string? sessionExternalId,
        DateTimeOffset now,
        Func<string, string?> environment,
        LocalRuntime? local = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(environment);

        var settings = RetrievalSettings.Read(ConfigFile.Load(home.ConfigPath));
        var currentSessionId = sessionExternalId is { Length: > 0 }
            ? SessionStore.FindSession(connection, sessionExternalId)
            : null;

        var lanes = new List<LaneReport>();

        // Prepared once: RecallRanker's own KNN search runs inside the ranking statement (D59), so
        // the embedding computed here is bound straight into it. ReadVector's own VectorIndex.Search
        // below is display-only and reuses this embedding rather than asking the provider for a
        // second one, which would double an explain call's most expensive step.
        var embedding = EmbeddingSettings.Read(ConfigFile.Load(home.ConfigPath));
        var vectorQuery = VectorLane.PrepareQuery(connection, home, embedding, query, environment, local);

        var lexical = ReadLexical(connection, query, settings.SeedK, lanes);

        // Ahead of the fusion, not after it: these ranks are an input to the ranking this method
        // exists to describe. Reporting a lane that ran too late to affect the result would be
        // exactly the drift D30 forbids. The lane order printed below is unchanged — the overlap
        // report is still inserted at the front once its count is known.
        var (vector, vectorSpace) = ReadVector(connection, vectorQuery, settings.SeedK, lanes);

        var outcome = RecallRanker.Rank(
            connection, query, budgetTokens, settings.SeedK, currentSessionId, now, vectorQuery);

        lanes.Insert(0, OverlapLaneReport(outcome));

        var salience = ReadSalience(connection, lanes);
        var tiers = ReadTiers(connection, outcome.Candidates, displayLimit);

        lanes.Add(new LaneReport(
            "RRF fusion",
            LaneState.Ranking,
            $"k={RecallEngine.RrfK}, seed_k={settings.SeedK} — this is the order engram_recall returns"
                + (vectorSpace is null ? ", over the two lexical lanes" : $", over three lanes including {vectorSpace}")));

        var explained = outcome.Candidates
            .Select(candidate => new ExplainedCandidate(
                candidate,
                candidate.FactId is { } id && tiers.TryGetValue(id, out var tier) ? tier : null,
                candidate.FactId is { } lid && lexical.TryGetValue(lid, out var hit) ? hit : null,
                candidate.FactId is { } vid && vector.TryGetValue(vid, out var match) ? match : null,
                candidate.FactId is { } sid && salience.TryGetValue(sid, out var score) ? score : null))
            .ToList();

        var missedCount = MissedCount(outcome, displayLimit);

        var recall = new RecallExplanation(
            query,
            outcome.QueryTerms,
            outcome.DroppedTerms,
            outcome.Candidates,
            outcome.BudgetTokens,
            outcome.TokensUsed,
            outcome.Coverage);

        return new RetrievalExplanation(recall, explained, missedCount, lanes);
    }

    /// <summary>
    /// The "term overlap" lane row — <see cref="LaneState.Contributing"/> when the token index is
    /// ready, otherwise the same wording <see cref="RecallRanker"/> puts in the RECALL digest's
    /// availability note (spec §2.0.1), so the two surfaces describe one state with one message.
    /// </summary>
    private static LaneReport OverlapLaneReport(RankOutcome outcome) => outcome.OverlapState switch
    {
        FactTokenIndexState.Ready => new LaneReport(
            "term overlap",
            LaneState.Contributing,
            $"{Count(outcome.Candidates.Count(c => c.OverlapRank is not null))} over subject and body, matched literally"),
        FactTokenIndexState.Unbuilt => new LaneReport(
            "term overlap", LaneState.Unbuilt, RecallRanker.OverlapUnavailableDetail(outcome.OverlapState)!),
        _ => new LaneReport(
            "term overlap", LaneState.Unavailable, RecallRanker.OverlapUnavailableDetail(outcome.OverlapState)!),
    };

    private static string Count(int hits) =>
        hits == 1 ? "1 hit" : hits.ToString(CultureInfo.InvariantCulture) + " hits";

    private static Dictionary<long, LexicalHit> ReadLexical(
        SqliteConnection connection,
        string query,
        int seedK,
        List<LaneReport> lanes)
    {
        var hits = FactStore.SearchRanked(connection, query, seedK);
        lanes.Add(new LaneReport(
            "lexical (fts5/bm25)",
            hits.Count > 0 ? LaneState.Contributing : LaneState.Unavailable,
            hits.Count > 0
                ? $"{Count(hits.Count)} over body and predicate, porter-stemmed, best bm25 {hits[0].Bm25.ToString("0.00", CultureInfo.InvariantCulture)}"
                : "no hits for this query"));

        return hits.ToDictionary(h => h.FactId);
    }

    /// <summary>
    /// Reports the vector lane, from a query already embedded by <see cref="VectorLane.PrepareQuery"/>.
    /// </summary>
    /// <remarks>
    /// The search itself is display-only here: <see cref="RecallRanker"/> runs the authoritative KNN
    /// search inside the ranking statement (D59), and D30 makes this method's whole purpose
    /// describing the ranker that actually runs. Re-embedding the query for this call would be a
    /// second implementation of nothing — the ranks it would produce are the ranker's own — while
    /// still paying the provider's cost a second time, so the embedding is shared and only the
    /// search (a plain in-process SQL query) is repeated.
    /// </remarks>
    private static (Dictionary<long, VectorHit> Hits, EmbeddingSpace? Space) ReadVector(
        SqliteConnection connection,
        VectorLaneQuery prepared,
        int seedK,
        List<LaneReport> lanes)
    {
        const string Name = "vector (sqlite-vec)";

        if (prepared.State is not VectorLaneState.Queried)
        {
            lanes.Add(new LaneReport(
                Name,
                prepared.State == VectorLaneState.Off ? LaneState.Off : LaneState.Unavailable,
                prepared.Reason));
            return (new Dictionary<long, VectorHit>(), null);
        }

        IReadOnlyList<VectorMatch> matches;
        try
        {
            matches = VectorIndex.Search(connection, prepared.Embedding!, seedK);
        }
        catch (SqliteException exception)
        {
            lanes.Add(new LaneReport(Name, LaneState.Unavailable, $"the index would not answer: {exception.Message}"));
            return (new Dictionary<long, VectorHit>(), prepared.Space);
        }

        lanes.Add(new LaneReport(
            Name,
            matches.Count > 0 ? LaneState.Contributing : LaneState.Unavailable,
            matches.Count > 0
                ? $"{matches.Count} hits in {prepared.Space}, nearest {matches[0].Distance.ToString("0.000", CultureInfo.InvariantCulture)}, fused into the ranking"
                : $"{prepared.Space} is queryable and empty for this query"));

        return (
            matches.Select((m, i) => new VectorHit(m.FactId, m.Distance, i + 1)).ToDictionary(h => h.FactId),
            prepared.Space);
    }

    private static Dictionary<long, double> ReadSalience(SqliteConnection connection, List<LaneReport> lanes)
    {
        var scores = new Dictionary<long, double>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fact_id, score FROM salience;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                scores[reader.GetInt64(0)] = reader.GetDouble(1);
            }
        }

        lanes.Add(new LaneReport(
            "salience",
            scores.Count > 0 ? LaneState.Idle : LaneState.Unbuilt,
            scores.Count > 0
                ? $"{scores.Count} scored facts, contributing nothing to the order above"
                : "table is present and empty — nothing writes access counts yet"));

        return scores;
    }

    /// <summary>
    /// How each candidate the report can print was learned.
    /// </summary>
    /// <remarks>
    /// <para>Bounded twice over, and the two bounds fix different faults. Reading only as far as
    /// the caller renders is the latency half: every candidate beyond it is a row fetched for a
    /// line nobody prints, which is how explain came to cost time proportional to the corpus
    /// rather than to its own output. At 50,000 candidates that was a single statement carrying
    /// 50,000 bound parameters.</para>
    ///
    /// <para>Chunking is the correctness half and does not follow from the first: the display
    /// limit is a number a user types, so <c>--limit 100000</c> restores the hazard in full — and
    /// crossing SQLite's variable ceiling is a hard failure, not a slow query. Neither bound
    /// substitutes for the other.</para>
    /// </remarks>
    private static Dictionary<long, string> ReadTiers(
        SqliteConnection connection,
        IReadOnlyList<RecallCandidate> candidates,
        int displayLimit)
    {
        const int ChunkSize = 500;

        var ids = candidates
            .Take(displayLimit)
            .Where(c => c.FactId is not null)
            .Select(c => c.FactId!.Value)
            .Distinct()
            .ToList();

        var tiers = new Dictionary<long, string>();
        for (var start = 0; start < ids.Count; start += ChunkSize)
        {
            var chunk = ids.GetRange(start, Math.Min(ChunkSize, ids.Count - start));

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, learned_via FROM fact WHERE id IN ({Placeholders(command, chunk)});";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tiers[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        return tiers;
    }

    /// <summary>
    /// How many candidates the ranker matched but that fall below the display bound (spec §2.0.2
    /// ruling 2).
    /// </summary>
    /// <remarks>
    /// This replaces a per-fact list that compared the wrong sets after D59: every lane hit — overlap
    /// unbounded, lexical and vector to <c>seed_k</c> — becomes a row in the ranking statement's own
    /// <c>candidates</c> CTE before the token-budget <c>LIMIT</c> ever applies, so nothing a lane
    /// finds can be structurally invisible to the ranker the way a malformed <c>/sessions/</c> path
    /// once could under the object ranker; a list built by diffing display-only lexical/vector hits
    /// against the ranker's own bounded output would have misreported ordinary truncation as "a lane
    /// the ranker does not read". But reporting nothing at all is a different defect: D30 makes
    /// <c>explain</c> a promise about what actually happened, and a silently-empty section reads as
    /// "nothing was missed" when the truth is "this is no longer computed that way". The statement
    /// already computes <see cref="RankOutcome.MatchedTotal"/> as <c>COUNT(*) OVER ()</c> over every
    /// scored candidate, for D44 — reusing it here to report the honest count costs nothing further.
    /// </remarks>
    private static int MissedCount(RankOutcome outcome, int displayLimit) =>
        Math.Max(0, outcome.MatchedTotal - displayLimit);

    /// <summary>Binds each id and returns the <c>$p0, $p1, …</c> list to splice into the SQL.</summary>
    private static string Placeholders(SqliteCommand command, List<long> ids)
    {
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            names[i] = "$p" + i.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(names[i], ids[i]);
        }

        return string.Join(", ", names);
    }
}
