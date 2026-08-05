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

    /// <summary>
    /// The bracketed id the model sees for a stored fact.
    /// </summary>
    /// <remarks>
    /// The store's own id is the identity (D2); the 'f' keeps the handle shape recall output
    /// already used. One spelling, in one place, because a tool that takes a handle and a
    /// tool that prints one disagreeing is a class of bug with no symptom until a user tries
    /// to forget something.
    /// </remarks>
    public static string HandleFor(long factId) =>
        "f" + factId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a handle back to a fact id, tolerating the brackets it is printed inside.
    /// </summary>
    public static bool TryParseHandle(string handle, out long factId)
    {
        factId = 0;
        if (handle is null)
        {
            return false;
        }

        var trimmed = handle.Trim().Trim('[', ']');
        if (!trimmed.StartsWith('f'))
        {
            return false;
        }

        return long.TryParse(
            trimmed.AsSpan(1),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out factId);
    }

    public static CannedFact ToCannedFact(StoredFact fact, DateTimeOffset now, IReadOnlyDictionary<string, string>? topicNames = null) => new(
        Id: HandleFor(fact.Id),
        Subject: fact.SubjectName,
        Predicate: fact.Predicate,
        Body: fact.Body,
        Scope: fact.Scope,
        Topic: TopicOf(fact.SubjectPath, topicNames),
        AgeDays: AgeDaysOf(fact, now),
        Evidence: fact.Evidence);

    /// <summary>
    /// The display text of a fact's topic: the second path segment, resolved through the
    /// topic node that holds the text it was authored with.
    /// </summary>
    /// <remarks>
    /// Root-agnostic on purpose. Every root shares the <c>/root/topic/subject</c> shape —
    /// <c>/knowledge</c> for the seed corpus, <c>/user</c> for what the user stated — so
    /// singling one of them out here would mean a second root's facts silently reporting a
    /// topic of "memory" until someone noticed the primer had stopped naming them.
    ///
    /// The path only carries a slug, which is why this resolves through a node rather than
    /// de-slugging: "claude-code hooks" and "claude code hooks" produce the same slug, so
    /// the display text is not recoverable from the path. Without a node to resolve against
    /// the slug is returned as-is — a store written before topic nodes existed should read
    /// as slightly wrong rather than not read at all.
    /// </remarks>
    public static string TopicOf(string path, IReadOnlyDictionary<string, string>? topicNames = null)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return "memory";
        }

        var topicPath = "/" + segments[0] + "/" + segments[1];
        if (topicNames is not null && topicNames.TryGetValue(topicPath, out var name))
        {
            return name;
        }

        return segments[1];
    }

    private static int AgeDaysOf(StoredFact fact, DateTimeOffset now)
    {
        var age = now - DateTimeOffset.FromUnixTimeSeconds(fact.CreatedAt);
        return age.TotalDays > 0 ? (int)age.TotalDays : 0;
    }
}
