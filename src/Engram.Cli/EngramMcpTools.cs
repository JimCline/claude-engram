using System.ComponentModel;
using Engram.Core;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace Engram.Cli;

public sealed record McpSessionId(string Value);

public sealed record McpHomeState(bool Initialized);

[McpServerToolType]
public sealed class EngramMcpTools
{
    [McpServerTool(Name = "engram_recall")]
    [Description(
        "Check memory BEFORE reading files or exploring the repo. Searches Engram's stored facts " +
        "(decisions, conventions, gotchas, contracts) and returns a token-budgeted, ranked digest with " +
        "fact handles and a coverage estimate. Call this first for any question about how this project " +
        "works, what was decided, or why something is the way it is — it is far cheaper than rediscovering " +
        "the answer by reading source.")]
    public static string Recall(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        LocalRuntime local,
        SessionPinStore pins,
        [Description("What you want to know, as a few keywords or a short question.")] string query,
        [Description("Maximum tokens to spend on the response. Defaults to 500.")] int? budget_tokens = null)
    {
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = RetrievalSettings.Read(config);
        var budget = budget_tokens is > 0 ? budget_tokens.Value : settings.BudgetTokens;

        // One connection, one temporal model, one statement: SQLite ranks and bounds every tier —
        // long-term, current session, prior session — from a single atomic read (D59). Nothing
        // O(corpus) crosses into C# in either direction.
        var now = DateTimeOffset.UtcNow;
        using var connection = EngramDatabase.OpenInitialized(home);

        var currentSessionId = SessionStore.FindSession(connection, session.Value);

        // The same lane `explain` reports, so what it describes is what ran here. It costs nothing
        // when embeddings are off — the factory refuses before any request — and it can never fail
        // this call: every way it can stop comes back as a reason and no embedding, leaving recall
        // exactly as lexical as it was before. The search itself now happens inside the ranking
        // statement (RecallRanker), so only the embedding — not a result set — crosses back into C#.
        var vectorQuery = VectorLane.PrepareQuery(
            connection, home, EmbeddingSettings.Read(config), query, Environment.GetEnvironmentVariable, local);

        var result = RecallRanker.Pack(
            connection, query, budget, settings.SeedK, currentSessionId, now, vectorQuery, pins.PinnedFor(session));

        if (homeState.Initialized)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: session.Value,
                Kind: TelemetryEventKind.Recall,
                Query: query,
                FactCount: result.FactCount,
                TokensReturned: result.TokensUsed,
                Coverage: RecallEngine.ToText(result.Coverage),
                SessionFactCount: result.SessionFactCount,
                LongTermFactCount: result.LongTermFactCount,
                PriorSessionFactCount: result.PriorSessionFactCount));
        }

        return result.Text;
    }

    [McpServerTool(Name = "engram_remember")]
    // Opening on durability and on the trigger is the whole point of this wording. It previously
    // opened "Save a durable note to this session's working memory", which reads as scratch space,
    // and it never named a trigger at all — so against a memory system whose instructions fire on
    // the literal words "remember this", it lost every race it was in, correctly. The competing
    // system is not necessarily Claude Code's file-based memory; it is whatever the agent was told
    // about somewhere Engram cannot see, which is why the claim is stated here rather than assumed.
    // How Engram ranks against such a system is a per-install preference and lives in the primer
    // instead (MemorySettings) — a [Description] is a compile-time constant and cannot vary (D51).
    [Description(
        "Engram's durable memory, and where anything worth keeping goes even when another memory " +
        "system is available. Call it when the user asks you to remember or save something, and " +
        "whenever you learn something durable: a decision, a constraint, a partial result, a dead end " +
        "already ruled out — state you would otherwise repeat in context and would lose to compaction. " +
        "`engram_recall` ranks these above long-term memory for the rest of this session, and they stay " +
        "recallable in later ones, so this is for anything still true next week rather than only the " +
        "next ten minutes. Subagents pass their own name in `agent`. When restating something the user " +
        "just said, pass the bracketed id of its automatic capture in `supersedes` so the rewrite " +
        "replaces it rather than duplicating it. When candidates are enabled, also returns up to 3 " +
        "similar live facts already stored, for a follow-up engram_judge call.")]
    public static string Remember(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        LocalRuntime local,
        [Description("The fact to remember, as a short, self-contained statement — aim under ~300 characters; depth belongs in details.")] string statement,
        [Description("Depth the statement cannot carry — a full config, a long rationale, verbatim output. Most memories need none: if the statement holds it, stop there.")] string? details = null,
        [Description("What or who the fact is about, if not obvious from the statement.")] string? subject = null,
        [Description("Where this was learned — a file path, PR, or command output.")] string? evidence = null,
        [Description("Your own name, if you are a subagent recording this fact rather than the main agent.")] string? agent = null,
        [Description(
            "The bracketed id of a raw user-statement capture this restates, e.g. \"f42\". Closes that "
                + "capture so the rewritten version replaces it instead of duplicating it.")] string? supersedes = null,
        [Description("Flags for sync. Triggered by \"share engram\" plus content.")] bool sync = false,
        [Description("When to revisit this: a relative duration ('3d', '2w', '12h') or an ISO date. Surfaces in 'engram review list' and doctor once it passes.")] string? review_after = null)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); this statement was not saved.";
        }

        if (DetailsCeilingError(details) is { } ceilingError)
        {
            return ceilingError;
        }

        if (ReviewAfterError(review_after, out var reviewAfterUnix) is { } reviewAfterError)
        {
            return reviewAfterError;
        }

        // A restatement of something the user said replaces the capture in place — same
        // subject, same predicate — so the store's own collision rule closes the original
        // and records why. Writing it to this session's notes instead would leave the
        // rewritten version as the one that expires and the raw one as the one that lasts,
        // which is backwards.
        if (!string.IsNullOrWhiteSpace(supersedes))
        {
            if (!FactCatalog.TryParseHandle(supersedes, out var targetId))
            {
                return $"'{supersedes}' is not a fact handle; they look like 'f42'. Nothing was saved.";
            }

            var restateNow = DateTimeOffset.UtcNow;
            using var connection = EngramDatabase.OpenInitialized(home);
            using var restateTransaction = EngramDatabase.BeginWrite(connection);
            var replacementId = UserFacts.Restate(
                connection, restateTransaction, targetId, statement, session.Value, restateNow, details: details, sync: sync);

            if (replacementId is null)
            {
                restateTransaction.Rollback();
                return $"No live fact with id '{supersedes}' to restate — it may already have been "
                    + "superseded or forgotten. Nothing was saved; call engram_recall to see what stands.";
            }

            if (reviewAfterUnix is { } restateReviewAfter)
            {
                FactReview.Set(connection, restateTransaction, replacementId.Value, restateReviewAfter, restateNow.ToUnixTimeSeconds());
            }

            restateTransaction.Commit();

            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: session.Value,
                Kind: TelemetryEventKind.Remember));

            return $"[{FactCatalog.HandleFor(replacementId.Value)}] replaced capture [{supersedes}]: \"{statement}\"";
        }

        long factId;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            var rememberNow = DateTimeOffset.UtcNow;
            using var rememberTransaction = EngramDatabase.BeginWrite(connection);
            bool isRepeat;
            (factId, isRepeat) = SessionFacts.Append(
                connection, rememberTransaction, session.Value, statement, subject, evidence, agent, rememberNow, details, sync);

            if (isRepeat)
            {
                // Nothing new was written, so there is no fresh row for a review marker to be
                // atomic with — set it as its own statement, same as before this fix.
                rememberTransaction.Rollback();

                if (reviewAfterUnix is { } repeatReviewAfter)
                {
                    FactReview.Set(connection, null, factId, repeatReviewAfter, rememberNow.ToUnixTimeSeconds());
                }
            }
            else
            {
                if (reviewAfterUnix is { } factReviewAfter)
                {
                    FactReview.Set(connection, rememberTransaction, factId, factReviewAfter, rememberNow.ToUnixTimeSeconds());
                }

                rememberTransaction.Commit();
            }
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Remember));

        var subjectText = string.IsNullOrWhiteSpace(subject) ? "(unspecified subject)" : subject;
        var evidenceText = string.IsNullOrWhiteSpace(evidence) ? string.Empty : $" (evidence: {evidence})";
        var agentText = string.IsNullOrWhiteSpace(agent) ? string.Empty : $" [via {agent}]";

        var response = $"[{FactCatalog.HandleFor(factId)}] remembered: {subjectText} — \"{statement}\"{evidenceText}{agentText}";

        var config = ConfigFile.Load(home.ConfigPath);
        if (RememberSettings.Read(config).Candidates)
        {
            var candidateLines = NearNeighbourCandidates(home, config, local, session, statement, factId);
            if (candidateLines.Count > 0)
            {
                response += "\n\nPossibly related:\n" + string.Join('\n', candidateLines.Select(line => "  " + line));
            }
        }

        return response;
    }

    // Near-neighbour candidates for a fresh engram_remember write (docs/memory-expansion/
    // 02-conflict-verdicts-spec.md, Design). Runs post-write, through the same lanes and the
    // same D44 corroboration bar (2+ lanes agreeing) recall itself uses — no new matcher, no
    // new threshold. Store-wide: engram_remember's `subject` is free-text display metadata,
    // not a structured entity path, so there is no entity grouping to scope a search to.
    private static IReadOnlyList<string> NearNeighbourCandidates(
        EngramHome home, ConfigFile config, LocalRuntime local, McpSessionId session, string statement, long factId)
    {
        var settings = RetrievalSettings.Read(config);
        var now = DateTimeOffset.UtcNow;

        using var connection = EngramDatabase.OpenInitialized(home);
        var currentSessionId = SessionStore.FindSession(connection, session.Value);
        var vectorQuery = VectorLane.PrepareQuery(
            connection, home, EmbeddingSettings.Read(config), statement, Environment.GetEnvironmentVariable, local);

        var outcome = RecallRanker.Rank(connection, statement, settings.BudgetTokens, settings.SeedK, currentSessionId, now, vectorQuery);

        return outcome.Candidates
            .Where(candidate => candidate.FactId != factId)
            .Where(candidate => RecallEngine.LanesThatFound(candidate) > 1)
            .Take(3)
            .Select(candidate => candidate.Line)
            .ToList();
    }

    [McpServerTool(Name = "engram_forget")]
    [Description(
        "Retract a stored fact by its bracketed id (e.g. \"f42\"). Use it whenever the user says something "
            + "stored is wrong, private, or no longer true, and to clear a session note that turned out to be "
            + "mistaken. Retracted facts stop appearing in recall immediately, and stay retracted — nothing "
            + "re-seeds them. Call engram_recall first if you need to find the id.")]
    public static string Forget(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The bracketed id of the fact to retract, e.g. \"f42\".")] string id)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); nothing was retracted.";
        }

        if (!FactCatalog.TryParseHandle(id, out var factId))
        {
            return $"'{id}' is not a fact handle; they look like 'f42'. Nothing was retracted.";
        }

        // Any live fact — seeded, captured from the user, or a note this session took.
        // Refusing to close one because of where it came from would be the store telling a
        // user which of their own memories they are allowed to drop, and once every tier is
        // an ordinary fact there is nothing left to base the refusal on. The seeder already
        // declines to write back anything this store has held before, so a retraction
        // survives a corpus revision.
        using var connection = EngramDatabase.OpenInitialized(home);
        var closed = FactStore.Forget(connection, factId, "retracted by the user", DateTimeOffset.UtcNow);

        // Reporting success for something that was never live would leave the user believing
        // a fact is gone while recall keeps returning it.
        if (!closed)
        {
            return $"No live fact with id '{id}'. It may already have been superseded or retracted; "
                + "call engram_recall to see what stands.";
        }

        return $"Retracted [{id}]. It will not appear in recall again. The retraction is recorded "
            + "rather than the original erased, because facts here are only ever closed, never deleted.";
    }

    [McpServerTool(Name = "engram_browse")]
    [Description(
        "List what memory holds under a path — children, fact counts, and the top facts at that node. " +
        "A table of contents, not a search: engram_recall finds facts by content, this shows how an " +
        "area is organised. Paths look like /people/jim or /projects/acme.")]
    public static string Browse(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The memory path to list, e.g. /projects/acme.")] string path,
        [Description("Levels of children to show, 1-3. Defaults to 1.")] int? depth = null)
    {
        using var connection = EngramDatabase.OpenInitialized(home);
        var node = MemoryBrowser.Browse(connection, path, depth ?? 1);

        if (homeState.Initialized)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: session.Value,
                Kind: TelemetryEventKind.Browse,
                Query: path));
        }

        if (node is null)
        {
            return $"Nothing in memory under {path}. Browse lists structure that exists; "
                + "engram_recall searches by content and does not need a path.";
        }

        var builder = new System.Text.StringBuilder();
        builder.Append(node.Path)
            .Append(" — ")
            .Append(CountText(node.FactsHere, "fact"))
            .Append(" here, ")
            .Append(node.FactsUnder)
            .Append(" under it\n");

        foreach (var fact in MemoryBrowser.TopFacts(connection, node.Path, 3))
        {
            builder.Append("  [")
                .Append(FactCatalog.HandleFor(fact.Id))
                .Append("] ")
                .Append(fact.Predicate)
                .Append(": ")
                .Append(fact.Body)
                .Append('\n');
        }

        AppendChildren(builder, node, indent: "  ");
        return builder.ToString().TrimEnd('\n');
    }

    [McpServerTool(Name = "engram_expand")]
    [Description(
        "The full story behind one fact handle: its supersession history, related facts on the same " +
        "subject, its evidence, or where it was learned. Call it when a fact engram_recall returned " +
        "needs scrutiny before you rely on it. The details view returns everything the handle holds, " +
        "paged by budget_tokens and offset.")]
    public static string Expand(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The bracketed fact id, e.g. \"f42\".")] string id,
        [Description("One of: history, related, evidence, source, details.")] string view,
        [Description("Maximum tokens returned per call. Defaults to 800.")] int budget_tokens = 800,
        [Description("Character offset to continue a paged details view from. Defaults to 0.")] int offset = 0)
    {
        if (!FactCatalog.TryParseHandle(id, out var factId))
        {
            return $"'{id}' is not a fact handle; they look like 'f42'.";
        }

        using var connection = EngramDatabase.OpenInitialized(home);
        var fact = FactStore.ReadById(connection, factId);
        if (fact is null)
        {
            return $"No fact with id '{id}' — call engram_recall to find handles.";
        }

        if (homeState.Initialized)
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: session.Value,
                Kind: TelemetryEventKind.Expand,
                Query: view));
        }

        return view.Trim().ToLowerInvariant() switch
        {
            "history" => ExpandHistory(connection, fact),
            "related" => ExpandRelated(connection, fact),
            "evidence" => ExpandEvidence(fact),
            "source" => ExpandSource(connection, fact),
            "details" => ExpandDetails(fact, budget_tokens, offset),
            _ => $"Unknown view '{view}'. The views are history, related, evidence, source, and details.",
        };
    }

    [McpServerTool(Name = "engram_revise")]
    [Description(
        "Replace a stored belief with a corrected statement, recording why. The old fact is closed, " +
        "never erased, and the new one takes its place in recall. Use it when the user or fresh " +
        "evidence contradicts something stored; engram_forget retracts without a replacement.")]
    public static string Revise(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The bracketed id of the fact to revise, e.g. \"f42\".")] string fact_id,
        [Description("The corrected statement, short and self-contained.")] string statement,
        [Description("Why the belief changed.")] string reason,
        [Description("Where the correction came from — a file, PR, or the user's words.")] string? evidence = null,
        [Description("Depth for the corrected fact. Does not carry forward from the old version — restate it or drop it deliberately.")] string? details = null,
        [Description("Sync flag; omit inherits, else overrides.")] bool? sync = null,
        [Description("When to revisit this: a relative duration ('3d', '2w', '12h') or an ISO date. Does not carry forward from the old version.")] string? review_after = null)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); nothing was revised.";
        }

        if (!FactCatalog.TryParseHandle(fact_id, out var factId))
        {
            return $"'{fact_id}' is not a fact handle; they look like 'f42'. Nothing was revised.";
        }

        if (string.IsNullOrWhiteSpace(statement) || string.IsNullOrWhiteSpace(reason))
        {
            return "Revision needs both a corrected statement and a reason. Nothing was revised.";
        }

        if (DetailsCeilingError(details) is { } ceilingError)
        {
            return ceilingError;
        }

        if (ReviewAfterError(review_after, out var reviewAfterUnix) is { } reviewAfterError)
        {
            return reviewAfterError;
        }

        using var connection = EngramDatabase.OpenInitialized(home);
        var target = FactStore.ReadById(connection, factId);
        if (target is null)
        {
            return $"No fact with id '{fact_id}'. Nothing was revised.";
        }

        if (target.ValidTo is not null)
        {
            return $"[{fact_id}] is already closed; revising it would rewrite history rather than "
                + "correct a belief. Call engram_recall to find the live fact that replaced it.";
        }

        var now = DateTimeOffset.UtcNow;
        var sessionId = SessionStore.EnsureSession(connection, null, session.Value, now);

        // Read before Remember closes the incumbent: FactSyncRequests keys on fact_id, and the
        // old row stays exactly what it was (the flag never moves to a closed fact).
        var carriesSyncFlag = sync ?? FactSyncRequests.IsFlagged(connection, null, factId);

        RememberResult result;
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            // The store's own collision rule does the revision: one live fact per
            // (subject, predicate), so remembering the correction closes the incumbent and
            // records the reason on the supersession row.
            result = FactStore.Remember(
                connection,
                transaction,
                new FactWrite(
                    target.SubjectPath,
                    "concept",
                    target.Predicate,
                    statement.Trim(),
                    target.Scope,
                    "stated",
                    evidence,
                    Regenerable: false,
                    SessionId: sessionId,
                    Details: details),
                now,
                reason.Trim());

            // Without this, a fact someone deliberately flagged to always sync would silently
            // stop syncing the moment they revised it (docs/memory-expansion/01-sync-spec.md,
            // "Per-fact opt-in") — landed in the same transaction as the write it flags, so a
            // crash between the two can never leave one without the other.
            if (carriesSyncFlag)
            {
                FactSyncRequests.Insert(connection, transaction, result.FactId, now.ToUnixTimeSeconds());
            }

            if (reviewAfterUnix is { } revisedReviewAfter)
            {
                FactReview.Set(connection, transaction, result.FactId, revisedReviewAfter, now.ToUnixTimeSeconds());
            }

            transaction.Commit();
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Revise));

        var moved = result.SupersededFactId is { } replaced && replaced != factId
            ? $" (The live belief had moved; [{FactCatalog.HandleFor(replaced)}] is what was replaced.)"
            : string.Empty;

        return $"[{FactCatalog.HandleFor(result.FactId)}] revised [{fact_id}]: \"{statement.Trim()}\". "
            + "The reason is recorded on the supersession, and the old fact stays closed rather than erased."
            + moved;
    }

    [McpServerTool(Name = "engram_index_repo")]
    // The primer can name the *condition* for offering enrollment but not the mechanism (D51) —
    // that split is why this description states the verbs outright, as tightly as it can while
    // keeping "declining is a valid answer" explicit: a model that reads this as enroll-only
    // never records a "no", and the prompt returns every session.
    [Description(
        "Record the user's answer on indexing this checkout: enroll, decline (stop asking), or " +
        "later (ask in a week). Call as soon as they answer — decline is as valid an answer as enroll.")]
    public static string IndexRepo(
        EngramHome home,
        McpSessionId session,
        [Description("Git checkout path.")] string path,
        [Description("enroll, decline, or later.")] string decision)
    {
        var root = RepoCommand.ResolveCheckoutRoot(path);
        if (root is null)
        {
            return $"'{path}' is not inside a git checkout; engram_index_repo only tracks enrollment for git checkouts.";
        }

        var normalized = decision.Trim().ToLowerInvariant();
        if (normalized is not ("enroll" or "decline" or "later"))
        {
            return $"'{decision}' is not a recognized decision; expected enroll, decline, or later. Nothing was recorded.";
        }

        RepoCommand.RepoDecisionResult result;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            result = RepoCommand.ApplyDecision(home, connection, root, normalized, session.Value, DateTimeOffset.UtcNow);
        }

        return normalized switch
        {
            "enroll" => $"Enrolled {root} ({result.Identity}). " + (result.IndexSpawned
                ? "The first index is running in the background."
                : $"warning: could not start the first index automatically ({result.SpawnError}); "
                    + $"run 'engram index --apply --full {root}' by hand."),
            "decline" => $"Declined {root} ({result.Identity}). It will not be offered again unless the "
                + "decision is reset with 'engram repo reset'.",
            _ => $"Deferred {root} ({result.Identity}). It will be offered again in "
                + $"{(int)RepoEnrollment.DeferralCooldown.TotalDays} days.",
        };
    }

    [McpServerTool(Name = "engram_judge")]
    [Description(
        "Record a verdict on how two facts relate: supersedes, conflicts_with, scoped, or not_conflict. " +
        "Call it after engram_recall or engram_expand surfaces two facts that might disagree, to settle " +
        "which one stands and why. The verdict is recorded alongside both facts — neither is changed or " +
        "closed by it — and shows up under engram_expand ... history for either one.")]
    public static string Judge(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The bracketed id of the fact being judged, e.g. \"f42\".")] string fact_id,
        [Description("The bracketed id of the fact it is being compared against, e.g. \"f17\".")] string related_id,
        [Description("One of: supersedes, conflicts_with, scoped, not_conflict.")] string relation,
        [Description("Why this verdict — what distinguishes or reconciles the two facts.")] string reason)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); nothing was recorded.";
        }

        if (!FactCatalog.TryParseHandle(fact_id, out var factId))
        {
            return $"'{fact_id}' is not a fact handle; they look like 'f42'. Nothing was recorded.";
        }

        if (!FactCatalog.TryParseHandle(related_id, out var relatedId))
        {
            return $"'{related_id}' is not a fact handle; they look like 'f42'. Nothing was recorded.";
        }

        if (factId == relatedId)
        {
            return $"'{fact_id}' and '{related_id}' are the same fact. Nothing was recorded.";
        }

        var normalizedRelation = relation.Trim().ToLowerInvariant();
        if (!FactRelations.Kinds.Contains(normalizedRelation))
        {
            return $"'{relation}' is not a recognized relation; expected one of "
                + $"{string.Join(", ", FactRelations.Kinds)}. Nothing was recorded.";
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return "A verdict needs a reason. Nothing was recorded.";
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        if (FactStore.ReadById(connection, factId) is null)
        {
            return $"No fact with id '{fact_id}'. Nothing was recorded.";
        }

        if (FactStore.ReadById(connection, relatedId) is null)
        {
            return $"No fact with id '{related_id}'. Nothing was recorded.";
        }

        StoredRelation stored;
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            stored = FactRelations.Judge(
                connection, transaction, factId, relatedId, normalizedRelation, reason.Trim(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            transaction.Commit();
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Judge));

        return $"[{fact_id}] {normalizedRelation} [{related_id}]: {reason.Trim()}. "
            + "Recorded as a standalone verdict — neither fact was changed or closed.";
    }

    [McpServerTool(Name = "engram_pin")]
    [Description(
        "Pin a fact for this session: engram_recall guarantees it top position whenever the query " +
        "matches it, without pulling it into results it would not otherwise match. Use it to keep a " +
        "fact from being crowded out of the digest for the rest of a task where it keeps mattering. " +
        "The pin does not persist past this session — engram_unpin releases it early.")]
    public static string Pin(
        SessionPinStore pins,
        McpSessionId session,
        [Description("The bracketed id of the fact to pin, e.g. \"f42\".")] string fact_id)
    {
        if (!FactCatalog.TryParseHandle(fact_id, out var factId))
        {
            return $"'{fact_id}' is not a fact handle; they look like 'f42'. Nothing was pinned.";
        }

        var added = pins.Pin(session, factId);
        return added
            ? $"[{fact_id}] pinned for this session."
            : $"[{fact_id}] was already pinned for this session.";
    }

    [McpServerTool(Name = "engram_unpin")]
    [Description("Release a fact pinned earlier this session with engram_pin. Pins that are never released end with the session anyway.")]
    public static string Unpin(
        SessionPinStore pins,
        McpSessionId session,
        [Description("The bracketed id of the fact to unpin, e.g. \"f42\".")] string fact_id)
    {
        if (!FactCatalog.TryParseHandle(fact_id, out var factId))
        {
            return $"'{fact_id}' is not a fact handle; they look like 'f42'. Nothing was unpinned.";
        }

        var removed = pins.Unpin(session, factId);
        return removed
            ? $"[{fact_id}] unpinned."
            : $"[{fact_id}] was not pinned for this session. Nothing changed.";
    }

    // Match tier only — extraction tier (§7.1) is a Phase 4 stamp with no schema yet; an absent
    // field would read as tier 0, the failure §3.4 already forbids for callers/callees/neighbors
    // returning empty instead of "not yet indexed", so every row-bearing response says so instead.
    private const string ExtractionTierUnrecordedHeader =
        "(extraction tier not recorded until Phase 4 — rows below carry match tier only)\n";

    private sealed record NavigateOutcome(string Text, bool Found, IReadOnlyList<string> Tiers)
    {
        public static NavigateOutcome NotFound(string text) => new(text, Found: false, Tiers: []);
    }

    [McpServerTool(Name = "engram_navigate")]
    [Description(
        "Where is a symbol defined, or what does a file import — a deterministic lookup over indexed " +
        "code, not a search. Use it instead of Read/Grep to answer 'where is Z defined' or 'what does " +
        "Y import'. relation is defined_at or imports this phase; callers, callees, and neighbors are " +
        "recognized but answer 'not yet indexed' rather than an empty result, since an empty list would " +
        "read as 'nothing calls this' when the graph simply is not built yet.")]
    public static string Navigate(
        EngramHome home,
        McpSessionId session,
        [Description("A symbol name for defined_at, or a file path for imports.")] string query,
        [Description("One of: defined_at, imports, callers, callees, neighbors.")] string relation,
        [Description("Restrict matches to one repo's indexed code, by its slug.")] string? repo = null,
        [Description("Maximum matches returned. Defaults to 20, clamped to 1-100.")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var normalizedRelation = relation.Trim().ToLowerInvariant();

        NavigateOutcome outcome;
        if (normalizedRelation is "callers" or "callees" or "neighbors")
        {
            outcome = NavigateOutcome.NotFound(
                $"'{normalizedRelation}' is not yet indexed — code edges (calls, references) are "
                    + "Phase 3 work that has not landed. This is not a negative result; it means the "
                    + "question cannot be answered yet, not that the answer is empty.");
        }
        else
        {
            using var connection = EngramDatabase.OpenInitialized(home);
            outcome = normalizedRelation switch
            {
                "defined_at" => NavigateDefinedAt(connection, query, repo, limit),
                "imports" => NavigateImports(connection, query, repo, limit),
                _ => NavigateOutcome.NotFound(
                    $"Unknown relation '{relation}'. Use defined_at or imports (callers, callees, and "
                        + "neighbors are recognized but not yet indexed)."),
            };
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Navigate,
            Relation: normalizedRelation,
            Found: outcome.Found,
            Tiers: string.Join(",", outcome.Tiers)));

        return outcome.Text;
    }

    private static NavigateOutcome NavigateDefinedAt(SqliteConnection connection, string query, string? repo, int limit)
    {
        var repoNeedle = RepoNeedle(repo);
        if (repoNeedle is not null && !RepoIndexed(connection, repoNeedle))
        {
            return NavigateOutcome.NotFound($"Repo '{repo}' is not indexed. Nothing to search.");
        }

        var matches = SymbolResolver.Resolve(connection, query, limit, repoNeedle);

        if (matches.Count == 0)
        {
            return NavigateOutcome.NotFound(
                $"No symbol named '{query}' found (checked exact, case-insensitive, and substring match).");
        }

        var builder = new System.Text.StringBuilder();
        builder.Append(ExtractionTierUnrecordedHeader);
        builder.Append(CountText(matches.Count, "match")).Append(" for '").Append(query).Append("':\n");

        foreach (var match in matches)
        {
            var declared = FactStore.History(connection, match.Path, "declared-as")
                .FirstOrDefault(f => f.ValidTo is null);

            builder.Append("  [").Append(match.Tier).Append("] ").Append(match.Path).Append(": ")
                .Append(declared?.Body ?? "(no declaration recorded)")
                .Append('\n');
        }

        var tiers = matches.Select(m => m.Tier.ToString()).Distinct().ToList();
        return new NavigateOutcome(builder.ToString().TrimEnd('\n'), Found: true, Tiers: tiers);
    }

    private static NavigateOutcome NavigateImports(SqliteConnection connection, string query, string? repo, int limit)
    {
        var repoNeedle = RepoNeedle(repo);
        if (repoNeedle is not null && !RepoIndexed(connection, repoNeedle))
        {
            return NavigateOutcome.NotFound($"Repo '{repo}' is not indexed. Nothing to search.");
        }

        var exact = QueryFileEntities(connection, "e.path = $q", query, limit, repoNeedle)
            .Select(path => (Path: path, Tier: "exact"))
            .ToList();

        var matches = exact.Count > 0
            ? exact
            : QueryFileEntities(
                    connection,
                    "e.path LIKE '%/' || $q ESCAPE '\\'",
                    SymbolResolver.LikeEscape(query),
                    limit,
                    repoNeedle)
                .Select(path => (Path: path, Tier: "path-suffix"))
                .ToList();

        if (matches.Count == 0)
        {
            return NavigateOutcome.NotFound(
                $"No file matching '{query}' found (checked exact path, then path ending in '/{query}').");
        }

        var builder = new System.Text.StringBuilder();
        builder.Append(ExtractionTierUnrecordedHeader);
        builder.Append(CountText(matches.Count, "file")).Append(" matching '").Append(query).Append("':\n");

        foreach (var match in matches)
        {
            var imports = FactStore.History(connection, match.Path, "imports")
                .Where(f => f.ValidTo is null)
                .ToList();
            var modules = ImportedModuleNames(connection, imports.Select(f => f.Id))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            var body = modules.Count > 0
                ? "imports " + string.Join(", ", modules)
                : "no imports recorded";

            builder.Append("  [").Append(match.Tier).Append("] ").Append(match.Path).Append(": ")
                .Append(body)
                .Append('\n');
        }

        var tiers = matches.Select(m => m.Tier).Distinct().ToList();
        return new NavigateOutcome(builder.ToString().TrimEnd('\n'), Found: true, Tiers: tiers);
    }

    private static IEnumerable<string> ImportedModuleNames(SqliteConnection connection, IEnumerable<long> factIds)
    {
        foreach (var id in factIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT o.path FROM fact f JOIN entity o ON o.id = f.object_id WHERE f.id = $id;";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is string path)
            {
                yield return CodePaths.SymbolNameOf(path) ?? path;
            }
        }
    }

    private static List<string> QueryFileEntities(
        SqliteConnection connection, string pathPredicate, string query, int limit, string? repoContains)
    {
        using var command = connection.CreateCommand();
        var repoClause = repoContains is null ? string.Empty : " AND e.path LIKE '%' || $repo || '%'";
        command.CommandText =
            $"SELECT e.path FROM entity e WHERE e.kind = 'file' AND {pathPredicate}{repoClause} ORDER BY e.path LIMIT $limit;";
        command.Parameters.AddWithValue("$q", query);
        command.Parameters.AddWithValue("$limit", limit);
        if (repoContains is not null)
        {
            command.Parameters.AddWithValue("$repo", repoContains);
        }

        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    // Bracketing slashes make this segment-exact — "/code/engram/" cannot match
    // "/code/engram-docs/" — and CodePaths.Slug is the exact normalization the indexer applies
    // to a repo name when it composes CodePaths.RepoRoot, so a query using the real repo name
    // filters to the same paths the indexer wrote (code-nav fixup B2/constraints 1-2). Slug's
    // output is restricted to [a-z0-9-], so the needle never itself needs LIKE-escaping.
    private static string? RepoNeedle(string? repo) => repo is null ? null : $"/code/{CodePaths.Slug(repo)}/";

    // Distinguishes "this repo isn't indexed" from "no match in this repo" (D60's rule already
    // applied to callers/callees/neighbors) — without it, an unknown repo silently falls into
    // the generic not-found message and reads as "no symbol", not as "wrong repo".
    private static bool RepoIndexed(SqliteConnection connection, string repoNeedle)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM entity WHERE path LIKE '%' || $repo || '%' LIMIT 1;";
        command.Parameters.AddWithValue("$repo", repoNeedle);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    private static void AppendChildren(System.Text.StringBuilder builder, BrowseNode node, string indent)
    {
        foreach (var child in node.Children)
        {
            builder.Append(indent)
                .Append(child.Name)
                .Append(" — ")
                .Append(CountText(child.FactsHere + child.FactsUnder, "fact"))
                .Append('\n');

            AppendChildren(builder, child, indent + "  ");
        }

        if (node.ChildrenOmitted > 0)
        {
            builder.Append(indent).Append("…and ").Append(node.ChildrenOmitted).Append(" more\n");
        }
    }

    private static string ExpandHistory(SqliteConnection connection, StoredFact fact)
    {
        var chain = FactStore.History(connection, fact.SubjectPath, fact.Predicate);
        var reasons = MemoryBrowser.Reasons(
            connection,
            chain.Where(f => f.ValidTo is not null).Select(f => f.Id));

        var builder = new System.Text.StringBuilder();
        builder.Append("History of ").Append(fact.SubjectPath).Append(' ').Append(fact.Predicate)
            .Append(" — ").Append(CountText(chain.Count, "version")).Append(":\n");

        foreach (var entry in chain)
        {
            builder.Append("  [").Append(FactCatalog.HandleFor(entry.Id)).Append("] \"")
                .Append(entry.Body).Append("\" (").Append(When(entry.ValidFrom))
                .Append(", ").Append(entry.LearnedVia).Append(')');

            if (entry.ValidTo is { } closed)
            {
                builder.Append(" — closed ").Append(When(closed));
                if (reasons.TryGetValue(entry.Id, out var why))
                {
                    builder.Append(": ").Append(why);
                }
            }

            builder.Append('\n');
        }

        var relations = FactRelations.ForFact(connection, fact.Id);
        if (relations.Count > 0)
        {
            builder.Append("Judged against ").Append(CountText(relations.Count, "other fact")).Append(":\n");
            foreach (var relation in relations)
            {
                var otherId = relation.FactId == fact.Id ? relation.RelatedId : relation.FactId;

                // "supersedes" is the one directional relation kept — the other three
                // (conflicts_with, scoped, not_conflict) read the same from either side. Rendered
                // literally from the wrong side it would say the older fact superseded the newer
                // one; flip the wording when the fact whose history this is happens to be the
                // superseding (fact_id) side of the row.
                var verb = relation.Relation == "supersedes" && relation.FactId == fact.Id
                    ? "is superseded by this fact"
                    : relation.Relation;

                builder.Append("  [").Append(FactCatalog.HandleFor(otherId)).Append("] ")
                    .Append(verb).Append(" (").Append(When(relation.JudgedAt)).Append(')');

                if (!string.IsNullOrWhiteSpace(relation.Reason))
                {
                    builder.Append(": ").Append(relation.Reason);
                }

                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string ExpandRelated(SqliteConnection connection, StoredFact fact)
    {
        var related = FactStore.ReadSubtree(connection, fact.SubjectPath)
            .Where(f => f.Id != fact.Id && f.ValidTo is null)
            .Take(8)
            .ToList();

        if (related.Count == 0)
        {
            return $"Nothing else is recorded about {fact.SubjectPath}. engram_browse on a parent "
                + "path shows the wider neighbourhood.";
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("Also recorded about ").Append(fact.SubjectPath).Append(":\n");
        foreach (var entry in related)
        {
            builder.Append("  [").Append(FactCatalog.HandleFor(entry.Id)).Append("] ")
                .Append(entry.SubjectPath == fact.SubjectPath
                    ? entry.Predicate
                    : entry.SubjectPath[fact.SubjectPath.Length..].TrimStart('/', '#') + " " + entry.Predicate)
                .Append(": ").Append(entry.Body).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string ExpandEvidence(StoredFact fact)
    {
        var evidence = string.IsNullOrWhiteSpace(fact.Evidence)
            ? "No evidence was recorded with this fact."
            : $"Evidence: {fact.Evidence}";

        var regenerable = fact.Regenerable
            ? " It is regenerable — the indexer can recompute it from source, and 'engram index' refreshes it."
            : " It is not regenerable: it exists only because it was recorded, and nothing can recompute it.";

        return $"[{FactCatalog.HandleFor(fact.Id)}] {evidence} Learned via '{fact.LearnedVia}', "
            + $"recorded {When(fact.CreatedAt)}.{regenerable}";
    }

    private static string ExpandSource(SqliteConnection connection, StoredFact fact)
    {
        var sitting = MemoryBrowser.Sitting(connection, fact.Id);
        var origin = sitting is { } s
            ? $"recorded in session {s.ExternalId} (started {When(s.StartedAt)})"
            : "recorded outside any tracked session — seeded, indexed, or written by the CLI";

        return $"[{FactCatalog.HandleFor(fact.Id)}] was {origin}, learned via '{fact.LearnedVia}' "
            + $"on {When(fact.CreatedAt)}."
            + (fact.ValidTo is { } closed ? $" It was closed {When(closed)}." : " It is currently believed.");
    }

    // Shared by engram_remember and engram_revise so the ceiling and its wording cannot
    // drift into checking two different limits.
    private static string? DetailsCeilingError(string? details)
    {
        if (details is null)
        {
            return null;
        }

        var tokens = TokenEstimator.Estimate(details);
        return tokens > 2000
            ? $"details is ~{tokens} tokens against the 2,000-token ceiling — a memory that large is a "
                + "document; store where to find it (evidence, a path) rather than its contents. Nothing "
                + "was stored."
            : null;
    }

    // Shared by engram_remember and engram_revise, mirroring DetailsCeilingError's shape: validated
    // before any write so a bad review_after never leaves a partial save behind.
    private static string? ReviewAfterError(string? reviewAfter, out long? reviewAfterUnix)
    {
        reviewAfterUnix = null;

        if (reviewAfter is null)
        {
            return null;
        }

        if (!DurationParsing.TryParse(reviewAfter, DateTimeOffset.UtcNow, out var unixSeconds))
        {
            return $"'{reviewAfter}' is not a recognized duration or date — try e.g. '3d', '2w', '12h', "
                + "or an ISO date. Nothing was saved.";
        }

        reviewAfterUnix = unixSeconds;
        return null;
    }

    private static string ExpandDetails(StoredFact fact, int budgetTokens, int offset)
    {
        var text = fact.Details is null ? fact.Body : fact.Body + "\n\n" + fact.Details;

        if (offset < 0 || offset >= text.Length)
        {
            return $"offset {offset} is past the end ({text.Length} chars).";
        }

        // A budget under 1 char produces an empty, non-advancing page — the same failure
        // class the word-boundary progress guard below exists for.
        if (budgetTokens < 1)
        {
            return "budget_tokens must be at least 1.";
        }

        // Clamped to the remaining length in the double domain before casting: budgetTokens
        // as large as int.MaxValue would otherwise overflow int arithmetic and go negative.
        var maxChars = (int)Math.Min(budgetTokens * TokenEstimator.CharactersPerToken, text.Length - offset);
        var end = offset + maxChars;

        if (end < text.Length)
        {
            // Cut at a word boundary rather than mid-word — a hard cut is the fallback only
            // when the window holds no space to back up to.
            var lastSpace = text.LastIndexOf(' ', end - 1, end - offset);
            if (lastSpace > offset)
            {
                end = lastSpace;
            }
        }

        if (end < text.Length && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        var page = text[offset..end];

        return end < text.Length
            ? $"{page}\n\nshowing chars {offset}–{end} of {text.Length} · continue with offset: {end}"
            : page;
    }

    private static string When(long unixSeconds) => MomentText.Local(unixSeconds);

    private static string CountText(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
