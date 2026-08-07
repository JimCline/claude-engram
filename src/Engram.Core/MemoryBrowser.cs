using Microsoft.Data.Sqlite;

namespace Engram.Core;

public sealed record BrowseNode(
    string Path,
    string Name,
    int FactsHere,
    int FactsUnder,
    IReadOnlyList<BrowseNode> Children,
    int ChildrenOmitted);

/// <summary>
/// The read side of <c>engram_browse</c> and <c>engram_expand</c>: structure and history,
/// as opposed to recall's ranking. Everything here is a live-fact read — browse is a table
/// of contents, and a table of contents that counted closed beliefs would advertise rooms
/// that are no longer there.
/// </summary>
public static class MemoryBrowser
{
    /// <summary>Children shown per node before the rest fold into a count.</summary>
    public const int MaxChildrenShown = 15;

    public const int MaxDepth = 3;

    /// <summary>
    /// One query for the whole subtree, folded into a tree in memory. The boundary is
    /// MoveSubtree's — <c>substr</c>, not <c>LIKE</c>, because paths contain <c>%</c> and
    /// <c>_</c> in ordinary filenames — and a child is the next segment across <c>/</c> or
    /// <c>#</c>, so <c>/code/api-docs</c> never counts under <c>/code/api</c>.
    /// </summary>
    public static BrowseNode? Browse(SqliteConnection connection, string path, int depth)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.TrimEnd('/');
        if (normalized.Length == 0)
        {
            normalized = "/";
        }

        depth = Math.Clamp(depth, 1, MaxDepth);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT e.path, count(f.id)
                FROM entity e
                LEFT JOIN fact f ON f.subject_id = e.id AND f.valid_to IS NULL
                WHERE e.path = $path
                   OR (substr(e.path, 1, $len) = $path AND substr(e.path, $len + 1, 1) IN ('/', '#'))
                GROUP BY e.path;
                """;
            command.Parameters.AddWithValue("$path", normalized);
            command.Parameters.AddWithValue("$len", normalized.Length);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts[reader.GetString(0)] = (int)reader.GetInt64(1);
            }
        }

        if (counts.Count == 0)
        {
            return null;
        }

        return Fold(normalized, LastSegment(normalized), counts, depth);
    }

    /// <summary>Live facts at exactly this path, most salient first, newest breaking ties.</summary>
    /// <remarks>
    /// Salience today is present-but-unwritten (nothing increments access counts yet), so
    /// the COALESCE means recency decides in practice. The ORDER BY is written for the
    /// store as designed rather than as currently populated, so the day something writes
    /// scores this starts honoring them with no edit here.
    /// </remarks>
    public static IReadOnlyList<StoredFact> TopFacts(SqliteConnection connection, string path, int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var facts = FactStore.ReadSubtree(connection, path)
            .Where(f => f.SubjectPath == path.TrimEnd('/') && f.ValidTo is null)
            .ToList();

        if (facts.Count <= limit)
        {
            return facts;
        }

        var scores = Salience(connection, facts.Select(f => f.Id));
        return facts
            .OrderByDescending(f => scores.GetValueOrDefault(f.Id, 0.0))
            .ThenByDescending(f => f.CreatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>Supersession reasons for a set of closed facts, keyed by the closed fact's id.</summary>
    public static Dictionary<long, string> Reasons(SqliteConnection connection, IEnumerable<long> factIds)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var reasons = new Dictionary<long, string>();
        foreach (var id in factIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT reason FROM supersession WHERE old_fact_id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);

            if (command.ExecuteScalar() is string reason)
            {
                reasons[id] = reason;
            }
        }

        return reasons;
    }

    /// <summary>The session a fact was recorded in, if any: external id and start time.</summary>
    public static (string ExternalId, long StartedAt)? Sitting(SqliteConnection connection, long factId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.external_id, s.started_at
            FROM fact f
            JOIN session s ON s.id = f.session_id
            WHERE f.id = $id AND s.external_id IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$id", factId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static Dictionary<long, double> Salience(SqliteConnection connection, IEnumerable<long> factIds)
    {
        var scores = new Dictionary<long, double>();
        foreach (var id in factIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT score FROM salience WHERE fact_id = $id;";
            command.Parameters.AddWithValue("$id", id);

            if (command.ExecuteScalar() is double score)
            {
                scores[id] = score;
            }
        }

        return scores;
    }

    private static BrowseNode Fold(
        string path,
        string name,
        Dictionary<string, int> counts,
        int depth)
    {
        var here = counts.GetValueOrDefault(path, 0);

        // Group every strict descendant by its next segment after this node.
        var byChild = new Dictionary<string, (string ChildPath, int Facts)>(StringComparer.Ordinal);
        foreach (var (entityPath, factCount) in counts)
        {
            if (entityPath.Length <= path.Length
                || !entityPath.StartsWith(path, StringComparison.Ordinal)
                || entityPath[path.Length] is not ('/' or '#'))
            {
                continue;
            }

            var separator = entityPath[path.Length];
            var rest = entityPath[(path.Length + 1)..];
            var segmentEnd = rest.IndexOfAny(['/', '#']);
            var segment = segmentEnd < 0 ? rest : rest[..segmentEnd];
            var childPath = path + separator + segment;

            var display = separator == '#' ? "#" + segment : segment;
            byChild[display] = byChild.TryGetValue(display, out var existing)
                ? (existing.ChildPath, existing.Facts + factCount)
                : (childPath, factCount);
        }

        var ordered = byChild
            .OrderByDescending(pair => pair.Value.Facts)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        var children = new List<BrowseNode>(Math.Min(ordered.Count, MaxChildrenShown));
        foreach (var (display, (childPath, facts)) in ordered.Take(MaxChildrenShown))
        {
            // FactsUnder is strict descendants on every node, whichever branch built it —
            // the grouped sum includes the child's own facts, so they come back off here.
            var childHere = counts.GetValueOrDefault(childPath, 0);
            children.Add(depth > 1
                ? Fold(childPath, display, counts, depth - 1)
                : new BrowseNode(childPath, display, childHere, facts - childHere, [], 0));
        }

        var under = ordered.Sum(pair => pair.Value.Facts);
        return new BrowseNode(path, name, here, under, children, Math.Max(0, ordered.Count - MaxChildrenShown));
    }

    private static string LastSegment(string path)
    {
        var index = path.LastIndexOfAny(['/', '#']);
        return index < 0 || index == path.Length - 1 ? path : path[(index + 1)..];
    }
}
