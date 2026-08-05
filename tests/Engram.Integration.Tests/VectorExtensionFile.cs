using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Where the real <c>sqlite-vec</c> binary is, for the tests that will not accept a stand-in.
/// </summary>
/// <remarks>
/// Environment-gated for the same reason <c>ENGRAM_TEST_BINARY</c> is: the artefact is
/// downloaded rather than built, so a developer who has not fetched it should get skips instead
/// of failures. That makes it a guard that can silently never run, which is worth nothing — CI
/// has to set this, and a skip in CI is a broken build, not a clean one.
/// </remarks>
internal static class VectorExtensionFile
{
    public const string SkipReason =
        "ENGRAM_VEC_EXTENSION is not set; skipping the test that drives the real sqlite-vec extension.";

    public static string? Path { get; } = Environment.GetEnvironmentVariable("ENGRAM_VEC_EXTENSION");

    /// <summary>Copies the extension into a home's <c>lib/</c>, as `init --with-embeddings` will.</summary>
    public static void InstallInto(EngramHome home)
    {
        Directory.CreateDirectory(home.LibDir);
        File.Copy(Path!, VectorExtension.PathIn(home.LibDir), overwrite: true);
    }
}
