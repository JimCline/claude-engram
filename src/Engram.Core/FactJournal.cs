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
    long CreatedAt,
    string? Details = null);

/// <summary>What a replay did, or would do.</summary>
/// <param name="AlreadyPresent">
/// Journal records the target already held. Counted once per record, in pass 1 only — an idempotent
/// supersession edge adds nothing, because every record reaching that branch was already counted here (D68).
/// </param>
/// <param name="Conflicted">
/// Records nothing was recovered for. Two shapes: a fact left out because the target already believes
/// something else about that subject and predicate, and a fact whose body was already present but whose
/// supersession edge the target disagreed with (D68) — the second also counts
/// <paramref name="AlreadyPresent"/>, since both statements are true of it.
/// </param>
public sealed record ReplayResult(int Written, int AlreadyPresent, int Unresolved, int Conflicted);

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
                written = WriteTo(connection, writer, pathPrefix: null, now);
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

    /// <summary>
    /// Streams the journal to a writer — the whole store, or one subtree when
    /// <paramref name="pathPrefix"/> is given. This is what <c>export</c> produces, and the
    /// format is exactly the backup journal's on purpose: a bundle that is a filtered
    /// <c>facts.jsonl</c> is one <c>Parse</c>/<c>Replay</c> away from any store, with no
    /// second format to keep honest.
    /// </summary>
    public static int WriteTo(
        SqliteConnection connection,
        TextWriter writer,
        string? pathPrefix,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(writer);

        var header = new JsonObject
        {
            ["format"] = "engram-facts",
            ["format_version"] = FormatVersion,
            ["schema_version"] = EngramDatabase.ReadSchemaVersion(connection),
            ["written_at"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        };
        if (pathPrefix is not null)
        {
            header["path"] = pathPrefix;
        }

        writer.WriteLine(header.ToJsonString());

        var written = 0;
        foreach (var fact in Read(connection))
        {
            if (pathPrefix is not null && !InSubtree(fact.Subject, pathPrefix))
            {
                continue;
            }

            writer.WriteLine(ToJson(fact).ToJsonString());
            written++;
        }

        return written;
    }

    /// <summary>
    /// The subtree boundary MoveSubtree uses: the path itself, or a descendant across
    /// <c>/</c> or <c>#</c> — never a sibling that merely shares a spelling
    /// (<c>/code/api-docs</c> is not under <c>/code/api</c>).
    /// </summary>
    private static bool InSubtree(string subject, string prefix) =>
        subject.Length >= prefix.Length
        && subject.StartsWith(prefix, StringComparison.Ordinal)
        && (subject.Length == prefix.Length || subject[prefix.Length] is '/' or '#');

    /// <summary>Every fact in the store, oldest first, with its subject and object resolved.</summary>
    public static IEnumerable<JournalFact> Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, se.path, se.kind, f.predicate, f.body, oe.path, oe.kind, f.scope,
                   f.learned_via, f.regenerable, f.evidence, f.valid_from, f.valid_to,
                   f.superseded_by, s.reason, f.created_at, f.details
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
                reader.GetInt64(15),
                reader.IsDBNull(16) ? null : reader.GetString(16));
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
    /// <para><b>A live belief the target already holds is left alone, and the journalled one is
    /// dropped.</b> The schema permits one live fact per subject and predicate
    /// (<c>ux_fact_live</c>), so a journal whose fact disagrees with what the target currently
    /// believes cannot be written without closing that belief — which is precisely what the
    /// paragraph above forbids. Skipping is therefore the only move the constraint and D8 leave
    /// open, and it is reported rather than silent, because the difference between "already there"
    /// and "not recovered" is the whole question someone running a recovery tool is asking. Before
    /// this, the insert simply violated the index and took the entire replay down with it — a
    /// journal replayed into a store that had been initialised (and so carries the seeded corpus)
    /// recovered nothing at all.</para>
    ///
    /// <para><c>session_id</c> is deliberately dropped. Sessions are local to a store, so carrying
    /// the number across would point a recovered fact at an unrelated session or a missing one.
    /// The provenance that survives is the part that means something anywhere: the subject, the
    /// predicate, how it was learned, and when.</para>
    /// </remarks>
    public static ReplayResult Replay(
        SqliteConnection connection,
        IReadOnlyList<JournalFact> facts,
        bool apply) =>
        Replay(connection, facts, apply, out _);

    /// <summary>
    /// Same replay, also handing back the journal-id-to-target-id map it built — what
    /// <see cref="RelationJournal.Replay"/> resolves <c>relations.jsonl</c>'s fact references
    /// through, so a verdict never ends up pointed at the wrong row (D32).
    /// </summary>
    public static ReplayResult Replay(
        SqliteConnection connection,
        IReadOnlyList<JournalFact> facts,
        bool apply,
        out IReadOnlyDictionary<long, long> idMap)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(facts);

        var written = 0;
        var present = 0;
        var unresolved = 0;
        var conflicted = 0;
        var idMapBuilder = new Dictionary<long, long>();

        // Journal ids whose target row this replay inserted. Link may write superseded_by into
        // those rows and no others: a row that was already here carries the target's own account
        // of how the belief closed, and replay does not rewrite that (D68).
        var inserted = new HashSet<long>();

        // Subject+predicate pairs this replay has already claimed a live fact for. This is what the
        // dry run has instead of a transaction: an apply sees its own inserts through the store
        // query, so a journal holding two live facts for one subject — which only a merged bundle
        // can — resolves there without help. A dry run inserts nothing, so without this it would
        // count both as writable and promise a recovery the apply cannot deliver.
        var claimed = new HashSet<(string Subject, string Predicate)>();

        if (!apply)
        {
            foreach (var fact in facts)
            {
                if (Existing(connection, null, fact) is not null)
                {
                    present++;
                }
                else if (WouldDisplaceALiveBelief(connection, null, fact, claimed))
                {
                    conflicted++;
                }
                else
                {
                    written++;
                }
            }

            idMap = idMapBuilder;
            return new ReplayResult(written, present, 0, conflicted);
        }

        using var transaction = EngramDatabase.BeginWrite(connection);

        foreach (var fact in facts)
        {
            var existing = Existing(connection, transaction, fact);
            if (existing is { } match)
            {
                if (match.AddressUsable)
                {
                    idMapBuilder[fact.Id] = match.Id;
                }
                else if (fact.SupersededBy is not null)
                {
                    // Several closed duplicates and no live one: no basis to prefer any row as the
                    // address a supersession would point at (D68). The pointer is lost the same way
                    // a missing superseder is — see the unresolved branch below.
                    unresolved++;
                }

                present++;
                continue;
            }

            if (WouldDisplaceALiveBelief(connection, transaction, fact, claimed))
            {
                // No idMap entry on purpose: nothing was written, so a supersession pointing at
                // this fact has to come out as unresolved rather than aimed at some other row.
                conflicted++;
                continue;
            }

            idMapBuilder[fact.Id] = Insert(connection, transaction, fact);
            inserted.Add(fact.Id);
            written++;
        }

        foreach (var fact in facts)
        {
            if (fact.SupersededBy is not { } oldTarget || !idMapBuilder.TryGetValue(fact.Id, out var newId))
            {
                continue;
            }

            if (!idMapBuilder.TryGetValue(oldTarget, out var newTarget))
            {
                // The superseding fact is not in this journal. The belief still closed at the
                // recorded time; only the pointer to what replaced it is lost, which is strictly
                // better than dropping the fact or inventing a target for it.
                unresolved++;
                continue;
            }

            if (inserted.Contains(fact.Id))
            {
                Link(connection, transaction, newId, newTarget, fact);
            }
            else if (CurrentSupersededBy(connection, transaction, newId) != newTarget)
            {
                // Still live, NULL, or pointing elsewhere — the journal held an edge the target
                // disagreed with, and D68 forbids writing into a row this replay did not insert.
                // The matching case counts nothing: every record reaching here was counted
                // AlreadyPresent in pass 1, so an idempotent edge restates that rather than adding
                // to it (D68).
                conflicted++;
            }
        }

        transaction.Commit();
        idMap = idMapBuilder;
        return new ReplayResult(written, present, unresolved, conflicted);
    }

    /// <summary>
    /// Whether writing this fact would need an existing live belief closed first — which replay
    /// may never do.
    /// </summary>
    /// <remarks>
    /// Only live facts can collide: <c>ux_fact_live</c> is partial on <c>valid_to IS NULL</c>, so a
    /// closed fact from the journal lands beside whatever the target believes now and adds to the
    /// supersession record rather than competing with it.
    /// </remarks>
    private static bool WouldDisplaceALiveBelief(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        JournalFact fact,
        HashSet<(string Subject, string Predicate)> claimed)
    {
        if (fact.ValidTo is not null)
        {
            return false;
        }

        if (!claimed.Add((fact.Subject, fact.Predicate)))
        {
            return true;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1 FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.predicate = $predicate AND f.valid_to IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", fact.Subject);
        command.Parameters.AddWithValue("$predicate", fact.Predicate);

        return command.ExecuteScalar() is not null;
    }

    /// <summary>Whether a match exists, and — separately — whether it names a usable address.</summary>
    /// <param name="AddressUsable">
    /// False only for several closed matches with no live one: no basis exists to prefer any of
    /// them as the row a supersession would point at (D68 §4.2).
    /// </param>
    private readonly record struct ExistingMatch(long Id, bool AddressUsable);

    private static ExistingMatch? Existing(SqliteConnection connection, SqliteTransaction? transaction, JournalFact fact)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Two rows, not one: the second says only "there is more than one", which is what decides
        // whether an address exists. Live first, so row 0 is the live one when any match is live —
        // ux_fact_live guarantees at most one is.
        command.CommandText =
            """
            SELECT f.id, f.valid_to IS NULL AS is_live FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.predicate = $predicate AND f.body = $body
              AND f.valid_from = $validFrom
            ORDER BY is_live DESC, f.id ASC
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$path", fact.Subject);
        command.Parameters.AddWithValue("$predicate", fact.Predicate);
        command.Parameters.AddWithValue("$body", fact.Body);
        command.Parameters.AddWithValue("$validFrom", fact.ValidFrom);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var id = reader.GetInt64(0);
        var live = reader.GetInt64(1) != 0;
        var ambiguous = reader.Read();

        // Ambiguity does not affect presence (D68 §4.1) — only whether this row may be used as the
        // address a supersession points at. Several closed duplicates give no basis for preferring
        // one.
        return new ExistingMatch(id, AddressUsable: !ambiguous || live);
    }

    private static long? CurrentSupersededBy(SqliteConnection connection, SqliteTransaction transaction, long factId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT superseded_by FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() as long?;
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
                              regenerable, evidence, session_id, valid_from, valid_to, created_at,
                              details)
            VALUES ($subject, $predicate, $body, $object, $path, $scope, $learnedVia,
                    $regenerable, $evidence, NULL, $validFrom, $validTo, $createdAt, $details)
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
        command.Parameters.AddWithValue("$details", (object?)fact.Details ?? DBNull.Value);

        var factId = (long)command.ExecuteScalar()!;

        // The index holds live facts only (mirroring fact_fts). A replayed fact can arrive
        // already closed — the journal carries closed facts deliberately — so indexing it
        // unconditionally would put a dead fact's tokens in a table that is supposed to answer
        // for what is currently believed.
        if (fact.ValidTo is null)
        {
            FactTokenIndex.Add(connection, transaction, factId);
        }

        return factId;
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
        // The predicate is an assertion, not the guard: provenance (only rows this replay inserted
        // reach Link — see the `inserted` set in Replay) is the only thing that discriminates a
        // replay-inserted row from a pre-existing one closed by Forget, which is structurally
        // identical (D68 §3.1-3.2). This is cheap insurance against a future caller reaching Link
        // by another route.
        update.CommandText =
            "UPDATE fact SET superseded_by = $target " +
            "WHERE id = $id AND valid_to IS NOT NULL AND superseded_by IS NULL;";
        update.Parameters.AddWithValue("$target", supersededBy);
        update.Parameters.AddWithValue("$id", factId);
        update.ExecuteNonQuery();

        // Defensive rather than load-bearing today: a fact only reaches Link when its journal
        // record already carried superseded_by, which in the source store means valid_to was
        // already set — so Insert never indexed it. Calling the closing counterpart anyway is
        // what keeps this site correct if that pairing ever changes, per spec 1.3.
        FactTokenIndex.Remove(connection, transaction, factId);

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

    /// <summary>
    /// Internal rather than private so <see cref="Sync"/> can reuse this exact serialization for a
    /// chunk's <c>"t":"fact"</c> lines (docs/gp-adoption/01-sync-spec.md) — "same fields FactJournal
    /// already emits into facts.jsonl" — without a second implementation of the field mapping.
    /// </summary>
    internal static JsonObject ToJson(JournalFact fact) => new()
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
        ["details"] = fact.Details,
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
            Number(record, "created_at") ?? 0,
            Text(record, "details"));
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
