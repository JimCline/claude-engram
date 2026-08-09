namespace Engram.EndToEnd.Tests;

internal static class EndToEndBinary
{
    public const string SkipReason = "no published binary; skipping end-to-end test. Publish to ./out (or set ENGRAM_TEST_BINARY) to run tier 3.";

    /// <summary>
    /// The published binary to drive, from <c>ENGRAM_TEST_BINARY</c> or from the repository's
    /// conventional <c>./out</c>.
    /// </summary>
    /// <remarks>
    /// Falling back to <c>./out/engram</c> is what keeps this tier from needing ceremony: a tree
    /// that has been published once runs tier 3 on a plain <c>dotnet test</c>, and a tree that has
    /// not says so and skips. Requiring the variable and failing without it was tried and reverted
    /// — it turned every inner-loop run into a failure, which is a worse habit to build than the
    /// one it was guarding against.
    /// </remarks>
    public static string? Path { get; } = Resolve();

    private static string? Resolve()
    {
        if (Environment.GetEnvironmentVariable("ENGRAM_TEST_BINARY") is { Length: > 0 } configured)
        {
            return configured;
        }

        // Up from bin/<config>/<tfm>/ to the repository root, which is the directory holding .git.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "out", "engram");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (Directory.Exists(System.IO.Path.Combine(directory.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }
}
