using System.Text;

namespace Engram.Core;

/// <summary>
/// The literal-token split every overlap-scoring lane shares: ASCII letters and digits only,
/// lowercased, runs delimited by everything else.
/// </summary>
/// <remarks>
/// One implementation, used by the query-side ranker (<see cref="RecallEngine"/>) and the
/// write-side index (<see cref="FactTokenIndex"/>) alike. SQLite triggers cannot call this, which
/// is why <c>fact_token</c> is maintained from C# call sites rather than from SQL triggers the way
/// <c>fact_fts</c> is — a trigger design would need a second, SQL-side tokenizer that could drift
/// from this one.
/// </remarks>
internal static class Tokenizer
{
    internal static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "and", "or", "the", "a", "an", "of", "to", "in", "for", "is", "are", "was", "were",
        "be", "on", "with", "that", "this", "it", "as", "at", "by", "from", "but", "not",
        "all", "any", "how", "what", "when", "where", "which", "who", "why", "do", "does",
        "did", "can", "should", "would", "will",
    };

    internal static HashSet<string> Tokenize(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return terms;
    }

    /// <summary>
    /// Whether a token is worth keeping — long enough and not a stopword. <c>fact_token</c> never
    /// stores a token this rejects (the storage saving); the query side uses the same predicate
    /// with a fallback for the case where filtering would empty the term set entirely.
    /// </summary>
    internal static bool IsIndexable(string term) => term.Length >= 3 && !Stopwords.Contains(term);
}
