# Code-navigation adoption spec

**Status:** design only. Nothing here has been implemented or executed. Several claims are
NEEDS-EVIDENCE (§6) and the recommendation in §5 is staged so that the unmeasured levers sit
*below* the measurement lever.

**Goal.** Get agents to reach for `engram_navigate` / `engram_index_repo` ahead of Grep/Glob/Read
more reliably than today's once-per-session PreToolUse nudge.

**Non-goal.** Improving the code graph's *coverage* (what `navigate` can answer). That is
`docs/specs/close-graph-query-gap.md`. This spec assumes coverage is what it is and asks only how
often the model reaches for it. §6 N4 exists because the two questions can be confused by a number.

---

## 1. What exists today (do not re-propose)

| Channel | Where | Behaviour |
|---|---|---|
| Tool description | `src/Engram.Cli/EngramMcpTools.cs:724-733`, `engram_navigate`'s `[Description]` | Names the seven relations and says "Use it instead of Read/Grep to answer 'where is Z defined'…". Compile-time constant; reaches every session and every subagent; survives compaction. |
| PreToolUse nudge | `src/Engram.Cli/HookCommand.cs:765-833` (`RunLookupNudge`), dispatched from the verb switch at `:41-55` | Intercepts `Grep`/`Glob`/`Bash`, extracts a query, and if it is symbol-shaped **denies the tool call once per session** with a message pointing at `engram_navigate` / `engram_index_repo`. Re-running the identical call proceeds. |
| Classifier | `src/Engram.Core/SymbolQueryDetector.cs:24-183` | `LooksLikeSymbol` + `ExtractSearchPattern`. Deliberately fails toward silence. |
| One-shot state | `src/Engram.Core/SessionNudgeState.cs:16-53` | One `session_id` per line at `home.LookupNudgeStatePath`. Separate file from memory-guard's so the two nudges never spend each other's shot. |
| Telemetry | `TelemetryEventKind.LookupNudge = "lookup-nudge"` (`src/Engram.Core/Telemetry.cs`), appended in `RunLookupNudge` with `SessionId` + `Query` | One record per nudge. Deliberately its own kind so it cannot inflate the D18/D43 memory-adoption counts. |
| Telemetry | `TelemetryEventKind.Navigate = "navigate"`, appended in `Navigate` (`EngramMcpTools.cs:763-770`) with `SessionId` (MCP), `Relation`, `Found`, `Tiers`, `ExtractionTiers` | One record per `engram_navigate` call. **No query text, no repo, no cwd.** |
| Global instruction | `~/.claude/CLAUDE.md`, "Code lookups" | User-authored, this machine only. Not a shippable channel. |
| Verification spec | `docs/specs/lookup-nudge-verification.md` | The existing acceptance spec for this hook, including the standing "filter by kind, never assert a total line count" rule. Any change here amends that spec too. |
| Tests | `tests/Engram.Integration.Tests/LookupNudgeHookTests.cs`; two comments in `EngramNavigateTests.cs` (`:324`, `:408`) that already reason about the nudge steering lookups | The consumer set L6 must sweep. |

**Registration** is `plugin/hooks/hooks.json`, PreToolUse entry two, matcher `Grep|Glob|Bash`. That
file's `description` already records the design intent verbatim, including *"False positives are the
expensive failure… every rule in that classifier is a reason to stay silent and a lowercase word
never fires."* This spec does not contradict that; §3 L3 upholds it.

**D71 already pre-registered the decision rule this spec serves:** *if `navigate` telemetry after a
reasonable period shows the model does not reach for the surface* — the D6-override justification
rests on that surface being reached, which is why it was instrumented from Phase 1. This spec is the
response to that condition being met, and §3 L6 is what makes "reaches for it" a number rather than
an impression.

---

## 2. Question 1 — where the current nudge fails

Six findings. F1, F2 and F3 are the load-bearing ones.

### F1. The one shot is spent before Engram knows whether it can answer.

`RunLookupNudge` writes the session into `SessionNudgeState` and denies **without any check that
this checkout is enrolled or indexed**. Its deny text asserts a fact it has not established:

> "…and Engram indexes this repo's code graph."

On an unenrolled or never-scanned checkout that sentence is false. The model complies, calls
`engram_navigate`, receives a not-indexed answer, and — because the shot is now spent — is never
nudged again for the rest of the session, including on the later lookups the graph *would* have
answered. **The single worst outcome of the current design is a nudge that lands on an unindexed
repo**: it costs a turn, teaches the model the tool is empty, and disarms itself in the same call.
This is also a D37 violation in the narrow sense — a diagnostic making a claim that is not true of
the state the user is in is one people learn to route around.

### F2. Once per session, unconditionally, is the wrong grain for a long session.

`SessionNudgeState.Contains` keys on `session_id` alone. A reminder delivered at the second minute
of a three-hour session cannot govern the ninetieth, and it does not: every symbol-shaped search
after the first is completely unguarded. The one-shot rule is correct as an *anti-nag* rule
(D37) — it is wrong as a *coverage* rule, and today it is doing both jobs with one number.

### F3. `Read` is not intercepted, and should not be.

The hooks matcher is `Grep|Glob|Bash`. "Open the file and look for it" — the most common substitute
for `navigate` once a model has a path — passes untouched. This gap is **not closable by the
nudge**: a `Read` payload carries a path and no query, so nothing in the payload distinguishes
"read to locate a symbol" from "read the file I already located", and a detector that guessed would
deny ordinary reads. F3 is therefore an argument *for* the description channel (§3 L4) and against
widening the matcher. Do not widen it.

### F4. `LooksLikeSymbol` under-fires on whole naming conventions.

`IsSymbolShaped` requires `HasCaseTransition` (a lower→upper transition) **or** an underscore, on
the last qualified part. Consequences, read directly off the source:

- `Telemetry`, `Facade`, `Engram` — capitalized single words — have no lower→upper transition and
  are **rejected**. Most C# type names spelled as one English word never fire.
- `serve`, `main`, `parse` — lowercase single words — rejected.
- Any pattern containing a space, `/`, `(`, `*`, quotes or a regex metacharacter is rejected by
  `SearchSyntax` first. `rg "def handle_request"`, `rg 'class Foo'`, `grep -n "Foo("` — the ordinary
  spellings of a symbol search — all reject. `rg handle_request` fires.
- `ExtractSearchPattern` skips flags by leading dash, so a separate-value flag donates its value
  instead of the pattern; the comment already notes this fails toward silence.

The bias toward silence is deliberate and correct for a *deny*. The point here is only that F2 and
F4 compound: a narrow detector firing at most once is a very small aperture, and **which of the two
is the binding constraint is not knowable by reading** — see N2.

### F5. A subagent probably inherits the parent's spent shot.

`SessionNudgeState` is keyed on the payload's `session_id`. If a subagent's PreToolUse payload
carries the parent's `session_id`, then a parent that burned the shot leaves every `Explore` /
`Task` subagent unguarded — and bulk grepping is exactly what subagents are dispatched to do.
Unverified: **N1**.

### F5b. A nudge can steer the model at a *stale* graph, not just an empty one.

D72: *"The graph rots in precisely the situation navigation is most wanted, a refactor."*
`src/Engram.Core/FileFreshness.cs:17` already records that this gap "matters more since
`lookup-nudge` began steering symbol lookups to" the graph. So F1's failure mode has a second,
quieter form: the repo *is* enrolled and indexed, the nudge is honoured, and `navigate` returns a
confidently wrong answer from before the rename. Under a first-reach mandate this is the worse
outcome — a partial or stale answer stops the search, where an empty one sends the model to Grep
(the principle already adopted in `docs/specs/close-graph-query-gap.md`).

Consequence for L1: the stamp it reads should carry *when this checkout was last indexed*, not only
*whether*. Deciding what staleness threshold silences the nudge is out of scope here and belongs
with D72's freshness work; what this spec fixes is that the gate must not be a bare boolean, or the
threshold has nowhere to live later.

### F6. Nothing observes whether the nudge changed behaviour.

The nudge is a *deny*, so compliance has an unusually crisp definition: **the model complied iff it
did not re-run the denied call**. Today the re-run is invisible — `Contains` returns true and the
hook exits 0 silently — so `lookup-nudge` records count nudges *delivered* and say nothing about
nudges *honoured*.

The obvious repair — compare `lookup-nudge` counts against `navigate` counts — is **forbidden by
D43**. `lookup-nudge` carries Claude Code's hook `session_id`; `navigate` carries the transport's
`Mcp-Session-Id`. Those are disjoint id spaces with no value in both, exactly the pair D43 recorded
after a subtraction across them produced a confident wrong sentence. Do not ratio them, do not
subtract them, and do not attribute a `navigate` call to the session that was nudged. §3 L6 gets
the compliance signal from inside the hook's own id space instead.

---

## 3. Question 2 & 3 — candidate levers

Each lever is scored against D51's split (invariant mechanism → `[Description]`; session-conditional
trigger → primer), D37's no-nag rule, and D55/D56's event-log rules.

---

### L1 — Gate the nudge on this checkout being answerable

**Change.** Before spending the shot, `RunLookupNudge` reads a *file* stamp under `EngramHome`
listing checkouts that are enrolled and have been indexed at least once. If the cwd's checkout root
is absent, the navigate nudge does not fire and the shot is not spent.

**Why this one first.** It converts F1 from the worst case into a non-case, and it does so by
*removing* nudges rather than adding any. It also makes every other lever safe to tune: raising the
nudge rate (L2) is only defensible once every nudge lands somewhere that can answer.

**Frequency class.** This hook is on `Grep|Glob|Bash` — the same class as `file-touched` and
`memory-guard`, and D4/D66's rule applies: **it must not open the database**. `repo_enrollment` is a
DB table, so the gate must read a plain file (one `File.ReadLines`, the shape `SessionNudgeState`
already uses on this path) written by `RepoCommand.ApplyDecision` and by the indexer. Whether such a
stamp already exists is **N3**; if it does, reuse it — do not add a second store for the same fact.

**Three states, three behaviours** (the distinction is load-bearing):

| Enrollment state | Behaviour |
|---|---|
| enrolled **and** indexed at least once | navigate nudge as today. Stamp carries the last-index time, not a bare flag, so F5b's threshold has somewhere to live. |
| enrolled, **not yet indexed** | silent; shot **unspent**. *(Row added after Stage 1; confirms the Implementor's reading.)* A nudge here sends the model to a graph that cannot answer yet — F1 exactly — and an enrollment nudge would re-ask a question already answered. Leaving the shot unspent is the point: when the first index lands mid-session the very next symbol-shaped search may nudge. Stage 1 has this row and the two below behave identically; they differ only once L7 exists. |
| no decision recorded yet | Stage 1: silent, shot unspent. Stage 2 (L7): **one** enrollment-flavoured nudge per session, in place of the navigate nudge |
| `decline`, or `later` still inside its cooldown | silent, forever. The shot is not spent. |

Never nudge a declined repo. A decline is a recorded answer, and re-asking is the D37 failure that
makes people stop reading Engram's output at all.

**Measurable?** Yes, and it needs no new kind: the effect is a *fall* in nudges on unindexed repos,
visible as `lookup-nudge` records disappearing for cwds that were never enrolled. To see it at all
the record must say which repo it fired for — see the field note in L6.

**Cost.** One file read on a path that already does one. No DB open. No new tool surface.

**Deny text — ruling (added after Stage 1).** `LookupNudgeDenyReason` still says *"If the repo has
not been indexed yet, engram_index_repo builds the graph."* With the gate in place the deny fires
only on an indexed checkout, so that sentence is now false-flavoured noise the model pays for on
every nudge. **Cut it.** Do not keep it as a seed for L7: the enrollment nudge is a different
message on a different branch under a different telemetry kind, and dead prose held for a future
stage is the CLAUDE.md "retired key that reads exactly like a live setting" failure in a string.
The recovery rule for an *empty* answer already lives where it belongs — L4's `[Description]`. The
remaining escape-hatch sentence (*"If Engram has nothing, or you are searching text rather than
resolving a symbol, re-run the exact same call…"*) stays verbatim: it is the contract L6's
`overridden` detection counts on.

---

### L2 — Re-key the one-shot: per distinct symbol, hard-capped per session

**Change.** `SessionNudgeState` lines become `session_id \t hash(query)`; the hook nudges once per
*distinct* symbol, and never more than **N** times in one session. Proposed N = 3.

**Why not unlimited.** Every nudge is a `deny`, which costs a full model turn. Unbounded nudging is
both a nag (D37) and a real latency and token cost, and the cost is paid by the user whether or not
the model complies. N is the anti-nag budget that the current design spells as N = 1.

**Why N is not a config key.** D55: two ways to switch one thing off is how a setting disagrees with
itself. `[memory] precedence = off` already silences this hook entirely and is the only switch. N is
a policy constant.

**Why this is staged below L6.** N = 3 is a *preference* until the override rate is known. If most
first nudges are already being overridden, more nudges buy nothing and cost three turns instead of
one. **Do not land L2 before L6 has produced a number.** This is the measured-not-preference bar.

**Measurable?** Yes — directly, as the change in nudges-per-session and in the L6 override rate.

---

### L3 — Widen `SymbolQueryDetector`

**Change.** Accept a single capitalized word of length ≥ 4; and/or extract a bare identifier out of
a pattern that also contains a keyword (`class Foo`, `def handle_request`).

**Assessment: reject for now.** The detector's asymmetry is deliberate and correct — a false
positive here is a *deny*, which costs a turn and trains the model to distrust the nudge, while a
false negative costs nothing beyond a grep that would have happened anyway. Accepting single
capitalized words newly fires on `Error`, `Warning`, `TODO`-adjacent prose and every capitalized
English word in a log-line search. Widening is only justified if **N5** shows that the detector,
rather than the one-shot, is what is actually holding the fire rate down; and even then the widening
should be the narrow one (extract the identifier out of `class Foo` / `def foo`), not the broad one
(bare capitalized words).

---

### L4 — The `[Description]` channel (D51's unconditional half)

**Change.** Amend `engram_navigate`'s `[Description]` (`EngramMcpTools.cs:725-733`) to add two
things it does not currently carry:

1. **A trigger, in the model's own idiom, not a list of question forms.** D51's finding was that a
   rule with no trigger loses to a rule that has one, regardless of which is more correct.
   `engram_remember`'s description opens on durability and names the trigger; `engram_navigate`'s
   opens on a capability list and names Read/Grep only in the middle of a sentence about question
   forms. The trigger to name is the act, not the question: *looking for code by name.*
2. **The recovery rule.** What to do when the answer is empty or the repo is not indexed —
   `engram_index_repo`, then Grep. Without this, the first empty answer reads as "this tool does not
   work" and there is nothing in the description to contradict it.

**Why the description and not the primer.** D51's criterion is exactly met: both of these are
invariant mechanism, true of every install, so they belong in a compile-time constant that reaches
every session and every subagent and survives compaction. The primer decays and is silently dropped
under `PrimerBuilder.MaxTokens`.

**Why it is a supplement and not the fix.** A model that has already reached for Grep is not reading
`engram_navigate`'s description. That asymmetry is precisely why the hook exists, and it is why L4
cannot be the whole answer even though it is the cheapest.

**Constraint.** `docs/mcp-tool-descriptions.golden.txt` covers the seven tools and must be updated
in the same change; the description is also budget the model pays on every session, so the edit must
be a rewrite at roughly constant length, not an append.

**Measurable?** **No — not in isolation, and this must be stated plainly.** No telemetry
distinguishes a `navigate` call caused by the description from one caused by the hook, and D43
forbids inferring it by joining the two id spaces. L4 is justified by D51's argument, not by a
number this design can produce. That is an acceptable basis for a description edit (it is nearly
free and cannot regress anything) and would **not** be an acceptable basis for new machinery.

---

### L5 — A primer line

**Change considered.** A session-start primer line telling the agent to prefer the code graph.

**Assessment: reject the guidance form; accept a narrow capability form.**

- **Guidance form — reject.** D15 forbids tool names in primer guidance, with exactly one exemption
  subtracted by exact string precisely so every other drift still fails. Adding a second exemption
  weakens the guard in the one way it was designed to resist. And the primer is the decaying channel:
  a rule that must reach subagents and survive compaction belongs in L4.
- **Capability form — accept, cheaply.** State a *fact*, not an instruction: this checkout's code
  graph is indexed, N symbols over M files (or: not indexed). This names no tool, so it needs no D15
  exemption; it is genuinely session-state-conditional, which is D51's own criterion for the primer
  half; and it is the same shape the primer already uses for `long_term_fact_count`. Ordering
  constraint: it goes **after** the precedence line, which D51 fixes as first because `TryAppendLine`
  drops silently and precedence is the only line whose absence changes behaviour.

**Measurable?** Only weakly, and only alongside L1 (both move the same population). Rank it below L6
for that reason.

---

### L6 — Measure compliance, inside one id space

**Change.** Give the `lookup-nudge` record a `Phase`, following D55's precedent exactly
(`index` and `embedding` already carry `started` / `finished` / `failed` because work with a
duration must report both ends):

- `phase: nudged` — written where the record is written today, at the deny.
- `phase: overridden` — written when the *same session* re-issues the *same query* after being
  nudged, i.e. in the branch that currently returns 0 silently at `Contains`.

Compliance rate = `1 − overridden / nudged`, computed entirely from hook-space records. No join, no
subtraction across id spaces, no correspondence assumed between `lookup-nudge` and `navigate`.

**Also add to the record: which checkout it fired for** (repo identity or slug, not a raw path).
Without it, L1's effect is invisible and a nudge cannot be told from a nudge on a repo that could
never have answered. Do **not** add any count field: D43 traced a wrong conclusion to a nearby number
in a field that meant something else, and D55 keeps counts out of these records for the same reason.

**D55/D56 collision check:**

- *New kind or existing kind?* Existing. `lookup-nudge` already means "a symbol search met the
  nudge"; a phase distinguishes the two ends of that one event. A separate kind would be the D56
  mistake in reverse — splitting one event class across two counters.
- *Does it inflate a gated count?* No. D18/D43 gate on `remember` / `recall` / `session-open`.
  `lookup-nudge` was made its own kind for exactly this reason and stays outside them. The
  hook-driven capture rule (D56) — a capture folded into `remember` inflates the number a gate turns
  on — is honoured because nothing here touches `remember`.
- *Does it break a consumer?* **Yes, and this is the part to write down.** Any reader counting
  `lookup-nudge` lines now double-counts a nudge that was overridden. D55/D56 recorded end-to-end
  tests breaking on exactly this shape twice — the MCP test asserting five lines, and the four
  session-start tests broken by the detached maintenance child. Known consumers to sweep:
  `tests/Engram.Integration.Tests/LookupNudgeHookTests.cs`, item 8 of
  `docs/specs/lookup-nudge-verification.md` (which already states the filter-by-kind rule and must be
  amended to filter by phase as well), `engram activity --json`'s per-kind counts, the
  claude-tui-line statusline segment that parses `telemetry.jsonl`, and any `[webhook] kinds` entry
  naming `lookup-nudge`. Enumerate and confirm before landing.
- *Frequency.* At most two telemetry appends per session on this hook (one nudge, one override), not
  one per intercepted call. That must be an explicit invariant of the implementation: the record is
  written once at the deny and once at the *first* matching re-run, never on every subsequent
  `Grep`. This hook shares `file-touched`'s frequency class and a per-call shared append is the
  thing that class forbids.
- *Webhook.* `WebhookService` tails the log verbatim, so a phase field appears in subscribers'
  feeds with no envelope change (D55). Nothing to do.

**This is the lever that unblocks the others.** L2's N, L3's widening and L5's ranking are all
preferences until this number exists.

#### L6 amendment — how `overridden` is detected (added after Stage 1 shipped `nudged`)

*Why this is here:* the Implementor built L4, L1 and the `nudged` phase and stopped, correctly, on
`overridden`: nothing persisted the nudged query, `lookup-nudge.state` holds `session_id` only, and
its key scheme is out of scope. Three options were offered; the ruling is (b)-shaped, and (c) is
rejected on measurement grounds, not taste.

**Rejected: (c) "first symbol-shaped search after the nudge counts as overridden".** A model that
complied — called `engram_navigate`, got its answer — and twenty minutes later greps a *different*
symbol would be recorded as overriding a nudge it honoured. Nearly every session eventually does
another symbol search, so `overridden` → ~100% and the ratio reads as total failure regardless of
behaviour. That is a metric wrong in a fixed direction, which is the D43 shape. "Same query" is
what makes the deny's own escape hatch — *re-run the exact same call* — the thing being counted.

**Rejected: (a) changing `lookup-nudge.state`'s line to `session_id\tquery`.** It is the key-scheme
change the brief excludes, and `SessionNudgeState` is shared with memory-guard.

**Decision: one new file, no new type, `SessionNudgeState` reused verbatim.**

- `EngramHome` gains `LookupNudgeOutcomePath` = `<home>/lookup-nudge-outcome.state`. Append-only,
  never compacted, same remarks as `SessionNudgeState` (bounded by nudges, which are bounded by
  sessions).
- Lines are exact-match keys, which is all `SessionNudgeState.Contains`/`TryAppend` do — so the
  existing type serves unchanged, the "key" simply being a composite string:
  - at the deny: `{sessionId}\t{query}`
  - at the first matching re-run: `{sessionId}\toverridden\t{query}`
- The tab is safe as a separator **because `LooksLikeSymbol` already guarantees it**: `SearchSyntax`
  rejects `' '` and `'\t'`, and `IsIdentifier` admits only letters, digits and `_`, so a query that
  reached this point cannot contain a tab or a newline. No escaping, no parsing beyond
  `string.Equals`. Do not add a parser.

**Control flow in `RunLookupNudge`** (`src/Engram.Cli/HookCommand.cs`, currently ~`:765-840`).
Everything above the `SessionNudgeState.Contains(home.LookupNudgeStatePath, sessionId)` check is
unchanged — in particular the L1 gate stays *before* it, so an unindexed checkout still records
nothing at all (the Reviewer's ordering point stands).

1. `if (SessionNudgeState.Contains(home.LookupNudgeStatePath, sessionId))` — session already
   nudged. Today: `return 0`. New:
   1. `if (!SessionNudgeState.Contains(home.LookupNudgeOutcomePath, $"{sessionId}\t{query}")) return 0;`
      — not the nudged query (or the query line never landed): allow silently. This is the common
      post-nudge path and costs one small-file read, on a branch that already paid one.
   2. `var marker = $"{sessionId}\toverridden\t{query}";`
      `if (SessionNudgeState.Contains(home.LookupNudgeOutcomePath, marker)) return 0;` — already
      counted: allow silently. This is what holds the ≤2-appends-per-session invariant.
   3. `if (!SessionNudgeState.TryAppend(home.LookupNudgeOutcomePath, marker)) return 0;` — marker
      **before** telemetry, same reason the nudge writes state before the deny: a crash between the
      two must not produce two `overridden` records.
   4. Append telemetry, best-effort in the same `try { } catch { }` shape as the nudge:
      `Kind: TelemetryEventKind.LookupNudge, Phase: LookupNudgePhaseOverridden, Query: query,
      Repo: stamp.Identity, SessionId: sessionId`. Add
      `internal const string LookupNudgePhaseOverridden = "overridden";` beside `…PhaseNudged`.
   5. `return 0` — the call proceeds. **No output on stdout.** An override is observed, never
      commented on; a second message here would be the nag D37 forbids.
2. The deny path (session not yet nudged) gains one line, placed **after** the `TryAppend` to
   `LookupNudgeStatePath` succeeds and **before** the telemetry append:
   `SessionNudgeState.TryAppend(home.LookupNudgeOutcomePath, $"{sessionId}\t{query}");` — result
   deliberately ignored. If it fails, the override can never be detected for this session and the
   deny still fires: the signal is lost, the behaviour is not. Fails open, like everything on this
   hook.

**What "same query" means, precisely.** Ordinal equality on the string `LooksLikeSymbol` accepted —
`toolInput.Pattern` for Grep/Glob, `ExtractSearchPattern(command)` for Bash. Flags, paths and the
tool used do not matter (`rg Foo src/` after a denied `Grep Foo` is a match; `grep -rn Foo` too).
A *rephrased* override (`FooBar` → `FooBar\(`) is not matched and reads as compliance. That bias is
known and is in the safe direction: it under-counts overrides, so a compliance rate this design
reports is an upper bound, never an inflated one. Do not widen the match to fix it; widening is how
(c) happens gradually.

**Not a time window.** A re-run of the nudged symbol an hour later, after a `navigate` that did
answer, still counts as `overridden`. It is the same session searching text for the symbol it was
told the graph resolves, which is the behaviour being measured; and a hook cannot see sequence
without more state than this stage buys.

**Invariants, restated for the Reviewer.**
- ≤ 2 telemetry appends per session on this hook: `nudged` guarded by `lookup-nudge.state`,
  `overridden` guarded by the marker line. Never per intercepted call.
- Both new appends target a file no other hook writes. No DB, no config beyond what the hook already
  reads.
- `Telemetry.cs`'s `Phase` doc — *"whose other end is the same session re-issuing the same query"* —
  is exactly what this implements. Extend it to name `overridden` as that other end; no rewording.
- `docs/specs/lookup-nudge-verification.md` gains: (i) a re-run of the denied query proceeds and
  writes exactly one `overridden` record; (ii) a second re-run writes none; (iii) a *different*
  symbol-shaped query after the nudge writes nothing and proceeds; (iv) the `overridden` record's
  `repo` equals the `nudged` record's. Filter by kind **and** phase; never a total.
- Falsification to prove: delete step 1.2 (the marker check) and (ii) must go red; replace
  `$"{sessionId}\t{query}"` with `sessionId` in step 1.1 and (iii) must go red.

**Consumers.** `engram activity --json` groups by kind only and already counts 2–3 lines per
`index`/`embedding`/`sync` run under that rule, so `lookup-nudge` now behaves the same way; that is
consistent, and making it phase-aware is separate work — not in this stage. The double-count begins
the moment `overridden` lands, so the note in `Telemetry.cs` is the load-bearing documentation.

**Forward compatibility note, moot but free.** The marker carries the query so that a per-symbol
scheme could reuse this file unchanged. **L2 is dropped, not deferred**: Jim ruled the nudge stays
at one per session. Nothing here should be read as preparing for L2.

---

### L7 — Enrollment prompting at the moment of demonstrated need

**Change.** The "no decision recorded" branch of L1. A symbol-shaped search on an unenrolled
checkout is the highest-signal moment there is to offer enrollment: the model has just demonstrated
it needs the graph. One such nudge per session, replacing the navigate nudge, naming
`engram_index_repo` and stating that declining is a valid answer (the wording `engram_index_repo`'s
own `[Description]` already uses, and for the same reason — a model that reads it as enroll-only
never records a "no", and then the prompt returns every session).

**Why not more prompting elsewhere.** The existing conditional primer line already offers
enrollment at session start. Adding a second unconditional prompt would be the nag; this is a
*relocation* of the shot L1 stops wasting, not a new interruption.

**Measurable?** Yes, and the kind already exists: `TelemetryEventKind.Enrollment`. Do not fold this
prompt into `lookup-nudge` — a nudge that offers enrollment and a nudge that offers navigation
answer different questions, and folding them makes the L6 compliance ratio uninterpretable.

---

## 4. What must not change

- The hook must not open the database, on any path, including L1's gate (D4, D66).
- `[memory] precedence = off` remains the only switch. No second key (D55).
- A recorded `decline` is never re-asked (D37).
- No cross-space arithmetic between `lookup-nudge` and `navigate` (D43).
- No count field on a `lookup-nudge` record (D43, D55).
- `SessionNudgeState`'s two state files stay separate, so memory-guard and lookup-nudge never spend
  each other's shot.
- The detector keeps failing toward silence: a false-positive deny is dearer than a missed nudge.
- `plugin/hooks/hooks.json`'s `description` field states the current behaviour ("once-per-session
  checkpoint", "re-run the identical call and it proceeds") in prose. It is the shipped
  documentation of this hook and must be amended in the same change as any behaviour change here —
  a description that disagrees with the hook is worse than none.

---

## 5. Question 4 — recommendation, cheapest first

**Stage 1 — free, no new machinery, land together.**

1. **L4**, the `[Description]` rewrite: add the trigger and the recovery rule at constant length;
   update `docs/mcp-tool-descriptions.golden.txt`. Justified by D51, not by a measurement, and
   cannot regress anything.
2. **L1**, gate the nudge on the checkout being answerable, with the three-state table. This is the
   highest-value single change: it removes the failure mode that disarms the nudge in the same call
   that wastes it.
3. **L6**, the `Phase` field plus repo identity on the `lookup-nudge` record, and the consumer sweep.
   Land it *with* stage 1 so stage 1's effect is observable at all.

**Stage 2 — only after §6's evidence is in.**

4. **L7**, the enrollment branch (it is L1's third row; ship it with L1 if the stamp already carries
   the "no decision" state, otherwise immediately after).
5. ~~**L2**, per-symbol keying with cap N~~ — **dropped by user decision after Stage 1**: the nudge
   stays at one per session. §7's open call is closed; N = 1.
6. **L5**, the primer capability line.

**Rejected for now:** **L3**, widening the detector. Revisit only if N5 shows the detector is the
binding constraint.

---

## 6. NEEDS-EVIDENCE

Nothing below was run. Each item names what to run and what each outcome decides. Route to the
Implementor / Task-Runner; **`ENGRAM_HOME` or `--home` must be set on every ad-hoc invocation of the
published binary** (CLAUDE.md's third invariant — a verification command that omits it litters the
real `~/.engram`, which has happened once already).

**N1 — Does a subagent inherit the parent's `session_id` in a PreToolUse payload?**
Run: with a fresh session, trigger one nudge in the parent, then dispatch a subagent whose task is a
symbol-shaped `Grep`. Inspect `telemetry.jsonl` for a second `lookup-nudge` record and compare the
two `session_id` values; equivalently, inspect the payload directly.
Decides: if inherited, F5 is real and the one shot governs a whole agent tree — which raises the
priority of L2 sharply and may argue for keying on `session_id + agent_type`. If not inherited, F5
is void and L2 stays a stage-2 item.

**N2 — How often does the nudge fire today, and on what?**
Run: over `telemetry.jsonl`, count `lookup-nudge` records in the last 30 days; count distinct
`session_id` among them; list the `query` values; and count `session-start` records over the same
window for a nudges-per-session rate.
Decides: a rate near 1.0 means the one-shot (F2) is binding → L2 is the next lever. A rate near 0
means the detector (F4) is binding → L3 moves up and L2 buys nothing. Anything in between means both
and L6's number arbitrates.

**N3 — Does a file-level enrollment/index stamp already exist under `EngramHome`?**
Run: read `src/Engram.Core/RepoEnrollment.cs` and `RepoCommand.ApplyDecision` for any path written
under the home; check whether `IsFullScanDue`/`StampFullScan` persist to a file or to
`repo_enrollment` in the database.
Decides: whether L1 reuses an existing stamp or must add one. If it must add one, the write goes in
`RepoCommand.ApplyDecision` (already the single point below both the CLI verb and the MCP tool) and
in the indexer — never a third place.

**N4 — What fraction of `navigate` calls answer not-found?**
Run: over `telemetry.jsonl`, count `navigate` records by `relation` and by `found`.
Decides: whether adoption is the right problem at all. A high not-found rate means the nudge is
successfully sending the model somewhere that cannot answer, and coverage work
(`docs/specs/close-graph-query-gap.md`) outranks everything in this spec. **Check `valid_from` /
timestamps against index runs before reading a miss as a coverage failure** — D44's cold-start trap
applies verbatim.

**N5 — What is the detector's false-negative rate on real searches?**
Run: sample ~100 recent `Grep`/`Glob` patterns from Claude Code transcripts under
`~/.claude/projects/*/`, classify by hand which were symbol lookups, and run
`SymbolQueryDetector.LooksLikeSymbol` over each.
Decides: L3. Note that N2 can only show queries that *did* fire, so it cannot answer this;
transcripts are the only source for the negatives.

---

## 7. Confidence, and the one call that is not mine

Confidence is **moderate-to-high** on the diagnosis (F1–F4 and F6 are read directly off the source)
and on the staging. No Ultra-Advisor escalation is recommended: nothing here is security-, migration-
or concurrency-shaped, and the destructive surface is nil.

**Deferred to the user (Jim), not decided here:** *how interruptive the nudge is allowed to be.*
L2's N — 1 today, 3 proposed — is a tolerance-for-interruption preference, not a technical fact.
Every nudge is a deny that costs a full model turn whether or not it is honoured, so N trades the
user's latency and tokens against reach. §5 stages L2 behind L6 so the trade can be made against a
measured override rate rather than a guess, but the final N is Jim's call.

**Second, weaker fork, flagged rather than settled:** L5's capability line. D51's own criterion
(session-state-conditional → primer) argues for it; D15's guard and the 300-token budget argue
against anything that looks like guidance. The capability-not-guidance form threads that, but it is
the least certain item here, which is why it sits last in stage 2.
