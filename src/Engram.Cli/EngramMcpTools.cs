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

        var result = RecallRanker.Pack(connection, query, budget, settings.SeedK, currentSessionId, now, vectorQuery);

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
        "replaces it rather than duplicating it.")]
    public static string Remember(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The fact to remember, as a short, self-contained statement.")] string statement,
        [Description("What or who the fact is about, if not obvious from the statement.")] string? subject = null,
        [Description("Where this was learned — a file path, PR, or command output.")] string? evidence = null,
        [Description("Your own name, if you are a subagent recording this fact rather than the main agent.")] string? agent = null,
        [Description(
            "The bracketed id of a raw user-statement capture this restates, e.g. \"f42\". Closes that "
                + "capture so the rewritten version replaces it instead of duplicating it.")] string? supersedes = null)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); this statement was not saved.";
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

            using var connection = EngramDatabase.OpenInitialized(home);
            var replacementId = UserFacts.Restate(connection, targetId, statement, session.Value, DateTimeOffset.UtcNow);

            if (replacementId is null)
            {
                return $"No live fact with id '{supersedes}' to restate — it may already have been "
                    + "superseded or forgotten. Nothing was saved; call engram_recall to see what stands.";
            }

            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: session.Value,
                Kind: TelemetryEventKind.Remember));

            return $"[{FactCatalog.HandleFor(replacementId.Value)}] replaced capture [{supersedes}]: \"{statement}\"";
        }

        long factId;
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            factId = SessionFacts.Append(
                connection, session.Value, statement, subject, evidence, agent, DateTimeOffset.UtcNow);
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Remember));

        var subjectText = string.IsNullOrWhiteSpace(subject) ? "(unspecified subject)" : subject;
        var evidenceText = string.IsNullOrWhiteSpace(evidence) ? string.Empty : $" (evidence: {evidence})";
        var agentText = string.IsNullOrWhiteSpace(agent) ? string.Empty : $" [via {agent}]";

        return $"[{FactCatalog.HandleFor(factId)}] remembered: {subjectText} — \"{statement}\"{evidenceText}{agentText}";
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

    /// <summary>
    /// The cap is not arbitrary: a batch is one call so end-of-session capture costs one
    /// round trip, and 25 is where the response stops being cheap to read back.
    /// </summary>
    public const int MaxDigestLearnings = 25;

    [McpServerTool(Name = "engram_digest")]
    [Description(
        "Flush the durable learnings from this session in one call, before context is compacted or the " +
        "session ends. Accepts up to 25 short learnings plus an optional summary. Each is stored as a " +
        "session note — recallable in later sessions too — and comes back with an id you can pass to " +
        "engram_forget. Re-sending one already stored creates no duplicate, so calling this at " +
        "compaction and again at the end is safe.")]
    public static string Digest(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("Up to 25 short, self-contained facts learned this session.")] string[] learnings,
        [Description("A one- or two-sentence summary of the session.")] string? session_summary = null)
    {
        if (!homeState.Initialized)
        {
            return "Engram home is not initialised (run 'engram init'); nothing from this digest was saved.";
        }

        var accepted = learnings.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var overflow = Math.Max(0, accepted.Count - MaxDigestLearnings);
        var blank = learnings.Length - accepted.Count;

        var handles = new List<string>(Math.Min(accepted.Count, MaxDigestLearnings));
        var failed = 0;

        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var learning in accepted.Take(MaxDigestLearnings))
            {
                // Per-learning rather than one transaction over the batch: SessionFacts.Append
                // takes its own write lock, and a batch that discards twenty-four good notes
                // because the twenty-fifth collided would be worse than a partial flush the
                // model can see and retry.
                try
                {
                    var factId = SessionFacts.Append(
                        connection, session.Value, learning.Trim(), subject: null, evidence: null, agent: null, now);

                    handles.Add(FactCatalog.HandleFor(factId));
                }
                catch (SqliteException)
                {
                    failed++;
                }
            }

            if (!string.IsNullOrWhiteSpace(session_summary))
            {
                SessionStore.WriteDigest(connection, session.Value, session_summary.Trim(), now);
            }
        }

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.Digest));

        if (handles.Count == 0 && string.IsNullOrWhiteSpace(session_summary))
        {
            return failed > 0
                ? $"Nothing was stored: all {failed} learning(s) failed to write. Try engram_remember with one of them."
                : "Nothing to store — no learnings were supplied.";
        }

        // The ids, not the statements: the model just sent the text, and echoing 25 of them
        // back costs the context this call exists to protect.
        var storedNote = handles.Count > 0
            ? $"{handles.Count} learning(s) stored as session notes [{string.Join(' ', handles)}]. "
                + "Recallable now and in later sessions; pass an id to engram_forget to retract one."
            : "No learnings stored.";

        var summaryNote = string.IsNullOrWhiteSpace(session_summary)
            ? string.Empty
            : " Session summary recorded.";
        var blankNote = blank > 0 ? $" ({blank} blank entr(y/ies) skipped.)" : string.Empty;
        var overflowNote = overflow > 0
            ? $" ({overflow} learning(s) beyond the {MaxDigestLearnings}-cap were not stored — send them in a second call.)"
            : string.Empty;
        var failureNote = failed > 0 ? $" ({failed} learning(s) failed to write.)" : string.Empty;

        return storedNote + summaryNote + blankNote + overflowNote + failureNote;
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
        "needs scrutiny before you rely on it.")]
    public static string Expand(
        EngramHome home,
        McpSessionId session,
        McpHomeState homeState,
        [Description("The bracketed fact id, e.g. \"f42\".")] string id,
        [Description("One of: history, related, evidence, source.")] string view)
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
            _ => $"Unknown view '{view}'. The views are history, related, evidence, and source.",
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
        [Description("Where the correction came from — a file, PR, or the user's words.")] string? evidence = null)
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

        // The store's own collision rule does the revision: one live fact per
        // (subject, predicate), so remembering the correction closes the incumbent and
        // records the reason on the supersession row.
        var result = FactStore.Remember(
            connection,
            new FactWrite(
                target.SubjectPath,
                "concept",
                target.Predicate,
                statement.Trim(),
                target.Scope,
                "stated",
                evidence,
                Regenerable: false,
                SessionId: sessionId),
            now,
            reason.Trim());

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

    private static string When(long unixSeconds) => MomentText.Local(unixSeconds);

    private static string CountText(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
