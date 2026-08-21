using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// <c>engram browse</c> — an interactive path-tree navigator over the same entity tree
/// <c>engram_browse</c> already walks (docs/memory-expansion/05-browse-tui-spec.md). Read-only:
/// it reuses <see cref="MemoryBrowser"/>'s query rather than a second implementation of it, and
/// <see cref="Tui"/>'s row-budget contract (D52) rather than a second renderer. A keybinding on
/// the selected fact jumps into <see cref="TimelineCommand"/>'s window or
/// <see cref="FactStore.History"/> (the same query <c>engram_expand ... history</c> answers,
/// D57) — both are useful next steps from a browse selection, so both are offered.
/// </summary>
internal static class BrowseCommand
{
    private const int FactsPerNode = 5;
    private const int DefaultTimelineWindow = 5;
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";

    private enum ActionKind
    {
        NoOp,
        Quit,
        Up,
        Descend,
        Timeline,
        History,
    }

    private sealed record BrowseAction(ActionKind Kind, string? ChildPath = null, long FactId = 0)
    {
        public static readonly BrowseAction NoOp = new(ActionKind.NoOp);
        public static readonly BrowseAction Quit = new(ActionKind.Quit);
        public static readonly BrowseAction Up = new(ActionKind.Up);

        public static BrowseAction Descend(string path) => new(ActionKind.Descend, ChildPath: path);

        public static BrowseAction Timeline(long factId) => new(ActionKind.Timeline, FactId: factId);

        public static BrowseAction History(long factId) => new(ActionKind.History, FactId: factId);
    }

    private enum EntryKind
    {
        Child,
        Fact,
    }

    private sealed record BrowseEntry(EntryKind Kind, TuiChoice Choice, string? ChildPath = null, long? FactId = null);

    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var home = EngramHome.ResolveFromProcess(homePath);
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        using var connection = EngramDatabase.Open(home);
        return Loop(connection, Console.In, stdout, Tui.Detect());
    }

    /// <summary>The navigation loop, apart from <see cref="Run"/> so a test can drive it without a real terminal.</summary>
    internal static int Loop(SqliteConnection connection, TextReader stdin, TextWriter stdout, Tui tui)
    {
        var path = "/";

        while (true)
        {
            var node = MemoryBrowser.Browse(connection, path, depth: 1);
            if (node is null)
            {
                stdout.WriteLine($"Nothing in memory under {path}.");
                return 0;
            }

            var facts = MemoryBrowser.TopFacts(connection, node.Path, FactsPerNode);
            var entries = Entries(node, facts);

            if (entries.Count == 0)
            {
                stdout.WriteLine($"{node.Path} — nothing here or beneath it.");
                if (path == "/")
                {
                    return 0;
                }

                path = ParentOf(path);
                continue;
            }

            // Routed through Tui.Frame rather than a bare WriteLine: these two lines sit outside
            // Tui.Draw's own clipping, and D52 forbids emitting a row the redraw cannot count
            // regardless of which method wrote it.
            tui.Frame(
                stdout,
                [
                    $"{node.Path} — {CountText(node.FactsHere)} here, {node.FactsUnder} under it",
                    path == "/"
                        ? "  Enter opens · t timeline · h history · q quit"
                        : "  Enter opens · t timeline · h history · b up · q quit",
                ],
                previousRows: 0);

            var action = tui.Interactive ? RunInteractive(stdout, tui, entries) : RunPlain(stdin, stdout, entries);

            switch (action.Kind)
            {
                case ActionKind.Quit:
                    return 0;
                case ActionKind.Up when path != "/":
                    path = ParentOf(path);
                    break;
                case ActionKind.Descend:
                    path = action.ChildPath!;
                    break;
                case ActionKind.Timeline:
                    ShowTimeline(connection, stdout, action.FactId);
                    break;
                case ActionKind.History:
                    ShowHistory(connection, stdout, action.FactId);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// The menu entries for one node, in the shape <see cref="Tui.Draw"/> takes. Public to the
    /// assembly so a test can draw the store's real list rather than a hand-built stand-in — the
    /// D52 pitfall this codebase has already shipped once (docs/memory-expansion/05-browse-tui-spec.md).
    /// </summary>
    internal static IReadOnlyList<TuiChoice> Choices(BrowseNode node, IReadOnlyList<StoredFact> facts) =>
        [.. Entries(node, facts).Select(e => e.Choice)];

    private static IReadOnlyList<BrowseEntry> Entries(BrowseNode node, IReadOnlyList<StoredFact> facts) =>
    [
        .. node.Children.Select(c => new BrowseEntry(
            EntryKind.Child,
            new TuiChoice(c.Path, c.Name + "/", $"{CountText(c.FactsHere)} here, {c.FactsUnder} under it"),
            ChildPath: c.Path)),
        .. facts.Select(f => new BrowseEntry(
            EntryKind.Fact,
            new TuiChoice("f" + f.Id, "[" + FactCatalog.HandleFor(f.Id) + "]", f.Predicate + ": " + f.Body),
            FactId: f.Id)),
    ];

    private static BrowseAction RunInteractive(TextWriter stdout, Tui tui, IReadOnlyList<BrowseEntry> entries)
    {
        var choices = entries.Select(e => e.Choice).ToList();
        var index = 0;

        stdout.Write("\x1b[?25l");
        try
        {
            var rows = tui.Draw(stdout, choices, index, previousRows: 0);
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        index = (index - 1 + choices.Count) % choices.Count;
                        break;
                    case ConsoleKey.DownArrow or ConsoleKey.J:
                        index = (index + 1) % choices.Count;
                        break;
                    case ConsoleKey.Enter:
                        return entries[index].Kind == EntryKind.Child
                            ? BrowseAction.Descend(entries[index].ChildPath!)
                            : BrowseAction.NoOp;
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        return BrowseAction.Quit;
                    case ConsoleKey.B or ConsoleKey.Backspace:
                        return BrowseAction.Up;
                    case ConsoleKey.T when entries[index].Kind == EntryKind.Fact:
                        return BrowseAction.Timeline(entries[index].FactId!.Value);
                    case ConsoleKey.H when entries[index].Kind == EntryKind.Fact:
                        return BrowseAction.History(entries[index].FactId!.Value);
                    default:
                        continue;
                }

                rows = tui.Draw(stdout, choices, index, rows);
            }
        }
        finally
        {
            stdout.Write("\x1b[?25h");
            stdout.WriteLine();
            stdout.Flush();
        }
    }

    private static BrowseAction RunPlain(TextReader stdin, TextWriter stdout, IReadOnlyList<BrowseEntry> entries)
    {
        var children = entries.Where(e => e.Kind == EntryKind.Child).ToList();
        var facts = entries.Where(e => e.Kind == EntryKind.Fact).ToList();

        for (var i = 0; i < children.Count; i++)
        {
            stdout.WriteLine($"  {i + 1}) {children[i].Choice.Label} — {children[i].Choice.Description}");
        }

        for (var i = 0; i < facts.Count && i < Letters.Length; i++)
        {
            stdout.WriteLine($"  {Letters[i]}) {facts[i].Choice.Label} {facts[i].Choice.Description}");
        }

        stdout.Write("browse> [number to open · t<letter>/h<letter> on a fact · b back · q quit] ");
        stdout.Flush();
        var line = (stdin.ReadLine() ?? string.Empty).Trim();

        if (line.Length == 0 || line is "q" or "quit")
        {
            return BrowseAction.Quit;
        }

        if (line is "b" or "back" or "u" or "up")
        {
            return BrowseAction.Up;
        }

        if (int.TryParse(line, out var choice) && choice >= 1 && choice <= children.Count)
        {
            return BrowseAction.Descend(children[choice - 1].ChildPath!);
        }

        if (line.Length >= 2 && line[0] is 't' or 'h')
        {
            var letterIndex = Letters.IndexOf(line[1]);
            if (letterIndex >= 0 && letterIndex < facts.Count)
            {
                var factId = facts[letterIndex].FactId!.Value;
                return line[0] == 't' ? BrowseAction.Timeline(factId) : BrowseAction.History(factId);
            }
        }

        stdout.WriteLine($"'{line}' is not a valid command here.");
        return BrowseAction.NoOp;
    }

    private static void ShowTimeline(SqliteConnection connection, TextWriter stdout, long factId)
    {
        var anchor = FactStore.ReadById(connection, factId);
        if (anchor is null)
        {
            stdout.WriteLine("That fact is gone.");
            return;
        }

        TimelineCommand.WriteWindow(stdout, connection, anchor, DefaultTimelineWindow, DefaultTimelineWindow);
    }

    private static void ShowHistory(SqliteConnection connection, TextWriter stdout, long factId)
    {
        var anchor = FactStore.ReadById(connection, factId);
        if (anchor is null)
        {
            stdout.WriteLine("That fact is gone.");
            return;
        }

        var history = FactStore.History(connection, anchor.SubjectPath, anchor.Predicate);
        var reasons = MemoryBrowser.Reasons(connection, history.Where(f => f.ValidTo is not null).Select(f => f.Id));

        foreach (var fact in history)
        {
            var status = fact.ValidTo is null ? "live" : "closed";
            var reason = fact.ValidTo is not null && reasons.TryGetValue(fact.Id, out var why) ? $" — {why}" : string.Empty;
            stdout.WriteLine($"  [{FactCatalog.HandleFor(fact.Id)}] {MomentText.Local(fact.ValidFrom)} ({status}){reason}: {fact.Body}");
        }
    }

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOfAny(['/', '#']);
        return index <= 0 ? "/" : path[..index];
    }

    private static string CountText(int count) => count == 1 ? "1 fact" : $"{count} facts";
}
