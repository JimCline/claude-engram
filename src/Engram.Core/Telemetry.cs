using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace Engram.Core;

public static class TelemetryEventKind
{
    public const string Recall = "recall";
    public const string Remember = "remember";
    public const string Digest = "digest";
    public const string SessionStart = "session-start";
    public const string PreCompact = "pre-compact";
    public const string FileTouched = "file-touched";
}

public sealed record TelemetryRecord(
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("query")] string? Query = null,
    [property: JsonPropertyName("fact_count")] int? FactCount = null,
    [property: JsonPropertyName("tokens_returned")] int? TokensReturned = null,
    [property: JsonPropertyName("coverage")] string? Coverage = null);

[JsonSerializable(typeof(TelemetryRecord))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;

public static class Telemetry
{
    private const string TelemetryFileName = "telemetry.jsonl";

    private const int MaxRecordBytes = 4096;
    private static readonly TimeSpan AppendRetryBudget = TimeSpan.FromMilliseconds(500);
    private const int MaxRetryDelayMs = 20;

    // FileMode.Append is seek-then-write, not POSIX O_APPEND: two processes can resolve the
    // same end-of-file offset and one silently overwrites the other's record. FileShare.None
    // turns that lost update into a refused open (IOException), which the retry loop below
    // treats as contention to back off from instead of a race to lose. This lock only closes
    // the same gap between threads in *this* process, which don't get that refusal from the OS.
    private static readonly object AppendLock = new();

    public static void Append(EngramHome home, TelemetryRecord record)
    {
        var payload = BuildPayload(record);
        var path = Path.Combine(home.Root, TelemetryFileName);

        lock (AppendLock)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    Directory.CreateDirectory(home.Root);

                    using var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: payload.Length);
                    stream.Write(payload, 0, payload.Length);
                    return;
                }
                catch (IOException) when (elapsed.Elapsed < AppendRetryBudget)
                {
                    Thread.Sleep(Random.Shared.Next(1, MaxRetryDelayMs));
                }
                catch (UnauthorizedAccessException) when (elapsed.Elapsed < AppendRetryBudget)
                {
                    Thread.Sleep(Random.Shared.Next(1, MaxRetryDelayMs));
                }
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }

    private static byte[] BuildPayload(TelemetryRecord record)
    {
        var payload = SerializeLine(record);
        if (payload.Length < MaxRecordBytes || string.IsNullOrEmpty(record.Query))
        {
            return payload;
        }

        var low = 0;
        var high = record.Query.Length;
        var best = SerializeLine(record with { Query = string.Empty });

        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = SerializeLine(record with { Query = record.Query[..mid] });
            if (candidate.Length < MaxRecordBytes)
            {
                best = candidate;
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static byte[] SerializeLine(TelemetryRecord record)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(record, TelemetryJsonContext.Default.TelemetryRecord);
        return Encoding.UTF8.GetBytes(json + "\n");
    }
}
