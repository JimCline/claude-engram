using System.Text.RegularExpressions;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The arrow-key menu's redraw arithmetic.
/// </summary>
/// <remarks>
/// This is the coverage whose absence let the picker ship broken. Every other test drives
/// redirected streams and so takes <see cref="Tui.Plain"/>, and the one pty test presses Enter
/// on the first menu — which selects "none" without ever pressing an arrow key, so it never
/// triggers a redraw and never reaches the model menu whose entries were the long ones. The
/// bug was structurally unreachable by the whole suite.
///
/// What is asserted is the invariant the escapes depend on: <c>\x1b[{n}A</c> and <c>\x1b[2K</c>
/// both count physical rows, so a menu may never emit a line the terminal would wrap, and a
/// redraw must move up exactly as far as the previous draw came down.
/// </remarks>
public class TuiRenderTests
{
    private const int Columns = 80;

    private static readonly Regex Ansi = new(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    private static string Strip(string text) => Ansi.Replace(text, string.Empty);

    private static IReadOnlyList<string> VisibleLines(string output) =>
        [.. Strip(output).Split('\n')];

    /// <summary>The shape that broke it: a spec line plus a paragraph, on one entry.</summary>
    private static IReadOnlyList<TuiChoice> Overlong() =>
    [
        new("a", "all-minilm-l6-v2", "384d · 25 MB · 256-token window · English", new string('x', 260)),
        new("b", "nomic-embed-text-v1.5", "768d · 146 MB · 8k-token window · English", new string('y', 260)),
        new("c", "qwen3-embedding-0.6b", "1024d · 639 MB · 32k-token window · 100+ languages", new string('z', 260)),
    ];

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void Draw_EntriesFarWiderThanTheTerminal_StillFitEveryRow(int columns)
    {
        var writer = new StringWriter();

        Tui.ForTest(columns).Draw(writer, Overlong(), index: 0, previousRows: 0);

        foreach (var line in VisibleLines(writer.ToString()))
        {
            Assert.True(
                line.Length < columns,
                $"a rendered row was {line.Length} of {columns} columns and would wrap: {line}");
        }
    }

    // The return value is the contract the redraw is built on. A draw that emits a row it does
    // not count leaves the next redraw short by exactly that many rows, which is the defect.
    [Fact]
    public void Draw_ReturnsExactlyTheRowsItWrote()
    {
        var writer = new StringWriter();

        var rows = Tui.ForTest(Columns).Draw(writer, Overlong(), index: 0, previousRows: 0);

        Assert.Equal(writer.ToString().Count(c => c == '\n'), rows);
    }

    [Fact]
    public void Draw_Redraw_MovesUpExactlyAsFarAsTheLastDrawCameDown()
    {
        var tui = Tui.ForTest(Columns);
        var first = new StringWriter();
        var rows = tui.Draw(first, Overlong(), index: 0, previousRows: 0);

        var second = new StringWriter();
        tui.Draw(second, Overlong(), index: 1, previousRows: rows);

        Assert.StartsWith($"\x1b[{rows}A", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(rows, second.ToString().Count(c => c == '\n'));
    }

    // Prose of any length costs the same rows, so the count cannot drift with the selection.
    // A variable-height detail block would reintroduce the original bug by another route.
    [Fact]
    public void Draw_DetailBlock_IsTheSameHeightWhicheverEntryIsSelected()
    {
        var tui = Tui.ForTest(Columns);
        var choices = new List<TuiChoice>
        {
            new("a", "short", "spec", "tiny"),
            new("b", "long", "spec", new string('w', 400)),
        };

        var counts = Enumerable.Range(0, choices.Count)
            .Select(i =>
            {
                var writer = new StringWriter();
                return tui.Draw(writer, choices, i, previousRows: 0);
            })
            .Distinct()
            .ToList();

        Assert.Single(counts);
    }

    [Fact]
    public void Draw_NoEntryHasDetail_SpendsNoRowsOnTheBlock()
    {
        var tui = Tui.ForTest(Columns);
        var bare = new List<TuiChoice> { new("a", "one", "spec"), new("b", "two", "spec") };

        Assert.Equal(bare.Count, tui.Draw(new StringWriter(), bare, index: 0, previousRows: 0));
    }

    // The menu that actually broke, drawn from the picker's own list rather than a copy of it.
    // Building the choices here instead would make this pass no matter what EmbeddingSetup did,
    // which is exactly how the first version of this test failed to see the defect it was
    // written for.
    [Fact]
    public void Draw_TheRealModelMenu_FitsTheTerminal()
    {
        var writer = new StringWriter();

        var rows = Tui.ForTest(Columns).Draw(writer, EmbeddingSetup.ModelChoices(), index: 0, previousRows: 0);

        Assert.Equal(writer.ToString().Count(c => c == '\n'), rows);
        foreach (var line in VisibleLines(writer.ToString()))
        {
            Assert.True(line.Length < Columns, $"row of {line.Length} columns would wrap: {line}");
        }
    }

    // Clipping stops an over-long entry from corrupting the screen, so the failure mode it
    // leaves behind is silent: the specs get ellipsed away and the menu merely becomes useless.
    // This is what catches paragraph-length prose being put back into Description, which
    // the width assertion above no longer can.
    [Fact]
    public void ModelMenu_SpecsFitBesideTheLabel_WithoutBeingEllipsed()
    {
        var writer = new StringWriter();
        Tui.ForTest(Columns).Draw(writer, EmbeddingSetup.ModelChoices(), index: 0, previousRows: 0);

        foreach (var model in EmbeddingModels.All)
        {
            Assert.Contains(model.Dimensions + "d", Strip(writer.ToString()), StringComparison.Ordinal);
        }

        var menuRows = VisibleLines(writer.ToString()).Take(EmbeddingSetup.ModelChoices().Count);
        Assert.DoesNotContain(menuRows, row => row.Contains('…', StringComparison.Ordinal));
    }

    // ---- Frame: the same row budget, for a block that is not a menu ----

    /// <summary>
    /// Frame inherits D52's contract, so it inherits D52's assertions. A live status block that
    /// redraws itself has precisely the failure mode the model menu had.
    /// </summary>
    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void Frame_LinesWiderThanTheTerminal_StillCostOneRowEach(int columns)
    {
        var writer = new StringWriter();
        string[] lines = [new('a', 400), "short", new('b', 90)];

        var rows = Tui.ForTest(columns).Frame(writer, lines, previousRows: 0);

        Assert.Equal(lines.Length, rows);
        foreach (var written in VisibleLines(writer.ToString()))
        {
            Assert.True(
                written.Length < columns,
                $"a row of {written.Length} columns cannot fit a {columns}-column terminal");
        }
    }

    [Fact]
    public void Frame_MovesUpExactlyAsFarAsTheLastFrameCameDown()
    {
        var tui = Tui.ForTest(80);
        var first = new StringWriter();
        var rows = tui.Frame(first, ["one", "two", "three"], previousRows: 0);

        var second = new StringWriter();
        tui.Frame(second, ["one", "two", "three"], rows);

        Assert.Equal(3, rows);
        Assert.StartsWith("\x1b[3A", second.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The first frame has nothing above it, so it must not move the cursor at all.</summary>
    [Fact]
    public void Frame_TheFirstOne_DoesNotMoveTheCursorUp()
    {
        var writer = new StringWriter();
        Tui.ForTest(80).Frame(writer, ["one"], previousRows: 0);

        Assert.DoesNotMatch(@"\x1b\[\d+A", writer.ToString());
    }
}
