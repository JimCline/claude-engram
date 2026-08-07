using Engram.Core;

namespace Engram.Integration.Tests;

public class PidFileTests
{
    [Fact]
    public void Read_NoFile_ReturnsNull()
    {
        using var sandbox = new SandboxHome();

        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void WriteThenRead_RoundTripsExactly()
    {
        using var sandbox = new SandboxHome();
        var record = new PidFileRecord(123, 7433, "0.1.0", new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

        PidFile.Write(sandbox.Home, record);

        Assert.Equal(record, PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void WriteThenRead_CarriesTheStartToken()
    {
        using var sandbox = new SandboxHome();
        var record = new PidFileRecord(
            123, 7433, "0.1.0", new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), "boot-a:5150");

        PidFile.Write(sandbox.Home, record);

        Assert.Equal("boot-a:5150", PidFile.Read(sandbox.Home)?.StartToken);
    }

    /// <summary>
    /// A pid file left by a binary that predates tokens reads back with none, rather than failing.
    /// </summary>
    /// <remarks>
    /// Null is what selects the legacy wall-clock comparison, so a change that made the property
    /// required would turn every server running across an upgrade into an unreachable one.
    /// </remarks>
    [Fact]
    public void Read_AFileWrittenBeforeTokensExisted_HasNoTokenAndIsOtherwiseIntact()
    {
        using var sandbox = new SandboxHome();
        File.WriteAllText(
            PidFile.ResolvePath(sandbox.Home),
            """{"pid":123,"port":7433,"version":"0.1.0","start_time":"2026-03-04T05:06:07+00:00"}""");

        var record = PidFile.Read(sandbox.Home);

        Assert.NotNull(record);
        Assert.Null(record.StartToken);
        Assert.Equal(123, record.Pid);
        Assert.Equal(7433, record.Port);
    }

    [Fact]
    public void Write_SetsOwnerReadWriteOnlyPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(1, 7433, "0.1.0", DateTimeOffset.UtcNow));

        var mode = File.GetUnixFileMode(PidFile.ResolvePath(sandbox.Home));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Read_CorruptFile_ReturnsNull()
    {
        using var sandbox = new SandboxHome();
        File.WriteAllText(PidFile.ResolvePath(sandbox.Home), "{not valid json");

        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        using var sandbox = new SandboxHome();
        PidFile.Write(sandbox.Home, new PidFileRecord(1, 7433, "0.1.0", DateTimeOffset.UtcNow));

        PidFile.Delete(sandbox.Home);

        Assert.Null(PidFile.Read(sandbox.Home));
    }

    [Fact]
    public void Delete_NoFile_DoesNotThrow()
    {
        using var sandbox = new SandboxHome();

        PidFile.Delete(sandbox.Home);
    }
}
