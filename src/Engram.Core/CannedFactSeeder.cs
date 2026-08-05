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

    public const string TopicKind = "topic";

    /// <summary>
    /// Authored, not derived: a human wrote these bodies, so they are <c>stated</c> and not
    /// regenerable. Marking them regenerable would invite `repair` to discard the entire
    /// seed corpus as rebuildable derived state (D23).
    /// </summary>
    public const string LearnedVia = "stated";

    public static string PathFor(CannedFact fact) => $"{Root}/{Slug(fact.Topic)}/{Slug(fact.Subject)}";

    public static string TopicPath(string topic) => $"{Root}/{Slug(topic)}";

    public const string SeededVersionKey = "seed_corpus_version";

    public static int Seed(SqliteConnection connection, DateTimeOffset now) =>
        Seed(connection, CannedFacts.All, now);

    /// <summary>
    /// Seeds only if this database has not already had this corpus version applied, and
    /// returns how many facts were written.
    /// </summary>
    /// <remarks>
    /// The recorded version keeps seeding out of the read path. It is a short-circuit, not a
    /// safety mechanism: deciding "is this store already seeded?" by counting rows would be
    /// wrong for a user who has forgotten every seeded fact, but the protection against
    /// resurrecting those facts lives in <see cref="Seed"/>, which will not rewrite a
    /// subject and predicate this store has held before. Bumping the version to ship a
    /// revised corpus is therefore safe: revisions land on facts still live, and facts the
    /// user deleted stay deleted.
    /// </remarks>
    public static int SeedOnce(SqliteConnection connection, DateTimeOffset now)
    {
        var recorded = EngramDatabase.ReadMeta(connection, SeededVersionKey);
        if (int.TryParse(recorded, out var applied) && applied >= CannedFacts.Version)
        {
            return 0;
        }

        var written = Seed(connection, CannedFacts.All, now);
        EngramDatabase.WriteMeta(
            connection,
            transaction: null,
            SeededVersionKey,
            CannedFacts.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return written;
    }

    /// <summary>
    /// Writes every fact that is not already stored, and returns how many were written.
    /// </summary>
    /// <remarks>
    /// Idempotent by necessity rather than by politeness. Seeding is not a one-shot
    /// migration — it runs against whatever database is already there, and
    /// <see cref="FactStore.Remember"/> supersedes on a subject+predicate collision. Without
    /// the skip, a second run would close all 51 facts and rewrite them identically,
    /// producing a supersession history that records a change nobody made.
    ///
    /// Four cases, and the third is the one that is easy to get wrong:
    /// <list type="bullet">
    /// <item>live, same body — nothing changed, skip.</item>
    /// <item>live, different body — a real corpus revision, supersede.</item>
    /// <item>not live but written before — the user forgot it, skip. Deciding this from the
    /// live set alone is impossible: a forgotten fact and a fact that never existed look
    /// identical there, so a revision would resurrect everything anybody had deleted.</item>
    /// <item>never written — new statement, write it.</item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// Creates the topic node for each topic in <paramref name="facts"/>, carrying the
    /// authored display text as its name. Idempotent, and writes no facts.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SeedOnce"/>, and deliberately not gated on the corpus
    /// version. A store seeded by an earlier build has the facts but not these nodes, and
    /// tying their appearance to a corpus version bump would mean display metadata could
    /// only be repaired by declaring the corpus revised. Topic nodes are addressing
    /// metadata rather than belief content, so creating them is not a fact write and needs
    /// no such gate.
    /// </remarks>
    public static void EnsureTopics(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<CannedFact> facts,
        DateTimeOffset now)
    {
        var createdAt = now.ToUnixTimeSeconds();

        foreach (var topic in facts.Select(f => f.Topic).Distinct(StringComparer.Ordinal))
        {
            FactStore.EnsureEntity(connection, transaction, TopicPath(topic), TopicKind, createdAt, displayName: topic);
        }
    }

    public static void EnsureTopics(SqliteConnection connection, DateTimeOffset now)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        EnsureTopics(connection, transaction, CannedFacts.All, now);
        transaction.Commit();
    }

    public static int Seed(SqliteConnection connection, IReadOnlyList<CannedFact> facts, DateTimeOffset now)
    {
        var live = new Dictionary<(string Path, string Predicate), string>();
        foreach (var stored in FactStore.ReadLive(connection))
        {
            live[(stored.SubjectPath, stored.Predicate)] = stored.Body;
        }

        var everWritten = FactStore.ReadEverWritten(connection);

        var written = 0;
        using var transaction = EngramDatabase.BeginWrite(connection);

        EnsureTopics(connection, transaction, facts, now);

        foreach (var fact in facts)
        {
            var path = PathFor(fact);
            var key = (path, fact.Predicate);

            if (live.TryGetValue(key, out var body))
            {
                // Same body means nothing changed. A DIFFERENT body is a genuine revision of
                // the corpus, and falls through to Remember so it supersedes properly instead
                // of being silently dropped.
                if (body == fact.Body)
                {
                    continue;
                }
            }
            else if (everWritten.Contains(key))
            {
                // Not live, but this store held it once: the user forgot it. Re-seeding would
                // hand back exactly what they asked to be rid of, and a corpus revision is not
                // consent to undo that. Silence is the only correct answer, including when the
                // authored body has since changed — a better version of a fact somebody
                // deleted is still a fact somebody deleted.
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
