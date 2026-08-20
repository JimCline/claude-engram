using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram directive</c> — the only authoring path for a Tier-2 directive (D-2, D-10: no MCP
/// write path, no promotion from a captured instruction). <c>add</c> acts immediately, since it
/// can only ever create; <c>remove</c> and <c>revise</c> are dry-run first (D49).
/// </summary>
public static class DirectiveCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            stderr.WriteLine("error: expected a subcommand — add, list, remove, or revise.");
            return 2;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var rest = args[1..];

        return args[0] switch
        {
            "add" => Add(home, rest, stdout, stderr),
            "list" => List(home, rest, stdout, stderr),
            "remove" => Remove(home, rest, stdout, stderr),
            "revise" => Revise(home, rest, stdout, stderr),
            _ => Unknown(args[0], stderr),
        };
    }

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown subcommand '{subcommand}' — expected add, list, remove, or revise.");
        return 2;
    }

    private static int Add(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            stderr.WriteLine("""Usage: engram directive add "<text>" """);
            return 2;
        }

        var statement = args[0].Trim();
        var now = DateTimeOffset.UtcNow;

        using var connection = EngramDatabase.OpenInitialized(home);

        var currentTotal = DirectiveFacts.ReadLive(connection).Sum(d => TokenEstimator.Estimate(d.Body));
        var cost = TokenEstimator.Estimate(statement);

        if (currentTotal + cost > DirectiveFacts.MaxDirectiveTokens)
        {
            stderr.WriteLine(
                $"error: refused — directives are at {currentTotal}/{DirectiveFacts.MaxDirectiveTokens} tokens; "
                    + $"this one costs {cost} more, which would overrun the cap. Retire one first with "
                    + "'engram directive remove <id> --apply', or shorten the statement.");
            return 1;
        }

        var factId = DirectiveFacts.Add(connection, statement, now);

        stdout.WriteLine($"[{FactCatalog.HandleFor(factId)}] added: \"{statement}\"");
        stdout.WriteLine("Takes effect at the next context reset (session start or subagent spawn), not immediately.");
        return 0;
    }

    private static int List(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var all = args.Contains("--all");

        using var connection = EngramDatabase.OpenInitialized(home);
        var directives = all ? DirectiveFacts.ReadAll(connection) : DirectiveFacts.ReadLive(connection);

        if (directives.Count == 0)
        {
            stdout.WriteLine(all ? "No directives, live or retired." : "No live directives.");
            return 0;
        }

        var runningTotal = 0;
        foreach (var directive in directives)
        {
            var cost = TokenEstimator.Estimate(directive.Body);
            var status = directive.ValidTo is null
                ? string.Empty
                : directive.SupersededBy is { } newId
                    ? $" (retired, superseded by [{FactCatalog.HandleFor(newId)}])"
                    : " (retired)";

            if (directive.ValidTo is null)
            {
                runningTotal += cost;
            }

            stdout.WriteLine($"[{FactCatalog.HandleFor(directive.Id)}] {cost}t: \"{directive.Body}\"{status}");
        }

        stdout.WriteLine($"Total: {runningTotal}/{DirectiveFacts.MaxDirectiveTokens} tokens (live only).");
        return 0;
    }

    private static int Remove(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var positional = args.Where(a => a != "--apply").ToArray();

        if (positional.Length != 1 || !FactCatalog.TryParseHandle(positional[0], out var factId))
        {
            stderr.WriteLine("""Usage: engram directive remove <id> --apply""");
            return 2;
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        if (!TryReadLiveDirective(connection, factId, out var target))
        {
            stderr.WriteLine($"error: no live directive with id '{positional[0]}'.");
            return 1;
        }

        if (!apply)
        {
            stdout.WriteLine($"Would retire [{positional[0]}]: \"{target.Body}\"");
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to retire it.");
            return 0;
        }

        FactStore.Forget(connection, factId, "retired via engram directive remove", DateTimeOffset.UtcNow);

        stdout.WriteLine($"Retired [{positional[0]}]: \"{target.Body}\"");
        return 0;
    }

    private static int Revise(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = args.Contains("--apply");
        var positional = args.Where(a => a != "--apply").ToArray();

        if (positional.Length != 2
            || !FactCatalog.TryParseHandle(positional[0], out var factId)
            || string.IsNullOrWhiteSpace(positional[1]))
        {
            stderr.WriteLine("""Usage: engram directive revise <id> "<text>" --apply""");
            return 2;
        }

        var statement = positional[1].Trim();

        using var connection = EngramDatabase.OpenInitialized(home);

        if (!TryReadLiveDirective(connection, factId, out var target))
        {
            stderr.WriteLine($"error: no live directive with id '{positional[0]}'.");
            return 1;
        }

        var currentTotal = DirectiveFacts.ReadLive(connection).Sum(d => TokenEstimator.Estimate(d.Body));
        var projectedTotal = currentTotal - TokenEstimator.Estimate(target.Body) + TokenEstimator.Estimate(statement);

        if (projectedTotal > DirectiveFacts.MaxDirectiveTokens)
        {
            stderr.WriteLine(
                $"error: refused — revising would bring directives to "
                    + $"{projectedTotal}/{DirectiveFacts.MaxDirectiveTokens} tokens. Retire one first, or shorten "
                    + "the statement.");
            return 1;
        }

        if (!apply)
        {
            stdout.WriteLine($"Would replace [{positional[0]}] \"{target.Body}\" with \"{statement}\".");
            stdout.WriteLine();
            stdout.WriteLine("Dry run only — nothing was changed. Re-run with --apply to revise it.");
            return 0;
        }

        // Keeps the existing path rather than recomputing one from the new text: PathFor is
        // only for a brand new directive (D-7). Reusing FactStore.Remember on the same
        // (subject, predicate) is what closes the old row and links superseded_by — the same
        // mechanism every other revise in this codebase uses, and the only way the two versions
        // stay on one thread for FactStore.History/VersionCounts (D57), which addresses a
        // thread by path.
        var result = FactStore.Remember(
            connection,
            new FactWrite(
                SubjectPath: target.SubjectPath,
                SubjectKind: DirectiveFacts.Kind,
                Predicate: DirectiveFacts.Predicate,
                Body: statement,
                Scope: DirectiveFacts.Scope,
                LearnedVia: DirectiveFacts.LearnedVia,
                Regenerable: false),
            DateTimeOffset.UtcNow,
            reason: "revised via engram directive revise");

        stdout.WriteLine($"[{FactCatalog.HandleFor(result.FactId)}] revised [{positional[0]}]: \"{statement}\"");
        return 0;
    }

    /// <summary>
    /// A directive verb may only ever read, close, or revise a fact that is actually a live
    /// directive — never an arbitrary fact id someone typed, and never a captured instruction
    /// (predicate <c>requires</c>), which stays governed entirely by <c>engram_forget</c>.
    /// </summary>
    private static bool TryReadLiveDirective(
        Microsoft.Data.Sqlite.SqliteConnection connection, long factId, out StoredFact target)
    {
        var fact = FactStore.ReadById(connection, factId);

        if (fact is not null
            && fact.ValidTo is null
            && fact.Predicate == DirectiveFacts.Predicate
            && fact.SubjectPath.StartsWith(DirectiveFacts.Root + "/", StringComparison.Ordinal))
        {
            target = fact;
            return true;
        }

        target = null!;
        return false;
    }
}
