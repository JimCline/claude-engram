using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// Reports and compacts the <c>file-touched</c> edit queue.
/// </summary>
/// <remarks>
/// <c>status</c> and a dry-run <c>compact</c> are the same analysis printed the same way, because
/// the only honest way to say what compaction would do is to do the work and stop before deleting.
/// A separate cheaper status would be a second implementation free to disagree with the first.
/// </remarks>
internal static class QueueCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var home = EngramHome.ResolveFromProcess(homePath);

        var subcommand = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "status";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        return subcommand switch
        {
            "status" => Report(home, rest, apply: false, stdout),
            "compact" => Report(home, rest, apply: rest.Contains("--apply"), stdout),
            _ => Unknown(subcommand, stderr),
        };
    }

    private static int Report(EngramHome home, string[] args, bool apply, TextWriter stdout)
    {
        // Asked for outright, the threshold is not what the caller wants — it exists to keep the
        // automatic pass free, not to refuse a person who typed the command.
        var result = SpoolCompactor.Compact(home.QueueDir, apply, force: !args.Contains("--if-large"));

        if (result.Before == 0)
        {
            stdout.WriteLine("The edit queue is empty.");
            return 0;
        }

        stdout.WriteLine($"{home.QueueDir}: {Entries(result.Before)} spooled by file-touched.");

        if (result.Paths > 0 || result.Pathless)
        {
            var naming = result.Paths > 0 ? $"{result.Paths} distinct {Files(result.Paths)}" : null;
            var anonymous = result.Pathless ? "1 entry that does not say which file" : null;
            stdout.WriteLine("  covering " + string.Join(", and ", new[] { naming, anonymous }.OfType<string>()) + ".");
        }

        if (!result.Changes)
        {
            stdout.WriteLine();
            stdout.WriteLine("Nothing to compact — every entry names a file no later entry supersedes.");
            return 0;
        }

        stdout.WriteLine();
        var verb = apply ? "Removed" : "Would remove";
        stdout.WriteLine($"{verb} {result.Superseded} superseded, {result.Unparseable} unreadable as an entry"
            + (result.Dropped > 0 ? $", {result.Dropped} past the {SpoolCompactor.MaxPaths}-file ceiling" : string.Empty)
            + $" — leaving {Entries(result.Kept)}.");

        if (result.Unreadable > 0)
        {
            stdout.WriteLine($"{Entries(result.Unreadable)} could not be opened, and were left alone rather than deleted.");
        }

        if (!apply)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was deleted. Re-run with --apply to compact.");
        }

        return 0;
    }

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown queue subcommand '{subcommand}'. Expected status or compact.");
        return 2;
    }

    private static string Entries(int count) => count == 1 ? "1 entry" : $"{count} entries";

    private static string Files(int count) => count == 1 ? "file" : "files";
}
