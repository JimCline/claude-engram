using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Equivalence guard for the single-pass accumulation in <see cref="MemoryBrowser.Browse"/>
/// (docs/memory-expansion/05b-browse-depth-bound-spec.md, Change 1): diffs it against the
/// materialize-then-refold implementation it replaced. <see cref="LegacyBrowser"/> exists only
/// so this suite has something independent to diff against — shipping code keeps one
/// implementation, so the old one lives here and nowhere in <c>src/</c>.
/// </summary>
public class MemoryBrowserAccumulationEquivalenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static int s_offset;

    private static void Seed(SqliteConnection connection, string path, string predicate, string body)
    {
        FactStore.Remember(
            connection,
            new FactWrite(path, "note", predicate, body, "notes", "stated"),
            T0.AddSeconds(s_offset++));
    }

    private static SqliteConnection BuildFixture(SandboxHome sandbox)
    {
        var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // Two live facts on one path (FactsHere > 1), a path several segments beyond
        // MaxDepth so a deep row still has to fold into the deepest visible ancestor's
        // FactsUnder, hash-boundary children beside slash children, and a zero-fact entity
        // (an unforgotten predicate closed, leaving the entity behind per D8) so the
        // LEFT JOIN's zero-count rows exercise both implementations identically.
        Seed(connection, "/people/jim/preferences", "states", "prefers dark mode");
        Seed(connection, "/people/jim/preferences", "confirms", "checked twice");
        Seed(connection, "/people/ada", "states", "wrote the first algorithm");
        Seed(connection, "/people/ada/notes/early/detail#one", "states", "five segments below root");
        Seed(connection, "/code/Auth.cs#ValidateToken", "states", "checks the token signature");
        Seed(connection, "/code/Auth.cs#Encode", "states", "encodes the payload");
        Seed(connection, "/code/Startup.cs", "states", "wires up the container");

        FactStore.Remember(connection, new FactWrite("/people/jim/archived", "note", "states", "old note", "notes", "stated"), T0);
        FactStore.Forget(connection, FactStore.ReadAt(connection, "/people/jim/archived")[0].Id, "superseded", T0.AddSeconds(1));

        return connection;
    }

    private static void AssertNodesEqual(BrowseNode? expected, BrowseNode? actual)
    {
        if (expected is null || actual is null)
        {
            Assert.Equal(expected is null, actual is null);
            return;
        }

        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.FactsHere, actual.FactsHere);
        Assert.Equal(expected.FactsUnder, actual.FactsUnder);
        Assert.Equal(expected.ChildrenOmitted, actual.ChildrenOmitted);
        Assert.Equal(expected.Children.Count, actual.Children.Count);

        for (var i = 0; i < expected.Children.Count; i++)
        {
            AssertNodesEqual(expected.Children[i], actual.Children[i]);
        }
    }

    [Theory]
    [InlineData("/", 1)]
    [InlineData("/", 2)]
    [InlineData("/", 3)]
    [InlineData("/people", 1)]
    [InlineData("/people/jim", 2)]
    [InlineData("/code", 2)]
    [InlineData("/code/Auth.cs", 1)]
    [InlineData("/nonexistent", 1)]
    public void Browse_MatchesTheLegacyMaterializeAndRefoldImplementation(string path, int depth)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = BuildFixture(sandbox);

        var expected = LegacyBrowser.Browse(connection, path, depth);
        var actual = MemoryBrowser.Browse(connection, path, depth);

        AssertNodesEqual(expected, actual);
    }

    /// <summary>
    /// The materialize-then-refold implementation <see cref="MemoryBrowser.Browse"/> replaced
    /// (docs/memory-expansion/05a-browse-root-fix-spec.md's root fix carried forward), kept
    /// only as an equivalence oracle.
    /// </summary>
    private static class LegacyBrowser
    {
        public static BrowseNode? Browse(SqliteConnection connection, string path, int depth)
        {
            var normalized = path.TrimEnd('/');
            if (normalized.Length == 0)
            {
                normalized = "/";
            }

            depth = Math.Clamp(depth, 1, MemoryBrowser.MaxDepth);
            var prefix = normalized == "/" ? string.Empty : normalized;

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
                command.Parameters.AddWithValue("$path", prefix);
                command.Parameters.AddWithValue("$len", prefix.Length);

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

            var node = Fold(prefix, prefix.Length == 0 ? "/" : LastSegment(prefix), counts, depth);
            return prefix.Length == 0 ? node with { Path = normalized } : node;
        }

        private static BrowseNode Fold(string path, string name, Dictionary<string, int> counts, int depth)
        {
            var here = counts.GetValueOrDefault(path, 0);

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

            var children = new List<BrowseNode>(Math.Min(ordered.Count, MemoryBrowser.MaxChildrenShown));
            foreach (var (display, (childPath, facts)) in ordered.Take(MemoryBrowser.MaxChildrenShown))
            {
                var childHere = counts.GetValueOrDefault(childPath, 0);
                children.Add(depth > 1
                    ? Fold(childPath, display, counts, depth - 1)
                    : new BrowseNode(childPath, display, childHere, facts - childHere, [], 0));
            }

            var under = ordered.Sum(pair => pair.Value.Facts);
            return new BrowseNode(path, name, here, under, children, Math.Max(0, ordered.Count - MemoryBrowser.MaxChildrenShown));
        }

        private static string LastSegment(string path)
        {
            var index = path.LastIndexOfAny(['/', '#']);
            return index < 0 || index == path.Length - 1 ? path : path[(index + 1)..];
        }
    }
}
