using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram sync</c> — cross-machine sync (docs/memory-expansion/01-sync-spec.md). <c>export</c> and
/// <c>import</c> are dry-run by default, requiring <c>--apply</c> to write (D49, matching
/// <c>backup</c>/<c>repair</c>); <c>status</c> is read-only.
/// </summary>
public static class SyncCommand
{
    /// <summary>Telemetry records this verb's session id as "cli", the same convention <c>RepoCommand</c> uses.</summary>
    private const string CliSessionId = "cli";

    /// <summary>
    /// Watermark for <c>--if-new</c>: its mtime marks the last time this machine checked for peer
    /// content, compared against the newest peer chunk mtime rather than mere directory existence,
    /// so the cheap check stays cheap after the first exchange instead of becoming a permanent
    /// full-scan no-op.
    /// </summary>
    private const string ImportWatermarkFileName = "import-watermark";

    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var home = EngramHome.ResolveFromProcess(homePath);
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = SyncSettings.Read(config);

        foreach (var problem in settings.Problems)
        {
            stderr.WriteLine("warning: " + problem);
        }

        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            stderr.WriteLine("error: expected a subcommand (export, import, status, compact).");
            return 1;
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "export" => Export(home, settings, rest, stdout, stderr),
            "import" => Import(home, settings, rest, stdout, stderr),
            "status" => Status(home, settings, stdout, stderr),
            "compact" => Compact(home, settings, rest, stdout, stderr),
            _ => Unknown(subcommand, stderr),
        };
    }

    private static int Export(EngramHome home, SyncSettings settings, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var ifDue = args.Contains("--if-due");
        var scope = ReadScopeOption(args) ?? settings.Scope;

        if (!File.Exists(home.DatabasePath))
        {
            stdout.WriteLine("No store at " + home.DatabasePath + " — nothing to export.");
            return 0;
        }

        var syncRoot = settings.ResolveDir(home);
        // A dry run must not write anything (D49), including the machine-id file that
        // ResolveMachineId creates on first use — read it if it exists, otherwise compute a
        // throwaway candidate purely for this run's "what would happen" arithmetic.
        var machineId = apply ? Sync.ResolveMachineId(home.SyncDir) : Sync.TryReadMachineId(home.SyncDir) ?? Sync.GenerateMachineId();

        if (ifDue && !IsExportDue(home, syncRoot, machineId))
        {
            stdout.WriteLine("Skipped: nothing has changed since the last export.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        using var connection = EngramDatabase.OpenInitialized(home);

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: now.ToString("o"),
            SessionId: CliSessionId,
            Kind: TelemetryEventKind.Sync,
            Phase: "started"));

        SyncExportResult result;
        try
        {
            result = Sync.Export(connection, syncRoot, machineId, apply, scope);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"), SessionId: CliSessionId,
                Kind: TelemetryEventKind.Sync, Phase: "failed"));
            stderr.WriteLine("error: export failed and nothing was written — " + exception.Message);
            return 1;
        }

        if (result.Error is not null)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"), SessionId: CliSessionId,
                Kind: TelemetryEventKind.Sync, Phase: "failed"));
            stderr.WriteLine("error: " + result.Error);
            return 1;
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTimeOffset.UtcNow.ToString("o"), SessionId: CliSessionId,
            Kind: TelemetryEventKind.Sync, Phase: "finished"));

        if (result.FactCount == 0 && result.CloseCount == 0)
        {
            stdout.WriteLine("Nothing to export — no new facts or closes since the last export.");
            return 0;
        }

        stdout.WriteLine(
            $"{(apply ? "Wrote" : "Would write")} {result.FactCount} fact(s) and {result.CloseCount} close(s) "
                + $"as machine {machineId}.");

        if (apply)
        {
            stdout.WriteLine("  " + result.ChunkPath);
        }
        else
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was written. Re-run with --apply to write the chunk.");
        }

        return 0;
    }

    /// <summary>
    /// Parses a <c>--scope=&lt;value&gt;</c> argument, if present. Returns <c>null</c> when absent
    /// so the caller falls back to <c>[sync] scope</c> — this run-only override does not persist.
    /// </summary>
    private static string? ReadScopeOption(string[] args)
    {
        const string prefix = "--scope=";
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// The cheap check before doing any real export work: nothing to do if the database file has
    /// not changed since this machine's newest own chunk was written. Mirrors <c>backup take
    /// --if-due</c>'s "skip when the fingerprint of authored truth has not moved" without a second
    /// fingerprint table — the database's own mtime already answers "did anything change".
    /// </summary>
    private static bool IsExportDue(EngramHome home, string syncRoot, string machineId)
    {
        var chunkDir = Path.Combine(syncRoot, machineId);
        if (!Directory.Exists(chunkDir))
        {
            return true;
        }

        var newestChunk = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(chunkDir, "*.jsonl"))
        {
            var mtime = File.GetLastWriteTimeUtc(file);
            if (mtime > newestChunk)
            {
                newestChunk = mtime;
            }
        }

        return File.GetLastWriteTimeUtc(home.DatabasePath) > newestChunk;
    }

    private static int Import(EngramHome home, SyncSettings settings, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var ifNew = args.Contains("--if-new");

        if (!File.Exists(home.DatabasePath))
        {
            stdout.WriteLine("No store at " + home.DatabasePath + " — nothing to import into.");
            return 0;
        }

        var syncRoot = settings.ResolveDir(home);
        // Same D49 reasoning as Export: no write on a dry run, including the id file itself.
        var machineId = apply ? Sync.ResolveMachineId(home.SyncDir) : Sync.TryReadMachineId(home.SyncDir) ?? Sync.GenerateMachineId();

        // Cheap check first (spec's hook-impact note): mirrors IsExportDue's mtime comparison
        // rather than only existence, so this stays cheap on every session-start after the first
        // exchange with a peer instead of becoming a permanent full-scan no-op.
        var watermarkPath = Path.Combine(home.SyncDir, ImportWatermarkFileName);
        if (ifNew && !HasNewPeerContent(syncRoot, machineId, watermarkPath))
        {
            stdout.WriteLine("Skipped: no peer content newer than the last import check under " + syncRoot + ".");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        using var connection = EngramDatabase.OpenInitialized(home);

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: now.ToString("o"), SessionId: CliSessionId,
            Kind: TelemetryEventKind.Sync, Phase: "started"));

        SyncImportResult result;
        try
        {
            result = Sync.Import(connection, syncRoot, machineId, now, apply, settings.RetryCeiling);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"), SessionId: CliSessionId,
                Kind: TelemetryEventKind.Sync, Phase: "failed"));
            stderr.WriteLine("error: import failed and nothing was written — " + exception.Message);
            return 1;
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTimeOffset.UtcNow.ToString("o"), SessionId: CliSessionId,
            Kind: TelemetryEventKind.Sync, Phase: "finished"));

        if (apply)
        {
            // Only a real, applied import may move the watermark (D49) — a dry run must leave
            // the next --if-new check exactly as due as it already was.
            Directory.CreateDirectory(home.SyncDir);
            File.WriteAllText(watermarkPath, string.Empty);
        }

        if (result.ChunksApplied == 0)
        {
            stdout.WriteLine("Nothing to import — no pending chunks.");
            return 0;
        }

        stdout.WriteLine(
            $"{(apply ? "Applied" : "Would apply")} {result.ChunksApplied} chunk(s): "
                + $"Written {result.Written}, AlreadyPresent {result.AlreadyPresent}, "
                + $"Deferred {result.Deferred}, Stalled {result.Stalled}, Conflicted {result.Conflicted}.");

        if (result.Unresolved > 0)
        {
            stdout.WriteLine(
                $"  {result.Unresolved} supersession link(s) pointed outside the applied chunks; "
                    + "those facts kept their end date but not what replaced them.");
        }

        if (result.Conflicted > 0)
        {
            stdout.WriteLine(
                $"  {result.Conflicted} close(s) named a fact this store diverged on independently; "
                    + "left untouched. Run 'engram sync status' to see them.");
        }

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to apply it.");
        }

        return 0;
    }

    /// <summary>
    /// Mirrors <see cref="IsExportDue"/>'s mtime comparison on the import side: due when a peer
    /// directory exists whose newest chunk is newer than <paramref name="watermarkPath"/>, or when
    /// no watermark has been written yet. The watermark advances only after a real, applied import
    /// (see <see cref="Import"/>), so a dry run or a skip never moves it.
    /// </summary>
    private static bool HasNewPeerContent(string syncRoot, string ownMachineId, string watermarkPath)
    {
        if (!Directory.Exists(syncRoot))
        {
            return false;
        }

        if (!File.Exists(watermarkPath))
        {
            return true;
        }

        var watermark = File.GetLastWriteTimeUtc(watermarkPath);

        foreach (var directory in Directory.EnumerateDirectories(syncRoot))
        {
            if (string.Equals(Path.GetFileName(directory), ownMachineId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(file) > watermark)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int Status(EngramHome home, SyncSettings settings, TextWriter stdout, TextWriter stderr)
    {
        if (!File.Exists(home.DatabasePath))
        {
            stdout.WriteLine("No store at " + home.DatabasePath + " — nothing to report.");
            return 0;
        }

        var syncRoot = settings.ResolveDir(home);
        var machineId = Sync.TryReadMachineId(home.SyncDir);

        using var connection = EngramDatabase.OpenInitialized(home);
        var status = Sync.Status(connection, syncRoot, machineId ?? string.Empty);

        if (!settings.Enabled)
        {
            stdout.WriteLine(
                "[sync] enabled = false — automatic export/import/compact are off; this machine "
                    + "will not send or receive updates until it is turned on.");
        }

        stdout.WriteLine("This machine: " + (machineId ?? "(not yet exported)") + " (" + syncRoot + ")");

        if (status.PendingByMachine.Count == 0)
        {
            stdout.WriteLine("No pending chunks from any peer machine.");
        }
        else
        {
            foreach (var (peer, pending) in status.PendingByMachine)
            {
                stdout.WriteLine($"  {peer}: {pending} pending chunk(s).");
            }
        }

        stdout.WriteLine($"Deferred closes: {status.DeferredCount}.");
        stdout.WriteLine($"Stalled closes: {status.StalledCount}.");
        stdout.WriteLine($"Conflicted closes: {status.ConflictCount}.");

        var observations = Sync.GatherPeerObservations(connection, syncRoot, machineId ?? string.Empty);
        var staleness = SyncStaleness.Evaluate(observations, DateTimeOffset.UtcNow, TimeSpan.FromDays(settings.StaleAfterDays));

        if (staleness.Count == 0)
        {
            stdout.WriteLine("No known peer machines.");
        }
        else
        {
            stdout.WriteLine($"Peers (stale past {settings.StaleAfterDays}d):");
            foreach (var peer in staleness)
            {
                var seen = peer.LastObservedUtc is { } observed
                    ? $"last observed {FormatAge(DateTimeOffset.UtcNow - observed)} ago"
                    : "never observed";
                stdout.WriteLine($"  {peer.MachineId}: {seen}{(peer.IsStale ? " STALE" : string.Empty)}");
            }
        }

        return 0;
    }

    private static int Compact(EngramHome home, SyncSettings settings, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var ifLarge = args.Contains("--if-large");

        var syncRoot = settings.ResolveDir(home);
        // Same D49 reasoning as Export/Import: no write on a dry run, including the id file itself.
        var machineId = apply ? Sync.ResolveMachineId(home.SyncDir) : Sync.TryReadMachineId(home.SyncDir) ?? Sync.GenerateMachineId();
        var retain = TimeSpan.FromDays(settings.RetainDays);
        var now = DateTimeOffset.UtcNow;

        if (ifLarge)
        {
            var probe = Sync.Compact(syncRoot, machineId, apply: false, retain, now);
            if (probe.ChunkFilesBefore <= Sync.CompactThreshold)
            {
                stdout.WriteLine("Skipped: this machine's own chunk history is not large enough to compact yet.");
                return 0;
            }
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: now.ToString("o"), SessionId: CliSessionId,
            Kind: TelemetryEventKind.Sync, Phase: "started"));

        Sync.CompactResult result;
        try
        {
            result = Sync.Compact(syncRoot, machineId, apply, retain, now);
        }
        catch (IOException exception)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"), SessionId: CliSessionId,
                Kind: TelemetryEventKind.Sync, Phase: "failed"));
            stderr.WriteLine("error: compact failed and nothing was written — " + exception.Message);
            return 1;
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTimeOffset.UtcNow.ToString("o"), SessionId: CliSessionId,
            Kind: TelemetryEventKind.Sync, Phase: "finished"));

        stdout.WriteLine(
            $"{(apply ? "Chunk files" : "Would rewrite chunk files")}: {result.ChunkFilesBefore} -> {result.ChunkFilesAfter} "
                + $"({FormatBytes(result.BytesBefore)} -> {FormatBytes(result.BytesAfter)}) as machine {machineId}.");
        stdout.WriteLine(
            $"  {result.LiveCount} live fact(s) kept, {result.RetainedClosedCount} closed fact(s) within "
                + $"{settings.RetainDays}d kept, {result.Dropped.Count} closed fact(s) older than {settings.RetainDays}d dropped.");

        if (result.Dropped.Count > 0)
        {
            stdout.WriteLine(
                "  Dropped from this machine's future sync history (a peer that already caught up keeps "
                    + "them locally forever; a peer reconnecting after longer than retain_days will not receive them):");
            foreach (var identity in result.Dropped)
            {
                stdout.WriteLine($"    {identity.Subject} / {identity.Predicate} = \"{identity.Body}\"");
            }
        }

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was written. Re-run with --apply to compact.");
        }

        return 0;
    }

    /// <summary>Coarse day/hour/minute/second age, matching the granularity <c>doctor</c> reports elsewhere.</summary>
    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalDays >= 1 ? $"{(int)age.TotalDays}d"
            : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h"
            : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m"
            : $"{(int)age.TotalSeconds}s";
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0} KB"
        : $"{bytes} B";

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown sync subcommand '{subcommand}'. Expected export, import, status, or compact.");
        return 1;
    }
}
