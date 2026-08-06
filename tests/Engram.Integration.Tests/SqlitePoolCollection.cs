namespace Engram.Integration.Tests;

/// <summary>
/// Test classes that depend on, or destroy, the process-wide SQLite connection pool.
/// </summary>
/// <remarks>
/// <c>SqliteConnection.ClearAllPools()</c> is exactly as wide as its name says: every pool in
/// the process, not the one belonging to the caller's connection string. xUnit runs test classes
/// in parallel, so a class that clears pools to demonstrate what a cold process sees can delete
/// the recycled handle another class is halfway through observing — which is a real failure this
/// suite produced, not a hypothetical.
///
/// <para>Separate database paths do not help. They give separate pools, and <c>ClearAllPools</c>
/// clears separate pools too. Serialising the classes against each other is the only fix that
/// addresses the actual sharing, and it leaves the rest of the suite parallel.</para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SqlitePoolCollection
{
    public const string Name = "sqlite-pool";
}
