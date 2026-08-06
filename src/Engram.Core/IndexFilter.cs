namespace Engram.Core;

public enum SkipReason
{
    /// <summary>Not skipped.</summary>
    None,

    /// <summary>Matched an <c>[indexing] ignore</c> pattern.</summary>
    Ignored,

    /// <summary>Contains a NUL byte, so it is not text.</summary>
    Binary,

    /// <summary>Bigger than <c>max_file_bytes</c>.</summary>
    TooLarge,

    /// <summary>Made of long lines — minified, bundled, or a data dump.</summary>
    Generated,

    /// <summary>Gone, or unreadable, between listing and inspection.</summary>
    Unreadable,
}

public readonly record struct FileVerdict(SkipReason Reason)
{
    public bool Include => Reason == SkipReason.None;

    public static FileVerdict Included { get; } = new(SkipReason.None);
}

/// <summary>
/// Decides whether one file is worth indexing.
/// </summary>
/// <remarks>
/// <para><b>Classified by content, never by extension.</b> An extension list is infinite, always
/// out of date, and wrong in both directions — generated blobs ship as <c>.h</c>, and real scripts
/// ship with no extension at all. A NUL byte in the first few kilobytes is what git itself uses to
/// call a file binary, and it costs one read that the indexer has to do anyway.</para>
///
/// <para>Checks run cheapest-first — pattern, then size from the directory entry, then one read of
/// the head — so the common case of an ignored directory never touches the disk.</para>
/// </remarks>
public sealed class IndexFilter
{
    private readonly IndexingSettings settings;
    private readonly PathGlob[] ignore;

    public IndexFilter(IndexingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        this.settings = settings;
        ignore = [.. settings.Ignore.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathGlob.Parse)];
    }

    /// <summary>Whether the path alone disqualifies the file, without reading it.</summary>
    public bool IsIgnored(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return Array.Exists(ignore, g => g.Matches(relativePath));
    }

    public FileVerdict Inspect(string relativePath, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(fullPath);

        if (IsIgnored(relativePath))
        {
            return new FileVerdict(SkipReason.Ignored);
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new FileVerdict(SkipReason.Unreadable);
            }

            if (info.Length > settings.MaxFileBytes)
            {
                return new FileVerdict(SkipReason.TooLarge);
            }

            // An empty file has no facts in it, but it is not junk either, and calling it
            // unreadable would put it in a bucket that means something else.
            if (info.Length == 0)
            {
                return FileVerdict.Included;
            }

            Span<byte> head = stackalloc byte[IndexingSettings.HeadBytes];
            using var stream = File.OpenRead(fullPath);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            return Classify(head[..read], settings.MaxMeanLineBytes);
        }
        catch (IOException)
        {
            return new FileVerdict(SkipReason.Unreadable);
        }
        catch (UnauthorizedAccessException)
        {
            return new FileVerdict(SkipReason.Unreadable);
        }
    }

    /// <summary>
    /// Classifies the head of a file: binary, generated, or worth indexing.
    /// </summary>
    /// <remarks>
    /// <para><b>Mean line length, not longest line.</b> Longest-line is the obvious test and it is
    /// wrong, measured against this repository: at a 2048-byte cap it rejected
    /// <c>plugin/hooks/hooks.json</c> — 61 lines, hand-written, with one 2662-byte line in it. One
    /// long line among many short ones is a formatting choice; a file that is <i>made of</i> long
    /// lines is generated. Across this repository's 175 tracked text files the mean is 38 bytes
    /// per line at p50, 68 at p99 and 170 at worst, while a minified bundle runs to thousands —
    /// so the two populations are separated by more than an order of magnitude and the default
    /// sits in the gap rather than near either edge.</para>
    ///
    /// <para>A truncated head needs no special case. A bundle is one line for its whole length,
    /// so its head has no newline at all, one line is counted, and the mean is the head size.</para>
    /// </remarks>
    public static FileVerdict Classify(ReadOnlySpan<byte> head, int maxMeanLineBytes)
    {
        if (head.IsEmpty)
        {
            return FileVerdict.Included;
        }

        var lines = 0;

        for (var i = 0; i < head.Length; i++)
        {
            if (head[i] == 0)
            {
                return new FileVerdict(SkipReason.Binary);
            }

            if (head[i] == (byte)'\n')
            {
                lines++;
            }
        }

        // The trailing run counts as a line even without its newline, which is what makes a
        // single-line file weigh its whole length rather than nothing.
        if (head[^1] != (byte)'\n')
        {
            lines++;
        }

        return head.Length / lines > maxMeanLineBytes
            ? new FileVerdict(SkipReason.Generated)
            : FileVerdict.Included;
    }
}
