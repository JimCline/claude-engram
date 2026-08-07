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
}
