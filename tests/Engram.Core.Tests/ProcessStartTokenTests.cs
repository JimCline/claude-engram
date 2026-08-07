namespace Engram.Core.Tests;

public class ProcessStartTokenTests
{
    /// <summary>
    /// A <c>/proc/&lt;pid&gt;/stat</c> line whose field 22 is 987654, built to the documented field
    /// order: pid, comm, state, ppid, pgrp, session, tty_nr, tpgid, flags, minflt, cminflt, majflt,
    /// cmajflt, utime, stime, cutime, cstime, priority, nice, num_threads, itrealvalue, starttime.
    /// </summary>
    private const string Fields3To22 = "S 1 1234 1234 0 -1 4194304 100 0 0 0 5 3 0 0 20 0 1 0 987654";

    [Fact]
    public void APlainCommandName_ParsesField22()
    {
        Assert.Equal("987654", ProcessStartToken.ParseStarttime($"1234 (engram) {Fields3To22} 0 0 0"));
    }

    /// <summary>
    /// <c>comm</c> is whatever the process called itself, so it may hold spaces and parentheses
    /// both.
    /// </summary>
    /// <remarks>
    /// Cutting at the first <c>)</c> shifts every field left by two and still parses — it returns
    /// <c>1</c>, num_threads, rather than failing. A process free to name itself is therefore free
    /// to nominate its own start time, which is the value a termination decision rests on.
    /// </remarks>
    [Fact]
    public void ACommandNameHoldingSpacesAndParens_StillParsesField22()
    {
        Assert.Equal("987654", ProcessStartToken.ParseStarttime($"1234 (my) (evil proc) {Fields3To22} 0 0 0"));
    }

    [Fact]
    public void TrailingFieldsBeyondField22_AreIgnored()
    {
        var full = $"1234 (engram) {Fields3To22} {string.Join(' ', Enumerable.Repeat("7", 30))}";

        Assert.Equal("987654", ProcessStartToken.ParseStarttime(full));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234 engram S 1 1234")]                        // no comm parens at all
    [InlineData("1234 (engram) S 1 1234 1234 0 -1")]            // truncated before field 22
    [InlineData("1234 (engram) S 1 1234 1234 0 -1 4194304 100 0 0 0 5 3 0 0 20 0 1 0 not-a-number")]
    public void AnythingItCannotRead_IsNoTokenRatherThanAGuess(string line)
    {
        Assert.Null(ProcessStartToken.ParseStarttime(line));
    }

    /// <summary>The token has to be reader-independent, which is the whole point of reading it here.</summary>
    [Fact]
    public void TheCallersOwnToken_IsTheSameAsTheTokenReadBackByPid()
    {
        var mine = ProcessStartToken.ForSelf();

        Assert.NotNull(mine);
        Assert.Equal(mine, ProcessStartToken.ForPid(Environment.ProcessId), StringComparer.Ordinal);
    }
}
