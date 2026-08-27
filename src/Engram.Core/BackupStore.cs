using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// A fingerprint of authored truth, used to decide whether a snapshot would say anything new.
/// </summary>
/// <remarks>
/// <para>Counts rather than content, because this runs on a timer and has to be cheap. It moves
/// only when authored truth moved (<c>fact WHERE regenerable = 0</c>) plus the supersession
/// structure over those facts (backup-fingerprint-semantics.md §3) — a code fact written or
/// closed by <c>index --apply</c> does not move it, because that is derived state a re-index can
/// always reproduce.</para>
///
/// <para><c>MAX(id)</c> sits alongside the count, restricted to authored facts the same way, so
/// that an authored write and an authored close of equal size cannot cancel out.</para>
///
/// <para><c>entity</c>/<c>entity_alias</c> are deliberately absent: an entity is addressing
/// metadata (D2), not belief content, and one holding an authored fact is already counted through
/// the fact terms, which move when that fact is written. <c>edge</c>, <c>fact_relation</c> and
/// <c>fact_sync_request</c> stay unrestricted — none of the three moves during a code-only
/// <c>index --apply</c> (backup-fingerprint-semantics.md NE-2, measured empirically), so there is
/// no regenerable-driven noise to filter out of them.</para>
///
/// <para><b><c>ClosedFacts</c> is redundant today and is kept anyway, which is worth stating
/// rather than implying.</b> Both paths that set <c>valid_to</c> on an authored fact also insert a
/// supersession row — forgetting and superseding — so the restricted supersession count alone
/// already moves whenever an authored fact closes, and no test here can distinguish the two. It
/// stays because it costs one scan and does not depend on two tables agreeing. Do not write a test
/// claiming to guard it: there is no production path that would make such a test fail.</para>
/// </remarks>
public readonly record struct BackupFingerprint(
    long Facts,
    long MaxFactId,
    long ClosedFacts,
    long Supersessions,
    long Edges,
    long Relations,
    long SyncRequests)
{
    public bool IsEmpty => Facts == 0;

    public static BackupFingerprint Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT (SELECT COUNT(*) FROM fact WHERE regenerable = 0),
                   (SELECT COALESCE(MAX(id), 0) FROM fact WHERE regenerable = 0),
                   (SELECT COUNT(*) FROM fact WHERE valid_to IS NOT NULL AND regenerable = 0),
                   (SELECT COUNT(*) FROM supersession s
                      JOIN fact nf ON nf.id = s.new_fact_id
                     WHERE nf.regenerable = 0),
                   (SELECT COUNT(*) FROM edge),
                   (SELECT COUNT(*) FROM fact_relation),
                   (SELECT COUNT(*) FROM fact_sync_request);
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The fingerprint query returned no row.");
        }

        return new BackupFingerprint(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }
}

/// <summary>One snapshot on disk.</summary>
public sealed record BackupSnapshot(string Path, DateTimeOffset TakenAt, int SchemaVersion, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Why a snapshot is or is not due, in words a person can act on.</summary>
public sealed record BackupDecision(bool ShouldTake, string Reason);

/// <summary>What a prune would delete, and what it would keep.</summary>
public sealed record PrunePlan(IReadOnlyList<BackupSnapshot> Keep, IReadOnlyList<BackupSnapshot> Delete);

/// <summary>
/// Snapshots of the store: taking them, listing them, thinning them, and putting one back.
/// </summary>
public static class BackupStore
{
    /// <summary>
    /// Snapshots are named <c>engram-20260806T034500Z-v2.db</c>.
    /// </summary>
    /// <remarks>
    /// The schema version is in the name so that <c>restore</c> can refuse an incompatible
    /// snapshot without opening it, and so a directory listing answers "what can this binary
    /// actually read?" on its own. The timestamp is UTC and sorts lexically, which is what lets
    /// listing and thinning work off the name rather than off filesystem timestamps — those get
    /// rewritten by copies, archives, and restores, and would silently reorder history.
    /// </remarks>
    public const string Prefix = "engram-";

    public const string Extension = ".db";

    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Width of a rendered <see cref="TimestampFormat"/>: <c>20260806T034500Z</c>.</summary>
    private const int TimestampLength = 16;

    /// <summary>
    /// A snapshot being written. <c>VACUUM INTO</c> is not atomic, and a truncated file whose name
    /// says it is a snapshot is worse than no snapshot at all — it is the one you would reach for.
    /// </summary>
    private const string PartialSuffix = ".partial";

    public static string NameFor(DateTimeOffset takenAt, int schemaVersion) =>
        Prefix
            + takenAt.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)
            + "-v"
            + schemaVersion.ToString(CultureInfo.InvariantCulture)
            + Extension;

    /// <summary>
    /// Takes the exclusive right to snapshot, or returns null because someone else holds it.
    /// </summary>
    /// <remarks>
    /// <para>Needed because the trigger fans out. Session start spawns a backup child, and
    /// sessions start in bursts — a fleet of subagents, a script opening several at once. Thirty
    /// of them arriving together would run thirty <c>VACUUM</c>s over the same database to produce
    /// thirty files, twenty-nine of which the very next prune deletes. The first one through does
    /// the work and the rest exit immediately.</para>
    ///
    /// <para><c>DeleteOnClose</c> rather than a pid file with staleness rules: the kernel closes
    /// the handle however the process ends, including a kill, so there is no such thing as a lock
    /// left behind by a crash. The age check below exists only for a filesystem that does not
    /// honour that, and is deliberately generous — releasing a lock someone is still using is
    /// worse than waiting an hour.</para>
    /// </remarks>
    public static IDisposable? TryLock(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        Directory.CreateDirectory(home.BackupDir);
        var path = Path.Combine(home.BackupDir, ".lock");

        var acquired = Create(path);
        if (acquired is not null)
        {
            return acquired;
        }

        try
        {
            if (DateTimeOffset.UtcNow - new FileInfo(path).LastWriteTimeUtc > TimeSpan.FromHours(1))
            {
                File.Delete(path);
                return Create(path);
            }
        }
        catch (IOException)
        {
        }

        return null;

        static FileStream? Create(string path)
        {
            try
            {
                return new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Every snapshot in the home, newest first.</summary>
    public static IReadOnlyList<BackupSnapshot> List(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        if (!Directory.Exists(home.BackupDir))
        {
            return [];
        }

        var snapshots = new List<BackupSnapshot>();
        foreach (var path in Directory.EnumerateFiles(home.BackupDir, Prefix + "*" + Extension))
        {
            if (TryParse(path, out var snapshot))
            {
                snapshots.Add(snapshot);
            }
        }

        snapshots.Sort((left, right) => right.TakenAt.CompareTo(left.TakenAt));
        return snapshots;
    }

    public static bool TryParse(string path, out BackupSnapshot snapshot)
    {
        snapshot = null!;

        var name = System.IO.Path.GetFileName(path);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)
            || !name.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        // Read positionally from the front rather than searching for the "-v" from the back. The
        // timestamp is fixed width, and everything after the version is a free-text label — a
        // label of "pre-v2" or a "-2" collision suffix both contain the delimiter being searched
        // for, and a parser that scans for it drops exactly the snapshots taken at the moments
        // most worth keeping.
        var middle = name[Prefix.Length..^Extension.Length];
        if (middle.Length < TimestampLength + 2)
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                middle[..TimestampLength],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var takenAt))
        {
            return false;
        }

        var rest = middle[TimestampLength..];
        if (!rest.StartsWith("-v", StringComparison.Ordinal))
        {
            return false;
        }

        var digits = rest[2..];
        var labelStart = digits.IndexOf('-', StringComparison.Ordinal);
        var versionText = labelStart < 0 ? digits : digits[..labelStart];
        if (!int.TryParse(versionText, CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        var bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        snapshot = new BackupSnapshot(path, takenAt, version, bytes);
        return true;
    }

    /// <summary>
    /// Whether a snapshot is due: the interval has elapsed <i>and</i> authored truth has moved.
    /// </summary>
    /// <remarks>
    /// Both conditions, because either alone gets it wrong. Time alone copies identical bytes
    /// around the clock and then thins all but one back out — work performed solely to undo
    /// itself. Change alone turns a busy afternoon into a snapshot per fact.
    /// <para>
    /// The comparison is against the newest snapshot's own fingerprint, read back out of it, and
    /// not against a watermark kept on the side. A watermark is a second copy of the truth that
    /// can disagree with the files it describes — delete a snapshot by hand and it starts lying.
    /// Reading the snapshot costs one database open in a process that is about to do far more
    /// than that, and it cannot drift, because the file it describes is the file it lives in.
    /// </para>
    /// </remarks>
    public static BackupDecision Due(
        EngramHome home,
        SqliteConnection connection,
        BackupSettings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
        {
            return new BackupDecision(false, "backups are disabled in config");
        }

        var live = BackupFingerprint.Read(connection);
        if (live.IsEmpty)
        {
            return new BackupDecision(false, "the store holds nothing worth keeping yet");
        }

        var newest = List(home).FirstOrDefault();
        if (newest is null)
        {
            return new BackupDecision(true, "no snapshot exists");
        }

        var age = now - newest.TakenAt;
        if (age < TimeSpan.FromMinutes(settings.IntervalMinutes))
        {
            return new BackupDecision(
                false,
                $"the newest snapshot is {Describe(age)} old, inside the {settings.IntervalMinutes}-minute interval");
        }

        if (FingerprintOf(newest, home) == live)
        {
            return new BackupDecision(false, "nothing has changed since the newest snapshot");
        }

        return new BackupDecision(true, $"the newest snapshot is {Describe(age)} old and the store has changed");
    }

    /// <summary>
    /// Copies the store to a new snapshot and returns it.
    /// </summary>
    /// <remarks>
    /// <c>VACUUM INTO</c> rather than a file copy, and this is not a preference. The store runs in
    /// WAL mode, so at any moment committed data lives partly in <c>engram.db</c> and partly in
    /// <c>engram.db-wal</c>; copying the first without the second yields a file that opens
    /// cleanly and is missing recent facts. <c>VACUUM INTO</c> reads through one transaction and
    /// writes a single consistent, compacted database — and takes no write lock, so a snapshot
    /// never blocks a hook trying to record a fact.
    /// </remarks>
    public static BackupSnapshot Take(
        SqliteConnection connection,
        EngramHome home,
        DateTimeOffset now,
        string? label = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);

        Directory.CreateDirectory(home.BackupDir);

        var version = EngramDatabase.ReadSchemaVersion(connection);
        var final = Path.Combine(home.BackupDir, Unique(home.BackupDir, now, version, label));
        var partial = final + PartialSuffix;

        // VACUUM INTO refuses to overwrite, so a partial left by a killed process would block
        // every future snapshot. Clearing it is safe precisely because the name says it was never
        // finished.
        File.Delete(partial);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", partial);
            command.ExecuteNonQuery();

            File.Move(partial, final);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return new BackupSnapshot(final, now.ToUniversalTime(), version, new FileInfo(final).Length);
    }

    /// <summary>
    /// Which snapshots survive: the newest in each of the last N hours, days, and weeks.
    /// </summary>
    /// <remarks>
    /// <para>Generational rather than "keep the last N", because the failure modes are. Losing an
    /// hour of facts is noticed at once; losing a fortnight is noticed long after a flat window of
    /// hourly snapshots has rolled past. Bucketing spends a bounded number of files on a reach
    /// measured in months.</para>
    ///
    /// <para>The newest snapshot is kept unconditionally, whatever the buckets say. It is the one
    /// a restore actually wants, and a retention rule that can delete it has misunderstood its
    /// job. Reading config, that line is unreachable — <see cref="BackupSettings.Read"/> floors
    /// every count at one, so the newest snapshot is always the first entry in its own hourly
    /// bucket and survives anyway. It is reachable from code, which constructs settings directly
    /// and is under no such floor, and that is the case the test for it exercises.</para>
    /// </remarks>
    public static PrunePlan Plan(IReadOnlyList<BackupSnapshot> snapshots, BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(settings);

        var ordered = snapshots.OrderByDescending(s => s.TakenAt).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);

        if (ordered.Count > 0)
        {
            keep.Add(ordered[0].Path);
        }

        Fill(ordered, keep, settings.KeepHourly, s => s.TakenAt.ToUniversalTime().ToString("yyyyMMddHH", CultureInfo.InvariantCulture));
        Fill(ordered, keep, settings.KeepDaily, s => s.TakenAt.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Fill(ordered, keep, settings.KeepWeekly, WeekOf);

        return new PrunePlan(
            ordered.Where(s => keep.Contains(s.Path)).ToList(),
            ordered.Where(s => !keep.Contains(s.Path)).ToList());
    }

    /// <summary>Deletes what <see cref="Plan"/> chose. The caller decides whether to call it.</summary>
    public static void Prune(PrunePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var snapshot in plan.Delete)
        {
            File.Delete(snapshot.Path);
        }
    }

    /// <summary>
    /// Puts a snapshot back in place of the live store.
    /// </summary>
    /// <remarks>
    /// <para>Three things have to be true and none of them are the caller's to remember. A
    /// snapshot from a newer schema version is refused outright, because this binary cannot know
    /// what changed and a store it half-understands is worse than one it cannot open. The current
    /// store is snapshotted first — restoring is the one operation that destroys the copy you
    /// might have wanted, and discovering that afterwards is discovering it too late. And the
    /// stale <c>-wal</c> and <c>-shm</c> beside the old database are removed, because a WAL from
    /// one database applied to another is corruption with a clean exit code.</para>
    ///
    /// <para>Every connection must be closed first. This does not verify that, because it cannot:
    /// another process may hold one, and a check that passes for this process while another writes
    /// would be a guarantee that is not one.</para>
    /// </remarks>
    public static void Restore(EngramHome home, BackupSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion > EngramDatabase.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"'{snapshot.Name}' was written at schema version {snapshot.SchemaVersion}, and this "
                    + $"binary understands {EngramDatabase.SchemaVersion}. Refusing to restore it rather "
                    + "than reading it wrongly.");
        }

        if (File.Exists(home.DatabasePath))
        {
            PreserveCurrent(home, now);
        }

        var staged = home.DatabasePath + PartialSuffix;
        File.Copy(snapshot.Path, staged, overwrite: true);
        File.Move(staged, home.DatabasePath, overwrite: true);

        File.Delete(home.DatabasePath + "-wal");
        File.Delete(home.DatabasePath + "-shm");
    }

    /// <summary>
    /// Keeps whatever is currently at the database path before something else takes that path.
    /// </summary>
    /// <remarks>
    /// The fallback is the point. A store worth restoring over is disproportionately likely to be
    /// one that will not open — that is often why someone is restoring — and a preservation step
    /// that only works on healthy databases is missing at exactly the moment it is needed. So a
    /// failed snapshot degrades to moving the bytes aside under a name that deliberately does not
    /// parse as a snapshot: it is kept, and it is never offered as something to restore from.
    /// </remarks>
    private static void PreserveCurrent(EngramHome home, DateTimeOffset now)
    {
        Directory.CreateDirectory(home.BackupDir);
        SqliteConnection.ClearAllPools();

        try
        {
            using var connection = EngramDatabase.Open(home);
            Take(connection, home, now, "pre-restore");
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            var stamp = now.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
            File.Copy(home.DatabasePath, Path.Combine(home.BackupDir, $"unreadable-{stamp}{Extension}"), overwrite: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void Fill(
        List<BackupSnapshot> ordered,
        HashSet<string> keep,
        int limit,
        Func<BackupSnapshot, string> bucketOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in ordered)
        {
            if (seen.Count >= limit)
            {
                break;
            }

            if (seen.Add(bucketOf(snapshot)))
            {
                keep.Add(snapshot.Path);
            }
        }
    }

    private static string WeekOf(BackupSnapshot snapshot)
    {
        var date = snapshot.TakenAt.ToUniversalTime().Date;
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return monday.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /// <remarks>
    /// Two snapshots in the same second collide on the name, which happens whenever something
    /// takes one either side of a fast operation — a restore, or a migration of an empty store.
    /// Suffixing loses no information the name carried, where overwriting would lose a snapshot.
    /// </remarks>
    private static string Unique(string directory, DateTimeOffset now, int version, string? label)
    {
        var stem = NameFor(now, version);
        if (label is { Length: > 0 })
        {
            stem = stem[..^Extension.Length] + "-" + label + Extension;
        }

        var candidate = stem;
        for (var attempt = 2; File.Exists(Path.Combine(directory, candidate)); attempt++)
        {
            candidate = stem[..^Extension.Length] + "-" + attempt.ToString(CultureInfo.InvariantCulture) + Extension;
        }

        return candidate;
    }

    private static BackupFingerprint FingerprintOf(BackupSnapshot snapshot, EngramHome home)
    {
        try
        {
            using var connection = EngramDatabase.Open(snapshot.Path, home.LibDir);
            return BackupFingerprint.Read(connection);
        }
        catch (SqliteException)
        {
            // An unreadable snapshot is not a reason to skip taking a good one. Returning a
            // fingerprint that cannot match any live store says exactly that.
            return new BackupFingerprint(-1, -1, -1, -1, -1, -1, -1);
        }
    }

    private static string Describe(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "under a minute",
        < 60 => $"{(int)age.TotalMinutes} minutes",
        < 60 * 24 => $"{(int)age.TotalHours} hours",
        _ => $"{(int)age.TotalDays} days",
    };
}
