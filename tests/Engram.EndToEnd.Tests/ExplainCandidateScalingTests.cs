using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>explain</c> costs what it prints plus the floor every query pays, never what it considered.
/// </summary>
/// <remarks>
/// <para>The rule under test is deliberately narrower than the spec's p50 target. Two other
/// readings were available and both are untestable here: the 50 ms number measures the machine
/// far more than the code, and the ratio at <c>Pack</c> level is ~1.2x — indistinguishable from
/// run-to-run noise, so a test asserting it could not fail for the right reason. What actually
/// broke was per-candidate work reaching the database, and that is a ratio a test can hold.</para>
///
/// <para>A ratio rather than a wall clock, on the model of
/// <see cref="FileTouchedBudgetTests"/>: both arms run the same binary against the same store, so
/// a loaded machine moves them together and only work proportional to the candidate set separates
/// them. Every sample asserts exit 0, because the failure this guards against is also reachable as
/// a hard error — past SQLite's variable ceiling the oversized statement throws rather than
/// crawling, and a crashed arm would otherwise time as the fastest one and pass.</para>
/// </remarks>
public class ExplainCandidateScalingTests
{
    /// <summary>
    /// Every one of these is a candidate on the hot arm and none is on the no-match arm, which is
    /// the only difference between the two runs.
    /// </summary>
    /// <remarks>
    /// 20,000 was not raised, because it already separates the arms by far more than the margin:
    /// the unbounded per-candidate read measured 12.9x the no-match arm here, against a margin of
    /// 3. Going larger would buy nothing and spend seeding time on every run. It is also under
    /// SQLite's 32,766-variable ceiling on purpose — past it the oversized statement throws, and a
    /// guard whose planted defect crashes rather than crawls is measuring the wrong thing.
    /// </remarks>
    private const int Facts = 20_000;

    /// <summary>
    /// A token every synthetic body contains literally. The overlap lane compares literal tokens
    /// and does not stem, so this reaches all of them and the fusion has 20,000 candidates to rank.
    /// </summary>
    private const string HotToken = "zzhotzz";

    /// <summary>Matched by nothing in any lane, so the same pipeline runs over zero candidates.</summary>
    private const string NoMatchToken = "zzzznomatch";

    private const int Warmup = 2;
    private const int Samples = 5;

    /// <summary>
    /// Generous on purpose. The two arms are not expected to differ much — the floor is shared and
    /// dominates — but the hot arm does legitimately format and sort what it found, so a margin of
    /// 1 would fail on honest work: the passing ratio is 1.3x, not 1.0x. 3x sits well clear of
    /// both that and the 12.9x the regression produces.
    /// </summary>
    /// <remarks>
    /// What this margin does <i>not</i> resolve, measured: restoring the unbounded candidate list
    /// while leaving the chunked read in place lands at 1.89x and stays green. The two bounds in
    /// <c>ReadTiers</c> overlap in what they cost, so this holds the pair, not each half — which
    /// is also the evidence that chunking is not merely a correctness measure at this size.
    ///
    /// <para>Do not try to tune this into a bounding test. Separating 1.89x from 1.3x needs a
    /// margin narrower than the noise either arm carries, which trades a real guard for an
    /// intermittent one. The display bound is pinned on its own, deterministically and without a
    /// clock, by <c>RetrievalExplainerTests.Explain_ReadsTheProvenanceTierOnlyAsFarAsTheCallerWillPrint</c>,
    /// which asserts which candidates carry a tier rather than how long the read took.</para>
    /// </remarks>
    private const double Margin = 3.0;

    [Fact]
    public void Explain_CostScalesWithWhatItPrints_NotWithTheNumberOfCandidates()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        Seed(Path.Combine(home.Root, "engram.db"), Facts);

        // Without this the guard rots into a tautology: anything that stops the seed from
        // producing candidates — a reworded body, a change to what init seeds, a tokenizer that
        // splits the marker — collapses both arms onto the shared floor, and a timing ratio of two
        // identical runs passes forever while proving nothing. These counts are deterministic
        // because this test writes the store, which is why they may be asserted here even though
        // counts drawn from the real corpus may not be.
        var hotOutput = Run(home.Root, HotToken);
        var noMatchOutput = Run(home.Root, NoMatchToken);

        Assert.Contains($"of {Facts} candidates returned", hotOutput, StringComparison.Ordinal);
        Assert.Contains("more (--limit to see them)", hotOutput, StringComparison.Ordinal);
        Assert.Contains("of 0 candidates returned", noMatchOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("more (--limit to see them)", noMatchOutput, StringComparison.Ordinal);

        for (var i = 0; i < Warmup; i++)
        {
            Time(home.Root, NoMatchToken);
            Time(home.Root, HotToken);
        }

        // Interleaved, and the minimum of each arm rather than a median: process-start noise is
        // one-sided, so the fastest sample of each converges on the deterministic work while a
        // median wanders with whatever else the machine is doing.
        var noMatch = new List<double>(Samples);
        var hot = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            noMatch.Add(Time(home.Root, NoMatchToken));
            hot.Add(Time(home.Root, HotToken));
        }

        var noMatchCost = noMatch.Min();
        var hotCost = hot.Min();

        Assert.True(
            hotCost <= noMatchCost * Margin,
            $"explain over {Facts} candidates took {hotCost:0} ms against {noMatchCost:0} ms over none "
                + $"(fastest of {Samples}, ratio {hotCost / noMatchCost:0.0}x, margin {Margin:0.0}x). "
                + "explain must cost the shared floor plus the rows it prints, never work proportional "
                + "to the candidate set. Both bounds in RetrievalExplainer.ReadTiers are needed to hold "
                + "this and this asserts them as a pair, so look at each: the display bound and the "
                + "500-id chunking. Which of the two regressed is not decidable from here — "
                + "Explain_ReadsTheProvenanceTierOnlyAsFarAsTheCallerWillPrint pins the display bound "
                + "on its own.");
    }

    /// <summary>
    /// Writes <paramref name="count"/> live facts whose bodies all carry <see cref="HotToken"/>.
    /// </summary>
    /// <remarks>
    /// <para>Straight into <c>fact</c> rather than through <c>engram remember</c>, which would be
    /// 20,000 process starts. The FTS index needs no rebuild here and must not be given one:
    /// <c>fact_fts_insert</c> fires on exactly this insert, so the index tracks the live set as
    /// the rows land. Nothing may reach for FTS5's own <c>'rebuild'</c>, which re-reads closed
    /// facts the index deliberately excludes.</para>
    ///
    /// <para>Ids are assigned above whatever <c>init</c> seeded rather than read back per row, so
    /// each fact is one round trip and each gets its own subject — <c>ux_fact_live</c> is unique
    /// on subject and predicate among live facts, so a shared subject would collide on the second
    /// row.</para>
    /// </remarks>
    private static void Seed(string databasePath, int count)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();

        var entityBase = Scalar(connection, "SELECT COALESCE(MAX(id), 0) FROM entity;");
        var factBase = Scalar(connection, "SELECT COALESCE(MAX(id), 0) FROM fact;");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Execute(connection, "BEGIN IMMEDIATE;");

        using (var entity = connection.CreateCommand())
        using (var fact = connection.CreateCommand())
        {
            entity.CommandText =
                "INSERT INTO entity (id, path, kind, name, created_at) VALUES ($id, $path, 'note', $name, $now);";
            fact.CommandText =
                """
                INSERT INTO fact
                  (id, subject_id, predicate, body, path, scope, learned_via, regenerable, valid_from, created_at)
                VALUES
                  ($id, $subject, 'states', $body, $path, 'project', 'stated', 0, $now, $now);
                """;

            for (var i = 1; i <= count; i++)
            {
                var name = "scaling-" + i.ToString(CultureInfo.InvariantCulture);
                var path = "/knowledge/perf/scaling/" + name;

                entity.Parameters.Clear();
                entity.Parameters.AddWithValue("$id", entityBase + i);
                entity.Parameters.AddWithValue("$path", path);
                entity.Parameters.AddWithValue("$name", name);
                entity.Parameters.AddWithValue("$now", now);
                entity.ExecuteNonQuery();

                fact.Parameters.Clear();
                fact.Parameters.AddWithValue("$id", factBase + i);
                fact.Parameters.AddWithValue("$subject", entityBase + i);
                fact.Parameters.AddWithValue("$body", Body(i));
                fact.Parameters.AddWithValue("$path", path);
                fact.Parameters.AddWithValue("$now", now);
                fact.ExecuteNonQuery();
            }
        }

        Execute(connection, "COMMIT;");
    }

    // Long enough that formatting and the token estimate cost something recognisable per fact,
    // since understating the floor would flatter the ratio this asserts on.
    private static string Body(int i) =>
        $"Synthetic belief {i.ToString(CultureInfo.InvariantCulture)} carrying the {HotToken} marker, "
        + "written at a length a real note reaches so the per-fact floor is not understated.";

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Run(string home, string query)
    {
        var (exitCode, stdout, stderr) = EngramProcess.Run(home, "explain", query);

        Assert.True(exitCode == 0, $"explain \"{query}\" exited {exitCode}: {stderr}");

        return stdout;
    }

    private static double Time(string home, string query)
    {
        var stopwatch = Stopwatch.StartNew();
        var (exitCode, _, stderr) = EngramProcess.Run(home, "explain", query);
        stopwatch.Stop();

        Assert.True(exitCode == 0, $"explain \"{query}\" exited {exitCode}: {stderr}");

        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
