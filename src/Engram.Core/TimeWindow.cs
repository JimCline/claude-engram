using System.Globalization;

namespace Engram.Core;

public static class TimeWindow
{
    public static bool TryParse(string value, out TimeSpan window)
    {
        window = TimeSpan.Zero;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var unit = value[^1];
        var digits = char.IsDigit(unit) ? value : value[..^1];

        if (digits.Length == 0
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            || n <= 0)
        {
            return false;
        }

        long seconds = char.IsDigit(unit)
            ? n
            : unit switch
            {
                's' => n,
                'm' => (long)n * 60,
                'h' => (long)n * 3600,
                'd' => (long)n * 86400,
                _ => -1,
            };

        if (seconds < 0 || seconds > (long)TimeSpan.MaxValue.TotalSeconds)
        {
            return false;
        }

        window = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
