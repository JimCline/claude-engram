using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <param name="Budget">
/// What the directory walk may spend before it reports a partial list. Null takes
/// <see cref="ScanBudget.Default"/>. Per run rather than per install, because it is the seam a test
/// needs to reach the truncated path without building a tree large enough to exhaust the machine.
/// </param>
/// <param name="Queue">
/// A queue snapshot to drain against, in place of a fresh <see cref="SpoolQueue.Peek"/>. Null takes
/// the default fresh peek — every caller before <c>--drain-all</c> passes null and sees no change.
/// Set by a multi-root pass that must peek the spool directory exactly once and drain every root
/// against the same captured list (§6.3e), rather than re-reading the whole directory per root.
/// </param>
/// <param name="EnrolledIdentity">
/// The identity a caller already resolved this repo's <c>repo_enrollment</c> row under —
/// <see cref="RepoEnrollment.StampFullScan"/> and the suppression-reason clear must target this
/// identity, not the one <see cref="CodeIndexer.ResolveIdentity"/> recomputes from <c>Root</c> here,
/// because a caller enrolling under a precomputed identity is not guaranteed to get the same string
/// back from a fresh git remote lookup and a mismatch makes that write silently match no row. Null
/// for callers with no enrollment row to stamp (a bare <c>engram index --apply</c>), which fall back
/// to the recomputed identity as before.
/// </param>
public sealed record IndexOptions(
    string Root,
    bool Apply,
    bool Drain,
    bool Full,
    string? SidecarPath = null,
    ScanBudget? Budget = null,
    bool AllowFullScanDue = true,
    SpoolQueue? Queue = null,
    string? EnrolledIdentity = null);

public sealed record IndexReport(
    string RepoPath,
    string Root,
    bool Applied,
    bool FullScan,
    bool VersionForcedFull,
    int FilesConsidered,
    int Analyzed,
    int Unchanged,
    int Deleted,
    int Renamed,
    int FactsWritten,
    int FactsClosed,
    int FactsUnchanged,
    int ProtectedSkipped,
    int QueueConsumed,
    int QueueLeft,
    IReadOnlyList<string> Notes);

/// <summary>
/// The M3 pipeline: turns files into code facts, incrementally. Consumes the
/// <c>file-touched</c> queue when asked, falls back to a full scan whenever the queue
/// cannot say what changed, and diffs everything against <c>file_state</c> so an
/// unchanged file costs one dictionary lookup and no write.
/// </summary>
/// <remarks>
/// <para>Every fact written here is <c>observed</c> and <c>regenerable</c> (D19/D23) —
/// which is also the boundary of what this class may close. A non-regenerable fact at a
/// code path is an agent's or the user's belief, and the indexer neither supersedes nor
/// closes it: tier-0 extraction does not outrank testimony.</para>
///
/// <para>It reads the spool through <see cref="SpoolQueue"/>, never a drain-style consumer
/// that deletes before the caller has acted or takes every repo's entries at once. Entries
/// here are removed only after the work they describe has committed; entries for other
/// repos stay queued (the queue is folded, never pruned — D41). A parsed entry with no
/// path means "something changed, cannot say what", and escalates the run to a full
/// scan.</para>
/// </remarks>
public static class CodeIndexer
{
    public const string VersionKey = "code_index_version";

    public static string CurrentVersion => $"{CodePaths.GrammarVersion}.{CodeAnalyzer.AnalyzerVersion}";

    public static IndexReport Index(
        SqliteConnection connection,
        EngramHome home,
        ConfigFile config,
        IndexingSettings settings,
        IndexOptions options,
        DateTimeOffset now)
    {
        var notes = new List<string>();
        var root = ResolveRoot(options.Root);
        var identity = ResolveIdentity(root);

        // Claimed before the last_root repair below writes anything, so the whole
        // read-modify-write cycle for this identity — the file_state snapshot, the scan, and the
        // write pass — runs inside one critical section per repo (§6.4). Never claimed for a dry
        // run: it writes nothing, so it must neither block nor be blocked. `using` releases the
        // lock at every exit from this method, the same guarantee a `finally` would give, without
        // wrapping the rest of the method in one.
        IndexLock? claimedLock = null;
        if (options.Apply)
        {
            var (held, blockedBy) = IndexLock.TryClaim(home, identity, now);
            if (held is null)
            {
                return ContentionReport(root, blockedBy);
            }

            claimedLock = held;
        }

        using var indexLock = claimedLock;

        // A repo enrolled once and later moved on disk still resolves to the same identity
        // from its new location; without this repair the stale last_root sticks, the
        // session-start hook's cache-only lookup keeps missing it, and the primer offers
        // enrollment again for a checkout the user already answered about. Runs ahead of
        // anything that decides the repo is unenrolled (§6.3c, §6.4, guard 12).
        var enrollmentRow = RepoEnrollment.Get(connection, identity);
        if (options.Apply && enrollmentRow is not null)
        {
            RepoEnrollment.UpdateLastRoot(connection, identity, root);
        }

        var repoPath = EnsureRepo(connection, config, root, identity, options.Apply, now, notes);

        var storedVersion = ReadMeta(connection, VersionKey);
        var versionForcedFull = storedVersion is not null && storedVersion != CurrentVersion;
        if (versionForcedFull)
        {
            notes.Add($"grammar/analyzer moved {storedVersion} -> {CurrentVersion}; re-reading everything");
        }

        var fullScanDue = options.AllowFullScanDue
            && RepoEnrollment.IsFullScanDue(enrollmentRow, IndexingSettings.FullScanIntervalMinutes, now);
        if (fullScanDue)
        {
            notes.Add($"full scan due: more than {IndexingSettings.FullScanIntervalMinutes} minutes since the last one");
        }

        var full = options.Full || versionForcedFull || !options.Drain || fullScanDue;
        var states = LoadStates(connection, repoPath);
        var filter = new IndexFilter(settings);

        var queue = options.Drain ? options.Queue ?? SpoolQueue.Peek(home.QueueDir) : SpoolQueue.Empty;
        if (options.Drain && queue.Pathless > 0)
        {
            full = true;
            notes.Add($"{queue.Pathless} queue entr{(queue.Pathless == 1 ? "y" : "ies")} could not say which file; scanning everything");
        }

        List<string> targets;
        List<string> deletions;

        // Set inside the full-scan branch below and read after the fact-writing pass commits,
        // so a failure between "the scan completed" and "the writes landed" cannot stamp
        // last_full_scan_at for work that never happened — that would read as freshly scanned
        // for a full FullScanIntervalMinutes and silently downgrade the repo to incremental.
        var stampFullScan = false;

        // Same reasoning as stampFullScan: null unless a completed, applied run actually skipped
        // deletions, so a dry run reports what it would do without moving repo_registry state.
        string? suppressedReason = null;

        if (full)
        {
            var scan = RepoScanner.Scan(root, settings, budget: options.Budget ?? ScanBudget.Default);
            var onDisk = new HashSet<string>(scan.Files, StringComparer.Ordinal);
            targets = [.. scan.Files];

            // Absence is only evidence when the scan was allowed to finish. Every path past a
            // budget cut is missing from `onDisk` for a reason that has nothing to do with the
            // disk, so deleting on that evidence would retract the code facts for most of the
            // repository — the bound would turn a slow scan into a destructive one.
            if (scan.Truncated)
            {
                deletions = [];
                notes.Add($"{scan.Summary()}; skipped deletions, because a partial scan cannot show a file is gone");
                suppressedReason = options.Apply ? "truncated" : null;
            }
            else if (onDisk.Count == 0 && states.Count > 0)
            {
                // A completed scan that found nothing against a repo that was previously indexed is
                // more likely an unmounted volume or an excluded-everything filter change than a
                // truly emptied repository — either way, closing every fact at once is the wrong
                // default, and the scan not having failed means D53's Truncated guard never arms.
                deletions = [];
                notes.Add($"scan found 0 files against {states.Count} already indexed; skipped deletions rather than closing every fact in the repo");
                suppressedReason = options.Apply ? "empty-scan" : null;
            }
            else
            {
                deletions = states.Keys.Where(rel => !onDisk.Contains(rel)).ToList();

                // Only a completed scan has actually seen the repo, and only an applied run
                // may move state (§6.3b) — a dry run must report what it would do, not do it.
                stampFullScan = options.Apply;
            }
        }
        else
        {
            targets = [];
            deletions = [];
            foreach (var rel in queue.Under(root))
            {
                var fullPath = Path.Combine(root, rel);
                if (File.Exists(fullPath))
                {
                    if (filter.Inspect(rel, fullPath).Include)
                    {
                        targets.Add(rel);
                    }
                }
                else if (states.ContainsKey(rel))
                {
                    deletions.Add(rel);
                }
            }
        }

        var blobs = GitBlobShas(root);
        var shas = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<string>();
        var unchangedFiles = 0;

        foreach (var rel in targets.Distinct(StringComparer.Ordinal))
        {
            var sha = ShaOf(root, rel, blobs);
            if (sha is null)
            {
                continue;
            }

            shas[rel] = sha;
            if (states.TryGetValue(rel, out var known) && known == sha && !versionForcedFull && !options.Full)
            {
                unchangedFiles++;
            }
            else
            {
                changed.Add(rel);
            }
        }

        var renames = PairRenames(states, changed, deletions, shas);
        var counters = new Counters();

        foreach (var (oldRel, newRel) in renames)
        {
            if (ApplyRename(connection, repoPath, oldRel, newRel, options.Apply, now, notes))
            {
                deletions.Remove(oldRel);
            }
        }

        var deep = DeepAnalyses(root, changed, options.SidecarPath, notes);
        Tier1Analyses(root, changed, home, notes, deep);

        foreach (var rel in changed)
        {
            deep.TryGetValue(rel, out var analysis);
            ProcessFile(connection, repoPath, root, rel, shas[rel], options.Apply, now, counters, notes, analysis);
        }

        foreach (var rel in deletions)
        {
            ProcessDeletion(connection, repoPath, rel, options.Apply, now, counters);
        }

        // After the writes, not before: see the comment on stampFullScan above.
        if (stampFullScan)
        {
            // One transaction, not two commits: a crash between them would leave
            // last_full_scan_at fresh with the suppression reason still set, which reads as an
            // unresolved condition forever (D4, D33's retired-key shape).
            var stampIdentity = options.EnrolledIdentity ?? identity;
            using var transaction = EngramDatabase.BeginWrite(connection);
            RepoEnrollment.StampFullScan(connection, transaction, stampIdentity, now);
            ClearSuppressedReason(connection, transaction, stampIdentity);
            transaction.Commit();
        }
        else if (suppressedReason is not null)
        {
            SetSuppressedReason(connection, identity, suppressedReason);
        }

        if (options.Apply && storedVersion != CurrentVersion)
        {
            WriteMeta(connection, VersionKey, CurrentVersion);
        }

        var consumed = options.Apply ? queue.Consume(root, consumePathless: full) : 0;

        return new IndexReport(
            RepoPath: repoPath,
            Root: root,
            Applied: options.Apply,
            FullScan: full,
            VersionForcedFull: versionForcedFull,
            FilesConsidered: targets.Count,
            Analyzed: changed.Count,
            Unchanged: unchangedFiles,
            Deleted: deletions.Count,
            Renamed: renames.Count,
            FactsWritten: counters.Written,
            FactsClosed: counters.Closed,
            FactsUnchanged: counters.Unchanged,
            ProtectedSkipped: counters.Protected,
            QueueConsumed: consumed,
            QueueLeft: queue.LeftBehind(root),
            Notes: notes);
    }

    /// <summary>
    /// What a blocked <c>--apply</c> caller gets when <see cref="IndexLock.TryClaim"/> loses to a
    /// live holder (§6.4): zero counts, and a note every caller checks for by the same prefix,
    /// regardless of whether it has to report it (commanded) or stay silent (ambient).
    /// </summary>
    private static IndexReport ContentionReport(string root, IndexLockRecord? blockedBy) =>
        new(
            RepoPath: root,
            Root: root,
            Applied: false,
            FullScan: false,
            VersionForcedFull: false,
            FilesConsidered: 0,
            Analyzed: 0,
            Unchanged: 0,
            Deleted: 0,
            Renamed: 0,
            FactsWritten: 0,
            FactsClosed: 0,
            FactsUnchanged: 0,
            ProtectedSkipped: 0,
            QueueConsumed: 0,
            QueueLeft: 0,
            Notes: [blockedBy is not null
                ? $"skipped: another process is indexing this repo (pid {blockedBy.Pid}, since {blockedBy.StartedAt:O})"
                : "skipped: another process is indexing this repo"]);

    /// <summary>
    /// Batch pre-pass for files whose language declares tier 2 (D24): one sidecar process
    /// for the whole run, results keyed by relative path. Anything that stops it — no
    /// binary beside the executable, no runtime, a hang — leaves the batch at tier 0,
    /// because the deep tier is an upgrade, never a requirement.
    /// </summary>
    private static Dictionary<string, DeepAnalysis> DeepAnalyses(
        string root,
        List<string> changed,
        string? sidecarPath,
        List<string> notes)
    {
        var deep = changed.Where(rel => LanguageRegistry.Resolve(rel).Tier == 2).ToList();
        if (deep.Count == 0)
        {
            return [];
        }

        var sidecar = sidecarPath ?? RoslynSidecar.Locate(Environment.GetEnvironmentVariable);
        if (sidecar is null)
        {
            return [];
        }

        var inputs = new List<(string RelativePath, string Content)>();
        foreach (var rel in deep)
        {
            try
            {
                inputs.Add((rel, File.ReadAllText(Path.Combine(root, rel))));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // ProcessFile reports the unreadable file; the sidecar just never sees it.
            }
        }

        // Ten seconds to start plus 50 ms a file: parsing costs milliseconds, and the
        // bound exists to kill a hung process, not to race a slow one.
        var results = RoslynSidecar.Analyze(
            sidecar, inputs, TimeSpan.FromMilliseconds(10_000 + (50 * inputs.Count)));

        if (results is null)
        {
            notes.Add($"deep analyzer did not answer; {deep.Count} file(s) took tier 0");
            return [];
        }

        notes.Add($"tier 2: deep analyzer covered {results.Count} of {deep.Count} file(s)");
        return results;
    }

    /// <summary>
    /// The tier-1 pass (D24/D47): files whose language declares tier 1 go through the
    /// tree-sitter grammars in the home's lib directory, into the same dictionary the
    /// sidecar fills — a file is tier 1 or tier 2 by its language, never both, so the two
    /// passes cannot collide on a key. Absence is quiet, exactly like an absent sidecar:
    /// doctor is where "which tier do I have" gets answered, and an index note repeated
    /// every run is a note nobody reads. Anything short of absence — a grammar that will
    /// not load, a refused query — surfaces once per cause.
    /// </summary>
    private static void Tier1Analyses(
        string root,
        List<string> changed,
        EngramHome home,
        List<string> notes,
        Dictionary<string, DeepAnalysis> results)
    {
        var syntactic = changed.Where(rel => LanguageRegistry.Resolve(rel).Tier == 1).ToList();
        if (syntactic.Count == 0)
        {
            return;
        }

        var directory = TreeSitter.Locate(Environment.GetEnvironmentVariable, home);
        if (directory is null)
        {
            return;
        }

        using var runtime = TreeSitter.TryCreate(directory, notes);
        if (runtime is null)
        {
            return;
        }

        var covered = 0;
        foreach (var rel in syntactic)
        {
            string content;
            try
            {
                content = File.ReadAllText(Path.Combine(root, rel));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // ProcessFile reports the unreadable file; tier 1 just never sees it.
                continue;
            }

            if (runtime.Analyze(LanguageRegistry.Resolve(rel), rel, content) is { } analysis)
            {
                results[rel] = analysis;
                covered++;
            }
        }

        notes.AddRange(runtime.Downgrades);
        if (covered > 0)
        {
            notes.Add($"tier 1: tree-sitter covered {covered} of {syntactic.Count} file(s)");
        }
    }

    private sealed class Counters
    {
        public int Written;
        public int Closed;
        public int Unchanged;
        public int Protected;
    }

    private static void ProcessFile(
        SqliteConnection connection,
        string repoPath,
        string root,
        string rel,
        string sha,
        bool apply,
        DateTimeOffset now,
        Counters counters,
        List<string> notes,
        DeepAnalysis? deep)
    {
        string content;
        try
        {
            content = File.ReadAllText(Path.Combine(root, rel));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{rel}: unreadable ({e.GetType().Name}), skipped");
            return;
        }

        var language = LanguageRegistry.Resolve(rel);
        var filePath = CodePaths.ForFile(repoPath, rel);
        var candidates = CodeAnalyzer.Analyze(filePath, content, language);
        if (deep is not null)
        {
            candidates = DeepTier.Merge(filePath, candidates, deep);
        }

        var live = ReadLiveUnder(connection, filePath);

        var evidence = $"{rel} @ {sha[..Math.Min(8, sha.Length)]}";
        var closeReason = language.DocHeadings
            ? $"document changed ({sha[..Math.Min(8, sha.Length)]})"
            : $"source changed ({sha[..Math.Min(8, sha.Length)]})";

        var matched = new HashSet<(string, string, string?)>();
        var writes = new List<CodeCandidate>();

        foreach (var candidate in candidates)
        {
            // Keyed on the object's address, not its raw name: live's third component comes
            // back as entity path, and candidate.Object is the name as written — they must
            // agree on which form they compare in.
            var objectPath = candidate.Object is null ? null : CodePaths.ForSymbolName(candidate.Object);
            var key = (candidate.EntityPath, candidate.Predicate, objectPath);
            matched.Add(key);

            if (live.TryGetValue(key, out var existing))
            {
                if (!existing.Regenerable)
                {
                    // An agent's or the user's belief about this subject. Tier-0
                    // extraction does not supersede testimony (D19).
                    counters.Protected++;
                    continue;
                }

                if (existing.Body == candidate.Body)
                {
                    counters.Unchanged++;
                    continue;
                }
            }

            writes.Add(candidate);
        }

        var closes = live.Values
            .Where(fact => fact.Regenerable && !matched.Contains((fact.Path, fact.Predicate, fact.Object)))
            .ToList();

        counters.Written += writes.Count;
        counters.Closed += closes.Count;

        if (!apply)
        {
            return;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);
        var timestamp = now.ToUnixTimeSeconds();

        foreach (var write in writes)
        {
            // EnsureEntity first, because Remember cannot carry a display name and a
            // section's heading is not recoverable from its slug.
            FactStore.EnsureEntity(connection, transaction, write.EntityPath, write.Kind, timestamp, write.DisplayName);

            string? objectPath = null;
            if (write.Object is not null)
            {
                objectPath = CodePaths.ForSymbolName(write.Object);
                FactStore.EnsureEntity(connection, transaction, objectPath, "symbol-name", timestamp, write.Object);
            }

            FactStore.Remember(
                connection,
                transaction,
                new FactWrite(
                    SubjectPath: write.EntityPath,
                    SubjectKind: write.Kind,
                    Predicate: write.Predicate,
                    Body: write.Body,
                    Scope: "code",
                    LearnedVia: "observed",
                    Evidence: evidence,
                    Regenerable: true,
                    ObjectPath: objectPath,
                    ObjectKind: objectPath is null ? null : "symbol-name"),
                now,
                closeReason);
        }

        foreach (var stale in closes)
        {
            FactStore.Forget(connection, transaction, stale.Id, closeReason, now);
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO file_state (repo_path, path, blob_sha, lang, indexed_at)
            VALUES ($repo, $path, $sha, $lang, $now)
            ON CONFLICT (repo_path, path) DO UPDATE SET blob_sha = $sha, lang = $lang, indexed_at = $now;
            """,
            ("$repo", repoPath),
            ("$path", rel),
            ("$sha", sha),
            ("$lang", language.Id),
            ("$now", timestamp));

        transaction.Commit();
    }

    private static void ProcessDeletion(
        SqliteConnection connection,
        string repoPath,
        string rel,
        bool apply,
        DateTimeOffset now,
        Counters counters)
    {
        var filePath = CodePaths.ForFile(repoPath, rel);
        var live = ReadLiveUnder(connection, filePath);
        var stale = live.Values.Where(fact => fact.Regenerable).ToList();
        counters.Closed += stale.Count;

        if (!apply)
        {
            return;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);

        foreach (var fact in stale)
        {
            FactStore.Forget(connection, transaction, fact.Id, "file removed", now);
        }

        Execute(
            connection,
            transaction,
            "DELETE FROM file_state WHERE repo_path = $repo AND path = $path;",
            ("$repo", repoPath),
            ("$path", rel));

        transaction.Commit();
    }

    /// <summary>
    /// Moves a renamed file's subtree so entities keep their ids (D2). Returns false when
    /// the move could not happen — the old path then still needs closing as a deletion.
    /// </summary>
    private static bool ApplyRename(
        SqliteConnection connection,
        string repoPath,
        string oldRel,
        string newRel,
        bool apply,
        DateTimeOffset now,
        List<string> notes)
    {
        var oldPath = CodePaths.ForFile(repoPath, oldRel);
        var newPath = CodePaths.ForFile(repoPath, newRel);

        // A historical entity may already hold the target address — a file deleted last
        // year and reborn under this name. Moving onto it would collide on entity.path;
        // adopting it is an identity merge this tier has no basis to make (D2), so the
        // rename falls back to close-and-rewrite and the deep tier can adopt later.
        var occupied = Scalar(
            connection,
            null,
            $"SELECT COUNT(*) FROM entity WHERE {SubtreePredicate}",
            ("$len", newPath.Length),
            ("$old", newPath));

        if (occupied > 0)
        {
            notes.Add($"{oldRel} -> {newRel}: target address already has history; closing and rewriting instead");
            return false;
        }

        notes.Add($"{oldRel} -> {newRel} (same content; entities keep their ids)");

        if (!apply)
        {
            return true;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);

        FactStore.MoveSubtree(connection, transaction, oldPath, newPath, now);

        Execute(
            connection,
            transaction,
            "DELETE FROM file_state WHERE repo_path = $repo AND path = $path;",
            ("$repo", repoPath),
            ("$path", oldRel));

        transaction.Commit();
        return true;
    }

    private static List<(string OldRel, string NewRel)> PairRenames(
        Dictionary<string, string> states,
        List<string> changed,
        List<string> deletions,
        Dictionary<string, string> shas)
    {
        var added = changed
            .Where(rel => !states.ContainsKey(rel))
            .GroupBy(rel => shas[rel], StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        var pairs = new List<(string, string)>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var oldRel in deletions)
        {
            if (states.TryGetValue(oldRel, out var sha)
                && added.TryGetValue(sha, out var newRel)
                && claimed.Add(sha))
            {
                pairs.Add((oldRel, newRel));
            }
        }

        return pairs;
    }

    private const string SubtreePredicate =
        "substr(path, 1, $len) = $old AND (length(path) = $len OR substr(path, $len + 1, 1) IN ('/', '#'))";

    private sealed record LiveFact(long Id, string Path, string Predicate, string? Object, string Body, bool Regenerable);

    private static Dictionary<(string, string, string?), LiveFact> ReadLiveUnder(SqliteConnection connection, string filePath)
    {
        var facts = new Dictionary<(string, string, string?), LiveFact>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT fact.id, fact.path, fact.predicate, o.path, fact.body, fact.regenerable
            FROM fact
            LEFT JOIN entity o ON o.id = fact.object_id
            WHERE fact.valid_to IS NULL
              AND (fact.path = $p OR (substr(fact.path, 1, $len) = $p AND substr(fact.path, $len + 1, 1) = '#'));
            """;
        command.Parameters.AddWithValue("$p", filePath);
        command.Parameters.AddWithValue("$len", filePath.Length);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var fact = new LiveFact(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) != 0);
            facts[(fact.Path, fact.Predicate, fact.Object)] = fact;
        }

        return facts;
    }

    /// <summary>Whether the directory sits inside a git checkout at all.</summary>
    /// <remarks>
    /// The auto-index gate: a maintenance child launched from an arbitrary shell must
    /// never treat that shell's directory as a repository.
    /// </remarks>
    public static bool IsGitCheckout(string root) =>
        !string.IsNullOrEmpty(GitFileLister.Run(Path.GetFullPath(root), "rev-parse", "--show-toplevel")?.Trim());

    public static string ResolveRoot(string requested)
    {
        var full = Path.GetFullPath(requested);
        var toplevel = GitFileLister.Run(full, "rev-parse", "--show-toplevel")?.Trim();
        return string.IsNullOrEmpty(toplevel) ? full : Path.GetFullPath(toplevel);
    }

    /// <summary>
    /// The durable answer to "is this the same repository": the origin URL when there is
    /// one — a checkout can move directories without becoming a different repo — else the
    /// root path.
    /// </summary>
    /// <remarks>
    /// A caller enrolling a row under a precomputed identity must pass a <paramref name="root"/>
    /// already run through <see cref="PathCanonicalizer.Canonical"/>, matching what
    /// <see cref="RepoEnrollment.Enroll"/> stores as <c>LastRoot</c> — <see cref="Index"/> resolves
    /// its own identity independently from that stored root, ignoring whatever identity the caller
    /// enrolled under, so a test that skips canonicalizing first enrolls under an identity a real
    /// tick never reaches and <c>StampFullScan</c> silently no-ops (hit while writing tests for
    /// commits D, E and F).
    /// </remarks>
    public static string ResolveIdentity(string root)
    {
        var remote = GitFileLister.Run(root, "remote", "get-url", "origin")?.Trim();
        if (string.IsNullOrEmpty(remote))
        {
            return root;
        }

        var normalized = remote.TrimEnd('/');
        return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static string EnsureRepo(
        SqliteConnection connection,
        ConfigFile config,
        string root,
        string identity,
        bool apply,
        DateTimeOffset now,
        List<string> notes)
    {
        var existing = ScalarText(
            connection,
            "SELECT repo_path, disk_path FROM repo_registry WHERE identity = $identity;",
            ("$identity", identity));

        if (existing is { } registered)
        {
            if (apply && registered.Second != root)
            {
                Execute(
                    connection,
                    null,
                    "UPDATE repo_registry SET disk_path = $disk, detached_at = NULL WHERE identity = $identity;",
                    ("$disk", root),
                    ("$identity", identity));
            }

            return registered.First;
        }

        var repoName = identity[(identity.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        var project = config.String(IndexingSettings.Section, "project") ?? Path.GetFileName(root);
        var candidate = CodePaths.RepoRoot(CodePaths.Slug(project), CodePaths.Slug(repoName));

        var suffix = 2;
        var repoPath = candidate;
        while (ScalarText(connection, "SELECT identity, '' FROM repo_registry WHERE repo_path = $path;", ("$path", repoPath)) is not null)
        {
            repoPath = $"{candidate}-{suffix++}";
        }

        if (!apply)
        {
            notes.Add($"would register {repoPath} for {identity}");
            return repoPath;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);
        var timestamp = now.ToUnixTimeSeconds();

        Execute(
            connection,
            transaction,
            """
            INSERT INTO repo_registry (repo_path, identity, disk_path, created_at)
            VALUES ($path, $identity, $disk, $now);
            """,
            ("$path", repoPath),
            ("$identity", identity),
            ("$disk", root),
            ("$now", timestamp));

        FactStore.EnsureEntity(connection, transaction, repoPath, "repo", timestamp, repoName);

        transaction.Commit();
        notes.Add($"registered {repoPath} for {identity}");
        return repoPath;
    }

    private static void SetSuppressedReason(SqliteConnection connection, string identity, string reason) =>
        Execute(
            connection,
            null,
            "UPDATE repo_registry SET last_scan_suppressed_reason = $reason WHERE identity = $identity;",
            ("$reason", reason),
            ("$identity", identity));

    private static void ClearSuppressedReason(SqliteConnection connection, SqliteTransaction transaction, string identity) =>
        Execute(
            connection,
            transaction,
            "UPDATE repo_registry SET last_scan_suppressed_reason = NULL WHERE identity = $identity;",
            ("$identity", identity));

    private static Dictionary<string, string> LoadStates(SqliteConnection connection, string repoPath)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, blob_sha FROM file_state WHERE repo_path = $repo;";
        command.Parameters.AddWithValue("$repo", repoPath);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            states[reader.GetString(0)] = reader.GetString(1);
        }

        return states;
    }

    /// <summary>
    /// Blob hashes for every clean tracked file in two <c>git</c> invocations. A file that
    /// is dirty in the working tree, or untracked, falls through to a content hash —
    /// <c>ls-files -s</c> reports the staged blob, and the staged blob is exactly what an
    /// unstaged edit does not change. Found on the published binary: the hook spooled an
    /// edit and the drain called the file unchanged, because the edit was not staged.
    /// mtime is never consulted either way (D38's cousin: it moves under checkouts and
    /// copies that change nothing).
    /// </summary>
    private static Dictionary<string, string> GitBlobShas(string root)
    {
        var shas = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = GitFileLister.Run(root, "ls-files", "-s", "-z");
        if (output is null)
        {
            return shas;
        }

        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            var parts = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                shas[record[(tab + 1)..]] = parts[1];
            }
        }

        var status = GitFileLister.Run(root, "status", "--porcelain", "-z", "--untracked-files=all");
        if (status is not null)
        {
            var records = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < records.Length; i++)
            {
                var record = records[i];
                if (record.Length < 4)
                {
                    continue;
                }

                shas.Remove(record[3..]);

                // A rename record carries the old path as its own following record.
                if (record[0] is 'R' or 'C')
                {
                    i++;
                }
            }
        }

        return shas;
    }

    private static string? ShaOf(string root, string rel, Dictionary<string, string> blobs)
    {
        if (blobs.TryGetValue(rel, out var blob))
        {
            return blob;
        }

        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, rel));
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        Execute(
            connection,
            transaction,
            "INSERT INTO schema_meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = $value;",
            ("$key", key),
            ("$value", value));
        transaction.Commit();
    }

    private static long Scalar(
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

        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static (string First, string? Second)? ScalarText(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

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
