using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>What <see cref="FactTokenIndex.ReadState"/> found in <c>schema_meta</c>.</summary>
public enum FactTokenIndexState
{
    /// <summary>No readiness key at all — never built, or built by a store that predates it.</summary>
    Unbuilt,

    /// <summary>Built, but stamped with a tokenizer version older than <see cref="FactTokenIndex.CurrentVersion"/>.</summary>
    VersionMismatch,

    /// <summary>Present and current. <see cref="FactTokenIndex.Rebuild"/> would produce the same table.</summary>
    Ready,
}

/// <summary>
/// The literal-token overlap index over live facts: <c>fact_token(token, fact_id)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Maintained from C#, not from SQL triggers the way <c>fact_fts</c> is. A trigger cannot call
/// <see cref="Tokenizer"/>, and expressing tokenization a second time in SQL is exactly the kind
/// of second implementation that drifts from the first. So every write goes through <see
/// cref="Add"/> or <see cref="Remove"/>, called from the same handful of chokepoints that already
/// exist for <c>fact_fts</c>: <c>FactStore.InsertFact</c>, <c>FactStore.Remember</c>'s close,
/// <c>FactStore.Forget</c>, <c>FactJournal.Insert</c>, and <c>FactJournal.Link</c>.
/// </para>
/// <para>
/// Holds live facts only, mirroring <c>fact_fts</c>. <c>MoveSubtree</c> and the repair path's
/// path re-derivation touch neither: <see cref="TextFor"/> reads the subject's <em>name</em>, not
/// its path, so a rename changes no indexed token.
/// </para>
/// </remarks>
public static class FactTokenIndex
{
    /// <summary>
    /// Bumped whenever <see cref="Tokenizer"/> or <see cref="TextFor"/> changes what a fact
    /// contributes. A stored value that does not match this means the table needs a rebuild —
    /// not that anything is broken, since recall reports the overlap lane unavailable rather
    /// than fail (spec ruling 3: no scanning fallback).
    /// </summary>
    internal const int CurrentVersion = 1;

    private const string ReadinessKey = "fact_token_version";

    /// <summary>
    /// Bounds bound parameters per statement, for both the insert batches and the candidate
    /// reads. SQLite's ceiling is 32,766 and both call sites size their statement from a
    /// collection that has no bound of its own — a full rebuild's token stream, and every live
    /// fact absent from the table — so an unchunked statement is not a slow query but a throw
    /// (D58, which cost <c>RetrievalExplainer.ReadTiers</c> the same way).
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Indexes one fact's live tokens. Called once, right after the row exists, so the caller's
    /// transaction sees a consistent index the moment the fact does.
    /// </summary>
    public static void Add(SqliteConnection connection, SqliteTransaction transaction, long factId)
    {
        var (path, name, body) = ReadForIndexing(connection, transaction, factId);
        IndexText(connection, transaction, factId, TextFor(path, name, body));
    }

    /// <summary>Removes every token row for a fact — the closing counterpart to <see cref="Add"/>.</summary>
    public static void Remove(SqliteConnection connection, SqliteTransaction transaction, long factId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM fact_token WHERE fact_id = $fact;";
        command.Parameters.AddWithValue("$fact", factId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Live facts with no row in <c>fact_token</c> at all — a missed <see cref="Add"/> at one of
    /// the write-site chokepoints. A candidate is excluded when its own subject name and body
    /// tokenize to nothing indexable (an all-stopword, all-short-token fact): that fact never had
    /// a row to miss, so counting it would be a false positive rather than a caught defect.
    /// Checked against the corpus at only that candidate set, not the whole table, so this stays
    /// cheap enough to run on every dry run — most stores have zero candidates most of the time.
    /// </summary>
    /// <remarks>
    /// This does not check whether a <em>present</em> row's tokens are the right ones — "right
    /// fact, wrong tokens" needs a full recompute of every live fact, which is exactly the cost
    /// this detector exists to avoid paying on every dry run.
    /// </remarks>
    public static int CountMissing(SqliteConnection connection)
    {
        // NOT IN rather than the EXCEPT that CountExtra uses, and the asymmetry is measured, not
        // an oversight: at 50,097 live facts against 701,358 token rows this form runs 22 ms and
        // the EXCEPT rewrite 42 ms, five alternating runs each. SQLite plans NOT IN as a Bloom
        // filter probed while scanning fact; EXCEPT materializes a temp B-tree for the set
        // difference. Making the two detectors read alike costs 20 ms on the larger set.
        var candidates = ReadIds(
            connection,
            "SELECT id FROM fact WHERE valid_to IS NULL "
            + "AND id NOT IN (SELECT DISTINCT fact_id FROM fact_token);");

        if (candidates.Count == 0)
        {
            return 0;
        }

        var rows = ReadManyForIndexing(connection, candidates);
        var missing = 0;
        foreach (var id in candidates)
        {
            if (rows.TryGetValue(id, out var fact) && ProducesIndexableTokens(fact.Path, fact.Name, fact.Body))
            {
                missing++;
            }
        }

        return missing;
    }

    /// <summary>
    /// <c>fact_token</c> rows whose <c>fact_id</c> is not a live fact — a missed <see
    /// cref="Remove"/> at a closing chokepoint (<c>Remember</c>'s supersession, <c>Forget</c>,
    /// <c>FactJournal.Link</c>).
    /// </summary>
    public static int CountExtra(SqliteConnection connection) => Scalar(
        connection,
        "SELECT count(*) FROM (SELECT DISTINCT fact_id FROM fact_token "
        + "EXCEPT SELECT id FROM fact WHERE valid_to IS NULL);");

    /// <summary>
    /// The text a fact contributes to the overlap index — the single authority <see
    /// cref="Add"/> tokenizes, so a caller that needs to reason about what got indexed can call
    /// this directly rather than re-deriving it.
    /// </summary>
    /// <remarks>
    /// A session note's entity is named after its path leaf — the statement's fingerprint —
    /// until something gives it a real subject (<see cref="SessionFacts.Append"/>). That default
    /// name is content-free, so <see cref="SessionFacts"/>'s own read path
    /// (<c>ToSessionFact</c>) reports no subject when the name still equals the leaf, and this
    /// mirrors it exactly rather than indexing a hex fingerprint as a word. A long-term fact's
    /// name is included unconditionally even when it, too, equals its path leaf — that is the
    /// ordinary case for every entity <c>EnsureEntity</c> names by default, and the ranker has
    /// always tokenized it (<c>CannedFact.Subject</c> is unconditional).
    /// </remarks>
    internal static string TextFor(string path, string subjectName, string body)
    {
        if (!IsSessionPath(path))
        {
            return subjectName + " " + body;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var leaf = segments.Length > 0 ? segments[^1] : path;
        var subject = string.Equals(subjectName, leaf, StringComparison.Ordinal) ? string.Empty : subjectName;

        return subject + " " + body;
    }

    private static bool IsSessionPath(string path) =>
        path.StartsWith(SessionFacts.Root + "/", StringComparison.Ordinal);

    /// <summary>
    /// Recomputes the whole table from <c>fact</c>. Derived state under D8, so — unlike a
    /// migration — it needs no snapshot before running.
    /// </summary>
    public static void Rebuild(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        Execute(connection, transaction, "DELETE FROM fact_token;");

        var pending = new List<(string Token, long FactId)>(BatchSize);
        foreach (var (id, path, name, body) in ReadLiveForIndexing(connection, transaction))
        {
            foreach (var token in Tokenizer.Tokenize(TextFor(path, name, body)))
            {
                if (!Tokenizer.IsIndexable(token))
                {
                    continue;
                }

                pending.Add((token, id));
                if (pending.Count >= BatchSize)
                {
                    InsertBatch(connection, transaction, pending);
                    pending.Clear();
                }
            }
        }

        InsertBatch(connection, transaction, pending);
        WriteReadiness(connection, transaction);
    }

    /// <summary>
    /// Whether the table matches what <see cref="Rebuild"/> would produce right now: present and
    /// stamped with <see cref="CurrentVersion"/>. Anything else — absent, or stamped with an
    /// older version — means unbuilt, per spec ruling 3: there is no scanning fallback, so an
    /// unbuilt index simply costs the overlap lane rather than failing recall.
    /// </summary>
    public static bool IsReady(SqliteConnection connection) => ReadState(connection) == FactTokenIndexState.Ready;

    /// <summary>The three states doctor distinguishes, rather than the single Boolean the writers need.</summary>
    public static FactTokenIndexState ReadState(SqliteConnection connection)
    {
        var raw = EngramDatabase.ReadMeta(connection, ReadinessKey);
        if (raw is null)
        {
            return FactTokenIndexState.Unbuilt;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return FactTokenIndexState.Unbuilt;
        }

        return version == CurrentVersion ? FactTokenIndexState.Ready : FactTokenIndexState.VersionMismatch;
    }

    /// <summary>
    /// Builds or rebuilds the table if it is missing or stamped with a stale tokenizer version.
    /// A no-op otherwise, so callers on a hot path (server startup, session-start maintenance)
    /// can call this unconditionally.
    /// </summary>
    public static void EnsureBuilt(SqliteConnection connection)
    {
        if (IsReady(connection))
        {
            return;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);
        Rebuild(connection, transaction);
        transaction.Commit();
    }

    private static void WriteReadiness(SqliteConnection connection, SqliteTransaction? transaction) =>
        EngramDatabase.WriteMeta(
            connection, transaction, ReadinessKey, CurrentVersion.ToString(CultureInfo.InvariantCulture));

    private static void IndexText(SqliteConnection connection, SqliteTransaction transaction, long factId, string text)
    {
        var pending = new List<(string Token, long FactId)>(BatchSize);
        foreach (var token in Tokenizer.Tokenize(text))
        {
            if (!Tokenizer.IsIndexable(token))
            {
                continue;
            }

            pending.Add((token, factId));
            if (pending.Count >= BatchSize)
            {
                InsertBatch(connection, transaction, pending);
                pending.Clear();
            }
        }

        InsertBatch(connection, transaction, pending);
    }

    private static void InsertBatch(
        SqliteConnection connection, SqliteTransaction? transaction, List<(string Token, long FactId)> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var values = new List<string>(pending.Count);
        for (var i = 0; i < pending.Count; i++)
        {
            var tokenParam = "$t" + i.ToString(CultureInfo.InvariantCulture);
            var factParam = "$f" + i.ToString(CultureInfo.InvariantCulture);
            values.Add("(" + tokenParam + "," + factParam + ")");
            command.Parameters.AddWithValue(tokenParam, pending[i].Token);
            command.Parameters.AddWithValue(factParam, pending[i].FactId);
        }

        command.CommandText = "INSERT INTO fact_token (token, fact_id) VALUES " + string.Join(",", values) + ";";
        command.ExecuteNonQuery();
    }

    private static (string Path, string Name, string Body) ReadForIndexing(
        SqliteConnection connection, SqliteTransaction transaction, long factId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT f.path, e.name, f.body FROM fact f JOIN entity e ON e.id = f.subject_id WHERE f.id = $id;";
        command.Parameters.AddWithValue("$id", factId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"fact_token: no fact with id {factId} to index.");
        }

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static List<(long Id, string Path, string Name, string Body)> ReadLiveForIndexing(
        SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT f.id, f.path, e.name, f.body
              FROM fact f
              JOIN entity e ON e.id = f.subject_id
             WHERE f.valid_to IS NULL;
            """;

        var facts = new List<(long, string, string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            facts.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return facts;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool ProducesIndexableTokens(string path, string name, string body)
    {
        foreach (var token in Tokenizer.Tokenize(TextFor(path, name, body)))
        {
            if (Tokenizer.IsIndexable(token))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<long, (string Path, string Name, string Body)> ReadManyForIndexing(
        SqliteConnection connection, IReadOnlyList<long> factIds)
    {
        var result = new Dictionary<long, (string, string, string)>();

        for (var start = 0; start < factIds.Count; start += BatchSize)
        {
            var count = Math.Min(BatchSize, factIds.Count - start);

            using var command = connection.CreateCommand();
            var placeholders = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var parameter = "$id" + i.ToString(CultureInfo.InvariantCulture);
                placeholders.Add(parameter);
                command.Parameters.AddWithValue(parameter, factIds[start + i]);
            }

            command.CommandText =
                "SELECT f.id, f.path, e.name, f.body FROM fact f JOIN entity e ON e.id = f.subject_id "
                + "WHERE f.id IN (" + string.Join(",", placeholders) + ");";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
            }
        }

        return result;
    }

    private static List<long> ReadIds(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(long)command.ExecuteScalar()!;
    }
}
