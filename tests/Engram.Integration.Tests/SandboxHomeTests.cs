namespace Engram.Integration.Tests;

public class SandboxHomeTests
{
    [Fact]
    public void Constructor_ResolvesToExistingWritableDirectory_NotUnderRealHome()
    {
        using var sandbox = new SandboxHome();

        Assert.True(Directory.Exists(sandbox.Home.Root));

        var probeFile = Path.Combine(sandbox.Home.Root, "write-probe.tmp");
        File.WriteAllText(probeFile, "probe");
        Assert.True(File.Exists(probeFile));
        File.Delete(probeFile);

        var realHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".engram");
        Assert.DoesNotContain(realHome, sandbox.Home.Root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispose_RemovesTheDirectory()
    {
        var sandbox = new SandboxHome();
        var root = sandbox.Home.Root;
        Assert.True(Directory.Exists(root));

        sandbox.Dispose();

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Guard_ThrowsWhenPointedAtRealHome()
    {
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realHome = Path.Combine(userProfileDirectory, ".engram");

        Assert.Throws<InvalidOperationException>(() => SandboxHome.ThrowIfRealHome(realHome, userProfileDirectory));
    }

    [Fact]
    public void Guard_ThrowsWhenPointedAtSubdirectoryOfRealHome()
    {
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realHomeSubdirectory = Path.Combine(userProfileDirectory, ".engram", "queue");

        Assert.Throws<InvalidOperationException>(() => SandboxHome.ThrowIfRealHome(realHomeSubdirectory, userProfileDirectory));
    }
}
