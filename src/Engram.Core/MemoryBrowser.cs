using System.Runtime.InteropServices;
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
    /// One query for the whole subtree, accumulated into a tree in a single pass over the
    /// rows as they stream from the reader (docs/memory-expansion/05b-browse-depth-bound-spec.md).
    /// The boundary is MoveSubtree's — <c>substr</c>, not <c>LIKE</c>, because paths contain
    /// <c>%</c> and <c>_</c> in ordinary filenames — and a child is the next segment across
    /// <c>/</c> or <c>#</c>, so <c>/code/api-docs</c> never counts under <c>/code/api</c>.
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

        // Root's separator is its own last character, so nothing precedes a root child's first
        // segment: the prefix the boundary test measures from is empty, and each child's leading
        // '/' is the separator that test looks for. Every other prefix ends in a segment, where
        // this is the same string and the query is unchanged.
        var prefix = normalized == "/" ? string.Empty : normalized;

        // here/under hold exact-match and strict-descendant fact counts per path, and
        // childrenOf holds each path's observed direct children — all three bounded to the
        // paths actually reached while walking a row's segments up to `depth` deep, never to
        // the full row count, so memory stops scaling with corpus size the way the prior
        // materialize-then-refold approach did.
        var here = new Dictionary<string, int>(StringComparer.Ordinal);
        var under = new Dictionary<string, int>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var sawAnyRow = false;

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
            command.Parameters.AddWithValue("$path", prefix);
            command.Parameters.AddWithValue("$len", prefix.Length);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sawAnyRow = true;
                var rowPath = reader.GetString(0);
                var count = (int)reader.GetInt64(1);

                if (rowPath == prefix)
                {
                    CollectionsMarshal.GetValueRefOrAddDefault(here, prefix, out _) += count;
                    continue;
                }

                // Root's own FactsUnder is unbounded by `depth` — every strict descendant
                // counts toward it regardless of how many segments down it is.
                CollectionsMarshal.GetValueRefOrAddDefault(under, prefix, out _) += count;

                var currentPath = prefix;
                for (var level = 1; level <= depth; level++)
                {
                    // rest/segment/display/childPath are spans over rowPath, not copies — a
                    // node within depth is seen by every entity beneath it (up to 28,920 rows
                    // for one node on the DEEP-50K fixture), so materializing a fresh string
                    // per row here is what D-7 measured as ~889 B/entity of pure GC churn. The
                    // separator sits immediately before the segment in rowPath itself, so a
                    // '#' child's display span (which must keep the '#') is just one character
                    // further left than a '/' child's (which must drop it) — no copy either way.
                    var separator = rowPath[currentPath.Length];
                    var afterSeparator = rowPath.AsSpan(currentPath.Length + 1);
                    var segmentEnd = afterSeparator.IndexOfAny('/', '#');
                    var segment = segmentEnd < 0 ? afterSeparator : afterSeparator[..segmentEnd];
                    var display = separator == '#'
                        ? rowPath.AsSpan(currentPath.Length, 1 + segment.Length)
                        : segment;
                    var childEnd = currentPath.Length + 1 + segment.Length;

                    if (!childrenOf.TryGetValue(currentPath, out var siblings))
                    {
                        siblings = new Dictionary<string, string>(StringComparer.Ordinal);
                        childrenOf[currentPath] = siblings;
                    }

                    // Only a node's first-seen row pays for a materialized string — every
                    // later row reuses the exact instance already stored here, which is what
                    // keeps allocation bounded to the node count within depth rather than the
                    // row count.
                    var siblingsLookup = siblings.GetAlternateLookup<ReadOnlySpan<char>>();
                    if (!siblingsLookup.TryGetValue(display, out var childPath))
                    {
                        childPath = rowPath[..childEnd];
                        siblingsLookup[display] = childPath;
                    }

                    if (childPath == rowPath)
                    {
                        CollectionsMarshal.GetValueRefOrAddDefault(here, childPath, out _) += count;
                        break;
                    }

                    // Every visible node's FactsUnder is likewise the full arbitrary-depth
                    // sum beneath it, not just what's within the remaining `depth` budget —
                    // a row deeper than `depth` still folds into the deepest visible
                    // ancestor's count on the last iteration of this loop.
                    CollectionsMarshal.GetValueRefOrAddDefault(under, childPath, out _) += count;
                    currentPath = childPath;
                }
            }
        }

        if (!sawAnyRow)
        {
            return null;
        }

        var node = BuildNode(prefix, prefix.Length == 0 ? "/" : LastSegment(prefix), 0, depth, here, under, childrenOf);

        // BuildNode addresses root as the empty prefix; every caller addresses it as "/" —
        // BrowseCommand.Loop compares its own path against "/", prints node.Path in the header,
        // and passes node.Path to TopFacts.
        return prefix.Length == 0 ? node with { Path = normalized } : node;
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

        var facts = FactStore.ReadAt(connection, path);

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

    private static BrowseNode BuildNode(
        string path,
        string name,
        int level,
        int depth,
        Dictionary<string, int> here,
        Dictionary<string, int> under,
        Dictionary<string, Dictionary<string, string>> childrenOf)
    {
        var hereCount = here.GetValueOrDefault(path);
        var underCount = under.GetValueOrDefault(path);

        if (level == depth || !childrenOf.TryGetValue(path, out var siblings))
        {
            return new BrowseNode(path, name, hereCount, underCount, [], 0);
        }

        var unordered = siblings
            .Select(pair => (
                Display: pair.Key,
                ChildPath: pair.Value,
                Total: here.GetValueOrDefault(pair.Value) + under.GetValueOrDefault(pair.Value)));

        var ordered = OrderChildren(unordered);

        var children = new List<BrowseNode>(Math.Min(ordered.Count, MaxChildrenShown));
        foreach (var entry in ordered.Take(MaxChildrenShown))
        {
            children.Add(BuildNode(entry.ChildPath, entry.Display, level + 1, depth, here, under, childrenOf));
        }

        return new BrowseNode(path, name, hereCount, underCount, children, Math.Max(0, ordered.Count - MaxChildrenShown));
    }

    /// <summary>
    /// Sort order for a node's children: highest fact count first, ties broken ordinally by
    /// display name. Exposed at internal visibility so a test can drive it directly with an
    /// already-in-memory, arbitrarily-ordered sequence — the query's own row order is always
    /// path-ascending (SQLite sorts on the GROUP BY column), which happens to coincide with
    /// display-name-ordinal order for children of one parent, so a fixture built through
    /// <see cref="Browse"/> can never exercise a tie the way this ordering intends.
    /// </summary>
    internal static List<(string Display, string ChildPath, int Total)> OrderChildren(
        IEnumerable<(string Display, string ChildPath, int Total)> entries)
    {
        return entries
            .OrderByDescending(entry => entry.Total)
            .ThenBy(entry => entry.Display, StringComparer.Ordinal)
            .ToList();
    }

    private static string LastSegment(string path)
    {
        var index = path.LastIndexOfAny(['/', '#']);
        return index < 0 || index == path.Length - 1 ? path : path[(index + 1)..];
    }
}
