using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The long-term corpus, read from the store and presented in the shape the ranker already
/// consumes.
/// </summary>
/// <remarks>
/// This is deliberately a storage change and nothing else. Recall keeps its existing
/// token-overlap ranking rather than switching to the FTS lane in the same step, because
/// changing where facts live and how they are ranked at once would make the M0 adoption
/// telemetry uninterpretable — a shift in coverage could be either cause, and the whole point
/// of that measurement is to attribute it.
/// </remarks>
public static class FactCatalog
{
    public static IReadOnlyList<CannedFact> ReadLongTerm(EngramHome home, DateTimeOffset now)
    {
        // A fresh connection per call, closed immediately. D4's watched failure mode is WAL
        // checkpoint starvation caused by long-lived read snapshots in the MCP loop, so the
        // connection must not outlive the read.
        using var connection = EngramDatabase.OpenInitialized(home);

        var facts = FactStore.ReadLive(connection);
        var topics = ReadTopicNames(connection);
        var catalog = new List<CannedFact>(facts.Count);

        foreach (var fact in facts)
        {
            catalog.Add(ToCannedFact(fact, now, topics));
        }

        return catalog;
    }

    /// <summary>
    /// Maps each topic node's path to the display text it was authored with.
    /// </summary>
    public static Dictionary<string, string> ReadTopicNames(SqliteConnection connection)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, name FROM entity WHERE kind = $kind;";
        command.Parameters.AddWithValue("$kind", CannedFactSeeder.TopicKind);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names[reader.GetString(0)] = reader.GetString(1);
        }

        return names;
    }

    public static CannedFact ToCannedFact(StoredFact fact, DateTimeOffset now, IReadOnlyDictionary<string, string>? topicNames = null) => new(
        // The store's own id is the identity (D2). The 'f' keeps the handle shape the model
        // already sees in recall output, and strips back to the id when a tool takes one.
        Id: "f" + fact.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Subject: fact.SubjectName,
        Predicate: fact.Predicate,
        Body: fact.Body,
        Scope: fact.Scope,
        Topic: TopicOf(fact.SubjectPath, topicNames),
        AgeDays: AgeDaysOf(fact, now),
        Evidence: fact.Evidence);

    /// <summary>
    /// The display text of a seeded path's topic, or a fallback for facts stored elsewhere.
    /// </summary>
    /// <remarks>
    /// The path only carries the slug. The primer prints this string to the model verbatim,
    /// so it resolves through the topic node, which stores the text the corpus was authored
    /// with — de-slugging cannot substitute, since "claude-code hooks" and "claude code
    /// hooks" produce the same slug. Without a node to resolve against, the slug is returned
    /// as-is: a store written before topic nodes existed should read as slightly wrong
    /// rather than not read at all.
    /// </remarks>
    public static string TopicOf(string path, IReadOnlyDictionary<string, string>? topicNames = null)
    {
        var prefix = CannedFactSeeder.Root + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "memory";
        }

        var rest = path[prefix.Length..];
        var separator = rest.IndexOf('/');
        var slug = separator < 0 ? rest : rest[..separator];

        if (topicNames is not null && topicNames.TryGetValue(prefix + slug, out var name))
        {
            return name;
        }

        return slug;
    }

    private static int AgeDaysOf(StoredFact fact, DateTimeOffset now)
    {
        var age = now - DateTimeOffset.FromUnixTimeSeconds(fact.CreatedAt);
        return age.TotalDays > 0 ? (int)age.TotalDays : 0;
    }
}
