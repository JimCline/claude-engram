using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// Everything one call to <see cref="RecallRanker.Rank"/> produced, before <see cref="EngramMcpTools.Recall"/>
/// or <c>explain</c> turn it into their own output shape.
/// </summary>
public sealed record RankOutcome(
    string Query,
    IReadOnlyList<string> QueryTerms,
    IReadOnlyList<string> DroppedTerms,
    IReadOnlyList<RecallCandidate> Candidates,
    int BudgetTokens,
    int TokensUsed,
    RecallCoverage Coverage,
    int MatchedTotal,
    int CorroboratedTotal,
    FactTokenIndexState OverlapState,
    VectorLaneState VectorState,
    string VectorReason);

/// <summary>
/// The one class that produces the ranking statement SQLite executes for recall (spec §2.1).
/// </summary>
/// <remarks>
/// <para><b>Nothing else may build ranking SQL.</b> <see cref="EngramMcpTools.Recall"/> and
/// <see cref="RetrievalExplainer.Explain"/> both call <see cref="Rank"/>, which is what lets
/// <c>explain</c> keep D30's promise — it describes the ranker that actually runs, because it
/// <i>is</i> the ranker that runs, not a second implementation reading the same tables. This class
/// is <c>public</c> because <see cref="EngramMcpTools"/> lives in a different assembly than this one
/// (spec §2.0.2 ruling 1) — accessibility was never what enforced "one producer"; a lint test keyed
/// on a token unique to this statement (<c>is_corroborated</c>) does that, by scanning <c>src/</c>
/// for a second file that contains it.</para>
///
/// <para><b>SQL ranks and bounds; C# formats and packs.</b> The statement returns at most
/// <c>budgetTokens + 1</c> rows — a materialization bound, not a completeness proof: it guarantees
/// only that at most that many rank-ordered candidates are ever considered, and that packed items
/// keep their rank order within them. Under the skip contract (<see cref="RecallEngine.ApplyBudget"/>)
/// a fitting candidate can pack from arbitrarily deep in the rank order once everything above it is
/// skipped as oversized, so a candidate beyond this bound is unmaterialized rather than provably
/// unpackable. The exposure is quantified and accepted rather than closed: reaching a candidate the
/// bound hides needs more oversized-for-remaining-budget candidates than the bound itself
/// (<c>budgetTokens + 1</c> of them) ranked above the first packable fact — against a store measured
/// to hold roughly ten facts over 500 tokens. D64's formatting-time truncation is in place and bounds
/// every line to ~130 tokens, so the residual exposure is a tail-fill loss at ranks beyond the
/// bound — accepted, not deferred: raising <c>$limit</c> reopens the O(matches) cost control D58/D60
/// priced, and it stays closed unless recorded evidence (D44's method) shows a hidden candidate
/// actually mattering.
/// <see cref="RecallEngine.FormatFactLine"/>, <see cref="RecallEngine.FormatSessionFactLine"/>,
/// <see cref="RecallEngine.FormatPriorSessionFactLine"/> and <see cref="RecallEngine.ApplyBudget"/>
/// run unchanged over that bounded set — building the line in SQL would be a second implementation of
/// three C# format strings that drifts the first time one of them is edited.</para>
/// </remarks>
public static class RecallRanker
{
    /// <summary>
    /// Bound to <c>$match</c> when the query holds no valid FTS5 tokens
    /// (<see cref="FactStore.ToMatchExpression"/> returns <c>""</c> for <c>""</c>, whitespace, or
    /// pure punctuation). <c>fact_fts MATCH ''</c> is not "zero rows" — measured directly against a
    /// real store, it is <c>fts5: syntax error near ""</c> — so the empty string cannot be bound as
    /// though it behaved like an ordinary non-matching query. This phrase is an ordinary quoted FTS5
    /// phrase (always valid) built from characters no tokenized document can ever contain, so it
    /// reproduces <see cref="FactStore.SearchRanked"/>'s "no lexical hits" answer without a fifth
    /// statement variant keyed on lexical availability.
    /// </summary>
    private const string NoMatchSentinel = "\"zzz_engram_no_such_token_sentinel_zzz\"";

    private static readonly IReadOnlySet<long> EmptyPinnedFactIds = new HashSet<long>();

    // Four stable texts (spec §2.2): built once per (overlap built?, vector available?, vec table
    // name) combination and held, so every recall in a process reuses the same CommandText instead
    // of paying to assemble it again. The vec table name is keyed in because it is not stable across
    // an embedding-space change (D38 re-pins it at server startup) — seeing a table name change is
    // rare enough that an unbounded cache is fine.
    private static readonly ConcurrentDictionary<string, string> StatementCache = new(StringComparer.Ordinal);

    /// <param name="minCandidates">
    /// Materialize at least this many candidates, for a caller that renders more rows than the
    /// budget can pack. Recall leaves this at 0. <c>budgetTokens + 1</c> is a materialization bound,
    /// not a completeness proof, now that <see cref="RecallEngine.ApplyBudget"/> skips rather than
    /// stops: it still guarantees that at most that many rank-ordered candidates are ever considered,
    /// and that packed items keep their rank order within them, but a fitting candidate ranked deeper
    /// than the bound is unmaterialized rather than provably unpackable — and raising this value is
    /// exactly what would surface it, which is why recall does not (reading further would put
    /// O(matches) materialization back on the recall path — the cost D58 priced and deferred).
    /// Reaching a hidden candidate needs more oversized-for-remaining-budget candidates than the bound
    /// itself ranked above it, against a store measured to hold roughly ten facts over 500 tokens, and
    /// D64's formatting-time line truncation narrows this further. Accepted, not deferred: the bound
    /// stays as is unless recorded evidence (D44's method) shows a hidden candidate actually
    /// mattering. <c>explain</c> is the caller that needs
    /// a larger value, because its <c>--limit</c> can exceed the budget bound and D30 makes it a
    /// promise about the ranker rather than a view of the first 501 rows. Coverage is unaffected
    /// either way: it reads the window columns computed over the unbounded matched set, not the
    /// materialized one.
    /// </param>
    public static RankOutcome Rank(
        SqliteConnection connection,
        string query,
        int budgetTokens,
        int seedK,
        long? currentSessionId,
        DateTimeOffset now,
        VectorLaneQuery vectorQuery,
        int minCandidates = 0,
        IReadOnlySet<long>? pinnedFactIds = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(vectorQuery);

        var queryTerms = RecallEngine.TokenizeQuery(query);
        var allTerms = Tokenizer.Tokenize(query);
        var dropped = allTerms.Where(t => !queryTerms.Contains(t)).Order(StringComparer.Ordinal).ToList();

        var match = FactStore.ToMatchExpression(query);
        if (match.Length == 0)
        {
            match = NoMatchSentinel;
        }

        var overlapState = FactTokenIndex.ReadState(connection);
        var overlapAvailable = overlapState == FactTokenIndexState.Ready;
        var vectorAvailable = vectorQuery.State == VectorLaneState.Queried;

        var text = overlapAvailable && vectorAvailable
            ? StatementFor(true, true, VectorIndex.TableName)
            : overlapAvailable
                ? StatementFor(true, false, VectorIndex.TableName)
                : vectorAvailable
                    ? StatementFor(false, true, VectorIndex.TableName)
                    : StatementFor(false, false, VectorIndex.TableName);

        var agentNames = FactStore.ReadEntityNames(connection, SessionFacts.AgentKind);

        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddWithValue("$terms", TermsJson(queryTerms));
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$seedK", seedK);
        command.Parameters.AddWithValue("$currentSessionId", (object?)currentSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limit", Math.Max(budgetTokens + 1, minCandidates));
        if (vectorAvailable)
        {
            command.Parameters.AddWithValue("$embedding", ToBlob(vectorQuery.Embedding!));
        }

        var candidates = new List<RecallCandidate>();
        var matchedTotal = 0;
        var corroboratedTotal = 0;

        using (var reader = command.ExecuteReader())
        {
            var first = true;
            while (reader.Read())
            {
                if (first)
                {
                    matchedTotal = reader.GetInt32(reader.GetOrdinal("matched_total"));
                    corroboratedTotal = reader.GetInt32(reader.GetOrdinal("corroborated_total"));
                    first = false;
                }

                candidates.Add(ReadCandidate(reader, now, agentNames));
            }
        }

        var coverage = RecallEngine.ClassifyCoverage(matchedTotal, corroboratedTotal);

        // A ranking boost among already-matched candidates, never a second relevance signal
        // (D44/D60) — applied before ApplyBudget so a pinned fact's reordering is what the budget
        // packs against, not an afterthought layered on top of an already-cut digest.
        candidates = RecallEngine.ApplyPinBoost(candidates, pinnedFactIds ?? EmptyPinnedFactIds).ToList();

        var tokensUsed = RecallEngine.ApplyBudget(candidates, budgetTokens);

        return new RankOutcome(
            query,
            queryTerms.Order(StringComparer.Ordinal).ToList(),
            dropped,
            candidates,
            budgetTokens,
            tokensUsed,
            coverage,
            matchedTotal,
            corroboratedTotal,
            overlapState,
            vectorQuery.State,
            vectorQuery.Reason);
    }

    /// <summary>Builds the RECALL digest text, mirroring what the object ranker's Pack used to do.</summary>
    public static RecallPackResult Pack(
        SqliteConnection connection,
        string query,
        int budgetTokens,
        int seedK,
        long? currentSessionId,
        DateTimeOffset now,
        VectorLaneQuery vectorQuery,
        IReadOnlySet<long>? pinnedFactIds = null)
    {
        var outcome = Rank(
            connection, query, budgetTokens, seedK, currentSessionId, now, vectorQuery, pinnedFactIds: pinnedFactIds);

        var includedLines = new List<string>();
        var sessionFactCount = 0;
        var longTermFactCount = 0;
        var priorSessionFactCount = 0;
        foreach (var candidate in outcome.Candidates)
        {
            if (!candidate.Packed)
            {
                continue;
            }

            includedLines.Add(candidate.Line);
            switch (candidate.Origin)
            {
                case FactOrigin.CurrentSession:
                    sessionFactCount++;
                    break;
                case FactOrigin.LongTerm:
                    longTermFactCount++;
                    break;
                case FactOrigin.PriorSession:
                    priorSessionFactCount++;
                    break;
            }
        }

        var factCount = sessionFactCount + longTermFactCount + priorSessionFactCount;
        var lines = new List<string>
        {
            $"RECALL \"{query}\" · {factCount} facts · {outcome.TokensUsed}/{budgetTokens} tokens · "
                + $"coverage: {RecallEngine.ToText(outcome.Coverage)}{AvailabilityNote(outcome)}",
        };
        lines.AddRange(includedLines);

        if (outcome.Coverage != RecallCoverage.High)
        {
            lines.Add($"gaps: {RecallEngine.GapsMessage(query, outcome.Coverage)}");
        }

        lines.Add("→ engram_remember what you discover");

        return new RecallPackResult(
            string.Join('\n', lines),
            factCount,
            outcome.TokensUsed,
            outcome.Coverage,
            sessionFactCount,
            longTermFactCount,
            priorSessionFactCount);
    }

    /// <summary>
    /// The D44 consequence of an unavailable lane (spec §2.0.1): a note keyed to lane <i>state</i>,
    /// never to hit count, independent of coverage, appended at every coverage value including
    /// <c>high</c> and <c>none</c> — <c>none</c> is exactly where an unavailable overlap lane can
    /// misdescribe an overlap-only fact as "the store said nothing".
    /// </summary>
    private static string AvailabilityNote(RankOutcome outcome)
    {
        var notes = new List<string>(2);
        if (OverlapUnavailableDetail(outcome.OverlapState) is { } overlapDetail)
        {
            notes.Add("overlap lane did not run (" + overlapDetail + ")");
        }

        // Off is a supported configuration (D18), not a fault — reporting it here would be exactly
        // what D37 says trains people to stop reading a diagnostic, and it would make the note fire
        // on every recall in every store with embeddings off, which is not this note's job.
        if (outcome.VectorState == VectorLaneState.Unavailable)
        {
            notes.Add($"vector lane did not run ({outcome.VectorReason})");
        }

        return notes.Count == 0 ? string.Empty : " · " + string.Join(" · ", notes);
    }

    /// <summary>
    /// The overlap lane's unavailability reason, or null when it is ready. Shared by
    /// <see cref="AvailabilityNote"/> and <see cref="RetrievalExplainer"/>'s "term overlap" lane row,
    /// so the two surfaces describe the same state with the same words rather than two independent
    /// ones that can drift.
    /// </summary>
    public static string? OverlapUnavailableDetail(FactTokenIndexState state) => state switch
    {
        FactTokenIndexState.Ready => null,
        FactTokenIndexState.Unbuilt => "token index not built yet",
        _ => "token index built by an older tokenizer",
    };

    private static RecallCandidate ReadCandidate(
        SqliteDataReader reader, DateTimeOffset now, IReadOnlyDictionary<string, string> agentNames)
    {
        var factId = reader.GetInt64(reader.GetOrdinal("fact_id"));
        var handle = reader.GetString(reader.GetOrdinal("handle"));
        var origin = (FactOrigin)reader.GetInt32(reader.GetOrdinal("origin"));
        var overlapRank = NullableInt(reader, "overlap_rank");
        var lexicalRank = NullableInt(reader, "lexical_rank");
        var vectorRank = NullableInt(reader, "vector_rank");
        var fused = reader.GetDouble(reader.GetOrdinal("fused"));
        var body = reader.GetString(reader.GetOrdinal("body"));
        var detailsChars = reader.GetInt32(reader.GetOrdinal("details_chars"));
        var scope = reader.GetString(reader.GetOrdinal("scope"));
        var createdAt = reader.GetInt64(reader.GetOrdinal("created_at"));
        var subjectName = reader.GetString(reader.GetOrdinal("subject_name"));
        var path = reader.GetString(reader.GetOrdinal("path"));
        var versions = reader.GetInt32(reader.GetOrdinal("versions"));
        var judged = reader.GetInt32(reader.GetOrdinal("relations")) > 0;
        var labelIndex = NullableInt(reader, "label_index");

        var ageDays = AgeDaysOf(createdAt, now);

        var line = origin switch
        {
            FactOrigin.LongTerm => RecallEngine.FormatFactLine(
                new CannedFact(handle, subjectName, string.Empty, body, scope, string.Empty, ageDays, null, versions, detailsChars, judged)),
            FactOrigin.CurrentSession => RecallEngine.FormatSessionFactLine(
                ToSessionFact(factId, body, path, subjectName, ageDays, detailsChars, agentNames)),
            FactOrigin.PriorSession => RecallEngine.FormatPriorSessionFactLine(
                ToSessionFact(factId, body, path, subjectName, ageDays, detailsChars, agentNames),
                "p" + (labelIndex ?? throw new InvalidOperationException(
                    $"fact {handle} ranked as a prior-session candidate with no prior_sessions label — "
                    + "the origin CASE and the prior_sessions CTE's predicates have drifted apart."))
                        .ToString(CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException($"no formatter for fact origin {origin}"),
        };

        return new RecallCandidate(
            factId, handle, line, fused, overlapRank, lexicalRank, vectorRank, origin,
            TokenEstimator.Estimate(line), Packed: false);
    }

    // Mirrors SessionFacts.ToSessionFact exactly: the agent segment resolves through the agent
    // entity's display name, falling back to its path slug, and Subject is null when nothing named
    // this note (the entity name still equals the path's fingerprint leaf).
    private static SessionFact ToSessionFact(
        long factId, string body, string path, string subjectName, int ageDays, int detailsChars,
        IReadOnlyDictionary<string, string> agentNames)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? agent = null;
        if (segments.Length == 4)
        {
            var agentPath = "/" + segments[0] + "/" + segments[1] + "/" + segments[2];
            agent = agentNames.TryGetValue(agentPath, out var name) ? name : segments[2];
        }

        var subject = segments.Length > 0 && string.Equals(subjectName, segments[^1], StringComparison.Ordinal)
            ? null
            : subjectName;

        return new SessionFact(factId, 0, body, subject, agent, ageDays, detailsChars);
    }

    private static int AgeDaysOf(long createdAtUnixSeconds, DateTimeOffset now)
    {
        var age = now - DateTimeOffset.FromUnixTimeSeconds(createdAtUnixSeconds);
        return age.TotalDays > 0 ? (int)age.TotalDays : 0;
    }

    private static int? NullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string TermsJson(IEnumerable<string> terms)
    {
        var array = new JsonArray();
        foreach (var term in terms.Order(StringComparer.Ordinal))
        {
            ((IList<JsonNode?>)array).Add(JsonValue.Create(term));
        }

        return array.ToJsonString();
    }

    /// <summary>
    /// Internal rather than private so the §3.2 EXPLAIN QUERY PLAN guard can obtain the exact text
    /// SQLite executes, for all four lane-availability variants, without a second implementation of
    /// how the variant is chosen.
    /// </summary>
    internal static string StatementFor(bool overlapAvailable, bool vectorAvailable, string vecTable) =>
        StatementCache.GetOrAdd(
            overlapAvailable.ToString(CultureInfo.InvariantCulture) + "|"
                + vectorAvailable.ToString(CultureInfo.InvariantCulture) + "|" + vecTable,
            _ => BuildStatementText(overlapAvailable, vectorAvailable, vecTable));

    /// <summary>
    /// Assembles one of the four statement variants (spec §2.3). Built from shared fragments rather
    /// than four hand-written copies, so the overlap/vector lane terms cannot go out of sync between
    /// variants that include a lane and variants that omit it.
    /// </summary>
    private static string BuildStatementText(bool overlapAvailable, bool vectorAvailable, string vecTable)
    {
        var sessionPrefix = SessionFacts.Root + "/"; // "/sessions/" — kept in sync with the C# constant
        var directivePrefix = DirectiveFacts.Root + "/"; // "/directives/" — kept in sync with the C# constant
        var rrfK = RecallEngine.RrfK.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("WITH\nterms(term) AS (\n  SELECT value FROM json_each($terms)\n),\n\n");

        if (overlapAvailable)
        {
            sb.Append(
                """
                -- COUNT(*), not COUNT(DISTINCT ft.token): (token, fact_id) is fact_token's primary
                -- key, so a pair cannot repeat and the two counts are identical.
                overlap_hits AS (
                  SELECT ft.fact_id, COUNT(*) AS score
                  FROM fact_token ft
                  JOIN terms t ON t.term = ft.token
                  GROUP BY ft.fact_id
                ),
                overlap_ranked AS (
                  SELECT fact_id,
                         ROW_NUMBER() OVER (ORDER BY score DESC, 'f' || CAST(fact_id AS TEXT)) AS rank
                  FROM overlap_hits
                ),

                """);
        }

        sb.Append(
            """
            -- ORDER BY bm25(), not rank: measured slower here despite the documentation (constraints
            -- file, Part 2 — USE TEMP B-TREE FOR ORDER BY disappearing under `rank` is not evidence
            -- of speed).
            lex(fact_id, rank) AS (
              SELECT f.id, ROW_NUMBER() OVER (ORDER BY bm25(fact_fts))
              FROM fact f
              JOIN fact_fts ON fact_fts.rowid = f.id
              WHERE fact_fts MATCH $match AND f.valid_to IS NULL
              ORDER BY bm25(fact_fts)
              LIMIT $seedK
            ),

            """);

        if (vectorAvailable)
        {
            sb.Append(
                $"""
                -- Present only when the lane is available: a vec0 reference fails to PREPARE when
                -- sqlite-vec is not loaded, which would take recall down with the lane (D36).
                vec(fact_id, rank) AS (
                  SELECT v.fact_id, ROW_NUMBER() OVER (ORDER BY v.distance)
                  FROM {vecTable} v
                  WHERE v.embedding MATCH $embedding AND v.is_live = 1 AND k = $seedK
                ),

                """);
        }

        sb.Append(
            $"""
            -- Ranges over every prior-session fact, never over `candidates` (ruling 6) — a session
            -- contributing no candidates must not shift a later session's label.
            prior_sessions(session_id, label_index) AS (
              SELECT DISTINCT f.session_id,
                     DENSE_RANK() OVER (ORDER BY f.session_id)
              FROM fact f JOIN entity e ON e.id = f.subject_id
              WHERE f.valid_to IS NULL
                AND substr(e.path, 1, {sessionPrefix.Length}) = '{sessionPrefix}'
                AND f.session_id IS NOT $currentSessionId
            ),

            candidates AS (

            """);

        var unions = new List<string>(3);
        if (overlapAvailable)
        {
            unions.Add("  SELECT fact_id FROM overlap_ranked");
        }

        unions.Add("  SELECT fact_id FROM lex");
        if (vectorAvailable)
        {
            unions.Add("  SELECT fact_id FROM vec");
        }

        sb.Append(string.Join("\n  UNION\n", unions));
        sb.Append("\n),\n\n");

        var overlapRankColumn = overlapAvailable ? "o.rank" : "NULL";
        var vectorRankColumn = vectorAvailable ? "v.rank" : "NULL";

        var fusedTerms = new List<string>(3);
        var corroboratedTerms = new List<string>(3);
        if (overlapAvailable)
        {
            fusedTerms.Add($"COALESCE(1.0 / ({rrfK} + o.rank), 0.0)");
            corroboratedTerms.Add("(o.rank IS NOT NULL)");
        }

        fusedTerms.Add($"COALESCE(1.0 / ({rrfK} + l.rank), 0.0)");
        corroboratedTerms.Add("(l.rank IS NOT NULL)");
        if (vectorAvailable)
        {
            fusedTerms.Add($"COALESCE(1.0 / ({rrfK} + v.rank), 0.0)");
            corroboratedTerms.Add("(v.rank IS NOT NULL)");
        }

        // Single-lane variants degenerate to `(rank IS NOT NULL) > 1`, which is always false — the
        // D44 consequence of an unavailable lane (spec §2.0.1) is implemented by this arithmetic, not
        // by a separate branch: a lane that did not run cannot contribute a term here at all.
        sb.Append(
            $"""
            scored AS (
              SELECT
                f.id                                   AS fact_id,
                'f' || CAST(f.id AS TEXT)              AS handle,
                CASE
                  WHEN substr(e.path, 1, {sessionPrefix.Length}) <> '{sessionPrefix}' THEN 1  -- long term
                  WHEN f.session_id IS $currentSessionId THEN 0                                -- current session
                  ELSE 2                                                                       -- prior session
                END                                    AS origin,
                {overlapRankColumn}                    AS overlap_rank,
                l.rank                                 AS lexical_rank,
                {vectorRankColumn}                     AS vector_rank,
                {string.Join("\n              + ", fusedTerms)} AS fused,
                ({string.Join(" + ", corroboratedTerms)}) > 1  AS is_corroborated,
                f.body, f.scope, f.predicate, f.session_id, f.created_at,
                COALESCE(length(f.details), 0) AS details_chars,
                e.name AS subject_name, e.path, ps.label_index AS label_index
              FROM candidates c
              JOIN fact f          ON f.id = c.fact_id
              JOIN entity e        ON e.id = f.subject_id

            """);

        if (overlapAvailable)
        {
            sb.Append("  LEFT JOIN overlap_ranked o ON o.fact_id = f.id\n");
        }

        sb.Append("  LEFT JOIN lex l            ON l.fact_id = f.id\n");
        if (vectorAvailable)
        {
            sb.Append("  LEFT JOIN vec v            ON v.fact_id = f.id\n");
        }

        sb.Append("  LEFT JOIN prior_sessions ps ON ps.session_id = f.session_id\n");
        // A directive is delivered unconditionally by the primer (D-1) and answers a
        // class-addressed question through engram_browse, not a content-addressed one through
        // recall (D-5) — excluded here, where the candidate set drawn from every lane is
        // materialized, rather than at any one lane's own query, so it can never surface
        // regardless of which lane matched it, and never inflates matched/corroborated counts.
        sb.Append($"  WHERE f.valid_to IS NULL\n    AND substr(e.path, 1, {directivePrefix.Length}) <> '{directivePrefix}'\n),\n\n");

        sb.Append(
            """
            bounded AS (
              SELECT *,
                     COUNT(*)             OVER () AS matched_total,
                     SUM(is_corroborated) OVER () AS corroborated_total
              FROM scored
              ORDER BY CASE WHEN origin = 0 THEN 0 ELSE 1 END, fused DESC, handle
              LIMIT $limit
            )

            -- The outer ORDER BY is repeated: LIMIT inside a CTE bounds the rows, but SQLite does not
            -- guarantee the outer query preserves that order. Free at <= 501 rows.
            -- The versions and relations subqueries are outside the LIMIT, so each runs at most 501
            -- times instead of once per candidate. versions groups on path+predicate (D57), not
            -- subject_id, matching FactStore.VersionCounts. relations counts fact_relation rows
            -- naming this fact on either side, matching FactRelations.RelationCounts — the source
            -- of the "· judged" marker; ranking itself is unchanged.
            SELECT b.*,
                   (SELECT COUNT(*) FROM fact f2
                      JOIN entity e2 ON e2.id = f2.subject_id
                     WHERE e2.path = b.path AND f2.predicate = b.predicate) AS versions,
                   (SELECT COUNT(*) FROM fact_relation fr
                     WHERE fr.fact_id = b.fact_id OR fr.related_id = b.fact_id) AS relations
            FROM bounded b
            ORDER BY CASE WHEN b.origin = 0 THEN 0 ELSE 1 END, b.fused DESC, b.handle;
            """);

        return sb.ToString();
    }
}
