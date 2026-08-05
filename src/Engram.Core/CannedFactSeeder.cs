using System.Text;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// Puts the curated corpus into the real store.
/// </summary>
/// <remarks>
/// Paths are <c>/knowledge/&lt;topic&gt;/&lt;subject&gt;</c>. The topic is a path segment
/// rather than a column or an edge because grouping by topic is then a range scan over the
/// path index, which is what D2 built <c>path</c> for. It costs a fact its ability to hold
/// more than one topic; that is recoverable — retopicing is a subtree move, and multi-topic
/// membership can be layered on as edges later without moving anything.
/// </remarks>
public static class CannedFactSeeder
{
    public const string Root = "/knowledge";

    public const string SubjectKind = "concept";

    /// <summary>
    /// Authored, not derived: a human wrote these bodies, so they are <c>stated</c> and not
    /// regenerable. Marking them regenerable would invite `repair` to discard the entire
    /// seed corpus as rebuildable derived state (D23).
    /// </summary>
    public const string LearnedVia = "stated";

    public static string PathFor(CannedFact fact) => $"{Root}/{Slug(fact.Topic)}/{Slug(fact.Subject)}";

    public static string TopicPath(string topic) => $"{Root}/{Slug(topic)}";

    public static int Seed(SqliteConnection connection, DateTimeOffset now) =>
        Seed(connection, CannedFacts.All, now);

    /// <summary>
    /// Writes every fact that is not already stored, and returns how many were written.
    /// </summary>
    /// <remarks>
    /// Idempotent by necessity rather than by politeness. Seeding is not a one-shot
    /// migration — it runs against whatever database is already there, and
    /// <see cref="FactStore.Remember"/> supersedes on a subject+predicate collision. Without
    /// the skip, a second run would close all 51 facts and rewrite them identically,
    /// producing a supersession history that records a change nobody made.
    /// </remarks>
    public static int Seed(SqliteConnection connection, IReadOnlyList<CannedFact> facts, DateTimeOffset now)
    {
        var existing = new Dictionary<(string Path, string Predicate), string>();
        foreach (var stored in FactStore.ReadLive(connection))
        {
            existing[(stored.SubjectPath, stored.Predicate)] = stored.Body;
        }

        var written = 0;
        using var transaction = EngramDatabase.BeginWrite(connection);

        foreach (var fact in facts)
        {
            var path = PathFor(fact);

            // Same body means nothing changed. A DIFFERENT body for the same subject and
            // predicate is a genuine revision of the corpus, and falls through to Remember so
            // it supersedes properly instead of being silently dropped.
            if (existing.TryGetValue((path, fact.Predicate), out var body) && body == fact.Body)
            {
                continue;
            }

            FactStore.Remember(
                connection,
                transaction,
                new FactWrite(
                    SubjectPath: path,
                    SubjectKind: SubjectKind,
                    Predicate: fact.Predicate,
                    Body: fact.Body,
                    Scope: fact.Scope,
                    LearnedVia: LearnedVia,
                    Evidence: fact.Evidence,
                    Regenerable: false),
                now,
                reason: "the seed corpus revised this statement");

            written++;
        }

        transaction.Commit();
        return written;
    }

    /// <summary>
    /// Lowercases and hyphenates a topic or subject for use as a path segment. Anything that
    /// is not a letter, digit, or hyphen becomes a hyphen, so a path segment can never carry
    /// a '/' and silently invent a level of hierarchy.
    /// </summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().TrimEnd('-');
    }
}
