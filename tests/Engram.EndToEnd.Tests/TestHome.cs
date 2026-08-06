namespace Engram.EndToEnd.Tests;

internal sealed class TestHome : IDisposable
{
    public string Root { get; }

    public TestHome(bool initialize = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        if (initialize)
        {
            var (exitCode, _, stderr) = EngramProcess.Run(Root, "init");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"engram init failed with exit code {exitCode}: {stderr}");
            }
        }
    }

    /// <summary>
    /// Removes the home, tolerating a detached child still writing into it.
    /// </summary>
    /// <remarks>
    /// Session start spawns a backup process that outlives the hook by design, so a test that
    /// drives thirty concurrent hooks can reach here while one is mid-write and the recursive
    /// delete fails with "directory not empty". That is a fact about detached children, not a
    /// defect the test is placed to catch: this is cleanup of a temp directory, and turning a
    /// cleanup race into a failed assertion reports a bug that is not there. Retry briefly, then
    /// leave it to the operating system's temp reaper.
    /// </remarks>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
