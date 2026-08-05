using Microsoft.Data.Sqlite;

namespace Engram.Core.Tests;

public class VectorExtensionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "engram-vecext-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public void FileName_MatchesThePlatformsSharedLibrarySuffix()
    {
        var expected =
            OperatingSystem.IsWindows() ? "vec0.dll"
            : OperatingSystem.IsMacOS() ? "vec0.dylib"
            : "vec0.so";

        Assert.Equal(expected, VectorExtension.FileName);
    }

    [Fact]
    public void PathIn_PutsTheExtensionInsideTheGivenDirectory()
    {
        var path = VectorExtension.PathIn("/somewhere/lib");

        Assert.Equal(Path.Combine("/somewhere/lib", VectorExtension.FileName), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Load_WithNoLibraryDirectory_ReportsNotInstalled(string? libraryDirectory)
    {
        using var connection = OpenInMemory();

        Assert.Equal(
            VectorExtensionState.NotInstalled,
            VectorExtension.Load(connection, libraryDirectory));
    }

    [Fact]
    public void Load_WhenTheDirectoryDoesNotExist_ReportsNotInstalled()
    {
        using var connection = OpenInMemory();

        Assert.Equal(
            VectorExtensionState.NotInstalled,
            VectorExtension.Load(connection, Path.Combine(_directory, "never-created")));
    }

    [Fact]
    public void Load_WhenTheDirectoryExistsButIsEmpty_ReportsNotInstalled()
    {
        Directory.CreateDirectory(_directory);
        using var connection = OpenInMemory();

        Assert.Equal(VectorExtensionState.NotInstalled, VectorExtension.Load(connection, _directory));
    }

    /// <summary>
    /// The state that separates "embeddings were never installed" from "embeddings are
    /// installed and broken". Collapsing the two would have `doctor` tell a user with a
    /// corrupt extension that the feature is simply off.
    /// </summary>
    [Fact]
    public void Load_WhenTheFileIsNotALoadableLibrary_ReportsFailedRatherThanNotInstalled()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(VectorExtension.PathIn(_directory), "this is not a shared library");

        using var connection = OpenInMemory();

        Assert.Equal(VectorExtensionState.Failed, VectorExtension.Load(connection, _directory));
    }

    [Fact]
    public void Load_WhenTheFileIsNotALoadableLibrary_LeavesTheConnectionUsable()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(VectorExtension.PathIn(_directory), "this is not a shared library");

        using var connection = OpenInMemory();
        VectorExtension.Load(connection, _directory);

        // Recall has to keep working on FTS5 alone when the vector lane is unavailable, so a
        // failed load must cost the vector lane and nothing else.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Load_NeverThrows_SoAnInstanceWithoutEmbeddingsIsNotAnError()
    {
        using var connection = OpenInMemory();

        var exception = Record.Exception(() =>
        {
            VectorExtension.Load(connection, null);
            VectorExtension.Load(connection, _directory);
            VectorExtension.Load(connection, Path.Combine(_directory, "nested", "deeper"));
        });

        Assert.Null(exception);
    }
}
