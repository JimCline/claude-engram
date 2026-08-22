# Standing directives — detailed spec

Status: **ready for the Implementor**, 2026-08-20. Author: architect.
Feature #7 of `docs/memory-expansion-spec.md`. All design forks resolved; no open questions.

**Pre-implementation evidence is in.** E-4 and E-6 are answered — E-4 confirmed the design, E-6
changed it (the promotion surface is cut; see D-10). Nothing now blocks implementation.
E-1/E-2/E-3 gate the merge.

## Goal

A memory class whose members are **delivered in full, unconditionally, at every context
reset** — the two moments `CLAUDE.md` itself is (re-)loaded — rather than being *retrieved*
when a query happens to reach them. A directive is a standing rule the user has authored:
"always X", "never Y". Its value is that the model is holding it, verbatim, at all times,
which is a different property from being findable.

Everything else Engram stores is retrieval-gated: a fact reaches the model only if recall
ranks it, and the primer reports only a *count and two examples* of a corpus. That is correct
for beliefs and wrong for rules. A rule that applies to every turn cannot be delivered by a
mechanism that fires on some turns.

## Non-goals

- **Not a `CLAUDE.md` replacement, mirror, or sync target.** Engram never reads, writes, or
  parses `CLAUDE.md`.
- **Not written by the passive capture heuristic, and not model-authored.** No MCP tool writes a
  directive. See "The collision" and D-2.
- **Not a promotion UI over the existing capture.** No candidate listing, no promote-by-id — cut
  on evidence, see D-10.
- **Not enforcement.** A directive is text delivered to the model. Nothing rejects a tool call
  for violating one. "On par with `CLAUDE.md`" means on par in *delivery*. `memory-guard` (D66)
  is the only hook that denies anything and it is untouched.
- **Not project-scoped.** v1 is `user` scope only — a **deliberate scope decision** (D-8), not an
  unresolved question. Deferred follow-up, not a rejected idea.
- **Not in recall's candidate set** (D-5).
- **Not a change to `engram_browse`'s behaviour on any path** (D-9). Deferred follow-up, not a
  rejected idea.
- **Not a fix for the classifier's precision.** E-6 found a real defect in the existing
  `requires` population; it is out of scope here and recorded under "A finding this feature does
  not fix".

## Inspiration

The comparable tool reviewed for this series had a **durable global pin**: a flag on a stored
memory that permanently forces it to the top of retrieval. `docs/memory-expansion-spec.md`
feature 4 **explicitly declined** to adopt that. This spec must not quietly re-adopt it under
another name:

| | Durable global pin (rejected) | Standing directive (this spec) |
|---|---|---|
| Mechanism | A priority flag on an ordinary fact row | A separate content class with its own delivery channel |
| Effect | Distorts the ranker permanently | Touches the ranker **not at all** |
| Failure mode | Silent, unbounded ranking corruption; D44 coverage inflates | Bounded, refused at write time; ranking unchanged by construction |
| Bound | None | Hard aggregate token cap, enforced at authoring |

The pin makes *retrieval* lie; a directive bypasses retrieval entirely, which is why it can be
honest about its cost.

## The collision: Engram already has a thing called a directive

**Read this before anything else.** `UserStatementClassifier` already defines
`UserFactKind.Directive` — "A standing instruction: 'always', 'never', 'from now on',
'remember that'" — which `HookCommand.cs:118` maps to `UserFactTopic.Instruction` and
`UserFacts.PredicateFor` stores with predicate **`requires`**. The passive `user-prompt` capture
*already* records standing instructions; it just leaves them retrieval-gated.

**The delta this feature adds is therefore narrower than the brief assumed.** It is not "Engram
learns about standing rules." It is "a rule the user authored deliberately is delivered
unconditionally instead of retrieval-gated." Still worth building — a rule that surfaces only
when a query reaches it is not a rule — but a feature described against a false baseline gets
built against one too.

### The two tiers

| | Tier 1 — captured instruction (exists) | Tier 2 — directive (this spec) |
|---|---|---|
| Authored by | Regex over chat text | The user, typing a CLI command |
| Predicate | `requires` | `directs` |
| Delivery | Retrieval-gated | Unconditional, every reset and subagent spawn |
| Retractable by the model | Yes (`engram_forget`) | No — CLI only |

**Auto-promotion is rejected, and E-6 measured how badly it would have gone.** It is the obvious
simplification and it is what Jim ruled out. A regex matching "always" would write into permanent
context; "I always end up debugging this" is a sentence about frustration, and a false positive
costs a line in *every* primer of *every* session and subagent spawn until someone notices. The
measurement: of **152** live `requires` facts on a real instance, roughly **5–15** read as
genuine standing rules. Auto-promotion would have put ~140 defect reports and code-review
comments into unconditional context. The classifier's precision is adequate for a
retrieval-gated store and not for a channel with no retrieval gate.

**The tiers are connected by nothing, and that is deliberate** (D-10). There is no candidate
listing and no promote-by-id verb. A user who wants a rule as a directive types it:
`engram directive add "always run the tests before committing"`.

## Delta over CLAUDE.md

1. Authored in one command, mid-session, without editing a file.
2. Retirable and revisable with history — `valid_to` / `superseded_by`, never a rewrite (D8).
3. Carried by sync (feature 1) to the user's other machines; `CLAUDE.md` is per-checkout.
4. Reaches Engram's subagent primer, a channel `CLAUDE.md` does not control and where
   `SessionStart` never fires.
5. Measured and bounded. A file grows silently; this refuses the write that would overrun.

## Engram design

### Storage — schema delta: **none**

`SchemaVersion` stays **11**. No migration, no new table, column, or index. New
`src/Engram.Core/DirectiveFacts.cs`, a structural sibling of `SessionFacts`:

```csharp
public static class DirectiveFacts
{
    public const string Root       = "/directives";
    public const string Predicate  = "directs";
    public const string Scope      = "user";
    public const string LearnedVia = "stated";

    // Slug for readability, fingerprint for identity. See D-7.
    public static string PathFor(string statement) =>
        Root + "/" + CannedFactSeeder.Slug(statement) + "-" + FactStore.Fingerprint(statement)[..8];
}
```

- **One entity per directive is required, not stylistic.** `ux_fact_live ON fact(subject_id,
  predicate) WHERE valid_to IS NULL` permits exactly one live fact per (subject, predicate), so N
  live directives sharing a subject is not representable. This is also why "put them all on the
  `/directives` node so browse finds them" does not work — see "Answering the question".
- **`Predicate = "directs"`, deliberately not `requires`.** `requires` is tier 1's and must stay
  unambiguously tier 1 — a shared predicate makes the tiers indistinguishable in the store, the
  journal, and sync. E-6 makes this sharper than it was: 152 rows already carry `requires` and
  ~92% of them are not standing rules, so sharing the predicate would mix ~8 authored directives
  into a population that is mostly misfiled prose.
- **`LearnedVia = "stated"`.** D19 reserves `stated` for the user's own words. Never `observed` —
  nothing in a directive was worked out by an agent. Keep the doc-comment symmetrical with
  `SessionFacts.LearnedVia`, which documents the opposite choice for the opposite reason.
- **`Scope = "user"`.** No new `fact.scope` value. D27's scopes are a *reach* axis; "directive"
  is a *kind*, carried by the path root. Collapsing them leaves a directive with no expressible
  reach, which is the axis the deferred per-project work (D-8) would use.
- **`regenerable = 0`**; `details` is not accepted (see CLI).

### D-7 — the path scheme deviates from `SessionFacts`, deliberately

`SessionFacts.PathFor` ends in a bare `FactStore.Fingerprint(statement)`. Directives use
**`<slug>-<8-char fingerprint>`** instead, and the deviation is justified by a difference in
access pattern, not by taste:

- A session note is found **by content**, through recall. Nobody ever reads its path.
- A directive is enumerated **by class**, through a listing. Its path *is* what a listing shows.

A bare fingerprint would render `engram_browse /directives` as a column of opaque hashes — a
useless answer to the question this feature exists to answer. A bare slug would reintroduce
exactly what D57 warns about: "Slugging it instead would let two distinct sessions collide on one
segment, which at the fingerprint leaf is not a display problem but one note superseding an
unrelated one." Two directives about testing would slug identically and one would silently close
the other through `FactStore.Append`'s live-match path — data loss on the one class whose whole
point is that the user's rules are honoured. The suffix keeps identity; the slug carries display.

**Since D-9 declines to change `engram_browse`, this scheme is the *entire* pull-side readability
of the feature.** It is load-bearing, not cosmetic. Do not "simplify" it back to a bare
fingerprint to match `SessionFacts` — the two classes are read by different mechanisms.

`CannedFactSeeder.Slug` is the existing implementation and must be reused rather than re-derived
(constraint 6). Its output is unbounded for a long statement, so the slug segment is truncated to
a fixed prefix before the `-` and fingerprint are appended: the listing stays readable and paths
stay bounded. Truncation is safe precisely because identity lives in the suffix — the same reason
the suffix exists.

### The read path — an existing index, and one specific trap

`DirectiveFacts.ReadLive` serves the primer:

```sql
SELECT id, body, valid_from
FROM   fact
WHERE  path >= '/directives/' AND path < '/directives0'
  AND  valid_to IS NULL
ORDER  BY valid_from;
```

**Do not write this with `LIKE '/directives/%'` or `substr(path, 1, $plen) = $prefix`.** Neither
can use `ix_fact_path` (`engram-schema.sql:172`): SQLite cannot plan an index seek through
`substr()`, and `LIKE` is case-insensitive by default, which disables the prefix optimization.
Both silently degrade to a full scan of `fact` — on a hook path, at 50,097 live facts. That is
D60 exactly, and it is invisible until the store is large. `'0'` (`0x30`) is the byte after `'/'`
(`0x2F`), so the bound is correct and exclusive. `FactStore.ReadSubtree` (`FactStore.cs:345`)
already uses this range form — copy it, do not re-derive it.

`PrimerSummary` gains one field:

```csharp
public sealed record PrimerSummary(
    int FactCount,
    IReadOnlyDictionary<string, int> TopicCounts,
    IReadOnlyList<CannedFact> ExampleCandidates,
    IReadOnlyList<string> Directives);          // new; empty when none
```

`PrimerSummary.From(IReadOnlyList<CannedFact>)` defaults it to empty — which keeps every
`PrimerBuilder` signature unchanged, needs no new threading through `HookCommand`, and is what
leaves the D15 guard passing (hazard 1).

**Directives are excluded from `FactCount` and `TopicCounts`.** `TopicHistogramSql` gains
`/directives/` alongside the session prefix it already excludes via `BindSessionExclusion` —
extending that binding, not adding a mechanism beside it (constraint 6). The coverage line
describes the *recallable* corpus; counting content the model is reading three lines above is
the D43 shape.

### The primer — ordering and the budget question

`PrimerBuilder.MaxTokens` is **300 for the entire primer**, and `TryAppendLine` **drops
silently**. Decision D-1: **directives do not share that budget and are never dropped.**

`Build` (session start):

```
1. precedence line             ← TryAppendLine, unchanged (D51: goes first)
2. directive block (+ header)  ← appended directly, NOT budget-checked
3. enrollment line             ← TryAppendLine, unchanged
4. coverage line               ← TryAppendLine, unchanged
5. examples                    ← AppendExamples, unchanged
```

`BuildForSubagent`: `SubagentInstruction`, precedence line, directive block, coverage line.

- **The mechanism already exists in this file.** `BuildForSubagent:88` adds `SubagentInstruction`
  straight into `lines`, counting its tokens but never budget-checking them. Directives use that
  same pattern — not a new concept beside `TryAppendLine` (constraint 6).
- **Directive tokens do not increment `tokens`.** Separate additive allowance, so they cannot
  displace the coverage line or examples. Ordering is therefore *reading order only*, not a drop
  priority — say so in the code comment, because every reader assumes the opposite.
- **The block carries a header**, and the header names the **path, never a tool**:
  `Standing directives (complete; memory path /directives):`
  Bare lines with no label are uninterpretable, and D15's guard forbids tool names in primer
  guidance. A path is not a tool name, and `engram_browse`'s own description already teaches that
  paths are browsable — so the pointer costs nothing on installs with no directives, because the
  header renders only with the block.
- **`session-start` is the only primer path that renders directives, and E-4 confirmed it is the
  only one that can.** `PostCompact` maps to `HookCommand.RunPostCompact` (`HookCommand.cs`
  542–584), which parses the compaction digest, appends session facts and records telemetry — it
  never calls `PrimerBuilder.Build` or `BuildForSubagent`. One primer per compaction, not two. Do
  not add a primer build to that verb.

Worst-case primer becomes `300 + MaxDirectiveTokens`. That is what constraint 3 requires be
measured; see E-1.

### The cost multiplier is subagent spawns, not sessions

`CLAUDE.md` is loaded a handful of times per session. Engram's primer is rebuilt at **every
context reset and every subagent spawn**. Twenty subagents multiply the block twenty times. The
cap is not "how much standing guidance is reasonable to write" but "how much is reasonable to
pay for on every spawn."

**`MaxDirectiveTokens = 250`** — Jim's decision. Roughly 900 characters, seven to eight
one-sentence rules at `TokenEstimator.Estimate`'s `ceil(len / 3.6)`. Deliberately far smaller
than a real `CLAUDE.md`.

**Hardcoded, not a config key.** "A bound a caller can raise is not a bound" — CLAUDE.md says
this about `--limit`, and the premise of the cap is that accumulated directives have no natural
brake. A config key would be the brake's off switch sitting next to the brake.

**The cap does two jobs beyond latency, and both are load-bearing elsewhere in this spec.** At
250 tokens the population is ~8, so (i) a directive listing can never hit a page boundary, a row
cap, or a `Take(n)` — D53's completeness rule satisfied by construction rather than by disclosure,
which is what **D-9** rests on; and (ii) the addressable population is small enough that no
discovery UI is warranted, which is what **D-10** rests on. Raising the cap reopens both.

### CLI surface — `engram directive`

New `src/Engram.Cli/DirectiveCommand.cs`, house pattern exactly: static class,
`public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)`,
home via `EngramHome.ResolveFromProcess(homePath)`, dispatch on `args[0]`. Registered in
`src/Engram.Cli/CliApp.cs`'s switch (24 verbs today, ending at `"sync"`):

```csharp
"directive" => DirectiveCommand.Run(homePath, rest, stdout, stderr),
```

| Verb | Behaviour | Dry-run? |
|---|---|---|
| `engram directive add "<text>"` | Writes one directive. The only authoring path (D-10). | **No** — acts by default |
| `engram directive list [--all]` | Live directives; `--all` retired too. | Read-only |
| `engram directive remove <id> --apply` | Closes one (`valid_to`). | **Yes** |
| `engram directive revise <id> "<text>" --apply` | Closes one, opens the replacement, links `superseded_by`. | **Yes** |

- **`add` acts by default; `remove`/`revise` dry-run first.** D49 applied — the boundary is the
  word *destructive*. Both close a fact already there, same class as `forget` (on D49's list).
  `add` only adds, the `install.sh` side of the line.
- **Addressing is the bracketed fact id** (`f42`) — exactly what `engram_forget` and
  `engram_revise` take. No new addressing scheme. **`list` is where the ids come from**, and
  after D-10 it is the *only* place: the CLI has no `recall` or `search` verb (verified against
  all 24 verbs in `CliApp.cs`), so a fact id cannot otherwise be obtained from a terminal. This
  is why `list` is not optional and why `remove`/`revise` are useless without it.
- **`list` prints each directive's token cost and the running total against the cap**, which is
  what makes the cap discoverable *before* the refusal rather than at it.
- **`add` refuses hard past `MaxDirectiveTokens`**, naming current total, this directive's cost,
  the cap, and how to retire one (D54: "a service that declines records why").
- **No `--details` flag.** `details` reaches no lane (D64) and the primer renders the statement,
  so an authored `details` would be content that can never reach where it was written for.
- **`add` prints that the directive takes effect at the next context reset**, not immediately.
  One line; not saying it produces the "I added it and nothing happened" report.

There is **no CLI `browse` verb** either, so `engram directive list` and `engram_browse` are two
audiences, not a duplicated behaviour: a human at a terminal, and the model mid-session.

### Answering "what are your directives?" — enumeration, not ranking

Jim's framing named a specific intent: *"I want to be able to ask 'What are your directives in
engram?'"* That is a **class-addressed** question with an exact, enumerable answer.

**Recall-lane inclusion is rejected, and the strongest argument is not D44.** It is that recall
structurally cannot answer this question:

1. **Zero term overlap.** `RecallEngine.BuildCandidates` scores
   `OverlapScore(queryTerms, fact.Subject + " " + fact.Body)`, and the lexical lane is FTS over
   body. The query "what are your directives" has terms {what, your, directives, engram}; a
   directive's body is "always run the tests before committing" — {always, run, tests, before,
   committing}. **They share nothing.** A recall lane containing directives would rank them
   *last* for precisely the question Jim wants to ask. It is a category mismatch, not a tuning
   problem: recall is content-addressed, this question is class-addressed.
2. **A ranked answer to an enumerable question is indistinguishable from a complete one.** Eight
   directives, three returned, and nothing in the output says five are missing. That is D53's
   rule verbatim — "a scan is bounded, and absence is only evidence when it finished" — and D44's
   shape, where a result that was 86% noise reported `high`.
3. **D44's corroboration math** would additionally be corrupted: a directive the model was
   *handed* is not independent evidence, so letting it reach the `3+` boundary inflates coverage
   in the direction that looks like success.

**`engram_browse` is the right surface, and its own description already says so:** *"A table of
contents, not a search: engram_recall finds facts by content, this shows how an area is
organised."* That is the same distinction, already shipped. It is in spec 03's `default` profile
and already in `ClaudePermissions.GrantedTools`, so serving directives from it costs **zero new
schema bytes and zero new permission prompts** — strictly better than a new tool.

**What browse does and does not return, precisely.** `MemoryBrowser.TopFacts`
(`MemoryBrowser.cs:82`) filters `.Where(f => f.SubjectPath == path.TrimEnd('/') && f.ValidTo is
null)` — **exact node only**. Since each directive is its own entity one level down,
`engram_browse("/directives")` returns **the child listing and no bodies**. Note the shape of the
waste: `FactStore.ReadSubtree` already range-scanned those descendants and `TopFacts` discards
them.

Three responses were considered:

- **Rejected — put every directive on the `/directives` node.** `ux_fact_live` permits one live
  fact per (subject, predicate), so only one directive could be live. Varying the predicate to
  dodge it would abuse a "normalized verb phrase" column as an identity key, which is the
  nearby-field reuse D43/D56 exist to prevent.
- **Rejected — make `TopFacts` always include descendants.** That changes a shipped tool for
  every path: browsing `/projects` would return every fact in every project, which is not a table
  of contents and is unbounded.
- **Deferred, not rejected (D-9) — a narrow fallback to immediate-children facts when a node has
  none of its own.** See "Deferred follow-ups".

**Adopted: the child listing is the answer, via D-7's readable slugs.**
`engram_browse("/directives")` renders `/directives/always-run-tests-before-committing-a3f9c2e1`
— the rule is legible in the path. The listing is **complete** rather than ranked (browse
enumerates children; the cap holds the population at ~8), which is the property that mattered.

**And the pull surface is a smaller part of this feature than it first appears.** `SessionStart`
matches `compact` (D51), so the verbatim block is re-injected at *every* context reset —
directives leave the model's context exactly when they are put back. The pull path serves two
narrower needs: confirming the set after a CLI edit made mid-session, and a long session that has
not yet compacted. Neither justifies changing a shipped tool. **Implementors should not read the
slug listing as a compromise the design is unhappy with** — verbatim delivery is the primer's
job, and it is unconditional.

### MCP surface — no new tool, and no write path

No new tool; no new parameter on `engram_remember`. If "directive" were a boolean on
`engram_remember`, the model could set it and the feature's defining property would become a
sentence in a `[Description]` asking it not to. **A property enforced by asking the model nicely
is not enforced.** D51 is explicit that that description's job is durability-plus-trigger and
that a diluted trigger loses.

A CLI-only write path makes authorship structural: **typing the command is the proof** — the same
argument `MaintenanceLauncher.cs:83` makes for `repo enroll` ("an enrollment index is someone
typing `engram repo enroll` and is never ambient").

**Do not overclaim it.** A model with `Bash` could shell out to `engram directive add`. What the
design buys is that there is no ambient path and no tool it reaches for by default — the same bar
`repo enroll` meets.

Reading needs no new tool either: the primer is the push surface, `engram_browse` the pull
surface, `engram directive list` the human one.

### Telemetry (D46, D43, D56)

`TelemetryRecord` gains one nullable field:

```csharp
[property: JsonPropertyName("directive_count")] int? DirectiveCount = null,
```

- `session-start` / `subagent-start` set it to the number delivered.
- **`fact_count` stays null on primer records** (D46). The null assertion is the load-bearing
  test half, as it was for D46.
- **Directive tokens stay out of `tokens_returned`** and out of `long_term_fact_count`. Those
  move with the corpus; this moves when a user types a CLI command. A field that moves on user
  authoring cannot answer D18/D43's "is memory delivery growing" — the exact trap D43 traced a
  wrong conclusion back to.
- Its own field, never a nearby one (D56; the `decision`-vs-`phase` precedent).

## Decisions

**D-1 — Never dropped; the bound moves to write time.** Rejected: inheriting `TryAppendLine`'s
silent drop. A directive that silently vanishes is the worst available failure — the user
believes a rule is in force, the model never saw it, nothing reports the discrepancy. Every
expensive lesson in CLAUDE.md is a silent-failure lesson (`dim`, pooling, the FTS desync, `has
tensor`). This is D64's split applied unchanged: `statement` is advisory because a bounced write
risks the capture; `details` refuses hard because it is deliberately authored and "a refusal
costs nothing a retry can't fix." A directive is *maximally* deliberately authored.

**D-2 — CLI-only authoring, no MCP write path.** Argued above.

**D-3 — Ordinary `fact` rows at a path prefix; zero schema delta.** The alternative — a new
predicate plus a partial index — was **rejected once `ix_fact_path` was found to already exist**.
A migration that buys nothing is a migration whose `CREATE INDEX IF NOT EXISTS` will one day
no-op against a fixture and pass (D60's trap).

**D-4 — Directives go to subagents.** `SessionStart` never fires for a subagent (D51;
`BuildForSubagent` exists because of it). A subagent under different standing rules than its
parent, while the parent believes otherwise, is worse than one with no memory at all — the
discrepancy is invisible on both sides. This is also where the cost lives, so it is the arm E-1
must measure most carefully.

**D-5 — Excluded from recall; served by enumeration.** Argued above.

**D-6 — Two tiers, and the existing enum is renamed.**
**Rename `UserFactKind.Directive` → `UserFactKind.Instruction`** (mechanical, two call sites:
`UserStatementClassifier.cs`, `HookCommand.cs:118`), and this feature keeps the name "directive."
The rename also makes the existing code self-consistent — it already maps to
`UserFactTopic.Instruction` and stores `requires`, so `Directive` was the odd name out before
this feature existed. The tiers remain distinct in the data model; no verb bridges them (D-10).

**D-7 — `<slug>-<fingerprint>` paths.** Argued above. Load-bearing under D-9.

**D-8 — `user` scope only; per-project directives deferred.** A **deliberate v1 scope decision**,
taken by Jim. Not an open question and not an unconfirmed default — an implementor builds
global-only and does not flag it as a gap. See "Deferred follow-ups".

**D-9 — `engram_browse` is not modified.** Taken by Jim. The narrow children fallback was
designed and **declined for this feature**, on blast radius. See "Deferred follow-ups". Its
consequences, so nobody re-derives them under pressure: D-7's readable slugs are the entire
pull-side readability and may not be simplified away; and `MaxDirectiveTokens` is what keeps the
child listing complete rather than lossy.

**D-10 — No promotion surface. `add` is the only authoring path. Rejected on E-6's evidence.**
Earlier drafts of this spec carried `engram directive list --candidates` (a menu of live,
unpromoted `requires` facts) and `engram directive add --from <id>` (promote one, copying rather
than moving). Both are **cut**. Three arguments, in order of weight:

1. **The cap makes a discovery UI structurally impossible to need.** `MaxDirectiveTokens = 250`
   bounds the live population at ~8. E-6 measured 152 candidates spanning ~14 unrelated projects.
   A 152-item menu cannot help fill eight slots, and the user already knows which eight rules
   they want — they are the user's own rules.
2. **Filtering does not rescue it.** The obvious repair is a text search over candidates. But
   then the user types the text of the rule in order to *find* it, and the search query and the
   directive body are nearly the same string — at which point `add "<text>"` was the shorter
   path. A surface whose input is its own output is not a surface.
3. **`--from` has no way to obtain its argument.** Verified against all 24 verbs in `CliApp.cs`:
   there is no `recall` verb, no `search` verb, and no handler containing either name. A human at
   a terminal cannot get a fact id from the Engram CLI at all except from `directive list`, which
   lists directives and not candidates. Without `--candidates`, `--from` would ship a flag whose
   argument can only be obtained by asking the model to run an MCP tool and reading the id back —
   for a rule short enough to retype, since the cap forces it under ~30 words.

**The premise was also wrong, and this is the part worth remembering.** `--candidates` was
justified in this spec as closing a seam: *"a user who typed 'always use tabs', saw it captured,
and finds it is not a directive."* The `user-prompt` hook writes silently — **the user never sees
the capture.** The seam was invented and then a verb was built to close it. E-6 did not merely
size the list badly; it removed the reason for the list.

**What is genuinely lost:** the `evidence: "promoted from f42"` provenance link. Judged not worth
a verb — the directive's own `learned_via = 'stated'` and `valid_from` carry the provenance that
matters, and under append-only the tier-1 fact stays live regardless, so nothing is destroyed.

**Distinguish the two cuts when reading this later.** `--candidates` is **rejected on evidence** —
E-6 falsified it. `--from` is **unmotivated once `--candidates` is gone**, which is a weaker
claim; if a promotion path is ever wanted again, it needs a way to address a fact from the CLI
first, and that is a separate design.

## A finding this feature does not fix

E-6 surfaced a real defect in the **existing** store, independent of this feature and out of
scope for it. Recorded so it is not lost:

**~92% of the `requires` population is misfiled.** Of 152 live `predicate='requires'` facts on a
real instance, only ~5–15 read as standing behavioural rules. The rest are defect reports,
code-review comments, spec decisions and general findings whose ordinary technical prose happens
to contain "always" or "never" — *"the IOException escapes uncaught on lock exhaustion"*, *"the
equality check fires first and that assertion never runs"*. The classifier's trigger words are
firing on prose, not on directive-shaped statements.

Why it matters beyond tidiness: `UserFacts.PredicateFor` maps
`UserFactTopic.Instruction → "requires"`, and `requires` asserts *the user requires this of me*.
That claim is false for ~140 rows. It is the D43 shape — a field whose meaning is one thing being
populated by something that means another — and it is currently harmless only because those facts
are retrieval-gated, which is exactly the property this feature removes for its own class. It is
**not** a reason to delay feature 7: `directs` is a separate predicate and the two populations
never mix.

Genuine examples worth knowing exist, since they are what a directive should look like: *"Jim's
standing architectural rule: do filtering, ranking and aggregation in the database engine, not by
materializing rows into memory"* and *"Jim's standing instruction: research established best
practices for scalable design FIRST, before proposing or accepting an approach."*

## Deferred follow-ups

**These are deferred, not rejected.** Both were designed, judged sound on their merits, and set
aside because they are separable from this feature. A future reader should treat them as open
proposals worth making on their own, not as ideas the design considered and turned down. (The
promotion surface is a *different* case — it was rejected on evidence; see D-10.)

| Follow-up | Why deferred (one line) | What it would need |
|---|---|---|
| **Per-project directives** (D-8) | Requires the primer to resolve a project identity it does not currently need, and to answer what a directive means in a session that is not inside a repo. | A scope design, not a parameter. `fact.scope` already carries the axis (D27), so nothing here forecloses it. |
| **`MemoryBrowser` immediate-children fallback** (D-9) | Changes `engram_browse` behaviour on *every* path, not just `/directives` — browsing `/sessions` would begin returning note bodies. | Judging it against every path it affects, as its own proposal. It costs no extra query (`ReadSubtree` already reads the rows) and is bounded by the existing `limit`; the merits are real, the scope is the objection. |

Neither blocks nor shapes this implementation. Building either *into* this feature is out of
scope — see hazard 7.

## Invariants preserved

| Invariant | Citation | How held |
|---|---|---|
| Facts are append-only | D8, constraint 2 | `remove` sets `valid_to`; `revise` sets `valid_to` + `superseded_by`. No body/predicate/validity rewrite. |
| Destructive verbs dry-run first | D49, constraint 5 | `remove`/`revise` need `--apply`; `add` only adds. |
| Measured budgets hold | Constraint 3, D4 | E-1/E-3. `file-touched` and `memory-guard` untouched. |
| One implementation per behaviour | Constraint 6 | Reuses `PrimerBuilder`'s undroppable-line pattern, `PrimerSummary`'s prefix exclusion, `ReadSubtree`'s range form, `CannedFactSeeder.Slug`, `engram_forget`'s id addressing. |
| No tool names in primer guidance | D15 | Header names a *path*. Guard renders zero directives — hazard 1. |
| `fact_count` null on primer records | D46 | Unchanged; `directive_count` instead. |
| No nearby-number reuse | D43, D56 | Own field; tokens stay out of `tokens_returned`. |
| Provenance is honest | D19 | `learned_via = 'stated'`. |
| Coverage counts lane agreement honestly | D44 | Directives cannot corroborate — not in the candidate set. |
| One home resolver | CLAUDE.md | `EngramHome.ResolveFromProcess`; no new path literal. |
| Derived state repairable, authored truth not | D8, constraint 4 | A directive is authored truth; `repair`/`compact` must not touch it. |
| Scopes need no change | D27 | `user` reused (D-8). |
| Existing tool contracts unchanged | D-9 | `engram_browse` behaviour identical on every path, `/directives` included. |
| Tier 1 untouched | D-10 | No verb reads, promotes, or closes a `requires` fact. |

## Implementation hazards

1. **The D15 guard scans the RENDERED primer.**
   `PrimerBuilderTests.Build_GuidanceLines_DoNotRestateToolDescriptions` (`PrimerBuilderTests.cs:43`)
   takes every line before `"Examples:"`, subtracts the exact precedence strings via a
   `HashSet<string>` of `MemorySettings.PrimerLine` values, and asserts no tool name appears. A
   user directive saying "always call `engram_recall` first" would redden the build. It does not
   today, because the test's `IReadOnlyList<CannedFact>` overload routes through
   `PrimerSummary.From` and renders **zero** directives. **That is luck until it is written
   down** — add an explicit assertion that the summary under test has no directives, plus the
   reason. **Do not widen the exemption set**; it is subtracted by exact string precisely so it
   cannot be, and a guard loose enough to admit arbitrary user text no longer catches
   system-authored drift.
2. **`FactCatalog.ReadLongTerm` is NOT recall's read path — filtering there is a silent no-op.**
   Verified: it has exactly **one** non-test call site (`FactCatalog.cs:24`, an overload chaining
   to itself); recall reads through `RecallRanker.Rank` (`RecallRanker.cs:99`), which executes
   hand-written SQL filtering `f.valid_to IS NULL` inline. An implementor who "helpfully" adds
   the directive exclusion to `ReadLongTerm` changes **nothing in production** while perturbing
   `FactCatalogTests`, `RecallRankerEquivalenceTests`, and `PrimerSummaryEquivalenceTests` — a
   green-looking suite over a broken feature. The exclusion belongs where the long-term candidate
   list is materialized inside `RecallRanker`. If that list is assembled by more than one query,
   it must be applied to each, and the test below must prove a directive reaches **no** lane.
3. **`LIKE` / `substr` silently full-scans.** Covered above. Pair `EXPLAIN QUERY PLAN` with a
   timing — D60: "a plan is not a clock."
4. **Do not add a primer build to `RunPostCompact`.** E-4 established that it does not build one
   (`HookCommand.cs` 542–584) and that `SessionStart` matching `compact` is the single injection
   point. An implementor who "notices" that post-compact has no primer and adds one produces
   double injection per compaction — double tokens, reading as emphasis.
5. **Zero-directive byte-identity.** The primer on an install with no directives must be
   byte-identical to today's. This is the regression that would otherwise go unnoticed on every
   existing install. Write this test first.
6. **Empty-store interaction.** D51 makes an empty store still emit a primer. A store with
   directives and no other facts must emit the directives *and* the precedence line, with
   `CoverageLine` returning null as today (`factCount == 0`). The code must not conflate "nothing
   to say" with "no facts."
7. **Do not touch `MemoryBrowser` (D-9), and do not re-add a promotion surface (D-10).** Both
   were designed and cut. An implementor who notices that `/directives` browses without bodies,
   or that tier 1 has no bridge to tier 2, is taking a decision that was already taken the other
   way — the second one against measured evidence.

## Tests by tier (D9)

Every guard names its falsification. A guard that cannot fail is worthless, and per D60 a
falsification runs against a **committed** tree with `git diff --quiet` proving the break landed
— a harness restoring with `git checkout --` reverts the change under test.

### Tier 1 — unit

- Ordering in both `Build` and `BuildForSubagent`; header present and containing no tool name.
- **Undroppability:** a directive set at the cap plus an informational primer already at 300
  tokens renders *both* in full. Falsify by routing directives through `TryAppendLine`.
- **Zero-directive byte-identity** (hazard 5).
- Empty store + directives present: precedence + directives emitted, coverage null (hazard 6).
- Cap boundary: exactly `MaxDirectiveTokens` accepted, one token more refused.
- `DirectiveFacts.PathFor` — stable for one statement, distinct for two, and **two statements
  that slug identically produce different paths** (D-7's whole point). Falsify by dropping the
  fingerprint suffix.
- `PathFor` on a very long statement stays bounded and still ends in the fingerprint.
- The D15 guard gains its explicit zero-directive assertion (hazard 1).

### Tier 2 — integration, real SQLite (the bulk, per D9)

- `add` / `list` / `remove --apply` / `revise --apply` round-trip.
- **`remove` and `revise` without `--apply` change nothing** — assert the store is unchanged, not
  merely exit 0.
- `revise` sets `superseded_by` and leaves the closed row's body intact (D8).
- **Cap refusal leaves the store unchanged** — no partial application.
- **A directive reaches no recall lane**, for a query whose terms it contains — **and a tier-1
  `requires` fact still does.** The second half is load-bearing: an over-broad filter excluding
  both would pass a test asserting only the first.
- **No `directive` verb reads, writes, or closes a `requires` fact** (D-10, hazard 7). Seed a
  live `requires` fact, run every `directive` verb, assert it is untouched and never listed.
- **A directive does not reach `coverage`'s corroboration count** (D44) — catches an exclusion
  applied to the pack but not the ranker.
- Excluded from `PrimerSummary.FactCount` and `TopicCounts`.
- **The `fact_token` from-scratch-vs-incremental guard still passes with directives present** —
  catches an implementor excluding at the index chokepoints instead of at query time, which would
  leave `TokenIndexNeedsRebuild` permanently true.
- `DirectiveFacts.ReadLive` uses `ix_fact_path`: assert `EXPLAIN QUERY PLAN` shows `SEARCH`, not
  `SCAN`. Falsify by rewriting with `LIKE`.
- `engram_browse("/directives")` lists every live directive as a child, **and each child path
  contains the directive's slug** — the D-7 property D-9 makes load-bearing. Falsify by reverting
  `PathFor` to a bare fingerprint; the slug assertion must fail, not merely look worse.
- **`engram_browse` on a non-directive path returns exactly what it returns today** (D-9). The
  regression guard for hazard 7.
- `backup replay` round-trips a directive additively (feature 1 / D32).

### Tier 3 — end-to-end, published binary (read the skip count, not the pass count)

- `engram directive add`, then `hook session-start`: the directive text appears verbatim.
- The same for `hook subagent-start` (D-4) — the arm that would silently not exist if
  `BuildForSubagent` were missed.
- **`hook post-compact` emits no primer and no directive block** (E-4, hazard 4). Falsify by
  adding a primer build to `RunPostCompact`.
- Telemetry for that `session-start` carries `directive_count` **and `fact_count` null** — the
  null is the load-bearing half (D46).
- Zero-directive install: primer byte-identical to the pre-feature binary (hazard 5).
- **No test may assert a total line count of `telemetry.jsonl`** — `session-start` spawns the
  maintenance child and the server writes its own records; filter by kind. This has broken four
  tests twice already (D55, D56).

## What is measured, and how

Constraint 3: a feature that puts work on a hook path carries its own measurement. This adds a
query and a text block to `session-start` and `subagent-start`. None of these numbers exist yet.

**Method, and the rule easiest to get wrong:** measure on the **published binary**, and time
`session-start` **through a file, never a pipe.** It spawns the detached maintenance child, and a
timer stopping at EOF measures every process holding the pipe — that error already invalidated a
before/after pair here and produced a 148.9 → 92.5 ms "saving" that was not real.
`subagent-start` forks nothing and is the clean isolate; prefer it for attributing cost.
Calibrate by running the same binary against itself (±0.07 ms here) — a difference smaller than
the harness is not a difference. Alternate arm order; running the same arm first every iteration
charges arm A whatever the first of a pair costs (that error once turned +0.08 ms into +0.78 ms
and nearly moved a write into a polling service for nothing).

**Every measurement passes `--home` or exports `ENGRAM_HOME`.** The test-side guards
(`SandboxHome`, the assembly fixture) constrain test code and not a shell running `./out/engram`,
which resolves the default home and writes there — correctly, since that is its production
behaviour. This has already littered the real `~/.engram` once.

## NEEDS-EVIDENCE

### Answered — pre-implementation

- **E-4 — Double injection. ANSWERED; design unchanged.** `plugin/hooks/hooks.json` maps
  PostCompact → verb `post-compact` → `HookCommand.RunPostCompact` (`HookCommand.cs` 542–584),
  which opens the DB, parses the compaction digest via `CompactionDigestParser.Parse`, appends
  session facts and records telemetry. It does **not** call `PrimerBuilder.Build` or
  `BuildForSubagent`. Only `SessionStart` (matching `compact`) builds a primer, so there is one
  primer per compaction. Settled by reading, with no experiment needed. Now guarded — hazard 4
  and its tier-3 test.
- **E-6 — Tier-1 population. ANSWERED; changed the design.** 152 live `requires` facts across
  ~14 unrelated projects; ~5–15 read as genuine standing rules. This **validated** the
  no-auto-promotion decision (auto-promotion would have put ~140 irrelevant facts into
  unconditional context) and **falsified** the candidate-listing surface, which is cut — see
  D-10. The misfiling itself is recorded under "A finding this feature does not fix".

### Gate the merge; run after implementation

- **E-1 — Hook cost.** `session-start` and `subagent-start`, published binary, **5k and 50k live
  facts × 0 / 8 / 20 directives**, reported over the `probe` floor. *Decides:* whether it ships
  as designed. **Bar:** the 20-directive arm within noise of the 0-directive arm at both sizes —
  the block is bounded while the corpus is not, so cost growing with the corpus means the read is
  not using `ix_fact_path` and hazard 3 is live.
- **E-2 — Index confirmation.** `EXPLAIN QUERY PLAN` at 50k **plus** a wall-clock timing of the
  same query. Both halves required — D60: a plan could show the scan and could not show it was
  99% of the statement.
- **E-3 — Cap calibration.** Actual `TokenEstimator` counts for a realistic directive set, and
  **what fraction of the 300-token budget the informational primer uses today** at 5k and 50k.
  250 is decided; this confirms the combined worst case rather than re-opening the number.

### Resolved in design

- **E-5.** The D15 guard scans the rendered primer (`PrimerBuilderTests.cs:43`) and passes
  because the test's overload renders zero directives. See hazard 1.

## Open questions

**None.** All forks resolved: Q-1 (enumeration via browse, not recall — D-5), Q-2 (250 tokens),
Q-3 (user scope only — D-8), Q-4 (`list --all` is the history surface), Q-5 (rename the existing
enum — D-6), Q-6 (`engram_browse` unmodified — D-9), Q-7 (no promotion surface — D-10).

Two items are **deferred with reasons recorded** and are not open questions: see "Deferred
follow-ups". One item is **rejected on evidence**: the promotion surface, D-10.

## Confidence

High. Storage, read path, budget model, authoring surface, and pull surface all land on existing
mechanisms; the schema delta is zero and no shipped tool changes behaviour. Five findings
reshaped the design and each removed either new machinery or a wrong assumption: `ix_fact_path`
already exists (no migration); `BuildForSubagent` already has an undroppable line (no new budget
concept); `UserFactKind.Directive` already exists (the two-tier model and the rename);
`MemoryBrowser.TopFacts` filters to the exact node (which makes D-7's readable slugs load-bearing
rather than cosmetic); and E-6's 152-vs-~8 measurement (which cut the promotion surface entirely).
**The surface area has shrunk at every revision**, which is the direction that should inspire
confidence.

**No Ultra-Advisor escalation recommended.** No auth, security, concurrency, or data migration;
no existing row is rewritten; no existing contract changes.

**Ready for the Implementor, with nothing outstanding before start.** E-1/E-2/E-3 gate the merge.
The only residual uncertainty is measurement, not judgment.
