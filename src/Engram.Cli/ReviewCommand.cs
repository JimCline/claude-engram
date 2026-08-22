using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram review</c> — inspect and clear review-due markers
/// (docs/memory-expansion/04-lifecycle-spec.md). <c>list</c> is read-only; <c>clear</c> is
/// dry-run by default, like everything else that changes the store (D49).
/// </summary>
internal static class ReviewCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var subcommand = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "list";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        return subcommand switch
        {
            "list" => List(homePath, stdout, stderr),
            "clear" => Clear(homePath, rest, stdout, stderr),
            _ => Unknown(subcommand, stderr),
        };
    }

    private static int List(string? homePath, TextWriter stdout, TextWriter stderr)
    {
        var home = EngramHome.ResolveFromProcess(homePath);

        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        using var connection = EngramDatabase.Open(home);
        var rows = FactReview.ListLive(connection);

        if (rows.Count == 0)
        {
            stdout.WriteLine("Nothing has a review date set.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (entry, body) in rows)
        {
            var due = DateTimeOffset.FromUnixTimeSeconds(entry.ReviewAfter);
            var status = due <= now ? "due" : "not yet due";
            stdout.WriteLine($"[{FactCatalog.HandleFor(entry.FactId)}] {body} — {status} ({MomentText.Local(entry.ReviewAfter)})");
        }

        return 0;
    }

    private static int Clear(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var idText = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (idText is null)
        {
            stderr.WriteLine("error: 'engram review clear' needs a fact id, e.g. 'engram review clear f42'");
            return 1;
        }

        if (!FactCatalog.TryParseHandle(idText, out var factId))
        {
            stderr.WriteLine($"error: '{idText}' is not a fact handle; they look like 'f42'.");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        using var connection = apply ? EngramDatabase.OpenInitialized(home) : EngramDatabase.Open(home);

        var exists = FactReview.ListLive(connection).Any(row => row.Entry.FactId == factId);
        if (!exists)
        {
            stdout.WriteLine($"[{idText}] has no review date set — nothing to clear.");
            return 0;
        }

        if (!apply)
        {
            stdout.WriteLine($"Would clear the review date on [{idText}].");
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was written. Re-run with --apply to clear it.");
            return 0;
        }

        FactReview.Clear(connection, null, factId);
        stdout.WriteLine($"Cleared the review date on [{idText}].");
        return 0;
    }

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown review subcommand '{subcommand}'. Expected list or clear.");
        return 2;
    }
}
