using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>One fact as the journal carries it: everything needed to write it somewhere else.</summary>
public sealed record JournalFact(
    long Id,
    string Subject,
    string SubjectKind,
    string Predicate,
    string Body,
    string? Object,
    string? ObjectKind,
    string Scope,
    string LearnedVia,
    bool Regenerable,
    string? Evidence,
    long ValidFrom,
    long? ValidTo,
    long? SupersededBy,
    string? SupersessionReason,
    long CreatedAt);

/// <summary>What a replay did, or would do.</summary>
public sealed record ReplayResult(int Written, int AlreadyPresent, int Unresolved);

/// <summary>
/// The store's facts as plain text, and the way back from it.
/// </summary>
/// <remarks>
/// <para><b>The tier a <c>.db</c> snapshot cannot be.</b> A snapshot restores only into the schema
/// version that wrote it — that is why the version is in its filename and why restore refuses a
/// mismatch. This is text: it carries paths and predicates rather than row ids and table shapes,
/// so it replays into any later schema, and it can be read by a person, grepped, and diffed when
/// the thing you need to know is what the store believed rather than how it stored it.</para>
///
/// <para><b>Rewritten whole, atomically, rather than appended to.</b> The plan for this said
/// incremental — facts are append-only, so in principle each run need only write what is new.
/// Building it, the simpler design won and the reasoning is worth keeping. Appending needs a
/// watermark, and a watermark is a second copy of the truth that can disagree with the file it
/// describes. It also needs an ordering rule between closing an old fact and writing the one that
/// superseded it, because the live-fact uniqueness constraint is violated for as long as both look
/// live. A whole-file rewrite has neither: every line carries the fact's final validity, replay is
/// one pass, and the result is verifiable against the store by inspection. The cost is O(facts)
/// per run instead of O(new facts), which at an hourly cadence in a detached process is not a cost
/// yet. When it is, it will be a measured change rather than a guessed one.</para>
///
/// <para>Atomic because whole-file rewriting is the one thing that could destroy the archive it is
/// maintaining: written to <c>.partial</c> and renamed, so a process killed halfway leaves the
/// previous complete journal exactly where it was.</para>
/// </remarks>
public static class FactJournal
{
    public const string FileName = "facts.jsonl";

    /// <summary>
    /// Bumped when a reader would misunderstand an older file, never for adding a field — readers
    /// ignore fields they do not know, which is most of the point of using text.
    /// </summary>
    public const int FormatVersion = 1;

    private const string PartialSuffix = ".partial";

    public static string PathIn(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Path.Combine(home.BackupDir, FileName);
    }

    /// <summary>Writes every fact in the store, live or closed, to the journal.</summary>
    /// <remarks>
    /// Closed facts are included deliberately. A journal of only what is currently believed cannot
    /// reconstruct why it is believed — the supersession chain is the record of a mind changing,
    /// and D8 protects it as authored truth exactly like the facts themselves.
    /// </remarks>
    public static int Write(SqliteConnection connection, EngramHome home, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);

        Directory.CreateDirectory(home.BackupDir);
        var final = PathIn(home);
        var partial = final + PartialSuffix;

        var written = 0;
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                var header = new JsonObject
                {
                    ["format"] = "engram-facts",
                    ["format_version"] = FormatVersion,
                    ["schema_version"] = EngramDatabase.ReadSchemaVersion(connection),
                    ["written_at"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                };
                writer.WriteLine(header.ToJsonString());

                foreach (var fact in Read(connection))
                {
                    writer.WriteLine(ToJson(fact).ToJsonString());
                    written++;
                }
            }

            File.Move(partial, final, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return written;
    }

    /// <summary>Every fact in the store, oldest first, with its subject and object resolved.</summary>
    public static IEnumerable<JournalFact> Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, se.path, se.kind, f.predicate, f.body, oe.path, oe.kind, f.scope,
                   f.learned_via, f.regenerable, f.evidence, f.valid_from, f.valid_to,
                   f.superseded_by, s.reason, f.created_at
            FROM fact f
            JOIN entity se ON se.id = f.subject_id
            LEFT JOIN entity oe ON oe.id = f.object_id
            LEFT JOIN supersession s ON s.old_fact_id = f.id
            ORDER BY f.id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new JournalFact(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9) != 0,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetInt64(15));
        }
    }

    /// <summary>Parses a journal file, skipping the header and anything unreadable.</summary>
    /// <remarks>
    /// A malformed line is skipped rather than fatal. This file is read when something has already
    /// gone wrong, and refusing to recover 4,000 facts because one line is truncated would be the
    /// tool choosing its own tidiness over the user's data. What was skipped is counted and
    /// reported, so nobody has to guess whether the recovery was complete.
    /// </remarks>
    public static IReadOnlyList<JournalFact> Parse(IEnumerable<string> lines, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var facts = new List<JournalFact>();
        var bad = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                bad++;
                continue;
            }

            if (node is not JsonObject record)
            {
                bad++;
                continue;
            }

            // The header, which carries no fact.
            if (record.ContainsKey("format"))
            {
                continue;
            }

            var fact = FromJson(record);
            if (fact is null)
            {
                bad++;
                continue;
            }

            facts.Add(fact);
        }

        skipped = bad;
        return facts;
    }

    /// <summary>
    /// Writes journalled facts into a store, skipping any it already holds.
    /// </summary>
    /// <remarks>
    /// <para>Two passes, because a fact's <c>superseded_by</c> points at another fact whose new id
    /// is not known until it has been written. The first pass writes the facts and builds the
    /// mapping from journalled ids to real ones; the second links the supersessions.</para>
    ///
    /// <para>Idempotent by content — a fact matching an existing subject, predicate, body and
    /// <c>valid_from</c> is left alone rather than duplicated. This is what makes replay safe to
    /// run twice, and safe against a store that was partially recovered by other means. It is not
    /// a merge: nothing here rewrites or closes a fact the target store already had. Facts are
    /// append-only (D8), and a recovery tool that could silently retire live beliefs would be a
    /// worse problem than the one it was called to fix.</para>
    ///
    /// <para><c>session_id</c> is deliberately dropped. Sessions are local to a store, so carrying
    /// the number across would point a recovered fact at an unrelated session or a missing one.
    /// The provenance that survives is the part that means something anywhere: the subject, the
    /// predicate, how it was learned, and when.</para>
    /// </remarks>
    public static ReplayResult Replay(
        SqliteConnection connection,
        IReadOnlyList<JournalFact> facts,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(facts);

        var written = 0;
        var present = 0;
        var unresolved = 0;
        var idMap = new Dictionary<long, long>();

        if (!apply)
        {
            foreach (var fact in facts)
            {
                if (Existing(connection, null, fact) is not null)
                {
                    present++;
                }
                else
                {
                    written++;
                }
            }

            return new ReplayResult(written, present, 0);
        }

        using var transaction = EngramDatabase.BeginWrite(connection);

        foreach (var fact in facts)
        {
            var existing = Existing(connection, transaction, fact);
            if (existing is { } id)
            {
                idMap[fact.Id] = id;
                present++;
                continue;
            }

            idMap[fact.Id] = Insert(connection, transaction, fact);
            written++;
        }

        foreach (var fact in facts)
        {
            if (fact.SupersededBy is not { } oldTarget || !idMap.TryGetValue(fact.Id, out var newId))
            {
                continue;
            }

            if (!idMap.TryGetValue(oldTarget, out var newTarget))
            {
                // The superseding fact is not in this journal. The belief still closed at the
                // recorded time; only the pointer to what replaced it is lost, which is strictly
                // better than dropping the fact or inventing a target for it.
                unresolved++;
                continue;
            }

            Link(connection, transaction, newId, newTarget, fact);
        }

        transaction.Commit();
        return new ReplayResult(written, present, unresolved);
    }

    private static long? Existing(SqliteConnection connection, SqliteTransaction? transaction, JournalFact fact)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT f.id FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.predicate = $predicate AND f.body = $body
              AND f.valid_from = $validFrom
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", fact.Subject);
        command.Parameters.AddWithValue("$predicate", fact.Predicate);
        command.Parameters.AddWithValue("$body", fact.Body);
        command.Parameters.AddWithValue("$validFrom", fact.ValidFrom);

        return command.ExecuteScalar() is long id ? id : null;
    }

    private static long Insert(SqliteConnection connection, SqliteTransaction transaction, JournalFact fact)
    {
        var subjectId = EnsureEntity(connection, transaction, fact.Subject, fact.SubjectKind, fact.CreatedAt);
        long? objectId = fact.Object is { Length: > 0 }
            ? EnsureEntity(connection, transaction, fact.Object, fact.ObjectKind ?? "concept", fact.CreatedAt)
            : null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO fact (subject_id, predicate, body, object_id, path, scope, learned_via,
                              regenerable, evidence, session_id, valid_from, valid_to, created_at)
            VALUES ($subject, $predicate, $body, $object, $path, $scope, $learnedVia,
                    $regenerable, $evidence, NULL, $validFrom, $validTo, $createdAt)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$subject", subjectId);
        command.Parameters.AddWithValue("$predicate", fact.Predicate);
        command.Parameters.AddWithValue("$body", fact.Body);
        command.Parameters.AddWithValue("$object", (object?)objectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", fact.Subject);
        command.Parameters.AddWithValue("$scope", fact.Scope);
        command.Parameters.AddWithValue("$learnedVia", fact.LearnedVia);
        command.Parameters.AddWithValue("$regenerable", fact.Regenerable ? 1 : 0);
        command.Parameters.AddWithValue("$evidence", (object?)fact.Evidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$validFrom", fact.ValidFrom);
        command.Parameters.AddWithValue("$validTo", (object?)fact.ValidTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", fact.CreatedAt);

        return (long)command.ExecuteScalar()!;
    }

    private static void Link(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long factId,
        long supersededBy,
        JournalFact fact)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE fact SET superseded_by = $target WHERE id = $id;";
        update.Parameters.AddWithValue("$target", supersededBy);
        update.Parameters.AddWithValue("$id", factId);
        update.ExecuteNonQuery();

        using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText =
            """
            INSERT INTO supersession (old_fact_id, new_fact_id, reason, created_at)
            VALUES ($old, $new, $reason, $createdAt)
            ON CONFLICT(old_fact_id) DO NOTHING;
            """;
        record.Parameters.AddWithValue("$old", factId);
        record.Parameters.AddWithValue("$new", supersededBy);
        record.Parameters.AddWithValue("$reason", fact.SupersessionReason ?? "replayed from the fact journal");
        record.Parameters.AddWithValue("$createdAt", fact.ValidTo ?? fact.CreatedAt);
        record.ExecuteNonQuery();
    }

    private static long EnsureEntity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        string kind,
        long createdAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO entity (path, kind, name, created_at) VALUES ($path, $kind, $name, $createdAt)
            ON CONFLICT(path) DO UPDATE SET path = excluded.path
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", LastSegment(path));
        command.Parameters.AddWithValue("$createdAt", createdAt);

        return (long)command.ExecuteScalar()!;
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    private static JsonObject ToJson(JournalFact fact) => new()
    {
        ["id"] = fact.Id,
        ["subject"] = fact.Subject,
        ["kind"] = fact.SubjectKind,
        ["predicate"] = fact.Predicate,
        ["body"] = fact.Body,
        ["object"] = fact.Object,
        ["object_kind"] = fact.ObjectKind,
        ["scope"] = fact.Scope,
        ["learned_via"] = fact.LearnedVia,
        ["regenerable"] = fact.Regenerable,
        ["evidence"] = fact.Evidence,
        ["valid_from"] = fact.ValidFrom,
        ["valid_to"] = fact.ValidTo,
        ["superseded_by"] = fact.SupersededBy,
        ["reason"] = fact.SupersessionReason,
        ["created_at"] = fact.CreatedAt,
    };

    private static JournalFact? FromJson(JsonObject record)
    {
        var subject = Text(record, "subject");
        var predicate = Text(record, "predicate");
        var body = Text(record, "body");

        if (subject is null || predicate is null || body is null)
        {
            return null;
        }

        return new JournalFact(
            Number(record, "id") ?? 0,
            subject,
            Text(record, "kind") ?? "concept",
            predicate,
            body,
            Text(record, "object"),
            Text(record, "object_kind"),
            Text(record, "scope") ?? "project",
            Text(record, "learned_via") ?? "stated",
            record["regenerable"]?.GetValue<bool>() ?? false,
            Text(record, "evidence"),
            Number(record, "valid_from") ?? 0,
            Number(record, "valid_to"),
            Number(record, "superseded_by"),
            Text(record, "reason"),
            Number(record, "created_at") ?? 0);
    }

    private static string? Text(JsonObject record, string key) =>
        record.TryGetPropertyValue(key, out var value) && value is JsonValue text
            ? text.GetValue<string?>()
            : null;

    private static long? Number(JsonObject record, string key) =>
        record.TryGetPropertyValue(key, out var value) && value is JsonValue number
            ? number.GetValue<long?>()
            : null;
}
