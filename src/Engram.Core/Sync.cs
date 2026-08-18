using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The portable, content-addressed identity of a fact: subject path, predicate, body and
/// <c>valid_from</c> — the same 4-tuple <see cref="FactJournal"/> already uses to decide whether a
/// journalled fact is one the target already holds (D32). Sync reuses it rather than inventing a
/// machine-id+local-id identity, per docs/gp-adoption/01-sync-spec.md's "Identity decision".
/// </summary>
public readonly record struct FactIdentity(string Subject, string Predicate, string Body, long ValidFrom);

/// <summary>
/// A sync chunk's <c>"t":"close"</c> record: a fact this machine already exported once (as a
/// <c>"t":"fact"</c> line, while still live) has since been closed locally, and the closure needs
/// to reach any machine that already replicated the open fact. <see cref="FactJournal.Replay"/>
/// never rewrites a row the target already has (D8), which is exactly why this exists as its own
/// record kind rather than a second "fact" line.
/// </summary>
public sealed record SyncCloseRecord(
    string Subject,
    string Predicate,
    string Body,
    long ValidFrom,
    long ValidTo,
    string? SupersededByBody,
    long? SupersededByValidFrom);

/// <summary>One local row — live or closed — in the <c>(subject, predicate)</c> slot a close record names.</summary>
public readonly record struct LocalFactRow(string Body, long ValidFrom, bool IsLive);

/// <summary>
/// The four-case close-resolution decision (docs/gp-adoption/01-sync-spec.md, "Close-record
/// semantics (precise)"), plus the retry-ceiling-to-stalled transition. Both are pure functions
/// over a caller-supplied local-fact table, deliberately: this is the piece the spec's tier-1 tests
/// exercise without a database.
/// </summary>
public static class CloseResolver
{
    /// <summary>
    /// Retry-ceiling default before a <c>sync_deferred_close</c> row moves to <c>stalled</c>
    /// (spec NEEDS-EVIDENCE item 4 — a product call, not evidence). Chosen to match roughly a
    /// week of daily <c>sync import --if-new</c> runs riding session start (D28's detached child),
    /// which is long enough that a fact arriving within a normal working week still resolves, and
    /// short enough that a genuinely orphaned close (the origin machine's superseding chunk was
    /// lost, or the two machines diverged permanently) stops retrying inside a sprint rather than
    /// silently forever. Config-overridable via <c>[sync] retry_ceiling</c>.
    /// </summary>
    public const int DefaultRetryCeiling = 20;

    /// <summary>
    /// Resolves one close record against every local row (live or closed) sharing its
    /// <c>(subject, predicate)</c> slot.
    /// </summary>
    public static CloseResolution Resolve(IReadOnlyList<LocalFactRow> rowsForSlot, SyncCloseRecord record)
    {
        ArgumentNullException.ThrowIfNull(rowsForSlot);
        ArgumentNullException.ThrowIfNull(record);

        LocalFactRow? live = null;
        foreach (var row in rowsForSlot)
        {
            if (row.IsLive)
            {
                live = row;
                break;
            }
        }

        if (live is { } liveRow)
        {
            // The live-check branch: case 2 (apply) only when the slot's live fact IS the named
            // fact, content-identical. Any other live fact in the slot is case 4 (conflict) — the
            // target authored something else here, and a close may never touch it (D8).
            return liveRow.Body == record.Body && liveRow.ValidFrom == record.ValidFrom
                ? CloseResolution.Apply
                : CloseResolution.Conflict;
        }

        foreach (var row in rowsForSlot)
        {
            if (!row.IsLive && row.Body == record.Body && row.ValidFrom == record.ValidFrom)
            {
                return CloseResolution.AlreadyPresent;
            }
        }

        return CloseResolution.Defer;
    }

    /// <summary>
    /// Whether a deferred close, having just failed to resolve for the <paramref name="retryCount"/>th
    /// time, should move to the terminal <c>stalled</c> status rather than remain <c>deferred</c>.
    /// </summary>
    public static string NextDeferredStatus(int retryCount, int ceiling) =>
        retryCount >= ceiling ? "stalled" : "deferred";
}

public enum CloseResolution
{
    Apply,
    AlreadyPresent,
    Conflict,
    Defer,
}

/// <summary>What one <c>sync import</c> did, or would do.</summary>
public sealed record SyncImportResult(
    int Written,
    int AlreadyPresent,
    int Unresolved,
    int Conflicted,
    int Deferred,
    int Stalled,
    int ChunksApplied);

/// <summary>What one <c>sync export</c> wrote, or would write.</summary>
public sealed record SyncExportResult(int FactCount, int CloseCount, string? ChunkPath);

/// <summary>
/// Per-machine, per-remote pending-import counts and deferred/stalled/conflict totals for
/// <c>sync status</c>.
/// </summary>
public sealed record SyncStatus(
    IReadOnlyList<(string MachineId, int PendingChunks)> PendingByMachine,
    int DeferredCount,
    int StalledCount,
    int ConflictCount);

/// <summary>
/// Cross-machine sync (docs/gp-adoption/01-sync-spec.md): a chunk is a slice of the existing
/// journal format (D32) plus close records, written to <c>&lt;home&gt;/sync/&lt;machine-id&gt;/&lt;seq&gt;.jsonl</c>.
/// Applying a chunk's fact lines is a call to the unchanged <see cref="FactJournal.Replay"/>; close
/// lines resolve through <see cref="CloseResolver"/>. Not a second replay implementation (D32).
/// </summary>
public static class Sync
{
    private const string ChunkExtension = ".jsonl";
    private const string PartialSuffix = ".partial";
    private const string MachineIdFileName = "machine-id";

    /// <summary>
    /// The opaque, locally-generated directory discriminator (spec: "never stored per-fact and
    /// never gates authority") — created on first <c>sync export</c>, read thereafter.
    /// </summary>
    public static string ResolveMachineId(string syncRoot)
    {
        ArgumentNullException.ThrowIfNull(syncRoot);

        var path = Path.Combine(syncRoot, MachineIdFileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        Directory.CreateDirectory(syncRoot);
        var id = GenerateMachineId();
        File.WriteAllText(path, id);
        return id;
    }

    /// <summary>
    /// A fresh candidate id, generated but never persisted — what a dry-run <c>export</c>/
    /// <c>import</c> uses in place of <see cref="ResolveMachineId"/> when no id has been assigned
    /// yet, so it can still compute "what would happen" without writing anything (D49). Since no
    /// real chunk directory can exist under a never-persisted id, the computation comes out the
    /// same as it would for this machine's real first id.
    /// </summary>
    public static string GenerateMachineId() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>
    /// Read-only counterpart to <see cref="ResolveMachineId"/> for <c>sync status</c>, which the
    /// spec documents as read-only — it must not assign (and persist) a machine id that export has
    /// never created. Returns <c>null</c> when this machine has never exported.
    /// </summary>
    public static string? TryReadMachineId(string syncRoot)
    {
        ArgumentNullException.ThrowIfNull(syncRoot);

        var path = Path.Combine(syncRoot, MachineIdFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var existing = File.ReadAllText(path).Trim();
        return existing.Length > 0 ? existing : null;
    }

    // ------------------------------------------------------------------
    // Export
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes a new chunk of facts and closes not yet included in any of this machine's previous
    /// own chunks. "Since the last export" is derived entirely from the chunk files already on
    /// disk under this machine's own directory — there is no separate export watermark table
    /// (the schema delta is <c>sync_chunk_state</c>/<c>sync_deferred_close</c>, both import-side
    /// bookkeeping) — which is what "the directory listing... is the index" means for the writer,
    /// not only the reader.
    /// </summary>
    public static SyncExportResult Export(
        SqliteConnection connection,
        string syncRoot,
        string machineId,
        bool apply)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(syncRoot);
        ArgumentNullException.ThrowIfNull(machineId);

        var chunkDir = Path.Combine(syncRoot, machineId);
        var (allExported, openAtExport, closedExported) = ScanOwnChunks(chunkDir);

        var factsToExport = new List<JournalFact>();
        foreach (var fact in FactJournal.Read(connection))
        {
            var identity = new FactIdentity(fact.Subject, fact.Predicate, fact.Body, fact.ValidFrom);
            if (!allExported.Contains(identity))
            {
                factsToExport.Add(fact);
            }
        }

        var closesToExport = new List<SyncCloseRecord>();
        foreach (var identity in openAtExport)
        {
            if (closedExported.Contains(identity))
            {
                continue;
            }

            var current = LookupExact(connection, identity);
            if (current is { IsLive: false } row)
            {
                closesToExport.Add(BuildCloseRecord(connection, identity, row));
            }
        }

        if (!apply || (factsToExport.Count == 0 && closesToExport.Count == 0))
        {
            return new SyncExportResult(factsToExport.Count, closesToExport.Count, ChunkPath: null);
        }

        Directory.CreateDirectory(chunkDir);
        var seq = NextSeq(chunkDir);
        var final = Path.Combine(chunkDir, seq + ChunkExtension);
        var partial = final + PartialSuffix;

        using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            foreach (var fact in factsToExport)
            {
                var line = FactJournal.ToJson(fact);
                line["t"] = "fact";
                writer.WriteLine(line.ToJsonString());
            }

            foreach (var close in closesToExport)
            {
                writer.WriteLine(ToJson(close).ToJsonString());
            }
        }

        // Never rewrites an existing chunk (spec's gp-verified rationale): a collision here means
        // NextSeq raced another export of this same machine, which a single-process CLI verb does
        // not do to itself.
        File.Move(partial, final, overwrite: false);

        return new SyncExportResult(factsToExport.Count, closesToExport.Count, final);
    }

    private static (HashSet<FactIdentity> AllExported, HashSet<FactIdentity> OpenAtExport, HashSet<FactIdentity> ClosedExported)
        ScanOwnChunks(string chunkDir)
    {
        var all = new HashSet<FactIdentity>();
        var open = new HashSet<FactIdentity>();
        var closed = new HashSet<FactIdentity>();

        foreach (var (_, lines) in EnumerateChunkFiles(chunkDir))
        {
            foreach (var line in lines)
            {
                var node = TryParseLine(line);
                if (node is not JsonObject record)
                {
                    continue;
                }

                var tag = record.TryGetPropertyValue("t", out var t) ? t?.GetValue<string>() : null;
                if (tag == "fact")
                {
                    var fact = FactJournal.Parse([line], out _);
                    if (fact.Count != 1)
                    {
                        continue;
                    }

                    var identity = new FactIdentity(fact[0].Subject, fact[0].Predicate, fact[0].Body, fact[0].ValidFrom);
                    all.Add(identity);
                    if (fact[0].ValidTo is null)
                    {
                        open.Add(identity);
                    }
                }
                else if (tag == "close")
                {
                    var close = FromJson(record);
                    if (close is not null)
                    {
                        closed.Add(new FactIdentity(close.Subject, close.Predicate, close.Body, close.ValidFrom));
                    }
                }
            }
        }

        return (all, open, closed);
    }

    private static int NextSeq(string chunkDir)
    {
        var max = 0;
        if (Directory.Exists(chunkDir))
        {
            foreach (var file in Directory.EnumerateFiles(chunkDir, "*" + ChunkExtension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (int.TryParse(name, out var seq) && seq > max)
                {
                    max = seq;
                }
            }
        }

        return max + 1;
    }

    // ------------------------------------------------------------------
    // Import
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies every not-yet-applied chunk from every other machine under <paramref name="syncRoot"/>,
    /// in per-machine sequence order. All pending facts across every pending chunk are handed to one
    /// <see cref="FactJournal.Replay"/> call rather than one per chunk — a supersession pointer that
    /// spans two chunks pending in the same import run resolves through the shared <c>idMap</c> that
    /// way, where two separate calls could not see each other's inserts. Fact lines apply before
    /// close lines, mirroring the spec's own within-chunk ordering, extended to the whole batch.
    /// </summary>
    public static SyncImportResult Import(
        SqliteConnection connection,
        string syncRoot,
        string ownMachineId,
        DateTimeOffset now,
        bool apply,
        int retryCeiling)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(syncRoot);
        ArgumentNullException.ThrowIfNull(ownMachineId);

        var pending = DiscoverPendingChunks(connection, syncRoot, ownMachineId);

        var factLines = new List<string>();
        var closeRecords = new List<SyncCloseRecord>();
        foreach (var chunk in pending)
        {
            factLines.AddRange(chunk.FactLines);
            closeRecords.AddRange(chunk.Closes);
        }

        // Dedup close records seen in this batch by identity — a later chunk's copy of the same
        // close (e.g. re-exported because an earlier peer never received it) wins, since it is the
        // more recent knowledge of that slot.
        var closesByIdentity = new Dictionary<FactIdentity, SyncCloseRecord>();
        foreach (var close in closeRecords)
        {
            closesByIdentity[new FactIdentity(close.Subject, close.Predicate, close.Body, close.ValidFrom)] = close;
        }

        // Also re-evaluate every already-deferred row on disk: it may resolve now that this batch's
        // facts just landed, even if this batch carried no new close record naming it.
        foreach (var deferred in ReadDeferredRows(connection))
        {
            var identity = new FactIdentity(deferred.SubjectPath, deferred.Predicate, deferred.Body, deferred.ValidFrom);
            if (!closesByIdentity.ContainsKey(identity))
            {
                closesByIdentity[identity] = ToCloseRecord(deferred);
            }
        }

        MergeSameBatchClosesIntoFactLines(factLines, closesByIdentity);
        var facts = FactJournal.Parse(factLines, out _);

        var applied = 0;
        var alreadyPresent = 0;
        var conflicted = 0;
        var deferredCount = 0;
        var stalledCount = 0;
        var pendingSupersessionBackfill = new List<SyncCloseRecord>();

        // Pass 1, against pre-replay state: a close whose slot already holds exactly the fact it
        // names — live or already closed — is resolved and applied before Replay runs at all. This
        // matters when the same batch also carries that fact's own successor (two revisions on the
        // origin machine since this machine's last import): FactJournal.Replay refuses to displace a
        // live belief it did not itself insert in this call (its own insertion tracking is local to
        // one call, per D32/D68), so closing the predecessor first — clearing the slot — is what lets
        // Replay insert the successor a moment later without that guard mistaking a same-origin
        // catch-up for an independent conflict.
        var resolvedBeforeReplay = new HashSet<FactIdentity>();
        SqliteTransaction? preTransaction = apply ? EngramDatabase.BeginWrite(connection) : null;
        try
        {
            foreach (var (identity, record) in closesByIdentity)
            {
                var rows = LookupSlotRows(connection, preTransaction, identity.Subject, identity.Predicate);
                var outcome = CloseResolver.Resolve(rows, record);

                if (outcome == CloseResolution.Apply)
                {
                    resolvedBeforeReplay.Add(identity);
                    applied++;
                    if (apply)
                    {
                        ApplyClose(connection, preTransaction!, record);
                        RemoveDeferredRow(connection, preTransaction, identity);

                        // The successor lookup ApplyClose just ran only sees rows Replay has
                        // already inserted, and Replay itself has not run yet — a successor that
                        // arrives in this same batch is invisible here, so its pointer lands NULL.
                        // Revisit once Replay has committed (below).
                        if (record.SupersededByBody is not null)
                        {
                            pendingSupersessionBackfill.Add(record);
                        }
                    }
                }
                else if (outcome == CloseResolution.AlreadyPresent)
                {
                    resolvedBeforeReplay.Add(identity);
                    alreadyPresent++;
                    if (apply)
                    {
                        RemoveDeferredRow(connection, preTransaction, identity);
                    }
                }
            }

            preTransaction?.Commit();
        }
        finally
        {
            preTransaction?.Dispose();
        }

        var replay = FactJournal.Replay(connection, facts, apply);

        SqliteTransaction? transaction = apply ? EngramDatabase.BeginWrite(connection) : null;
        try
        {
            // Now that Replay has inserted this batch's fact rows, re-resolve the successor
            // pointer for every pass-1-applied close that named one — the ordinary "revise
            // already-synced fact" case (see the note above where these are collected).
            foreach (var record in pendingSupersessionBackfill)
            {
                BackfillSupersededBy(connection, transaction!, record);
            }

            foreach (var (identity, record) in closesByIdentity)
            {
                if (resolvedBeforeReplay.Contains(identity))
                {
                    continue;
                }

                var rows = LookupSlotRows(connection, transaction, identity.Subject, identity.Predicate);
                var outcome = CloseResolver.Resolve(rows, record);

                switch (outcome)
                {
                    case CloseResolution.Apply:
                        applied++;
                        if (apply)
                        {
                            ApplyClose(connection, transaction!, record);
                            RemoveDeferredRow(connection, transaction, identity);
                        }

                        break;

                    case CloseResolution.AlreadyPresent:
                        alreadyPresent++;
                        if (apply)
                        {
                            RemoveDeferredRow(connection, transaction, identity);
                        }

                        break;

                    case CloseResolution.Conflict:
                        conflicted++;
                        break;

                    case CloseResolution.Defer:
                        var existingRetry = FindDeferredRetryCount(connection, transaction, identity);
                        var nextRetry = existingRetry + 1;
                        var status = CloseResolver.NextDeferredStatus(nextRetry, retryCeiling);
                        if (status == "stalled")
                        {
                            stalledCount++;
                        }
                        else
                        {
                            deferredCount++;
                        }

                        if (apply)
                        {
                            UpsertDeferredRow(connection, transaction, identity, record, nextRetry, status, now);
                        }

                        break;
                }
            }

            if (apply)
            {
                foreach (var chunk in pending)
                {
                    MarkChunkApplied(connection, transaction!, chunk.MachineId, chunk.Seq, now, chunk.FactLines.Count, chunk.Closes.Count);
                }

                transaction!.Commit();
            }
        }
        finally
        {
            transaction?.Dispose();
        }

        return new SyncImportResult(
            replay.Written + applied,
            replay.AlreadyPresent + alreadyPresent,
            replay.Unresolved,
            replay.Conflicted + conflicted,
            deferredCount,
            stalledCount,
            pending.Count);
    }

    /// <summary>
    /// A fact that has never synced anywhere before, and is already closed by the time this same
    /// import batch first carries it, arrives as an "open" fact line (that is what it looked like at
    /// its own first export) plus a separate close record — <see cref="FactJournal.Replay"/> has no
    /// way to see the two as one chain, because it links supersession purely through matching journal
    /// ids present in the same call, and this fact's own line never carries its closed state. Left
    /// alone, Replay inserts the predecessor as an ordinary open fact and then refuses to insert the
    /// successor into the same slot, since nothing tells it they are the same author's chain rather
    /// than an independent conflict (D68). Stamping <c>valid_to</c>/<c>superseded_by</c> directly onto
    /// the predecessor's own fact line — using the successor's journal id, when the successor is also
    /// present in this batch — turns it back into what a normal <c>facts.jsonl</c> entry always looks
    /// like for a closed fact, which Replay's existing chain-linking already handles correctly. Only
    /// applies when the predecessor's fact line is actually in this batch; a predecessor the target
    /// already replicated in an earlier import goes through <see cref="Import"/>'s own two-pass close
    /// resolution instead.
    /// </summary>
    private static void MergeSameBatchClosesIntoFactLines(
        List<string> factLines,
        IReadOnlyDictionary<FactIdentity, SyncCloseRecord> closesByIdentity)
    {
        var lineIdentities = new Dictionary<FactIdentity, (int Index, long FactId)>();
        for (var i = 0; i < factLines.Count; i++)
        {
            if (TryParseLine(factLines[i]) is not JsonObject line)
            {
                continue;
            }

            var id = NumberOf(line, "id");
            var subject = TextOf(line, "subject");
            var predicate = TextOf(line, "predicate");
            var body = TextOf(line, "body");
            var validFrom = NumberOf(line, "valid_from");
            if (id is null || subject is null || predicate is null || body is null || validFrom is null)
            {
                continue;
            }

            lineIdentities[new FactIdentity(subject, predicate, body, validFrom.Value)] = (i, id.Value);
        }

        foreach (var (identity, record) in closesByIdentity)
        {
            if (!lineIdentities.TryGetValue(identity, out var predecessor))
            {
                continue;
            }

            long? successorId = null;
            if (record.SupersededByBody is { } successorBody && record.SupersededByValidFrom is { } successorValidFrom)
            {
                var successorIdentity = identity with { Body = successorBody, ValidFrom = successorValidFrom };
                if (lineIdentities.TryGetValue(successorIdentity, out var successor))
                {
                    successorId = successor.FactId;
                }
            }

            if (TryParseLine(factLines[predecessor.Index]) is JsonObject toClose)
            {
                toClose["valid_to"] = record.ValidTo;
                toClose["superseded_by"] = successorId;
                factLines[predecessor.Index] = toClose.ToJsonString();
            }
        }
    }

    private sealed record PendingChunk(string MachineId, int Seq, IReadOnlyList<string> FactLines, IReadOnlyList<SyncCloseRecord> Closes);

    private static List<PendingChunk> DiscoverPendingChunks(SqliteConnection connection, string syncRoot, string ownMachineId)
    {
        var applied = ReadAppliedChunkKeys(connection);
        var result = new List<PendingChunk>();

        if (!Directory.Exists(syncRoot))
        {
            return result;
        }

        foreach (var machineDir in Directory.EnumerateDirectories(syncRoot))
        {
            var machineId = Path.GetFileName(machineDir);
            if (string.Equals(machineId, ownMachineId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (seq, lines) in EnumerateChunkFiles(machineDir).OrderBy(c => c.Seq))
            {
                if (applied.Contains((machineId, seq)))
                {
                    continue;
                }

                var factLines = new List<string>();
                var closes = new List<SyncCloseRecord>();
                foreach (var line in lines)
                {
                    var node = TryParseLine(line);
                    if (node is not JsonObject record)
                    {
                        continue;
                    }

                    var tag = record.TryGetPropertyValue("t", out var t) ? t?.GetValue<string>() : null;
                    if (tag == "fact")
                    {
                        factLines.Add(line);
                    }
                    else if (tag == "close")
                    {
                        var close = FromJson(record);
                        if (close is not null)
                        {
                            closes.Add(close);
                        }
                    }
                }

                result.Add(new PendingChunk(machineId, seq, factLines, closes));
            }
        }

        return result;
    }

    private static IEnumerable<(int Seq, IReadOnlyList<string> Lines)> EnumerateChunkFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*" + ChunkExtension).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(name, out var seq))
            {
                continue;
            }

            yield return (seq, File.ReadAllLines(file));
        }
    }

    private static JsonNode? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // sync_chunk_state
    // ------------------------------------------------------------------

    private static HashSet<(string MachineId, int Seq)> ReadAppliedChunkKeys(SqliteConnection connection)
    {
        var keys = new HashSet<(string, int)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT machine_id, seq FROM sync_chunk_state;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            keys.Add((reader.GetString(0), (int)reader.GetInt64(1)));
        }

        return keys;
    }

    private static void MarkChunkApplied(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        int seq,
        DateTimeOffset now,
        int factCount,
        int closeCount)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO sync_chunk_state (machine_id, seq, applied_at, fact_count, close_count)
            VALUES ($machineId, $seq, $appliedAt, $factCount, $closeCount)
            ON CONFLICT(machine_id, seq) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$appliedAt", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$factCount", factCount);
        command.Parameters.AddWithValue("$closeCount", closeCount);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // sync_deferred_close
    // ------------------------------------------------------------------

    private sealed record DeferredRow(
        string SubjectPath,
        string Predicate,
        string Body,
        long ValidFrom,
        long ValidTo,
        string? SupersededByBody,
        long? SupersededByValidFrom,
        string Status,
        int RetryCount);

    private static SyncCloseRecord ToCloseRecord(DeferredRow row) => new(
        row.SubjectPath, row.Predicate, row.Body, row.ValidFrom, row.ValidTo,
        row.SupersededByBody, row.SupersededByValidFrom);

    private static List<DeferredRow> ReadDeferredRows(SqliteConnection connection)
    {
        var rows = new List<DeferredRow>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT subject_path, predicate, body, valid_from, valid_to, superseded_by_body,
                   superseded_by_valid_from, status, retry_count
            FROM sync_deferred_close
            WHERE status = 'deferred';
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DeferredRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7),
                (int)reader.GetInt64(8)));
        }

        return rows;
    }

    private static int FindDeferredRetryCount(SqliteConnection connection, SqliteTransaction? transaction, FactIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT retry_count FROM sync_deferred_close
            WHERE subject_path = $subject AND predicate = $predicate AND body = $body AND valid_from = $validFrom;
            """;
        AddIdentityParameters(command, identity);
        var value = command.ExecuteScalar();
        return value is long l ? (int)l : 0;
    }

    private static void UpsertDeferredRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        FactIdentity identity,
        SyncCloseRecord record,
        int retryCount,
        string status,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO sync_deferred_close
                (subject_path, predicate, body, valid_from, valid_to, superseded_by_body,
                 superseded_by_valid_from, status, retry_count, first_seen_at, source_chunk)
            VALUES
                ($subject, $predicate, $body, $validFrom, $validTo, $supersededByBody,
                 $supersededByValidFrom, $status, $retryCount, $firstSeenAt, $sourceChunk)
            ON CONFLICT(subject_path, predicate, body, valid_from) DO UPDATE SET
                status = excluded.status,
                retry_count = excluded.retry_count;
            """;
        AddIdentityParameters(command, identity);
        command.Parameters.AddWithValue("$validTo", record.ValidTo);
        command.Parameters.AddWithValue("$supersededByBody", (object?)record.SupersededByBody ?? DBNull.Value);
        command.Parameters.AddWithValue("$supersededByValidFrom", (object?)record.SupersededByValidFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$retryCount", retryCount);
        command.Parameters.AddWithValue("$firstSeenAt", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$sourceChunk", "sync");
        command.ExecuteNonQuery();
    }

    private static void RemoveDeferredRow(SqliteConnection connection, SqliteTransaction? transaction, FactIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM sync_deferred_close
            WHERE subject_path = $subject AND predicate = $predicate AND body = $body AND valid_from = $validFrom;
            """;
        AddIdentityParameters(command, identity);
        command.ExecuteNonQuery();
    }

    private static void AddIdentityParameters(SqliteCommand command, FactIdentity identity)
    {
        command.Parameters.AddWithValue("$subject", identity.Subject);
        command.Parameters.AddWithValue("$predicate", identity.Predicate);
        command.Parameters.AddWithValue("$body", identity.Body);
        command.Parameters.AddWithValue("$validFrom", identity.ValidFrom);
    }

    // ------------------------------------------------------------------
    // fact lookups shared by export and import
    // ------------------------------------------------------------------

    private readonly record struct ExactRow(bool IsLive, long ValidTo, long? SupersededById);

    private static ExactRow? LookupExact(SqliteConnection connection, FactIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.valid_to IS NULL, f.valid_to, f.superseded_by FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $subject AND f.predicate = $predicate AND f.body = $body AND f.valid_from = $validFrom;
            """;
        AddIdentityParameters(command, identity);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ExactRow(
            !reader.IsDBNull(0) && reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static List<LocalFactRow> LookupSlotRows(SqliteConnection connection, SqliteTransaction? transaction, string subject, string predicate)
    {
        var rows = new List<LocalFactRow>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT f.body, f.valid_from, f.valid_to IS NULL FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $subject AND f.predicate = $predicate;
            """;
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$predicate", predicate);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LocalFactRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) != 0));
        }

        return rows;
    }

    private static SyncCloseRecord BuildCloseRecord(SqliteConnection connection, FactIdentity identity, ExactRow row)
    {
        string? supersededByBody = null;
        long? supersededByValidFrom = null;

        if (row.SupersededById is { } supersededById)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body, valid_from FROM fact WHERE id = $id;";
            command.Parameters.AddWithValue("$id", supersededById);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                supersededByBody = reader.GetString(0);
                supersededByValidFrom = reader.GetInt64(1);
            }
        }

        return new SyncCloseRecord(
            identity.Subject, identity.Predicate, identity.Body, identity.ValidFrom,
            row.ValidTo, supersededByBody, supersededByValidFrom);
    }

    /// <summary>
    /// Looks up the local row id for a close record's named successor, if any and if it has
    /// synced here yet. Shared by <see cref="ApplyClose"/> (first attempt, pre- or post-Replay)
    /// and <see cref="BackfillSupersededBy"/> (a second attempt once Replay has had a chance to
    /// insert it).
    /// </summary>
    private static long? TryResolveSuccessorId(SqliteConnection connection, SqliteTransaction transaction, SyncCloseRecord record)
    {
        if (record.SupersededByBody is not { } sbBody || record.SupersededByValidFrom is not { } sbValidFrom)
        {
            return null;
        }

        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText =
            """
            SELECT f.id FROM fact f
            JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $subject AND f.predicate = $predicate AND f.body = $body AND f.valid_from = $validFrom
            LIMIT 1;
            """;
        lookup.Parameters.AddWithValue("$subject", record.Subject);
        lookup.Parameters.AddWithValue("$predicate", record.Predicate);
        lookup.Parameters.AddWithValue("$body", sbBody);
        lookup.Parameters.AddWithValue("$validFrom", sbValidFrom);
        return lookup.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>
    /// Re-resolves <c>superseded_by</c> for a close pass 1 already applied before its named
    /// successor existed locally (the successor arrived moments later, via the same import's
    /// call to <see cref="FactJournal.Replay"/>). Only ever moves the pointer from NULL to a
    /// found id — it never touches <c>valid_to</c> (already correct) and never overwrites a
    /// pointer some other path already set, so it is safe to run unconditionally over the
    /// pass-1-applied set.
    /// </summary>
    private static void BackfillSupersededBy(SqliteConnection connection, SqliteTransaction transaction, SyncCloseRecord record)
    {
        var successorId = TryResolveSuccessorId(connection, transaction, record);
        if (successorId is null)
        {
            return;
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE fact SET superseded_by = $supersededBy
            WHERE id = (
                SELECT f.id FROM fact f
                JOIN entity e ON e.id = f.subject_id
                WHERE e.path = $subject AND f.predicate = $predicate AND f.body = $body AND f.valid_from = $validFrom
            ) AND superseded_by IS NULL;
            """;
        update.Parameters.AddWithValue("$supersededBy", successorId.Value);
        update.Parameters.AddWithValue("$subject", record.Subject);
        update.Parameters.AddWithValue("$predicate", record.Predicate);
        update.Parameters.AddWithValue("$body", record.Body);
        update.Parameters.AddWithValue("$validFrom", record.ValidFrom);
        update.ExecuteNonQuery();
    }

    /// <summary>Applies an Apply-resolved close: sets <c>valid_to</c>/<c>superseded_by</c> the same way <c>engram_revise</c>/<c>engram_forget</c> do (D8).</summary>
    private static void ApplyClose(SqliteConnection connection, SqliteTransaction transaction, SyncCloseRecord record)
    {
        // If the superseding fact has not synced here yet, the close still applies — the pointer
        // is left unresolved for now (BackfillSupersededBy revisits it after Replay runs), the
        // same tradeoff FactJournal.Replay makes for a superseding fact missing from the same
        // journal (D32): the belief closed at the recorded time, only the "replaced by" link is
        // missing, and only until Replay has had its chance.
        var supersededById = TryResolveSuccessorId(connection, transaction, record);

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE fact SET valid_to = $validTo, superseded_by = $supersededBy
            WHERE id = (
                SELECT f.id FROM fact f
                JOIN entity e ON e.id = f.subject_id
                WHERE e.path = $subject AND f.predicate = $predicate AND f.body = $body
                  AND f.valid_from = $validFrom AND f.valid_to IS NULL
            )
            RETURNING id;
            """;
        update.Parameters.AddWithValue("$subject", record.Subject);
        update.Parameters.AddWithValue("$predicate", record.Predicate);
        update.Parameters.AddWithValue("$body", record.Body);
        update.Parameters.AddWithValue("$validFrom", record.ValidFrom);
        update.Parameters.AddWithValue("$validTo", record.ValidTo);
        update.Parameters.AddWithValue("$supersededBy", (object?)supersededById ?? DBNull.Value);
        var closedId = update.ExecuteScalar();

        if (closedId is long factId)
        {
            FactTokenIndex.Remove(connection, transaction, factId);

            if (supersededById is { } target)
            {
                using var supersession = connection.CreateCommand();
                supersession.Transaction = transaction;
                supersession.CommandText =
                    """
                    INSERT INTO supersession (old_fact_id, new_fact_id, reason, created_at)
                    VALUES ($old, $new, $reason, $createdAt)
                    ON CONFLICT(old_fact_id) DO NOTHING;
                    """;
                supersession.Parameters.AddWithValue("$old", factId);
                supersession.Parameters.AddWithValue("$new", target);
                supersession.Parameters.AddWithValue("$reason", "synced from another machine");
                supersession.Parameters.AddWithValue("$createdAt", record.ValidTo);
                supersession.ExecuteNonQuery();
            }
        }
    }

    // ------------------------------------------------------------------
    // sync status
    // ------------------------------------------------------------------

    public static SyncStatus Status(SqliteConnection connection, string syncRoot, string ownMachineId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(syncRoot);
        ArgumentNullException.ThrowIfNull(ownMachineId);

        var pendingByMachine = new List<(string, int)>();
        if (Directory.Exists(syncRoot))
        {
            var applied = ReadAppliedChunkKeys(connection);
            foreach (var machineDir in Directory.EnumerateDirectories(syncRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var machineId = Path.GetFileName(machineDir);
                if (string.Equals(machineId, ownMachineId, StringComparison.Ordinal))
                {
                    continue;
                }

                var pending = EnumerateChunkFiles(machineDir).Count(c => !applied.Contains((machineId, c.Seq)));
                if (pending > 0)
                {
                    pendingByMachine.Add((machineId, pending));
                }
            }
        }

        int deferred, stalled;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sync_deferred_close WHERE status = 'deferred';";
            deferred = (int)(long)command.ExecuteScalar()!;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sync_deferred_close WHERE status = 'stalled';";
            stalled = (int)(long)command.ExecuteScalar()!;
        }

        return new SyncStatus(pendingByMachine, deferred, stalled, CountConflicts(connection, syncRoot, ownMachineId));
    }

    /// <summary>
    /// A conflict is a live disagreement, not a one-time event, and there is no third side table
    /// for it (the schema delta is exactly <c>sync_chunk_state</c>/<c>sync_deferred_close</c>) — so
    /// <c>sync status</c> re-derives the current count by re-resolving every close record this
    /// machine has ever seen from a peer (applied or still pending) against the store's current
    /// live facts, the same way "derived state is repairable" already licenses re-deriving
    /// <c>sync_chunk_state</c>/<c>sync_deferred_close</c> from the full chunk history.
    /// </summary>
    private static int CountConflicts(SqliteConnection connection, string syncRoot, string ownMachineId)
    {
        if (!Directory.Exists(syncRoot))
        {
            return 0;
        }

        var seen = new HashSet<FactIdentity>();
        var conflicts = 0;

        foreach (var machineDir in Directory.EnumerateDirectories(syncRoot))
        {
            var machineId = Path.GetFileName(machineDir);
            if (string.Equals(machineId, ownMachineId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (_, lines) in EnumerateChunkFiles(machineDir))
            {
                foreach (var line in lines)
                {
                    if (TryParseLine(line) is not JsonObject record
                        || !record.TryGetPropertyValue("t", out var t)
                        || t?.GetValue<string>() != "close")
                    {
                        continue;
                    }

                    var close = FromJson(record);
                    if (close is null)
                    {
                        continue;
                    }

                    var identity = new FactIdentity(close.Subject, close.Predicate, close.Body, close.ValidFrom);
                    if (!seen.Add(identity))
                    {
                        continue;
                    }

                    var rows = LookupSlotRows(connection, null, close.Subject, close.Predicate);
                    if (CloseResolver.Resolve(rows, close) == CloseResolution.Conflict)
                    {
                        conflicts++;
                    }
                }
            }
        }

        return conflicts;
    }

    // ------------------------------------------------------------------
    // close record JSON
    // ------------------------------------------------------------------

    private static JsonObject ToJson(SyncCloseRecord record)
    {
        var obj = new JsonObject
        {
            ["t"] = "close",
            ["subject"] = record.Subject,
            ["predicate"] = record.Predicate,
            ["body"] = record.Body,
            ["valid_from"] = record.ValidFrom,
            ["valid_to"] = record.ValidTo,
        };

        obj["superseded_by"] = record.SupersededByBody is not null
            ? new JsonObject { ["body"] = record.SupersededByBody, ["valid_from"] = record.SupersededByValidFrom }
            : null;

        return obj;
    }

    private static SyncCloseRecord? FromJson(JsonObject record)
    {
        var subject = TextOf(record, "subject");
        var predicate = TextOf(record, "predicate");
        var body = TextOf(record, "body");
        var validFrom = NumberOf(record, "valid_from");
        var validTo = NumberOf(record, "valid_to");

        if (subject is null || predicate is null || body is null || validFrom is null || validTo is null)
        {
            return null;
        }

        string? supersededByBody = null;
        long? supersededByValidFrom = null;
        if (record.TryGetPropertyValue("superseded_by", out var sb) && sb is JsonObject sbObject)
        {
            supersededByBody = TextOf(sbObject, "body");
            supersededByValidFrom = NumberOf(sbObject, "valid_from");
        }

        return new SyncCloseRecord(subject, predicate, body, validFrom.Value, validTo.Value, supersededByBody, supersededByValidFrom);
    }

    private static string? TextOf(JsonObject record, string key) =>
        record.TryGetPropertyValue(key, out var value) && value is JsonValue text
            ? text.GetValue<string?>()
            : null;

    private static long? NumberOf(JsonObject record, string key) =>
        record.TryGetPropertyValue(key, out var value) && value is JsonValue number
            ? number.GetValue<long?>()
            : null;
}
