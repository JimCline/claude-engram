using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3 (D9), narrow-terminal-width scenario for <c>engram browse</c>'s interactive path
/// (docs/memory-expansion/05-browse-tui-spec.md). Mirrors <see cref="TuiPtyTests"/>'s harness:
/// <c>script(1)</c> lends the published binary a real pty so <see cref="Engram.Cli.Tui.Detect"/>
/// answers Interactive, and <c>stty</c> narrows that pty before <c>browse</c> starts so the row
/// budget in <c>Engram.Cli.BrowseCommand.RunInteractive</c> is exercised at a width where the
/// deterministic <c>TuiRenderTests</c> assertions cannot reach — they draw in-process, never
/// through a real terminal.
/// </summary>
public partial class BrowsePtyTests
{
    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    [Fact]
    public async Task UnderANarrowPty_BrowseNeverEmitsARowThatWouldWrap()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the pty harness uses macOS script(1) argument semantics.");

        var home = Path.Combine(Path.GetTempPath(), "engram-browse-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var (initExit, _, initErr) = EngramProcess.Run(home, "init");
            Assert.True(initExit == 0, $"engram init failed: {initErr}");

            var port = FreeTcpPort.Next();
            var cancellationToken = TestContext.Current.CancellationToken;
            var (startExit, _, startErr) = EngramProcess.Run(home, "start", "--port", port.ToString());
            Assert.True(startExit == 0, $"engram start failed: {startErr}");

            try
            {
                using var client = new HttpMcpClient(port);
                await client.InitializeAsync(cancellationToken);
                await client.CallToolTextAsync(
                    "engram_remember", new JsonObject { ["statement"] = "The narrow-pty browse test wrote this fact." }, cancellationToken);
            }
            finally
            {
                EngramProcess.Run(home, "stop");
            }

            const int narrowColumns = 40;
            var psi = new ProcessStartInfo("/usr/bin/script")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"stty cols {narrowColumns}; exec '{EndToEndBinary.Path}' browse");
            psi.Environment["ENGRAM_HOME"] = home;
            psi.Environment["TERM"] = "xterm-256color";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start script");
            process.StandardInput.Write('q');
            process.StandardInput.Flush();
            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("browse under a narrow pty never exited; the raw-key loop is not reading what script feeds it");
            }

            var visibleLines = Ansi().Replace(stdout, string.Empty).Split('\n');
            foreach (var rawLine in visibleLines)
            {
                // The pty's own ONLCR turns every '\n' this process writes into '\r\n', so a
                // trailing '\r' lands on the prior line after Split('\n'). It never advances the
                // cursor, so it cannot cause a wrap — measuring it as if it did would fail a
                // line that is genuinely within budget.
                var line = rawLine.TrimEnd('\r');
                Assert.True(
                    line.Length < narrowColumns,
                    $"a row of {line.Length} columns would wrap a {narrowColumns}-column terminal: {line}");
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
