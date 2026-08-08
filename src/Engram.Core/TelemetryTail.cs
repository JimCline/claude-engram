using System.Text;

namespace Engram.Core;

/// <summary>
/// Reads whole lines appended to <c>telemetry.jsonl</c> since a byte offset.
/// </summary>
/// <remarks>
/// <para>Only complete lines are returned, and the offset advances only past a newline that was
/// actually read. An append caught halfway leaves those bytes unconsumed for the next call rather
/// than yielding a truncated record: <see cref="Telemetry.Append"/> writes a whole record at a
/// time, but nothing in the file format lets a reader tell a short read from a short record, so
/// the newline is the only trustworthy boundary.</para>
/// <para>A file shorter than the offset is read from the start again. That is what a rotated or
/// hand-deleted log looks like from here, and the alternative — keeping an offset past the end —
/// is a tail that silently stops forever.</para>
/// <para>Reads are capped so a large backlog drains over several polls instead of one burst that
/// blocks whatever is delivering.</para>
/// </remarks>
public sealed class TelemetryTail(string path, long offset)
{
    /// <summary>Bytes consumed per <see cref="Read"/>.</summary>
    public const int MaxBytesPerRead = 256 * 1024;

    /// <summary>How far into the file this tail has consumed.</summary>
    public long Offset { get; private set; } = offset;

    /// <summary>
    /// The end of the file now, which is where a live tail starts.
    /// </summary>
    /// <remarks>
    /// A missing file is offset zero rather than an error: the log is created by the first event,
    /// and a server that starts before anything has been recorded is the ordinary case.
    /// </remarks>
    public static long EndOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Consumes up to <paramref name="maxLines"/> complete lines and advances past them.
    /// </summary>
    /// <remarks>
    /// Every failure to read returns nothing and leaves the offset alone, so a transient sharing
    /// collision costs one poll rather than a gap in the stream.
    /// </remarks>
    public IReadOnlyList<string> Read(int maxLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        if (!File.Exists(path))
        {
            Offset = 0;
            return [];
        }

        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return [];
        }

        if (length < Offset)
        {
            Offset = 0;
        }

        if (length == Offset)
        {
            return [];
        }

        var want = (int)Math.Min(length - Offset, MaxBytesPerRead);
        var buffer = new byte[want];
        int read;

        try
        {
            // FileShare.Delete as well as ReadWrite: the writer appends while this reads, and a
            // log that is rotated out from under an open handle must not fault the reader.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Offset, SeekOrigin.Begin);
            read = stream.ReadAtLeast(buffer, want, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var lines = new List<string>();
        var consumed = 0;

        for (var i = 0; i < read && lines.Count < maxLines; i++)
        {
            if (buffer[i] != (byte)'\n')
            {
                continue;
            }

            // TrimStart for the mark specifically: Telemetry never writes one, but an editor that
            // opened the log will, and U+FEFF is not whitespace to Trim and not whitespace to the
            // JSON reader either — so without this a marked file parses as nothing at all and
            // delivers in perfect silence.
            var line = Encoding.UTF8.GetString(buffer, consumed, i - consumed)
                .TrimStart('\uFEFF')
                .Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }

            consumed = i + 1;
        }

        // A stretch this long with no newline in it cannot be a record — Telemetry caps one at
        // 4 KB — so it is a corrupted or foreign file. Skipping it costs whatever it held;
        // leaving the offset where it is would stop the tail on that byte forever.
        Offset += consumed == 0 && want == MaxBytesPerRead && read == want ? read : consumed;

        return lines;
    }
}
