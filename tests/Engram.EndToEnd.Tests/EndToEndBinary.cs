namespace Engram.EndToEnd.Tests;

internal static class EndToEndBinary
{
    public const string SkipReason = "ENGRAM_TEST_BINARY is not set; skipping end-to-end test that drives the published binary.";

    /// <summary>
    /// Set to acknowledge that this run is not exercising the published binary.
    /// </summary>
    /// <remarks>
    /// Skipping tier 3 used to be free and running it needed a flag, which is backwards for the
    /// same reason D49 gives: a default that needs a flag is not a default. The whole point of
    /// this tier is that a green JIT build says nothing about what ships, so a run that quietly
    /// drops it must not be reportable as a pass. <see cref="TierThreeCoverageTests"/> is what
    /// enforces that; this variable is the way to say "I know, I am iterating".
    /// </remarks>
    public const string OptOutVariable = "ENGRAM_SKIP_TIER3";

    public static string? Path { get; } = Environment.GetEnvironmentVariable("ENGRAM_TEST_BINARY");

    public static bool SkipAcknowledged { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptOutVariable));
}
