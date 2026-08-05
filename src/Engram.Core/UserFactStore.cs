using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engram.Core;

public sealed record UserFactRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("supersedes")] string? Supersedes = null,
    [property: JsonPropertyName("retracts")] string? Retracts = null);

[JsonSerializable(typeof(UserFactRecord))]
internal sealed partial class UserFactJsonContext : JsonSerializerContext;

/// <summary>
/// Durable, user-scoped facts: the things the user stated about themselves rather than
/// the things an agent worked out. Survives every session, because that is the point —
/// "I saw a film last Saturday" is worth nothing if it expires with the conversation.
///
/// One file per record rather than appends to a shared log. The writer is a hook on the
/// path of every message the user sends, so it must never block on another process
/// holding a lock: a fresh CreateNew file cannot contend with anything, which buys an
/// unconditional write cost in exchange for a directory to enumerate on read.
///
/// Nothing is ever edited or deleted here. A retraction is a new record naming the id it
/// closes, which keeps the append-only invariant intact and leaves the fact that a user
/// retracted something as visible as the original.
/// </summary>
public static class UserFactStore
{
    public static string ResolveDirectory(EngramHome home) => home.UserFactsDir;

    public static string Append(
        EngramHome home,
        string kind,
        string statement,
        string? sessionId = null,
        string? supersedes = null,
        string? retracts = null)
    {
        Directory.CreateDirectory(home.UserFactsDir);

        var now = DateTime.UtcNow;
        var id = "u" + Guid.NewGuid().ToString("N")[..8];
        var record = new UserFactRecord(
            Id: id,
            Timestamp: now.ToString("o"),
            Kind: kind,
            Statement: statement,
            SessionId: sessionId,
            Supersedes: supersedes,
            Retracts: retracts);

        var fileName = $"{now.Ticks}-{Environment.ProcessId}-{id}.json";
        var path = Path.Combine(home.UserFactsDir, fileName);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, record, UserFactJsonContext.Default.UserFactRecord);

        return id;
    }

    /// <summary>
    /// Every record on disk, oldest first, including retractions and superseded facts.
    /// </summary>
    public static IReadOnlyList<UserFactRecord> ReadAll(EngramHome home)
    {
        if (!Directory.Exists(home.UserFactsDir))
        {
            return [];
        }

        var records = new List<UserFactRecord>();

        foreach (var path in Directory.EnumerateFiles(home.UserFactsDir, "*.json"))
        {
            UserFactRecord? record;

            try
            {
                using var stream = File.OpenRead(path);
                record = JsonSerializer.Deserialize(stream, UserFactJsonContext.Default.UserFactRecord);
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

        records.Sort((a, b) => string.CompareOrdinal(a.Timestamp, b.Timestamp));
        return records;
    }

    /// <summary>
    /// The facts that still stand: retracted and superseded ones removed, and the
    /// bookkeeping records that closed them removed along with them.
    /// </summary>
    public static IReadOnlyList<UserFactRecord> ReadActive(EngramHome home)
    {
        var all = ReadAll(home);
        var closed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in all)
        {
            if (record.Retracts is { Length: > 0 } retracted)
            {
                closed.Add(retracted);
            }

            if (record.Supersedes is { Length: > 0 } superseded)
            {
                closed.Add(superseded);
            }
        }

        return all
            .Where(r => r.Retracts is null or { Length: 0 })
            .Where(r => !closed.Contains(r.Id))
            .ToList();
    }

    /// <summary>
    /// Presents the standing user facts as long-term facts so recall ranks and budgets
    /// them through the same path as everything else. They belong in that tier rather
    /// than beside session notes: what the user said about themselves outlives the
    /// conversation it was said in, which is the entire reason for capturing it.
    /// </summary>
    public static IReadOnlyList<CannedFact> ToFacts(EngramHome home, DateTime utcNow)
    {
        var facts = new List<CannedFact>();

        foreach (var record in ReadActive(home))
        {
            var isDirective = string.Equals(record.Kind, "directive", StringComparison.Ordinal);

            var ageDays = DateTime.TryParse(
                record.Timestamp,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var written)
                ? Math.Max(0, (int)(utcNow - written).TotalDays)
                : 0;

            facts.Add(new CannedFact(
                Id: record.Id,
                Subject: isDirective ? "user-instruction" : "about-the-user",
                Predicate: isDirective ? "requires" : "stated",
                Body: record.Statement,
                Scope: "user",
                Topic: isDirective ? "your standing instructions" : "about you",
                AgeDays: ageDays,
                Evidence: "stated by the user"));
        }

        return facts;
    }
}
