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
        var catalog = new List<CannedFact>(facts.Count);

        foreach (var fact in facts)
        {
            catalog.Add(ToCannedFact(fact, now));
        }

        return catalog;
    }

    public static CannedFact ToCannedFact(StoredFact fact, DateTimeOffset now) => new(
        // The store's own id is the identity (D2). The 'f' keeps the handle shape the model
        // already sees in recall output, and strips back to the id when a tool takes one.
        Id: "f" + fact.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Subject: fact.SubjectName,
        Predicate: fact.Predicate,
        Body: fact.Body,
        Scope: fact.Scope,
        Topic: TopicOf(fact.SubjectPath),
        AgeDays: AgeDaysOf(fact, now),
        Evidence: fact.Evidence);

    /// <summary>
    /// The topic segment of a seeded path, or a fallback for facts stored elsewhere.
    /// </summary>
    /// <remarks>
    /// This returns the slug, not the display text the corpus was authored with —
    /// "claude-code-hooks", not "claude-code hooks". Nothing renders it today: recall formats
    /// only id, body, scope and age. When the primer moves onto the store it will need the
    /// display form, and the way to get it is a topic entity carrying the original text as
    /// its name, because de-slugging cannot work — "claude-code hooks" and "claude code hooks"
    /// produce the same slug.
    /// </remarks>
    public static string TopicOf(string path)
    {
        var prefix = CannedFactSeeder.Root + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "memory";
        }

        var rest = path[prefix.Length..];
        var separator = rest.IndexOf('/');

        return separator < 0 ? rest : rest[..separator];
    }

    private static int AgeDaysOf(StoredFact fact, DateTimeOffset now)
    {
        var age = now - DateTimeOffset.FromUnixTimeSeconds(fact.CreatedAt);
        return age.TotalDays > 0 ? (int)age.TotalDays : 0;
    }
}
