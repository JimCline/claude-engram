# Proactive `engram_remember` adoption — design spec

Design only. Nothing here is implemented. Sibling of `docs/code-nav-adoption-spec.md` (read side);
this is the write side. Read `docs/session-capture-design.md` and D51/D62 first — this spec does not
re-derive what they settled, and its rejected list inherits theirs.

## 0. TL;DR

- **The failure mode is (c) with a structural (a) beneath it, not (b).** The harvester runs, fires on
  auto and manual compactions, and is now the *largest* write path by volume. The `[Description]` for
  `engram_remember` already names the own-decision trigger verbatim. What is missing is not a mechanism
  or an instruction; it is a **trigger at a moment** — the same diagnosis `session-capture-design.md`
  wrote for `digest`: *standing guidance where the model already reads is not a trigger.*
- **Nothing measures the gap today.** Whether a given Claude Code session called `engram_remember` at
  all is not computable (D43: hook session ids and MCP session ids are disjoint). So Stage 1 is an
  **observer**, not a nudge: a `PostToolUse` hook matched on Engram's own MCP tools writes a
  hook-space telemetry record per call. That one hook closes D43's attribution gap for every
  Engram tool, not just `remember`, and it is what makes every later stage measurable.
- **Stage 2 is one conditional nudge per session, at zero turn cost**, delivered through the
  `UserPromptSubmit` hook that already runs and already emits `additionalContext`, gated on
  "≥ T minutes into the session and no `remember`/`revise` observed". Phase `nudged` / `followed`,
  same shape as the lookup-nudge's L6.
- **The Stop hook is Stage 3 and evidence-gated.** It is the only lever that fires when the user
  never types again, and it costs a whole turn every time. Recommended only if Stage 2's `followed`
  rate is high but the loss persists in single-prompt autonomous runs.
- **Read/write asymmetry, stated plainly:** there is no tool call to intercept, so no
  `PreToolUse` deny can exist here. But the write side has something the read side lacks — the
  *presence* of the call is observable in hook space (`PostToolUse` on the MCP tool), so absence
  becomes countable per session. The detection problem is "no remember for N minutes/turns", which
  is cheap; the *judgement* problem — was there a decision worth keeping — is left with the model,
  never with a heuristic (design doc: *only judgment identifies decisions*).

## 1. What exists, and what is measured

| Surface | Where | Fires | Measured |
|---|---|---|---|
| `engram_remember` `[Description]` | `src/Engram.Cli/EngramMcpTools.cs:81-91` | every session, survives compaction (D51) | not in isolation (same limit as code-nav L4) |
| Text of that trigger | *"whenever you learn something durable: a decision, a constraint, a partial result, a dead end already ruled out — state you would otherwise repeat in context and would lose to compaction"* | — | — |
| Primer write line | `PrimerBuilder.cs:13-14` (subagent), session primer since `6a0b8ff` | at `startup|resume|clear|compact`; decays between | no |
| Recall footer `→ engram_remember what you discover` | `RecallRanker`/`RecallEngine` | only after a recall (424 in 30 d) | no |
| `~/.claude/CLAUDE.md` "whenever you reach a decision or finding worth keeping" | user file | once, at session start; decays | no |
| `user-prompt` capture (D51) | `HookCommand.RunUserPrompt` | per prompt; writes only for first-person statements; 103 captures / 30 d | yes, kind `user-prompt` |
| PreCompact instruction + PostCompact harvester (D62) | `RunPreCompact`, `RunPostCompact`, `CompactionDigest*.cs` | every compaction, auto and manual | yes, kinds `pre-compact` / `post-compact` |
| `/digest` slash command | `plugin/commands/digest.md` | when typed | opt-in — same class as the retired tool |

**Real-instance numbers, 2026-08-04 → 2026-09-03 (telemetry.jsonl, read-only; DB read-only):**

- `session-start` 2,166 (over-counts sessions: fires on resume/clear/compact too), `pre-compact` 1,559,
  `post-compact` 1,371, `remember` 1,427, `revise` 27, `recall` 424, `user-prompt` 103.
- `post-compact` is written **only when ≥ 1 fact landed**, so **1,371 / 1,559 = 88 % of compactions
  harvested something.** 165 distinct sessions carry harvested facts in 35 days. Today alone: 279
  harvested facts against 43 `remember` calls.
- Harvested facts are ordinary session facts under `/sessions/<id>/compaction-digest/…`; nothing
  ages them out (`StoreCompactor`, `SessionFacts`: no expiry path), and recall returns them (this
  spec's own retrieval surfaced several tagged `compaction-digest`).
- Live facts: 53,940.

**What is *not* measurable today, and it is the number this whole question turns on:** the share of
sessions in which the model called `engram_remember` zero times. `remember` records carry the MCP
session id; `session-start`/`post-compact` carry the hook session id; D43 forbids the join.
`session-open` (201) is the count of MCP sessions that made *any* tool call — it does not say which
hook session they belong to.

## 2. Diagnosis — which of (a) / (b) / (c)

**(b) is ruled out as the primary gap.** The harvester fires, on auto compactions (captured live,
`trigger: "auto"`, design doc 2b), and populates on 88 % of compactions. Two residual (b) sub-cases
are real but bounded and are carried as NEEDS-EVIDENCE rather than assumed:

- **(b1) sessions that end without ever compacting.** Nothing automatic runs there. The retirement
  commit `4758913` moved this case "onto mechanisms that already exist" — meaning `remember` along
  the way, which is exactly the behaviour Jim reports as unreliable. So (b1) is (a) wearing a
  different hat; it is solved by whatever solves (a), and measured by N2.
- **(b2) summarizer selectivity.** The instruction says *prefer omission*; a decision from early in
  a long session may not survive into the summary that gets harvested. Populated blocks average
  ~10 items today (279 / 26), so selectivity is not starving the channel, but per-decision recall of
  the summarizer is unmeasured (N1).

**(c) is the primary diagnosis, and it is a trigger problem, not an instruction problem.** Every
write-side instruction that exists is *standing* guidance: the `[Description]` (read when the tool
list is scanned, not when a decision is reached), the primer (decays), CLAUDE.md (decays), the recall
footer (only after a recall). None fires *at the moment* a decision is formed. `session-capture-design.md`
§"The problem, as it actually is" reached the same conclusion for `digest` — 30 recalls carried its
name in the footer and produced 0 calls — and the fix there was to remove the opt-in entirely. For
own-decisions the opt-in cannot be removed without a model (rejected: *deterministic extraction of
decisions from prose*, precision; *detached harvester with own model*, cost — both inherited), so the
next-best thing is a trigger that fires **conditionally, at a moment, once**, exactly as the
lookup-nudge does for the read side.

**Rewriting the `[Description]` again is not a lever here.** D51 already made it open on durability
and name both triggers; D51's own measurement is that a rule with a trigger beats a longer rule
without one, and the trigger is present. A second rewrite would be an unmeasurable change to a
surface that already says the right thing.

**What this spec cannot settle by reading:** whether the decisions Jim noticed losing were in
sessions that (i) never compacted, (ii) compacted but the summarizer dropped them, or (iii) were
harvested and then not *recalled* — which is the read-side problem, not this one. N1 asks Jim for
three concrete instances; each classifies mechanically against telemetry.

## 3. The asymmetry, and what it removes from the lever set

Code-nav had a **tool call to intercept**: the wrong action (`Grep`) is itself an event, so
`PreToolUse` can deny it once and the re-run is observable. Here the wrong action is **doing
nothing**, which is not an event. Consequences:

- No `PreToolUse` lever exists. There is nothing to deny.
- "Override" has no direct analogue. The measurable pair is `nudged` → `followed` (a `remember` was
  observed in the same hook session after the nudge), which is weaker than overridden-by-re-run
  because "followed" cannot tell *followed because nudged* from *would have called anyway*. The
  control for that is the nudged/un-nudged split that the trigger threshold itself creates (§4 L3).
- What the write side has that the read side lacked: the *desired* action **is** a tool call, and
  `PostToolUse` fires on MCP tools by name (hooks guide, "Match MCP tools"; `mcp__plugin_engram_engram__engram_remember`
  is already the spelling in `ClaudePermissions.cs` and `plugin/commands/*.md`). So the presence of
  the call is observable in the hook's own id space, which is precisely what D43 said nothing could
  do. That is the foundation every stage below stands on.
- Hooks that could see the *assistant's text* — `Stop` (via `transcript_path`), `MessageDisplay`
  (undocumented schema) — exist, but reading the transcript is the private-format risk the design doc
  already flagged, and classifying prose into "decision / not" is the rejected precision problem.
  No lever below reads assistant text. The model keeps the judgement; the hook keeps the clock.

## 4. Levers

### L1 — Hook-space observer for Engram tool calls  **(Stage 1, measurement)**

- **Register** in `plugin/hooks/hooks.json` a second `PostToolUse` entry:
  `"matcher": "mcp__plugin_engram_engram__.*"` → `engram-exec.sh hook tool-observed`. The existing
  `Edit|Write|MultiEdit|NotebookEdit` → `file-touched` entry is untouched. Claude Code filters on the
  matcher before spawning, so the process cost is per Engram tool call (~60/day), not per tool call.
- **Verb** `tool-observed` in `HookCommand.cs` dispatch (`Run`, `:11`), `RunToolObserved(home, payload)`:
  1. `payload.SessionId` empty → return 0 (no synthetic ids; a record with a Guid session id would be
     an unjoinable row, which is the defect this exists to remove).
  2. `payload.ToolName` (new `HookStdinInput` field, JSON `tool_name`; `agent_id`/`agent_type` are
     already carried per `docs/claude-code-hooks-reference.md:19`) — strip the
     `mcp__plugin_engram_engram__engram_` prefix; the remainder is the tool short name
     (`remember`, `recall`, …). Unknown prefix → return 0.
  3. `Telemetry.Append(home, new TelemetryRecord(Timestamp, SessionId, Kind: TelemetryEventKind.ToolObserved, Tool: shortName, AgentId, AgentType))`
     inside the same best-effort `try/catch` every hook uses; default 500 ms retry budget (not
     `TimeSpan.Zero` — this is not in `file-touched`'s frequency class).
  4. **Never opens the database** (D4/D66 class). No stdout. Return 0.
- **Telemetry:** new constant `TelemetryEventKind.ToolObserved = "tool-observed"`; new
  `TelemetryRecord` field `[property: JsonPropertyName("tool")] string? Tool = null`. A **subject**
  field, no count (D55/D56). It is deliberately its own kind: folding it into `remember` would double
  D18/D43's adoption numbers — the server already writes a `remember` record for the same call — and
  the two records are the two halves of the join, one per id space. Add it to `TelemetryEventKind.All`
  and the reflection guard covers it (D55).
- **Consumer sweep:** `engram activity`/probe readers count by kind — a new kind appears as its own
  row, nothing breaks. `TelemetryProbeReport`'s D43 wording ("what fraction of sessions used memory is
  not computable") becomes false the day this lands and must be amended in the same commit, not
  left to contradict the data. Webhook: delivered verbatim like any kind (D55).
- **What it answers**, all in one id space: per hook session — remembers, recalls, compactions,
  prompts-with-capture, lookup-nudges. Specifically: **share of sessions with ≥ 1 `session-start`
  and 0 `tool-observed remember|revise`**, split by whether a `post-compact` occurred. That is the
  a/(b1) split of §2, and it is the number that decides whether L3 is needed at all and at what
  threshold.
- **Subagents:** `PostToolUse` fires inside a subagent's loop with `agent_id` present
  (`hooks-reference.md:19`), so a subagent's `remember` is attributed to the parent hook session,
  tagged by `AgentId`. That is the correct attribution for "did this session record anything".
- **Verification:** tier-2 test: payload with `tool_name: mcp__plugin_engram_engram__engram_remember`
  → exactly one `tool-observed` line with `tool: "remember"`; foreign tool name → no line; missing
  session id → no line; no `engram.db` access (assert the store's mtime is unchanged, the `doctor`
  pattern). Falsify: drop the prefix strip → `tool` carries the full name → test red. Tier-3: run the
  published binary with the same payload; never assert a total line count (D56).

### L2 — Session-start stamp  **(Stage 2 prerequisite; trivial)**

`RunSessionStart` already runs once per `startup|resume|clear|compact` and already opens the store.
Add, best-effort, `SessionNudgeState.TryAppend(home.SessionStartPath, $"{sessionId}\t{unixSeconds}")`
where the line is written **only if no line for this `sessionId` exists** (`resume`/`compact` re-fires
must not reset the clock — the whole point is elapsed time since the session began). Use a
`Contains`-style prefix check on `"{sessionId}\t"` — a new `SessionNudgeState.FindLine(path, prefix)`
returning the first line starting with the prefix, or extend with an exact-line composite as L6 did.
Growth: ~2,200 lines / month ≈ 100 KB / month; same unbounded class as `lookup-nudge.state`
(1,591 B after a month) — acceptable, and if it ever matters the fix is an age prune from
`MaintenanceLauncher` beside `queue compact --if-large`; a stale session's start time has no other
reader.

### L3 — Conditional write nudge at `UserPromptSubmit`  **(Stage 2)**

- **Placement:** inside `RunUserPrompt`, *after* the existing capture path has decided what it will
  emit, so a prompt that carries a capture and a nudge emits one `additionalContext` with both
  (capture text first — it already ends with "do nothing" guidance; the nudge follows as its own
  paragraph). Never two envelopes on one prompt.
- **Gate, in order, every step file-only and cheap:**
  1. `[memory] precedence = off` → no nudge (D51's one switch; no second key).
  2. `SessionNudgeState.Contains(home.RememberNudgePath, sessionId)` → already nudged → return.
  3. Start time from L2 absent → return (unknown age reads as young; fails toward silence).
  4. `now − start < T` → return. **T = 20 minutes** — decided here so the Implementor is not choosing
     it, and flagged in §8 as Jim's to override. Rationale: short Q&A sessions never reach it; a
     20-minute working session with zero writes is the population Jim describes.
  5. Any `tool-observed` with `tool ∈ {remember, revise}` for this session → return. **Source of this
     bit:** the hook may not tail `telemetry.jsonl` (30 MB; and D55 warns that a second reader can
     starve `DurableAppend`). So L1's verb **also** appends `sessionId` to
     `home.RememberSeenPath` (`SessionNudgeState.TryAppend`, only when `tool ∈ {remember, revise}`,
     only if not already `Contains` — one line per session). Two tiny state files, both read by
     exact-line `Contains`; no new file format.
  6. `!TryAppend(RememberNudgePath, sessionId)` → return (marker before emission, so a crash cannot
     double-nudge; identical to L6's ordering rule).
  7. Emit; then telemetry with its own kind `TelemetryEventKind.RememberNudge = "remember-nudge"`,
     `Phase: nudged` — never reuse `lookup-nudge`, whose compliance metric would otherwise absorb it.
- **Text** (constant in `HookCommand`, ~70 tokens, names the tool because this is hook
  `additionalContext`, not primer guidance — D15's rule does not apply, same as the user-prompt
  capture text already names `engram_remember`):
  > This session has run for a while with nothing recorded in Engram. If you have reached a
  > decision, a measured number, a constraint, or ruled something out, call `engram_remember` now —
  > one call per fact, each self-contained. If nothing durable has happened, do nothing. Do not
  > mention this to the user.
- **`followed`:** in L1's verb, when `tool ∈ {remember, revise}` and `Contains(RememberNudgePath, sessionId)`
  and `!Contains(RememberSeenPath, sessionId)` (i.e. this is the *first* remember after a nudge),
  append telemetry `remember-nudge` / `Phase: followed` before appending to `RememberSeenPath`.
  ≤ 2 `remember-nudge` records per session, by construction.
- **Metric:** `followed / nudged` over hook-space records only. Never ratio against MCP-space
  `remember` (D43). The un-nudged population (sessions that remembered before T) is the implicit
  control: if `followed` ≈ the un-nudged sessions' natural remember rate, the nudge did nothing.
- **Cost:** two small file scans on the user-prompt path, which already opens the store; one
  ~70-token line, once per qualifying session; zero turns. D37: conditional, once, silent otherwise.
- **Does not fire in subagents** (`UserPromptSubmit` never fires there, `hooks-reference.md:18`) —
  correct: the subagent primer already carries the write instruction and its report-back is the
  parent's problem.
- **Peer messages** arrive on this same hook (`RunUserPrompt`'s own comment), so an orchestrator
  session driven by cross-session traffic still gets its one nudge. The `IsGenuinelyTyped` guard
  applies to *capture*, not to the nudge — the nudge goes to the model regardless of who typed.
- **Verification:** tier-2, `ConsoleStdinCollection`: (i) session older than T, no remember seen →
  one nudge, one `nudged` record; (ii) second prompt same session → nothing; (iii) remember seen →
  nothing; (iv) younger than T → nothing; (v) `precedence = off` → nothing; (vi) `tool-observed
  remember` after a nudge → exactly one `followed`, a second remember → none. Falsify: delete gate
  step 2 → (ii) red; drop the `RememberSeenPath` append in L1 → (vi)'s second half red.

### L4 — `AskUserQuestion` decision capture  **(Stage 2b, optional; Jim's call)**

Jim's global CLAUDE.md makes `AskUserQuestion` *the* decision-point surface on this machine, so its
answer is a decision by construction — the one place structure identifies a decision rather than a
directive. `PostToolUse` matcher `AskUserQuestion` → capture `"{question} → {chosen label}"` as a
session fact tagged agent `decision-capture` (provenance-marked like the harvester, D62 2b), and
return `additionalContext` in the D51 pattern: *captured verbatim as [fNN]; if it would not stand on
its own, `engram_remember` a self-contained version with `supersedes`.* Opens the store (rare:
per question answered). **Blocked on N4:** the payload's `tool_response` shape for this tool is
unverified — the hooks guide lists `tool_input` and the repo's reference does not mention
`tool_response` at all. Not in Stage 1 or 2; listed because it is the only zero-judgement decision
signal available and it captures the *user's* half of decisions, which the brief also names.

### L5 — Stop-hook nudge  **(Stage 3, evidence-gated)**

The one lever that fires when the user never types again (single-prompt autonomous runs, sessions
closed after the last answer). Design if it is ever built: same gate as L3 (age ≥ T, no remember
seen, not yet nudged, `stop_hook_active == false`), `decision: "block"` with the L3 text as
`reason`; the model gets one more turn; `stop_hook_active: true` on the next Stop ends it. **Cost is
a whole model turn per firing** and the UI renders it under a "Stop hook error:" label (Engram
recall f8062) — visible to the user every time. Coexists with claudetools' existing Stop hooks
(`stop-peer-nudge.mjs`, `stop-orchestrator-liveness.mjs`), all of which run; two blocks in one Stop
are two reasons, harmless.

**Gate to build it:** L1 data shows a material share of zero-remember sessions that (a) ended
without compaction *and* (b) never received an L3 nudge because no further prompt arrived. If L3
covers the losses, L5 is never built. The design doc's rejection of "Stop as a nudge" was of an
*unconditional* nudge for `digest`; a conditional, once-per-session one is a different object, but
it is still a turn per firing, which is why it waits for a number.

### L6 — `MessageDisplay` / `PostToolBatch` probe  **(not a lever yet)**

`MessageDisplay` fires with the assistant's message and `PostToolBatch` fires before the next model
call (hooks guide lines 493–495); neither has a documented stdin schema or a documented
`additionalContext` path. If one of them accepts `additionalContext`, it is a zero-turn-cost,
decision-fresh channel that would beat both L3 and L5. Probe, never assume (design doc: *probe a
channel, never read it*). N5 states the probe. Not ranked until answered.

### Rejected here (in addition to everything `session-capture-design.md` rejects)

- **A second `[Description]` rewrite.** Already carries the trigger; unmeasurable; D17 budget.
- **A per-prompt reminder line.** Nag (D37); and the user-prompt hook already emits context on
  captures — stacking a standing line on every prompt is the CLAUDE.md line again, one channel over.
- **Turn-counting for the L3 trigger.** Needs a per-prompt file append and a count; elapsed time
  needs one stamp per session. Same signal, a tenth of the state.
- **Transcript-reading triggers** ("decision-shaped language in the last assistant message").
  Private format, precision-bound, and a false positive costs a nudge the user sees. The clock is
  enough to find the population; the model does the classifying.
- **`git commit` PostToolUse nudge.** Commit messages here already hold the *why* by rule; a nudge
  there duplicates git log into memory, which the digest instruction's own *"a reader could get from
  the code itself"* clause forbids. L3's clock covers the session anyway.
- **SessionEnd harvester.** No summary exists at SessionEnd, 1.5 s budget, no model turn. A
  `session-end` telemetry kind was considered for measurement and dropped: L1 + `post-compact`
  already yield the "ended un-compacted with zero remembers" split without it.
- **PreCompact flush of "pending decisions".** Nothing is pending — the model holds no queue. The
  90 s PreCompact budget the reference notes is for work the *hook* can do, and the hook cannot
  decide what was decided.

## 5. What must not change

- `engram_remember`'s `[Description]` and the golden file (`docs/mcp-tool-descriptions.golden.txt:65-68`).
- `RunUserPrompt`'s capture path: classifier → `IsGenuinelyTyped` → store → nudge. L3 is appended
  after it, never interleaved; the `user-prompt` telemetry kind keeps meaning "a fact was written".
- The `file-touched` PostToolUse entry and its budget. L1 is a *second* matcher entry; do not widen
  the existing one.
- `SessionNudgeState` line semantics (exact-match keys). New files, not a new key scheme.
- D62's emitter/harvester and the `pre-compact`/`post-compact` kinds — untouched.
- `TelemetryRecord`: one new subject field `tool`; no count fields (D55/D56).
- No hook on any path above opens the database except `RunUserPrompt`, which already does.
- Falsify against a committed tree; assert the patch landed (D60).

## 6. Recommendation, cheapest first

**Stage 1 — L1 (observer) alone. Ship first, measure for ~2 weeks.** It changes no behaviour, costs
one process per Engram tool call, and answers the question nobody can answer today: *which sessions
never wrote anything, and did they compact.* It also retires D43's "not computable" for every
Engram tool, which D18's gate has been waiting on. Amend `TelemetryProbeReport`'s D43 text and add a
D-number recording that hook-space attribution now exists.

**Stage 2 — L2 + L3 (conditional nudge) with `nudged`/`followed`.** Build when Stage 1 shows
zero-remember sessions are a material share of sessions ≥ T minutes old (Jim to read the number;
the spec's prior is that they are). Ship with T = 20 min; tune only against `followed` and Jim's own
reports, never against a guess.

**Stage 2b — L4** if Jim wants the user's half of decisions captured and N4 confirms the payload.

**Stage 3 — L5** only on Stage 2 evidence showing losses concentrated in sessions that never
received a prompt after T. **L6** if N5's probe comes back positive, in which case it displaces L5
and possibly L3.

## 7. NEEDS-EVIDENCE

- **N1 (Jim, decisive).** Three concrete lost decisions: session (or date + project) and what was
  decided. For each, from telemetry + DB (read-only): did that hook session `pre-compact` after the
  decision? Is the decision in `/sessions/<id>/compaction-digest/…`? Was it ever `remember`ed?
  Outcomes: never compacted + no remember → (a)/(b1), L3 is the fix; compacted, absent from digest →
  (b2), tune the instruction against the *observed* omission (design doc's ranked candidates);
  harvested but not recalled → read-side, out of scope here, hand to the code-nav/recall work.
- **N2 (after L1, 2 weeks).** Share of hook sessions ≥ 20 min old with zero `tool-observed
  remember|revise`, split by presence of `post-compact`. Decides whether Stage 2 ships and the T
  threshold. Also the raw D18 number.
- **N3 (after L3, 2 weeks).** `followed / nudged`, and the same zero-remember share as N2. Decides
  Stage 3. Also: sessions nudged whose *only* remember was the followed one — that is the pure
  effect size.
- **N4 (cheap probe).** A logging `PostToolUse` hook on `AskUserQuestion`: does stdin carry
  `tool_response` with the chosen option? Yes → L4 is buildable as specified; no → L4 is dead.
- **N5 (cheap probe).** Logging hooks on `MessageDisplay` and `PostToolBatch`: record stdin, then
  return a `hookSpecificOutput.additionalContext` marker and check whether the next model turn can
  see it (the PreCompact probe's method, sentinel string, log-before-write). Positive → L6 displaces
  L5. Negative → L5 stays the Stage-3 candidate.
- **N6 (assumption stated).** `PostToolUse` matchers match `mcp__plugin_engram_engram__engram_remember`
  by that exact spelling in a plugin-installed MCP server. Doc-sourced (hooks guide "Match MCP
  tools"); the spelling is the one `ClaudePermissions.cs` already uses. L1's tier-3 test cannot
  verify Claude Code's matcher; a one-session live check of `tool-observed` appearing after a real
  `engram_remember` is the acceptance for Stage 1.

## 8. Decisions made here, and the user's calls

- Made: Stage 1 is measurement, not a nudge; nudge rides `UserPromptSubmit` not `Stop`; trigger is
  elapsed time not turn count; T = 20 min; `remember|revise` count as writes, `recall`/`judge` do
  not; observer covers all Engram tools, not just `remember`.
- **Jim's calls:** T; whether L4 (capturing his own `AskUserQuestion` answers) is wanted at all —
  it widens automatic ingestion to a new source, the same class of decision as D62's scope/privacy
  answer; whether a visible Stop-hook turn is ever acceptable (L5).
- Confidence: high on the diagnosis shape and on L1; medium on L3's effect size (that is what N3
  measures); low on L6 until probed. No escalation needed — nothing here is hard to reverse.
