using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class VectorExtensionLoadTests
{
    private static string? VectorVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT vec_version();";
        return command.ExecuteScalar() as string;
    }

    private static bool AnswersVectorQueries(SqliteConnection connection)
    {
        try
        {
            return VectorVersion(connection) is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// An empty <c>lib/</c> would claim embeddings are installed when they are not, and
    /// <c>doctor</c> reads the filesystem to tell those apart.
    /// </summary>
    [Fact]
    public void Init_DoesNotCreateLibDir()
    {
        using var sandbox = new SandboxHome();

        Assert.False(Directory.Exists(sandbox.Home.LibDir));
    }

    [Fact]
    public void LibDir_IsInsideTheHome()
    {
        using var sandbox = new SandboxHome(initialize: false);

        Assert.Equal(Path.Combine(sandbox.Home.Root, "lib"), sandbox.Home.LibDir);
    }

    [Fact]
    public void Load_WithTheRealExtension_ReportsLoaded()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new SandboxHome();
        VectorExtensionFile.InstallInto(sandbox.Home);

        using var connection = EngramDatabase.Open(sandbox.Home.DatabasePath);

        Assert.Equal(VectorExtensionState.Loaded, VectorExtension.Load(connection, sandbox.Home.LibDir));
        Assert.Equal("v0.1.9", VectorVersion(connection));
    }

    /// <summary>
    /// What lets <see cref="EngramDatabase.Open(EngramHome)"/> load eagerly without breaking a
    /// caller that loads again to learn the state.
    /// </summary>
    [Fact]
    public void Load_IsSafeToCallTwiceOnOneConnection()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new SandboxHome();
        VectorExtensionFile.InstallInto(sandbox.Home);

        using var connection = EngramDatabase.Open(sandbox.Home.DatabasePath);

        Assert.Equal(VectorExtensionState.Loaded, VectorExtension.Load(connection, sandbox.Home.LibDir));
        Assert.Equal(VectorExtensionState.Loaded, VectorExtension.Load(connection, sandbox.Home.LibDir));
    }

    [Fact]
    public void Open_WithNoExtensionInstalled_CannotAnswerAVectorQuery()
    {
        using var sandbox = new SandboxHome();

        using var connection = EngramDatabase.Open(sandbox.Home);

        Assert.False(AnswersVectorQueries(connection));
    }

    [Fact]
    public void Open_WithTheExtensionInstalled_AnswersAVectorQuery()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new SandboxHome();
        VectorExtensionFile.InstallInto(sandbox.Home);

        using var connection = EngramDatabase.Open(sandbox.Home);

        Assert.True(AnswersVectorQueries(connection));
    }

    /// <summary>
    /// The measurement the whole eager-loading design rests on, kept as a test because it is
    /// the reasoning most likely to be optimised away by someone who sees a redundant load.
    /// </summary>
    /// <remarks>
    /// A connection that loaded nothing answers <c>vec_version()</c> anyway, because the pool
    /// handed it a recycled <c>sqlite3</c> handle with the module still registered. So a
    /// successful vector query proves nothing about the connection running it, an opt-in loader
    /// passes its tests on borrowed state, and the process that draws a cold handle — a hook,
    /// or the next run of the binary — is the one that fails.
    ///
    /// <para>No <c>ClearAllPools</c> anywhere here: each sandbox has its own database path and
    /// therefore its own pool, so both halves are deterministic and neither can be contaminated
    /// by a test running beside it.</para>
    /// </remarks>
    [Fact]
    public void AConnectionThatLoadedNothing_StillInheritsTheExtensionFromThePool()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new SandboxHome();
        VectorExtensionFile.InstallInto(sandbox.Home);

        using (var loader = EngramDatabase.Open(sandbox.Home))
        {
            Assert.True(AnswersVectorQueries(loader));
        }

        // Same path, so the same pool — and deliberately no library directory, so this
        // connection does not load the extension itself.
        using var borrower = EngramDatabase.Open(sandbox.Home.DatabasePath);

        Assert.True(
            AnswersVectorQueries(borrower),
            "A connection that never loaded sqlite-vec failed to answer a vector query. That is "
                + "the pool no longer recycling handles, which would make an opt-in loader safe "
                + "and this test's premise obsolete — check before deleting the eager load.");
    }

    /// <summary>
    /// The other half: without a warm pool the same connection cannot answer, which is what
    /// makes the inheritance above attributable to pooling rather than to something ambient.
    /// </summary>
    [Fact]
    public void AConnectionThatLoadedNothing_OnAColdPool_CannotAnswer()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new SandboxHome();
        VectorExtensionFile.InstallInto(sandbox.Home);

        using var connection = EngramDatabase.Open(sandbox.Home.DatabasePath);

        Assert.False(AnswersVectorQueries(connection));
    }
}
