using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// A schema-only database with <c>sqlite-vec</c> installed alongside it.
/// </summary>
/// <remarks>
/// Schema-only rather than fully initialized, because <c>EngramInitializer</c> seeds the canned
/// corpus and every count in the vector tests is a count of facts — dozens of seeded ones would
/// make each assertion about the corpus rather than about what the test wrote.
/// </remarks>
internal sealed class VectorSandbox : IDisposable
{
    private readonly SandboxHome sandbox = new(initialize: false);

    public VectorSandbox()
    {
        VectorExtensionFile.InstallInto(sandbox.Home);
        Connection = EngramDatabase.OpenInitialized(sandbox.Home);
    }

    public SqliteConnection Connection { get; }

    public EngramHome Home => sandbox.Home;

    public long AddFact(string name, string body) =>
        FactStore.Remember(
            Connection,
            new FactWrite(
                SubjectPath: $"test/{name}",
                SubjectKind: "concept",
                Predicate: "is",
                Body: body,
                Scope: "project",
                LearnedVia: "stated"),
            DateTimeOffset.UnixEpoch.AddSeconds(1)).FactId;

    /// <summary>A regenerable, object-bearing fact — the shape edge-fact-lane-eligibility.md
    /// §2.2 excludes from the text and vector lanes.</summary>
    public long AddEdgeFact(string name, string predicate, string objectName) =>
        FactStore.Remember(
            Connection,
            new FactWrite(
                SubjectPath: $"test/{name}",
                SubjectKind: "symbol",
                Predicate: predicate,
                Body: $"{predicate} {objectName}",
                Scope: "code",
                LearnedVia: "observed",
                Regenerable: true,
                ObjectPath: $"test/{objectName}",
                ObjectKind: "symbol-name"),
            DateTimeOffset.UnixEpoch.AddSeconds(1)).FactId;

    public void AddFacts(int count)
    {
        for (var i = 0; i < count; i++)
        {
            AddFact($"f{i}", $"fact number {i}");
        }
    }

    public void Close(long factId) =>
        FactStore.Forget(Connection, factId, "superseded", DateTimeOffset.UnixEpoch.AddSeconds(2));

    public void Dispose()
    {
        Connection.Dispose();
        sandbox.Dispose();
    }
}
