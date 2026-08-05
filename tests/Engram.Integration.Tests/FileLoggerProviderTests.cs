using Engram.Cli;

namespace Engram.Integration.Tests;

public class FileLoggerProviderTests
{
    // The daemon logs while Kestrel binds, so this is the first write to need the home.
    // On a machine where ~/.engram has never existed, that threw inside a process whose
    // stderr is /dev/null and surfaced only as `engram start` timing out.
    [Fact]
    public void Construction_CreatesTheLogDirectoryWhenItDoesNotExistYet()
    {
        using var temp = new TempDirectory();
        var absent = Path.Combine(temp.Path, "never-created");

        using var provider = new FileLoggerProvider(Path.Combine(absent, "engram.log"));

        Assert.True(Directory.Exists(absent));
    }

    // Log level alone does not bound the file: a crash-looping server writes warnings
    // continuously, which is precisely when nobody is watching disk.
    [Fact]
    public void Construction_RotatesALogThatHasGrownPastTheCap()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "engram.log");
        File.WriteAllBytes(path, new byte[(1024 * 1024) + 1]);

        using var provider = new FileLoggerProvider(path);

        Assert.True(File.Exists(path + ".1"), "the oversized log should have been moved aside");
        Assert.False(File.Exists(path), "the live log should start empty after a rotation");
    }

    [Fact]
    public void Construction_LeavesASmallLogAlone()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "engram.log");
        File.WriteAllText(path, "one earlier line\n");

        using var provider = new FileLoggerProvider(path);

        Assert.False(File.Exists(path + ".1"));
        Assert.Equal("one earlier line\n", File.ReadAllText(path));
    }
}
