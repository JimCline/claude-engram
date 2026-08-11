using System.Text;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The primary acceptance instrument for the SQL ranker (spec §3.1): runs the object ranker
/// (<see cref="RecallEngine.Explain"/>, demoted to test-support per §3.4) and <see cref="RecallRanker.Rank"/>
/// over the same store and the same query, and asserts the candidate lists agree element by element,
/// field by field, plus <c>TokensUsed</c> and <c>Coverage</c>.
/// </summary>
/// <remarks>
/// The corpus here is hand-built rather than a copy of the real store or the 50k synthetic fixture,
/// because those exist only as scratch files outside the repo for this round of implementation — a
/// committed test that hardcoded that path would fail for every future reader. Both larger fixtures
/// were run once by hand instead, against the real store snapshot and the 50k-fact synthetic store,
/// and the results are reported alongside this file rather than encoded into it. See the
/// implementor's report for those numbers.
///
/// The query set follows the predecessor spec's §9.3.1 sourcing (adopted per spec §3.1) as far as a
/// hand-built corpus allows: source 1 (telemetry) is not used — reading the real store's telemetry
/// was out of scope for a sandboxed corpus and is reported separately — but sources 2 (every distinct
/// indexable token, exhaustive here since the vocabulary is small), 3 (seeded pairs/triples) and 4
/// (degenerate and adversarial, by name) are all present.
/// </remarks>
public class RecallRankerEquivalenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static long Write(
        SqliteConnection connection, string slug, string body, string learnedVia = "stated", string? details = null) =>
        FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/" + slug, "note", "states", body, "project", learnedVia, Details: details),
            T0).FactId;

    /// <summary>
    /// Writes a fact whose subject display name is chosen independently of its path slug, and
    /// re-indexes <c>fact_token</c> under that name — mirroring <see cref="SessionFacts.Append"/>'s
    /// own rename dance. Needed because the default entity name is the path's last segment (so
    /// <see cref="Write"/> alone can never produce a subject token absent from the path — the two
    /// are the same string), and <c>fact_fts</c> indexes <c>path</c> as well as <c>body</c> and
    /// <c>predicate</c> (<c>docs/engram-schema.sql</c>), so a naively "distinctive" slug is
    /// FTS-findable via its own path the moment it is also the display name.
    /// </summary>
    private static long WriteWithSubjectName(SqliteConnection connection, string slug, string subjectName, string body)
    {
        var id = Write(connection, slug, body);
        var path = "/knowledge/testing/" + slug;

        using var transaction = EngramDatabase.BeginWrite(connection);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE entity SET name = $name WHERE path = $path;";
            command.Parameters.AddWithValue("$name", subjectName);
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }

        FactTokenIndex.Remove(connection, transaction, id);
        FactTokenIndex.Add(connection, transaction, id);
        transaction.Commit();

        return id;
    }

    /// <summary>
    /// The bulk of §3.1: every distinct indexed token as a one-term query (source 2, exhaustive —
    /// the corpus is small enough that sampling would only lose coverage for nothing), a curated set
    /// of pairs and triples (source 3), and the degenerate/adversarial queries from source 4 that do
    /// <b>not</b> trigger the §2.5 stopword-fallback divergence (that carve-out has its own test,
    /// below, per the spec's explicit instruction to assert it by name rather than silently exclude
    /// it here).
    /// </summary>
    [Fact]
    public void RecallRanker_MatchesTheObjectRanker_ElementByElementFieldByField()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "kestrel-both", "Kestrel binds loopback only.");
        Write(connection, "loopback-only", "The listener binds loopback addresses.");
        Write(connection, "pragma", "Every connection sets its own pragma.");
        Write(connection, "fox", "The quick brown fox jumps over the lazy dog.");
        Write(connection, "cafe", "café naïve 東京 — non-ASCII survives round-tripping.");
        Write(connection, "unrelated", "Nothing here concerns listeners or binding at all.", "inferred");

        // D64's trap: DetailsChars is computed by two separate derivations (FactCatalog.ToCannedFact
        // and RecallRanker's SQL projection), and neither the sweep above nor AssertCandidatesEqual's
        // Line comparison can catch the two disagreeing unless a seeded fact actually carries Details.
        Write(
            connection, "gannet", "Gannets dive from height to catch fish.",
            details: "Depth beyond the statement, present only to make the two DetailsChars derivations comparable.");

        var queries = new List<string>();

        // Source 2: every distinct token the index actually holds, one-term queries — exhaustive.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT token FROM fact_token ORDER BY token;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                queries.Add(reader.GetString(0));
            }
        }

        // Source 3: seeded pairs and triples over the same vocabulary.
        queries.AddRange([
            "kestrel loopback",
            "connection pragma",
            "quick brown fox",
            "loopback addresses listener",
            "binds loopback only",
        ]);

        // Source 4: degenerate and adversarial, by name — excluding the stopword-fallback cases
        // ("the", "a of and", "ab", "x y"), which diverge by design (§2.5) and are asserted
        // separately below.
        queries.AddRange([
            "",
            "   ",
            "!!!",
            "the pragma", // mixed: stopword dropped, indexable term still used — no fallback triggered
            "café naïve 東京",
            "kestrels", // plural — porter-stemmed match, literal overlap miss
            "connections",
            "pragmas",
        ]);

        // A pasted paragraph of 600+ distinct tokens (source 4) — none of them in this corpus, so
        // both paths must agree on "nothing", which is itself the assertion worth making: a term
        // list this large must not crash statement compilation or json_each binding.
        var paragraph = new StringBuilder();
        for (var i = 0; i < 620; i++)
        {
            paragraph.Append("zzzterm").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        }

        queries.Add(paragraph.ToString());

        Assert.True(queries.Count >= 20, $"query set only has {queries.Count} entries — this proves too little");

        var (currentSessionFacts, priorSessionFacts) = SessionFacts.Read(connection, null, T0);
        var facts = FactCatalog.ReadLongTerm(connection, T0);

        foreach (var query in queries)
        {
            var lexicalRanks = FactStore
                .SearchRanked(connection, query, RetrievalSettings.DefaultSeedK)
                .ToDictionary(h => h.FactId, h => h.Rank);

            var perQueryVectorQuery = VectorLane.PrepareQuery(
                connection, sandbox.Home, EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)), query, _ => null);

            var oracle = RecallEngine.Explain(
                query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks,
                new Dictionary<long, int>(), RetrievalSettings.DefaultBudgetTokens);

            var actual = RecallRanker.Rank(
                connection, query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
                currentSessionId: null, T0, perQueryVectorQuery);

            AssertCandidatesEqual(oracle.Candidates, actual.Candidates, query);
            Assert.True(oracle.TokensUsed == actual.TokensUsed, $"query '{query}': TokensUsed differs — expected {oracle.TokensUsed}, got {actual.TokensUsed}");
            Assert.True(oracle.Coverage == actual.Coverage, $"query '{query}': Coverage differs — expected {oracle.Coverage}, got {actual.Coverage}");
        }
    }

    /// <summary>
    /// Spec §2.5's accepted divergence, asserted by name rather than silently excluded from the
    /// sweep above. <see cref="RecallEngine.TokenizeQuery"/> falls back to the query's *unfiltered*
    /// tokens when every token is a stopword or shorter than 3 characters — and <c>fact_token</c>
    /// deliberately never indexes those tokens (ruling 7), so the overlap lane cannot find what the
    /// object ranker's in-memory scan still can. The lexical lane is unaffected by any of this — FTS5
    /// has no stopword list here — so it is what proves the divergence is exactly this and nothing
    /// more.
    /// </summary>
    [Theory]
    [InlineData("the")]
    [InlineData("a of and")]
    [InlineData("ab")]
    [InlineData("x y")]
    public void RecallRanker_ReproducesTheDocumentedStopwordDivergence(string query)
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "fox", "The quick brown fox jumps over the lazy dog and a cat.");
        Write(connection, "pragma", "Every connection sets its own pragma.");
        Write(connection, "shortcode", "The label reads x and y, plus ab as a short code.");

        var (currentSessionFacts, priorSessionFacts) = SessionFacts.Read(connection, null, T0);
        var facts = FactCatalog.ReadLongTerm(connection, T0);
        var lexicalRanks = FactStore
            .SearchRanked(connection, query, RetrievalSettings.DefaultSeedK)
            .ToDictionary(h => h.FactId, h => h.Rank);
        var vectorQuery = VectorLane.PrepareQuery(
            connection, sandbox.Home, EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)), query, _ => null);

        var oracle = RecallEngine.Explain(
            query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks,
            new Dictionary<long, int>(), RetrievalSettings.DefaultBudgetTokens);

        var actual = RecallRanker.Rank(
            connection, query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            currentSessionId: null, T0, vectorQuery);

        // The divergence must actually fire, or this test proves nothing (the CLAUDE.md standard
        // for a guard: prove it can fail). The object ranker's overlap lane must find something via
        // its in-memory scan for the fallback to matter.
        Assert.Contains(oracle.Candidates, c => c.OverlapRank is not null);

        // The SQL ranker's overlap lane must find nothing — fact_token holds none of these tokens.
        Assert.All(actual.Candidates, c => Assert.Null(c.OverlapRank));

        // What is unaffected: the lexical lane, which both paths compute identically regardless of
        // this divergence. The set of facts found lexically must match exactly.
        var oracleLexicalIds = oracle.Candidates.Where(c => c.LexicalRank is not null).Select(c => c.FactId).Order().ToList();
        var actualLexicalIds = actual.Candidates.Where(c => c.LexicalRank is not null).Select(c => c.FactId).Order().ToList();
        Assert.Equal(oracleLexicalIds, actualLexicalIds);
    }

    /// <summary>
    /// Spec §3.1's explicit safety-net case: a fact findable by <b>no lane but overlap</b> — its
    /// subject token appears in neither body nor path. This is how a lane that has quietly stopped
    /// contributing would be caught; the sweep above would not distinguish "found via overlap" from
    /// "found via lexical" for a fact both lanes reach.
    /// </summary>
    [Fact]
    public void RecallRanker_FindsAPlantedOverlapOnlyFact_TheOverlapLaneAloneCanReach()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // The path slug ("planted-note") is deliberately different from the subject name
        // ("zzzoverlaponly") — fact_fts indexes path as well as body and predicate, so a fact whose
        // display name equals its own path slug is findable by the lexical lane via that path, which
        // would make this fact useless as an overlap-only planted case.
        const string SubjectToken = "zzzoverlaponly";
        var plantedId = WriteWithSubjectName(
            connection, "planted-note", SubjectToken, "This note explains nothing distinctive about anything.");

        var facts = FactCatalog.ReadLongTerm(connection, T0);
        var planted = Assert.Single(facts, f => f.Id == FactCatalog.HandleFor(plantedId));
        Assert.Equal(SubjectToken, planted.Subject);

        var query = planted.Subject;
        Assert.DoesNotContain(query, planted.Body, StringComparison.OrdinalIgnoreCase);

        var (currentSessionFacts, priorSessionFacts) = SessionFacts.Read(connection, null, T0);
        var lexicalRanks = FactStore
            .SearchRanked(connection, query, RetrievalSettings.DefaultSeedK)
            .ToDictionary(h => h.FactId, h => h.Rank);
        var vectorQuery = VectorLane.PrepareQuery(
            connection, sandbox.Home, EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)), query, _ => null);

        // Confirm the query is genuinely overlap-only before trusting the comparison: no lexical hit.
        Assert.Empty(lexicalRanks);

        var oracle = RecallEngine.Explain(
            query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks,
            new Dictionary<long, int>(), RetrievalSettings.DefaultBudgetTokens);
        var actual = RecallRanker.Rank(
            connection, query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            currentSessionId: null, T0, vectorQuery);

        var oracleHit = Assert.Single(oracle.Candidates, c => c.FactId == plantedId);
        var actualHit = Assert.Single(actual.Candidates, c => c.FactId == plantedId);
        Assert.NotNull(oracleHit.OverlapRank);
        Assert.NotNull(actualHit.OverlapRank);
        Assert.Null(oracleHit.LexicalRank);
        Assert.Null(actualHit.LexicalRank);

        AssertCandidatesEqual(oracle.Candidates, actual.Candidates, query);
    }

    /// <summary>
    /// Spec ruling 6: the prior-session <c>p1</c>/<c>p2</c> discriminator ranges over the whole
    /// prior-session set, not over candidates, and the origin split (current/prior/long-term) has to
    /// match between the in-memory partition (<see cref="SessionFacts.Read"/>) and the SQL
    /// <c>prior_sessions</c> CTE.
    /// </summary>
    [Fact]
    public void RecallRanker_MatchesAcrossSessionOriginsAndPriorSessionDiscriminators()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "longterm-kestrel", "Kestrel binds loopback only.");
        // 4d's trap, mirroring 4a's: DetailsChars is computed by two derivations for session
        // facts too (SessionFacts.ToSessionFact and RecallRanker's own), so at least one
        // current-session and one prior-session fact here must carry Details or neither
        // derivation disagreeing would redden this test.
        SessionFacts.Append(
            connection, "session-current", "Kestrel needs a loopback binding here too.", null, null, null, T0,
            details: "Depth beyond the statement, to exercise the current-session derivation.");
        SessionFacts.Append(
            connection, "session-prior-a", "Kestrel loopback binding noted in an earlier session.", null, null, null, T0,
            details: "Depth beyond the statement, to exercise the prior-session derivation.");
        SessionFacts.Append(connection, "session-prior-b", "Another earlier session also noted kestrel loopback.", null, null, "worker", T0);

        const string Query = "kestrel loopback binding";

        var currentSessionId = SessionStore.FindSession(connection, "session-current");
        var (currentSessionFacts, priorSessionFacts) = SessionFacts.Read(connection, "session-current", T0);
        var facts = FactCatalog.ReadLongTerm(connection, T0);
        var lexicalRanks = FactStore
            .SearchRanked(connection, Query, RetrievalSettings.DefaultSeedK)
            .ToDictionary(h => h.FactId, h => h.Rank);
        var vectorQuery = VectorLane.PrepareQuery(
            connection, sandbox.Home, EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)), Query, _ => null);

        var oracle = RecallEngine.Explain(
            Query, facts, currentSessionFacts, priorSessionFacts, lexicalRanks,
            new Dictionary<long, int>(), RetrievalSettings.DefaultBudgetTokens);
        var actual = RecallRanker.Rank(
            connection, Query, RetrievalSettings.DefaultBudgetTokens, RetrievalSettings.DefaultSeedK,
            currentSessionId, T0, vectorQuery);

        Assert.Contains(oracle.Candidates, c => c.Origin == FactOrigin.CurrentSession);
        Assert.Contains(oracle.Candidates, c => c.Origin == FactOrigin.PriorSession);
        Assert.Contains(oracle.Candidates, c => c.Origin == FactOrigin.LongTerm);

        AssertCandidatesEqual(oracle.Candidates, actual.Candidates, Query);
    }

    private static void AssertCandidatesEqual(
        IReadOnlyList<RecallCandidate> expected, IReadOnlyList<RecallCandidate> actual, string query)
    {
        Assert.True(
            expected.Count == actual.Count,
            $"query '{query}': expected {expected.Count} candidates, got {actual.Count}.\n"
                + $"expected: {string.Join(", ", expected.Select(c => c.Handle))}\n"
                + $"actual:   {string.Join(", ", actual.Select(c => c.Handle))}");

        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            var equal = e.FactId == a.FactId
                && e.Handle == a.Handle
                && e.Line == a.Line
                && Math.Abs(e.Fused - a.Fused) < 1e-9
                && e.OverlapRank == a.OverlapRank
                && e.LexicalRank == a.LexicalRank
                && e.VectorRank == a.VectorRank
                && e.Origin == a.Origin
                && e.Tokens == a.Tokens
                && e.Packed == a.Packed;

            Assert.True(
                equal,
                $"query '{query}': candidate {i} differs.\n"
                    + $"expected: {e}\n"
                    + $"actual:   {a}");
        }
    }
}
