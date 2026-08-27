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
        "Check Engram's memory BEFORE reading files or exploring the repo. Searches Engram's stored facts " +
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
        "List what Engram's memory holds under a path — children, fact counts, and the top facts at that node. " +
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

    // §5.2: NULL and 0 must render distinctly and never collapse/sort together.
    private static string ExtractionTierLabel(int? tier) => tier switch
    {
        0 => "regex",
        1 => "syntactic",
        2 => "semantic",
        _ => "not recorded",
    };

    // §5.3: "a qualifier that always fires is noise" — state it once in the header when every
    // row shares one extraction tier; say nothing in the header when rows differ, and mark each
    // row instead (the caller does that marking when this returns null).
    private static string? UniformExtractionTierNote(IReadOnlyList<int?> tiers) =>
        tiers.Count > 0 && tiers.Distinct().Count() == 1
            ? $"(extraction tier: {ExtractionTierLabel(tiers[0])})\n"
            : null;

    private sealed record NavigateOutcome(
        string Text, bool Found, IReadOnlyList<string> Tiers, IReadOnlyList<string> ExtractionTiers)
    {
        /// <summary>
        /// A miss, carrying the reason the index could not answer. <paramref name="coverageCaveat"/>
        /// appends what the index does not cover — pass false only where the caveat is untrue or
        /// already stated.
        /// </summary>
        /// <remarks>
        /// The caveat is the whole point of routing every miss through here. A bare "No symbol
        /// named 'X' found" states a fact about the index and reads as a fact about the repository,
        /// and the reader cannot tell the two apart — gitignored files are deliberately not indexed
        /// (D53's scan bound), and the queue that picks up edits drains on session start rather than
        /// on write, so a file can exist, and be findable by grep, while this returns nothing. That
        /// gap became load-bearing when the lookup-nudge hook started steering symbol lookups here
        /// first: a miss the model reads as "does not exist" is then a wrong conclusion the nudge
        /// itself caused. `neighbors` already carries its own version of this sentence, and an
        /// unknown relation is a usage error rather than a coverage question — both pass false.
        /// </remarks>
        public static NavigateOutcome NotFound(string text, bool coverageCaveat = true) =>
            new(
                coverageCaveat ? text + " " + CoverageCaveat : text,
                Found: false,
                Tiers: [],
                ExtractionTiers: []);

        /// <summary>
        /// Says what a <c>[stale]</c> or <c>[missing]</c> marker means and what to do about it. A
        /// marker nobody can interpret is decoration — the reader has to know that the index is
        /// behind the file rather than wrong about it, or the sensible response (re-read the file)
        /// does not follow from seeing it.
        /// </summary>
        internal static string StaleFootnote(int count) =>
            $"note: {count} result(s) marked [stale] or [missing] — the file changed on disk after "
            + "it was indexed, so the declaration above may describe older content. Read the file "
            + "directly for those, or run 'engram index --apply' to refresh.";

        private const string CoverageCaveat =
            "This is what Engram has indexed, not what exists: gitignored files are never indexed, "
            + "and recent edits land only after the index queue drains. Fall back to Grep/Glob "
            + "before concluding the symbol is absent.";
    }

    [McpServerTool(Name = "engram_navigate")]
    [Description(
        "Where is a symbol defined, what does a file import, who calls/is called by a symbol, what " +
        "does a type inherit/implement or who implements it, or what members a type declares — a " +
        "deterministic lookup over indexed code, not a search. Use it instead of Read/Grep to answer " +
        "'where is Z defined', 'what does Y import', 'who calls Z', 'what does Z call', 'what does Z " +
        "implement', 'who implements Z', or 'what members does Z have'. relation is defined_at, " +
        "imports, callers, callees, implements, implementers, or members. implements/implementers/" +
        "members drop base-list and containment edges for types nested inside another type.")]
    public static string Navigate(
        EngramHome home,
        McpSessionId session,
        [Description("A symbol name for defined_at/callers/callees/implements/implementers/members, or a file path for imports.")] string query,
        [Description("One of: defined_at, imports, callers, callees, implements, implementers, members.")] string relation,
        [Description("Restrict matches to one repo's indexed code, by its slug.")] string? repo = null,
        [Description("Maximum matches returned. Defaults to 20, clamped to 1-100.")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var normalizedRelation = relation.Trim().ToLowerInvariant();

        NavigateOutcome outcome;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            outcome = normalizedRelation switch
            {
                "defined_at" => NavigateDefinedAt(connection, query, repo, limit),
                "imports" => NavigateImports(connection, query, repo, limit),
                "callers" => NavigateCallers(connection, query, repo, limit),
                "callees" => NavigateCallees(connection, query, repo, limit),
                "implements" => NavigateImplements(connection, query, repo, limit),
                "implementers" => NavigateImplementers(connection, query, repo, limit),
                "members" => NavigateMembers(connection, query, repo, limit),
                _ => NavigateOutcome.NotFound(
                    $"Unknown relation '{relation}'. Use defined_at, imports, callers, callees, "
                        + "implements, implementers, or members.",
                    coverageCaveat: false),
            };
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Navigate,
            Relation: normalizedRelation,
            Found: outcome.Found,
            Tiers: string.Join(",", outcome.Tiers),
            ExtractionTiers: string.Join(",", outcome.ExtractionTiers)));

        return outcome.Text;
    }

    /// <summary>
    /// Appends a <c>[stale]</c>/<c>[missing]</c> marker for one result's file, reporting whether it
    /// did so the caller can count. Returns false for a null path — a callee with no resolved
    /// declaration has no file to be stale about.
    /// </summary>
    /// <remarks>
    /// Shared by every relation rather than written per loop, and that is the point rather than
    /// tidiness: a marker that appears on some result shapes and not others teaches the reader that
    /// an unmarked line is fresh, which is false wherever the check simply never ran. Partial
    /// coverage of a freshness signal is worse than none, because it is the absence that carries
    /// the false claim.
    /// </remarks>
    private static bool AppendFreshness(
        System.Text.StringBuilder builder, SqliteConnection connection, string? path)
    {
        if (path is null)
        {
            return false;
        }

        var freshness = FileFreshness.Check(connection, path);
        if (!freshness.IsWorthReporting)
        {
            return false;
        }

        builder.Append('[').Append(freshness.Label).Append(']');
        return true;
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

        var rows = matches
            .Select(match => (
                match,
                declared: FactStore.History(connection, match.Path, "declared-as")
                    .FirstOrDefault(f => f.ValidTo is null)))
            .ToList();

        var extractionTiers = rows.Select(r => r.declared?.AnalyzerTier).ToList();
        var uniformNote = UniformExtractionTierNote(extractionTiers);

        var builder = new System.Text.StringBuilder();
        builder.Append(uniformNote);
        builder.Append(CountText(matches.Count, "match")).Append(" for '").Append(query).Append("':\n");

        var staleCount = 0;
        foreach (var (match, declared) in rows)
        {
            builder.Append("  [").Append(match.Tier).Append(']');
            if (uniformNote is null)
            {
                builder.Append('[').Append(ExtractionTierLabel(declared?.AnalyzerTier)).Append(']');
            }

            if (AppendFreshness(builder, connection, match.Path))
            {
                staleCount++;
            }

            builder.Append(' ').Append(match.Path).Append(": ")
                .Append(declared?.Body ?? "(no declaration recorded)")
                .Append('\n');
        }

        if (staleCount > 0)
        {
            builder.Append(NavigateOutcome.StaleFootnote(staleCount)).Append('\n');
        }

        var tiers = matches.Select(m => m.Tier.ToString()).Distinct().ToList();
        var extractionTierLabels = extractionTiers.Select(ExtractionTierLabel).Distinct().ToList();
        return new NavigateOutcome(
            builder.ToString().TrimEnd('\n'), Found: true, Tiers: tiers, ExtractionTiers: extractionTierLabels);
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

        var rows = matches
            .Select(match =>
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

                return (match, body, tier: imports.FirstOrDefault()?.AnalyzerTier);
            })
            .ToList();

        var extractionTiers = rows.Select(r => r.tier).ToList();
        var uniformNote = UniformExtractionTierNote(extractionTiers);

        var builder = new System.Text.StringBuilder();
        builder.Append(uniformNote);
        builder.Append(CountText(matches.Count, "file")).Append(" matching '").Append(query).Append("':\n");

        var staleCount = 0;
        foreach (var (match, body, tier) in rows)
        {
            builder.Append("  [").Append(match.Tier).Append(']');
            if (uniformNote is null)
            {
                builder.Append('[').Append(ExtractionTierLabel(tier)).Append(']');
            }

            if (AppendFreshness(builder, connection, match.Path))
            {
                staleCount++;
            }

            builder.Append(' ').Append(match.Path).Append(": ").Append(body).Append('\n');
        }

        if (staleCount > 0)
        {
            builder.Append(NavigateOutcome.StaleFootnote(staleCount)).Append('\n');
        }

        var tiers = matches.Select(m => m.Tier).Distinct().ToList();
        var extractionTierLabels = extractionTiers.Select(ExtractionTierLabel).Distinct().ToList();
        return new NavigateOutcome(
            builder.ToString().TrimEnd('\n'), Found: true, Tiers: tiers, ExtractionTiers: extractionTierLabels);
    }

    private static NavigateOutcome NavigateCallers(SqliteConnection connection, string query, string? repo, int limit)
    {
        var repoNeedle = RepoNeedle(repo);
        if (repoNeedle is not null && !RepoIndexed(connection, repoNeedle))
        {
            return NavigateOutcome.NotFound($"Repo '{repo}' is not indexed. Nothing to search.");
        }

        var result = CodeCallGraph.Callers(connection, query, repoNeedle, limit);
        if (!result.Found)
        {
            return NavigateOutcome.NotFound(
                $"No symbol named '{query}' found (checked exact, case-insensitive, and substring match).");
        }

        if (result.Callers.Count == 0)
        {
            return NavigateOutcome.NotFound(
                $"No recorded call sites naming '{query}' ({CoverageCaveat(result.Coverage)}).");
        }

        var extractionTiers = result.Callers.Select(c => c.AnalyzerTier).ToList();
        var uniformNote = UniformExtractionTierNote(extractionTiers);

        var builder = new System.Text.StringBuilder();
        builder.Append(uniformNote);
        if (result.QueryTier != SymbolMatchTier.Exact)
        {
            builder.Append("No exact match for '").Append(query).Append("'; showing callers of ")
                .Append(result.DeclarationCount).Append(' ').Append(TierLabel(result.QueryTier))
                .Append(" match").Append(result.DeclarationCount == 1 ? "" : "es").Append(".\n");
        }
        else if (result.DeclarationCount > 1)
        {
            builder.Append(result.DeclarationCount).Append(" declarations of '").Append(query)
                .Append("' exist in this store — these are calls to some '").Append(query)
                .Append("', not necessarily one specific declaration.\n");
        }

        builder.Append(CountText(result.Callers.Count, "caller")).Append(" of '").Append(query).Append("'");
        if (result.TotalMatches > result.Callers.Count)
        {
            builder.Append(" (showing ").Append(result.Callers.Count).Append(" of ").Append(result.TotalMatches).Append(')');
        }

        builder.Append(":\n");

        // §1b: an over-approximation trusted rather than sanity-checked (first-reach, §8.2)
        // is a worse failure than a wrong answer, because it is invisible. Say so when the
        // query leaf matched more than one distinct callee spelling.
        if (result.DistinctSpellings.Count > 1)
        {
            builder.Append("note: '").Append(query).Append("' matched ").Append(result.DistinctSpellings.Count)
                .Append(" distinct callee spellings (").Append(string.Join(", ", result.DistinctSpellings.Take(5)))
                .Append(result.DistinctSpellings.Count > 5 ? ", …" : string.Empty)
                .Append("); these callers are matched by leaf name and may include unrelated symbols.\n");
        }
        var staleCount = 0;
        foreach (var caller in result.Callers)
        {
            builder.Append("  [").Append(RankLabel(caller.Signal)).Append(']');
            if (uniformNote is null)
            {
                builder.Append('[').Append(ExtractionTierLabel(caller.AnalyzerTier)).Append(']');
            }

            if (AppendFreshness(builder, connection, caller.CallerPath))
            {
                staleCount++;
            }

            builder.Append(' ').Append(caller.CallerPath);
            if (caller.AttributedToType)
            {
                builder.Append(" (attributed to the enclosing type — call site is a kind not indexed as a symbol)");
            }

            builder.Append('\n');
        }

        if (staleCount > 0)
        {
            builder.Append(NavigateOutcome.StaleFootnote(staleCount)).Append('\n');
        }

        var extractionTierLabels = extractionTiers.Select(ExtractionTierLabel).Distinct().ToList();
        return new NavigateOutcome(
            builder.ToString().TrimEnd('\n'), Found: true, Tiers: [], ExtractionTiers: extractionTierLabels);
    }

    private static NavigateOutcome NavigateCallees(SqliteConnection connection, string query, string? repo, int limit)
    {
        var repoNeedle = RepoNeedle(repo);
        if (repoNeedle is not null && !RepoIndexed(connection, repoNeedle))
        {
            return NavigateOutcome.NotFound($"Repo '{repo}' is not indexed. Nothing to search.");
        }

        var result = CodeCallGraph.Callees(connection, query, repoNeedle, limit);
        if (!result.Found)
        {
            return NavigateOutcome.NotFound(
                $"No symbol named '{query}' found (checked exact, case-insensitive, and substring match).");
        }

        if (result.Callees.Count == 0)
        {
            return NavigateOutcome.NotFound(
                $"No recorded calls from '{query}' ({CoverageCaveat(result.Coverage)}).");
        }

        var extractionTiers = result.Callees.Select(c => c.AnalyzerTier).ToList();
        var uniformNote = UniformExtractionTierNote(extractionTiers);

        var builder = new System.Text.StringBuilder();
        builder.Append(uniformNote);
        if (result.QueryTier != SymbolMatchTier.Exact)
        {
            builder.Append("No exact match for '").Append(query).Append("'; resolved by ")
                .Append(TierLabel(result.QueryTier)).Append(" match.\n");
        }

        builder.Append(CountText(result.Callees.Count, "call")).Append(" from '").Append(query).Append("'");
        if (result.TotalMatches > result.Callees.Count)
        {
            builder.Append(" (showing ").Append(result.Callees.Count).Append(" of ").Append(result.TotalMatches).Append(')');
        }

        builder.Append(":\n");
        var staleCount = 0;
        foreach (var callee in result.Callees)
        {
            builder.Append("  [").Append(RankLabel(callee.Signal)).Append(']');
            if (uniformNote is null)
            {
                builder.Append('[').Append(ExtractionTierLabel(callee.AnalyzerTier)).Append(']');
            }

            // Keyed on the resolved declaration, not the calling file: what a callee line asserts
            // is where the call lands, so that is the file whose age the reader is relying on.
            if (AppendFreshness(builder, connection, callee.DeclarationPath))
            {
                staleCount++;
            }

            builder.Append(' ').Append(callee.Callee);
            if (callee.DeclarationPath is not null)
            {
                builder.Append(" -> ").Append(callee.DeclarationPath);
            }
            else
            {
                builder.Append(" (no matching declaration found)");
            }

            builder.Append('\n');
        }

        if (staleCount > 0)
        {
            builder.Append(NavigateOutcome.StaleFootnote(staleCount)).Append('\n');
        }

        var extractionTierLabels = extractionTiers.Select(ExtractionTierLabel).Distinct().ToList();
        return new NavigateOutcome(
            builder.ToString().TrimEnd('\n'), Found: true, Tiers: [], ExtractionTiers: extractionTierLabels);
    }

    // The one union implementation (§8.5.3 item 2): every navigate relation that asks about
    // base lists reads all three predicates so a caller asking "implements" gets a match
    // regardless of which one the language's grammar could justify (§8.5.1).
    private static readonly string[] InheritancePredicates = ["inherits", "implements", "derives-from"];

    private static NavigateOutcome NavigateImplements(SqliteConnection connection, string query, string? repo, int limit)
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

        var rows = new List<(SymbolMatch Match, StoredFact Fact, string BaseName)>();
        foreach (var match in matches)
        {
            foreach (var predicate in InheritancePredicates)
            {
                foreach (var fact in FactStore.History(connection, match.Path, predicate).Where(f => f.ValidTo is null))
                {
                    rows.Add((match, fact, ObjectNameOf(connection, fact.Id) ?? "(unknown)"));
                }
            }
        }

        if (rows.Count == 0)
        {
            return NavigateOutcome.NotFound($"No recorded base-list edges for '{query}'.");
        }

        var displayed = rows.Take(limit).ToList();
        var builder = new System.Text.StringBuilder();
        builder.Append(CountText(displayed.Count, "base-list edge")).Append(" for '").Append(query).Append('\'');
        if (rows.Count > displayed.Count)
        {
            builder.Append(" (showing ").Append(displayed.Count).Append(" of ").Append(rows.Count).Append(')');
        }

        builder.Append(":\n");

        var staleCount = 0;
        foreach (var (match, fact, baseName) in displayed)
        {
            builder.Append("  [").Append(fact.Predicate).Append(']');
            if (AppendFreshness(builder, connection, match.Path))
            {
                staleCount++;
            }

            builder.Append(' ').Append(match.Path).Append(" -> ").Append(baseName).Append('\n');
        }

        AppendOverApproximationNote(builder, displayed.Select(r => r.Fact.Predicate));

        if (staleCount > 0)
        {
            builder.Append(NavigateOutcome.StaleFootnote(staleCount)).Append('\n');
        }

        return new NavigateOutcome(builder.ToString().TrimEnd('\n'), Found: true, Tiers: [], ExtractionTiers: []);
    }

    private static NavigateOutcome NavigateImplementers(SqliteConnection connection, string query, string? repo, int limit)
    {
        var repoNeedle = RepoNeedle(repo);
        if (repoNeedle is not null && !RepoIndexed(connection, repoNeedle))
        {
            return NavigateOutcome.NotFound($"Repo '{repo}' is not indexed. Nothing to search.");
        }

        // §11.2 (Architect ruling): leaf-match both sides via CodeCallGraph.MatchingSymbolNames,
        // the same mechanism `callers` uses for the structurally identical question (find stored
        // symbol-name objects naming X, return their subjects) — exact string equality against
        // `o.path` was an unargued divergence from that relation's leaf matching. Deliberately
        // NOT SymbolResolver's NOCASE/substring tiers: substring would return `IFooBar` for a
        // query of `IFoo`, the same false positive class Grep is being replaced for.
        var objects = CodeCallGraph.MatchingSymbolNames(connection, [CodePaths.LeafOf(query)]);
        var distinctSpellings = objects.Select(o => o.Name).Distinct(StringComparer.Ordinal).ToList();

        var rows = new List<(string SubjectPath, string Predicate, int? AnalyzerTier)>();
        var truncatedPredicates = new List<string>();
        if (objects.Count > 0)
        {
            var objectPaths = objects.Select(o => o.Path).ToList();

            // Ultra-Advisor ruling (graph-index-audit follow-up): one statement, not a
            // 3-query-per-predicate loop — §8.5.3.2's one-implementation rule applies to this
            // union the same way it applies to the predicate list itself. Not a flat LIMIT,
            // though: a single unpartitioned cap would let a 1000+-row `inherits` hub starve
            // `derives-from` out of the fetched set entirely, silently breaking
            // AppendOverApproximationNote below, which depends on every predicate actually
            // being sampled. ROW_NUMBER() OVER (PARTITION BY predicate) preserves the
            // per-predicate cap inside the single query. Fetching rn <= 1001 rather than 1000
            // is how a predicate's own cap-hit is detected without a second query — the 1001st
            // row (if present) is dropped from `rows` but its predicate is marked truncated.
            using var command = connection.CreateCommand();
            var objectPlaceholders = string.Join(',', objectPaths.Select((_, i) => $"$o{i}"));
            var predicateParams = InheritancePredicates.Select((p, i) => (Name: $"$p{i}", Value: p)).ToList();
            var predicatePlaceholders = string.Join(',', predicateParams.Select(p => p.Name));
            var repoClause = repoNeedle is null ? string.Empty : " AND f.path LIKE '%' || $repo || '%'";
            command.CommandText =
                "WITH ranked AS ("
                    + "SELECT f.path AS subject_path, f.predicate AS predicate, f.analyzer_tier AS analyzer_tier, "
                    + "ROW_NUMBER() OVER (PARTITION BY f.predicate ORDER BY f.predicate, f.path) AS rn "
                    + "FROM fact f JOIN entity o ON o.id = f.object_id "
                    + $"WHERE f.predicate IN ({predicatePlaceholders}) AND f.valid_to IS NULL "
                    + $"AND o.path IN ({objectPlaceholders}){repoClause}"
                    + ") SELECT subject_path, predicate, analyzer_tier, rn FROM ranked WHERE rn <= 1001 "
                    + "ORDER BY predicate, subject_path;";
            foreach (var p in predicateParams)
            {
                command.Parameters.AddWithValue(p.Name, p.Value);
            }

            for (var i = 0; i < objectPaths.Count; i++)
            {
                command.Parameters.AddWithValue($"$o{i}", objectPaths[i]);
            }

            if (repoNeedle is not null)
            {
                command.Parameters.AddWithValue("$repo", repoNeedle);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var predicate = reader.GetString(1);
                var rn = reader.GetInt32(3);
                if (rn > 1000)
                {
                    truncatedPredicates.Add(predicate);
                    continue;
                }

                rows.Add((reader.GetString(0), predicate, reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }
        }

        if (rows.Count == 0)
        {
            var miss =
                $"No recorded type names '{query}' as a base or interface (this is a name-as-written "
                    + "match, matched by leaf name).";
            if (HasTypeArgumentMarker(query) || HasParameterizedSpelling(connection, query))
            {
                miss += " " + GenericsGapNote;
            }

            return NavigateOutcome.NotFound(miss);
        }

        var displayed = rows.Take(limit).ToList();
        var builder = new System.Text.StringBuilder();
        builder.Append(CountText(displayed.Count, "implementer")).Append(" of '").Append(query).Append('\'');
        if (rows.Count > displayed.Count)
        {
            builder.Append(" (showing ").Append(displayed.Count).Append(" of ").Append(rows.Count).Append(')');
        }

        builder.Append(":\n");

        // §8.5.3 item 4 / §10.2 (Architect ruling): a returned list makes an implicit
        // completeness claim, so this relation's exact-match-only limitation must be
        // declared whenever the query could plausibly have been affected by it — not only
        // on a total miss, which was the original, incomplete asymmetry. The gap class
        // itself (generics missed by exact match) is a static property of this relation;
        // whether to print it is decided per call, from either side's spelling: the query
        // itself, or a stored candidate spelled with type arguments the query lacks.
        if (HasTypeArgumentMarker(query) || HasParameterizedSpelling(connection, query))
        {
            builder.Append("note: ").Append(GenericsGapNote).Append('\n');
        }

        // §11.2 / §1b: leaf matching makes `IFoo` hit `NS1.IFoo` and `NS2.IFoo` alike — an
        // over-approximation on the object side, marked the same way `callers`' hub note
        // marks its own leaf-matched ambiguity (already shipped in 6aa2f33, reused not
        // reinvented).
        if (distinctSpellings.Count > 1)
        {
            builder.Append("note: '").Append(query).Append("' matched ").Append(distinctSpellings.Count)
                .Append(" distinct type spellings (").Append(string.Join(", ", distinctSpellings.Take(5)))
                .Append(distinctSpellings.Count > 5 ? ", …" : string.Empty)
                .Append("); these results are matched by leaf name and may include unrelated types.\n");
        }

        // graph-index-audit §2.4: `LIMIT 1000` (now per predicate inside the windowed query
        // above) is a real cap, and a capped list makes the same implicit completeness claim
        // §8.5.3 item 4 forbids leaving silent — this is that rule's discrimination test
        // applied to the cap itself: fires only on the predicates that actually hit it, not
        // unconditionally.
        if (truncatedPredicates.Count > 0)
        {
            builder.Append("note: more than 1000 live '")
                .Append(string.Join("'/'", truncatedPredicates))
                .Append("' edge(s) exist for '").Append(query)
                .Append("'; only the first 1000 per predicate are included above.\n");
        }

        var staleCount = 0;
        foreach (var (subjectPath, predicate, _) in displayed)
        {
            builder.Append("  [").Append(predicate).Append(']');
            if (AppendFreshness(builder, connection, subjectPath))
            {
                staleCount++;
            }

            builder.Append(' ').Append(subjectPath).Append('\n');
        }

        AppendOverApproximationNote(builder, displayed.Select(r => r.Predicate));

        if (staleCount > 0)
        {
            builder.Append(NavigateOutcome.StaleFootnote(staleCount)).Append('\n');
        }

        return new NavigateOutcome(builder.ToString().TrimEnd('\n'), Found: true, Tiers: [], ExtractionTiers: []);
    }

    private static NavigateOutcome NavigateMembers(SqliteConnection connection, string query, string? repo, int limit)
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

        var rows = new List<(SymbolMatch Match, StoredFact Fact, string MemberName)>();
        foreach (var match in matches)
        {
            foreach (var fact in FactStore.History(connection, match.Path, "contains").Where(f => f.ValidTo is null))
            {
                rows.Add((match, fact, ObjectNameOf(connection, fact.Id) ?? "(unknown)"));
            }
        }

        if (rows.Count == 0)
        {
            return NavigateOutcome.NotFound($"No recorded members of '{query}'.");
        }

        var displayed = rows.Take(limit).ToList();
        var builder = new System.Text.StringBuilder();
        builder.Append(CountText(displayed.Count, "member")).Append(" of '").Append(query).Append('\'');
        if (rows.Count > displayed.Count)
        {
            builder.Append(" (showing ").Append(displayed.Count).Append(" of ").Append(rows.Count).Append(')');
        }

        builder.Append(":\n");

        var staleCount = 0;
        foreach (var (match, _, memberName) in displayed)
        {
            builder.Append("  ");
            if (AppendFreshness(builder, connection, match.Path))
            {
                staleCount++;
            }

            builder.Append(' ').Append(match.Path).Append(" -> ").Append(memberName).Append('\n');
        }

        if (staleCount > 0)
        {
            builder.Append(NavigateOutcome.StaleFootnote(staleCount)).Append('\n');
        }

        return new NavigateOutcome(builder.ToString().TrimEnd('\n'), Found: true, Tiers: [], ExtractionTiers: []);
    }

    /// <summary>
    /// §8.5.3 item 3: a <c>derives-from</c> hit answering an implements/inherits question is
    /// an over-approximation — the parser could not tell base class from interface — and must
    /// say so, or the union quietly turns "could not tell" into "yes".
    /// </summary>
    private static void AppendOverApproximationNote(System.Text.StringBuilder builder, IEnumerable<string> predicates)
    {
        var list = predicates as IReadOnlyCollection<string> ?? predicates.ToList();
        var derivesFrom = list.Count(p => p == "derives-from");
        if (derivesFrom == 0)
        {
            return;
        }

        builder.Append("note: ").Append(derivesFrom).Append(" of ").Append(list.Count)
            .Append(" results are from languages whose syntax does not distinguish base classes "
                + "from interfaces (C#, Python); those are base-list entries, not confirmed interface "
                + "implementations.\n");
    }

    // §8.5.3 item 4 / §10.2: a statically-known gap of the `implementers` relation itself
    // (query side, not per-file data) — exact-name matching cannot find a base-list entry
    // spelled with different type arguments than the query. Fix-or-declare: not fixed, so
    // declared, and surfaced only when the query's own spelling shows it could plausibly
    // have been affected, per the same discipline `[stale]` already uses.
    private const string GenericsGapNote =
        "'implementers' matches base-list entries by exact spelling only; a differently "
            + "parameterized spelling of this type (e.g. a different generic argument) would be missed.";

    private static bool HasTypeArgumentMarker(string text) => text.Contains('<', StringComparison.Ordinal);

    // §8.5.3 item 4: the dominant case is the query spelled bare (`IComparer`) against a
    // stored base-list entry spelled with type arguments (`IComparer<T>`) — the query itself
    // then carries no marker, so HasTypeArgumentMarker(query) alone never fires exactly when
    // the caveat is most needed. Probe the object side directly: does any stored symbol-name
    // start with this spelling followed by '<'? % and _ are LIKE metacharacters and symbol
    // names routinely contain '_' (and the escaping of '%' in CodePaths.ForSymbolName itself
    // reintroduces a literal '%'), so the probed spelling must be escaped, not just embedded.
    // ESCAPE keeps this query from using entity's unique-path index for a range scan — an
    // accepted cost, since correctness on '_'-bearing names matters more here than one extra
    // full scan per implementers call, and case-insensitive LIKE cannot use that index anyway
    // (see the ix_entity_kind comment in docs/engram-schema.sql).
    private static bool HasParameterizedSpelling(SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM entity WHERE kind = 'symbol-name' AND path LIKE $prefix ESCAPE '\\' LIMIT 1;";
        command.Parameters.AddWithValue("$prefix", EscapeLikePattern(CodePaths.ForSymbolName(query)) + "<%");
        return command.ExecuteScalar() is not null;
    }

    private static string EscapeLikePattern(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? ObjectNameOf(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT o.path FROM fact f JOIN entity o ON o.id = f.object_id WHERE f.id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() is string path ? CodePaths.SymbolNameOf(path) ?? path : null;
    }

    private static string TierLabel(SymbolMatchTier tier) => tier switch
    {
        SymbolMatchTier.CaseInsensitive => "case-insensitive",
        SymbolMatchTier.Substring => "substring",
        _ => "exact",
    };

    private static string CoverageCaveat(ExtractionCoverage coverage) => coverage switch
    {
        ExtractionCoverage.NotApplicable => "not applicable to this language",
        ExtractionCoverage.KnownZero => "extraction is current for this file; this is a real zero",
        _ => "extraction coverage for this file is unknown — this may mean no calls, or that it has not been extracted",
    };

    private static string RankLabel(CallRankSignal signal) => signal switch
    {
        CallRankSignal.SameFile => "same-file",
        CallRankSignal.QualifierAgreement => "qualifier-match",
        CallRankSignal.ImportFilenameMatch => "import-filename-match",
        CallRankSignal.SameRepo => "same-repo",
        _ => "name-only",
    };

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
