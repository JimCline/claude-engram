namespace Engram.Cli;

/// <summary>
/// One menu entry: the canonical value, the label shown, the one-line tradeoff beside it, and
/// optionally the longer prose shown for whichever entry is selected.
/// </summary>
public sealed record TuiChoice(string Value, string Label, string Description, string? Detail = null);

/// <summary>
/// The CLI's interactive dress: arrow-key menus and styled prompts on a real terminal,
/// and nothing anywhere else.
/// </summary>
/// <remarks>
/// <para>Hand-rolled ANSI rather than a TUI package on purpose: binary size is a latency
/// decision — every hook pays this executable's process start — and a terminal library
/// buys nothing here that forty escape codes do not.</para>
///
/// <para>The invariant that keeps every existing test honest: a redirected stream reads
/// byte-identical output to what shipped before this class existed. Rich rendering
/// requires a real console on both ends and a TERM that is not dumb; everything else —
/// every test, every pipe, every hook — takes the plain path, whose prompt strings are
/// supplied by the caller precisely so they can stay frozen.</para>
///
/// <para><b>Every rendered row must fit the terminal.</b> Redrawing is
/// <c>\x1b[{n}A</c> — move up n rows — and clearing is <c>\x1b[2K</c>, which clears one
/// row. Both count *physical* rows, so a line the terminal wraps makes the menu occupy
/// more rows than the redraw moves back over, and each keypress repaints lower down the
/// screen than the last. That is what shipped: the model menu's entries ran to about 290
/// characters against a redraw of one row per choice, so at 80 columns each entry took
/// four rows and the menu marched down the screen, clearing one row in four and leaving
/// the visible <c>❯</c> on a stale copy while the real index moved on — the reported
/// "options repeat, formatting is wrong, and it picked one I did not choose", all from
/// this one assumption. So rows are budgeted rather than hoped for: every line is clipped
/// to the width, the detail block is a fixed height, and <see cref="Render"/> returns the
/// count it actually wrote so the next redraw moves back exactly that far.</para>
/// </remarks>
public sealed class Tui
{
    /// <summary>The no-op instance: plain prompts, no escapes. What tests and pipes get.</summary>
    public static readonly Tui Plain = new(interactive: false, ansi: false, width: () => DefaultWidth);

    /// <summary>Rows reserved for the selected entry's prose. Fixed so the redraw arithmetic is exact.</summary>
    private const int DetailRows = 2;

    private const int DefaultWidth = 80;

    /// <summary>Below this a menu cannot be drawn sensibly, so the width is treated as unknown.</summary>
    private const int MinimumWidth = 24;

    private readonly Func<int> width;

    private Tui(bool interactive, bool ansi, Func<int> width)
    {
        Interactive = interactive;
        Ansi = ansi;
        this.width = width;
    }

    public bool Interactive { get; }

    public bool Ansi { get; }

    public static Tui Detect()
    {
        var term = Environment.GetEnvironmentVariable("TERM");
        var capable = term is { Length: > 0 } && term != "dumb";
        var interactive = capable && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 };
        return new Tui(interactive, interactive && !noColor, ConsoleWidth);
    }

    /// <summary>A rich instance of a fixed width, for tests that assert on what is drawn.</summary>
    internal static Tui ForTest(int columns, bool ansi = true) => new(interactive: true, ansi, () => columns);

    /// <summary>
    /// One choice from a list. Rich mode renders an arrow-key menu with the descriptions
    /// beside the labels; plain mode writes <paramref name="plainPrompt"/> verbatim and
    /// maps the typed answer through <paramref name="plainMap"/>, which is where a
    /// caller's legacy prompt semantics live unchanged. Returns null when the user backs
    /// out (Esc/q rich, whatever <paramref name="plainMap"/> says plain).
    /// </summary>
    public string? Menu(
        TextReader stdin,
        TextWriter stdout,
        string title,
        IReadOnlyList<TuiChoice> choices,
        string plainPrompt,
        Func<string, string?> plainMap)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(plainMap);

        if (!Interactive)
        {
            stdout.Write(plainPrompt);
            stdout.Flush();
            return plainMap((stdin.ReadLine() ?? string.Empty).Trim());
        }

        stdout.WriteLine(Paint(title, "1;36"));
        stdout.WriteLine(Paint("  ↑/↓ then Enter · a number picks directly · Esc leaves it alone", "2"));

        var index = 0;
        stdout.Write("\x1b[?25l");
        try
        {
            var rows = Draw(stdout, choices, index, previousRows: 0);
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
                        return choices[index].Value;
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        return null;
                    default:
                        if (key.KeyChar >= '1' && key.KeyChar - '1' < choices.Count)
                        {
                            index = key.KeyChar - '1';
                            Draw(stdout, choices, index, rows);
                            return choices[index].Value;
                        }

                        continue;
                }

                rows = Draw(stdout, choices, index, rows);
            }
        }
        finally
        {
            stdout.Write("\x1b[?25h");
            stdout.WriteLine();
            stdout.Flush();
        }
    }

    /// <summary>
    /// Draws a block of lines in place, replacing the previous <paramref name="previousRows"/>, and
    /// returns how many rows it wrote.
    /// </summary>
    /// <remarks>
    /// The same contract as <see cref="Render"/> and for the same reason (D52): one logical line is
    /// one physical row, so every line is clipped to the width and the caller feeds the count back.
    /// A separate method rather than a reuse of <c>Render</c> because that one owns the menu's
    /// marker-and-label layout, and widening it to serve both is how a layout change starts breaking
    /// a redraw somewhere else.
    /// </remarks>
    internal int Frame(TextWriter stdout, IReadOnlyList<string> lines, int previousRows)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(lines);

        if (previousRows > 0)
        {
            stdout.Write($"\x1b[{previousRows}A");
        }

        var room = Room;
        var rows = 0;

        foreach (var line in lines)
        {
            stdout.WriteLine(Ansi ? "\x1b[2K" + Clip(line, room) : Clip(line, room));
            rows++;
        }

        stdout.Flush();
        return rows;
    }

    /// <summary>A free-text question. Plain mode is byte-identical to a bare prompt.</summary>
    public string Line(TextReader stdin, TextWriter stdout, string prompt)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);

        stdout.Write(Interactive ? Paint(prompt, "1") : prompt);
        stdout.Flush();
        return (stdin.ReadLine() ?? string.Empty).Trim();
    }

    /// <summary>
    /// Draws one frame exactly as <see cref="Menu"/> does, and returns the rows it took.
    /// The seam the redraw invariant is tested through — <see cref="Menu"/> itself blocks on
    /// <c>Console.ReadKey</c>, which no test can feed, so without this the rich path can only
    /// be checked by eye.
    /// </summary>
    internal int Draw(TextWriter stdout, IReadOnlyList<TuiChoice> choices, int index, int previousRows)
    {
        var labelWidth = 0;
        foreach (var choice in choices)
        {
            labelWidth = Math.Max(labelWidth, choice.Label.Length);
        }

        return Render(stdout, choices, index, labelWidth, previousRows);
    }

    /// <summary>Draws the menu and returns how many rows it occupied.</summary>
    /// <remarks>
    /// The return value is the contract: the caller feeds it back as
    /// <paramref name="previousRows"/> so the redraw moves up exactly as far as the last
    /// draw came down. Nothing here may emit a row it does not count, and nothing may emit
    /// a line longer than the width, because either one breaks that correspondence.
    /// </remarks>
    private int Render(
        TextWriter stdout,
        IReadOnlyList<TuiChoice> choices,
        int index,
        int labelWidth,
        int previousRows)
    {
        if (previousRows > 0)
        {
            stdout.Write($"\x1b[{previousRows}A");
        }

        var room = Room;
        var rows = 0;

        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var marker = i == index ? " ❯ " : "   ";

            // Both halves are clipped, and the head first: a label long enough to fill the row
            // on its own — or any label at all on a narrow terminal — overflows just as surely
            // as a long description, and clipping only the description leaves that unbounded.
            var head = Clip(marker + choice.Label.PadRight(labelWidth + 2), room);
            var description = Clip(choice.Description, room - head.Length);

            var line = i == index
                ? Paint(head, "1;36") + Paint(description, "36")
                : head + Paint(description, "2");

            stdout.WriteLine("\x1b[2K" + line);
            rows++;
        }

        if (choices.Any(c => !string.IsNullOrEmpty(c.Detail)))
        {
            foreach (var line in Fold(choices[index].Detail ?? string.Empty, room - 2, DetailRows))
            {
                stdout.WriteLine("\x1b[2K" + (line.Length == 0 ? string.Empty : Paint("  " + line, "2")));
                rows++;
            }
        }

        stdout.Flush();
        return rows;
    }

    /// <summary>
    /// Columns anything may write into: one short of the terminal, because writing the last cell
    /// makes some terminals wrap immediately and others defer it, and that difference is a row the
    /// redraw arithmetic cannot see.
    /// </summary>
    internal int Room => Math.Max(MinimumWidth, width()) - 1;

    /// <summary>One line, never wider than <paramref name="room"/>, ellipsed if it was.</summary>
    private static string Clip(string text, int room)
    {
        if (room <= 0)
        {
            return string.Empty;
        }

        return text.Length <= room ? text : string.Concat(text.AsSpan(0, room - 1), "…");
    }

    /// <summary>
    /// Word-wraps into exactly <paramref name="lines"/> lines of at most
    /// <paramref name="room"/>, padding with empty ones and ellipsing what does not fit.
    /// Exactly, because the row count is what the redraw is going to move back over.
    /// </summary>
    private static IReadOnlyList<string> Fold(string text, int room, int lines)
    {
        var folded = new List<string>();
        var remaining = text.AsSpan().Trim();

        while (folded.Count < lines && remaining.Length > 0)
        {
            if (remaining.Length <= room)
            {
                folded.Add(remaining.ToString());
                break;
            }

            // The last line takes an ellipsis rather than a word break: there is no next line
            // for the rest to go on, so breaking cleanly would just hide that it was cut.
            if (folded.Count == lines - 1)
            {
                folded.Add(Clip(remaining.ToString(), room));
                break;
            }

            var brk = remaining[..(room + 1)].LastIndexOf(' ');
            if (brk <= 0)
            {
                brk = room;
            }

            folded.Add(remaining[..brk].ToString());
            remaining = remaining[brk..].TrimStart();
        }

        while (folded.Count < lines)
        {
            folded.Add(string.Empty);
        }

        return folded;
    }

    private static int ConsoleWidth()
    {
        try
        {
            var columns = Console.WindowWidth;
            return columns >= MinimumWidth ? columns : DefaultWidth;
        }
        catch (IOException)
        {
            return DefaultWidth;
        }
        catch (PlatformNotSupportedException)
        {
            return DefaultWidth;
        }
    }

    private string Paint(string text, string code) => Ansi ? $"\x1b[{code}m{text}\x1b[0m" : text;
}
