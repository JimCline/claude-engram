using System.Globalization;

namespace Engram.Core;

/// <summary>
/// Converts a relative duration or an absolute date into a unix timestamp, for the
/// <c>review_after</c> parameter (docs/memory-expansion/04-lifecycle-spec.md). The spec leaves
/// the accepted syntax unstated; this extends the one existing precedent in the codebase —
/// <c>ProbeCommand</c>'s <c>--since &lt;n&gt;d</c> convention — from days only to
/// <c>Nh</c>/<c>Nd</c>/<c>Nw</c>, plus an ISO-8601 absolute date or date-time.
/// </summary>
public static class DurationParsing
{
    /// <summary>
    /// Parses <paramref name="text"/> as either <c>N</c> followed by <c>h</c>/<c>d</c>/<c>w</c>
    /// (relative to <paramref name="now"/>) or an ISO-8601 date/date-time. Returns false, leaving
    /// <paramref name="unixSeconds"/> at zero, if neither form matches.
    /// </summary>
    public static bool TryParse(string? text, DateTimeOffset now, out long unixSeconds)
    {
        unixSeconds = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        if (trimmed.Length >= 2 && char.IsDigit(trimmed[0]))
        {
            var unit = trimmed[^1];
            var span = unit switch
            {
                'h' => TimeSpan.FromHours(1),
                'd' => TimeSpan.FromDays(1),
                'w' => TimeSpan.FromDays(7),
                _ => (TimeSpan?)null,
            };

            if (span is not null
                && int.TryParse(trimmed[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                && count > 0)
            {
                unixSeconds = now.ToUnixTimeSeconds() + (long)(span.Value.TotalSeconds * count);
                return true;
            }
        }

        if (DateTimeOffset.TryParseExact(
                trimmed, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            unixSeconds = parsed.ToUnixTimeSeconds();
            return true;
        }

        return false;
    }

    // Explicit ISO-8601 forms only — DateTimeOffset.TryParse's general parser also accepts
    // ambiguous formats like "01/09/2026", which it silently resolves differently depending on
    // whether it reads month-first or day-first, with no error either way.
    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.fffK",
    ];
}
