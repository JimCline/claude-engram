using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>A fact to write. Belief content only — validity is the store's business.</summary>
public sealed record FactWrite(
    string SubjectPath,
    string SubjectKind,
    string Predicate,
    string Body,
    string Scope,
    string LearnedVia,
    string? Evidence = null,
    bool Regenerable = false,
    long? SessionId = null);

/// <summary>A fact as stored, with its subject joined back in.</summary>
public sealed record StoredFact(
    long Id,
    long SubjectId,
    string SubjectPath,
    string SubjectName,
    string Predicate,
    string Body,
    string Scope,
    string LearnedVia,
    bool Regenerable,
    string? Evidence,
    long ValidFrom,
    long? ValidTo,
    long? SupersededBy,
    long CreatedAt);

public sealed record RememberResult(long FactId, long? SupersededFactId);

/// <summary>
/// Reads and writes facts. The temporal model lives here: writing a belief about a
/// subject+predicate that already has a live belief closes the old one rather than
/// replacing it, so what was believed at any past instant stays answerable.
/// </summary>
/// <remarks>
/// Timestamps are unix seconds, following the spec. Two facts written in the same second
/// are therefore tied on time — ordering within a tie is by <c>fact.id</c>, which is a
/// monotonic rowid and a total order. Supersession chains follow ids, not timestamps, so
/// the tie is not ambiguous where it would matter.
/// </remarks>
public static class FactStore
{
    public const string DefaultSupersessionReason = "superseded by a newer statement";

    public static RememberResult Remember(
        SqliteConnection connection,
        FactWrite write,
        DateTimeOffset now,
        string reason = DefaultSupersessionReason)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        var result = Remember(connection, transaction, write, now, reason);
        transaction.Commit();

        return result;
    }

    /// <summary>
    /// Writes one fact inside a caller's transaction, for batches that should land together.
    /// </summary>
    public static RememberResult Remember(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FactWrite write,
        DateTimeOffset now,
        string reason = DefaultSupersessionReason)
    {
        var timestamp = now.ToUnixTimeSeconds();
        var subjectId = EnsureEntity(connection, transaction, write.SubjectPath, write.SubjectKind, timestamp);
        var superseded = FindLiveFactId(connection, transaction, subjectId, write.Predicate);

        // Close first, insert second. ux_fact_live is a UNIQUE partial index over live facts,
        // and SQLite checks it per statement rather than at commit, so inserting the
        // replacement while the incumbent is still live violates it inside the transaction.
        // Closing first also gets the FTS eviction trigger and the insert trigger in the
        // right order, leaving exactly one live row indexed at every point.
        if (superseded is { } oldId)
        {
            Execute(
                connection,
                transaction,
                "UPDATE fact SET valid_to = $now WHERE id = $id;",
                ("$now", timestamp),
                ("$id", oldId));
        }

        var factId = InsertFact(connection, transaction, write, subjectId, timestamp);

        if (superseded is { } closedId)
        {
            Execute(
                connection,
                transaction,
                "UPDATE fact SET superseded_by = $new WHERE id = $old;",
                ("$new", factId),
                ("$old", closedId));

            Execute(
                connection,
                transaction,
                """
                INSERT INTO supersession (old_fact_id, new_fact_id, reason, evidence, session_id, created_at)
                VALUES ($old, $new, $reason, $evidence, $session, $now);
                """,
                ("$old", closedId),
                ("$new", factId),
                ("$reason", reason),
                ("$evidence", (object?)write.Evidence ?? DBNull.Value),
                ("$session", (object?)write.SessionId ?? DBNull.Value),
                ("$now", timestamp));
        }

        return new RememberResult(factId, superseded);
    }

    /// <summary>
    /// Closes a fact without replacing it. The fact is not deleted — a forgotten belief is
    /// still a belief that was held, and D8 forbids destroying authored truth.
    /// </summary>
    public static bool Forget(SqliteConnection connection, long factId, string reason, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds();

        using var transaction = EngramDatabase.BeginWrite(connection);

        var closed = Execute(
            connection,
            transaction,
            "UPDATE fact SET valid_to = $now WHERE id = $id AND valid_to IS NULL;",
            ("$now", timestamp),
            ("$id", factId));

        if (closed == 0)
        {
            transaction.Rollback();
            return false;
        }

        // new_fact_id stays NULL: closed and not replaced is exactly what forgetting means,
        // and the column is nullable so the state needs no sentinel.
        Execute(
            connection,
            transaction,
            """
            INSERT INTO supersession (old_fact_id, new_fact_id, reason, created_at)
            VALUES ($old, NULL, $reason, $now);
            """,
            ("$old", factId),
            ("$reason", reason),
            ("$now", timestamp));

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// The live fact for a subject path and predicate, or null if there is none.
    /// </summary>
    /// <remarks>
    /// The pair is what <c>ux_fact_live</c> is unique over, so this returns at most one row
    /// by construction rather than by convention.
    /// </remarks>
    public static long? FindLiveFactId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string subjectPath,
        string predicate) =>
        ScalarLong(
            connection,
            transaction,
            """
            SELECT f.id FROM fact f JOIN entity e ON e.id = f.subject_id
             WHERE e.path = $path AND f.predicate = $predicate AND f.valid_to IS NULL;
            """,
            ("$path", subjectPath),
            ("$predicate", predicate));

    public static StoredFact? ReadById(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectFactColumns + " WHERE f.id = $id;";
        command.Parameters.AddWithValue("$id", factId);

        return ReadFacts(command).FirstOrDefault();
    }

    public static IReadOnlyList<StoredFact> ReadLive(SqliteConnection connection, string? scope = null)
    {
        var sql = SelectFactColumns
            + " WHERE f.valid_to IS NULL"
            + (scope is null ? string.Empty : " AND f.scope = $scope")
            + " ORDER BY f.id;";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (scope is not null)
        {
            command.Parameters.AddWithValue("$scope", scope);
        }

        return ReadFacts(command);
    }

    /// <summary>
    /// Every subject and predicate this store has ever held a fact for, including facts that
    /// have since been closed.
    /// </summary>
    /// <remarks>
    /// The question this answers is "was anything ever believed about this?", which is not
    /// the same as "is anything believed about it now" and cannot be derived from the live
    /// set. A closed fact and a fact that never existed are indistinguishable in
    /// <see cref="ReadLive"/>, and treating them alike is how a re-seed resurrects what a
    /// user deliberately forgot.
    /// </remarks>
    public static HashSet<(string Path, string Predicate)> ReadEverWritten(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT e.path, f.predicate FROM fact f JOIN entity e ON e.id = f.subject_id;";

        var seen = new HashSet<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            seen.Add((reader.GetString(0), reader.GetString(1)));
        }

        return seen;
    }

    /// <summary>
    /// What was believed at an instant. The half-open window is deliberate: a fact closed at
    /// T and its replacement opened at T must not both answer a query as of T.
    /// </summary>
    public static IReadOnlyList<StoredFact> ReadAsOf(SqliteConnection connection, DateTimeOffset instant)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectFactColumns
            + " WHERE f.valid_from <= $at AND (f.valid_to IS NULL OR f.valid_to > $at) ORDER BY f.id;";
        command.Parameters.AddWithValue("$at", instant.ToUnixTimeSeconds());

        return ReadFacts(command);
    }

    /// <summary>
    /// Live facts about a subject and everything beneath it, as a range scan over the path
    /// index rather than a scan-and-filter (D2).
    /// </summary>
    public static IReadOnlyList<StoredFact> ReadSubtree(SqliteConnection connection, string pathPrefix)
    {
        var exact = pathPrefix.TrimEnd('/');
        var low = exact + "/";

        // Upper bound is the prefix with its final character incremented: '/' is 0x2F, so the
        // bound is the same string ending in '0'. Every path under low sorts below it and
        // nothing else can sort between them, which makes this exact rather than approximate.
        //
        // The alternatives are worse. A U+FFFD sentinel — what the schema comment sketches —
        // is not actually the largest encodable character, so a path containing an astral
        // character (U+10000 and up, which encode above it in UTF-8) would sort past the bound
        // and vanish from its own subtree. LIKE 'prefix%' is case-insensitive for ASCII by
        // default, which disqualifies it from the index range optimization entirely.
        var high = string.Concat(low.AsSpan(0, low.Length - 1), ((char)(low[^1] + 1)).ToString());

        using var command = connection.CreateCommand();
        command.CommandText = SelectFactColumns
            + """
               WHERE f.valid_to IS NULL
                 AND (e.path = $exact OR (e.path >= $low AND e.path < $high))
               ORDER BY e.path, f.id;
              """;
        command.Parameters.AddWithValue("$exact", exact);
        command.Parameters.AddWithValue("$low", low);
        command.Parameters.AddWithValue("$high", high);

        return ReadFacts(command);
    }

    public static IReadOnlyList<StoredFact> History(SqliteConnection connection, string subjectPath, string predicate)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectFactColumns
            + " WHERE e.path = $path AND f.predicate = $predicate ORDER BY f.id;";
        command.Parameters.AddWithValue("$path", subjectPath);
        command.Parameters.AddWithValue("$predicate", predicate);

        return ReadFacts(command);
    }

    /// <summary>
    /// Lexical search over live facts (D3). Ranked by bm25, best first.
    /// </summary>
    public static IReadOnlyList<StoredFact> Search(SqliteConnection connection, string query, int limit)
    {
        var match = ToMatchExpression(query);
        if (match.Length == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = SelectFactColumns
            + """
               JOIN fact_fts ON fact_fts.rowid = f.id
              WHERE fact_fts MATCH $match AND f.valid_to IS NULL
              ORDER BY bm25(fact_fts) LIMIT $limit;
              """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);

        return ReadFacts(command);
    }

    /// <summary>
    /// Turns arbitrary user text into an FTS5 expression. Every token is wrapped in double
    /// quotes, which makes it a literal string rather than syntax: unquoted, a query
    /// containing <c>AND</c>, <c>*</c>, <c>(</c>, or a stray <c>"</c> is either interpreted
    /// as an operator the user did not intend or rejected outright with a syntax error, and
    /// a search box that throws on an apostrophe is not a search box.
    /// </summary>
    public static string ToMatchExpression(string query)
    {
        var tokens = query.Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var quoted = tokens
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Select(t => '"' + t + '"');

        return string.Join(" OR ", quoted);
    }

    public static long EnsureEntity(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string path,
        string kind,
        long createdAt,
        string? displayName = null)
    {
        var existing = ScalarLong(
            connection,
            transaction,
            "SELECT id FROM entity WHERE path = $path;",
            ("$path", path));

        if (existing is { } id)
        {
            return id;
        }

        // The name is the last path segment, denormalized for display. A path with no
        // separator is its own name. A caller supplies the name explicitly when the
        // segment is a slug: "claude-code hooks" and "claude code hooks" slug identically,
        // so the display text is not recoverable from the path and has to be stored.
        string name;
        if (displayName is not null)
        {
            name = displayName;
        }
        else
        {
            var separator = path.LastIndexOf('/');
            name = separator >= 0 && separator < path.Length - 1 ? path[(separator + 1)..] : path;
        }

        return ScalarLong(
            connection,
            transaction,
            """
            INSERT INTO entity (path, kind, name, created_at) VALUES ($path, $kind, $name, $now);
            SELECT last_insert_rowid();
            """,
            ("$path", path),
            ("$kind", kind),
            ("$name", name),
            ("$now", createdAt))!.Value;
    }

    private const string SelectFactColumns =
        """
        SELECT f.id, f.subject_id, e.path, e.name, f.predicate, f.body, f.scope,
               f.learned_via, f.regenerable, f.evidence, f.valid_from, f.valid_to,
               f.superseded_by, f.created_at
          FROM fact f
          JOIN entity e ON e.id = f.subject_id
        """;

    private static long InsertFact(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FactWrite write,
        long subjectId,
        long timestamp) =>
        ScalarLong(
            connection,
            transaction,
            """
            INSERT INTO fact
              (subject_id, predicate, body, path, scope, learned_via, regenerable,
               evidence, session_id, valid_from, created_at)
            VALUES
              ($subject, $predicate, $body, $path, $scope, $learnedVia, $regenerable,
               $evidence, $session, $now, $now);
            SELECT last_insert_rowid();
            """,
            ("$subject", subjectId),
            ("$predicate", write.Predicate),
            ("$body", write.Body),
            ("$path", write.SubjectPath),
            ("$scope", write.Scope),
            ("$learnedVia", write.LearnedVia),
            ("$regenerable", write.Regenerable ? 1 : 0),
            ("$evidence", (object?)write.Evidence ?? DBNull.Value),
            ("$session", (object?)write.SessionId ?? DBNull.Value),
            ("$now", timestamp))!.Value;

    private static long? FindLiveFactId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        string predicate) =>
        ScalarLong(
            connection,
            transaction,
            "SELECT id FROM fact WHERE subject_id = $subject AND predicate = $predicate AND valid_to IS NULL;",
            ("$subject", subjectId),
            ("$predicate", predicate));

    private static List<StoredFact> ReadFacts(SqliteCommand command)
    {
        var facts = new List<StoredFact>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            facts.Add(new StoredFact(
                Id: reader.GetInt64(0),
                SubjectId: reader.GetInt64(1),
                SubjectPath: reader.GetString(2),
                SubjectName: reader.GetString(3),
                Predicate: reader.GetString(4),
                Body: reader.GetString(5),
                Scope: reader.GetString(6),
                LearnedVia: reader.GetString(7),
                Regenerable: reader.GetInt64(8) != 0,
                Evidence: reader.IsDBNull(9) ? null : reader.GetString(9),
                ValidFrom: reader.GetInt64(10),
                ValidTo: reader.IsDBNull(11) ? null : reader.GetInt64(11),
                SupersededBy: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                CreatedAt: reader.GetInt64(13)));
        }

        return facts;
    }

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = Build(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static long? ScalarLong(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = Build(connection, transaction, sql, parameters);
        var value = command.ExecuteScalar();

        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static SqliteCommand Build(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
