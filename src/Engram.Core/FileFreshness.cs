using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// Whether what the code index holds for one file still matches what is on disk.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IndexFreshness"/> and <see cref="RepoFreshness"/>, which decide which
/// repo is <i>due</i> to be re-scanned. Those run ahead of a question; this answers one at read
/// time, per file, about an answer already retrieved. Both are needed: the background loop
/// services one repo per five-minute tick, so a file edited seconds ago is still described by the
/// index as it was several edits back, and nothing in a navigate result says so.
/// </para>
/// <para>
/// That gap matters more since <c>lookup-nudge</c> began steering symbol lookups to
/// <c>engram_navigate</c> first — the hook creates the reliance, so the answer has to be honest
/// about its age. Closing the window instead was rejected: <c>file-touched</c> may not open the
/// database (D4), so an edit only becomes a spool entry and no drain schedule can make a freshness
/// promise true. You cannot win the race; you can always say which side of it an answer came from.
/// </para>
/// <para>
/// mtime, not a re-hash. <c>file_state</c> also stores <c>blob_sha</c> and comparing it would be
/// exact, but that reads and hashes every returned file on every call — cost scaling with file
/// size rather than result count, on a path meant to be cheaper than grep. A <c>stat</c> is O(1).
/// The trade is directional on purpose: mtime can report <see cref="State.Stale"/> for a touch
/// that changed nothing, which merely costs the reader a re-read, and only misses a real edit when
/// a tool deliberately preserves mtime. It never reports <see cref="State.Fresh"/> for content the
/// index has not seen through ordinary editing.
/// </para>
/// </remarks>
public static class FileFreshness
{
    public enum State
    {
        /// <summary>Nothing to compare — not indexed, repo detached, or not a file-shaped entity path.</summary>
        Unknown,

        /// <summary>Not written since it was indexed.</summary>
        Fresh,

        /// <summary>Written after it was indexed; the index may describe older content.</summary>
        Stale,

        /// <summary>Indexed, but no longer on disk.</summary>
        Missing,
    }

    public readonly record struct Verdict(State State, TimeSpan Behind)
    {
        public static readonly Verdict Unknown = new(FileFreshness.State.Unknown, TimeSpan.Zero);

        /// <summary>Whether this verdict is worth showing the caller. Fresh and Unknown are not.</summary>
        public bool IsWorthReporting => State is FileFreshness.State.Stale or FileFreshness.State.Missing;

        public string Label => State.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Compares one entity path's recorded <c>indexed_at</c> against its file's last write time.
    /// Never throws: every failure to reach an answer is <see cref="State.Unknown"/>, because a
    /// freshness check that can break a lookup is worse than one that stays quiet.
    /// </summary>
    public static Verdict Check(SqliteConnection connection, string entityPath)
    {
        try
        {
            if (CodePaths.SplitRepoPath(entityPath) is not var (repoPath, relativePath))
            {
                return Verdict.Unknown;
            }

            var indexedAt = ScalarLong(
                connection,
                "SELECT indexed_at FROM file_state WHERE repo_path = $repo AND path = $path;",
                ("$repo", repoPath),
                ("$path", relativePath));

            if (indexedAt is null)
            {
                return Verdict.Unknown;
            }

            // disk_path goes NULL once a checkout is detached, so a detached repo yields Unknown
            // rather than Missing — "cannot look" and "it is gone" are different answers and only
            // the second is worth telling the caller.
            var diskPath = ScalarText(
                connection,
                "SELECT disk_path FROM repo_registry WHERE repo_path = $repo AND detached_at IS NULL;",
                ("$repo", repoPath));

            if (string.IsNullOrEmpty(diskPath))
            {
                return Verdict.Unknown;
            }

            var file = Path.Combine(diskPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                return new Verdict(State.Missing, TimeSpan.Zero);
            }

            // indexed_at has second resolution, so a write inside the same second as the index run
            // is not evidence of staleness — counting it would mark a freshly indexed file stale.
            var behind = File.GetLastWriteTimeUtc(file)
                - DateTimeOffset.FromUnixTimeSeconds(indexedAt.Value).UtcDateTime;

            return behind > TimeSpan.FromSeconds(1)
                ? new Verdict(State.Stale, behind)
                : new Verdict(State.Fresh, TimeSpan.Zero);
        }
        catch (SqliteException)
        {
            return Verdict.Unknown;
        }
        catch (IOException)
        {
            return Verdict.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return Verdict.Unknown;
        }
        catch (ArgumentException)
        {
            return Verdict.Unknown;
        }
    }

    private static long? ScalarLong(
        SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static string? ScalarText(
        SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
