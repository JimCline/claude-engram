namespace Engram.EndToEnd.Tests;

/// <summary>
/// D22 acceptance item 15: <c>engram report</c> through the published binary, against a disposable
/// home — the AOT/JIT-divergence and process-boundary risk tier 2 cannot reach.
/// </summary>
public class ReportCommandTests
{
    [Fact]
    public void Report_AgainstThePublishedBinary_WritesAFileAndExitsZero()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exit, stdout, stderr) = EngramProcess.Run(home.Root, "report");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);

        var printedPath = stdout.TrimEnd('\r', '\n');
        Assert.True(File.Exists(printedPath), $"printed path does not exist: {printedPath}");

        // engram init seeds the canned fact corpus (D10), so a fresh home is not a zero-fact
        // store — assert the document's shape rather than a specific count.
        var document = File.ReadAllText(printedPath);
        Assert.Contains("# Engram memory report", document);
        Assert.Matches(@"facts: \d+ total — \d+ live, \d+ closed", document);
    }
}
