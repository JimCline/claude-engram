using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// One <c>fact_review</c> row as the journal carries it: the fact addressed by the same
/// descriptive tuple <see cref="JournalFact"/> uses for its own identity, since a raw
/// <c>fact.id</c> means nothing once replayed into a different store.
/// </summary>
public sealed record JournalReview(
    string FactSubject,
    string FactPredicate,
    string FactBody,
    long FactValidFrom,
    long ReviewAfter,
    long SetAt);

/// <summary>What a review replay did, or would do.</summary>
/// <param name="Unresolved">
/// Rows skipped because the fact could not be traced through <c>facts.jsonl</c>'s own journal
/// ids into the target store's — never pointed at an arbitrary fact instead (D32).
/// </param>
public sealed record ReviewReplayResult(int Written, int AlreadyPresent, int Unresolved);

/// <summary>
/// The store's review markers as plain text, sibling to <see cref="RelationJournal"/>'s
/// <c>relations.jsonl</c>. Rewritten whole and atomically for the same reason that journal is:
/// a review marker set via <see cref="FactReview.Set"/> replaces its row rather than
/// accumulating, so there is nothing to reconcile against a prior run.
/// </summary>
public static class ReviewJournal
{
    public const string FileName = "review.jsonl";

    public const int FormatVersion = 1;

    private const string PartialSuffix = ".partial";

    public static string PathIn(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Path.Combine(home.BackupDir, FileName);
    }

    /// <summary>Writes every <c>fact_review</c> row in the store to the journal.</summary>
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
            ["format"] = "engram-review",
            ["format_version"] = FormatVersion,
            ["schema_version"] = EngramDatabase.ReadSchemaVersion(connection),
            ["written_at"] = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        };
        writer.WriteLine(header.ToJsonString());

        var written = 0;
        foreach (var review in Read(connection))
        {
            writer.WriteLine(ToJson(review).ToJsonString());
            written++;
        }

        return written;
    }

    /// <summary>Every <c>fact_review</c> row, resolved to its fact's descriptive tuple.</summary>
    public static IEnumerable<JournalReview> Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT fe.path, ff.predicate, ff.body, ff.valid_from, r.review_after, r.set_at
              FROM fact_review r
              JOIN fact ff ON ff.id = r.fact_id
              JOIN entity fe ON fe.id = ff.subject_id
             ORDER BY r.fact_id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new JournalReview(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5));
        }
    }

    /// <summary>Parses a journal file, skipping the header and anything unreadable.</summary>
    public static IReadOnlyList<JournalReview> Parse(IEnumerable<string> lines, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var reviews = new List<JournalReview>();
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

            // The header, which carries no review row.
            if (record.ContainsKey("format"))
            {
                continue;
            }

            var review = FromJson(record);
            if (review is null)
            {
                bad++;
                continue;
            }

            reviews.Add(review);
        }

        skipped = bad;
        return reviews;
    }

    /// <summary>
    /// Resolves each row's fact through the same journal-id-to-target-id map
    /// <see cref="FactJournal.Replay"/> built for <c>facts.jsonl</c>, matched by the descriptive
    /// tuple both journals share. A fact that cannot be traced — no matching entry in
    /// <paramref name="facts"/>, or a journal id <paramref name="idMap"/> never wrote — is
    /// skipped and counted, never pointed at an arbitrary fact (D32, mirroring D68's rule that a
    /// conflicted fact gets no <c>idMap</c> entry).
    /// </summary>
    public static ReviewReplayResult Replay(
        SqliteConnection connection,
        IReadOnlyList<JournalReview> reviews,
        IReadOnlyList<JournalFact> facts,
        IReadOnlyDictionary<long, long> idMap,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(reviews);
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

        foreach (var review in reviews)
        {
            var factId = Resolve(review.FactSubject, review.FactPredicate, review.FactBody, review.FactValidFrom);

            if (factId is null)
            {
                unresolved++;
                continue;
            }

            if (Exists(connection, transaction, factId.Value))
            {
                present++;
                continue;
            }

            if (apply)
            {
                FactReview.Set(connection, transaction, factId.Value, review.ReviewAfter, review.SetAt);
            }

            written++;
        }

        transaction?.Commit();

        return new ReviewReplayResult(written, present, unresolved);
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, long factId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM fact_review WHERE fact_id = $factId LIMIT 1;";
        command.Parameters.AddWithValue("$factId", factId);
        return command.ExecuteScalar() is not null;
    }

    private static JsonObject ToJson(JournalReview review)
    {
        var json = new JsonObject
        {
            ["fact_subject"] = review.FactSubject,
            ["fact_predicate"] = review.FactPredicate,
            ["fact_body"] = review.FactBody,
            ["fact_valid_from"] = review.FactValidFrom,
            ["review_after"] = review.ReviewAfter,
            ["set_at"] = review.SetAt,
        };

        return json;
    }

    private static JournalReview? FromJson(JsonObject record)
    {
        if (!record.ContainsKey("fact_subject"))
        {
            return null;
        }

        try
        {
            return new JournalReview(
                FactSubject: (string)record["fact_subject"]!,
                FactPredicate: (string)record["fact_predicate"]!,
                FactBody: (string)record["fact_body"]!,
                FactValidFrom: (long)record["fact_valid_from"]!,
                ReviewAfter: (long)record["review_after"]!,
                SetAt: (long)record["set_at"]!);
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
