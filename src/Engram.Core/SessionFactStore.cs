using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engram.Core;

public sealed record SessionFactRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("evidence")] string? Evidence = null);

[JsonSerializable(typeof(SessionFactRecord))]
internal sealed partial class SessionFactJsonContext : JsonSerializerContext;

public static class SessionFactStore
{
    private static readonly TimeSpan AppendRetryBudget = TimeSpan.FromMilliseconds(500);
    private static readonly object AppendLock = new();

    public static string ResolvePath(EngramHome home, string sessionId) =>
        Path.Combine(home.Root, "sessions", sessionId + ".jsonl");

    public static string Append(
        EngramHome home,
        string sessionId,
        string statement,
        string? subject = null,
        string? evidence = null,
        string? agent = null)
    {
        var path = ResolvePath(home, sessionId);

        lock (AppendLock)
        {
            var handle = $"s{CountExistingRecords(path) + 1:D3}";

            var record = new SessionFactRecord(
                Id: handle,
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: sessionId,
                Statement: statement,
                Agent: agent,
                Subject: subject,
                Evidence: evidence);

            DurableAppend.TryAppend(path, SerializeLine(record), AppendRetryBudget);
            return handle;
        }
    }

    public static IReadOnlyList<SessionFactRecord> ReadAll(EngramHome home, string sessionId)
    {
        var path = ResolvePath(home, sessionId);
        return ReadRecords(path);
    }

    public static IReadOnlyList<SessionFactRecord> ReadAllExcept(EngramHome home, string excludeSessionId)
    {
        var sessionsDir = Path.Combine(home.Root, "sessions");
        var records = new List<SessionFactRecord>();

        foreach (var path in SafeEnumerateSessionFiles(sessionsDir))
        {
            var sessionId = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(sessionId, excludeSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            records.AddRange(ReadRecords(path));
        }

        return records;
    }

    private static IReadOnlyList<SessionFactRecord> ReadRecords(string path)
    {
        var records = new List<SessionFactRecord>();

        foreach (var line in SafeReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SessionFactRecord? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(line, SessionFactJsonContext.Default.SessionFactRecord);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is not null)
            {
                records.Add(parsed);
            }
        }

        return records;
    }

    private static IEnumerable<string> SafeEnumerateSessionFiles(string sessionsDir)
    {
        if (!Directory.Exists(sessionsDir))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(sessionsDir, "*.jsonl").ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int CountExistingRecords(string path)
    {
        var count = 0;
        foreach (var line in SafeReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static byte[] SerializeLine(SessionFactRecord record)
    {
        var json = JsonSerializer.Serialize(record, SessionFactJsonContext.Default.SessionFactRecord);
        return Encoding.UTF8.GetBytes(json + "\n");
    }
}
