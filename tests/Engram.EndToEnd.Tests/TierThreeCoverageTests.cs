namespace Engram.EndToEnd.Tests;

/// <summary>
/// Says, in the test output, whether this run drove a published binary.
/// </summary>
/// <remarks>
/// <para>Every other test here opens with <c>Assert.SkipUnless(EndToEndBinary.Path is not null,
/// …)</c>, so without one the whole tier evaporates into the skip count while the summary line
/// still reads <c>Passed!</c>. That is not hypothetical: this suite was reported green three times
/// in one session while 128 of its 161 tests were skipping, which is also how a red test survived
/// several commits. By D9 this is the tier that says what ships, so the run that drops it is the
/// run whose result means least — and it was the one that looked cleanest.</para>
///
/// <para>Failing on an unpublished tree was tried and reverted: it made every inner-loop
/// <c>dotnet test</c> red, and a check people learn to route around is worth less than no check
/// (D37, applied to a test rather than to <c>doctor</c>). <see cref="EndToEndBinary"/> falls back
/// to <c>./out/engram</c> instead, so a published tree needs no ceremony, and what remains here is
/// a named skip rather than a silent one — the thing to read is the skip count.</para>
/// </remarks>
public class TierThreeCoverageTests
{
    [Fact]
    public void TheseTestsDroveAPublishedBinary()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, SkipMessage());

        // Measured: a path that is set but missing does not skip anything, it fails 128 tests with
        // Win32Exception "No such file or directory" from wherever each one started a process.
        // This reduces that to one line naming the cause.
        Assert.True(
            File.Exists(EndToEndBinary.Path),
            $"ENGRAM_TEST_BINARY points at '{EndToEndBinary.Path}', which does not exist. "
                + "Publish first; every other failure in this run is downstream of this one.");
    }

    private static string SkipMessage() =>
        "TIER 3 DID NOT RUN — every end-to-end test in this assembly skipped, so this run says "
            + "nothing about the published binary. Do not read the summary as a pass for what "
            + "ships. To run it:\n"
            + "  dotnet publish src/Engram.Cli/Engram.Cli.csproj -c Release -r <rid> -o out\n"
            + "and re-run; ./out/engram is picked up automatically.";
}
