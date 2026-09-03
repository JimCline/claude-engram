using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Cli;

internal static class HookCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 1)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var eventName = rest[0];
        if (eventName is not ("session-start" or "subagent-start" or "pre-compact" or "post-compact"
            or "user-prompt" or "file-touched" or "memory-guard" or "lookup-nudge"))
        {
            return Usage(stderr);
        }

        EngramHome home;
        try
        {
            home = EngramHome.ResolveFromProcess(homePath);
        }
        catch
        {
            return 0;
        }

        if (!File.Exists(home.ConfigPath))
        {
            return 0;
        }

        return eventName switch
        {
            // Switch arms evaluate lazily, so file-touched never pays to drain stdin —
            // its payload carries the whole tool_input, which for a Write is an entire
            // file, and its budget is 10ms unconditionally.
            "session-start" => RunSessionStart(home, stdout, ReadPayload()),
            "subagent-start" => RunSubagentStart(home, stdout, ReadPayload()),
            "pre-compact" => RunPreCompact(home, stdout, ReadPayload()),
            "post-compact" => RunPostCompact(home, ReadPayload()),
            "user-prompt" => RunUserPrompt(home, stdout, ReadPayload()),
            "file-touched" => RunFileTouched(home, ReadPayload()),
            "memory-guard" => RunMemoryGuard(home, stdout, ReadPayload()),
            "lookup-nudge" => RunLookupNudge(home, stdout, ReadPayload()),
            _ => 0,
        };
    }

    private static int Usage(TextWriter stderr)
    {
        CliApp.PrintUsage(stderr);
        return 1;
    }

    // The only place every message the user sends passes through. Everything else in this
    // system depends on the model choosing to call engram_remember, and the M0 telemetry
    // says it does not — remember fired 0 times in 1 session. A fact the user states in
    // passing is lost unless something that is not the model writes it down.
    //
    // Two things happen here, and the redundancy is deliberate. The raw sentence is stored
    // immediately, so the fact survives regardless of what the model does next. The model
    // is then asked to restate it as something self-contained, because "last Saturday"
    // only means anything beside the timestamp it arrived with, and a stored sentence
    // nobody can date is a fact that rots.
    //
    // This is the one hook that writes to the database, and D4's no-database rule does not
    // reach it: that rule is about file-touched, and rests on a per-edit firing rate this
    // hook does not have. It fires once per message the user sends, which is orders of
    // magnitude rarer and is already gated on a human typing. Measured over 3 rounds of 40
    // invocations of each published AOT binary: 13.1-13.4 ms here against 11.1-11.4 ms for
    // the version that wrote a JSON file, so the store costs about +2 ms on top of a process
    // start that dominates both. The write is one BEGIN IMMEDIATE transaction holding the
    // lock for the length of an insert, and the only competing writers are this user's own
    // MCP calls.
    private static int RunUserPrompt(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        var candidates = UserStatementClassifier.Classify(payload?.Prompt);
        if (candidates.Count == 0)
        {
            return 0;
        }

        // A cross-session peer message or a background task notification arrives on this
        // hook's own Prompt field looking exactly like ordinary first-person prose — the
        // classifier above cannot tell them apart, and neither can this hook's stdin
        // payload, which carries no provenance field at all (session_id, cwd,
        // transcript_path, permission_mode are the documented common fields; nothing marks
        // who or what submitted it). The transcript record Claude Code just wrote for this
        // exact submission does: confirmed against every real capture and every real
        // mis-capture in this session's own history, 2026-08-09/10 — 2 of 2 genuine prompts
        // carried promptSource "typed", 16 of 16 false positives carried "system". See
        // docs/session-capture-design.md, "The transcript". Checked after classification,
        // not before: the common case is an ordinary prompt with nothing to capture, and
        // that path should not pay for a file read it will not use.
        if (!IsGenuinelyTyped(payload))
        {
            return 0;
        }

        var sessionId = ResolveSessionId(payload);
        var stored = new List<string>(candidates.Count);

        try
        {
            using var connection = EngramDatabase.OpenInitialized(home);
            var now = DateTimeOffset.UtcNow;

            foreach (var candidate in candidates)
            {
                var topic = candidate.Kind == UserFactKind.Instruction
                    ? UserFactTopic.Instruction
                    : UserFactTopic.AboutYou;

                // Null means the store already holds this statement, so there is nothing new
                // to announce. Listing it anyway would ask the model to rewrite a capture
                // that may already have been rewritten.
                if (UserFacts.Capture(connection, topic, candidate.Text, sessionId, now) is { } factId)
                {
                    stored.Add($"[{FactCatalog.HandleFor(factId)}] {candidate.Text}");
                }
            }
        }
        catch
        {
            // A capture that cannot be written is not worth failing a prompt over. Whatever
            // landed before the failure is still reported: a partial capture the model can
            // improve beats discarding the ones that succeeded.
        }

        if (stored.Count == 0)
        {
            return 0;
        }

        // Recorded here rather than above the guard, so the event means a fact was written. Every
        // prompt reaches this hook and most carry nothing to capture; announcing those would make
        // the kind a proxy for "the user typed", which is a thing nothing needed telemetry to know
        // and which anything reacting to the feed would show as memory activity that did not happen.
        // No count rides along, for the reason the record's own phase field gives.
        if (File.Exists(home.ConfigPath))
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.UserPrompt));
        }

        var nudge =
            "Engram captured what the user just stated, verbatim, as durable user-scoped memory:\n"
            + string.Join('\n', stored)
            + "\n\nIf any of those would not stand on its own when read months from now — a relative "
            + "date like \"last Saturday\", an unresolved \"it\" or \"that\" — call engram_remember with "
            + "a rewritten, self-contained version and pass the bracketed id as `supersedes` so the raw "
            + "capture is closed rather than duplicated. If a capture already reads fine on its own, do "
            + "nothing. Do not mention any of this to the user or thank them for the information; it is "
            + "bookkeeping, not a topic.";

        WriteJson(stdout, HookJsonContext.Default.AdditionalContextHookOutput,
            new AdditionalContextHookOutput(new HookSpecificOutput("UserPromptSubmit", nudge)));

        return 0;
    }

    // Bounded, tail-only, and any failure says "not typed" rather than guessing — the
    // transcript format is private and undocumented (docs/session-capture-design.md, "The
    // transcript"), so the failure mode here has to be "capture nothing", never "capture
    // garbage". A missing path, an unreadable file, a record larger than the tail window, or
    // a shape this cannot parse all fall through the same catch.
    private const int TranscriptTailBytes = 262_144;

    private static bool IsGenuinelyTyped(HookStdinInput? payload)
    {
        if (payload?.TranscriptPath is not { Length: > 0 } path)
        {
            return false;
        }

        try
        {
            var line = ReadLastTranscriptLine(path);
            return line is not null && JsonNode.Parse(line)?["promptSource"]?.GetValue<string>() == "typed";
        }
        catch
        {
            return false;
        }
    }

    // UserPromptSubmit fires before Claude processes the prompt, so the record for this exact
    // submission is already the last line — unlike PostCompact's isCompactSummary, there is no
    // separate write path racing this read. Reading only the tail keeps this cheap on a
    // transcript that only grows: this session's own reached 43 MB tonight, and a head-first
    // read of the whole file on every single message would be the file-size trap D53 already
    // paid for once. A read that lands mid-record (the common case, since the seek point is
    // arbitrary) discards everything before the last newline; JsonNode.Parse rejecting a
    // truncated line is what "harvest nothing" looks like when a record is larger than the tail
    // window, not a bug to work around.
    private static string? ReadLastTranscriptLine(string transcriptPath)
    {
        using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var toRead = (int)Math.Min(TranscriptTailBytes, stream.Length);
        if (toRead == 0)
        {
            return null;
        }

        stream.Seek(-toRead, SeekOrigin.End);
        var buffer = new byte[toRead];
        stream.ReadExactly(buffer);

        var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1] : null;
    }

    /// <summary>
    /// The long-term corpus as the primer should describe it, or nothing if it cannot be
    /// read.
    /// </summary>
    /// <remarks>
    /// D4's rule is that <c>file-touched</c> does not open the database, and its whole
    /// justification is that hook's per-edit frequency and its unconditional sub-10 ms
    /// budget. Neither applies to a primer, which fires once per session (or once per
    /// spawn), takes a read and closes it, and exists precisely to report what is in
    /// memory. Reading a hardcoded list instead is not a cheaper way to do that — it is a
    /// different answer to a different question, and once a fact can be forgotten the two
    /// disagree: recall stops returning it while the primer keeps announcing it.
    /// Measured over 3 rounds of 40 invocations of the published AOT binary: session-start
    /// costs 10.6 ms with the hardcoded list and 12.1 ms reading the store, so the read is
    /// about +1.5 ms against a process start that dominates both. Note the hardcoded
    /// version was already over 10 ms — further evidence that D4's sub-10 ms budget was
    /// written for a per-edit hook and never described this one.
    ///
    /// Falling back to <see cref="CannedFacts.All"/> when the read fails would reintroduce
    /// exactly the divergence this removes, and would do it at the worst moment — telling
    /// a user who forgot something that it is still remembered. Saying nothing is the
    /// honest degradation, and the caller already handles an empty primer.
    /// </remarks>
    private static PrimerSummary LongTermFacts(EngramHome home)
    {
        try
        {
            // A fresh connection per call, closed immediately. D4's watched failure mode is WAL
            // checkpoint starvation caused by long-lived read snapshots in the MCP loop, so the
            // connection must not outlive the read.
            using var connection = EngramDatabase.OpenInitialized(home);
            return PrimerSummary.Read(connection, DateTimeOffset.UtcNow);
        }
        catch
        {
            return PrimerSummary.From([]);
        }
    }

    /// <summary>
    /// The primer summary and whether to offer enrollment, read off one connection. Session
    /// start is on the hook's own latency clock (D4), so the enrollment lookup rides the same
    /// open <see cref="PrimerSummary.Read"/> already pays for rather than opening a second
    /// connection beside it.
    /// </summary>
    private static (PrimerSummary Summary, bool OfferEnrollment) SessionStartPrimerInputs(
        EngramHome home, ConfigFile? config, string startDirectory)
    {
        try
        {
            using var connection = EngramDatabase.OpenInitialized(home);
            var now = DateTimeOffset.UtcNow;
            var summary = PrimerSummary.Read(connection, now);
            var offerEnrollment = ShouldOfferEnrollment(connection, config, now, startDirectory);
            return (summary, offerEnrollment);
        }
        catch
        {
            return (PrimerSummary.From([]), false);
        }
    }

    /// <summary>
    /// Whether the primer's enrollment line should appear: config allows it, the session
    /// started inside a git checkout, and that checkout's enrollment state (never-asked, or a
    /// deferral whose cooldown elapsed) calls for asking. Filesystem-only lookup — no git
    /// subprocess on the hook's own clock (D4), which is why this reads <see cref="RepoEnrollment.ByRoot"/>
    /// off the checkout root rather than <see cref="RepoEnrollment.IsEnrolled"/>'s two-step resolution.
    /// <paramref name="startDirectory"/> comes from the hook's stdin payload rather than the
    /// ambient process directory (§6.13) — internal, rather than private, so a fixture can drive
    /// it with an explicit moved-checkout root instead of mutating process-wide cwd.
    /// </summary>
    internal static bool ShouldOfferEnrollment(
        SqliteConnection connection, ConfigFile? config, DateTimeOffset now, string startDirectory)
    {
        if (!AutoIndexOnSessionStart(config))
        {
            return false;
        }

        if (RepoEnrollment.FindCheckoutRoot(startDirectory) is not { } checkoutRoot)
        {
            return false;
        }

        var row = RepoEnrollment.ByRoot(connection, checkoutRoot);
        return RepoEnrollment.ShouldOfferEnrollment(row, now);
    }

    /// <summary>
    /// A misconfigured value must not cost a session its primer, so a config that will not
    /// parse falls back to the feature's default rather than throwing — the same rule
    /// <see cref="Precedence(ConfigFile?)"/> follows for memory precedence. Takes the config
    /// already loaded by <see cref="RunSessionStart"/> rather than loading it again — this hook
    /// is on its own latency clock and re-parsing the same file a second time per session start
    /// was measured overhead for nothing (D4).
    /// </summary>
    private static bool AutoIndexOnSessionStart(ConfigFile? config)
    {
        try
        {
            return config is null
                ? IndexingSettings.DefaultAutoIndexOnSessionStart
                : IndexingSettings.Read(config).AutoIndexOnSessionStart;
        }
        catch
        {
            return IndexingSettings.DefaultAutoIndexOnSessionStart;
        }
    }

    /// <summary>
    /// A telemetry record for a primer hook, saying what the primer actually delivered.
    /// </summary>
    /// <remarks>
    /// <para>Both primer hooks previously wrote a bare timestamp and session id, so 54
    /// <c>session-start</c> and 336 <c>subagent-start</c> records carried nothing about memory at
    /// all. That left <c>recall</c> — 7 events, and a tool the model has to choose to call — as the
    /// only evidence that memory ever reaches anyone, which understates delivery by construction:
    /// the primer reaches every session and every spawn whether or not a tool is called. Neither
    /// D6's gate on M3 nor D18's on M4 can be read off a record that omits the path memory actually
    /// travels.</para>
    ///
    /// <para><b><c>FactCount</c> stays null on purpose, and that is the whole care here.</b> On a
    /// <c>recall</c> record it means facts returned to the model. A primer returns no facts — it
    /// returns a count line and, at session start, up to two example bodies — so filling the same
    /// field with something almost-right is exactly how the probe's two session counts came to be
    /// subtracted from each other (D43). <c>LongTermFactCount</c> is what the store held and
    /// <c>TokensReturned</c> is what was injected, both well-defined for a primer and both directly
    /// comparable to the same fields on a recall.</para>
    /// </remarks>
    private static TelemetryRecord PrimerRecord(
        string sessionId,
        string kind,
        int longTermFactCount,
        int directiveCount,
        string primer) =>
        new(Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: sessionId,
            Kind: kind,
            LongTermFactCount: longTermFactCount,
            TokensReturned: TokenEstimator.Estimate(primer),
            DirectiveCount: directiveCount);

    /// <summary>What the primer should say about where this agent's durable memory lives.</summary>
    /// <remarks>
    /// A misconfigured value must not cost a session its primer, so a config that will not parse
    /// falls back to the default rather than throwing — <see cref="MemorySettings.Read"/> already
    /// reports the problem through <c>doctor</c>, which is where a person will look for it.
    /// </remarks>
    private static MemoryPrecedence Precedence(EngramHome home) => Precedence(TryLoadConfig(home));

    /// <summary>
    /// For a caller that already holds a loaded config — <see cref="RunSessionStart"/> — so the
    /// same bytes are not parsed a second time on the hook's own clock (D4).
    /// </summary>
    private static MemoryPrecedence Precedence(ConfigFile? config)
    {
        try
        {
            return config is null
                ? MemorySettings.DefaultPrecedence
                : MemorySettings.Read(config).Precedence;
        }
        catch
        {
            return MemorySettings.DefaultPrecedence;
        }
    }

    /// <summary>
    /// Loaded once per hook invocation and threaded to whichever readers need it that same run,
    /// rather than each one re-parsing <c>config.toml</c> off disk independently.
    /// </summary>
    private static ConfigFile? TryLoadConfig(EngramHome home)
    {
        try
        {
            return ConfigFile.Load(home.ConfigPath);
        }
        catch
        {
            return null;
        }
    }

    private static int RunSessionStart(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        var sessionId = ResolveSessionId(payload);

        // Fire and forget, and swallowing everything is deliberate: a session must start even if
        // no housekeeping can run. The child decides for itself whether each job is due, so this
        // costs a fork whether or not it ends up doing any work — cheaper than reading config
        // and fingerprinting the store here to find out, which is the work itself.
        //
        // Before the read rather than after it, but do not read that as a measured optimisation:
        // the measurement that once justified it was wrong. It timed the hook through a pipe,
        // and the detached child was holding that pipe, so moving the spawn earlier only moved
        // when the child started and therefore when the timer stopped — which is also why the
        // "saving" appeared to grow with the corpus. MaintenanceLauncher now redirects the
        // shell's own descriptors and nothing waits on the child at all; measured either side,
        // the spawn costs 1.6-3.4 ms and the ordering is worth no part of it. It stays here
        // because a fork is never more expensive for happening while the parent is small.
        var startDirectory = payload?.Cwd ?? Directory.GetCurrentDirectory();
        var config = TryLoadConfig(home);

        try
        {
            if (Environment.ProcessPath is { Length: > 0 } executable)
            {
                var syncEnabled = config is not null && SyncSettings.Read(config).Enabled;
                MaintenanceLauncher.Spawn(executable, home.Root, syncEnabled, startDirectory);
            }
        }
        catch
        {
        }

        var (summary, offerEnrollment) = SessionStartPrimerInputs(home, config, startDirectory);
        var primer = PrimerBuilder.Build(summary, Precedence(config), offerEnrollment);

        try
        {
            Telemetry.Append(
                home,
                PrimerRecord(
                    sessionId, TelemetryEventKind.SessionStart, summary.FactCount, summary.Directives.Count, primer));
        }
        catch
        {
        }

        // An empty primer became reachable once the standing guidance moved into the tool
        // descriptions (D15): with nothing stored there is no coverage line and nothing
        // left to say. Emitting an empty additionalContext would spend a hook round trip
        // injecting a blank string.
        if (!string.IsNullOrWhiteSpace(primer))
        {
            WriteJson(stdout, HookJsonContext.Default.AdditionalContextHookOutput,
                new AdditionalContextHookOutput(new HookSpecificOutput("SessionStart", primer)));
        }

        return 0;
    }

    // SessionStart never fires for a subagent, and SubagentStart reaches spawn paths a
    // PreToolUse rewrite of the Agent tool structurally cannot — a measured 47-agent
    // workflow run produced zero relay events through that route. This is the only proven
    // channel to every spawn.
    //
    // Bare stdout is SILENTLY DISCARDED on this event. SessionStart accepts it, so the
    // habit formed there actively misleads here; all three keys below are load-bearing and
    // hookEventName must match the event. There is no error when this is wrong — the
    // primer simply never arrives, which is indistinguishable from a subagent ignoring it.
    private static int RunSubagentStart(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        var summary = LongTermFacts(home);
        var primer = PrimerBuilder.BuildForSubagent(summary, Precedence(home));

        try
        {
            // Recording the session id the subagent was handed answers, from the first real
            // probe run, whether it matches its parent's. If it does, session facts are
            // shared across the spawn with no further work; if not, D11's sharing needs a
            // parent id threaded through instead of being assumed.
            Telemetry.Append(home, PrimerRecord(
                ResolveSessionId(payload), TelemetryEventKind.SubagentStart, summary.FactCount,
                summary.Directives.Count, primer) with
            {
                AgentId = payload?.AgentId,
                AgentType = payload?.AgentType,
            });
        }
        catch
        {
        }

        WriteJson(stdout, HookJsonContext.Default.AdditionalContextHookOutput,
            new AdditionalContextHookOutput(new HookSpecificOutput("SubagentStart", primer)));
        return 0;
    }

    // For any initialised home (the shared gate above already returns 0 before this runs for
    // an uninitialised one), emits unconditionally: no config read, no database open. There is
    // no further state that could make the right answer differ, and a hook that decided from
    // store state would be a hook that can be wrong about it (the same argument D51 uses for
    // the primer against an empty store). Written bare, never through
    // WriteJson/hookSpecificOutput — that envelope is measured to be REJECTED on this event,
    // the exact inverse of SessionStart/SubagentStart. The telemetry write stays first: if the
    // stdout write throws, the record of the attempt has already landed. FactCount stays null
    // on this record — the instruction's length and its item cap are not facts returned
    // (D43/D46).
    private static int RunPreCompact(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        var sessionId = ResolveSessionId(payload);

        try
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.PreCompact));
        }
        catch
        {
        }

        stdout.Write(CompactionDigest.Instruction);
        stdout.Write('\n');

        return 0;
    }

    // The harvester half of D62 (2b). PostCompact carries the whole compaction summary
    // inline as compact_summary, so this never reads the transcript — no tail-read, no
    // polling for a record a separate write path produces, no race (see
    // docs/session-capture-design.md, "The PostCompact trigger"). Each kept item is written
    // as a session fact tagged with CompactionDigest.HarvesterAgent, satisfying Jim's
    // provenance decision without a schema change: SessionFacts.Append already dedupes a
    // repeat statement within one session, which is what makes harvesting idempotent for
    // free if this hook ever fires twice for the same compaction. Telemetry is recorded
    // only when something was actually written, matching user-prompt's rule that the event
    // means a fact landed, not that the hook merely ran.
    private static int RunPostCompact(EngramHome home, HookStdinInput? payload)
    {
        if (payload?.CompactSummary is not { Length: > 0 } summary)
        {
            return 0;
        }

        var parsed = CompactionDigestParser.Parse(summary);
        if (parsed.Items.Count == 0)
        {
            return 0;
        }

        var sessionId = ResolveSessionId(payload);

        try
        {
            using var connection = EngramDatabase.OpenInitialized(home);
            var now = DateTimeOffset.UtcNow;

            foreach (var item in parsed.Items)
            {
                SessionFacts.Append(
                    connection,
                    sessionId,
                    item,
                    subject: null,
                    evidence: null,
                    agent: CompactionDigest.HarvesterAgent,
                    now);
            }

            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: now.UtcDateTime.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.PostCompact));
        }
        catch
        {
        }

        return 0;
    }

    // Claude Code pipes a JSON payload with a "session_id" field on the hook's stdin
    // (https://code.claude.com/docs/en/hooks). Only read it when stdin is actually
    // redirected, so a plain interactive invocation never blocks waiting on a terminal.
    //
    // Read once and passed down rather than fetched where needed: a stream drains once,
    // and a second caller would silently get nothing. Caching it in a static would fix
    // that but leak one invocation's payload into the next inside a test process.
    private static HookStdinInput? ReadPayload()
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }

        try
        {
            var input = Console.In.ReadToEnd();
            return string.IsNullOrWhiteSpace(input)
                ? null
                : JsonSerializer.Deserialize(input, HookJsonContext.Default.HookStdinInput);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSessionId(HookStdinInput? payload) =>
        payload?.SessionId is { Length: > 0 } sessionId ? sessionId : Guid.NewGuid().ToString("N");

    /// <summary>
    /// Records that a file changed, and which one. Never opens the database (D4).
    /// </summary>
    /// <remarks>
    /// The payload is read for its path even though every other cost here was shaved to protect
    /// the 10 ms budget, because a queue of bare timestamps answers exactly one bit no matter how
    /// many entries it has — "something changed" — and the indexer that drains it needs to know
    /// what to re-read. Measured on the published binary: piping the payload in at all costs
    /// 0.27 ms, and `user-prompt` parses the same stdin, opens the store *and* writes a fact for
    /// 0.67 ms more than this hook spent doing none of it. Parsing is not what threatens this
    /// budget; opening the database is, and this still does not.
    /// </remarks>
    private static int RunFileTouched(EngramHome home, HookStdinInput? payload)
    {
        try
        {
            Directory.CreateDirectory(home.QueueDir);

            var now = DateTime.UtcNow;
            var touched = payload?.ToolInput?.FilePath is { Length: > 0 } file ? file : null;

            var spoolFileName = $"{now.Ticks}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}.spool";
            var spoolPath = Path.Combine(home.QueueDir, spoolFileName);

            using var stream = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(now.ToString("o"));

            // Second line, and optional, so spool files written before this existed still parse
            // as an edit with no path rather than as a corrupt entry.
            if (touched is not null)
            {
                writer.WriteLine(touched);
            }

            // The one telemetry append in Engram that may not wait. The spool file above is
            // per-invocation and so uncontended by construction, which is what D4 bought; this one
            // is shared, and N concurrent edits mean N processes opening it. A zero retry budget
            // makes that queue impossible to join — one attempt, and a record lost to a collision
            // rather than a hook that overran.
            //
            // Both halves of that are measured on the published binary, against the version
            // without this write, alternating which arm runs first: +0.11 ms at the minimum and
            // +0.08 ms at p50, which is the harness noise floor (±0.07 ms, established by running
            // the same binary against itself). An earlier reading of +0.78 ms was ordering bias
            // and nothing else — it is what an always-A-first loop charges the first arm — and it
            // was very nearly the reason this write was moved out of the hook and into a polling
            // service in the server that would have existed for no reason.
            if (File.Exists(home.ConfigPath))
            {
                Telemetry.Append(
                    home,
                    new TelemetryRecord(
                        Timestamp: now.ToString("o"),
                        SessionId: ResolveSessionId(payload),
                        Kind: TelemetryEventKind.FileTouched,
                        Path: touched),
                    TimeSpan.Zero);
            }
        }
        catch
        {
        }

        return 0;
    }

    // PreToolUse fires ahead of every Write/Edit/MultiEdit, in every session — the same
    // frequency class as file-touched (D4) — so the non-matching path must do nothing beyond
    // parse-stdin -> path check -> exit 0. No config parse, no state/telemetry/database touch
    // may precede the path-match check below; the shared File.Exists(home.ConfigPath) gate this
    // switch already sits behind is the one exception, priced into file-touched's own measured
    // budget already (Amendment 1 to the memory-guard spec).
    private static int RunMemoryGuard(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        if (payload?.ToolInput?.FilePath is not { Length: > 0 } filePath)
        {
            return 0;
        }

        if (!MemoryGuardPathMatcher.IsFileBasedMemoryFile(filePath, home.ClaudeProjectsDir))
        {
            return 0;
        }

        if (Precedence(home) == MemoryPrecedence.Off)
        {
            return 0;
        }

        if (payload?.SessionId is not { Length: > 0 } sessionId)
        {
            return 0;
        }

        if (SessionNudgeState.Contains(home.MemoryGuardStatePath, sessionId))
        {
            return 0;
        }

        // Before emitting the deny, not after — a crash between the two must not produce a
        // session that gets denied twice; the reverse order risks exactly that.
        if (!SessionNudgeState.TryAppend(home.MemoryGuardStatePath, sessionId))
        {
            return 0;
        }

        try
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.MemoryGuard,
                Path: filePath));
        }
        catch
        {
        }

        WriteJson(stdout, HookJsonContext.Default.PreToolUseHookOutput,
            new PreToolUseHookOutput(new PreToolUseHookSpecificOutput(
                "PreToolUse", "deny", MemoryGuardDenyReason(filePath))));

        return 0;
    }

    private static string MemoryGuardDenyReason(string filePath) =>
        $"Engram memory guard: this write targets file-based memory ({filePath}), but Engram is "
        + "installed and is the preferred durable store. Save durable facts with engram_remember "
        + "instead — supersede the auto-captured id if one was printed. If the file write is "
        + "genuinely right (content that must stay human-browsable on disk), re-run the exact "
        + "same call and it will proceed; this reminder fires once per session.";

    // PreToolUse fires ahead of every Grep, Glob and Bash — a wider net than memory-guard's
    // Write|Edit|MultiEdit and the widest of any hook here — so the non-matching path must do
    // nothing beyond parse-stdin -> shape check -> exit 0, with no state, telemetry or database
    // touch before SymbolQueryDetector has said yes. Bash is in the matcher deliberately, and it
    // is the expensive half: a shell `grep -rn ProcessFile` is the exact call this exists to
    // catch, and a matcher of Grep|Glob alone would miss the incident that motivated the hook
    // while still taxing the two tools it does cover.
    //
    // The classifier, not the matcher, is what keeps this cheap in practice — see
    // SymbolQueryDetector, where every rule is a reason to stay silent. Ordinary word searches,
    // literals, TODOs and glob paths fall out before any I/O happens.
    private static int RunLookupNudge(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        if (payload?.ToolInput is not { } toolInput)
        {
            return 0;
        }

        // Grep and Glob name the query outright; Bash hides it in a command line, and a command
        // that is not a search at all yields null here without touching anything else.
        var query = payload.ToolName switch
        {
            "Grep" or "Glob" => toolInput.Pattern,
            "Bash" => SymbolQueryDetector.ExtractSearchPattern(toolInput.Command),
            _ => null,
        };

        if (!SymbolQueryDetector.LooksLikeSymbol(query))
        {
            return 0;
        }

        if (Precedence(home) == MemoryPrecedence.Off)
        {
            return 0;
        }

        if (payload.SessionId is not { Length: > 0 } sessionId)
        {
            return 0;
        }

        // The deny asserts that Engram indexes this repo's code graph, so it may only fire where
        // that is true — an enrolled checkout indexed at least once. Anywhere else (never asked,
        // declined, deferred, enrolled but not yet scanned, no checkout at all) it stays silent and
        // the once-per-session shot is left unspent for a later lookup the graph can answer. Read
        // from the file stamp, never the table: this hook may not open the database (D4, D66).
        var checkoutRoot = RepoEnrollment.FindCheckoutRoot(payload.Cwd ?? Directory.GetCurrentDirectory());
        if (checkoutRoot is null)
        {
            return 0;
        }

        var stamp = RepoIndexStamp.Read(home.RepoIndexStampPath, checkoutRoot);
        if (stamp is not { State: RepoEnrollmentState.Enrolled, LastIndexedAt: not null })
        {
            return 0;
        }

        // The outcome file's lines are composite exact-match keys; the tab separator is safe only
        // because LooksLikeSymbol already rejected any query containing whitespace, so nothing here
        // parses — it only compares.
        var nudgedKey = $"{sessionId}\t{query}";

        if (SessionNudgeState.Contains(home.LookupNudgeStatePath, sessionId))
        {
            // Already nudged this session. Re-running the very query that was denied is the deny's
            // own escape hatch being taken, and it is the one behaviour compliance is measured by
            // (1 − overridden / nudged, all inside the hook's own id space — D43). Any other query
            // proceeds unremarked; a rephrased re-run reads as compliance, under-counting on purpose.
            if (!SessionNudgeState.Contains(home.LookupNudgeOutcomePath, nudgedKey))
            {
                return 0;
            }

            // The marker holds the ≤2-appends-per-session invariant, and it lands before the
            // telemetry for the same reason the nudge writes state before the deny.
            var marker = $"{sessionId}\toverridden\t{query}";
            if (SessionNudgeState.Contains(home.LookupNudgeOutcomePath, marker)
                || !SessionNudgeState.TryAppend(home.LookupNudgeOutcomePath, marker))
            {
                return 0;
            }

            try
            {
                Telemetry.Append(home, new TelemetryRecord(
                    Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                    SessionId: sessionId,
                    Kind: TelemetryEventKind.LookupNudge,
                    Query: query,
                    Phase: LookupNudgePhaseOverridden,
                    Repo: stamp.Identity));
            }
            catch
            {
            }

            // Observed, never commented on: a second message here would be the nag D37 forbids.
            return 0;
        }

        // Before the deny, for the reason memory-guard records above: a crash between the two
        // must not leave a session that gets denied twice.
        if (!SessionNudgeState.TryAppend(home.LookupNudgeStatePath, sessionId))
        {
            return 0;
        }

        // Result ignored: if this line is lost the override can never be detected for this
        // session, but the deny still fires — the signal is lost, the behaviour is not.
        SessionNudgeState.TryAppend(home.LookupNudgeOutcomePath, nudgedKey);

        try
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.LookupNudge,
                Query: query,
                Phase: LookupNudgePhaseNudged,
                Repo: stamp.Identity));
        }
        catch
        {
        }

        WriteJson(stdout, HookJsonContext.Default.PreToolUseHookOutput,
            new PreToolUseHookOutput(new PreToolUseHookSpecificOutput(
                "PreToolUse", "deny", LookupNudgeDenyReason(query!))));

        return 0;
    }

    /// <summary>The <c>phase</c> a <c>lookup-nudge</c> record carries at the deny.</summary>
    internal const string LookupNudgePhaseNudged = "nudged";

    /// <summary>
    /// The <c>phase</c> written once when the nudged session re-runs the exact query it was denied.
    /// </summary>
    internal const string LookupNudgePhaseOverridden = "overridden";

    private static string LookupNudgeDenyReason(string query) =>
        $"Engram lookup nudge: \"{query}\" is shaped like a symbol, and Engram indexes this repo's "
        + "code graph. Try engram_navigate first — relation defined_at for where it lives, callers "
        + "or callees to trace a call chain, imports for a file's dependencies. If Engram has "
        + "nothing, or you are searching text rather than resolving a symbol, re-run the exact same "
        + "call and it will proceed; this reminder fires once per session.";

    private static void WriteJson<T>(TextWriter stdout, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, T value)
    {
        stdout.Write(JsonSerializer.Serialize(value, typeInfo));
        stdout.Write('\n');
    }
}
