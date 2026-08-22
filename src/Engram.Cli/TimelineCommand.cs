using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

/// <summary>
/// <c>engram timeline &lt;fact-id&gt; [--before N] [--after N]</c> — a chronological window of
/// facts around one fact, across every subject: "what else was I recording around this time"
/// (docs/memory-expansion/05-browse-tui-spec.md). Deliberately not a per-entity history — that
/// is <c>engram_expand ... history</c> (D57) — this crosses subjects on purpose.
/// </summary>
internal static class TimelineCommand
{
    private const int DefaultWindow = 5;
    private const string CliSessionId = "cli";

    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var idText = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (idText is null)
        {
            stderr.WriteLine("error: 'engram timeline' needs a fact id, e.g. 'engram timeline f42'");
            return 1;
        }

        if (!FactCatalog.TryParseHandle(idText, out var factId))
        {
            stderr.WriteLine($"error: '{idText}' is not a fact handle; they look like 'f42'.");
            return 1;
        }

        if (ReadWindow(args, "--before", stderr) is not { } before
            || ReadWindow(args, "--after", stderr) is not { } after)
        {
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        using var connection = EngramDatabase.Open(home);
        var anchor = FactStore.ReadById(connection, factId);
        if (anchor is null)
        {
            stdout.WriteLine($"No fact '{idText}' in this store.");
            return 0;
        }

        WriteWindow(stdout, connection, anchor, before, after);

        if (File.Exists(home.ConfigPath))
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                SessionId: CliSessionId,
                Kind: TelemetryEventKind.Timeline,
                Query: idText));
        }

        return 0;
    }

    /// <summary>
    /// Renders the window around <paramref name="anchor"/>. Internal so <c>engram browse</c>'s
    /// timeline keybinding shows exactly the same thing this verb prints, rather than a second
    /// rendering of the same query (docs/memory-expansion/05-browse-tui-spec.md).
    /// </summary>
    internal static void WriteWindow(TextWriter stdout, SqliteConnection connection, StoredFact anchor, int before, int after)
    {
        var (beforeFacts, afterFacts) = FactStore.Timeline(connection, anchor, before, after);

        foreach (var fact in beforeFacts)
        {
            WriteRow(stdout, fact, isAnchor: false);
        }

        WriteRow(stdout, anchor, isAnchor: true);

        foreach (var fact in afterFacts)
        {
            WriteRow(stdout, fact, isAnchor: false);
        }
    }

    private static void WriteRow(TextWriter stdout, StoredFact fact, bool isAnchor)
    {
        var marker = isAnchor ? "→ " : "  ";
        stdout.WriteLine(
            $"{marker}[{FactCatalog.HandleFor(fact.Id)}] {MomentText.Local(fact.ValidFrom)} {fact.SubjectPath} {fact.Predicate}: {fact.Body}");
    }

    private static int? ReadWindow(string[] args, string flag, TextWriter stderr)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != flag)
            {
                continue;
            }

            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var value) || value < 0)
            {
                stderr.WriteLine($"error: {flag} needs a non-negative number");
                return null;
            }

            return value;
        }

        return DefaultWindow;
    }
}
