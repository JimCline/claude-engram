using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>One live or retired directive, as both the CLI and the primer need it.</summary>
public sealed record Directive(long Id, string Body, long ValidFrom, long? ValidTo, long? SupersededBy);

/// <summary>
/// A standing instruction the user authored deliberately — "always X", "never Y" — delivered
/// in full at every context reset rather than retrieved when a query happens to reach it. See
/// docs/memory-expansion/07-directives-spec.md (D-6 through D-10).
/// </summary>
/// <remarks>
/// A structural sibling of <see cref="SessionFacts"/>, but flatter: each directive is its own
/// entity directly under <see cref="Root"/> rather than nested under a session/agent tree, so
/// no intermediate entity needs ensuring beyond what <see cref="FactStore.Remember"/> already
/// does for the leaf path.
/// </remarks>
public static class DirectiveFacts
{
    public const string Root = "/directives";
    public const string Predicate = "directs";
    public const string Scope = "user";

    /// <summary>D19 reserves "stated" for the user's own words; nothing about a directive was
    /// worked out by an agent, unlike a session note's "observed" (<see cref="SessionFacts"/>).</summary>
    public const string LearnedVia = "stated";

    public const string Kind = "directive";

    /// <summary>
    /// Every context reset and subagent spawn rebuilds the primer, so the bound is what is
    /// reasonable to pay for on every spawn, not what is reasonable to author once. Hardcoded
    /// rather than a config key — a bound a caller can raise is not a bound.
    /// </summary>
    public const int MaxDirectiveTokens = 250;

    private const int MaxSlugChars = 60;

    /// <summary>
    /// <c>&lt;slug&gt;-&lt;8-char fingerprint&gt;</c>, deliberately not a bare fingerprint like
    /// a session note's path. A session note is found by content, through recall; a directive
    /// is enumerated by class, through a listing, and its path is what the listing shows — a
    /// bare hash would render <c>engram_browse("/directives")</c> as a column of opaque hashes.
    /// The slug is truncated before the suffix is appended so the path stays bounded; identity
    /// lives in the fingerprint, so truncating the slug is safe.
    /// </summary>
    public static string PathFor(string statement)
    {
        var slug = CannedFactSeeder.Slug(statement);
        if (slug.Length > MaxSlugChars)
        {
            slug = slug[..MaxSlugChars].TrimEnd('-');
        }

        return Root + "/" + slug + "-" + FactStore.Fingerprint(statement)[..8];
    }

    /// <summary>Writes a brand new directive at a fresh, content-derived path.</summary>
    public static long Add(SqliteConnection connection, string statement, DateTimeOffset now)
    {
        var result = FactStore.Remember(
            connection,
            new FactWrite(
                SubjectPath: PathFor(statement),
                SubjectKind: Kind,
                Predicate: Predicate,
                Body: statement,
                Scope: Scope,
                LearnedVia: LearnedVia,
                Regenerable: false),
            now);

        return result.FactId;
    }

    /// <summary>Live directives, oldest first — the reading order the primer renders them in.</summary>
    public static IReadOnlyList<Directive> ReadLive(SqliteConnection connection) => Read(connection, liveOnly: true);

    /// <summary>Every directive ever written, live and retired — the CLI's <c>list --all</c> history surface.</summary>
    public static IReadOnlyList<Directive> ReadAll(SqliteConnection connection) => Read(connection, liveOnly: false);

    /// <remarks>
    /// A range scan over <c>fact.path</c> (the denormalized column, not a join through
    /// <c>entity</c>), which is what lets this use <c>ix_fact_path</c> directly. Written with
    /// <c>&gt;=</c>/<c>&lt;</c> range bounds rather than <c>LIKE</c> or <c>substr</c> — SQLite
    /// cannot plan an index seek through <c>substr()</c>, and <c>LIKE</c> is case-insensitive by
    /// default, which disables the prefix optimization. Either would silently degrade to a full
    /// scan of <c>fact</c> on a hook path (D60).
    /// </remarks>
    private static IReadOnlyList<Directive> Read(SqliteConnection connection, bool liveOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText = liveOnly
            ? """
              SELECT id, body, valid_from, valid_to, superseded_by
                FROM fact
               WHERE path >= '/directives/' AND path < '/directives0'
                 AND valid_to IS NULL
               ORDER BY valid_from;
              """
            : """
              SELECT id, body, valid_from, valid_to, superseded_by
                FROM fact
               WHERE path >= '/directives/' AND path < '/directives0'
               ORDER BY valid_from;
              """;

        var directives = new List<Directive>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            directives.Add(new Directive(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return directives;
    }
}
