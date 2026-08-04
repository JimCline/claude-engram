using Engram.Core;

namespace Engram.Integration.Tests;

public class AtomicFileTests
{
    [Fact]
    public void Write_OverExistingFile_LeavesNoTempFilesBehind()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "target.txt");
        File.WriteAllText(path, "original");

        AtomicFile.Write(path, "replacement");

        Assert.Equal("replacement", File.ReadAllText(path));

        var remainingFiles = Directory.GetFiles(dir.Path);
        Assert.Equal([path], remainingFiles);
    }

    [Fact]
    public void Write_TempFileCreationFails_LeavesOriginalFileByteIdentical()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "target.txt");
        const string original = "original content";
        File.WriteAllText(path, original);

        var originalMode = File.GetUnixFileMode(dir.Path);
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var exception = Record.Exception(() => AtomicFile.Write(path, "new content"));
            Assert.NotNull(exception);
        }
        finally
        {
            File.SetUnixFileMode(dir.Path, originalMode);
        }

        Assert.Equal(original, File.ReadAllText(path));

        var remainingFiles = Directory.GetFiles(dir.Path);
        Assert.Equal([path], remainingFiles);
    }

    [Fact]
    public void Prepare_LeavesTargetUnchangedUntilCommit()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "target.txt");
        File.WriteAllText(path, "original");

        using var pending = AtomicFile.Prepare(path, "replacement");

        Assert.Equal("original", File.ReadAllText(path));

        pending.Commit();

        Assert.Equal("replacement", File.ReadAllText(path));
    }

    [Fact]
    public void Dispose_WithoutCommit_LeavesTargetUnchanged_AndRemovesTempFile()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "target.txt");
        File.WriteAllText(path, "original");

        var pending = AtomicFile.Prepare(path, "replacement");
        pending.Dispose();

        Assert.Equal("original", File.ReadAllText(path));

        var remainingFiles = Directory.GetFiles(dir.Path);
        Assert.Equal([path], remainingFiles);
    }

    [Fact]
    public void Dispose_AfterCommit_DoesNotThrow_AndDoesNotDeleteTarget()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "target.txt");
        File.WriteAllText(path, "original");

        var pending = AtomicFile.Prepare(path, "replacement");
        pending.Commit();

        var exception = Record.Exception(() => pending.Dispose());

        Assert.Null(exception);
        Assert.Equal("replacement", File.ReadAllText(path));

        var remainingFiles = Directory.GetFiles(dir.Path);
        Assert.Equal([path], remainingFiles);
    }
}
