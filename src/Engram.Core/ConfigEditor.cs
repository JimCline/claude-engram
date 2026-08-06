using System.Globalization;

namespace Engram.Core;

/// <summary>Why a key was left alone.</summary>
public sealed record ConfigConflict(string Section, string Key, string Found, string Shipped)
{
    public string Describe() =>
        $"[{Section}] {Key} is set to {Found}, which is not what Engram wrote there ({Shipped}).";
}

/// <summary>
/// Changes one value in a config file without disturbing anything else in it.
/// </summary>
/// <remarks>
/// <para><b>Line surgery rather than a TOML round-trip.</b> The shipped config is mostly prose:
/// which embedding providers exist, what each costs, why "none" is a supported answer rather than
/// a degraded one. Parsing it into a model and serializing it back would produce a valid file with
/// the documentation deleted — the config would still work and would no longer explain itself. So
/// the value on one line changes and every other byte survives.</para>
///
/// <para><b>It will not overwrite a value it did not write.</b> Two things count as ours: a value
/// still matching what the shipped default puts there, and a line carrying the marker comment this
/// writes. The marker is what makes the rule survive its own use — without it the second run of a
/// picker refuses the edit the first run made, since by then the file no longer matches the
/// shipped default. It lives on the line rather than in a state file beside the config, because a
/// record kept elsewhere starts lying the moment someone edits the line it describes. A
/// commented-out key counts as absent: the user left the shipped suggestion alone, so writing a
/// real one takes nothing away.</para>
/// </remarks>
public static class ConfigEditor
{
    /// <summary>Appended to any line this writes, so a later run can tell its own work from a user's.</summary>
    public const string Marker = "# written by engram";

    /// <summary>The value written against <paramref name="key"/>, or null if it is absent.</summary>
    /// <remarks>Raw and unparsed: quotes are kept, so a conflict check compares like with like.</remarks>
    public static string? Read(string text, string section, string key)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Split(text);
        if (SectionRange(lines, section) is not { } range)
        {
            return null;
        }

        for (var i = range.Start; i < range.End; i++)
        {
            if (KeyOn(lines[i]) == key)
            {
                return ValueOn(lines[i]);
            }
        }

        return null;
    }

    /// <summary>Whether the file still holds what Engram put there, so changing it takes nothing away.</summary>
    public static bool IsUntouched(string text, string shipped, string section, string key)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Split(text);
        if (SectionRange(lines, section) is not { } range)
        {
            return true;
        }

        for (var i = range.Start; i < range.End; i++)
        {
            if (KeyOn(lines[i]) != key)
            {
                continue;
            }

            return lines[i].Contains(Marker, StringComparison.Ordinal)
                || ValueOn(lines[i]) == Read(shipped, section, key);
        }

        return true;
    }

    /// <summary>Returns <paramref name="text"/> with one value changed, adding the key or section if missing.</summary>
    public static string Set(string text, string section, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);

        var lines = Split(text);
        var line = key + " = " + value + "   " + Marker;

        if (SectionRange(lines, section) is not { } range)
        {
            var appended = new List<string>(lines);
            if (appended.Count > 0 && appended[^1].Length > 0)
            {
                appended.Add(string.Empty);
            }

            appended.Add("[" + section + "]");
            appended.Add(line);
            return string.Join('\n', appended);
        }

        var edited = new List<string>(lines);

        for (var i = range.Start; i < range.End; i++)
        {
            if (KeyOn(edited[i]) != key)
            {
                continue;
            }

            var indent = edited[i][..(edited[i].Length - edited[i].TrimStart().Length)];
            edited[i] = indent + line;
            return string.Join('\n', edited);
        }

        // After the last real setting in the section rather than straight after the header: the
        // header is followed by the comments explaining the section, and a value wedged above them
        // reads as though the prose were about it.
        var insertAt = range.Start;
        for (var i = range.Start; i < range.End; i++)
        {
            if (KeyOn(edited[i]) is not null)
            {
                insertAt = i + 1;
            }
        }

        edited.Insert(insertAt, line);
        return string.Join('\n', edited);
    }

    /// <summary>Formats a string for TOML.</summary>
    public static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Copies the file aside before it is changed, never overwriting an older copy.</summary>
    /// <returns>Where the copy went, or null if there was no file to copy.</returns>
    public static string? Backup(string path, DateTimeOffset now)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var basePath = path + ".bak-" + now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var destination = basePath;

        for (var n = 2; File.Exists(destination); n++)
        {
            destination = $"{basePath}-{n}";
        }

        File.Copy(path, destination, overwrite: false);
        return destination;
    }

    private static string[] Split(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>The key a line assigns, or null if it assigns nothing — including when it is commented out.</summary>
    /// <summary>The value a line assigns, with any trailing comment removed.</summary>
    /// <remarks>
    /// The <c>#</c> is only a comment outside quotes — an endpoint URL is entitled to a fragment,
    /// and truncating one would write a config that silently points somewhere else.
    /// </remarks>
    private static string? ValueOn(string line)
    {
        var equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            return null;
        }

        var value = line[(equals + 1)..];
        var quoted = false;

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"' && (i == 0 || value[i - 1] != '\\'))
            {
                quoted = !quoted;
            }
            else if (value[i] == '#' && !quoted)
            {
                return value[..i].Trim();
            }
        }

        return value.Trim();
    }

    private static string? KeyOn(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '[')
        {
            return null;
        }

        var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
        return equals <= 0 ? null : trimmed[..equals].TrimEnd();
    }

    private static (int Start, int End)? SectionRange(string[] lines, string section)
    {
        var header = "[" + section + "]";
        var start = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (start < 0)
            {
                if (trimmed == header)
                {
                    start = i + 1;
                }

                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                return (start, i);
            }
        }

        return start < 0 ? null : (start, lines.Length);
    }
}
