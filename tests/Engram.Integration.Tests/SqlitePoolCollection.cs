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
/// clears separate pools too. Only serialising the classes addresses the actual sharing.</para>
///
/// <para><b>The victim does not have to be a member.</b> This collection first only serialised its
/// members against each other, on the reasoning that a cleared handle can only hurt a class that
/// was observing one. It cannot: <c>ClearAllPools</c> disposes handles the pool has already handed
/// out, so the class that loses is whichever one is between renting a connection and using it —
/// an ordinary <c>SandboxHome</c> construction, most of the time. Measured: a full run failed in
/// <c>ServerLifecycleTests</c> with <c>ObjectDisposedException</c> on <c>SQLitePCL.sqlite3</c>,
/// thrown inside <c>EngramInitializer.Initialize</c>, while a non-member class was clearing pools.
/// So the collection runs alone — nothing else may be opening a connection while a member is
/// destroying the pool. That costs the wall-clock of running these classes serially — measured at
/// about a second across the assembly, 5s to 6s — and it is the only version of this that holds.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlitePoolCollection
{
    public const string Name = "sqlite-pool";
}
