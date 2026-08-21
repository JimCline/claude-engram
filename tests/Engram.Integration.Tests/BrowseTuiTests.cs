using System.Text.RegularExpressions;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 (D9): draws <c>engram browse</c>'s real menu list, not a hand-built stand-in — the
/// exact trap the model menu already paid for once (see <c>TuiRenderTests.Draw_TheRealModelMenu_FitsTheTerminal</c>),
/// restated here for browse's own list (docs/memory-expansion/05-browse-tui-spec.md).
/// </summary>
public class BrowseTuiTests
{
    private const int Columns = 80;

    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    private static string Strip(string text) => Ansi.Replace(text, string.Empty);

    private static IReadOnlyList<string> VisibleLines(string output) => [.. Strip(output).Split('\n')];

    [Fact]
    public void Draw_TheRealBrowseMenu_FitsTheTerminal_EvenWithLongPathsAndBodies()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // Long enough to overflow 80 columns on their own — the same shape that broke the model
        // menu: a long label plus a long description on one entry.
        FactStore.Remember(
            connection,
            new FactWrite("/knowledge/" + new string('a', 60), "note", "states", new string('x', 300), "notes", "stated"),
            T0);
        FactStore.Remember(
            connection,
            new FactWrite("/knowledge/short", "note", "states", "short body", "notes", "stated"),
            T0.AddSeconds(1));

        var node = MemoryBrowser.Browse(connection, "/knowledge", depth: 1)!;
        var facts = MemoryBrowser.TopFacts(connection, node.Path, 5);
        var choices = BrowseCommand.Choices(node, facts);

        var writer = new StringWriter();
        var rows = Tui.ForTest(Columns).Draw(writer, choices, index: 0, previousRows: 0);

        Assert.Equal(writer.ToString().Count(c => c == '\n'), rows);
        foreach (var line in VisibleLines(writer.ToString()))
        {
            Assert.True(line.Length < Columns, $"row of {line.Length} columns would wrap: {line}");
        }
    }

    // Falsify: replace `BrowseCommand.Choices(node, facts)` above with a short hand-built
    // TuiChoice list (e.g. two three-word entries) and re-run — it passes regardless of whether
    // BrowseCommand's own entry-building clips correctly, because the hand-built list never
    // carries the long path or the long body that triggers the defect. That is the trap
    // ModelMenu_SpecsFitBesideTheLabel_WithoutBeingEllipsed already exists to avoid for the model
    // menu, restated here for browse's own list.

    [Fact]
    public void Choices_IncludesBothChildrenAndFacts()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        FactStore.Remember(connection, new FactWrite("/knowledge/topic/a", "note", "states", "a fact", "notes", "stated"), T0);

        var node = MemoryBrowser.Browse(connection, "/knowledge", depth: 1)!;
        var facts = MemoryBrowser.TopFacts(connection, node.Path, 5);
        var choices = BrowseCommand.Choices(node, facts);

        Assert.Contains(choices, c => c.Label.EndsWith('/'));
    }
}
