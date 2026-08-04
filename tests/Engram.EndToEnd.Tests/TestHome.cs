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

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
