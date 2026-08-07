using System.Text;

namespace Engram.Core;

/// <summary>
/// Extracts a gist-level impression from prose or a code file's lead comment: what the
/// text is <i>about</i>, in its own words. Extractive and deterministic — the same input
/// always yields the same fact, which is what lets the pipeline diff candidates against
/// live facts and skip the unchanged ones instead of superseding them on every run.
/// </summary>
/// <remarks>
/// The LLM-refined mode (<c>[impressions] mode = "llm"</c>) is a later, opt-in layer per
/// the plan's §5.4; this extractive tier is the default and must stand alone.
/// </remarks>
public static class ImpressionExtractor
{
    public const int MaxTokens = 60;

    /// <summary>
    /// Lead sentences plus recurring keywords from the rest. The lead stops at half the
    /// budget on purpose: given all sixty tokens it swallows every sentence in a short
    /// document and leaves the keyword half — the part that routes recall toward words
    /// the opening never uses — permanently empty.
    /// </summary>
    public static string? FromProse(string text)
    {
        var lead = LeadSentences(text, MaxTokens / 2);
        if (lead is null)
        {
            return null;
        }

        var keywords = SalientKeywords(text, exclude: lead, take: 5);
        if (keywords.Count < 2)
        {
            return lead;
        }

        var combined = $"{lead} — covers {string.Join(", ", keywords)}";
        return TokenEstimator.Estimate(combined) > MaxTokens ? Truncate(combined, MaxTokens) : combined;
    }

    /// <summary>
    /// A code file's impression is its lead comment. A file without one gets no impression
    /// — echoing code back as prose would route recall toward noise, and the declarations
    /// are already extracted as their own facts.
    /// </summary>
    public static string? FromLeadComment(string source)
    {
        var lines = source.Split('\n');
        var comment = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 && comment.Length == 0)
            {
                continue;
            }

            var stripped = StripCommentMarker(line);
            if (stripped is null)
            {
                break;
            }

            if (stripped.Length > 0)
            {
                if (comment.Length > 0)
                {
                    comment.Append(' ');
                }

                comment.Append(stripped);
            }
        }

        if (comment.Length == 0)
        {
            return null;
        }

        // Doc-comment markup (<summary>, <see …/>) is scaffolding, not gist.
        var withoutTags = System.Text.RegularExpressions.Regex.Replace(
            comment.ToString(), "<[^>]*>", " ");

        return LeadSentences(withoutTags, MaxTokens);
    }

    private static string? StripCommentMarker(string line)
    {
        foreach (var marker in CommentMarkers)
        {
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                return line[marker.Length..].TrimStart('/', '*', '!', '-', '=').Trim();
            }
        }

        return null;
    }

    private static readonly string[] CommentMarkers = ["///", "//", "/*", "*", "#!", "#", "--", "<!--"];

    private static string? LeadSentences(string text, int budget)
    {
        var flattened = Flatten(text);
        if (flattened.Length == 0)
        {
            return null;
        }

        var result = new StringBuilder();
        foreach (var sentence in SplitSentences(flattened))
        {
            var candidate = result.Length == 0 ? sentence : $"{result} {sentence}";
            if (result.Length > 0 && TokenEstimator.Estimate(candidate) > budget)
            {
                break;
            }

            result.Clear();
            result.Append(candidate);

            if (TokenEstimator.Estimate(result.ToString()) >= budget)
            {
                break;
            }
        }

        var lead = result.ToString();
        return TokenEstimator.Estimate(lead) > budget ? Truncate(lead, budget) : lead;
    }

    private static string Flatten(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || text[i + 1] == ' '))
            {
                yield return text[start..(i + 1)];
                start = i + 2;
                i++;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static string Truncate(string text, int budget)
    {
        var limit = Math.Min(text.Length, budget * 4);
        var cut = text.LastIndexOf(' ', Math.Max(0, limit - 1));
        return (cut > 0 ? text[..cut] : text[..limit]).TrimEnd() + "…";
    }

    private static IReadOnlyList<string> SalientKeywords(string text, string exclude, int take)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var excluded = new HashSet<string>(Tokenize(exclude), StringComparer.OrdinalIgnoreCase);

        foreach (var word in Tokenize(text))
        {
            if (word.Length < 4 || excluded.Contains(word) || Stopwords.Contains(word))
            {
                continue;
            }

            if (!counts.TryGetValue(word, out var count))
            {
                order.Add(word);
            }

            counts[word] = count + 1;
        }

        var salient = new List<string>();
        foreach (var word in order)
        {
            if (counts[word] >= 2)
            {
                salient.Add(word.ToLowerInvariant());
                if (salient.Count == take)
                {
                    break;
                }
            }
        }

        return salient;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_');
            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "from", "into", "over", "under", "than", "then", "when",
        "where", "which", "while", "would", "could", "should", "there", "their", "these",
        "those", "because", "about", "every", "never", "always", "after", "before", "does",
        "have", "will", "been", "being", "were", "what", "your", "ours", "they", "them",
    };
}
