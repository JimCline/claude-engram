namespace Engram.EndToEnd.Tests;

/// <summary>
/// A tier-3 run that drove nothing must not be reportable as a pass.
/// </summary>
/// <remarks>
/// <para>Every other test here opens with <c>Assert.SkipUnless(EndToEndBinary.Path is not null,
/// …)</c>, so with the variable unset the whole tier evaporates into the skip count and the
/// summary line still reads <c>Passed!</c>. That is not hypothetical: a session reported this
/// suite green three times while 128 of its 161 tests were being skipped, because the summary
/// counts passes and the skips sit in a column nobody reads. D9 puts this tier here precisely
/// because CI passing on the JIT build proves nothing about what ships — so the run that skips it
/// is the run whose result means least, and it was the one that looked cleanest.</para>
///
/// <para>The fix is to invert which side needs a flag, on D49's reasoning: skipping is now the
/// thing you have to ask for. Unset both variables and this fails with the two commands that
/// resolve it; set <c>ENGRAM_SKIP_TIER3</c> and it skips like everything else, which is an
/// acknowledgement rather than an accident.</para>
///
/// <para>It deliberately does not check that the binary <i>works</i> — every other test in this
/// assembly does that, and a second opinion here would just be the first test to fail for
/// unrelated reasons while claiming to be about coverage.</para>
/// </remarks>
public class TierThreeCoverageTests
{
    [Fact]
    public void TheseTestsRanAgainstAPublishedBinary_OrTheSkipWasDeliberate()
    {
        if (EndToEndBinary.Path is not null)
        {
            // Not a skip guard — measured, a path that is set but missing does not skip anything,
            // it fails 128 tests with Win32Exception "No such file or directory" from wherever
            // each one happened to start a process. This turns that into one line naming the
            // variable, which is the difference between a typo and a debugging session.
            Assert.True(
                File.Exists(EndToEndBinary.Path),
                $"ENGRAM_TEST_BINARY points at '{EndToEndBinary.Path}', which does not exist. "
                    + "Publish first; every other failure in this run is downstream of this one.");
            return;
        }

        Assert.Skip(EndToEndBinary.SkipReason);
    }

    /// <summary>
    /// Split from the assertion above so the failure names the situation rather than a boolean.
    /// </summary>
    [Fact]
    public void AnUnacknowledgedSkipIsAFailure()
    {
        if (EndToEndBinary.Path is not null || EndToEndBinary.SkipAcknowledged)
        {
            return;
        }

        Assert.Fail(
            "Tier 3 did not run: ENGRAM_TEST_BINARY is unset, so every end-to-end test skipped "
                + "and this run says nothing about the binary that ships. Publish and point at it:\n"
                + "  dotnet publish src/Engram.Cli/Engram.Cli.csproj -c Release -r <rid> -o out\n"
                + "  ENGRAM_TEST_BINARY=$PWD/out/engram dotnet test\n"
                + $"To skip on purpose while iterating, set {EndToEndBinary.OptOutVariable}=1.");
    }
}
