using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Spec §3.2: the primary guard on the SQL ranker, not a timing test. Pins the properties that
/// were actually captured via <c>EXPLAIN QUERY PLAN</c> against a real store before this assertion
/// text was written ("pin reality, not a hope") — not the properties hoped for.
/// </summary>
/// <remarks>
/// <para><b>What is NOT asserted, and why.</b> Spec §3.2's literal text says "no SCAN of
/// <c>fact</c> anywhere in the plan." Captured reality, at both a 5-fact sandbox and a 50,000-fact
/// synthetic corpus (both with <c>ANALYZE</c>), is that this does not hold for the statement as
/// spec §2.3 literally specifies it — two sites scan <c>fact</c>:</para>
/// <list type="bullet">
/// <item><c>prior_sessions</c> materializes via <c>SCAN f USING INDEX ix_fact_session</c>. Every
/// rewrite of its path filter tried here — <c>substr(e.path,...)</c> through the entity join (as
/// spec §2.4 literally writes it), <c>substr(f.path,...)</c> against the denormalized column
/// directly, and <c>f.path LIKE '/sessions/%'</c> — produced the identical scan, at both a
/// realistic 49,950:50 long-term:session mix and an all-session corpus. The planner is choosing
/// <c>ix_fact_session</c> for the <c>DISTINCT ... ORDER BY session_id</c> it needs regardless of
/// the path predicate's selectivity, not failing to use an index — there does not appear to be a
/// query-text-only fix, and ruling 6 requires ranging over the *entire* prior-session set, which is
/// what this scan is doing. Reported as a spec-level finding, not fixed here.</item>
/// <item>The <c>versions</c> correlated subquery scans <c>fact</c> (as <c>SCAN f2</c>) when it
/// joins <c>entity e2</c> for <c>e2.path</c>, exactly as spec §2.3 writes it — confirmed at 50,000
/// facts. Substituting the already-live, already-indexed denormalized <c>fact.path</c> column
/// (<c>docs/engram-schema.sql:126,159</c>) for <c>entity.path</c> turns this into
/// <c>SEARCH f2 USING INDEX ix_fact_path (path=?)</c>, confirmed via the identical EQP capture —
/// no scan at all. Not applied here: <c>fact.path</c> is denormalized, derived state (D8), while
/// <c>entity.path</c> is the authoritative live value, so this trade (a correlated per-candidate
/// scan of the whole live-fact count, versus a possible staleness window between a rename and the
/// repair/compact cycle that resyncs the denormalized copy) is a correctness/performance call for
/// the spec's author, not this test.</item>
/// </list>
/// <para>Asserting the literal §3.2 text here would either fail permanently for a property this
/// implementation did not regress (it followed §2.3's literal SQL), or silently paper over two real
/// findings by weakening the assertion without saying so. Both are worse than naming the two
/// exceptions explicitly and asserting the rest.</para>
/// </remarks>
public class RecallRankerPlanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The two known, investigated exceptions to "no SCAN of fact" — see the class remarks.</summary>
    private static readonly string[] KnownFactScanLines =
    [
        "SCAN f USING INDEX ix_fact_session",
        "SCAN f2",
    ];

    /// <summary>
    /// <c>ANALYZE</c> is deliberately not exercised here. Captured at this fixture's 5-fact scale, it
    /// changes the plan qualitatively — <c>SEARCH f USING INTEGER PRIMARY KEY (rowid=?)</c> becomes a
    /// plain <c>SCAN f</c> throughout, because the optimizer correctly judges that scanning 5 rows is
    /// cheaper than an indexed nested loop. That is real ANALYZE behavior, but it is a small-table
    /// artifact unrelated to the question §3.2 (and NEEDS-EVIDENCE 6) actually asks — what the
    /// planner does at real scale — and asserting against it here would either pin a fact about a
    /// 5-row table forever or force this guard to swallow scan lines with no bearing on the ranker's
    /// real behavior. The <c>ANALYZE</c> question is answered against the 50,097-fact fixture
    /// instead, and reported narratively rather than pinned to a test that cannot see that store.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRankingStatement_ScansNeitherFactNorFactTokenOutsideTheNamedExceptions(bool overlapAvailable)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        for (var i = 0; i < 5; i++)
        {
            FactStore.Remember(
                connection,
                new FactWrite(
                    "/knowledge/testing/plan-" + i, "note", "states",
                    $"Fact number {i} about kestrel loopback binding.", "project", "stated"),
                T0);
        }

        var lines = CapturePlan(connection, overlapAvailable);
        var unexpectedFactScans = UnexplainedFactScans(lines);

        Assert.True(
            unexpectedFactScans.Count == 0,
            $"overlapAvailable={overlapAvailable}: unexpected SCAN of fact — "
                + $"{string.Join(" | ", unexpectedFactScans)}\nfull plan:\n{string.Join("\n", lines)}");

        var tokenAccess = lines.Where(line => line.Contains("fact_token", StringComparison.Ordinal)).ToList();
        Assert.True(
            tokenAccess.All(line => line.Contains("SEARCH", StringComparison.Ordinal) && line.Contains("PRIMARY KEY", StringComparison.Ordinal))
                || tokenAccess.Count == 0,
            $"overlapAvailable={overlapAvailable}: fact_token reached other than by "
                + $"a primary-key search — {string.Join(" | ", tokenAccess)}");
    }

    /// <summary>
    /// Falsification: <see cref="TheRankingStatement_ScansNeitherFactNorFactTokenOutsideTheNamedExceptions"/>
    /// must be able to fail, or it proves nothing. Runs the guard's own logic against a plan with an
    /// injected, unexplained scan of <c>fact</c> and confirms it is caught.
    /// </summary>
    [Fact]
    public void Guard_CatchesAnUnexplainedScanOfFact()
    {
        var lines = new List<string> { "SCAN f USING INDEX some_other_index", "SEARCH ft USING PRIMARY KEY (token=?)" };

        Assert.NotEmpty(UnexplainedFactScans(lines));
    }

    private static List<string> UnexplainedFactScans(IEnumerable<string> lines) => lines
        .Where(line => line.Contains("SCAN", StringComparison.Ordinal))
        .Where(line => line.Contains(" f ", StringComparison.Ordinal) || line.Contains(" f2", StringComparison.Ordinal) || line.EndsWith(" f", StringComparison.Ordinal))
        .Where(line => !KnownFactScanLines.Any(known => line.Contains(known, StringComparison.Ordinal)))
        .ToList();

    private static List<string> CapturePlan(SqliteConnection connection, bool overlapAvailable)
    {
        var text = RecallRanker.StatementFor(overlapAvailable, false, "fact_vec");

        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN\n" + text;
        command.Parameters.AddWithValue("$terms", "[]");
        command.Parameters.AddWithValue("$match", "\"kestrel\"");
        command.Parameters.AddWithValue("$seedK", 32);
        command.Parameters.AddWithValue("$currentSessionId", DBNull.Value);
        command.Parameters.AddWithValue("$now", T0.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limit", 501);

        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }
}
