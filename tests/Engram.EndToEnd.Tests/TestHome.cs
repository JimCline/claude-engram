namespace Engram.EndToEnd.Tests;

internal sealed class TestHome : IDisposable
{
    public string Root { get; }

    public TestHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
