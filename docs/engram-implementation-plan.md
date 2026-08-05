# ENGRAM — Implementation Plan

**Companion to** `engram-spec.md` (Rev D) · **Status:** proposed · **Date:** 2026-08-04

This plan takes the spec as the requirement and answers the questions the spec left
open or answered inconsistently. Where it departs from the spec, the departure is
stated as an erratum with a reason. Nothing here changes the spec's goals.

---

## 0. Environment (verified)

| | Found | Consequence |
|---|---|---|
| .NET SDK | `10.0.301` only (no 9.x) | Target `net10.0`. Spec says ".NET 9+" — satisfied and better for AOT. |
| Host | `osx-arm64`, Darwin 25.5 | Primary dev target. Native AOT on macOS needs Xcode CLT for the linker — verify day 1. |
| SQLite (system) | 3.51.0 | Above every floor we need. But we ship our own via SQLitePCLRaw, so the system copy only matters for `sqlite3` debugging. |
| Repo | **not a git repository** | `git init` before any code lands. |

---

## 1. Decisions (locked)

Twenty-six architectural forks are locked below. Each is a decision, not an option.
Six of them were adjudicated with Fable.

### D1 — Packaging: AOT core, Roslyn sidecar, native libs in the data directory

- **`engram`** — Native AOT. Contains CLI, MCP server, temporal store, retrieval, and
  the universal + document analyzers. All pure managed, plus one native library it does
  not contain: SQLite. This paragraph used to claim SQLite was statically linked via
  `SQLitePCLRaw.bundle_e_sqlite3`; that is false, and was believed for long enough to
  ship an installer built on it. `SQLitePCLRaw.lib.e_sqlite3` 2.1.12 provides a static
  `e_sqlite3.a` for `browser-wasm` and no other RID, and its targets file wires the
  static reference under `'$(RuntimeIdentifier)' == 'browser-wasm'` alone. On every RID
  engram ships, the P/Invoke is resolved at runtime by `dlopen` against a library beside
  the executable. Measured: an `engram` copied away from its publish directory dies with
  `DllNotFoundException: Unable to load shared library 'e_sqlite3'` on first database
  open — `otool -L` does not show this, because a dlopen dependency is not a load
  command. Anything that installs, copies, or packages the binary carries
  `libe_sqlite3.dylib`/`.so` with it, and proves it did by running the *installed* copy
  at a command that opens a database.
- **`engram-analyzer-roslyn`** — separate self-contained trimmed/R2R binary. Spawned
  only by background indexing, speaks stdio JSON-lines, returns entities/edges/facts.
  **The core owns every DB write**; the sidecar never opens the database.
- **Optional native libs** (`llama.cpp` for LLamaSharp, `sqlite-vec`) live in
  `~/.engram/lib/`, fetched by `engram init --with-embeddings`, loaded through
  `NativeLibrary.Load` with an explicit path.

Rationale: the latency-critical paths — hooks, MCP start, recall — never touch Roslyn
or llama.cpp, so AOT's cold-start budget is preserved where it is actually spent.
Indexing tolerates a ~300 ms sidecar start amortized over a batch.

Cost accepted: the release artifact is two binaries and "single file, no runtime"
softens to *"single file for the core; optional features populate the home directory."*
A protocol version is embedded in both binaries; mismatch is a hard refusal.

**Status: verified by measurement (2026-08-04).** See §1.5 — the AOT core is viable and
the MCP SDK is AOT-clean, so no hand-rolled JSON-RPC fallback is needed. Roslyn itself
was *not* tested under AOT; the sidecar stands on the general reflection/MSBuild
argument, and is the one part of D1 still resting on reasoning rather than a number.

> **Erratum (spec §5.2):** "AOT-compatible workspace loading" for Roslyn is not a real
> thing. `MSBuildWorkspace` is MEF/reflection-driven and hostile to trimming. The
> sidecar makes it moot — and it should use plain `CSharpCompilation` over enumerated
> files, no MSBuild at all.

### D2 — Identity: `entity.id` is identity; `path` is a mutable addressing attribute

`path` stays `UNIQUE` and indexed (the prefix range scan is the spec's key retrieval
trick and must stay fast), and `fact.path` stays denormalized. But a rename cascades:
one transaction rewriting the path prefix on `entity` and `fact`. Old paths are
recorded as aliases in `entity.meta`. Exposed as exactly one operation,
`MoveSubtree(oldPrefix, newPrefix)`, so there is one code path to test.

> **Erratum (spec §4.1):** "facts are NEVER updated except the two closure columns"
> contradicts a denormalized `fact.path` under renames. Amend to: *belief content
> (predicate, body, object, validity) is immutable; addressing metadata follows the
> entity.*

> **Erratum (spec §5.2):** the claim that a deep analyzer "never re-keys" universal-tier
> entities does not survive contact with overloads, nested types, and qualified-name
> grammar. Plan for re-keys: ship an explicit **adopt/merge** step where the deep tier
> resolves a symbol to an existing entity by name + span, takes its id, corrects the
> path, and files the old path as an alias. A versioned path-grammar document is an
> M3 deliverable, not an assumption.

### D3 — FTS5: external-content, live facts only, trigger-maintained

Not contentless. The `'delete'` command needs the previously indexed values, and
external content has them by construction; facts are never hard-deleted so the content
table cannot dangle.

```sql
CREATE VIRTUAL TABLE fact_fts USING fts5(
  body, predicate, content='fact', content_rowid='id',
  tokenize='porter unicode61');

CREATE TRIGGER fact_ai AFTER INSERT ON fact BEGIN
  INSERT INTO fact_fts(rowid, body, predicate) VALUES (new.id, new.body, new.predicate);
END;

CREATE TRIGGER fact_close AFTER UPDATE OF valid_to ON fact
  WHEN old.valid_to IS NULL AND new.valid_to IS NOT NULL BEGIN
  INSERT INTO fact_fts(fact_fts, rowid, body, predicate)
    VALUES ('delete', old.id, old.body, old.predicate);
END;

CREATE TRIGGER fact_ad AFTER DELETE ON fact BEGIN   -- compact only
  INSERT INTO fact_fts(fact_fts, rowid, body, predicate)
    VALUES ('delete', old.id, old.body, old.predicate);
END;
```

`subject_name` drops out of FTS (external content requires indexed columns to exist on
the content table) — join `entity` at query time instead.

Live-only is deliberate: letting superseded facts consume `seed_k` slots before
filtering pollutes ranking. History search is rare and served by a `LIKE` scan over
closed facts; an opt-in archive FTS table can come later if that ever hurts.

### D4 — Concurrency: WAL discipline, no lease, hooks never write SQLite

For M0/M1, ship all four of these together — they only work as a set:

1. `busy_timeout = 5000` on every connection.
2. **Every write transaction is `BEGIN IMMEDIATE`.** A deferred transaction that
   upgrades to a writer yields `SQLITE_BUSY_SNAPSHOT`, which busy-timeout cannot wait
   out. This is the classic WAL footgun and the single most likely source of
   intermittent failures.
3. The background indexer commits in chunks capped at ~50 ms / a few hundred rows.
   No long write transactions, ever.
4. **The `file-touched` hook does not open the database.** It writes one spool file per
   invocation under `~/.engram/queue/`, drained by the MCP server or the indexer, rather
   than appending to a single shared spool file: `FileMode.Append` is seek-then-write,
   not POSIX `O_APPEND`, so two concurrent hooks appending to one file can resolve the
   same end-of-file offset and lose a record, and the hook's < 10 ms budget rules out
   fixing that with lock-and-retry. This makes the < 10 ms budget unconditional rather
   than "true unless an indexer chunk is committing."

Rule 4 is about `file-touched`, not about hooks as a category, and the distinction is
load-bearing enough to state: everything justifying it — per-edit frequency, the
concurrent-append race, the unconditional sub-10 ms budget — describes that hook and
only that hook. The primer hooks (`session-start`, `subagent-start`) fire once per
session or per spawn, take a read, and close it. They may open the database, and must,
because a primer sourced from a hardcoded list stops agreeing with recall the moment a
fact is forgotten — it keeps announcing memory the user has cleared. Measured over 3
rounds of 40 invocations of the published binary: 10.6 ms hardcoded vs 12.1 ms reading
the store. Note the hardcoded version already exceeded 10 ms on process start alone,
which is the clearest evidence that the sub-10 ms budget was never a claim about these
hooks.

"No hook writes" was asserted here as the part that generalizes. It does not, and the
correction is worth more than the rule was. `user-prompt` writes, because it is the only
place a fact the user states in passing can be caught at all — the M0 telemetry says the
model does not call `engram_remember`, so a capture the model has to opt into is a capture
that does not happen. It fires once per message a human types, which is not remotely the
per-edit rate rule 4 was written against, and it takes one `BEGIN IMMEDIATE` transaction
held for the length of an insert. Measured the same way, 3 rounds of 40 invocations of
each published binary: 13.1–13.4 ms writing to the store against 11.1–11.4 ms writing a
JSON file, so about +2 ms on a process start that dominates both.

What actually generalizes is narrower and is the rule to keep: **a hook opening the
database is a decision with a measurement behind it, never a default.** Two have earned
it. `file-touched` has not and will not — its budget is the one rule 4 describes.

Salience bumps are batched in memory and flushed best-effort — a dropped bump is
harmless, and it is not worth contending for the write lock.

No lease, no queue table, no daemon: a lease is a daemon in denial and reintroduces
the liveness problems (stale lease after a kill) that SQLite was chosen to avoid.

**Watched failure mode:** WAL checkpoint starvation. Long-lived MCP read snapshots
during a bulk index block checkpointing; the WAL grows unbounded and every reader
slows. Mitigations: keep read transactions short (no lingering snapshot in the MCP
loop) and run `wal_checkpoint(TRUNCATE)` after each bulk indexing phase. **M1 must
include a test that measures hook p99 under a concurrent bulk index.** If that
measurement ever fails, the pre-planned escalation is the single-writer lease — but it
is not built until then.

### D5 — Contradiction detection: cut from v1

Ship no automated cross-predicate contradiction detection. Keep the `contradicts`
edge type and the ⚠ recall rendering, and let the *agent* write those edges via
`remember` / `revise`.

The same-subject-same-predicate collision path (§4.3 step 2) already covers the case
that matters, and §12's own proposed narrowing of step 3 ("same subject+predicate with
differing objects only") collapses step 3 into a subset of step 2 — the spec has
already argued itself out of the feature. A false ⚠ costs tokens *and* trust on every
recall that surfaces it, and negation heuristics over ≤ 60-token free-text bodies will
misfire constantly ("uses JWT" vs "doesn't use JWT for refresh" is not a
contradiction). The agent reading both facts side by side in recall output is the
detector, and it is free.

Revisit in M4 with embeddings, gated on real transcripts showing conflicts the agent
missed.

### D6 — Sequencing: prove adoption before building the code graph

The spec's §1.2 says every predecessor died because *the LLM never called the memory
tool*. M1 as specified spends its entire budget before producing one bit of evidence
on that question. So the plan inserts an **M0 adoption probe** ahead of it, and holds
M3 (the most expensive milestone, and the one carrying the D1 sidecar risk) behind
adoption data. See §3.

### D7 — Isolation: nothing is ever hardcoded to the installed instance

Every filesystem path in Engram derives from a single resolved **home root**, in this
precedence order: `--home <path>` flag → `ENGRAM_HOME` env var → `~/.engram`. There is
one resolver, and no other code may compute a path from `$HOME`.

This makes a fully isolated instance a one-liner (`ENGRAM_HOME=/tmp/e1 engram init`) and
makes every layer testable without risking the user's real memory:

- **The installer takes explicit targets.** `engram install claude-code
  --settings-path <file> --mcp-config <file>`, so a sandbox install writes hook and MCP
  registration into a throwaway settings file, never `~/.claude/settings.json`. The
  real paths are defaults, not assumptions.
- **Integration tests each get a fresh temp home** and assert against real database
  files, not mocks — temporal semantics and WAL concurrency cannot be honestly tested
  against an in-memory fake.
- **A hard test guard:** the test fixture refuses to run if the resolved home is the
  real `~/.engram`, failing loudly rather than writing a byte. The failure mode here is
  destroying the user's accumulated memory, so it gets a belt and braces.
- **A lint test** scans the sources for hardcoded `.engram`, `$HOME`, and `~/.claude`
  literals outside the one resolver and the one installer default, and fails the build.
  This is the only thing that keeps the rule true after month one.
- **End-to-end agent tests** additionally set `CLAUDE_CONFIG_DIR` to a temp directory,
  so a test session's hooks, MCP registration, and transcripts are all disposable.

Portability (spec §2.2, "copy the directory to move machines") falls out of the same
resolver for free.

### D8 — Diagnostics and repair, split along the derived/authored line

The spec has `doctor` but no repair. Both ship, and the boundary between them is the
one `compact` already uses: **derived state is repairable, authored truth is not.**

`engram doctor` — read-only, exit code reflects severity, one screen of actionable
output. Checks: `PRAGMA integrity_check`; FTS `'integrity-check'`; schema version vs.
binary; orphan facts (subject with no entity) and orphan salience rows; facts closed
with no `supersession` row and vice versa; `superseded_by` pointing at a missing fact;
`fact.path` disagreeing with its subject entity's path; WAL size and last checkpoint;
stale spool-queue entries; hook registration present *and* its binary path still valid;
config lint; embedder state; facts well over the 60-token target.

`engram repair` — **dry-run by default**, `--apply` to execute, and it snapshots the
database first regardless. It may only rebuild what can be derived again: the FTS index
(`INSERT INTO fact_fts(fact_fts) VALUES('rebuild')`), salience scores, `fact.path`
re-derived from the owning entity, orphan salience rows, WAL checkpoint and VACUUM, and
re-attaching a repo whose disk location moved.

It may **never** create, alter, or delete a fact body, a predicate, a validity window,
or a supersession row. If the only way to fix something is to invent or destroy a
belief, `repair` reports it and stops — that is the user's decision, not the tool's. A
database corrupted beyond that rule is a restore-from-backup situation, which is why
`export` and the pre-repair snapshot exist.

### D9 — Testing: five tiers, and the integration tier is the one that matters

Unit tests cannot establish the things most likely to break Engram. Its risks are
temporal invariants over long histories, several OS processes contending for one SQLite
file, an AOT binary behaving differently from the JIT build that passed CI, and an MCP
tool surface whose *output text* is a contract with a language model. All four are
integration concerns. So the integration tier is the primary tier, not a supplement.

| Tier | Runs | Scope |
|---|---|---|
| **1 · Unit** | every build | Pure logic — path resolution, salience math, token estimation, packing. Fast, no I/O. |
| **2 · Integration** | every build | Real SQLite files in a disposable home. Temporal semantics, FTS sync, retrieval fusion, doctor/repair, installer merges. **The bulk of the suite lives here.** |
| **3 · End-to-end** | every push | Drives the *published AOT binary* as a subprocess — CLI verbs, `engram mcp` over real stdio JSON-RPC, `engram hook …` emitting real hook payloads. |
| **4 · Stress & concurrency** | nightly + before release | N processes against one database; hook latency percentiles under bulk index; WAL growth; kill-mid-write recovery. |
| **5 · Adoption telemetry** | continuous, real sessions | Not pass/fail. Recall coverage rate over time — §12 of the spec calls this *the* health metric. |

Tier 3 gets its own project (`tests/Engram.EndToEnd.Tests`) because it depends on a
publish step and is too slow for the inner loop. It is not optional: the JIT build
passing proves nothing about the artifact that actually ships, and trimming failures are
exactly the kind that appear only in the published binary.

Specific things that must have integration tests, because each is a known way this
design fails quietly:

- **Temporal invariants, property-based.** Generate random sequences of
  remember/revise/forget/reindex and assert after every step: no fact ever disappears;
  at most one live fact per (subject, predicate); every closed fact has exactly one
  supersession row; every `superseded_by` resolves; history chains terminate. Chosen
  examples will not find the ordering bug that a thousand generated sequences will.
- **Concurrency, with real processes.** Not threads — processes, because that is what
  ships: several `engram mcp` servers, hook invocations, and a detached indexer against
  one database. Assert no `SQLITE_BUSY` surfaces to a caller, hook p99 stays under
  budget during a bulk index, and the WAL gets truncated rather than growing without
  bound (D4's watched failure mode).
- **Crash recovery.** `SIGKILL` mid-write, reopen, assert the database is consistent and
  no partial fact is visible. WAL should handle this; asserting it is cheap.
- **The recall output contract.** §6.2's format is read by a model, so it is an
  interface. Golden-file tests, changed deliberately and reviewed as an API change.
- **MCP conformance.** Drive the real server over stdio: `initialize`, `tools/list`,
  every tool called with valid and invalid arguments, and the `remember`-without-reason
  soft error that §9 says must be structurally enforced.
- **Repair drills.** Corrupt the FTS index, the salience table, and `fact.path`
  independently; assert `doctor` names each and `repair --apply` fixes it with every
  fact body and supersession row bit-identical afterward.
- **Installer round-trips** against fixtures carrying foreign hooks and unrelated
  settings — already built, and the pattern for everything that touches user files.
- **Migration.** Every schema version bump ships with a test that opens a database
  written by the previous version and reads it correctly.
- **Hard latency deadlines belong in tier 4, not tier 3.** A wall-clock assertion in the
  every-push tier competes with whatever else that tier happens to be running, so a
  hardcoded deadline there is unsound by construction. Tier 3 asserts a median over
  repeated samples as a smoke check; tier 4 owns real percentiles under controlled load.

Cross-platform matters too: the spec promises four RIDs, so CI runs the full suite on
osx-arm64 and linux-x64 at minimum. A test that only ever ran on the author's Mac is a
claim, not evidence.

### D10 — Zero manual init is a goal, not a side effect

A primary reason this tool exists rather than an existing one is that memory must
accrue without the user ever running an init command for a repository. The spec already
has the machinery — SessionStart-triggered indexing, `auto_index_on_session_start`, a
registry keyed on normalized git remote so a moved or re-cloned checkout reattaches —
but it describes them as implementation detail. They are the requirement.

Stated properly: **an unfamiliar repository must become useful memory with no user
action, ever.** That has a consequence worth naming, because it is the cost of the
virtue: a tool that indexes every repository you happen to open will accumulate memory
about repositories you looked at once and never think about again. Manual init is at
least a deliberate judgment about what deserves remembering; removing it means Engram
owns that judgment. So zero-init requires a matching opt-*out* — a per-repo ignore that
is one command and is honoured by the registry, plus `compact --path` to discard a
branch that turned out not to matter.

### D11 — Session memory: what the model would otherwise hold in context

Long-term memory is not the only thing missing from existing tools. The model also
needs somewhere to put **working state it would otherwise carry in context and lose** —
to compaction, or to a subagent that returns an incomplete report, or to a subagent that
dies. This is durable and later recallable like any other fact, but its primary read
window is the session that wrote it.

- **A new root, `/sessions/<session-id>/…`,** with subagent-written facts one level
  deeper at `/sessions/<session-id>/<agent>/…`. `[taxonomy] roots` is config, so this
  costs a config line and routing rules; the schema does not move.
- **Keyed on the MCP server's session id.** §1.5's research established that the
  server's process lifetime is exactly one Claude Code session, and subagents share the
  parent's MCP connection — so they write under the same session key by construction.
  Spec §8's `via_subagent` marker stops being provenance decoration and becomes the
  thing that distinguishes which worker learned what.
- **Recall needs a session lane.** Facts under the current session outrank everything
  else by default. Working memory that competes on equal terms with a year of project
  memory is not working memory.
- **A different distillation bar.** *"Checked the WAL-starvation theory, not the cause"*
  is a legitimate working note and not a ≤ 60-token durable truth. Session facts are
  exempt from the distillation target, though not from the recall token budget.
- **They age by salience, never by deletion.** A year-old session note still exists; it
  simply stops outranking anything. This is what keeps the branch from turning recall
  into an archaeology dig.

  That is about *time*, and it was over-read as being about retraction. Session notes
  spent M0 in a per-session JSONL file, a format with no notion of a closed record at
  all, which made a note the one kind of memory that could be written and never taken
  back: `engram_forget` refused them by construction, and a note the model got wrong
  stayed recallable for good. Notes are ordinary facts now, closed by the same
  `FactStore.Forget` as everything else. Aging still never deletes; a user still can.

- **The path segment is the `session` row id, not the host's session string.** D11 wrote
  `/sessions/<session-id>/…` when the session id was a file name. As a path segment it is
  a hazard: the host's identifier is opaque text that may contain a `/`, and a segment
  that invents a level of hierarchy silently reparents a whole session's notes. Slugging
  it instead lets two distinct sessions collide on one segment, which at the leaf is not
  a display problem but one session's note superseding another's. `session.external_id`
  is already the bridge between the host's string and a foreign key (D19), so the path
  uses the row id and reads names back through the entity table.

- **A note's leaf is a fingerprint of its own statement.** `ux_fact_live` is unique over
  subject and predicate, so a root holding many independent statements needs a
  per-statement address or each new note closes the last. Two behaviours then fall out of
  addressing rather than policy code: recording the same statement twice in one session
  returns the existing handle instead of writing a supersession row that claims a belief
  changed when the text is identical, and two sessions reaching the same conclusion keep
  two notes, because the session segment sits above the fingerprint.
- **Subagents need their own primer, and it has a trap.** Spec §8 says subagent sharing
  is "about provenance and scope, not transport" because subagents inherit the MCP
  server. That is transport-correct and adoption-blind: **`SessionStart` hooks do not
  fire for subagents**, so a subagent inherits the tools while knowing nothing about
  them. Under D12 — where adoption needs intervention even for the main agent — a
  subagent that never saw a primer will not write session memory, which is most of D11's
  point. So the hook suite gains `SubagentStart`, emitting a compact primer naming the
  session and the tools.

  The trap, confirmed first-hand by this user across two of their own plugin repos:
  **`SubagentStart` silently discards plain stdout**; only the
  `hookSpecificOutput.additionalContext` envelope is delivered, with no error and no log
  line. `SessionStart` accepts bare stdout, so the habit formed on one event actively
  misleads on the other. Our `session-start` already emits the envelope, which means
  nothing in our own code would have warned us.

  Note also that two plugins must not both rewrite the `Agent` tool's input via
  `PreToolUse` to reach subagents — that is undefined when both fire, whereas multiple
  hooks' `additionalContext` on one event aggregates safely. `SubagentStart` is the
  supported channel; input rewriting is not.

The payoff is testable in a way the rest of the design is not: write a fact, get
compacted, recall it. That is the tool visibly paying for itself inside a single
session, rather than a bet on value that only materializes next week.

#### The subagent primer, and the one thing it is built to find out

`SubagentStart` now delivers a shorter primer than `SessionStart` — no examples, since a
subagent's context is spent on its task — differing in one point: a subagent reports back
through a summary, and a summary is lossy by construction, so anything it learned that did
not fit the report is gone at handoff unless it was written down. That is precisely the gap
this decision exists to close, so it is what the primer says.

One thing is deliberately not assumed. **Whether a subagent is handed its parent's
`session_id` or one of its own decides whether session memory is shared across the spawn
for free.** If it matches, a subagent's writes land in the parent's file and sharing needs
no further work; if it does not, a parent id has to be threaded through explicitly. Rather
than guess, the hook records the session id it was handed along with `agent_id` and
`agent_type`, so the first real probe run answers it by comparing those records against the
`session-start` ones.

The envelope is the whole risk here, and it fails silently: bare stdout is discarded on this
event while `SessionStart` accepts it, so the habit formed there actively misleads. The E2E
test therefore asserts the parsed envelope rather than the exit code — verified by setting
`hookEventName` to `SessionStart`, the exact copy-paste mistake this invites, and confirming
the test fails.

### D12 — Adoption evidence from two sibling tools already on this machine

Both were inspected directly (2026-08-04). The findings bear on the spec's central
premise more than anything else in this plan.

**Structured authoring is the part that goes unused.** MemPalace holds 67,936 drawers of
unstructured capture. Its knowledge graph — append-only facts, supersession,
invalidation, timeline, i.e. structurally the closest analogue to *all of Engram §4* —
reports `{entities: 0, triples: 0, current_facts: 0, expired_facts: 0}`. Never called
once, in a mature installation, despite the tool embedding its own usage protocol in
every response. Meanwhile its "diary" room is the only room present in all 18 wings,
including wings whose entire contents are diary entries.

Set beside Codebase-Memory — auto-indexed, asks nothing of the model, 41 projects and up
to 112k edges — a mechanism appears:

| Asks of the model | Example | Usage |
|---|---|---|
| Nothing | auto-indexed code graph | heavy |
| Almost nothing | freeform diary note | universal |
| Distill, pick a predicate, justify a revision | structured fact + supersession | **zero** |

**Adoption appears inversely proportional to what the tool demands of the model**, and
Engram's authoring path is the most demanding of the three. This does not make the design
wrong, but it moves the riskiest assumption: *"will the agent call recall"* is the
question M0 was built to answer, and *"will the agent author structured facts"* is
probably the harder one. Both must be measured separately — a tool can look adopted in
aggregate while its authored core sits at zero. The probe already reports recall,
remember, and digest independently; that separation is load-bearing, not cosmetic.

**Every memory tool on this machine needed an external intervention to get used.**
Verified from `~/.claude/settings.json`: the only configured hooks are a SessionStart
reminder and a `PreToolUse` **gate that blocks `Grep|Glob`** until memory is consulted —
both hand-built for Codebase-Memory. MemPalace ships only capture hooks
(`Stop`/`SessionEnd`/`PreCompact`), no injection, which is why the user's global
`CLAUDE.md` carries a hand-written instruction to search it before answering.

Spec §6.3 states the primer "is the reason the LLM actually uses the tools." That is a
hypothesis, and the evidence here says it is probably insufficient on its own. The
intervention that demonstrably worked is the strongest one: a hard gate. So M0 should
baseline primer-only *first* — a clean measurement of the weakest lever — and treat a
`PreToolUse` gate as a planned, already-proven escalation rather than a last resort. The
spec text should be softened to match the plan's own skepticism.

**Three of our decisions are confirmed by other people's production scars**, which is
the cheapest possible validation:

- A **writer-lease** design produced exactly the liveness bugs D4 predicted, across five
  patch rounds, plus a hook that deadlocked against an open database client and left the
  host waiting on it. D4 rejected leases and forbade hooks touching the database; treat
  that as a validated prediction rather than a hypothetical.
- **Hardcoded and singleton path bugs** hit production three separate ways — a module
  singleton reading the wrong database under a rotating path, a hardcoded filename
  ignoring configured path, and symlinked/case-variant paths producing duplicate caches.
  D7's lint test is the mechanical check that catches that class at build time.
- A **contradiction detector documented as live but never wired up** was caught by public
  audit. D5 cut ours and said so; that is the right shape.

One place we are already better and should protect it: Codebase-Memory's `index_status`
returns no timestamp and no freshness signal at all. Engram's `file_state.blob_sha` /
`indexed_at` design (§5.3) is a genuine improvement over a real, actively used tool.

### D13 — Distribution: a build artifact cannot ship in a remote marketplace

Engram ships as a Claude Code plugin (marketplace at the repository root, plugin under
`plugin/`). The original arrangement worked locally: a build script published the AOT
binary into a gitignored `plugin/bin/`, and `.mcp.json` and `hooks.json` reached it
through `${CLAUDE_PLUGIN_ROOT}`.

That arrangement does not survive the move to a **remote** marketplace, which is the
intended end state. Claude Code clones the repository, so anything the manifests
reference must be committed — but committing a ~11 MB binary for each of four runtime
identifiers, on every version bump, makes the history unusable in a handful of releases.
There is also no documented mechanism for selecting a per-platform path, so `.mcp.json`
can name exactly one `command`.

**This is now built, and it was a correctness fix before it was a distribution one.** The
bundled binary had a defect nobody was looking for, found by reasoning about what
installing the plugin would do to the daemon already running.

A bundled copy lives under the version-pinned cache, so its path changes on every version
bump: `…/0.2.0/bin/engram` becomes `…/0.3.0/bin/engram`. D14's daemon proves ownership by
executable path before it will signal anything — correctly, since that is what stops it
killing a recycled pid. After an upgrade the new binary therefore cannot recognise the
running server as its own. It declines to replace it (right), fails to bind the port
(inevitable), asks `/health`, gets the *old* version back, and returns
`PortHeldByStranger`. `ensure-server.sh` swallows that by design. **The old daemon runs
forever, the new one never starts, and memory silently disappears after an upgrade** —
precisely when someone is most likely to blame the new version for something else.

A stable install path removes the failure rather than defending against it. With
`~/.local/bin/engram` the path does not change across upgrades, so ownership resolves, the
version check finds a stale daemon, and it is replaced exactly as intended.

So the plugin now ships **no binary at all**. `hooks/resolve-engram.sh` locates one —
`$ENGRAM_BIN`, then `$HOME/.local/bin/engram`, then `PATH` — and `hooks/engram-exec.sh`
`exec`s it. PATH is checked last on purpose: a hook inherits whatever environment launched
Claude Code, and a GUI launch can carry a minimal PATH that never included `~/.local/bin`.
`.mcp.json` needed no change, since it names a URL rather than a command — a second,
unplanned dividend of D14.

The two constraints below were written for a launcher that *fetches*. This one resolves
instead, which satisfies both trivially: no network on any path, and the hot path costs
three `stat` calls. They still apply the day fetching is added.

- **Pin the binary version inside the launcher.** The launcher is versioned with the
  plugin; the binary it fetches is not. Naming the exact expected version is what makes
  skew impossible rather than merely unlikely (the same reasoning as D1's protocol
  version between core and sidecar).
- **Never fetch on the hot path.** `file-touched` has a sub-10 ms budget (D4); a
  launcher that might download 11 MB cannot sit in front of it. Bootstrapping belongs in
  `SessionStart`, which runs once per session and can report a missing binary as
  `additionalContext` — a silent failure here would look exactly like the agent choosing
  not to use memory, which is the one confusion D12 says we cannot afford.

The cost is that the plugin is no longer self-contained: installed from a marketplace with
no binary present, it does nothing. That is why the missing-binary case is the one thing
`ensure-server.sh` says out loud. Every other hook fails silent — a hook that errors is
worse than one that no-ops — but `SessionStart` has a channel that reaches someone, and
silence there is indistinguishable from memory simply not working, which D12 names as the
one confusion we cannot afford. The E2E test asserts that message appears, and asserts the
success path stays completely silent, since anything printed there becomes a line of the
agent's prompt every session.

### D14 — A supervised local HTTP server replaces stdio

Engram serves MCP over local HTTP from a long-lived process managed by `engram start`,
`engram stop`, and `engram status`. The stdio transport is retired, not kept as a
fallback.

**Why, in order of weight.** A per-session stdio process cannot hold anything warm: at
M4 a ~640 MB GGUF embedding model would reload on every session, which is fatal to §7's
design. Claude Code reconnects HTTP and SSE servers — five attempts with exponential
backoff mid-session, three on initial connect — but **does not reconnect stdio servers
at all**, so today a crashed server is silently gone for the rest of a session and looks
identical in telemetry to the agent losing interest. And the streamable-HTTP transport
lets the *server* mint an `Mcp-Session-Id` at `initialize`, which is a real per-session
identity rather than the process-lifetime proxy we have been leaning on.

Write concurrency is explicitly **not** the motivation; D4 already handles that.

> **Amends spec §2.2**, which requires "zero services: no daemons required for core
> operation." That is no longer true, deliberately. The cost is a lifecycle to supervise
> and a crash that takes every session with it rather than one; the benefits above are
> judged to outweigh it, and `status` plus auto-start are what make it tolerable.

**Port.** A fixed default, overridable through the plugin's `userConfig`. Ephemeral
ports are impossible here — `${VAR}` in `.mcp.json` resolves from the environment Claude
Code itself was launched with, so a port chosen afterwards cannot be expressed — but
ephemeral ports were never a requirement. `engram start` fails loudly when the port is
occupied by something that is not us.

**Exposure.** Bind 127.0.0.1 only, and reject requests carrying an `Origin` header. A
loopback listener is reachable by local processes that cannot read the filesystem, so
"it's only localhost" is not on its own a sufficient posture. An optional bearer token
may be supplied later through `userConfig` with `sensitive: true`, which stores it in the
OS keychain rather than in `settings.json`.

**Identity and staleness.** `~/.engram/engram.pid`, mode 0600, holding pid, port, start
time, and version. The PID file is a hint; identity must be proven before any process is
killed, because "kill whatever holds the port" and "kill our own wedged server" are
identical in code and very different in consequence.

| PID file | Process | Identity | Health | Action |
|---|---|---|---|---|
| absent | — | — | — | start |
| present | dead | — | — | stale file → remove, start |
| present | alive | ours | answers with our pid+version | already running → exit 0 |
| present | alive | ours | no answer, or wrong version | **orphan → kill, clean up, start** |
| present | alive | **not ours** | — | PID was reused → remove file, start; **never kill** |
| — | — | — | port held by a stranger | report clearly; **never kill** |

Identity is proven without asking the process anything — the executable path matches ours
*and* the recorded start time matches the live process's — because the orphan case is
precisely a process too wedged to answer a health check. Health then separates
alive-and-well from alive-and-wedged, once identity is already settled.

Two concurrent `engram start` invocations need no lock: whoever binds the port wins, and
the loser finds a healthy server and exits 0. The OS already provides the mutex.

**Starting it is the plugin's job, not the user's and not the agent's.** The
`SessionStart` hook spawns the daemon detached and does not wait on readiness; Claude
Code retries an initial HTTP connection three times with exponential backoff, which
comfortably covers the gap. Nothing asks a human to run a command in the normal path.

An `http`-type MCP entry has no `command`, and `alwaysLoad` only blocks on *connecting* —
it never starts anything. So the process must be brought up out of band, and the hook is
the confirmed, stable way to do it.

*Considered and rejected: plugin **monitors*** (`monitors/monitors.json`), which Claude
Code does start at session start and on reload. Three disqualifiers, all confirmed: every
line a monitor writes to stdout is delivered to Claude as a **notification**, so a server
would spam the conversation; monitors explicitly **cannot reference `${user_config.*}`**,
which is where the port and any token live; and they carry **no documented crash-restart**,
are marked experimental with a schema that may change, and need a full session restart
rather than `/reload-plugins` to pick up changes. LSP servers are the only component with
a real supervision contract (`restartOnCrash`, `maxRestarts`, `shutdownTimeout`) — worth
remembering if host-owned supervision ever becomes worth the abuse of declaring a
non-LSP process as one.

Regardless of transport, the server must **never log to stdout** — file only. That is a
hard constraint under monitors and merely good hygiene otherwise.

`engram_start`, `engram_stop`, and `engram_status` are also exposed as MCP tools so the
agent can manage the server mid-session. They cannot bootstrap a cold start — if the
server is down, its own tools are unreachable — so their descriptions must say what they
are for (restart, deliberate stop, health) rather than implying they can revive it.

**The adoption denominator changes.** One `server-start` record per session was only ever
valid because stdio gave one process per session. A daemon starts once, so that count
collapses to 1 and the probe would silently under-report. It is replaced by a
`session-open` record emitted whenever the server mints a new `Mcp-Session-Id` — exactly
one per Claude Code session, and a real identity rather than a proxy.

**Verified, with one dependency worth stating plainly (2026-08-04).** A throwaway AOT
spike confirmed `ModelContextProtocol.AspNetCore` 2.0.0 on
`WebApplication.CreateSlimBuilder` publishes with **zero trim warnings**, produces a
16.6 MB native binary linking only system frameworks, and serves a full
`initialize` → `tools/list` → `tools/call` handshake from the published binary.

But `HttpServerTransportOptions.Stateless` **defaults to `true`** as of the 2026-07-28
protocol revision, and in stateless mode the server mints **no session identity at all** —
the first spike run produced no `Mcp-Session-Id` on any response. Everything above that
depends on that header requires `WithHttpTransport(o => o.Stateless = false)` set
deliberately.

That is a real dependency, not a formality. The SDK describes stateful mode as *"a
back-compat-only escape hatch for legacy clients"* (SEP-2567), so this design rests on a
mode the ecosystem is moving away from. In stateless mode nothing distinguishes one Claude
Code session from another at the transport layer, because Claude Code does not send its own
session id to MCP servers (§D12 research). Worth revisiting whenever the SDK version moves.

**Noted, not built: the primer could carry the session id instead.** The `SessionStart`
hook already receives Claude Code's real `session_id` on stdin. The primer could name it,
and the agent could pass it back on `remember` and `recall` — no transport involvement, so
it survives a stateless-only future.

It is also, on its face, the better design. Today's two id-spaces do not join: hooks record
under Claude Code's `session_id`, the server under its own. That split is why compaction
survival is a timestamp heuristic, why the probe carries two separate session counts, and
why the session-memory primer line had to be deleted as structurally dead. `Mcp-Session-Id`
does not close it — it is a *third* id. A primer-carried id would be the same one the hooks
use, making all three of those exact rather than approximate, and it extends to subagents
through the `SubagentStart` primer.

Its cost is the reason it is not being built yet: it depends on the model actually passing
a parameter, and D12's whole finding is that every demand made of the model is somewhere
adoption leaks. It would have to be optional, validated against session ids the hooks have
registered, falling back to `Mcp-Session-Id` when absent or unrecognised. Whether the model
reliably passes parameters is something M0's telemetry can answer, so this waits for
evidence rather than being adopted on the strength of the argument.

#### How the plugin reaches the daemon, and what is still unverified

`plugin/.mcp.json` is an `http` entry pointing at `http://127.0.0.1:7433/`. Nothing in that
file can start a server, so `SessionStart` runs `hooks/ensure-server.sh` ahead of the primer
hook. That script exists rather than a bare command for two reasons, both of which would be
silent faults: on `SessionStart` anything a hook prints to stdout is injected into the
model's context as `additionalContext`, so `engram started (pid 1234, port 7433)` would
become a line of every session's prompt; and a memory server that fails to start makes a
degraded session, not a broken one, so the script always exits 0.

The cost is measured, not assumed: **cold start 132 ms, warm start 16 ms, status 15 ms.**
Since the daemon outlives the session that started it, only the first session after a reboot
pays the cold number, and 132 ms is far inside any hook budget. This was the one number that
could have sunk the design — a hook that has to wait on a server is a hook that can time
out — and it does not.

Two things are built from documentation and remain unconfirmed against a running Claude
Code. Both fail closed:

- **Ordering.** Whether the MCP client connects before `SessionStart` hooks finish. If it
  does, the first session after a reboot loses its memory tools; every later session finds
  the daemon already up. Worst case is one degraded session, not a broken install.
- **`Origin`.** The server rejects any request carrying an `Origin` header, which is the
  standard DNS-rebinding defence for a localhost server. `Origin` is a browser concept and
  Node's `fetch` does not add one, so this should never fire — but if Claude Code does send
  it, every call returns 403. Verifiable in one session with `claude --plugin-dir`.

Cold start is structurally out of reach of the `engram_start` MCP tool: a tool call cannot
arrive when there is no server to receive it. That tool therefore reports and repairs a
missing or disagreeing pid file rather than starting anything, and starting the daemon stays
the hook's job. `engram_stop` schedules its shutdown ~500 ms out so the reply survives the
connection it travels on.

### Reading the M0 numbers honestly

Two documented behaviours distort the adoption metric, and both must be known before
anyone concludes anything from it.

**A crashed server is invisible.** Claude Code does not automatically reconnect stdio
MCP servers. `server-start` has already been recorded by the time a crash happens, so a
session whose server died halfway looks exactly like a session where the agent stopped
bothering. Low adoption is therefore not evidence of disinterest until a crash has been
ruled out — and optimizing primer wording for a process that was not running is a very
easy way to waste a fortnight.

**Web sessions may start servers lazily.** The eager-start guarantee that makes
`server-start` a valid denominator is documented for interactive local sessions. A web
session can start a plugin server on demand after an idle wake, which would under-count.
The probe reports MCP and hook session counts separately partly for this reason.

### D15 — Durable guidance belongs in tool descriptions; only volatile guidance in the primer

Prompted by `claude-mem`, which exposes an always-visible `important_workflow` tool
carrying its retrieval protocol as prose. The mechanism does not transfer, but the
observation behind it does.

**Why the mechanism does not transfer.** `claude-mem` needs that tool because its
retrieval is a three-call protocol — `search` returns a stub index (~50–100 tokens per
result), then `timeline`, then `get_observations` for the bodies. `important_workflow`
exists to teach a *sequence*, and the sequence exists because their `search` is
deliberately not self-sufficient. `engram_recall` answers in one call. There is no
intermediate state to disclose, so a fifth always-visible tool would spend permanent
definition budget describing a workflow one step long.

**What does transfer.** Tool descriptions persist for the whole session; primer text
injected at `SessionStart` is ordinary context and is summarized away by compaction.
Today `PrimerBuilder` puts both kinds of guidance in the volatile channel.

Decision: **split the primer by lifetime.** Guidance that is true in every session —
memory is cheap, prefer recall before exploring, flush with `engram_digest` — moves into
the existing `engram_recall` and `engram_remember` descriptions, which already run 403
and 715 characters and cost their tokens whether or not they carry it. Guidance that is
true only of *this* session — the live fact count and the topic-coverage line — stays in
the hook, because it cannot be a static string.

Cost accepted: tool descriptions become prompt-engineering surface and need the same
golden-file treatment D9 gives the recall output contract. A description edit is an
interface change, not a comment.

### D16 — A timeline view over session provenance, gated on a density check

The supersession chain is **truth-history**: what a belief became. It cannot answer
**time-history**: what else was being learned when this was learned. Facts recorded in
one session are often causally related in ways no predicate captures, and that
co-occurrence is already stored as session provenance.

Decision: expose it as a *view* on the existing expand path — an anchor fact plus its
session neighbours before and after — not as a new top-level tool. This is a query over
data already written; it requires no schema change, and it does not widen the tool
surface D17 budgets.

**Gated, not scheduled.** `claude-mem` anchors its timeline on *observations* — tool-use
events, which are dense and naturally chronological. Engram stores *facts*, which are far
sparser: a session may produce three. A neighbour window over three facts is not context,
it is noise. Before this is built, measure the real distribution of facts-per-session on
the author's own instance. If the median session yields fewer than roughly five facts,
the view does not earn its tokens and this decision lapses.

**Measured, and lapsed.** The author's instance, replayed into a scratch home so the real
one was not touched: **7 sessions had written a fact, 34 facts between them, distribution
`[1, 1, 1, 1, 2, 8, 20]` — median 1 against a gate of 5.** Not marginal, and the mean
(4.9) misses too. Seventeen sessions had run in total, so across *all* sessions the median
is 0; the reported figure is already the generous one, because a session row exists only
once that session has written.

The shape is the interesting part, and it is not "sessions are uniformly thin" — it is
bimodal. One design-heavy session produced 20 facts and would make a fine timeline; five of
the seven produced one or two and would produce a window containing only the anchor. A
median gate is the right test for exactly this: the view's value is concentrated in rare
sessions, and building it would mean paying tokens on the tool surface (D17) in every
session to serve the few.

What would reopen this is density, not time — if a later milestone makes facts routine
rather than deliberate (digest actually persisting, or a capture path wider than
first-person statements), re-run the measurement before assuming the answer still holds.
`engram probe` reports the distribution now, so re-running it is one command rather than a
study.

### D17 — The tool surface is a per-session token cost with a stated ceiling

Measured 2026-08-05: the four memory tools in `EngramMcpTools.cs` cost **2,399 characters
of definitions** (`recall` 521, `remember` 1,148, `forget` 345, `digest` 385 — description
plus schema), roughly 600 tokens, paid on every session whether or not memory is used.
Three more tools ship alongside them (`start`, `status`, `stop`).

`claude-mem` cut 9 overlapping tools (~2,500 tokens) to 4. We are already at 4 memory
tools and they do not overlap, so there is no consolidation to do — but the cost was
never an explicit line item, which is how a surface grows to 9 without anyone deciding to.

Decision: **tool-definition token cost is tracked, with a test.** A tier-1 test asserts
the total serialized `tools/list` payload stays under a stated ceiling; raising the
ceiling is a deliberate edit with a rationale, the same treatment D9 gives hook latency.
Any new tool must argue its cost against the alternative of a parameter on an existing
one — which is why D16 is a view on expand rather than a `timeline` tool.

> **Supersedes an earlier reading.** An analysis in session prep claimed ten tools and
> proposed cutting `engram_start` as unreachable. The count was wrong, and D14 has already
> settled the second point on better grounds: `engram_start` is retained precisely
> *because* a tool call cannot arrive with no server to receive it, so it reports and
> repairs a disagreeing pid file instead of starting anything. It is not dead weight; it
> is a different tool than its name suggests.

### D18 — Semantic retrieval is a local `sqlite-vec` lane, never a vector service

M4 already names `IEmbedder`, a `sqlite-vec` lane, and RRF fusion. This records *what it
buys and what it must not become*, because "add a vector database" is the version of this
that quietly costs a second daemon.

**What semantic search buys: vocabulary mismatch.** FTS5 matches tokens. It cannot connect
a query to a fact that records the same thing in different words — *"what's my kid's
name"* against a stored *"son is Liam"*, or *"how do we avoid database lock errors"*
against *"every write is `BEGIN IMMEDIATE`"*. This is the dominant failure mode for a
memory system specifically, because the user writing the query and the agent that wrote
the fact chose their words in different sessions, months apart, with no shared vocabulary.
A missed recall here is invisible — the agent simply explores files instead, and the
telemetry in D12 records it as disinterest.

**What FTS5 keeps winning, which is why this is a lane and not a replacement.** Embeddings
blur rare tokens, and this domain is full of them: `SQLITE_BUSY_SNAPSHOT`, `EngramHome`,
`BEGIN IMMEDIATE`, RIDs, file paths, error codes. Exact-identifier recall is where lexical
search is not merely adequate but strictly better. Fusing the two ranks by **RRF**
(`Σ 1/(k + rank)`, k≈60) rather than by blended scores, because BM25 rank and cosine
distance are not on comparable scales and any normalization between them is a tuning knob
nobody will ever have evidence to set.

**Why not ChromaDB.** It is a separate Python service. Adopting it would mean a second
supervised process, a Python runtime in the dependency chain, and a network hop — against
a design whose core is a single Native AOT binary (D1) that already holds the SQLite file
open. `sqlite-vec` is a loadable extension over that same file: one process, one database,
one backup, and query plans stay visible per the no-ORM rule. Embedding *generation* rides
the llama.cpp that D1 already side-loads into `~/.engram/lib/`, behind
`engram init --with-embeddings`.

**Model: `Qwen/Qwen3-Embedding-0.6B`, GGUF quantized, 1024 dimensions.** This is not chosen
from a benchmark table — it is running prior art. A separate project of the author's already
executes exactly this stack in production: Qwen3-Embedding-0.6B in-process through
**LLamaSharp**, no external API and no Ollama hop, feeding a **`sqlite-vec`** store. It
replaced `nomic-embed-text` there specifically because nomic underperformed on **mixed
code-and-prose content**.

That rationale transfers with unusual force. Most systems embed one kind of text; Engram
embeds prose beliefs *and*, once D24 lands, code facts extracted from C# and TypeScript —
into the same store, retrieved by the same query. Mixed code-and-prose is not an edge case
here, it is the corpus. An evaluation someone already paid for, on the exact content shape,
beats a fresh comparison of general-purpose sentence encoders.

Three consequences of the larger model, all absorbed by decisions already made:

- **Storage**: 1024 dims at float32 is ~4 KB per fact, so ten thousand facts cost ~40 MB.
  Irrelevant at this scale. Int8 quantization to ~1 KB is available and not needed yet.
- **Download**: a quantized 0.6B model is a few hundred megabytes, against ~90 MB for a
  small sentence encoder. Acceptable *only* because it sits behind
  `engram init --with-embeddings` and because FTS5-only remains a fully supported
  configuration rather than a degraded one — a promise this makes load-bearing.
- **Latency**: 0.6B parameters cost meaningfully more per embedding than a 22M encoder.
  Also absorbed: embedding already runs server-side and off the write path, so this is
  throughput on a background queue, not latency on a hook.

**Providers: LLamaSharp in-process by default, LM Studio or Ollama as alternatives.** The
same prior-art system supports all three behind one seam, which is what M4's `IEmbedder`
already anticipated. Engram takes the same shape, for two reasons beyond parity.

First, it is free capacity. A user already running Ollama or LM Studio has the model weights
and the runtime; pointing at them avoids a second several-hundred-megabyte download for no
gain. Second — and this is the part that changes the risk profile — **an HTTP provider is
trivially AOT-safe**. If LLamaSharp proves hostile to Native AOT, the vector lane does not
die with it; it ships with the in-process provider unavailable and the local-server
providers working. That converts the riskiest item in M4 from a blocker into a degraded
mode.

This does not reopen D20. Ollama and LM Studio are reached over **localhost**, so nothing
leaves the machine and the local-only guarantee holds — the objection to ChromaDB was never
that it spoke a protocol, it was that it was a *required* second daemon dragging a Python
runtime behind it. D20's line governs here unchanged: Engram must be complete with none of
them installed, which is what the in-process default and the FTS5-only fallback are for.

**The invariant that makes providers safe, and the quiet way this breaks.** Vectors from
different models are not comparable. Cosine distance between a Qwen3 embedding and a
`nomic-embed-text` embedding is a real number, it is meaningless, and nothing about it looks
wrong — retrieval simply degrades into confident nonsense. Dimensions differ too (1024 here,
768 for nomic, 384 for the small encoders), so a provider change can invalidate the
`sqlite-vec` table itself rather than merely its contents.

Therefore the vector index records **which model and which dimension produced it**, as index
metadata rather than per row. A provider or model change is detected, not assumed away:
`doctor` reports the mismatch, and `embed --rebuild` is the remedy. This sits cleanly inside
existing rules — the index is derived state, so D8 permits `repair` to rebuild it, and D23's
`regenerable` marker already distinguishes what may be discarded. `engram explain` (D21)
reports the model that produced each vector, because "which embedding space is this" is
exactly the sort of question an unexplainable ranking hides.

**Embeddings are derived state, and that settles three things at once.** Under D8 they are
repairable, so `repair` may rebuild the vector index and `embed --rebuild` is not a special
case. Under D4 hooks still never open the database, so embedding happens in the server,
off the write path, asynchronously. And a fact written but not yet embedded is still found
by FTS5 — the lane degrades to lexical-only rather than to a missing result, which means
backfill latency is never a correctness bug.

Cost accepted: recall output becomes model-dependent, so tier-2 tests need a deterministic
stub `IEmbedder` and must assert set membership rather than exact ordering. An install
without `--with-embeddings` runs FTS5-only and must stay a supported configuration, not a
degraded one — which also keeps the default install free of a model download.

D5 (contradiction detection, cut from v1) is reconsidered here and not before: detecting
that two facts contradict requires knowing they are *about the same thing* in different
words, which is the same capability, and attempting it lexically is why it was cut.

### D19 — Facts carry a provenance tier, and it is authored, not derived

Imported from Graphify, which tags every edge `EXTRACTED` (read from an AST) or `INFERRED`
(resolved by heuristic or model) so a consumer can tell what is grounded from what is
guessed. Engram has the raw material for this distinction and does not expose it.

The problem is concrete. Recall output is read by a model, and that model currently cannot
tell *"Jim said this, in these words"* from *"an agent concluded this from a diff."* Those
warrant different trust and sometimes different action — one is a premise, the other is a
hypothesis worth re-checking. Collapsing them is a silent correctness hazard in exactly the
situations where memory matters most.

**Three tiers, ordinal, no confidence score.**

| Tier | Means | Re-checkable? |
|---|---|---|
| `stated` | The user asserted it. Authored by the human. | No — it is testimony, not observation |
| `observed` | Derived from an artifact that existed: a file, command output, a diff, an API response. Carries `evidence`. | Yes, against the artifact |
| `inferred` | An agent's conclusion. No artifact backs it directly. | No, but it can be re-argued |

A user statement rewritten by an agent for self-containment stays `stated`. The content is
still the user's claim; the supersession row already records that a rewrite happened, so a
fourth tier would encode something the history already carries.

**No numeric confidence.** Graphify attaches a score; we deliberately do not. A float is a
tuning knob, and nobody will ever have the evidence to set it — the same argument D18 uses
to reject score-blending in favour of RRF. An ordinal tier is enough to break a tie, and it
cannot be fiddled with.

**Provenance is belief content, so it is immutable and unrepairable.** It is written at
insert and never updated, alongside predicate, body, and validity. This has a consequence
worth stating plainly because it is exactly the kind of thing a later contributor will try
to be helpful about: **`repair` may never assign or correct a provenance tier.** Where a
fact came from cannot be re-derived from the fact itself, so under D8's derived/authored
line it sits firmly on the authored side. A `repair` that "fixes missing provenance" is
inventing testimony.

**No migration, because this landed before the store did.** The first draft specified a
nullable column meaning *unknown, predates the tier*, with no backfill by guessing. That is
now moot and the simpler thing is true instead: no database exists yet, so `learned_via` is
`NOT NULL` with a `CHECK` constraint from the first `CREATE TABLE`. There is no era of facts
without provenance and never will be. Folding this into `docs/engram-schema.sql` before M1
rather than after was worth more than the decision itself.

Two things in the authored schema had to be reconciled, both settled there:

- **`learned_via` already existed** with the values `stated | observed | derived | indexed`.
  Three of those are provenance and one is not — `indexed` describes where a row came from,
  which is regenerability, and is precisely the conflation D23 exists to prevent. It was
  already latent in the schema. `indexed` is gone; code facts are `observed` provenance with
  `regenerable = 1`. `derived` is renamed to `inferred`, because "derived" already means
  *regenerable* in D8's vocabulary and using it for a provenance tier in the same codebase
  invites exactly the mistake.
- **`confidence REAL NOT NULL DEFAULT 0.8` is removed.** It is the numeric score this
  decision rejects, and its own default was the argument: a value nobody had evidence to
  set, applied uniformly to every fact, tuned by no one. Deleting it cost nothing because no
  row had ever been written.

**Surfaced, not scored.** The tier appears in recall output as a compact marker — one or two
tokens per fact, budgeted under D17 — and retrieval ranking does **not** consult it. Letting
provenance tilt salience would be a second tuning knob doing the first one's job. The model
sees the tier and decides; the ranker stays about relevance.

This gives D5 the thing it was missing. Contradiction detection was cut partly because two
conflicting facts offered no principled basis for choosing between them. `stated` over
`observed` over `inferred` is such a basis, and it does not require resolving the semantics
of the conflict — which is the expensive part. D5 is reconsidered alongside D18's vector
evidence, now with a tiebreak in hand.

### D20 — The code graph is built in-house; Graphify stays optional and unrequired

Two positions were considered. The first draft of this decision said *recommend and
coexist* — do not vendor Graphify, but point users at it, on the grounds that consuming an
MCP server is not really a dependency. **That reasoning was wrong and is corrected here.**

**An MCP server is a dependency with a protocol boundary in front of it.** If Engram's
documentation steers users to Graphify for code questions, and users come to rely on that,
Engram has taken on a Python service, an LLM egress path, and a pre-1.0 release cadence —
it has merely moved them where they do not show up in `pyproject.toml` or a `.csproj`.
Abstraction changes who notices the coupling, not whether it exists. Worse, a runtime
coupling is harder to audit than a declared one: nothing in the repository records it.

The concrete objections stand and are strengthened, not weakened, by being at arm's length.
Graphify is a **Python 3.10+ runtime** against a packaging thesis of one Native AOT binary
with no runtime (D1). Its `graph.json` is held in memory under a 512 MB cap, which does not
compose with a SQLite/WAL store several processes open at once (D4). It **sends documents,
PDFs, and images to a configured LLM**, which would puncture Engram's local-only guarantee
at a layer users could not see — and a guarantee broken by a *recommended companion tool* is
broken just the same. It is pre-1.0 at v0.9.33 with 201 releases in four months, 803 open
issues, and 70% of commits from a single author. And having no temporal model, its output
could not enter the fact store without a translation layer costing about what M3 costs.

Decision: **M3 is built in-house when its gate opens.** Engram never requires, invokes, or
assumes Graphify. The line that makes "optional" mean something:

> No Engram capability may work only when Graphify is installed. Engram is complete
> without it, or the feature does not ship.

Cost accepted, explicitly: M3 duplicates work that is freely available and four months
ahead. That is paid on purpose. A memory system that quietly needs a Python service to
answer code questions is not the single-binary local tool D1 promises, and the promise is
the product. Independence here is not pride; it is the thing being sold.

What "optional" reduces to in practice: a user who installs Graphify alongside Engram gets
both MCP servers, and the agent calls each directly. Engram neither knows nor cares. Facts
learned that way arrive through `engram_remember` at the `observed` or `inferred` tier per
D19 — the ordinary write path, coupling nothing, and identical to a fact learned from `grep`.

### D21 — Retrieval must be explainable, and the explainer ships with the vector lane

Graphify's stated principle is *"every edge explained"*, backed by a `graphify explain`
command. The principle transfers even though the implementation does not, and it lands on a
real and worsening gap.

Recall is already opaque. FTS5 rank and salience combine to produce an ordering nobody can
account for after the fact, and **D18 makes this materially worse**: RRF fuses two
independently ranked lists, so the reason a fact placed third becomes a function of its
position in two rankings that are themselves never shown. Adding a semantic lane without an
explainer means shipping a retrieval system whose failures cannot be diagnosed, only
re-rolled.

This bites hardest exactly where the project has staked its health metric. D12 and §12 make
**recall coverage** the measure of whether Engram works, which makes *missed recalls* the
unit of debugging — and a missed recall that cannot be explained cannot be fixed. Without
this, the response to "why didn't it find that?" is guesswork about primer wording, which
§"Reading the M0 numbers honestly" already identifies as a very easy way to waste a
fortnight.

Decision: `engram explain <query>` reports, per candidate fact — lexical rank and BM25
score, vector rank and cosine distance, salience contribution, the fused RRF position, and
the D19 provenance tier. Read-only, no side effects.

**Sequencing is the load-bearing part of this decision: the explainer ships with or before
the vector lane, never after.** A debugging tool added once the thing it debugs is already
in production is a tool built against remembered intentions rather than observed behaviour.
Concretely, this moves ahead of §6.1's step 8 (RRF fusion) and is a prerequisite for it.

This is also just the existing house rule one layer up. "No ORM. Hand-written SQL, so query
plans stay visible" is a commitment to legibility at the storage layer; D21 extends it to
the ranking layer, where the opacity is now greater and the consequences are quieter.

### D22 — The user can read everything stored about them, in one artifact

Graphify emits `GRAPH_REPORT.md` and `graph.html` beside its machine-readable graph. The
underlying gap is real here and sharper than it is for a code tool, because Engram's
contents are personal and were written **while the user was not watching**. Passive capture
is the entire point of the design — it is also what makes this obligation rather than
polish.

Nothing today enumerates. `doctor` reports health, not content. `recall` answers a question,
which means it shows what matched a query and is structurally incapable of showing what the
user never thought to ask about. So the current answer to *"what do you actually know about
me?"* is: run queries until you stop being surprised.

That interacts badly with `forget`. D7 and the local-only posture cover **confidentiality** —
data does not leave the machine. They say nothing about the user's **own access**, and
retraction without enumeration is not a real control: you cannot forget what you cannot find,
and a user who half-remembers an offhand remark has no way to check whether it was kept.

Decision: `engram report` — read-only, generates a Markdown artifact of everything stored.
Per fact: body, subject and predicate, validity window, D19 provenance tier, evidence, and
the identifier `forget` takes. Grouped by subject, and **including closed and superseded
facts, marked as such** — the history is the product, and it is also the part most worth
auditing, since a retracted belief that lingers in a chain is exactly what a user would want
to find.

Two constraints that are the decision rather than details:

- **No truncation, ever.** No top-N, no salience filter, no eliding the long tail. Every
  other output in this system is budgeted against a token ceiling; this one is not, because
  a report that silently omits is worse than no report at all when the purpose is audit. If
  it is long, it is long.
- **Markdown, not HTML.** No browser, no asset pipeline, no bundled viewer. It renders in a
  terminal, an editor, and a diff, and it survives being pasted into an issue. Graphify's
  `graph.html` suits a graph nobody can read as text; a fact store is already prose, and a
  visualization would be a second thing to maintain in service of no additional
  understanding.

Generated on demand and never stored, so it sits outside D8's derived/authored question
entirely — there is nothing for `repair` or `compact` to reconcile.

Ordering: this belongs with D19 rather than after it. Provenance is most of what makes the
report worth reading, and a report is the fastest way to find out whether the tiers were
assigned sensibly in the first place.

### D23 — `regenerable` is a separate axis from provenance, and `repair` keys off it alone

The code graph shares one database with the belief store — the sidecar returns
entities/edges/facts over stdio and the core writes them (D1), into the same file that
holds everything else. That sharing is the point: a standalone code graph answers *what
calls this function*, while code entities living beside beliefs answer *what did we decide
about this function, and what superseded it*. No snapshot tool can do the second.

It also creates a trap. D19's provenance tier and D8's derived/authored line look like the
same distinction and are not:

| | Regenerable? | Provenance (D19) |
|---|---|---|
| Code fact from an AST | **Yes** — recompute from the file | `observed` |
| Agent fact from command output | **No** — the output is gone | `observed` |
| Fact from a user statement | No | `stated` |

Two rows share a provenance tier and sit on opposite sides of D8. So an implementation of
`repair` that reads "rebuild the derived facts" as *"drop the `observed` ones and re-index"*
— an entirely reasonable reading — silently destroys every agent observation that can never
be recovered. That is precisely the failure the derived/authored rule exists to prevent, and
D19 as written makes it easier to reach, not harder.

Decision: facts carry a **`regenerable`** marker, set at insert, immutable, and wholly
independent of the provenance tier. It asserts one thing — *this fact can be recomputed from
an artifact that still exists* — and only the code indexer sets it. **`repair` and `compact`
key off `regenerable` and must never consult provenance.** Provenance describes how much to
trust a fact; `regenerable` describes whether destroying it loses anything. Ranking uses
neither.

Deletion of a source file does not retroactively clear the marker. The fact is not
destroyed and not rewritten — it is flagged stale by M3's existing stale-subject mechanism,
which is the same treatment D2 gives a moved entity. Append-only survives intact.

The guard, per the house rule that an unfalsifiable test is worthless: assert that `repair`
removes regenerable facts and leaves everything else, then **prove it by flipping one agent
observation to `regenerable` and confirming `repair` would consume it**. A test that passes
because nothing was ever at risk has demonstrated nothing.

### D24 — Three analyzer tiers, one language registry, C# then TypeScript/JavaScript

Priority is set by what actually gets worked on: **C# first, TypeScript/JavaScript second,
everything else behind a registry row.** That is a delivery order. The architecture below is
what keeps it from becoming a ceiling.

**Three tiers, distinguished by what they cost, not by what they parse.**

| Tier | Mechanism | Cost | Gets us |
|---|---|---|---|
| **0 · Universal** | Managed, in-core, no dependencies | None | Works on any file, including ones we have never heard of |
| **1 · Syntactic** | `tree-sitter` via `NativeLibrary.Load` from `~/.engram/lib/`, one grammar per language | One native lib, one grammar file per language | Definitions, references, imports/exports, structure |
| **2 · Semantic** | A language's own toolchain, in a sidecar that never opens the database | A whole extra binary and its ecosystem | Real type and overload resolution |

Tier 1 is the one that makes languages cheap. `tree-sitter` is a C library loaded the same
way `sqlite-vec` and llama.cpp already are under D1, so a new language costs a grammar and a
registry row — not a runtime, not a sidecar, not a dependency in the shipped artifact.

**Tier 2 is reserved, and TypeScript does not get it.** C# gets Roslyn because D1 already
commits to that sidecar and it is the language this project is written in. TypeScript's
equivalent depth would mean the TypeScript compiler API, which means **a Node runtime** —
and D20 has just finished rejecting exactly that shape of coupling for Python. The same
argument binds here or it was never an argument. TS/JS therefore ship at tier 1, and if
telemetry later shows syntactic extraction genuinely insufficient, that is a decision taken
then, on evidence, with the Node question faced honestly rather than smuggled in.

**One registry, and adding a language edits nothing else.** A single table declares, per
language: id, file extensions, grammar, tier, and which extractors apply. Adding a language
is one row plus a grammar file, with **zero edits to the indexer, the CLI, the report, or
the test harness**. If adding a language requires touching a `switch`, the abstraction has
not landed and the row is decoration.

Two constraints that make this real rather than aspirational:

- **The registry is a static table, not discovery.** Native AOT cannot scan assemblies for
  implementations, so registration is explicit and compile-time. This is a constraint worth
  welcoming: it keeps the enumerable kind enumerable and greppable.
- **The conformance suite iterates the registry.** One fixture-driven set of assertions runs
  for every entry — extraction produces entities, entities resolve, re-indexing an unchanged
  file is a no-op, a renamed symbol keeps its `entity.id` per D2. A harness carrying its own
  copy of the language list is the exact failure this decision exists to prevent, and a lint
  test should say so.

Every fact these analyzers write is `observed` (D19) and `regenerable` (D23), without
exception. That is what makes a full re-index safe.

### D25 — Native AOT is a per-process requirement, not a project-wide one

Raised while weighing what to do if LLamaSharp proves AOT-hostile: AOT is a means, and if
it costs us the embedding stack we should ask what it was actually buying and whether that
can be bought another way. Taking the question seriously produces a better answer than
either "keep AOT and drop LLamaSharp" or "drop AOT".

**What AOT gives, and what survives without it.**

| What it buys | Replaceable? |
|---|---|
| Single file, no runtime installed | **Yes** — self-contained deployment gets there. Costs artifact size, not correctness |
| Steady-state throughput | **Not a reason to keep it.** After warmup, tiered JIT is frequently *faster* than AOT |
| Memory footprint | **Mostly** — matters least, since the daemon is long-lived |
| **Cold start** | **No.** ReadyToRun narrows the gap; it does not close it |

Only the last one is load-bearing, and only in one place. The MCP server is a supervised
long-lived daemon (D14), so it pays startup once per launch and JIT warmup is amortized into
nothing. Recall runs inside that daemon. Neither cares much.

**The hook does.** `engram hook file-touched` is a fresh process on every file touch, and
its budget must hold unconditionally rather than only when nothing else is writing (D4).
§1.5 measured the gap directly: **3.44 ms median for the AOT build against 18.51 ms
self-contained without it.** The non-AOT build spends most of a budget's worth of time
before doing any work, and no configuration recovers that, because the cost is process
start itself. That single path is why AOT stays.

> **The 10 ms target is asserted, not derived.** It enters at `docs/engram-spec.md:532` as
> one entry in a list of performance targets, with no measurement, citation, or reasoning
> attached, and everything downstream — D4, this decision, `HookCommand.cs` — inherits it
> from there. It is *plausible*: `file-touched` fires per edited file, runs inside the
> agent's turn rather than in the background, and a multi-file edit multiplies it, so
> keeping the aggregate under a perceptible ~100 ms implies roughly this per event. But
> that derivation is reconstructed here, not recorded there.
>
> This matters because the ratio, not the threshold, is what actually decides D25: AOT is
> ~5.4× faster to start, and that holds whatever the true target turns out to be. If the
> budget were re-derived at 25 ms, non-AOT would clear it and this decision should be
> reopened. Someone should either write down where 10 ms came from or measure what
> `file-touched` latency users actually notice.

So the boundary is not *this project is AOT*. It is **the latency-critical process is AOT,
and everything else may be whatever it needs to be.** D1 already drew exactly this line for
Roslyn: MSBuild and MEF are hostile to trimming, so they went in a sidecar and the core
never links them. LLamaSharp takes the same escape hatch on the same reasoning, and needs no
new architecture to do it.

One difference from the Roslyn sidecar matters. Roslyn is spawned per batch and a ~300 ms
start amortizes fine. An embedder cannot work that way — loading several hundred megabytes
of weights per batch would dominate everything. The embedder sidecar is therefore
**long-lived**, a supervised child of the D14 daemon, started on demand and kept warm.

**The practical consequence: M4 does not have to answer the AOT question to ship.** The
order of attack is now a ladder, cheapest and least risky first:

1. **Localhost providers** (Ollama, LM Studio) — no AOT exposure, no process to supervise,
   and per D18 already a supported configuration.
2. **In-process LLamaSharp** — if it turns out AOT-clean, this becomes the default and the
   ladder stops here.
3. **An owned embedder sidecar** — only if LLamaSharp is AOT-hostile *and* we want embedding
   that does not require the user to install someone else's runtime.

Step 3 exists because D20's rule binds here too: an opt-in feature that works only when a
third-party tool is installed is weaker than one whose dependency we ship. It is third on
the ladder because it is the most work, not because it is optional forever.

This demotes the AOT spike from gating to informative: it decides *which rung* M4 lands on,
not whether M4 happens. And it stays falsifiable — if the 10 ms hook budget is ever shown
achievable without AOT, on measurement rather than argument, this decision should be
reopened, because at that point AOT would be buying only artifact size.

### D26 — User captures are ordinary facts, and forgetting is not scoped by origin

Captured user statements shipped in their own JSON directory, with their own `ReadActive`
walking `retracts` and `supersedes` links to work out what still stood. That was a **second
implementation of the validity window** the `fact` table already had, and the two agreed
only by coincidence. It also got none of `BEGIN IMMEDIATE`, `busy_timeout`, or a
supersession row saying *why* a belief changed. Same data, one temporal model.

**The statement is its own subject.** A capture lands at
`/user/{about-you,instructions}/<fingerprint>`, where the fingerprint is eight hex
characters of SHA-256 over the statement with case, punctuation, and whitespace runs
normalized away. That is what makes `ux_fact_live` do the right thing here: a per-statement
subject means each capture supersedes only itself, where a shared subject like
`/user/about-you` would have made every new statement close the previous one.

Two behaviours fall out of the addressing rather than being coded as policy:

- **Saying something twice captures it once.** The JSON store could not tell a repeat from
  a new statement — it had no key to compare on — so every restatement became another row
  in recall.
- **A repeat does not undo a rewrite.** The already-present check is on the *entity*, not
  the body: after the model rewrites a capture into something self-contained, the user
  typing the original again must not drag it back. What the store already knows wins.

Retracted is the asymmetric case, and deliberately so: a retracted statement has no live
fact, so saying it again captures it afresh. That is the opposite of the seed corpus, which
stays silent about anything this store has held before. A re-seed is nobody asking for a
fact back; a user typing the sentence again is.

**`engram_forget` closes any live fact, including seeded ones.** Not scope creep — the
consequence of the unification. Once user captures are ordinary facts there is no honest
way to keep the old restriction, and inventing a marker to preserve it would rebuild the
split this removed. It is also the right answer on its own: refusing to close a fact
because of where it came from is the store telling a user which of their own memories they
may drop. The seeder's refusal to rewrite any subject+predicate the store has held before
is what makes a retraction survive a corpus revision, so this composes rather than leaking.

The cost is a handle change — `u1a2b3c4d` becomes `f42` — which the golden file surfaced as
an intended diff rather than a silent one. Existing JSON captures are replayed into the
store in timestamp order on `init`, rewrites landing at the address of the capture they
replaced so the supersession chain survives the move, and the files are left on disk: they
are the only copy of the pre-migration state, and an upgrade does not delete a user's data
as a side effect.

---

## PreCompact cannot inject context

Spec §10.1 assumes `PreCompact` can "inject one instruction" the same way `SessionStart`
injects the primer via `hookSpecificOutput.additionalContext`. It cannot. Per the
[Claude Code hooks reference](https://docs.claude.com/en/docs/claude-code/hooks),
`additionalContext` injection is documented only for `SessionStart`, `UserPromptSubmit`,
`PostToolUse`, and a handful of other events — `PreCompact` is not among them. The only
`PreCompact` output that reaches the model is the top-level `decision`/`reason` pair, and
`"decision": "block"` does not annotate the compaction — it refuses it outright. Emitting
that on every `PreCompact` call blocks the user's every compaction, which is not what the
spec intended.

> **Erratum (spec §10.1):** no injection channel exists for `PreCompact`. `engram hook
> pre-compact` now only records its telemetry event and exits 0, emitting nothing on
> stdout. The "flush durable learnings via `engram_digest`" nudge moves to the session
> primer (`PrimerBuilder`) and the recall output footer, both already seen by the model
> on every session and every recall.

---

## 1.5 Spike results (2026-08-04, measured)

Three day-1 spikes, all on macOS arm64 / .NET SDK 10.0.301 / Apple clang 21. Every
number below is measured, not estimated.

**Spike A — AOT startup.** Publish succeeded, zero trim/AOT warnings.

| | min | median | p95 | max | binary | publish dir |
|---|---|---|---|---|---|---|
| Native AOT | 2.82 ms | **3.44 ms** | 4.34 ms | 4.55 ms | 1.06 MB | 6.6 MB |
| Self-contained, no AOT | 17.52 ms | 18.51 ms | 19.39 ms | 19.73 ms | 122 KB stub | 83 MB |

The hook budget is < 100 ms cold and < 10 ms for `file-touched`. AOT clears both with an
order of magnitude to spare; the non-AOT build would burn roughly twice the entire
`file-touched` budget doing nothing but starting. **D1's premise holds.**

**Spike B — MCP SDK under AOT.** `ModelContextProtocol` 2.0.0 with
`Microsoft.Extensions.Hosting` 10.0.10. Publish succeeded with **zero IL2xxx/IL3xxx
warnings**, confirmed non-collapsed (`TrimmerSingleWarn=false`, and the generated
`ilc.rsp` suppresses only routine codes). `initialize`, `tools/list`, and `tools/call`
all round-trip from the published binary. Independently re-verified: `Mach-O 64-bit
executable arm64`, linking only system libraries with no .NET runtime in the output
directory, returning `pong: hi`. Binary 10 MB. **The hand-rolled JSON-RPC fallback is
not needed — use the SDK.**

**Spike C — SQLite and the D3 FTS5 schema under AOT.** `Microsoft.Data.Sqlite` 10.0.10
with `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5, bundled SQLite **3.53.4**. Publish
succeeded, zero warnings, binary 2.7 MB. All 11 assertions passed from the AOT binary —
including the three that actually mattered: closing a fact via `valid_to` evicts it from
the live lane (6), the `WHEN` guard stops a second close from double-deleting (8), and
`'integrity-check'` stays clean after both a close and a hard delete (9, 10). Porter
stemming, `bm25()`, and `snippet()` all work. **D3 ships as written.**

Two build notes for the real projects: the 10 MB MCP binary sits comfortably inside the
spec's 15–40 MB budget, and `.dSYM` bundles (39.8 MB for Spike B) must be kept out of
release artifacts.

---

## 2. Riskiest assumption, and how it gets tested first

> **A ≤ 300-token session primer will make Claude Code call `engram_recall` before
> exploring files — unprompted, session after session.**

Everything else in the spec is well-motivated engineering that only pays off if this
holds. It is testable in days for almost no cost: a stub MCP server with ~30 canned
facts plus the SessionStart primer hook answers it without a real store. If the agent
will not reach for recall when perfect canned answers are waiting, no schema fixes it;
if it will, the rest of the build is justified.

**M0.0 is therefore the stub probe** (§3), and it runs before the real store is
written.

Secondary risks, in order:
1. ~~AOT publish breaks on a dependency~~ — **retired by Spike A/B/C** (§1.5). The CI
   AOT smoke test still runs on every push to keep it retired.
2. ~~The MCP C# SDK is not AOT-friendly~~ — **retired by Spike B** (§1.5): zero trim
   warnings, full stdio round-trip from the published binary.
3. Agent-written fact quality is poor enough that recall output is noise (visible in
   M0 telemetry; the response is tool-description and primer wording, not code).
4. Roslyn under AOT — *untested*, and the reason D1 puts it in a sidecar. It is only
   reached in M3, and the sidecar means a negative result costs nothing.

---

## 3. Milestones (revised)

### M0 — Adoption probe · ~1 week + 2 weeks of real use

**M0.0 (days):** the home resolver and sandbox installer flags (D7) — these come first
because every later line of code depends on them and retrofitting is expensive — then a
stub MCP server, ~30 hand-written canned facts, SessionStart primer hook, PreCompact
digest nudge. No database. Purpose: answer §2's question.

**M0.1 (rest of week):** minimal real store behind the same tool surface — `entity`,
`fact`, `supersession`, `session`, live-only FTS5, D4's four concurrency rules. Three
MCP tools only: `engram_recall`, `engram_remember`, `engram_digest`. Two hooks:
`SessionStart`, `PreCompact`.

**Telemetry (the actual deliverable):** per session — recall calls, remember calls,
coverage distribution (high/partial/none), tokens returned vs. budget, and whether
recall preceded first file read.

**Exit criterion:** two weeks of real self-use showing the agent calls `recall` before
exploring in a clear majority of sessions, and `digest` fires at session end without
prompting. If it does not, stop and fix adoption — not the schema.

Explicitly deferred out of M0: `browse`, `expand`, `revise`, `forget`, `share`/`join`,
salience, the CLI surface, embeddings, any code indexing.

### M1 — Core store hardened

Full §4.1 schema including `edge`, `salience`, `file_state`, `schema_meta`. Salience
scoring (§4.4). `MoveSubtree` cascade (D2). Remaining MCP tools: `browse`, `expand`,
`revise`, `forget`, `status`. Full CLI (§11) minus indexing/embedding verbs.
`doctor` **and `repair`** (D8), `export`/`import`. Migration framework keyed on
`schema_meta.schema_version`.

**Exit:** the spec's own criterion — remember → recall → revise → history shows a
reasoned chain — plus the D4 concurrency test (hook p99 under concurrent bulk write)
passing, and a corruption drill: deliberately damage the FTS index and a salience table
in a sandbox home, confirm `doctor` names both and `repair --apply` fixes them with
every fact body and supersession row bit-identical afterwards.

### M2 — Claude Code integration complete

Full hook suite, `engram install claude-code` with **safe idempotent JSON merge** into
`~/.claude/settings.json` and `.mcp.json` (back up first, `--dry-run` prints the diff,
uninstall is symmetric — this must never clobber a user's config), `share`/`join`,
session lifecycle, `report` HTML.

**Exit:** fresh machine, `engram init && engram install claude-code`, agent uses memory
unprompted in a real session.

### M3 — Code graph

Path-grammar document (versioned) first. Then universal + document analyzers with
extractive impressions, incremental pipeline over `file_state`, `engram_code`,
stale-subject flagging, adopt/merge for deep-tier re-keys (D2). Roslyn sidecar last.

Tiering, language priority, and the registry that makes new languages cost one row are
**D24**: C# first, TypeScript/JavaScript second. All code facts are written `observed`
(D19) and `regenerable` (D23), which is what makes a full re-index safe.

**Gate:** M0/M1 telemetry should show that missed recalls are substantially
code-structure questions. If they are not, M3 shrinks or moves behind M4. If they *are*,
it is built here: per **D20**, Engram does not outsource this to Graphify or any other
external server, because an MCP dependency is still a dependency.

### M4 — Embeddings

`IEmbedder` providers, `sqlite-vec` lane, RRF fusion, batch/backfill,
`embed --rebuild`. Reconsider D5 here with vector evidence. **D18** carries the rationale,
the lexical/semantic split, and the constraint that this never becomes a vector service.

### M5 — Polish

Salience tuning, `compact`, report completeness, doctor on aged installs.

---

## 4. Solution layout

```
engram/
  Engram.sln
  src/
    Engram.Core/               # store, temporal engine, retrieval, packing, clock, tokenizer
    Engram.Analyzers/          # universal + document tiers (managed, linked into the core binary)
    Engram.Embeddings/         # IEmbedder implementations
    Engram.Cli/                # the `engram` binary: cli | mcp | hook verbs  (AOT publish)
    Engram.Analyzer.Roslyn/    # sidecar binary (self-contained R2R)
  tests/
    Engram.Core.Tests/         # tier 1 — pure logic, no I/O
    Engram.Integration.Tests/  # tier 2 — real SQLite in a disposable home (the bulk)
    Engram.EndToEnd.Tests/     # tier 3 — drives the published AOT binary
    Engram.Stress.Tests/       # tier 4 — multi-process contention, nightly
  docs/
```

Cross-cutting engineering rules, decided now because retrofitting them is expensive:

- **`IClock` everywhere.** Every table is temporal; tests need a fixed clock or
  supersession semantics cannot be asserted deterministically. No `DateTimeOffset.Now`
  outside the clock.
- **`ITokenCounter`** behind an interface. Ship a calibrated character-ratio estimator
  first — the `214/500 tokens` line only needs to be honest, not exact — and leave the
  seam for a real BPE tokenizer if budgets ever prove sloppy.
- **The §6.2 recall output format is a contract.** Golden-file tests, changed
  deliberately.
- **AOT publish runs in CI from day 1**, on every push. A trim break found in M3 is
  expensive; found in M0 it is a package swap.
- **No `EF Core`, no ORM.** Hand-written SQL against `Microsoft.Data.Sqlite`. It keeps
  the AOT surface small and the query plans visible, which is the whole point of the
  prefix-range-scan design.
- **One home resolver, no exceptions** (D7). Paths come from it or they are a bug, and
  a lint test enforces that. Every test runs against a disposable home.
- **Every destructive operation is dry-run first.** `repair`, `compact`, `forget`, and
  the installer all print what they would do and require an explicit flag to act.

---

## 5. Execution model

Implementation is delegated to **Sonnet** subagents; I write the spec for each work
item, review every diff myself, and dispatch test/build runs to the Haiku runner so
their output does not crowd out review.

Per work item:

1. I write a precise, decision-free spec (files, contracts, test assertions).
2. A Sonnet implementor builds exactly that and reports what changed.
3. I read the diff. Separately, the runner executes the build + tests and returns only
   failures and the exit code.
4. Anything ambiguous comes back to me as a decision, never guessed at downstream.

Verification I will not accept on report alone: temporal correctness (supersession
chains and `valid_to` closure), the D4 concurrency rules, the D7 isolation guarantees,
`repair`'s promise never to touch authored facts, and anything touching
`~/.claude/settings.json`.

---

## 6. Immediate next steps

1. ~~`git init` and first commit~~ — **done**, pushed to a private `JimCline/engram`.
2. ~~Day-1 spikes~~ — **done**, all three green (§1.5). D1 and D3 confirmed by
   measurement; the MCP SDK is used directly, no fallback needed.
3. Scaffold the solution: `Directory.Build.props` (net10.0, nullable, warnings as
   errors), the projects from §4, and CI running build + tests + an AOT publish smoke
   test on every push.
4. Build **M0.0** — home resolver and sandbox install flags first (D7), then the stub
   adoption probe — and start using it in real sessions.

### 6.1 Work queue for D15–D18 (captured 2026-08-05, not yet started)

Ordered by risk retired per unit of work, not by decision number. Nothing below has been
implemented; D15–D18 are written but unbuilt.

**A. Tool-surface work — small, independent, no spike needed**

1. ~~**D15** — move the durable guidance out of `PrimerBuilder`~~ — **done** (`195bb9d`).
   It required no addition to the tool descriptions at all: the primer's instruction was
   already stated nearly phrase for phrase across `engram_recall`, `engram_remember`, and
   `engram_digest`, so the fix was deleting the duplicate from the channel that does not
   survive compaction. `SubagentInstruction` deliberately stays in the hook — durable but
   not *universal*, and a tool description is shared with a main agent that does not need
   telling its report is lossy. `HookCommand` now declines to emit an empty primer.
2. ~~**D17** — ceiling on the tool surface~~ — **done** (`195bb9d`), measured at **2,399
   characters** across four tools, ceiling set to 2,600. Lives in
   `tests/Engram.Integration.Tests/` rather than tier 1, because only that project
   references `Engram.Cli`. Falsified before being kept: at a ceiling of 2,000 it fails
   with the per-tool breakdown. A sibling test pins the tool count at four.
3. Golden-file both tool descriptions under the D9 recall-contract treatment. They are a
   model-facing interface now, not prose. **Still outstanding** — the D17 ceiling catches
   size drift but says nothing about wording, which is the part the model actually reads.

**B. Spikes that gate M4 — these carry the real risk**

*Substantially retired by prior art.* A separate project of the author's already runs
Qwen3-Embedding-0.6B in-process via LLamaSharp into a `sqlite-vec` store, with LM Studio and
Ollama as alternative providers. Model selection, the nomic comparison, and "does this
combination work at all" are answered — do not re-run them. **What remains is Native AOT,
which that project does not evidence**, and it is now the whole of this group.

4. **Does `sqlite-vec` load under Native AOT** through `NativeLibrary.Load` from
   `~/.engram/lib/`? D1 asserts it; §1.5 never measured it.
5. **Does LLamaSharp work under Native AOT?** It is a managed wrapper, not just a P/Invoke
   surface, and trimming is where wrappers break. **Informative, not gating** — per D25 the
   answer selects which rung of the provider ladder M4 lands on, and per D18 the localhost
   providers carry the lane regardless. Worth measuring early for planning, not before
   starting.
5b. Only if both pass: confirm embedding throughput on the background queue is sufficient
    to backfill the existing corpus in reasonable time. Not a latency deadline — embedding
    is off the write path by construction (D4), so this is a throughput sanity check.

**C. M4 proper, in dependency order (only after B is green)**

6. `IEmbedder`, and a **deterministic stub embedder first** — tier-2 tests need it to
   exist before anything real does, per D18's ordering caveat.
7. Vector table + backfill queue. Embedding runs server-side, off the hook path (D4).
8. **`engram explain <query>` first (D21)**, then RRF fusion (`k≈60`) in retrieval, with
   FTS5-only as a fully supported configuration when the lane is absent — degraded
   quality, never a missing result. The order within this step is the decision: an
   explainer written after the fusion it explains is written against remembered
   intentions rather than observed behaviour.
9. `embed --rebuild`; `doctor` and `repair` coverage for the vector index as derived
   state (D8).

**D. Cheap, unblocked, do whenever**

10. ~~**D16's gate** — measure the real facts-per-session distribution on the author's
    instance.~~ — **done.** Median **1** against a gate of 5 (`[1, 1, 1, 1, 2, 8, 20]`
    across 7 fact-writing sessions), so D16 lapses and the expand view is not built. The
    measurement lives in `engram probe` rather than in a one-off script, because a gate
    whose evidence cannot be re-taken is a gate that quietly stops being true.
11. ~~§1's intro still says *"Six architectural forks were adjudicated with Fable"*~~ —
    done. The count reflects the decisions actually present, and the Fable provenance
    stays scoped to six rather than being extended to decisions whose origin is not mine
    to assert. That was the only thing blocking the fix; the two claims did not have to
    travel together.

**E. Provenance tier (D19) — independent of A–D; run it alongside A**

12. Schema migration adding a **nullable** provenance column to `fact`; existing rows stay
    `NULL` (*unknown, predates the tier*). No backfill by inference — see D19.
13. Write path: `engram_remember` and the capture hook set the tier at insert. User
    captures are `stated`; anything carrying `evidence` is `observed`; otherwise
    `inferred`. Assert in tier 2 that the column is never updated after insert, and that
    `repair` cannot touch it.
14. Recall output: render the tier as a one-or-two-token marker with `NULL` shown
    distinctly from `inferred`. This is a golden-file change under D9 and counts against
    the D17 ceiling.

15. **`engram report` (D22)** — read-only Markdown enumeration of everything stored,
    including superseded facts, with no truncation and no HTML. Build it *with* E, not
    after: it is the fastest way to check whether the D19 tiers were assigned sensibly.

16. **`regenerable` marker (D23)** — same migration as step 12, separate column, immutable
    at insert. Rework `repair` to key off it alone and never off provenance, with the
    falsification test that flips an agent observation to `regenerable` and confirms
    `repair` would consume it.

*Deliberately not in scope for E:* letting provenance influence retrieval ranking, and any
numeric confidence score. Both are rejected in D19. No visualization or dashboard, per D22.

**F. Language analyzers (D24) — gated behind M3, listed so the shape is not re-litigated**

17. Language registry as a static compile-time table (AOT cannot discover implementations),
    plus a conformance suite that iterates it and a lint test asserting no harness carries
    its own language list.
18. Tier 1 `tree-sitter` loader via `NativeLibrary.Load` from `~/.engram/lib/`, following
    the `sqlite-vec` pattern. C# and TypeScript/JavaScript grammars, in that order.
19. Tier 2 Roslyn sidecar for C# only. TypeScript does **not** get a tier-2 analyzer — that
    would mean a Node runtime, which D20 rejects in principle.

**Standing tension to re-read before starting C.** D18 gates M4 behind M0/M1 telemetry
showing paraphrase misses, and the corpus is ~51 facts as of capture. The owner has
said they want semantic search regardless; B is pure risk-retirement and worth doing
either way, but C's payoff scales with corpus size and with the gap in time between the
session that wrote a fact and the one that queries it.

---

*Open items deliberately left for later: entity-resolution fuzziness thresholds (start
exact + alias + case-insensitive, per spec §12); whether `UserPromptSubmit` recall
earns default-on (decide from M0/M1 coverage data); archive FTS for history search
(only if `LIKE` scans prove slow).*
