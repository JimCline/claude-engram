using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

public sealed record LegacySessionFactRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("evidence")] string? Evidence = null);

[JsonSerializable(typeof(LegacySessionFactRecord))]
internal sealed partial class LegacySessionFactJsonContext : JsonSerializerContext;

/// <summary>
/// Reads the JSONL files session notes used to live in, and moves them into the store.
/// </summary>
/// <remarks>
/// Read-only; nothing writes this format any more. Notes from earlier sessions are what
/// recall's prior-session tier is made of, so dropping them on upgrade would quietly empty
/// a tier the model is told to expect. The files are left on disk afterwards — they are the
/// only copy of the pre-migration state, and the <c>schema_meta</c> marker is what stops a
/// second import, not their absence.
/// </remarks>
public static class LegacySessionFacts
{
    public const string ImportedKey = "session_facts_imported";

    /// <summary>
    /// Imports the JSONL notes into the store once, and returns how many facts it wrote.
    /// </summary>
    public static int Import(SqliteConnection connection, EngramHome home, DateTimeOffset now)
    {
        if (EngramDatabase.ReadMeta(connection, ImportedKey) is not null)
        {
            return 0;
        }

        var written = 0;

        foreach (var (sessionExternalId, record) in ReadAll(home))
        {
            var at = DateTimeOffset.TryParse(
                record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : now;

            SessionFacts.Append(
                connection,
                sessionExternalId,
                record.Statement,
                record.Subject,
                record.Evidence,
                record.Agent,
                at);

            written++;
        }

        EngramDatabase.WriteMeta(connection, transaction: null, ImportedKey, "1");
        return written;
    }

    /// <summary>
    /// Every note on disk with the session it belongs to, oldest first.
    /// </summary>
    /// <remarks>
    /// The session comes from the file name rather than the record's own <c>session_id</c>:
    /// the file is what actually grouped these notes, and a record whose field disagrees with
    /// the file it is in would otherwise silently reparent one note into another session.
    /// </remarks>
    public static IReadOnlyList<(string SessionId, LegacySessionFactRecord Record)> ReadAll(EngramHome home)
    {
        var sessionsDir = Path.Combine(home.Root, "sessions");
        if (!Directory.Exists(sessionsDir))
        {
            return [];
        }

        var records = new List<(string SessionId, LegacySessionFactRecord Record)>();

        string[] files;
        try
        {
            files = Directory.GetFiles(sessionsDir, "*.jsonl");
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var path in files)
        {
            var sessionId = Path.GetFileNameWithoutExtension(path);

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                LegacySessionFactRecord? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize(line, LegacySessionFactJsonContext.Default.LegacySessionFactRecord);
                }
                catch (JsonException)
                {
                    // A line truncated by a killed process is not a reason to lose the rest.
                    continue;
                }

                if (parsed is not null)
                {
                    records.Add((sessionId, parsed));
                }
            }
        }

        records.Sort((a, b) => string.CompareOrdinal(a.Record.Timestamp, b.Record.Timestamp));
        return records;
    }
}
