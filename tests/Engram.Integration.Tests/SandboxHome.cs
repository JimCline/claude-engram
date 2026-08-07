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

    /// <summary>Removes the sandbox, releasing the pooled handles that hold its database open.</summary>
    /// <remarks>
    /// <para>Without the release this fails on Windows and only on Windows: disposing a connection
    /// returns its handle to the pool rather than closing it, Unix lets an open file be unlinked
    /// anyway, and Windows does not. It was 346 of the 358 Windows CI failures, spread across
    /// thirty test classes that had nothing in common except this line.</para>
    ///
    /// <para>The retry absorbs a transient lock — a virus scanner or indexer holding a file it has
    /// just seen appear — but the last attempt is deliberately left unguarded. Swallowing the
    /// failure the way the end-to-end <c>TestHome</c> does would be wrong here: that one tolerates a
    /// detached backup child still writing, which is a real race with no in-process equivalent, and
    /// a silent give-up would let this go green on Windows while leaking every sandbox it ever made.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        // Every database under the root, not just the main one: a snapshot written by
        // BackupStore is its own file with its own pool the moment anything opens it —
        // FingerprintOf does, and so do the backup tests — and a release that names only
        // engram.db leaves those handles holding the directory open. That was 12 of the
        // Windows failures the first version of this line left behind.
        if (Directory.Exists(Home.Root))
        {
            foreach (var database in Directory.EnumerateFiles(Home.Root, "*.db", SearchOption.AllDirectories))
            {
                EngramDatabase.ReleasePooledConnections(database);
            }
        }

        for (var attempt = 0; Directory.Exists(Home.Root); attempt++)
        {
            try
            {
                Directory.Delete(Home.Root, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 9)
            {
                Thread.Sleep(50);
            }
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
