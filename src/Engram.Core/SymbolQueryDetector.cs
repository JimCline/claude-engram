namespace Engram.Core;

/// <summary>
/// Whether a search query is shaped like a <em>symbol</em> lookup — a function, class, method or
/// route name — rather than a text search for a literal, a log line, a TODO or a config key.
/// This is what decides whether the <c>lookup-nudge</c> hook defers to <c>engram_navigate</c>.
/// </summary>
/// <remarks>
/// <para>
/// False positives are the expensive failure here, and the asymmetry is the whole design. Grep,
/// Glob and shell greps carry most of the ordinary search traffic in a session, so a detector
/// that fires on plain word searches taxes every one of them to correct a habit that only shows
/// up on symbol lookups. Every rule below is therefore a reason to stay silent, and the shape
/// that fires is deliberately narrow: a bare identifier carrying a case transition, an
/// underscore, or a qualifier separator.
/// </para>
/// <para>
/// A lowercase word like <c>latency</c> is indistinguishable from prose at this layer and never
/// fires — a false negative accepted on purpose, because the alternative is firing on every
/// word search in the session. So is a short single-capital name like <c>Todo</c>: one leading
/// capital is how English spells a sentence, not how code spells a distinctive symbol.
/// </para>
/// </remarks>
public static class SymbolQueryDetector
{
    /// <summary>
    /// Below this a name carries too little shape to tell from an abbreviation or a flag value,
    /// and the qualified-name rule would accept file extensions (<c>.cs</c>, <c>.ts</c>).
    /// </summary>
    private const int MinimumLength = 3;

    /// <summary>
    /// Regex metacharacters, quoting and path syntax: their presence means the caller is
    /// searching text or walking paths, not naming a symbol. Checked first because it is what
    /// excludes glob patterns (<c>**/*.tsx</c>), quoted phrases and alternations in one pass.
    /// </summary>
    private static readonly char[] SearchSyntax =
    [
        ' ', '\t', '*', '?', '|', '(', ')', '[', ']', '{', '}', '\\', '^', '$', '+',
        '"', '\'', '<', '>', '/', '@', '#', '%', '&', '!', ',', ';', '=', '~', '`',
    ];

    /// <summary>Separators that make a name <em>qualified</em>: <c>Type.Member</c>, <c>ns::name</c>.</summary>
    private static readonly char[] Qualifiers = ['.', ':'];

    /// <summary>Commands whose first non-flag argument is a search pattern.</summary>
    private static readonly string[] GrepFamily = ["grep", "rg", "ag", "ack", "ripgrep"];

    private static readonly char[] StageSeparators = ['|', ';', '\n'];

    public static bool LooksLikeSymbol(string? query)
    {
        if (query is not { Length: >= MinimumLength })
        {
            return false;
        }

        if (query.IndexOfAny(SearchSyntax) >= 0)
        {
            return false;
        }

        var parts = query.Split(Qualifiers, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            if (!Array.TrueForAll(parts, IsIdentifier))
            {
                return false;
            }

            // `::` is unambiguous: no filename is spelled with it, so the parts need no shape of
            // their own and a lowercase `engram::recall` still fires. A `.` has to clear a
            // second bar, because `HookCommand.cs` is spelled exactly like a qualified member —
            // requiring the LAST part to carry symbol shape is what tells the extension apart
            // from the method, at the cost of never firing on an all-lowercase `foo.bar`.
            return query.Contains("::", StringComparison.Ordinal) || IsSymbolShaped(parts[^1]);
        }

        return IsSymbolShaped(query);
    }

    /// <summary>
    /// The search pattern inside a shell command, or null when the command is not a search.
    /// </summary>
    /// <remarks>
    /// The first non-flag token after a grep-family command is its pattern; that holds for every
    /// member of <see cref="GrepFamily"/>. Flags are skipped by their leading dash, which means a
    /// separate-value flag (<c>--include "*.cs" Name</c>) donates its value instead of the real
    /// pattern — and that value carries glob syntax, so <see cref="LooksLikeSymbol"/> rejects it.
    /// The heuristic therefore fails toward staying silent, which is the direction that costs
    /// nothing.
    /// </remarks>
    public static string? ExtractSearchPattern(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        foreach (var stage in command.Split(StageSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = stage.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!IsGrepFamily(tokens[i]))
                {
                    continue;
                }

                for (var j = i + 1; j < tokens.Length; j++)
                {
                    if (tokens[j].StartsWith('-'))
                    {
                        continue;
                    }

                    return Unquote(tokens[j]);
                }

                break;
            }
        }

        return null;
    }

    private static bool IsGrepFamily(string token)
    {
        // Take the command name off a path (`/usr/bin/grep`) before comparing.
        var name = token[(token.LastIndexOf('/') + 1)..];
        return Array.Exists(GrepFamily, g => string.Equals(name, g, StringComparison.Ordinal));
    }

    private static string Unquote(string token)
    {
        if (token.Length >= 2 && (token[0] is '"' or '\'') && token[^1] == token[0])
        {
            return token[1..^1];
        }

        return token;
    }

    private static bool IsSymbolShaped(string name) =>
        name.Length >= MinimumLength
        && IsIdentifier(name)
        && (HasCaseTransition(name) || name.Contains('_', StringComparison.Ordinal));

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || (!char.IsLetter(name[0]) && name[0] != '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A lower-to-upper transition, which is what separates <c>ProcessFile</c> from <c>TODO</c>
    /// and from a single capitalized English word.
    /// </summary>
    private static bool HasCaseTransition(string name)
    {
        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
            {
                return true;
            }
        }

        return false;
    }
}
