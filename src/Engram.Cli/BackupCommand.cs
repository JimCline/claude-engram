using System.Globalization;
using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram backup</c> — take a snapshot, list what exists, thin the old ones, put one back.
/// </summary>
/// <remarks>
/// Deleting and restoring both print what they would do and change nothing until <c>--apply</c>,
/// the same rule <c>repair</c>, <c>compact</c>, <c>forget</c> and the installer follow. Restore is
/// the sharper of the two: it is the one command here that can destroy a store, and it is reached
/// for in exactly the state where a person is least able to double-check themselves.
/// </remarks>
public static class BackupCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var home = EngramHome.ResolveFromProcess(homePath);
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = BackupSettings.Read(config);

        foreach (var problem in settings.Problems)
        {
            stderr.WriteLine("warning: " + problem);
        }

        var subcommand = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "take";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        return subcommand switch
        {
            "take" => Take(home, settings, rest, stdout, stderr),
            "list" => ListSnapshots(home, stdout),
            "prune" => Prune(home, settings, rest, stdout),
            "restore" => Restore(home, rest, stdout, stderr),
            "replay" => Replay(home, rest, stdout, stderr),
            _ => Unknown(subcommand, stderr),
        };
    }

    private static int Take(
        EngramHome home,
        BackupSettings settings,
        string[] args,
        TextWriter stdout,
        TextWriter stderr)
    {
        var ifDue = args.Contains("--if-due");

        if (!File.Exists(home.DatabasePath))
        {
            // Not an error, and deliberately not a created database either. This runs detached
            // from a hook, and a backup command that brings a store into existence would make
            // "engram has never run here" indistinguishable from "engram ran and lost everything".
            stdout.WriteLine("No store at " + home.DatabasePath + " — nothing to snapshot.");
            return 0;
        }

        using var held = BackupStore.TryLock(home);
        if (held is null)
        {
            stdout.WriteLine("Another backup is already running — leaving it to that one.");
            return 0;
        }

        using var connection = EngramDatabase.Open(home);

        if (ifDue)
        {
            var decision = BackupStore.Due(home, connection, settings, DateTimeOffset.UtcNow);
            if (!decision.ShouldTake)
            {
                stdout.WriteLine("Skipped: " + decision.Reason + ".");
                return 0;
            }

            stdout.WriteLine("Taking a snapshot: " + decision.Reason + ".");
        }

        BackupSnapshot snapshot;
        try
        {
            snapshot = BackupStore.Take(connection, home, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            stderr.WriteLine("error: could not write a snapshot — " + exception.Message);
            return 1;
        }

        stdout.WriteLine($"Wrote {snapshot.Name} ({Size(snapshot.Bytes)}).");

        if (settings.Journal)
        {
            try
            {
                var facts = FactJournal.Write(connection, home, DateTimeOffset.UtcNow);
                stdout.WriteLine($"Journalled {facts} {(facts == 1 ? "fact" : "facts")} to {FactJournal.FileName}.");
            }
            catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The snapshot is already on disk and is the tier that matters most. Failing the
                // whole command now would report a backup that did not happen when one did.
                stderr.WriteLine("warning: snapshot written, but the fact journal failed — " + exception.Message);
            }
        }

        var plan = BackupStore.Plan(BackupStore.List(home), settings);
        if (plan.Delete.Count > 0)
        {
            BackupStore.Prune(plan);
            stdout.WriteLine($"Pruned {plan.Delete.Count} older {Snapshots(plan.Delete.Count)}, kept {plan.Keep.Count}.");
        }

        return 0;
    }

    private static int ListSnapshots(EngramHome home, TextWriter stdout)
    {
        var snapshots = BackupStore.List(home);
        if (snapshots.Count == 0)
        {
            stdout.WriteLine("No snapshots in " + home.BackupDir + ".");
            return 0;
        }

        stdout.WriteLine("  taken (UTC)          schema      size  name");
        foreach (var snapshot in snapshots)
        {
            stdout.WriteLine(
                "  "
                    + snapshot.TakenAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).PadRight(21)
                    + ("v" + snapshot.SchemaVersion.ToString(CultureInfo.InvariantCulture)).PadRight(8)
                    + Size(snapshot.Bytes).PadLeft(8)
                    + "  "
                    + snapshot.Name);
        }

        stdout.WriteLine();
        stdout.WriteLine($"{snapshots.Count} {Snapshots(snapshots.Count)}, {Size(snapshots.Sum(s => s.Bytes))} total.");
        return 0;
    }

    private static int Prune(EngramHome home, BackupSettings settings, string[] args, TextWriter stdout)
    {
        var apply = args.Contains("--apply");
        var plan = BackupStore.Plan(BackupStore.List(home), settings);

        if (plan.Delete.Count == 0)
        {
            stdout.WriteLine($"Nothing to prune — {plan.Keep.Count} {Snapshots(plan.Keep.Count)} kept, all inside "
                + $"the {settings.KeepHourly} hourly / {settings.KeepDaily} daily / {settings.KeepWeekly} weekly limits.");
            return 0;
        }

        foreach (var snapshot in plan.Delete)
        {
            stdout.WriteLine((apply ? "  delete " : "  would delete ") + snapshot.Name);
        }

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine($"Dry run only — {plan.Delete.Count} {Snapshots(plan.Delete.Count)} would go, "
                + $"{plan.Keep.Count} would stay. Re-run with --apply to delete them.");
            return 0;
        }

        BackupStore.Prune(plan);
        stdout.WriteLine();
        stdout.WriteLine($"Deleted {plan.Delete.Count}, kept {plan.Keep.Count}.");
        return 0;
    }

    private static int Restore(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var named = args.FirstOrDefault(a => !a.StartsWith('-'));

        var snapshots = BackupStore.List(home);
        if (snapshots.Count == 0)
        {
            stderr.WriteLine("error: no snapshots in " + home.BackupDir + " to restore from.");
            return 1;
        }

        var chosen = named is null
            ? snapshots[0]
            : snapshots.FirstOrDefault(s => string.Equals(s.Name, named, StringComparison.Ordinal));

        if (chosen is null)
        {
            stderr.WriteLine($"error: no snapshot named '{named}'. Run 'engram backup list' to see what exists.");
            return 1;
        }

        if (chosen.SchemaVersion > EngramDatabase.SchemaVersion)
        {
            stderr.WriteLine(
                $"error: {chosen.Name} was written at schema version {chosen.SchemaVersion}, and this binary "
                    + $"understands {EngramDatabase.SchemaVersion}. Refusing to restore it rather than reading it wrongly.");
            return 1;
        }

        stdout.WriteLine((apply ? "Restoring " : "Would restore ") + chosen.Name
            + $" (taken {chosen.TakenAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC, {Size(chosen.Bytes)})");
        stdout.WriteLine((apply ? "  over " : "  over ") + home.DatabasePath);
        stdout.WriteLine(apply
            ? "  keeping the current store as a new snapshot first"
            : "  the current store would be snapshotted first, not discarded");

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to restore.");
            return 0;
        }

        try
        {
            BackupStore.Restore(home, chosen, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            stderr.WriteLine("error: " + exception.Message);
            return 1;
        }

        stdout.WriteLine();
        stdout.WriteLine("Restored. Any running engram server is holding the old store — restart it with 'engram stop' then 'engram start'.");
        return 0;
    }

    /// <remarks>
    /// Additive by design, and that is the difference between this and <c>restore</c>. Restore
    /// replaces a store with an older one; replay reads facts into whatever is already there and
    /// skips the ones it recognises. So it is the safe half of recovery — usable against a live
    /// store, against a half-recovered one, and twice in a row.
    /// </remarks>
    private static int Replay(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var named = args.FirstOrDefault(a => !a.StartsWith('-'));
        var path = named ?? FactJournal.PathIn(home);

        if (!File.Exists(path))
        {
            stderr.WriteLine($"error: no fact journal at {path}. Run 'engram backup take' to write one.");
            return 1;
        }

        IReadOnlyList<JournalFact> facts;
        int skipped;
        try
        {
            facts = FactJournal.Parse(File.ReadLines(path), out skipped);
        }
        catch (IOException exception)
        {
            stderr.WriteLine("error: could not read " + path + " — " + exception.Message);
            return 1;
        }

        if (skipped > 0)
        {
            stderr.WriteLine($"warning: {skipped} unreadable {(skipped == 1 ? "line was" : "lines were")} skipped in {path}.");
        }

        if (facts.Count == 0)
        {
            stdout.WriteLine("Nothing to replay — " + path + " holds no facts.");
            return 0;
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        ReplayResult result;
        try
        {
            result = FactJournal.Replay(connection, facts, apply);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            stderr.WriteLine("error: replay failed and nothing was written — " + exception.Message);
            return 1;
        }

        stdout.WriteLine(
            $"{(apply ? "Wrote" : "Would write")} {result.Written} {(result.Written == 1 ? "fact" : "facts")}"
                + $", leaving {result.AlreadyPresent} already in the store.");

        if (result.Unresolved > 0)
        {
            stdout.WriteLine(
                $"  {result.Unresolved} supersession {(result.Unresolved == 1 ? "link" : "links")} pointed outside "
                    + "this journal; those facts kept their end date but not what replaced them.");
        }

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to write them.");
        }

        return 0;
    }

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine(
            $"error: unknown backup subcommand '{subcommand}'. Expected take, list, prune, restore, or replay.");
        return 2;
    }

    private static string Snapshots(int count) => count == 1 ? "snapshot" : "snapshots";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };
}
