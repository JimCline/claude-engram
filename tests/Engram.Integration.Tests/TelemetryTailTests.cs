using System.Text;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2, against real files. What this has to survive is a writer appending underneath it: a
/// record caught halfway, a log replaced, and a burst larger than one read.
/// </summary>
public class TelemetryTailTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(), $"engram-tail-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// No byte-order mark, because <see cref="Telemetry.Append"/> writes none — it goes through
    /// <c>Encoding.UTF8.GetBytes</c>, while <c>File.AppendAllText</c> with the same encoding emits
    /// one on a file it creates. Writing a shape production never produces would have tested the
    /// wrong file; the mark gets its own test below instead.
    /// </summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private void Append(string text) => File.AppendAllText(path, text, Utf8);

    [Fact]
    public void AMissingFile_ReadsNothingRatherThanThrowing()
    {
        var tail = new TelemetryTail(path, 0);

        Assert.Empty(tail.Read(10));
        Assert.Equal(0, tail.Offset);
    }

    [Fact]
    public void EndOf_AMissingFile_IsZero() => Assert.Equal(0, TelemetryTail.EndOf(path));

    [Fact]
    public void Lines_AppendedAfterTheOffset_AreRead()
    {
        Append("{\"kind\":\"one\"}\n");
        var tail = new TelemetryTail(path, TelemetryTail.EndOf(path));

        Append("{\"kind\":\"two\"}\n{\"kind\":\"three\"}\n");
        var lines = tail.Read(10);

        Assert.Equal(["{\"kind\":\"two\"}", "{\"kind\":\"three\"}"], lines);
    }

    /// <summary>
    /// Starting at the end is the whole no-resume contract: what was written before the tail
    /// existed is history, and history is read from the file rather than delivered.
    /// </summary>
    [Fact]
    public void StartingAtTheEnd_SkipsEverythingAlreadyWritten()
    {
        Append("{\"kind\":\"before\"}\n");

        var tail = new TelemetryTail(path, TelemetryTail.EndOf(path));

        Assert.Empty(tail.Read(10));
    }

    /// <summary>
    /// The newline is the only boundary a reader can trust. Yielding the bytes seen so far would
    /// hand a subscriber a truncated JSON object that parses as nothing.
    /// </summary>
    [Fact]
    public void APartialLine_IsHeldBackUntilItsNewlineArrives()
    {
        var tail = new TelemetryTail(path, 0);

        Append("{\"kind\":\"half");
        Assert.Empty(tail.Read(10));
        Assert.Equal(0, tail.Offset);

        Append("\"}\n");
        Assert.Equal(["{\"kind\":\"half\"}"], tail.Read(10));
    }

    [Fact]
    public void ACompleteLineBeforeAPartialOne_IsReadWithoutWaitingForIt()
    {
        var tail = new TelemetryTail(path, 0);

        Append("{\"kind\":\"whole\"}\n{\"kind\":\"partial");

        Assert.Equal(["{\"kind\":\"whole\"}"], tail.Read(10));

        Append("\"}\n");
        Assert.Equal(["{\"kind\":\"partial\"}"], tail.Read(10));
    }

    /// <summary>
    /// A rotated or hand-deleted log is a shorter file, not an error. Holding the old offset would
    /// stop the tail forever the first time anyone truncated it.
    /// </summary>
    [Fact]
    public void AFileThatShrank_IsReadFromTheStartAgain()
    {
        Append("{\"kind\":\"one\"}\n{\"kind\":\"two\"}\n");
        var tail = new TelemetryTail(path, 0);
        tail.Read(10);

        File.WriteAllText(path, "{\"kind\":\"fresh\"}\n", Encoding.UTF8);

        Assert.Equal(["{\"kind\":\"fresh\"}"], tail.Read(10));
    }

    [Fact]
    public void MoreLinesThanAskedFor_AreLeftForTheNextRead()
    {
        var tail = new TelemetryTail(path, 0);
        for (var i = 0; i < 5; i++)
        {
            Append($"{{\"n\":{i}}}\n");
        }

        Assert.Equal(["{\"n\":0}", "{\"n\":1}"], tail.Read(2));
        Assert.Equal(["{\"n\":2}", "{\"n\":3}"], tail.Read(2));
        Assert.Equal(["{\"n\":4}"], tail.Read(2));
        Assert.Empty(tail.Read(2));
    }

    [Fact]
    public void BlankLines_AreConsumedWithoutBeingReported()
    {
        var tail = new TelemetryTail(path, 0);

        Append("\n\n{\"kind\":\"one\"}\n");

        Assert.Equal(["{\"kind\":\"one\"}"], tail.Read(10));
        Assert.Empty(tail.Read(10));
    }

    /// <summary>
    /// A run this long with no newline cannot be a record — Telemetry caps one at 4 KB — so it is a
    /// corrupted or foreign file. Without the skip the offset never advances and the tail stops on
    /// that byte for the life of the server.
    /// </summary>
    [Fact]
    public void AStretchTooLongToBeARecord_IsSkippedRatherThanBlockingForever()
    {
        var tail = new TelemetryTail(path, 0);

        Append(new string('x', TelemetryTail.MaxBytesPerRead + 32));
        Append("\n{\"kind\":\"after\"}\n");

        Assert.Empty(tail.Read(10));
        Assert.Equal(TelemetryTail.MaxBytesPerRead, tail.Offset);

        var rest = tail.Read(10);
        Assert.Contains("{\"kind\":\"after\"}", rest);
    }

    /// <summary>
    /// A log an editor has opened and saved carries U+FEFF, which is neither whitespace to
    /// <c>Trim</c> nor whitespace to the JSON reader — so an unstripped mark makes the first record
    /// unparseable and the subscriber hears nothing, with no error anywhere.
    /// </summary>
    [Fact]
    public void ALogCarryingAByteOrderMark_StillYieldsAParseableRecord()
    {
        File.WriteAllText(path, "{\"kind\":\"remember\"}\n", new UTF8Encoding(true));
        var tail = new TelemetryTail(path, 0);

        var line = Assert.Single(tail.Read(10));

        Assert.NotNull(Telemetry.TryParse(line));
    }

    [Fact]
    public void RecordsWrittenByTelemetry_ParseBackThroughTryParse()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var tail = new TelemetryTail(
            Telemetry.ResolvePath(sandbox.Home), TelemetryTail.EndOf(Telemetry.ResolvePath(sandbox.Home)));

        Telemetry.Append(sandbox.Home, new TelemetryRecord(
            DateTimeOffset.UtcNow.ToString("O"), "session-a", TelemetryEventKind.Remember));

        var line = Assert.Single(tail.Read(10));
        var record = Telemetry.TryParse(line);

        Assert.NotNull(record);
        Assert.Equal(TelemetryEventKind.Remember, record.Kind);
        Assert.Equal("session-a", record.SessionId);
    }

    [Fact]
    public void TryParse_OnSomethingThatIsNotARecord_IsNullRatherThanAThrow()
    {
        Assert.Null(Telemetry.TryParse("{not json"));
        Assert.Null(Telemetry.TryParse("   "));
    }
}
