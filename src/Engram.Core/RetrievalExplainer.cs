using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>What a retrieval lane is currently contributing.</summary>
public enum LaneState
{
    /// <summary>This lane decides the order recall returns.</summary>
    Ranking,

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

/// <summary>
/// A fact a lane found that the ranker never considered.
/// </summary>
/// <remarks>
/// The most useful row in the report. "Why didn't it find that?" has an answer here that no
/// other output can give: it <i>was</i> found, by a lane whose results nothing reads.
/// </remarks>
public sealed record MissedFact(long FactId, string Handle, string Body, string Lane, int Rank, double Score);

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
    IReadOnlyList<MissedFact> Missed,
    IReadOnlyList<LaneReport> Lanes)
{
    public int PackedCount => Candidates.Count(c => c.Candidate.Packed);
}

/// <summary>
/// Answers "why did recall return that, in that order, and why not this other thing" (D21).
/// </summary>
/// <remarks>
/// <para><b>Built against what recall does, not against what it is planned to do.</b> D21
/// describes fusing a BM25 rank and a vector rank by RRF and reporting both. Today neither
/// participates: <see cref="RecallEngine"/> orders by how many distinct query terms a fact
/// contains, the FTS5 table is written by triggers and read by nothing on the recall path, the
/// vector table is queried only by its own tests, and the <c>salience</c> table has no writer.
/// An explainer written to the letter of D21 would therefore report a ranking that does not
/// happen — which is precisely the failure D21 exists to prevent, one layer up.</para>
///
/// <para>So the shipped ranker is reported as the ranker, and the other lanes are reported as
/// what they are: present, answerable, and unread. That disagreement is the point. A fact BM25
/// places first and the term-overlap ranker misses entirely is evidence about whether fusing
/// them is worth doing, available before the fusion is written rather than after.</para>
///
/// <para>Read-only, including the vector lane: embedding the query is a network or model call,
/// never a database write, and nothing here touches salience counters. An explainer that
/// recorded an access would change the ranking it was asked to explain.</para>
/// </remarks>
public static class RetrievalExplainer
{
    /// <summary>
    /// How deep each unfused lane is queried. Mirrors <c>[retrieval] seed_k</c>, which is what a
    /// fused implementation would draw per lane before combining.
    /// </summary>
    public const int LaneDepth = 32;

    public static RetrievalExplanation Explain(
        SqliteConnection connection,
        EngramHome home,
        string query,
        int budgetTokens,
        string? sessionExternalId,
        DateTimeOffset now,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(environment);

        var longTerm = FactCatalog.ReadLongTerm(connection, now);
        var (currentSession, priorSession) = SessionFacts.Read(connection, sessionExternalId, now);
        var recall = RecallEngine.Explain(query, longTerm, currentSession, priorSession, budgetTokens);

        var lanes = new List<LaneReport>
        {
            new(
                "term overlap",
                LaneState.Ranking,
                $"{recall.Candidates.Count} candidates — this is the order engram_recall returns"),
        };

        var lexical = ReadLexical(connection, query, lanes);
        var (vector, vectorSpace) = ReadVector(connection, home, query, environment, lanes);
        var salience = ReadSalience(connection, lanes);
        var tiers = ReadTiers(connection, recall.Candidates);

        lanes.Add(new LaneReport(
            "RRF fusion",
            LaneState.Unbuilt,
            vectorSpace is null
                ? "M4 step 8. Fuses the lanes above once there is more than one to fuse."
                : $"M4 step 8. Both lanes answer in {vectorSpace}; nothing combines them yet."));

        var explained = recall.Candidates
            .Select(candidate => new ExplainedCandidate(
                candidate,
                candidate.FactId is { } id && tiers.TryGetValue(id, out var tier) ? tier : null,
                candidate.FactId is { } lid && lexical.TryGetValue(lid, out var hit) ? hit : null,
                candidate.FactId is { } vid && vector.TryGetValue(vid, out var match) ? match : null,
                candidate.FactId is { } sid && salience.TryGetValue(sid, out var score) ? score : null))
            .ToList();

        var considered = recall.Candidates
            .Where(c => c.FactId is not null)
            .Select(c => c.FactId!.Value)
            .ToHashSet();

        var missed = ReadMissed(connection, lexical, vector, considered);

        return new RetrievalExplanation(recall, explained, missed, lanes);
    }

    private static Dictionary<long, LexicalHit> ReadLexical(
        SqliteConnection connection,
        string query,
        List<LaneReport> lanes)
    {
        var hits = FactStore.SearchRanked(connection, query, LaneDepth);
        lanes.Add(new LaneReport(
            "lexical (fts5/bm25)",
            hits.Count > 0 ? LaneState.Idle : LaneState.Unavailable,
            hits.Count > 0
                ? $"{hits.Count} hits, best bm25 {hits[0].Bm25.ToString("0.00", CultureInfo.InvariantCulture)} — indexed and answerable, read by nothing on the recall path"
                : "no hits for this query"));

        return hits.ToDictionary(h => h.FactId);
    }

    /// <summary>
    /// Runs the vector lane if every one of its four preconditions holds, and names the first
    /// one that does not.
    /// </summary>
    /// <remarks>
    /// The extension is loaded explicitly rather than assumed, even though
    /// <see cref="EngramDatabase.Open(EngramHome)"/> already tried: loadable extensions are
    /// connection-scoped and pooling recycles handles, so a successful query proves some
    /// connection loaded it, not this one. The state has to come from a load on the connection
    /// in hand.
    /// </remarks>
    private static (Dictionary<long, VectorHit> Hits, EmbeddingSpace? Space) ReadVector(
        SqliteConnection connection,
        EngramHome home,
        string query,
        Func<string, string?> environment,
        List<LaneReport> lanes)
    {
        const string Name = "vector (sqlite-vec)";
        var empty = new Dictionary<long, VectorHit>();

        var settings = EmbeddingSettings.Read(ConfigFile.Load(home.ConfigPath));
        var resolution = EmbedderFactory.Create(settings, environment);
        if (!resolution.Resolved)
        {
            lanes.Add(new LaneReport(
                Name,
                settings.Provider == EmbeddingProvider.None ? LaneState.Off : LaneState.Unavailable,
                resolution.Reason));
            return (empty, null);
        }

        if (VectorExtension.Load(connection, home.LibDir) is not VectorExtensionState.Loaded and var state)
        {
            lanes.Add(new LaneReport(
                Name,
                LaneState.Unavailable,
                state == VectorExtensionState.NotInstalled
                    ? $"sqlite-vec is not in {home.LibDir}, so the index cannot be queried"
                    : $"sqlite-vec is in {home.LibDir} and would not load — wrong architecture, or truncated"));
            return (empty, null);
        }

        if (!VectorIndex.Exists(connection) || VectorIndex.ReadSpace(connection) is not { } indexed)
        {
            lanes.Add(new LaneReport(Name, LaneState.Unavailable, "no vector index in this store yet"));
            return (empty, null);
        }

        if (indexed != resolution.Embedder!.Space)
        {
            // D18's quiet failure: distances between spaces are real numbers and mean nothing.
            lanes.Add(new LaneReport(
                Name,
                LaneState.Unavailable,
                $"the index holds {indexed} but the configured provider produces {resolution.Embedder.Space} — "
                + "vectors from different spaces are not comparable, so this lane is not queried"));
            return (empty, null);
        }

        float[]? embedded;
        try
        {
            embedded = resolution.Embedder
                .EmbedAsync([VectorIndex.InputFor(query)])
                .GetAwaiter()
                .GetResult()
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            lanes.Add(new LaneReport(Name, LaneState.Unavailable, $"the provider did not answer: {exception.Message}"));
            return (empty, null);
        }

        if (embedded is null)
        {
            lanes.Add(new LaneReport(Name, LaneState.Unavailable, "the provider returned no vector for this query"));
            return (empty, null);
        }

        var matches = VectorIndex.Search(connection, embedded, LaneDepth);
        lanes.Add(new LaneReport(
            Name,
            matches.Count > 0 ? LaneState.Idle : LaneState.Unavailable,
            matches.Count > 0
                ? $"{matches.Count} hits in {indexed}, nearest {matches[0].Distance.ToString("0.000", CultureInfo.InvariantCulture)} — answerable, read by nothing on the recall path"
                : $"{indexed} is queryable and empty for this query"));

        return (
            matches
                .Select((m, i) => new VectorHit(m.FactId, m.Distance, i + 1))
                .ToDictionary(h => h.FactId),
            indexed);
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

    private static Dictionary<long, string> ReadTiers(
        SqliteConnection connection,
        IReadOnlyList<RecallCandidate> candidates)
    {
        var ids = candidates.Where(c => c.FactId is not null).Select(c => c.FactId!.Value).Distinct().ToList();
        var tiers = new Dictionary<long, string>();
        if (ids.Count == 0)
        {
            return tiers;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, learned_via FROM fact WHERE id IN ({Placeholders(command, ids)});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tiers[reader.GetInt64(0)] = reader.GetString(1);
        }

        return tiers;
    }

    /// <summary>
    /// Facts a lane ranked that the ranker never scored.
    /// </summary>
    /// <remarks>
    /// Bodies are read here rather than carried down from the lane queries because a lane returns
    /// ids: the join has to happen somewhere, and doing it once at the end reads only the handful
    /// of rows that turned out to be interesting.
    /// </remarks>
    private static List<MissedFact> ReadMissed(
        SqliteConnection connection,
        Dictionary<long, LexicalHit> lexical,
        Dictionary<long, VectorHit> vector,
        HashSet<long> considered)
    {
        var missed = new List<MissedFact>();

        foreach (var (id, hit) in lexical)
        {
            if (!considered.Contains(id))
            {
                missed.Add(new MissedFact(id, FactCatalog.HandleFor(id), "", "fts5", hit.Rank, hit.Bm25));
            }
        }

        foreach (var (id, hit) in vector)
        {
            if (!considered.Contains(id) && !lexical.ContainsKey(id))
            {
                missed.Add(new MissedFact(id, FactCatalog.HandleFor(id), "", "vector", hit.Rank, hit.Distance));
            }
        }

        if (missed.Count == 0)
        {
            return missed;
        }

        var bodies = ReadBodies(connection, missed.Select(m => m.FactId).ToList());

        return missed
            .Select(m => m with { Body = bodies.TryGetValue(m.FactId, out var body) ? body : "(unreadable)" })
            .OrderBy(m => m.Lane, StringComparer.Ordinal)
            .ThenBy(m => m.Rank)
            .ToList();
    }

    private static Dictionary<long, string> ReadBodies(SqliteConnection connection, List<long> ids)
    {
        var bodies = new Dictionary<long, string>();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, body FROM fact WHERE id IN ({Placeholders(command, ids)});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bodies[reader.GetInt64(0)] = reader.GetString(1);
        }

        return bodies;
    }

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
