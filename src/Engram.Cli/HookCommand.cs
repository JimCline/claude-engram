using System.Text.Json;
using Engram.Core;

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
        if (eventName is not ("session-start" or "subagent-start" or "pre-compact"
            or "user-prompt" or "file-touched"))
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
            "pre-compact" => RunPreCompact(home, ReadPayload()),
            "user-prompt" => RunUserPrompt(home, stdout, ReadPayload()),
            "file-touched" => RunFileTouched(home, ReadPayload()),
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

        var sessionId = ResolveSessionId(payload);
        var stored = new List<string>(candidates.Count);

        try
        {
            using var connection = EngramDatabase.OpenInitialized(home);
            var now = DateTimeOffset.UtcNow;

            foreach (var candidate in candidates)
            {
                var topic = candidate.Kind == UserFactKind.Directive
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
    private static IReadOnlyList<CannedFact> LongTermFacts(EngramHome home)
    {
        try
        {
            return FactCatalog.ReadLongTerm(home, DateTimeOffset.UtcNow);
        }
        catch
        {
            return [];
        }
    }

    private static int RunSessionStart(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        var sessionId = ResolveSessionId(payload);
        var primer = PrimerBuilder.Build(LongTermFacts(home));

        try
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.SessionStart));
        }
        catch
        {
        }

        // Fire and forget, and swallowing everything is deliberate: a session must start even if
        // no housekeeping can run. The child decides for itself whether each job is due, so this
        // costs a fork whether or not it ends up doing any work — cheaper than reading config
        // and fingerprinting the store here to find out, which is the work itself.
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } executable)
            {
                MaintenanceLauncher.Spawn(executable, home.Root);
            }
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
        var primer = PrimerBuilder.BuildForSubagent(LongTermFacts(home));

        try
        {
            // Recording the session id the subagent was handed answers, from the first real
            // probe run, whether it matches its parent's. If it does, session facts are
            // shared across the spawn with no further work; if not, D11's sharing needs a
            // parent id threaded through instead of being assumed.
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: ResolveSessionId(payload),
                Kind: TelemetryEventKind.SubagentStart,
                AgentId: payload?.AgentId,
                AgentType: payload?.AgentType));
        }
        catch
        {
        }

        WriteJson(stdout, HookJsonContext.Default.AdditionalContextHookOutput,
            new AdditionalContextHookOutput(new HookSpecificOutput("SubagentStart", primer)));
        return 0;
    }

    private static int RunPreCompact(EngramHome home, HookStdinInput? payload)
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
            var spoolFileName = $"{now.Ticks}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}.spool";
            var spoolPath = Path.Combine(home.QueueDir, spoolFileName);

            using var stream = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(now.ToString("o"));

            // Second line, and optional, so spool files written before this existed still parse
            // as an edit with no path rather than as a corrupt entry.
            if (payload?.ToolInput?.FilePath is { Length: > 0 } path)
            {
                writer.WriteLine(path);
            }
        }
        catch
        {
        }

        return 0;
    }

    private static void WriteJson<T>(TextWriter stdout, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, T value)
    {
        stdout.Write(JsonSerializer.Serialize(value, typeInfo));
        stdout.Write('\n');
    }
}
