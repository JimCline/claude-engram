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
    public static RetrievalExplanation Explain(
        SqliteConnection connection,
        EngramHome home,
        string query,
        int budgetTokens,
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
        var longTerm = FactCatalog.ReadLongTerm(connection, now);
        var (currentSession, priorSession) = SessionFacts.Read(connection, sessionExternalId, now);

        var lanes = new List<LaneReport>();
        var lexical = ReadLexical(connection, query, settings.SeedK, lanes);

        var recall = RecallEngine.Explain(
            query,
            longTerm,
            currentSession,
            priorSession,
            lexical.ToDictionary(pair => pair.Key, pair => pair.Value.Rank),
            budgetTokens);

        lanes.Insert(0, new LaneReport(
            "term overlap",
            LaneState.Contributing,
            $"{Count(recall.Candidates.Count(c => c.OverlapRank is not null))} over subject and body, matched literally"));

        var (vector, vectorSpace) = ReadVector(connection, home, query, environment, settings.SeedK, lanes, local);
        var salience = ReadSalience(connection, lanes);
        var tiers = ReadTiers(connection, recall.Candidates);

        lanes.Add(new LaneReport(
            "RRF fusion",
            LaneState.Ranking,
            $"k={RecallEngine.RrfK}, seed_k={settings.SeedK} — this is the order engram_recall returns"
                + (vectorSpace is null ? ", over the two lexical lanes" : $", over three lanes including {vectorSpace}")));

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
        int seedK,
        List<LaneReport> lanes,
        LocalRuntime? local)
    {
        const string Name = "vector (sqlite-vec)";
        var empty = new Dictionary<long, VectorHit>();

        var settings = EmbeddingSettings.Read(ConfigFile.Load(home.ConfigPath));
        var resolution = EmbedderFactory.Create(settings, environment, client: null, local);
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

        var matches = VectorIndex.Search(connection, embedded, seedK);
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
