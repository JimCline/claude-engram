namespace Engram.EndToEnd.Tests;

internal static class EndToEndBinary
{
    public const string SkipReason = "ENGRAM_TEST_BINARY is not set; skipping end-to-end test that drives the published binary.";

    public static string? Path { get; } = Environment.GetEnvironmentVariable("ENGRAM_TEST_BINARY");
}
