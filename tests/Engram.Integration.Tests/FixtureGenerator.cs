using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Scratch harness for the three fixtures in docs/memory-expansion/05b-fixture-spec.md §2-3.
/// Not part of the shipped test suite — seeds a fixed, persistent ENGRAM_HOME under /tmp so the
/// published binary can be pointed at it afterward for the real MCP measurement. Run one
/// fixture at a time via --filter; this file is removed once the fixtures are built.
/// </summary>
public class FixtureGenerator
{
    private static readonly string[] Dirs = ["src", "tests", "docs", "scripts"];
    private static readonly int[] SymbolFacts = [6, 2, 1, 1, 1];
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Generate_Base5k() => Generate("/tmp/engram-fixture-base5k", p: 8, d: 3, m: 3, f: 5);

    [Fact]
    public void Generate_Deep50k() => Generate("/tmp/engram-fixture-deep50k", p: 8, d: 3, m: 3, f: 50);

    [Fact]
    public void Generate_Broad50k() => Generate("/tmp/engram-fixture-broad50k", p: 80, d: 3, m: 3, f: 5);

    private static void Generate(string root, int p, int d, int m, int f)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);

        var home = EngramHome.Resolve(root, new Dictionary<string, string?>(), root, root);
        EngramInitializer.Initialize(home);

        using var connection = EngramDatabase.OpenInitialized(home);
        using var transaction = EngramDatabase.BeginWrite(connection);

        var counter = 0;
        var offset = 0;

        for (var pi = 0; pi < p; pi++)
        {
            var project = $"proj{pi:D3}";
            for (var di = 0; di < d; di++)
            {
                for (var mi = 0; mi < m; mi++)
                {
                    for (var fi = 0; fi < f; fi++)
                    {
                        var file = $"/projects/{project}/code/{project}/{Dirs[di]}/Mod{mi:D2}/File{fi:D3}.cs";

                        Emit(connection, transaction, file, "summary0", ref counter, ref offset);

                        for (var s = 0; s < SymbolFacts.Length; s++)
                        {
                            var symbolPath = $"{file}#Sym{s:D2}";
                            for (var k = 0; k < SymbolFacts[s]; k++)
                            {
                                Emit(connection, transaction, symbolPath, $"detail{k}", ref counter, ref offset);
                            }
                        }

                        Emit(connection, transaction, $"{file}#Sym00/Member0", "member", ref counter, ref offset);
                        Emit(connection, transaction, $"{file}#Sym00/Member1", "member", ref counter, ref offset);
                    }
                }
            }
        }

        transaction.Commit();
        Assert.True(counter > 0);
    }

    private static void Emit(
        SqliteConnection connection, SqliteTransaction transaction, string path, string predicate,
        ref int counter, ref int offset)
    {
        FactStore.Remember(
            connection,
            transaction,
            new FactWrite(path, "note", predicate, Body(counter), "notes", "stated"),
            T0.AddSeconds(offset++));
        counter++;
    }

    // Fixed length across every arm — 05b-fixture-spec.md §2 makes body length a first-order
    // term in the KB-per-fact measurement, so only the leading index may vary.
    private static string Body(int index)
    {
        var body = $"Fixture fact {index:D6} — synthetic body held at a constant length across every 05b arm so KB-per-fact stays comparable across shapes.";
        return body.Length > 120 ? body[..120] : body.PadRight(120, '.');
    }
}
