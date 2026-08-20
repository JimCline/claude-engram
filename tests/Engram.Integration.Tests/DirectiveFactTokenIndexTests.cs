using Engram.Core;

namespace Engram.Integration.Tests;

// Directives write through the standard FactStore.InsertFact -> FactTokenIndex.Add chokepoint
// with no special-casing (D-3: zero schema delta, ordinary fact rows). This is the guard proving
// that holds — an implementor who excluded directives at an index chokepoint instead of at query
// time would leave TokenIndexNeedsRebuild permanently true, since the excluded rows would never
// stop looking missing to a from-scratch recomputation.
[Collection(SqlitePoolCollection.Name)]
public class DirectiveFactTokenIndexTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FactToken_AgreesWithAFromScratchRecomputation_WithADirectivePresent()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0);
        DirectiveFacts.Add(connection, "never commit directly to main in this repo", T0.AddSeconds(1));

        var incremental = ReadAllRows(connection);
        FactTokenIndex.Rebuild(connection);
        var recomputed = ReadAllRows(connection);

        Assert.Equal(recomputed, incremental);
        Assert.NotEmpty(incremental);
    }

    private static List<(long FactId, string Token)> ReadAllRows(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fact_id, token FROM fact_token ORDER BY fact_id, token;";
        using var reader = command.ExecuteReader();

        var rows = new List<(long, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }
}
