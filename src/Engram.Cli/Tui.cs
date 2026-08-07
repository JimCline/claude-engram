namespace Engram.Cli;

/// <summary>One menu entry: the canonical value, the label shown, and the tradeoff prose.</summary>
public sealed record TuiChoice(string Value, string Label, string Description);

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
/// </remarks>
public sealed class Tui
{
    /// <summary>The no-op instance: plain prompts, no escapes. What tests and pipes get.</summary>
    public static readonly Tui Plain = new(interactive: false, ansi: false);

    public bool Interactive { get; }

    public bool Ansi { get; }

    private Tui(bool interactive, bool ansi)
    {
        Interactive = interactive;
        Ansi = ansi;
    }

    public static Tui Detect()
    {
        var term = Environment.GetEnvironmentVariable("TERM");
        var capable = term is { Length: > 0 } && term != "dumb";
        var interactive = capable && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 };
        return new Tui(interactive, interactive && !noColor);
    }

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

        var labelWidth = 0;
        foreach (var choice in choices)
        {
            labelWidth = Math.Max(labelWidth, choice.Label.Length);
        }

        var index = 0;
        stdout.Write("\x1b[?25l");
        try
        {
            Render(stdout, choices, index, labelWidth, redraw: false);
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
                            Render(stdout, choices, index, labelWidth, redraw: true);
                            return choices[index].Value;
                        }

                        continue;
                }

                Render(stdout, choices, index, labelWidth, redraw: true);
            }
        }
        finally
        {
            stdout.Write("\x1b[?25h");
            stdout.WriteLine();
            stdout.Flush();
        }
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

    private void Render(TextWriter stdout, IReadOnlyList<TuiChoice> choices, int index, int labelWidth, bool redraw)
    {
        if (redraw)
        {
            stdout.Write($"\x1b[{choices.Count}A");
        }

        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var label = choice.Label.PadRight(labelWidth + 2);
            var line = i == index
                ? Paint(" ❯ " + label, "1;36") + Paint(choice.Description, "36")
                : "   " + label + Paint(choice.Description, "2");
            stdout.WriteLine("\x1b[2K" + line);
        }

        stdout.Flush();
    }

    private string Paint(string text, string code) => Ansi ? $"\x1b[{code}m{text}\x1b[0m" : text;
}
