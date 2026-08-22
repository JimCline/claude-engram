using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// One <c>fact_relation</c> row as the journal carries it: each side addressed by the same
/// descriptive tuple <see cref="JournalFact"/> uses for its own identity, since a raw
/// <c>fact.id</c> means nothing once replayed into a different store.
/// </summary>
public sealed record JournalRelation(
    string FactSubject,
    string FactPredicate,
    string FactBody,
    long FactValidFrom,
    string RelatedSubject,
    string RelatedPredicate,
    string RelatedBody,
    long RelatedValidFrom,
    string Relation,
    string? Reason,
    long JudgedAt);

/// <summary>What a relations replay did, or would do.</summary>
/// <param name="Unresolved">
/// Rows skipped because one or both sides could not be traced through <c>facts.jsonl</c>'s own
/// journal ids into the target store's — never pointed at an arbitrary fact instead (D32).
/// </param>
public sealed record RelationReplayResult(int Written, int AlreadyPresent, int Unresolved);

/// <summary>
/// The store's conflict verdicts as plain text, sibling to <see cref="FactJournal"/>'s
/// <c>facts.jsonl</c>. Rewritten whole and atomically for the same reason that journal is:
/// <c>fact_relation</c> rows are immutable, so there is nothing to reconcile against a prior
/// run, and a whole-file rewrite needs no watermark to disagree with the file it describes.
/// </summary>
public static class RelationJournal
{
    public const string FileName = "relations.jsonl";

    public const int FormatVersion = 1;

    private const string PartialSuffix = ".partial";

    public static string PathIn(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Path.Combine(home.BackupDir, FileName);
    }

    /// <summary>Writes every <c>fact_relation</c> row in the store to the journal.</summary>
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
                written = WriteTo(connection, writer, now);
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

    public static int WriteTo(SqliteConnection connection, TextWriter writer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(writer);

        var header = new JsonObject
        {
            ["format"] = "engram-relations",
            ["format_version"] = FormatVersion,
            ["schema_version"] = EngramDatabase.ReadSchemaVersion(connection),
            ["written_at"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        };
        writer.WriteLine(header.ToJsonString());

        var written = 0;
        foreach (var relation in Read(connection))
        {
            writer.WriteLine(ToJson(relation).ToJsonString());
            written++;
        }

        return written;
    }

    /// <summary>Every <c>fact_relation</c> row, each side resolved to its descriptive tuple.</summary>
    public static IEnumerable<JournalRelation> Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT fe.path, ff.predicate, ff.body, ff.valid_from,
                   re.path, rf.predicate, rf.body, rf.valid_from,
                   fr.relation, fr.reason, fr.judged_at
              FROM fact_relation fr
              JOIN fact ff ON ff.id = fr.fact_id
              JOIN entity fe ON fe.id = ff.subject_id
              JOIN fact rf ON rf.id = fr.related_id
              JOIN entity re ON re.id = rf.subject_id
             ORDER BY fr.id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new JournalRelation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10));
        }
    }

    /// <summary>Parses a journal file, skipping the header and anything unreadable.</summary>
    public static IReadOnlyList<JournalRelation> Parse(IEnumerable<string> lines, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var relations = new List<JournalRelation>();
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

            // The header, which carries no relation.
            if (record.ContainsKey("format"))
            {
                continue;
            }

            var relation = FromJson(record);
            if (relation is null)
            {
                bad++;
                continue;
            }

            relations.Add(relation);
        }

        skipped = bad;
        return relations;
    }

    /// <summary>
    /// Resolves each row's fact/related side through the same journal-id-to-target-id map
    /// <see cref="FactJournal.Replay"/> built for <c>facts.jsonl</c>, matched by the descriptive
    /// tuple both journals share. A side that cannot be traced — no matching entry in
    /// <paramref name="facts"/>, or a journal id <paramref name="idMap"/> never wrote — is
    /// skipped and counted, never pointed at an arbitrary fact (D32, mirroring D68's rule that a
    /// conflicted fact gets no <c>idMap</c> entry).
    /// </summary>
    public static RelationReplayResult Replay(
        SqliteConnection connection,
        IReadOnlyList<JournalRelation> relations,
        IReadOnlyList<JournalFact> facts,
        IReadOnlyDictionary<long, long> idMap,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(idMap);

        var tupleToJournalId = new Dictionary<(string, string, string, long), long>();
        foreach (var fact in facts)
        {
            tupleToJournalId[(fact.Subject, fact.Predicate, fact.Body, fact.ValidFrom)] = fact.Id;
        }

        long? Resolve(string subject, string predicate, string body, long validFrom) =>
            tupleToJournalId.TryGetValue((subject, predicate, body, validFrom), out var journalId)
                && idMap.TryGetValue(journalId, out var targetId)
                ? targetId
                : null;

        var written = 0;
        var present = 0;
        var unresolved = 0;

        using var transaction = apply ? EngramDatabase.BeginWrite(connection) : null;

        foreach (var relation in relations)
        {
            var factId = Resolve(relation.FactSubject, relation.FactPredicate, relation.FactBody, relation.FactValidFrom);
            var relatedId = Resolve(relation.RelatedSubject, relation.RelatedPredicate, relation.RelatedBody, relation.RelatedValidFrom);

            if (factId is null || relatedId is null)
            {
                unresolved++;
                continue;
            }

            if (FactRelations.Exists(connection, transaction, factId.Value, relatedId.Value, relation.Relation, relation.JudgedAt))
            {
                present++;
                continue;
            }

            if (apply)
            {
                FactRelations.Insert(
                    connection, transaction!, factId.Value, relatedId.Value, relation.Relation, relation.Reason, relation.JudgedAt);
            }

            written++;
        }

        transaction?.Commit();

        return new RelationReplayResult(written, present, unresolved);
    }

    private static JsonObject ToJson(JournalRelation relation)
    {
        var json = new JsonObject
        {
            ["fact_subject"] = relation.FactSubject,
            ["fact_predicate"] = relation.FactPredicate,
            ["fact_body"] = relation.FactBody,
            ["fact_valid_from"] = relation.FactValidFrom,
            ["related_subject"] = relation.RelatedSubject,
            ["related_predicate"] = relation.RelatedPredicate,
            ["related_body"] = relation.RelatedBody,
            ["related_valid_from"] = relation.RelatedValidFrom,
            ["relation"] = relation.Relation,
            ["reason"] = relation.Reason,
            ["judged_at"] = relation.JudgedAt,
        };

        return json;
    }

    private static JournalRelation? FromJson(JsonObject record)
    {
        if (!record.ContainsKey("fact_subject"))
        {
            return null;
        }

        try
        {
            return new JournalRelation(
                FactSubject: (string)record["fact_subject"]!,
                FactPredicate: (string)record["fact_predicate"]!,
                FactBody: (string)record["fact_body"]!,
                FactValidFrom: (long)record["fact_valid_from"]!,
                RelatedSubject: (string)record["related_subject"]!,
                RelatedPredicate: (string)record["related_predicate"]!,
                RelatedBody: (string)record["related_body"]!,
                RelatedValidFrom: (long)record["related_valid_from"]!,
                Relation: (string)record["relation"]!,
                Reason: record["reason"] is null ? null : (string)record["reason"]!,
                JudgedAt: (long)record["judged_at"]!);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
