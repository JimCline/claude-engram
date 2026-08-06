namespace Engram.Core;

/// <summary>One edit recorded by <c>file-touched</c>: when, and to what.</summary>
/// <remarks>
/// <see cref="Path"/> is nullable because two things can produce an entry without one — a spool
/// file written before the hook recorded paths, and a tool invocation whose payload carried no
/// <c>file_path</c>. Both mean the same thing to a consumer: something changed here and the
/// entry cannot say what, so fall back to scanning rather than treating it as corrupt.
/// </remarks>
public readonly record struct SpooledEdit(DateTimeOffset At, string? Path);

public static class SpoolReader
{
    /// <summary>
    /// Reads every spooled edit in the order it was written, and removes what it read.
    /// </summary>
    /// <remarks>
    /// <para>Ordered by file name, which is safe because the writer leads each name with
    /// <c>DateTime.Ticks</c>: the sort is chronological for the same reason it is lexicographic.
    /// </para>
    ///
    /// <para><b>Deletes before its caller has acted.</b> An entry is therefore lost if the
    /// consumer fails after this returns, and the only recovery is a full rescan. That is
    /// tolerable precisely because a rescan is always available — the spool is an optimisation
    /// over walking the repo, never the sole record that a file changed — but a consumer that
    /// cannot rescan must not be built on this method as it stands.</para>
    /// </remarks>
    public static IReadOnlyList<SpooledEdit> Drain(string queueDir)
    {
        if (!Directory.Exists(queueDir))
        {
            return [];
        }

        var files = Directory.GetFiles(queueDir, "*.spool");
        Array.Sort(files, StringComparer.Ordinal);

        var edits = new List<SpooledEdit>(files.Length);
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (Parse(text) is { } edit)
            {
                edits.Add(edit);
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return edits;
    }

    /// <summary>
    /// A timestamp on the first line, and optionally a path on the second.
    /// </summary>
    /// <remarks>
    /// An unparseable entry is dropped rather than thrown on. This is a queue written by a hook
    /// that swallows its own errors to protect the budget, so a truncated file is a thing that
    /// happens; failing the whole drain over one would strand every edit behind it.
    /// </remarks>
    private static SpooledEdit? Parse(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0
            || !DateTimeOffset.TryParse(
                lines[0].Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var at))
        {
            return null;
        }

        var path = lines.Length > 1 ? lines[1].Trim() : string.Empty;
        return new SpooledEdit(at, path.Length > 0 ? path : null);
    }
}
