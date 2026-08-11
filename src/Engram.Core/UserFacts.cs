using Microsoft.Data.Sqlite;

namespace Engram.Core;

public enum UserFactTopic
{
    /// <summary>Something the user stated about themselves.</summary>
    AboutYou,

    /// <summary>Something the user told the agent to do or not do.</summary>
    Instruction,
}

/// <summary>
/// What the user said, stored as ordinary facts.
/// </summary>
/// <remarks>
/// These used to live in their own JSON directory with their own notion of active, retracted,
/// and superseded — a second implementation of the validity window the <c>fact</c> table
/// already has. The two agreed only by coincidence, and the JSON one got none of
/// <c>BEGIN IMMEDIATE</c>, <c>busy_timeout</c>, or a supersession record saying why a belief
/// changed. This is the same data through the one temporal model.
///
/// <para>
/// The statement is its own subject: the path leaf is a hash of the normalized text, so the
/// same sentence always addresses the same entity. That is what makes a repeat capture
/// recognizable — the JSON store could not tell "the user said this again" from "the user
/// said something new", because it had no key to compare on, and every restatement became
/// another row in recall.
/// </para>
/// <para>
/// The entity is not named after the statement, deliberately. A name is denormalized display
/// text that is written once, while the body is free to be superseded; naming the entity
/// after its first body would leave the name describing a belief no longer held.
/// </para>
/// </remarks>
public static class UserFacts
{
    public const string Root = "/user";

    public const string AboutYouPath = Root + "/about-you";

    public const string InstructionPath = Root + "/instructions";

    /// <summary>
    /// The entity kind for a statement node. Not <c>preference</c> or <c>person</c>: the
    /// subject here is the utterance, and what it is about is the body's business.
    /// </summary>
    public const string StatementKind = "statement";

    // Aliased rather than spelled again: FactCatalog.ReadTopicNames selects on this one
    // value, so a second literal that drifted would make a whole root's topics unresolvable
    // with nothing to report it but slightly wrong display text.
    public const string TopicKind = CannedFactSeeder.TopicKind;

    public const string Scope = "user";

    /// <summary>
    /// The user's own words, so the strongest tier D19 defines. A model's rewrite of a
    /// capture keeps it: a faithful restatement of what someone said is still their
    /// statement, and demoting it to <c>inferred</c> would rank the raw sentence above the
    /// legible one for no reason a reader would endorse.
    /// </summary>
    public const string LearnedVia = "stated";

    public static string PathFor(UserFactTopic topic, string statement) =>
        TopicPath(topic) + "/" + FactStore.Fingerprint(statement);

    public static string TopicPath(UserFactTopic topic) =>
        topic == UserFactTopic.Instruction ? InstructionPath : AboutYouPath;

    /// <summary>
    /// The predicate carries the topic distinction as well as the path does, so a fact read
    /// back without its path still says which kind of thing it is.
    /// </summary>
    public static string PredicateFor(UserFactTopic topic) =>
        topic == UserFactTopic.Instruction ? "requires" : "stated";

    public static string DisplayNameFor(UserFactTopic topic) =>
        topic == UserFactTopic.Instruction ? "your standing instructions" : "about you";

    /// <summary>
    /// Creates the two topic nodes. Idempotent, writes no facts, and not gated on anything:
    /// they are addressing metadata, and a store written before they existed should be able
    /// to acquire them without declaring anything revised.
    /// </summary>
    public static void EnsureTopics(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset now)
    {
        var createdAt = now.ToUnixTimeSeconds();

        foreach (var topic in (ReadOnlySpan<UserFactTopic>)[UserFactTopic.AboutYou, UserFactTopic.Instruction])
        {
            FactStore.EnsureEntity(
                connection,
                transaction,
                TopicPath(topic),
                TopicKind,
                createdAt,
                displayName: DisplayNameFor(topic));
        }
    }

    /// <summary>
    /// Records something the user said, and returns the new fact's id — or null if this
    /// store already holds a live fact for that statement.
    /// </summary>
    /// <remarks>
    /// Saying something again is not new information, so a repeat is silence rather than a
    /// second row or a supersession. The check is deliberately on the entity rather than on
    /// the body: after a model rewrites a capture into a self-contained sentence, the user
    /// repeating the original must not drag the good version back to the raw one. What the
    /// store already knows about that statement wins.
    ///
    /// A statement the user retracted has no live fact, so saying it again captures it
    /// afresh. That is the intended asymmetry with the seed corpus, which stays silent on
    /// facts a user forgot: a re-seed is nobody asking for it back, and the user typing the
    /// sentence again is.
    /// </remarks>
    public static long? Capture(
        SqliteConnection connection,
        UserFactTopic topic,
        string statement,
        string? sessionExternalId,
        DateTimeOffset now)
    {
        var path = PathFor(topic, statement);
        var predicate = PredicateFor(topic);

        using var transaction = EngramDatabase.BeginWrite(connection);

        if (FactStore.FindLiveFactId(connection, transaction, path, predicate) is not null)
        {
            transaction.Rollback();
            return null;
        }

        EnsureTopics(connection, transaction, now);

        var sessionId = sessionExternalId is { Length: > 0 } external
            ? SessionStore.EnsureSession(connection, transaction, external, now)
            : (long?)null;

        var result = FactStore.Remember(
            connection,
            transaction,
            new FactWrite(
                SubjectPath: path,
                SubjectKind: StatementKind,
                Predicate: predicate,
                Body: statement,
                Scope: Scope,
                LearnedVia: LearnedVia,
                Evidence: "stated by the user",
                Regenerable: false,
                SessionId: sessionId),
            now,
            reason: "the user restated this");

        transaction.Commit();
        return result.FactId;
    }

    /// <summary>
    /// Replaces a fact with a rewritten version of the same statement, and returns the new
    /// fact's id — or null if the target is not a live fact.
    /// </summary>
    /// <remarks>
    /// The replacement is written against the target's own subject and predicate, so the
    /// store's ordinary collision rule closes the old one and files the supersession. The
    /// rewrite therefore inherits the original's address: a capture that gets restated three
    /// times leaves one live fact and a chain, not four rows that all look current.
    /// </remarks>
    public static long? Restate(
        SqliteConnection connection,
        long targetFactId,
        string statement,
        string? sessionExternalId,
        DateTimeOffset now,
        string reason = "restated so it stands on its own",
        string? details = null)
    {
        var target = FactStore.ReadById(connection, targetFactId);
        if (target is null || target.ValidTo is not null)
        {
            return null;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);

        var sessionId = sessionExternalId is { Length: > 0 } external
            ? SessionStore.EnsureSession(connection, transaction, external, now)
            : (long?)null;

        var result = FactStore.Remember(
            connection,
            transaction,
            new FactWrite(
                SubjectPath: target.SubjectPath,
                SubjectKind: StatementKind,
                Predicate: target.Predicate,
                Body: statement,
                Scope: target.Scope,
                LearnedVia: LearnedVia,
                Evidence: "stated by the user",
                Regenerable: false,
                SessionId: sessionId,
                Details: details),
            now,
            reason);

        transaction.Commit();
        return result.FactId;
    }

}
