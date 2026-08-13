using Microsoft.Data.Sqlite;

namespace Engram.Core;

public enum RepoEnrollmentState
{
    Enrolled,
    Declined,
    Deferred,
}

public sealed record RepoEnrollmentRow(
    string Identity,
    RepoEnrollmentState State,
    string Source,
    string? LastRoot,
    long DecidedAt,
    long? LastFullScanAt);

/// <summary>
/// Authored truth: the user's answer to "should Engram index this repo". Kept out of
/// <c>repo_registry</c>, which <see cref="StoreCompactor"/> deletes rows from under a path
/// prefix — a decision stored there would be un-declined by <c>compact</c> and the user
/// re-prompted (D8). Keyed on the same <c>identity</c> value
/// <see cref="CodeIndexer.ResolveIdentity"/> writes to <c>repo_registry.identity</c>, so an
/// enrollment decision can be recorded for a repo that has never been indexed — a declined repo
/// is by definition never indexed and never gets a <c>repo_registry</c> row.
/// </summary>
public static class RepoEnrollment
{
    /// <summary>
    /// How long a "not now" answer suppresses the primer line before it is offered again — a
    /// human-politeness interval, not a performance number, so it is a constant rather than
    /// config (D58's one-unmeasured-knob rule, same reasoning as the full-scan interval).
    /// </summary>
    public static readonly TimeSpan DeferralCooldown = TimeSpan.FromDays(7);

    public static RepoEnrollmentRow? Get(SqliteConnection connection, string identity) =>
        Query(connection, "identity = $key", ("$key", identity));

    /// <summary>
    /// The hook-safe lookup: a cache read, no subprocess. Misses when a repo has moved on disk
    /// since it was last resolved here — the caller falls back to <see cref="IsEnrolled"/> off
    /// the hook's own clock (D4).
    /// </summary>
    public static RepoEnrollmentRow? ByRoot(SqliteConnection connection, string root) =>
        Query(connection, "last_root = $key", ("$key", PathCanonicalizer.Canonical(root)));

    /// <summary>
    /// The enclosing git checkout root, or null if there is none. Filesystem-only: the
    /// session-start hook may not spawn <c>git rev-parse</c> on its own clock (D4), and
    /// <c>last_root</c> stores the checkout root, so a lookup keyed by an arbitrary working
    /// directory misses for every session started in a subdirectory. Walks up one directory per
    /// path component and is bounded by path depth, not by <see cref="ScanBudget"/> — that bounds
    /// enumeration of a subtree walking down, which this never does.
    /// </summary>
    public static string? FindCheckoutRoot(string startDirectory)
    {
        var current = PathCanonicalizer.Canonical(startDirectory);

        while (true)
        {
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                return null;
            }

            current = parent.FullName;
        }
    }

    /// <summary>
    /// Full two-step resolution: <see cref="ByRoot"/> first, then — only on a miss —
    /// <see cref="CodeIndexer.ResolveIdentity"/>, which shells out to git. Callers on the
    /// session-start hook's own latency budget must use <see cref="ByRoot"/> alone; this method
    /// is for the detached maintenance child, never the hook itself (D4).
    /// </summary>
    public static bool IsEnrolled(SqliteConnection connection, string root)
    {
        try
        {
            var cached = ByRoot(connection, root);
            if (cached is not null)
            {
                return cached.State == RepoEnrollmentState.Enrolled;
            }

            var identity = CodeIndexer.ResolveIdentity(root);
            var row = Get(connection, identity);
            if (row is null)
            {
                return false;
            }

            UpdateLastRoot(connection, identity, root);
            return row.State == RepoEnrollmentState.Enrolled;
        }
        catch (SqliteException e) when (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // The detached maintenance child can race the parent's own migration on the first
            // session after an upgrade — session-start spawns this child before the parent's
            // OpenInitialized has finished migrating the store to schema 7, so a store with no
            // repo_enrollment table at all is a reachable state here, not just a theoretical one.
            // Nothing is enrolled in a store that has no enrollment table.
            return false;
        }
    }

    public static void Enroll(SqliteConnection connection, string identity, string root, DateTimeOffset now) =>
        Upsert(connection, identity, RepoEnrollmentState.Enrolled, root, now);

    public static void Decline(SqliteConnection connection, string identity, string root, DateTimeOffset now) =>
        Upsert(connection, identity, RepoEnrollmentState.Declined, root, now);

    public static void Defer(SqliteConnection connection, string identity, string root, DateTimeOffset now) =>
        Upsert(connection, identity, RepoEnrollmentState.Deferred, root, now);

    /// <summary>Removes the recorded decision, returning the repo to never-asked. Returns whether a row existed.</summary>
    public static bool Reset(SqliteConnection connection, string identity)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        var deleted = Execute(
            connection,
            transaction,
            "DELETE FROM repo_enrollment WHERE identity = $identity;",
            ("$identity", identity));
        transaction.Commit();
        return deleted > 0;
    }

    public static IReadOnlyList<RepoEnrollmentRow> ListAll(SqliteConnection connection)
    {
        var rows = new List<RepoEnrollmentRow>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT identity, state, source, last_root, decided_at, last_full_scan_at "
                + "FROM repo_enrollment ORDER BY identity;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    /// <summary>
    /// Repairs the lookup cache after a checkout moves. Idempotent, and — wherever it runs —
    /// what keeps <see cref="ByRoot"/> answering correctly on the next lookup, which is the
    /// property that lets the git subprocess in <see cref="IsEnrolled"/> stay off the
    /// session-start hook's own clock.
    /// </summary>
    public static void UpdateLastRoot(SqliteConnection connection, string identity, string root)
    {
        var canonicalRoot = PathCanonicalizer.Canonical(root);

        using var transaction = EngramDatabase.BeginWrite(connection);
        Execute(
            connection,
            transaction,
            "UPDATE repo_enrollment SET last_root = $root "
                + "WHERE identity = $identity AND (last_root IS NULL OR last_root != $root);",
            ("$root", canonicalRoot),
            ("$identity", identity));
        transaction.Commit();
    }

    /// <summary>
    /// Pure policy: whether a full scan is due. A row that does not exist is never due — this
    /// decides cadence for an already-enrolled repo, not whether one should be scanned at all.
    /// </summary>
    public static bool IsFullScanDue(RepoEnrollmentRow? row, int intervalMinutes, DateTimeOffset now)
    {
        if (row is null)
        {
            return false;
        }

        if (row.LastFullScanAt is not { } last)
        {
            return true;
        }

        return now.ToUnixTimeSeconds() - last >= intervalMinutes * 60L;
    }

    public static void StampFullScan(SqliteConnection connection, string identity, DateTimeOffset now)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        Execute(
            connection,
            transaction,
            "UPDATE repo_enrollment SET last_full_scan_at = $now WHERE identity = $identity;",
            ("$now", now.ToUnixTimeSeconds()),
            ("$identity", identity));
        transaction.Commit();
    }

    /// <summary>
    /// Pure policy for the primer's conditional line: emit for a never-asked repo, or a deferred
    /// one whose cooldown has elapsed. Never for <c>enrolled</c> or <c>declined</c>.
    /// </summary>
    public static bool ShouldOfferEnrollment(RepoEnrollmentRow? row, DateTimeOffset now)
    {
        if (row is null)
        {
            return true;
        }

        if (row.State != RepoEnrollmentState.Deferred)
        {
            return false;
        }

        return now.ToUnixTimeSeconds() - row.DecidedAt >= DeferralCooldown.TotalSeconds;
    }

    private static void Upsert(
        SqliteConnection connection,
        string identity,
        RepoEnrollmentState state,
        string root,
        DateTimeOffset now)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        Execute(
            connection,
            transaction,
            """
            INSERT INTO repo_enrollment (identity, state, source, last_root, decided_at, last_full_scan_at)
            VALUES ($identity, $state, 'user', $root, $now, NULL)
            ON CONFLICT (identity) DO UPDATE SET
              state = excluded.state,
              source = excluded.source,
              last_root = excluded.last_root,
              decided_at = excluded.decided_at;
            """,
            ("$identity", identity),
            ("$state", StateName(state)),
            ("$root", PathCanonicalizer.Canonical(root)),
            ("$now", now.ToUnixTimeSeconds()));
        transaction.Commit();
    }

    private static RepoEnrollmentRow? Query(
        SqliteConnection connection,
        string where,
        (string Name, object Value) parameter)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT identity, state, source, last_root, decided_at, last_full_scan_at "
                + $"FROM repo_enrollment WHERE {where};";
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private static RepoEnrollmentRow ReadRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            ParseState(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));

    private static RepoEnrollmentState ParseState(string state) => state switch
    {
        "enrolled" => RepoEnrollmentState.Enrolled,
        "declined" => RepoEnrollmentState.Declined,
        "deferred" => RepoEnrollmentState.Deferred,
        _ => throw new InvalidOperationException(
            $"repo_enrollment.state holds an unrecognized value '{state}'."),
    };

    private static string StateName(RepoEnrollmentState state) => state switch
    {
        RepoEnrollmentState.Enrolled => "enrolled",
        RepoEnrollmentState.Declined => "declined",
        RepoEnrollmentState.Deferred => "deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }
}
