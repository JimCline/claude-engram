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
    /// A timestamp on the first line, and optionally a path on the second.
    /// </summary>
    /// <remarks>
    /// An unparseable entry is null rather than a throw. This is a queue written by a hook that
    /// swallows its own errors to protect the budget, so a truncated file is a thing that happens;
    /// failing the whole drain over one would strand every edit behind it.
    /// </remarks>
    public static SpooledEdit? Parse(string text)
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
