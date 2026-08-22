using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// One <c>fact_sync_request</c> row as the journal carries it: the flagged fact addressed by
/// the same descriptive tuple <see cref="JournalFact"/> uses for its own identity, since a raw
/// <c>fact.id</c> means nothing once replayed into a different store.
/// </summary>
public sealed record JournalSyncRequest(
    string FactSubject,
    string FactPredicate,
    string FactBody,
    long FactValidFrom,
    long RequestedAt);

/// <summary>What a sync-request replay did, or would do.</summary>
/// <param name="Unresolved">
/// Rows skipped because the flagged fact could not be traced through <c>facts.jsonl</c>'s own
/// journal ids into the target store's — never pointed at an arbitrary fact instead (D32).
/// </param>
public sealed record SyncRequestReplayResult(int Written, int AlreadyPresent, int Unresolved);

/// <summary>
/// The store's always-sync flags as plain text, sibling to <see cref="FactJournal"/>'s
/// <c>facts.jsonl</c> and <see cref="RelationJournal"/>'s <c>relations.jsonl</c>. Rewritten
/// whole and atomically for the same reason those are: <c>fact_sync_request</c> rows are
/// insert-only, so there is nothing to reconcile against a prior run, and a whole-file rewrite
/// needs no watermark to disagree with the file it describes.
/// </summary>
public static class SyncRequestJournal
{
    public const string FileName = "sync_requests.jsonl";

    public const int FormatVersion = 1;

    private const string PartialSuffix = ".partial";

    public static string PathIn(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Path.Combine(home.BackupDir, FileName);
    }

    /// <summary>Writes every <c>fact_sync_request</c> row in the store to the journal.</summary>
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
            ["format"] = "engram-sync-requests",
            ["format_version"] = FormatVersion,
            ["schema_version"] = EngramDatabase.ReadSchemaVersion(connection),
            ["written_at"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        };
        writer.WriteLine(header.ToJsonString());

        var written = 0;
        foreach (var request in Read(connection))
        {
            writer.WriteLine(ToJson(request).ToJsonString());
            written++;
        }

        return written;
    }

    /// <summary>Every <c>fact_sync_request</c> row, resolved to its fact's descriptive tuple.</summary>
    public static IEnumerable<JournalSyncRequest> Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.path, f.predicate, f.body, f.valid_from, r.requested_at
              FROM fact_sync_request r
              JOIN fact f ON f.id = r.fact_id
              JOIN entity e ON e.id = f.subject_id
             ORDER BY r.fact_id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new JournalSyncRequest(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4));
        }
    }

    /// <summary>Parses a journal file, skipping the header and anything unreadable.</summary>
    public static IReadOnlyList<JournalSyncRequest> Parse(IEnumerable<string> lines, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var requests = new List<JournalSyncRequest>();
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

            // The header, which carries no sync request.
            if (record.ContainsKey("format"))
            {
                continue;
            }

            var request = FromJson(record);
            if (request is null)
            {
                bad++;
                continue;
            }

            requests.Add(request);
        }

        skipped = bad;
        return requests;
    }

    /// <summary>
    /// Resolves each row's flagged fact through the same journal-id-to-target-id map
    /// <see cref="FactJournal.Replay"/> built for <c>facts.jsonl</c>, matched by the descriptive
    /// tuple both journals share. A row that cannot be traced — no matching entry in
    /// <paramref name="facts"/>, or a journal id <paramref name="idMap"/> never wrote — is
    /// skipped and counted, never pointed at an arbitrary fact (D32, mirroring D68's rule that a
    /// conflicted fact gets no <c>idMap</c> entry). There is no conflict bucket: a sync-request
    /// row has no content that can diverge from another one, only "requested" or "not requested."
    /// </summary>
    public static SyncRequestReplayResult Replay(
        SqliteConnection connection,
        IReadOnlyList<JournalSyncRequest> requests,
        IReadOnlyList<JournalFact> facts,
        IReadOnlyDictionary<long, long> idMap,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(requests);
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

        foreach (var request in requests)
        {
            var factId = Resolve(request.FactSubject, request.FactPredicate, request.FactBody, request.FactValidFrom);

            if (factId is null)
            {
                unresolved++;
                continue;
            }

            if (FactSyncRequests.IsFlagged(connection, transaction, factId.Value))
            {
                present++;
                continue;
            }

            if (apply)
            {
                FactSyncRequests.Insert(connection, transaction!, factId.Value, request.RequestedAt);
            }

            written++;
        }

        transaction?.Commit();

        return new SyncRequestReplayResult(written, present, unresolved);
    }

    private static JsonObject ToJson(JournalSyncRequest request)
    {
        var json = new JsonObject
        {
            ["fact_subject"] = request.FactSubject,
            ["fact_predicate"] = request.FactPredicate,
            ["fact_body"] = request.FactBody,
            ["fact_valid_from"] = request.FactValidFrom,
            ["requested_at"] = request.RequestedAt,
        };

        return json;
    }

    private static JournalSyncRequest? FromJson(JsonObject record)
    {
        if (!record.ContainsKey("fact_subject"))
        {
            return null;
        }

        try
        {
            return new JournalSyncRequest(
                FactSubject: (string)record["fact_subject"]!,
                FactPredicate: (string)record["fact_predicate"]!,
                FactBody: (string)record["fact_body"]!,
                FactValidFrom: (long)record["fact_valid_from"]!,
                RequestedAt: (long)record["requested_at"]!);
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
