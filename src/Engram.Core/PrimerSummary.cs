using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The three things a primer is built from: how many long-term facts are believed, how they
/// break down by topic, and enough of the catalog's front to pick examples from.
/// </summary>
/// <remarks>
/// <para>These are raw aggregates, deliberately unordered and unselected. Every ordering and
/// selection rule — count-descending then key-ordinal, <c>MaxClusters</c>, the <c>+N more</c>
/// tail, the preferred scope order, the fill-from-the-front — stays in
/// <see cref="PrimerBuilder"/>, because those rules must have exactly one implementation. A
/// summary that arrived pre-sorted would move them into the reader and give the in-memory path
/// and the SQL path a rule each to disagree about.</para>
///
/// <para><see cref="From"/> and <see cref="Read"/> are required to produce primers that are
/// equal byte for byte; <c>PrimerSummaryEquivalenceTests</c> is what holds that.</para>
/// </remarks>
/// <param name="FactCount">Live non-session facts in the store.</param>
/// <param name="TopicCounts">Topic display name to the number of live facts under it.</param>
/// <param name="ExampleCandidates">
/// A superset of everything <see cref="PrimerBuilder"/> can choose as an example, in catalog
/// order (ascending fact id).
/// </param>
public sealed record PrimerSummary(
    int FactCount,
    IReadOnlyDictionary<string, int> TopicCounts,
    IReadOnlyList<CannedFact> ExampleCandidates)
{
    /// <summary>
    /// Counts a catalog already in memory — the shape every existing caller and test hands over.
    /// </summary>
    public static PrimerSummary From(IReadOnlyList<CannedFact> facts)
    {
        var topicCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            topicCounts[fact.Topic] = topicCounts.GetValueOrDefault(fact.Topic) + 1;
        }

        return new PrimerSummary(facts.Count, topicCounts, facts);
    }

    /// <summary>
    /// Reads the same aggregates from the store without materialising the corpus.
    /// </summary>
    /// <remarks>
    /// Three statements, none of which is O(facts) in transferred bytes: a histogram over
    /// distinct subject paths, one row per topic entity, and at most a handful of candidate
    /// facts. The histogram groups by path rather than by topic because
    /// <see cref="FactCatalog.TopicOf"/> is the one implementation of what a topic is —
    /// its segment splitting and topic-node resolution would diverge from a SQL copy the
    /// first time either side was tuned.
    /// </remarks>
    public static PrimerSummary Read(SqliteConnection connection, DateTimeOffset now)
    {
        var topicNames = FactStore.ReadEntityNames(connection, CannedFactSeeder.TopicKind);
        var sessionPrefix = SessionFacts.Root + "/";

        var factCount = 0;
        var topicCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = TopicHistogramSql;
            BindSessionExclusion(command, sessionPrefix);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var count = reader.GetInt32(1);
                var topic = FactCatalog.TopicOf(reader.GetString(0), topicNames);
                topicCounts[topic] = topicCounts.GetValueOrDefault(topic) + count;
                factCount += count;
            }
        }

        var candidates = new List<CannedFact>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = ExampleCandidatesSql;
            BindSessionExclusion(command, sessionPrefix);
            command.Parameters.AddWithValue("$limit", PrimerBuilder.MaxExampleFacts);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(FactCatalog.ToCannedFact(
                    FactStore.ReadStoredFact(reader), now, topicNames, reader.GetInt32(15)));
            }
        }

        return new PrimerSummary(factCount, topicCounts, candidates);
    }

    /// <remarks>
    /// <c>substr</c> rather than <c>LIKE</c>: the prefix is a constant today but need not stay
    /// one, and <c>LIKE</c> would give <c>_</c> and <c>%</c> wildcard meaning inside a value.
    /// <c>entity.path</c> declares no collation, so SQLite compares it BINARY — which is what
    /// the <see cref="StringComparison.Ordinal"/> exclusion in
    /// <see cref="FactCatalog.ReadLongTerm(SqliteConnection, DateTimeOffset)"/> does. That
    /// equivalence is required; do not add a COLLATE.
    /// </remarks>
    private static void BindSessionExclusion(SqliteCommand command, string sessionPrefix)
    {
        command.Parameters.AddWithValue("$plen", sessionPrefix.Length);
        command.Parameters.AddWithValue("$prefix", sessionPrefix);
    }

    private const string TopicHistogramSql =
        """
        SELECT e.path, COUNT(*)
          FROM fact f
          JOIN entity e ON e.id = f.subject_id
         WHERE f.valid_to IS NULL
           AND substr(e.path, 1, $plen) <> $prefix
         GROUP BY e.path;
        """;

    // The lowest-id live non-session fact per scope, unioned with the first $limit live
    // non-session facts overall. That superset is provably everything PrimerBuilder.TopFacts can
    // reach, for any $limit rather than just for two. It takes one scope-first per distinct
    // scope, in preferred order, and a scope-first is that scope's lowest id because catalog
    // order is `ORDER BY f.id` — so the first arm covers step one whole. It reaches step two only
    // when fewer than $limit scopes exist, and then fills from the front skipping what it already
    // holds; holding k of them, it needs $limit - k more and in the worst case they sit at
    // positions k+1 through $limit, so the front $limit rows are exactly enough and never more.
    //
    // The version count is keyed on subject_id rather than on e.path, which does NOT contradict
    // D57. D57 is about addressing — the catalog-wide FactStore.VersionCounts groups by path so
    // that the number it advertises describes the same thread History will walk, and History
    // addresses a thread by path. entity.path is UNIQUE, so path and id are bijective and the
    // two groupings return identical numbers; here the row already carries both, and subject_id
    // is what ix_fact_thread indexes. Do not "fix" this back to a path join.
    //
    // SQLite rejects ORDER BY ... LIMIT directly inside a compound-SELECT operand, which is why
    // the second arm is wrapped in a subquery.
    private const string ExampleCandidatesSql =
        $"""
        SELECT {FactStore.FactColumns},
               (SELECT COUNT(*) FROM fact fv
                 WHERE fv.subject_id = f.subject_id AND fv.predicate = f.predicate)
          FROM fact f
          JOIN entity e ON e.id = f.subject_id
         WHERE f.id IN (SELECT MIN(sf.id)
                          FROM fact sf
                          JOIN entity se ON se.id = sf.subject_id
                         WHERE sf.valid_to IS NULL
                           AND substr(se.path, 1, $plen) <> $prefix
                         GROUP BY sf.scope
                        UNION
                        SELECT id
                          FROM (SELECT nf.id
                                  FROM fact nf
                                  JOIN entity ne ON ne.id = nf.subject_id
                                 WHERE nf.valid_to IS NULL
                                   AND substr(ne.path, 1, $plen) <> $prefix
                                 ORDER BY nf.id
                                 LIMIT $limit))
         ORDER BY f.id;
        """;
}
