using Engram.Core;

namespace Engram.Integration.Tests;

public sealed class SandboxHome : IDisposable
{
    public EngramHome Home { get; }

    public SandboxHome(bool initialize = true)
    {
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tempRoot = Path.Combine(Path.GetTempPath(), "engram-sandbox-" + Guid.NewGuid().ToString("N"));
        var home = EngramHome.Resolve(tempRoot, new Dictionary<string, string?>(), userProfileDirectory, Environment.CurrentDirectory);

        ThrowIfRealHome(home.Root, userProfileDirectory);

        if (initialize)
        {
            EngramInitializer.Initialize(home);
        }
        else
        {
            Directory.CreateDirectory(home.Root);
        }

        Home = home;
    }

    public void Dispose()
    {
        if (Directory.Exists(Home.Root))
        {
            Directory.Delete(Home.Root, recursive: true);
        }
    }

    internal static void ThrowIfRealHome(string root, string userProfileDirectory)
    {
        var realHome = Path.GetFullPath(Path.Combine(userProfileDirectory, ".engram"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var isSameOrSubdirectory =
            normalizedRoot.Equals(realHome, StringComparison.OrdinalIgnoreCase)
            || normalizedRoot.StartsWith(realHome + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (isSameOrSubdirectory)
        {
            throw new InvalidOperationException(
                $"Refusing to sandbox at '{root}': it is the real Engram home ('{realHome}') or a subdirectory of it.");
        }
    }
}
