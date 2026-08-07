using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The one test that sees the rich path. Every other test drives redirected streams,
/// which by design get the frozen plain prompts — so without this, the arrow-key menu
/// would ship forever unexecuted. <c>script(1)</c> lends the published binary a real
/// pty, <c>Tui.Detect</c> answers Interactive, and a piped Enter selects the menu's
/// first option, "none" — the one choice that needs no network and no download.
/// </summary>
public class TuiPtyTests
{
    [Fact]
    public void UnderAPty_ThePickerRendersAMenu_AndEnterPicksNone()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the pty harness uses macOS script(1) argument semantics.");

        var home = Path.Combine(Path.GetTempPath(), "engram-tui-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/script")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add(EndToEndBinary.Path!);
            psi.ArgumentList.Add("init");
            psi.ArgumentList.Add("--with-embeddings");
            psi.Environment["ENGRAM_HOME"] = home;
            psi.Environment["TERM"] = "xterm-256color";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start script");
            process.StandardInput.Write('\r');
            process.StandardInput.Flush();
            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the picker under a pty never exited; the raw-key loop is not reading what script feeds it");
            }

            Assert.Contains("\x1b[", stdout, StringComparison.Ordinal);

            var config = File.ReadAllText(Path.Combine(home, "config.toml"));
            Assert.Contains("provider = \"none\"", config, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// The second menu, and a redraw. The test above presses Enter on the first menu, so it
    /// selects "none" without ever moving the cursor — it never reaches the model menu and
    /// never triggers a redraw, which is why it could not see the picker repeating its options.
    /// This drives '2' (local), an arrow key, then 'q' to back out before anything downloads.
    ///
    /// It asserts the loop survives and backs out cleanly, not the column arithmetic: the pty
    /// that <c>script(1)</c> hands out has whatever width it has, and an assertion on wrapping
    /// needs a width it chose. <c>TuiRenderTests</c> holds that half deterministically.
    /// </summary>
    [Fact]
    public void UnderAPty_TheModelMenuRedrawsAndBacksOutWithoutDownloading()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the pty harness uses macOS script(1) argument semantics.");

        var home = Path.Combine(Path.GetTempPath(), "engram-tui-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/script")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add(EndToEndBinary.Path!);
            psi.ArgumentList.Add("init");
            psi.ArgumentList.Add("--with-embeddings");
            psi.Environment["ENGRAM_HOME"] = home;
            psi.Environment["TERM"] = "xterm-256color";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start script");
            process.StandardInput.Write('2');
            process.StandardInput.Write("\x1b[B");
            process.StandardInput.Write('q');
            process.StandardInput.Flush();
            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the picker never exited; the raw-key loop is not reading what script feeds it");
            }

            Assert.Contains("Which model?", stdout, StringComparison.Ordinal);

            // A cursor-up means a redraw happened rather than the menu being painted once and
            // left. Without one the arrow key did nothing and this test proves only the first.
            Assert.Matches(@"\x1b\[\d+A", stdout);

            Assert.Contains("Left the config alone.", stdout, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.Combine(home, "models"), "*.gguf"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
