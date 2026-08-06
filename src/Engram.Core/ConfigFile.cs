using System.Globalization;

namespace Engram.Core;

/// <summary>
/// Reads the subset of TOML that <c>config.toml</c> actually uses: sections, scalar keys, and
/// arrays of strings.
/// </summary>
/// <remarks>
/// <para>Hand-written rather than taken from a package, for the same reason there is no ORM
/// here: this file is read by an AOT binary on a latency budget, a TOML library is a
/// reflection-shaped dependency, and the grammar in play is twenty lines of it. If the config
/// ever grows inline tables or datetimes, that is the moment to reconsider — not before.</para>
///
/// <para><b>Lenient about keys, strict about values.</b> An unknown key is ignored, because that
/// is how a config file survives a version bump and how a user leaves themselves a note. A key
/// that is present but malformed is a different thing: silently falling back to a default there
/// would mean the user asked for something, got something else, and was told nothing. Those
/// surface as <see cref="ConfigError"/> entries that <c>doctor</c> can report.</para>
/// </remarks>
public sealed class ConfigFile
{
    private readonly Dictionary<string, Dictionary<string, string>> sections;

    private ConfigFile(
        Dictionary<string, Dictionary<string, string>> sections,
        IReadOnlyList<ConfigError> errors)
    {
        this.sections = sections;
        Errors = errors;
    }

    /// <summary>Lines that looked like settings but could not be read as any.</summary>
    public IReadOnlyList<ConfigError> Errors { get; }

    public static ConfigFile Empty { get; } = new([], []);

    /// <summary>Reads the file, or <see cref="Empty"/> if it is not there.</summary>
    /// <remarks>
    /// A missing config is not an error: every setting has a default, and an instance that has
    /// never been configured must behave exactly like one configured with the defaults.
    /// </remarks>
    public static ConfigFile Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;

    public static ConfigFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var errors = new List<ConfigError>();
        var current = "";
        var lineNumber = 0;

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lineNumber = index + 1;
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // An array may run over several lines, and it has to: the ignore list is twenty
            // patterns, and a config file a person is expected to edit cannot put those on one
            // line. Joined here so everything below still sees one logical setting.
            if (OpensAnArray(line))
            {
                while (index + 1 < lines.Length && !line.EndsWith(']'))
                {
                    index++;
                    line += " " + StripComment(lines[index]).Trim();
                }

                if (!line.EndsWith(']'))
                {
                    errors.Add(new ConfigError(lineNumber, line, "array is never closed"));
                    continue;
                }
            }

            if (line[0] == '[')
            {
                if (line[^1] != ']' || line.Length < 3)
                {
                    errors.Add(new ConfigError(lineNumber, line, "expected a section header like [embedding]"));
                    continue;
                }

                current = line[1..^1].Trim();
                continue;
            }

            var split = line.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
            {
                errors.Add(new ConfigError(lineNumber, line, "expected key = value"));
                continue;
            }

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            if (key.Length == 0)
            {
                errors.Add(new ConfigError(lineNumber, line, "expected key = value"));
                continue;
            }

            if (!sections.TryGetValue(current, out var bucket))
            {
                bucket = new Dictionary<string, string>(StringComparer.Ordinal);
                sections[current] = bucket;
            }

            // Last wins. A duplicated key is how someone edits a file by pasting over it, and
            // the later line is the one they were looking at.
            bucket[key] = value;
        }

        return new ConfigFile(sections, errors);
    }

    /// <summary>The raw text of a setting, or null if it is absent.</summary>
    public string? Raw(string section, string key) =>
        sections.TryGetValue(section, out var bucket) && bucket.TryGetValue(key, out var value)
            ? value
            : null;

    public string? String(string section, string key)
    {
        var raw = Raw(section, key);
        if (raw is null)
        {
            return null;
        }

        return Unquote(raw) is { } text && text.Length > 0 ? text : null;
    }

    public int? Int(string section, string key) =>
        Raw(section, key) is { } raw
        && int.TryParse(Unquote(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public bool? Bool(string section, string key) => Unquote(Raw(section, key) ?? "") switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    public IReadOnlyList<string> Strings(string section, string key)
    {
        var raw = Raw(section, key);
        if (raw is null || raw.Length < 2 || raw[0] != '[' || raw[^1] != ']')
        {
            return [];
        }

        var items = new List<string>();
        foreach (var part in raw[1..^1].Split(','))
        {
            var item = Unquote(part.Trim());
            if (item.Length > 0)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>Whether this line starts an array value that may not close on it.</summary>
    private static bool OpensAnArray(string line)
    {
        var split = line.IndexOf('=', StringComparison.Ordinal);

        return split > 0 && line[(split + 1)..].TrimStart().StartsWith('[');
    }

    /// <summary>
    /// Drops a trailing <c>#</c> comment, leaving one inside a quoted value alone — endpoints
    /// carry fragments and API keys carry anything.
    /// </summary>
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == '#' && !quoted)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }
}

/// <summary>A line that looked like a setting but could not be read as one.</summary>
public sealed record ConfigError(int Line, string Text, string Problem)
{
    public override string ToString() => $"line {Line}: {Problem} — {Text}";
}
