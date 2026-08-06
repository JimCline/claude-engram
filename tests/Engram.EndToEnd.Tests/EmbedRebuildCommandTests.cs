namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>engram embed --rebuild</c> through the published binary.
/// </summary>
/// <remarks>
/// The refusal in <see cref="Rebuild_WhileTheServerIsRunning_RefusesAndSaysToStopIt"/> is why this
/// is tier 3 and not tier 2. The check runs against a real <c>ProcessInspector</c> reading a real
/// pid file written by a real second process — the command constructs those concretes itself, so a
/// test with fakes injected would be asserting on the fakes rather than on whether Engram can tell
/// that its own server is up.
/// </remarks>
public class EmbedRebuildCommandTests
{
    [Fact]
    public void Rebuild_WithProviderNone_RefusesRatherThanBuildingAnEmptyIndex()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "embed", "--rebuild");

        Assert.Equal(1, exitCode);
        Assert.Contains("none", stderr, StringComparison.Ordinal);
        Assert.Contains("--with-embeddings", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_WithNoStore_SaysToRunInit()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        var root = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (exitCode, _, stderr) = EngramProcess.Run(root, "embed", "--rebuild");

            Assert.Equal(1, exitCode);
            Assert.Contains("engram init", stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The guard the command is built around: one owner of vector production, and it is not this.
    /// </summary>
    /// <remarks>
    /// A running server holds an embedder built at its startup, so a rebuild prompted by a config
    /// change would race it — and lose, because the server's own <c>EnsureCreated</c> would re-pin
    /// the recreated table to the space the user had just moved away from.
    /// </remarks>
    [Fact]
    public void Rebuild_WhileTheServerIsRunning_RefusesAndSaysToStopIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        // A provider, so the refusal under test is the one that fires. On a default home
        // provider = "none" is checked first and exits 1 for an entirely different reason.
        ConfigureProvider(home.Root);

        var port = FreePort();
        var (startExit, _, startErr) = EngramProcess.Run(
            home.Root, "start", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        try
        {
            var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "embed", "--rebuild", "--apply");

            Assert.Equal(1, exitCode);
            Assert.Contains("engram stop", stderr, StringComparison.Ordinal);

            // Asserted separately from the exit code: provider is "none" on a default home, which
            // also exits 1. Without this the test would pass with the server check deleted.
            Assert.Contains("server is running", stderr, StringComparison.Ordinal);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }
    }

    [Fact]
    public void Rebuild_IsNotSilentlyAcceptedAsProbe()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "embed");

        Assert.Equal(2, exitCode);
        Assert.Contains("--rebuild", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Points the home at an endpoint nothing is serving.
    /// </summary>
    /// <remarks>
    /// Deliberately dead: the server check runs before the embedder is resolved, so a rebuild that
    /// refuses correctly never dials it. A test that needed a live endpoint to prove the refusal
    /// would be proving something else.
    /// </remarks>
    private static void ConfigureProvider(string root)
    {
        var path = Path.Combine(root, "config.toml");
        var config = File.ReadAllText(path);

        Assert.Contains("provider = \"none\"", config, StringComparison.Ordinal);

        // Substituted in place rather than appended: the keys have to land under [embedding], and
        // a block added at the end of the file belongs to whatever section happens to be last.
        File.WriteAllText(
            path,
            config.Replace(
                "provider = \"none\"",
                "provider = \"ollama\"\nendpoint = \"http://127.0.0.1:1\"\ndim = 8",
                StringComparison.Ordinal));
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
