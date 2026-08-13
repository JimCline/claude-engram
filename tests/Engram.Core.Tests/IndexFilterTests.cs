using System.Text;
using Engram.Core;

namespace Engram.Core.Tests;

public class IndexFilterTests
{
    private static ReadOnlySpan<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Classify_OnOrdinarySource_Includes()
    {
        var verdict = IndexFilter.Classify(Bytes("public class Foo\n{\n    int Bar;\n}\n"), 400);

        Assert.True(verdict.Include);
        Assert.Equal(SkipReason.None, verdict.Reason);
    }

    // git's own test, and the reason there is no extension list anywhere in this filter.
    [Fact]
    public void Classify_OnANulByte_IsBinary()
    {
        var verdict = IndexFilter.Classify([0x7f, 0x45, 0x4c, 0x46, 0x02, 0x00, 0x01], 400);

        Assert.Equal(SkipReason.Binary, verdict.Reason);
    }

    [Fact]
    public void Classify_OnAMinifiedBundle_IsGenerated()
    {
        var verdict = IndexFilter.Classify(Bytes(new string('x', 5000)), 400);

        Assert.Equal(SkipReason.Generated, verdict.Reason);
    }

    /// <summary>
    /// The trailing run has to count as a line even without its newline. A bundle is one line for
    /// its whole length, so the head never contains one — not counting it would divide by the
    /// newlines that are there and let exactly the intended target through.
    /// </summary>
    [Fact]
    public void Classify_OnALongLineWithNoNewlineYet_IsStillGenerated()
    {
        var head = Bytes("var a=1;\n" + new string('y', 4000));

        Assert.Equal(SkipReason.Generated, IndexFilter.Classify(head, 400).Reason);
    }

    /// <summary>
    /// The regression that a longest-line test gets wrong, taken from a real file in this
    /// repository: <c>plugin/hooks/hooks.json</c> is 61 hand-written lines, 4018 bytes, with one
    /// 2662-byte line among them. One long line is a formatting choice. Being made of long lines
    /// is what generated means.
    /// </summary>
    [Fact]
    public void Classify_OnAHandWrittenFileWithOneLongLine_Includes()
    {
        var head = Bytes(
            string.Concat(Enumerable.Repeat("  \"a\": 1,\n", 60))
            + new string('x', 2662) + "\n");

        Assert.True(IndexFilter.Classify(head, 400).Include);
    }

    [Fact]
    public void Classify_OnManyShortLines_Includes()
    {
        var head = Bytes(string.Concat(Enumerable.Repeat("a short line of source\n", 200)));

        Assert.True(IndexFilter.Classify(head, 400).Include);
    }

    // Binary wins over long lines: a one-line blob is binary first, and the reason a user sees
    // should be the true one.
    [Fact]
    public void Classify_PrefersBinaryOverGenerated()
    {
        var head = new byte[5000];
        head[4999] = 0;
        Array.Fill(head, (byte)'x', 0, 4999);

        Assert.Equal(SkipReason.Binary, IndexFilter.Classify(head, 400).Reason);
    }

    [Fact]
    public void Classify_OnAnEmptyHead_Includes()
    {
        Assert.True(IndexFilter.Classify([], 400).Include);
    }

    [Fact]
    public void IsIgnored_UsesTheConfiguredPatterns()
    {
        var filter = new IndexFilter(IndexingSettings.Default with { Ignore = ["**/vendor/**"] });

        Assert.True(filter.IsIgnored("web/vendor/jquery.js"));
        Assert.False(filter.IsIgnored("web/src/app.js"));
    }

    [Fact]
    public void IsIgnored_WithNoPatterns_IgnoresNothing()
    {
        var filter = new IndexFilter(IndexingSettings.Default with { Ignore = [] });

        Assert.False(filter.IsIgnored("bin/app.dll"));
    }

    // A blank entry in the config list would otherwise compile to a pattern matching the empty
    // string, which is harmless — but an entry of "  " should not silently become one either.
    [Fact]
    public void IsIgnored_SkipsBlankPatterns()
    {
        var filter = new IndexFilter(IndexingSettings.Default with { Ignore = ["", "   "] });

        Assert.False(filter.IsIgnored("anything.cs"));
    }

    [Fact]
    public void Inspect_ChecksThePatternBeforeTouchingTheDisk()
    {
        var filter = new IndexFilter(IndexingSettings.Default);

        var verdict = filter.Inspect("bin/nothing-here.dll", "/definitely/not/a/real/path.dll");

        Assert.Equal(SkipReason.Ignored, verdict.Reason);
    }

    [Fact]
    public void Inspect_OnAMissingFile_IsUnreadable()
    {
        var filter = new IndexFilter(IndexingSettings.Default);

        var verdict = filter.Inspect("src/gone.cs", Path.Combine(Path.GetTempPath(), $"engram-missing-{Guid.NewGuid():N}.cs"));

        Assert.Equal(SkipReason.Unreadable, verdict.Reason);
    }

    [Fact]
    public void Read_WithNoConfig_KeepsTheShippedDefaults()
    {
        var settings = IndexingSettings.Read(ConfigFile.Empty);

        Assert.Equal(IndexingSettings.DefaultIgnore, settings.Ignore);
        Assert.Equal(IndexingSettings.DefaultMaxFileBytes, settings.MaxFileBytes);
        Assert.Equal(IndexingSettings.DefaultMaxMeanLineBytes, settings.MaxMeanLineBytes);
        Assert.True(settings.UseGit);
        Assert.Empty(settings.Problems);
    }

    // An explicit empty list means "ignore nothing", which is not the same as saying nothing at
    // all — falling back to the defaults there would make the setting unusable.
    [Fact]
    public void Read_WithAnExplicitlyEmptyIgnoreList_DoesNotFallBack()
    {
        var settings = IndexingSettings.Read(ConfigFile.Parse("[indexing]\nignore = []\n"));

        Assert.Empty(settings.Ignore);
    }

    [Fact]
    public void Read_TakesTheConfiguredValues()
    {
        var settings = IndexingSettings.Read(ConfigFile.Parse(
            """
            [indexing]
            use_git = false
            ignore = ["**/vendor/**", "*.min.js"]
            max_file_bytes = 4096
            max_mean_line_bytes = 120
            """));

        Assert.False(settings.UseGit);
        Assert.Equal(["**/vendor/**", "*.min.js"], settings.Ignore);
        Assert.Equal(4096, settings.MaxFileBytes);
        Assert.Equal(120, settings.MaxMeanLineBytes);
    }

    // Zero would exclude every file, silently, and look like indexing was broken rather than
    // misconfigured.
    [Fact]
    public void Read_OnANonPositiveCap_KeepsTheDefaultAndSaysSo()
    {
        var settings = IndexingSettings.Read(ConfigFile.Parse("[indexing]\nmax_file_bytes = 0\n"));

        Assert.Equal(IndexingSettings.DefaultMaxFileBytes, settings.MaxFileBytes);
        Assert.Contains(settings.Problems, p => p.Contains("max_file_bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void TheShippedConfig_ParsesIntoTheSameDefaults()
    {
        var settings = IndexingSettings.Read(ConfigFile.Parse(DefaultConfig.Content));

        Assert.Empty(settings.Problems);
        Assert.Equal(IndexingSettings.DefaultIgnore, settings.Ignore);
        Assert.Equal(IndexingSettings.DefaultMaxFileBytes, settings.MaxFileBytes);
        Assert.Equal(IndexingSettings.DefaultMaxMeanLineBytes, settings.MaxMeanLineBytes);
        Assert.True(settings.UseGit);
    }
}
