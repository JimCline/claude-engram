using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

[Collection(SqlitePoolCollection.Name)]
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
    /// <para>Each sandbox has its own database path and therefore its own pool, which is not by
    /// itself enough to make this deterministic: <c>ClearAllPools</c> in a class running beside
    /// this one empties every pool in the process, including this one, between the dispose and
    /// the reopen. That is why this class is in <see cref="SqlitePoolCollection"/>.</para>
    /// </remarks>
    /// <summary>
    /// Why <see cref="SqlitePoolCollection"/> exists, stated as an assertion rather than a
    /// comment: a pool is per connection string, but clearing is not.
    /// </summary>
    /// <remarks>
    /// The intuition that a private database path gives a private pool is correct and useless
    /// here — <c>ClearAllPools</c> reaches every one of them. A class that clears pools to show
    /// what a cold process sees therefore deletes the recycled handle any other class is
    /// observing, wherever its database lives. This suite hit exactly that, intermittently,
    /// before the two classes were serialised.
    /// </remarks>
    [Fact]
    public void ClearAllPools_EmptiesThePoolOfADatabaseItWasNeverPointedAt()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var mine = new SandboxHome();
        using var elsewhere = new SandboxHome();
        VectorExtensionFile.InstallInto(mine.Home);

        using (var loader = EngramDatabase.Open(mine.Home))
        {
            Assert.True(AnswersVectorQueries(loader));
        }

        // Stands in for the unrelated test class that clears pools to prove a point about a
        // completely different database.
        using (var unrelated = EngramDatabase.Open(elsewhere.Home))
        {
            Assert.False(AnswersVectorQueries(unrelated));
        }

        SqliteConnection.ClearAllPools();

        using var borrower = EngramDatabase.Open(mine.Home.DatabasePath);

        Assert.False(
            AnswersVectorQueries(borrower),
            "The pooled handle survived ClearAllPools, so pools are not process-wide after all "
            + "and SqlitePoolCollection has nothing to protect.");
    }

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
    /// <see cref="EngramDatabase.ReleasePooledConnections"/> empties one database's pool, and only
    /// that one.
    /// </summary>
    /// <remarks>
    /// <para>Both halves are load-bearing, and they fail to opposite mistakes. The released database
    /// going cold is what a release that does nothing loses — including the quiet version, where
    /// <c>ReleasePooledConnections</c> builds a connection string that no longer matches
    /// <see cref="EngramDatabase.Open(string, string?)"/>'s: a pool is keyed by that exact text, and
    /// clearing a key nothing was ever stored under succeeds and reports nothing. The untouched
    /// database staying warm is what <c>ClearAllPools</c> loses, and that is not tidiness — it
    /// disposes handles the pool has already handed out, so what breaks belongs to whatever is
    /// mid-rent elsewhere in the process.</para>
    ///
    /// <para>Windows is the only platform where the release is load-bearing in production, and it is
    /// the one platform this suite cannot run here, so the property is asserted directly rather than
    /// through the deletion it enables.</para>
    /// </remarks>
    [Fact]
    public void ReleasingOnePool_LeavesEveryOtherDatabasesPoolWarm()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var released = new SandboxHome();
        using var untouched = new SandboxHome();
        VectorExtensionFile.InstallInto(released.Home);
        VectorExtensionFile.InstallInto(untouched.Home);

        // Warms both pools: each handle goes back with sqlite-vec still registered on it.
        using (var loader = EngramDatabase.Open(released.Home))
        {
            Assert.True(AnswersVectorQueries(loader));
        }

        using (var loader = EngramDatabase.Open(untouched.Home))
        {
            Assert.True(AnswersVectorQueries(loader));
        }

        EngramDatabase.ReleasePooledConnections(released.Home.DatabasePath);

        // No library directory on either, so neither loads anything itself and a vector query
        // answers only on a handle the pool kept.
        using var afterRelease = EngramDatabase.Open(released.Home.DatabasePath);
        using var neverReleased = EngramDatabase.Open(untouched.Home.DatabasePath);

        Assert.False(
            AnswersVectorQueries(afterRelease),
            "The pooled handle survived ReleasePooledConnections, so nothing was released and the "
                + "database file is still held open. On Windows that is an IOException on cleanup.");

        Assert.True(
            AnswersVectorQueries(neverReleased),
            "Releasing one database's pool emptied another's, which is ClearAllPools' behaviour and "
                + "not this method's. That form disposes connections already handed out, so it "
                + "breaks whichever unrelated caller happens to be between renting and using one.");
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
