using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

public sealed record LegacyUserFactRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("supersedes")] string? Supersedes = null,
    [property: JsonPropertyName("retracts")] string? Retracts = null);

[JsonSerializable(typeof(LegacyUserFactRecord))]
internal sealed partial class LegacyUserFactJsonContext : JsonSerializerContext;

/// <summary>
/// Reads the JSON directory user facts used to live in, and moves it into the store.
/// </summary>
/// <remarks>
/// Read-only: nothing writes this format any more. It exists because a real instance already
/// holds captures in it, and a storage change that silently drops what a user told the system
/// is not a migration, it is data loss with a changelog entry.
///
/// <para>
/// The files are left on disk after import. They are the only copy of the pre-migration state,
/// deleting them is not required for anything to work, and this codebase does not destroy a
/// user's data as a side effect of an upgrade. The <c>schema_meta</c> marker is what stops a
/// second import, not their absence.
/// </para>
/// </remarks>
public static class LegacyUserFacts
{
    public const string ImportedKey = "user_facts_imported";

    /// <summary>Guards against a malformed chain of supersedes pointing at itself.</summary>
    private const int MaxChainDepth = 64;

    /// <summary>
    /// Imports the JSON captures into the store once, and returns how many facts it wrote.
    /// </summary>
    /// <remarks>
    /// Replayed in timestamp order rather than collapsed to a final state, so the store ends
    /// up with the supersession chain the user actually produced: a raw capture, the model's
    /// rewrite of it, and a retraction each land as their own event. Collapsing would be
    /// cheaper and would lose exactly the history the move to this store was for.
    ///
    /// A record that supersedes another is written at the ORIGINAL's address, not its own.
    /// The legacy format gave a rewrite a fresh id and had it name what it replaced; here the
    /// replacement shares its target's subject and predicate, which is what makes the store's
    /// collision rule close the old one. Deriving the path from the rewrite's own text
    /// instead would leave both live, and recall would show a sentence next to its own
    /// correction.
    /// </remarks>
    public static int Import(SqliteConnection connection, EngramHome home, DateTimeOffset now)
    {
        if (EngramDatabase.ReadMeta(connection, ImportedKey) is not null)
        {
            return 0;
        }

        var records = ReadAll(home);
        var byId = new Dictionary<string, LegacyUserFactRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            byId[record.Id] = record;
        }

        var written = 0;

        foreach (var record in records)
        {
            var at = ParseTimestamp(record.Timestamp, now);

            if (record.Retracts is { Length: > 0 } retracted)
            {
                if (Address(retracted, byId) is { } closing
                    && FactStore.FindLiveFactId(connection, transaction: null, closing.Path, closing.Predicate) is { } liveId)
                {
                    FactStore.Forget(connection, liveId, "retracted by the user", at);
                }

                continue;
            }

            if (Address(record.Id, byId) is not { } address)
            {
                continue;
            }

            using var transaction = EngramDatabase.BeginWrite(connection);

            UserFacts.EnsureTopics(connection, transaction, at);

            var sessionId = record.SessionId is { Length: > 0 } external
                ? SessionStore.EnsureSession(connection, transaction, external, at)
                : (long?)null;

            FactStore.Remember(
                connection,
                transaction,
                new FactWrite(
                    SubjectPath: address.Path,
                    SubjectKind: UserFacts.StatementKind,
                    Predicate: address.Predicate,
                    Body: record.Statement,
                    Scope: UserFacts.Scope,
                    LearnedVia: UserFacts.LearnedVia,
                    Evidence: "stated by the user",
                    Regenerable: false,
                    SessionId: sessionId),
                at,
                reason: "restated so it stands on its own");

            transaction.Commit();
            written++;
        }

        EngramDatabase.WriteMeta(connection, transaction: null, ImportedKey, "1");
        return written;
    }

    /// <summary>
    /// Every record on disk, oldest first. Includes retractions and superseded facts, because
    /// the import replays them.
    /// </summary>
    public static IReadOnlyList<LegacyUserFactRecord> ReadAll(EngramHome home)
    {
        if (!Directory.Exists(home.UserFactsDir))
        {
            return [];
        }

        var records = new List<LegacyUserFactRecord>();

        foreach (var path in Directory.EnumerateFiles(home.UserFactsDir, "*.json"))
        {
            LegacyUserFactRecord? record;

            try
            {
                using var stream = File.OpenRead(path);
                record = JsonSerializer.Deserialize(stream, LegacyUserFactJsonContext.Default.LegacyUserFactRecord);
            }
            catch (JsonException)
            {
                // A half-written file from a killed hook is not a reason to lose the rest.
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            if (record is not null)
            {
                records.Add(record);
            }
        }

        // ISO 8601 round-trip strings in UTC sort lexicographically in chronological order,
        // which the replay depends on: a rewrite has to reach the store after the capture it
        // supersedes, or it is the one that gets closed.
        records.Sort((a, b) => string.CompareOrdinal(a.Timestamp, b.Timestamp));
        return records;
    }

    /// <summary>
    /// Where a record belongs in the store: the address of the root of its supersession chain.
    /// </summary>
    private static (string Path, string Predicate)? Address(
        string id,
        IReadOnlyDictionary<string, LegacyUserFactRecord> byId)
    {
        if (!byId.TryGetValue(id, out var record))
        {
            return null;
        }

        var depth = 0;
        while (record.Supersedes is { Length: > 0 } previous
            && byId.TryGetValue(previous, out var earlier)
            && depth++ < MaxChainDepth)
        {
            record = earlier;
        }

        var topic = string.Equals(record.Kind, "directive", StringComparison.Ordinal)
            ? UserFactTopic.Instruction
            : UserFactTopic.AboutYou;

        return (UserFacts.PathFor(topic, record.Statement), UserFacts.PredicateFor(topic));
    }

    private static DateTimeOffset ParseTimestamp(string timestamp, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : fallback;
}
