using System.Globalization;
using Engram.Core;

namespace Engram.Integration.Tests;

public class SpoolReaderTests
{
    [Fact]
    public void Parse_ReadsATimestampAndAPath()
    {
        var entry = SpoolReader.Parse(Stamp(At(1)) + "\n/repo/first.cs\n");

        Assert.NotNull(entry);
        Assert.Equal(At(1), entry.Value.At);
        Assert.Equal("/repo/first.cs", entry.Value.Path);
    }

    /// <summary>
    /// A spool file from before the hook recorded paths still parses.
    /// </summary>
    /// <remarks>
    /// Not hypothetical: the queue on a real instance held a thousand of these when the second
    /// line was added. An entry with no path is an edit whose target is unknown, which is a
    /// weaker fact than an entry with one but a much stronger one than a parse failure.
    /// </remarks>
    [Fact]
    public void Parse_ReadsATimestampOnlyEntryAsAnEditWithNoPath()
    {
        var entry = SpoolReader.Parse(Stamp(At(1)) + "\n");

        Assert.NotNull(entry);
        Assert.Equal(At(1), entry.Value.At);
        Assert.Null(entry.Value.Path);
    }

    /// <summary>
    /// Null rather than a throw: the writer swallows its own errors to protect the hook
    /// budget, so a truncated file is a thing that happens, and one must never strand the
    /// entries behind it.
    /// </summary>
    [Fact]
    public void Parse_ReturnsNullForAnEntryThatDoesNotStartWithATimestamp()
    {
        Assert.Null(SpoolReader.Parse("not a timestamp"));
        Assert.Null(SpoolReader.Parse(string.Empty));
    }

    private static DateTimeOffset At(int second) => DateTimeOffset.UnixEpoch.AddSeconds(second);

    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
}
