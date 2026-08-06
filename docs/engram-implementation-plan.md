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

Forty-four architectural forks are locked below. Each is a decision, not an option.
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
  `~/.engram/lib/`, fetched by `engram init --with-embeddings`, loaded from an explicit
  path — but by two different mechanisms, which this bullet originally conflated.
  `NativeLibrary.Load` is right for `llama.cpp` and tree-sitter, which the *managed* code
  P/Invokes into. `sqlite-vec` is not called from managed code at all: it is a SQLite
  loadable extension, so it goes in through `sqlite3_load_extension` —
  `SqliteConnection.EnableExtensions` then `LoadExtension(path)` — and registers a virtual
  table module on **that one connection**. Measured under AOT; see spike D in §1.5.
  For `llama.cpp` specifically, "loaded from an explicit path" is not one call: the ggml
  libraries must be `NativeLibrary.Load`-ed individually, in dependency order, *before*
  `NativeLibraryConfig.LLama.WithLibrary` names `libllama`. Their install names carry a
  version suffix (`@rpath/libggml.0.dylib`) that their filenames on disk do not, so a flat
  directory alone cannot satisfy `libllama`'s `@loader_path` lookup — loading each one first
  registers it under the name the lookup will use. Measured under AOT; see spike E in §1.5.

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

   **Measured** on the published binary (21.2 MB, macOS arm64, 120 samples after 20
   warmup, interleaved against a floor of `engram home` — same process, no database):

   | | p50 | p95 | p99 | vs floor |
   |---|---|---|---|---|
   | `home` (floor, no database) | 7.80 ms | 8.58 ms | 9.35 ms | — |
   | `file-touched` | 7.82 ms | 8.32 ms | 8.98 ms | **+0.02 ms** |
   | `session-start` (reads) | 10.18 ms | 10.79 ms | 11.13 ms | +2.38 ms |
   | `user-prompt` (writes) | 9.92 ms | 10.99 ms | 11.88 ms | +2.12 ms |

   Three things fall out, and the third is the one that matters.

   The budget holds, including at p99, and the unconditional part holds too: with an
   indexer-shaped writer committing 200-row `BEGIN IMMEDIATE` chunks back to back — 929
   chunks during the run — `file-touched` moved to p50 9.29 / p99 9.96 ms. Half a
   millisecond of shared disk, no tail. A hook that never opens the database cannot wait
   on a lock, which is the whole claim.

   The file-per-invocation design survives the case it was written for. Eight concurrent
   hooks (a multi-file edit) ran at p50 11.02 / p99 14.13 ms, and across the entire
   benchmark **580 invocations produced 580 spool files** — no record lost, at any width.

   **The hook's own work is unmeasurable; the budget is process start.** +0.02 ms is
   noise, and 99.7% of the 7.82 ms is spent before `RunFileTouched` is reached.

   Rule 4 is still not a stylistic preference about tidiness, but the arithmetic first
   written here was wrong and is worth correcting rather than quietly fixing. It read the
   +2.38 / +2.12 ms above as the cost of the open. Those are whole-hook deltas: the open
   *plus* reading facts and formatting a primer, or writing one. Isolating it — `probe`
   against two homes identical but for the presence of `engram.db`, which it skips when the
   file is absent — the open alone is **+1.46 ms** on the shipped binary, and +1.04 ms on
   the smaller spike-D binary. Against ~2.2 ms of headroom, a `file-touched` that opened the
   database would fit at p50 and sit on the line at p99.

   So the justification is not the p50 arithmetic; it is the word *unconditionally*. A hook
   that opens the database can wait on a lock, and `busy_timeout` is 5000 ms against a 10 ms
   budget — one contended open is a 500× overrun. The measurement above is what shows the
   rule buying that, and it would not survive the open. What threatens this budget in future
   is **binary size, not hook complexity**:
   spike A measured 3.44 ms of process start at 1.06 MB, and the same start now costs
   7.80 ms at 21.2 MB. That is a second, independent reason D1 keeps `sqlite-vec` and
   llama.cpp side-loaded rather than linked — quite apart from AOT-hostility, linking
   them would spend the hook budget on a lane this hook does not use.

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

**The first of those triggers has fired: `engram_digest` now persists.** One call can add up
to 25 notes, against a measured median of 1 fact per fact-writing session — so a single
digest in a session moves that session past the gate on its own. This does not reopen D16
yet, because nothing has been measured: the gate is about what sessions *typically* produce,
and no session has yet run against a persisting digest. Re-run `engram probe` after real use
before concluding either way. What would be wrong is to leave the lapse recorded as settled
when the thing that justified it has changed.

### D17 — The tool surface is a per-session token cost with a stated ceiling

Measured 2026-08-05: the four memory tools in `EngramMcpTools.cs` cost **2,575 characters
of definitions** (`recall` 521, `remember` 1,142, `forget` 403, `digest` 509 — description
plus schema), roughly 640 tokens, paid on every session whether or not memory is used.
Three more tools ship alongside them (`start`, `status`, `stop`).

That figure was 2,399 when this decision was written, and the drift is the ceiling doing its
job: `forget` grew when it learned to retract session notes, `digest` when it started
persisting them. Both were paid for deliberately — the second is 25 characters under the
ceiling, and buying it meant cutting words from the same description rather than raising the
limit. There are 25 characters left, so the next tool that wants more has to take it from
one that has it.

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
>
> **Measured, and the ratio moved.** Same command on the same machine, published both
> ways: AOT p50 **8.15 ms**, self-contained without AOT p50 **23.28 ms** (p99 27.80) —
> **2.86×**, not 5.4×. Spike A's ratio was taken on a 1.06 MB binary; at 21.2 MB the fixed
> cost of process start dominates both builds and the multiple shrinks. The decision is
> unchanged at the stated budget — AOT clears 10 ms and non-AOT misses it by more than
> twice over — but the margin behind it is not what it was, and the hypothetical is now
> answered rather than open: at a 25 ms budget non-AOT would clear p50 and miss p99, which
> is not clearing it. What is still not written down is where 10 ms came from.

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
   ladder stops here. **Measured 2026-08-05: it is, and it does.** Spike E in §1.5.
3. **An owned embedder sidecar** — only if LLamaSharp is AOT-hostile *and* we want embedding
   that does not require the user to install someone else's runtime.

Step 3 exists because D20's rule binds here too: an opt-in feature that works only when a
third-party tool is installed is weaker than one whose dependency we ship. It is third on
the ladder because it is the most work, not because it is optional forever.

**Step 3 is now unnecessary, and step 1 is what remains optional.** Spike E puts the
in-process provider in the AOT binary, which is what step 3 existed to recover if it could
not go there. The localhost providers stay — they are free capacity for a user already
running Ollama or LM Studio, per D18 — but they are no longer load-bearing, so nothing about
M4 waits on them. One correction to the reasoning above rather than the conclusion: the
sidecar was framed as the fallback for AOT-hostility, and the thing that would actually have
forced it is not hostility but the 6.5 s model load, which makes any per-batch process a
non-starter. In-process inherits that constraint rather than escaping it — the weights load
once into the long-lived daemon and unload on idle, which is what `idle_unload_minutes`
already describes.

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
an intended diff rather than a silent one.

**The replay that carried this across has since been deleted.** `init` used to import the
pre-database JSON captures and session JSONL in timestamp order, rewrites landing at the
address of the capture they replaced so the supersession chain survived the move. It existed
for exactly one installation — the author's — and Engram has not shipped, so no other store
can contain those files. Carrying ~350 lines of import into a first public release would
mean every new user's first command runs a migration for files that cannot exist, and the
`user-facts/` directory it read is gone with it. If a pre-release store still holds the only
copy of something, the supported move is to read it out before upgrading, not to make the
released binary carry the reader forever.

---

### D27 — A codebase is addressed inside its project, not beside it

`/code/<repo>/…` and `/projects/<name>/…` were siblings at the root, which gets the
containment backwards: a project may hold several codebases, and a codebase always belongs
to one project. Code now lands at `/projects/<name>/code/<repo>/…`, a sibling of
`/projects/<name>/decisions/…`.

Containment is the weaker half of the case. The stronger half is that the query this system
runs most could not be expressed as a prefix. Recall's fusion already boosts project and
code scope *together* whenever there is repo context, and the primer wants a project's
decisions and its code in one pass — but under sibling roots that is a union of two prefixes
plus a repo→project mapping to know which two. Nested, it is one indexed range scan, which
is the operation §4.2 promises is cheap.

It also settles a question the sibling layout had no answer for: where an indexed README or
ADR goes. It is a file in a repo *and* it is prose about decisions, so under sibling roots
the choice had to be adjudicated at write time, and different callers would adjudicate it
differently. Inside a project both candidate homes are in the same subtree and the choice
stops being load-bearing.

**Siblings in the path, separate in lifecycle, and that separation is already carried
elsewhere.** Code facts are regenerable and `compact` prunes them; decisions are authored
truth it may never touch. That distinction rides on the `regenerable` column (D23), which is
its own axis precisely so it is never inferred from location — so `compact --path
/projects/<name>` still filters on the column and cannot reach a decision. Sharing a prefix
costs nothing there, and the addressing implies nothing about storage layout.

**`<repo>` stays even when a project has one codebase.** Eliding it reads better —
`/projects/engram/code/src/Engram.Core/…` — but the day a second codebase joins, every fact
under the first one changes address. `path` is mutable only to follow an entity on rename
(D2); making it also move when an unrelated sibling appears turns a bounded rule into an
open one. Predictable beats short.

**Scope stops being derivable from the root**, which the spec claimed and the code never
implemented. Code and decisions now share `/projects`, so the root cannot discriminate them.
That is a correction, not a loss: `session` scope already had no root in the §4.2 list, and
scope has been caller-supplied at every write path from the start. Scope is what kind of
knowledge a fact is and how long it lives; the path is where its subject hangs. Conflating
them was always going to break on the first root that held two kinds.

Free to land now and expensive later: nothing writes `project` scope yet, code indexing is
deferred out of M0, and the seeded corpus carries no paths at all — so this is a change to
documentation and the default `[taxonomy] roots`, with no store to migrate. After a public
release it would mean re-addressing every indexed fact in every installed store.

**Left open: how a repo learns which project it belongs to.** Nesting makes that binding a
precondition for addressing any indexed fact, and it cannot be a prompt — a first index
that stops to ask a question is a first index that does not happen. The leading candidate
is to default the project to the repo's own directory name, so a solo codebase lands at
`/projects/engram/code/engram/…` with no configuration at all, and to let a repo be re-bound
in config when a project genuinely spans several. That keeps the common case zero-effort and
makes the multi-repo case declared rather than guessed, which matters because guessing it
wrong re-addresses a subtree. Deliberately not decided here: there is no multi-codebase
project to test it against yet, and the cost of deciding it late is one default, not a
migration.

---

### D28 — Built from source on the machine that runs it, so packaging is not a problem we have

Engram is not a commercial product and there is no shipped binary. It is built from the
repository on the end user's machine — `install.sh` already does exactly this, via
`dotnet publish` against a detected RID. Anyone who wants to distribute it differently owns
the consequences of that.

This is worth writing down because a whole class of work disappears with it, and that class
is seductive: Developer ID signing, notarization, reproducible builds, a prebuilt platform
matrix, checksummed release artifacts, and every decision that begins "what if the build
machine differs from the user's machine." They do not differ. Time spent making them agree is
time spent on a problem this project does not have, and the honest response to the residual
risk is a README callout about building rather than machinery to eliminate it.

**Two things survive as real requirements**, and both are about what the built binary can do
rather than how it got there:

**Performance on Apple Silicon must not be silently lost.** §1.5 records the mechanism:
`ggml-metal` compiles its shaders at runtime, and the shader language version defaults to the
SDK recorded in the *main executable*, so an executable linked against an old SDK quietly runs
the pre-tensor path at roughly half speed on an M5. Under D28 this mostly solves itself — the
binary is linked by Apple's linker on the user's own machine, `out/engram` measured
`sdk 26.5`, and an M5 implies macOS 26 anyway, so the machines that would benefit are the
machines that already have a current SDK. **What does not follow is a build-time assertion.**
Failing a build because its SDK field is below 26 would punish someone on an M2 and macOS 14
who has no tensor cores to lose. The check belongs at runtime, in `doctor`, where the hardware
and the actual `has_tensor` result are both known — report the Metal tensor path as on or off
and, when it is off on hardware that supports it, name updating the toolchain and rebuilding
as the fix. Rewriting the SDK field with `vtool` is rejected: it makes the binary claim an SDK
it was not built against, and on an older macOS that could push the main shader compile past
what the OS supports, turning a performance problem into a broken one.

**It must build and run on Windows and Linux, with CUDA where drivers are present.** This is
the constraint that decides the provider question. An HTTP-only embedder would not need CUDA
at all — the inference server would own the GPU — so wanting CUDA is wanting llama.cpp
in-process, and D25's ladder therefore lands on rung 2 as its ordinary case rather than its
optional one. D28 makes that far cheaper than it looked: building on the target machine means
there is no five-platform binary matrix to pre-produce, because each machine resolves its own
platform at build time. What it does not make cheaper is *backend selection* — Metal on macOS,
CUDA on Linux and Windows when the driver is there, CPU otherwise — which is now a real
requirement rather than a nicety, and the loading quirks spike E measured are macOS-specific
and have no Windows or Linux equivalent yet.

CI builds all three platforms as of this decision, because under D28 "does it build here" is
the entire portability question — there is no artifact to test instead — and Linux and Windows
were previously unproven.

---

### D29 — git decides what a repository contains; content decides what is worth reading

The question is which files an indexer should read, and it has two halves that want different
answers.

**What belongs to the repository is git's call, not ours.** `git ls-files --cached --others
--exclude-standard` returns tracked files plus untracked ones that are not ignored, which
already excludes build output, dependency directories, caches and temporary files — per every
nested `.gitignore`, per `.git/info/exclude`, and per the user's global ignore file. Each of
those is a decision the developer already made about their own tree. A pattern list maintained
inside Engram would be a worse, staler copy of a file the repository already ships, and it would
disagree with the developer on their own project. Untracked-but-not-ignored counts as theirs
because a file written five minutes ago is exactly the file an agent is about to be asked about.

The list still exists, for the directory that is not a checkout — and there it does all the
work, which is why it is not the four .NET-and-JavaScript patterns it started as. Measured
across a workspace of 38 repositories: the non-checkout directories walked 25,092 and 23,828
files, almost entirely `stable-audio-tools/.venv` (33,000 files), `venv/lib` (26,074) and
Swift's `.build` (11,780). None of the original patterns matched any of them. With Python,
Swift, Rust and Go patterns added, the same two directories walk 150 files in 60 ms and 2 files
in 27 ms — from 4,485 ms and 3,257 ms. The failure mode being written against is a list that
only knows the languages its author happened to be using.

**What is worth reading is decided by content, never by extension.** An extension list is
infinite, always out of date, and wrong in both directions: generated blobs ship as `.h`, and
real scripts ship with no extension at all. A NUL byte in the first 8 KB is what git itself uses,
and it costs a read the indexer must do anyway. Two shape rules catch what survives that — a
size cap, and a mean-line-length cap for minified and bundled files.

The mean, not the longest line, and that distinction was paid for. A longest-line rule at 2048
bytes rejected this repository's own `plugin/hooks/hooks.json`: 61 hand-written lines, 4,018
bytes, one 2,662-byte line among them. One long line is a formatting choice; being *made of*
long lines is what generated means. Across this repository's 175 tracked text files the mean
line is 38 bytes at p50, 49 at p90, 68 at p99 and 170 at worst, while a minified bundle runs to
thousands — the populations are separated by more than an order of magnitude, so 400 sits in a
gap rather than on a knife edge.

**Excluding is the safe error, and that sets every default.** A file wrongly indexed becomes
facts, and facts are append-only: `compact` and `repair` may not delete a fact body (D8), and
nothing downstream can distinguish a fact derived from a bundle from one derived from real
source. The only cure is `forget`, by hand, after the noise has already been served to a model.
A file wrongly excluded costs one line of config and a re-index. So the filter excludes when
unsure — and every skip is counted and reported (`engram scan`), because an over-eager rule
must show up as a number rather than as a repository that mysteriously has no code facts.

One consequence worth stating: pattern matching is hand-written rather than translated to a
regular expression. The regex version worked and passed the same tests; it cost 630,240 bytes of
published binary by linking `System.Text.RegularExpressions`, and binary size is a latency
decision here because the `file-touched` budget's remaining headroom is all process start.

### D30 — The explainer describes the ranker that runs, not the one that was planned

D21 specifies what `engram explain` reports: lexical rank and BM25 score, vector rank and cosine
distance, salience contribution, fused RRF position, provenance tier. Building it revealed that
**four of those five do not participate in recall today.**

`engram_recall` calls `RecallEngine.Pack`, which orders facts by *how many distinct query terms
each one contains*. `fact_fts` is maintained by triggers and read by nothing on the recall path.
`fact_vec` is queried only by its own tests. The `salience` table has no writer. Written to D21's
letter, `explain` would have reported a ranking that does not happen — which is the exact failure
D21 exists to prevent, one layer up.

So two things are decided here.

**The explainer shares the ranker's code, it does not reimplement it.** `Explain` and `Pack` call
one `BuildCandidates` and one `ApplyBudget`; `Pack` renders the digest afterwards and `Explain`
returns the candidate list. A separate implementation would be a copy that drifts, and the drift
would be invisible precisely because the explainer is the tool one would use to notice. The guard
is a test asserting the packed lines equal the digest's lines; breaking it by re-sorting inside
`Explain` fails three tests.

**Lanes are reported by observed state, not by intended role.** `term overlap` reports RANKING;
`fts5` and `vector` report *idle — answerable, read by nothing*; `salience` and `RRF fusion`
report *not built*. That disagreement is the deliverable, not an apology for one: a fact BM25
ranks first and the shipped ranker never scores is evidence about whether fusing them is worth
doing, available **before** the fusion is written rather than after.

**What it found within minutes of existing.** Term overlap matches literal lowercased tokens;
FTS5 tokenizes with `porter`. So recall is blind to morphology. Measured against Engram's own
seeded corpus, on twelve plurals of words that corpus uses: **eight had facts the ranker never
scored, and six returned "no facts matched" while FTS5 ranked the right fact first.** The
singular controls find those same facts — `pragma` → 1 candidate, `pragmas` → 0; `connection` →
1, `connections` → 0. Nine of twelve singulars match, and every one of their plurals returns
nothing.

That is not a ranking-quality problem, it is a false negative with a side effect: recall answers
`coverage: none` and tells the model to *"discover and engram_remember what you find"*, so the
agent re-derives a fact the store already holds and writes a near-duplicate — and facts are
append-only, so `compact` may not clean up after it (D8). Fixing it is a change to the ranker and
is therefore sequenced with step 8 rather than smuggled into the explainer, which is the point of
building the explainer first.

`explain` is read-only, including the vector lane — embedding a query is a provider call, never a
write, and nothing here touches salience counters. An explainer that recorded an access would
move the ranking it was asked to explain, and the effect would be invisible because the tool that
would reveal it is the one causing it. A test asserts the row counts are unchanged across two
explains.

**The fix: fuse the lanes, do not swap one for the other.** Recall now draws `fact_fts` to
`seed_k` and fuses it with term overlap by reciprocal rank, `k = 60`. Replacing overlap with FTS5
was the tempting one-line version and it is wrong, because the two lanes miss different things:
FTS5 stems and reads the predicate but cannot index the subject's display name, while overlap
reads the subject but matches literally. Swapping trades one class of false negative for another
and the tests would not have noticed.

RRF rather than a weighted sum, because there is no honest weight to pick: `bm25` returns an
unbounded negative whose scale depends on the corpus, and overlap returns a small count. Any
constant reconciling them would be a number nobody could defend. Reciprocal rank throws the
magnitudes away and keeps only the order, which is the part both lanes agree means something. It
also lands on the right emphasis by construction: at `k = 60` the gap between rank 1 and rank 2 is
1/61 − 1/62 ≈ 0.0003, while a fact both lanes found scores nearly double one only a single lane
found. Fusion rewards **agreement between lanes** far more than position within one, which is
exactly the confidence signal available here.

Measured after fusion, against the same corpus: the six queries that returned "no facts matched"
became **one**. Every other plural now matches or beats its singular — `hooks` 11 vs `hook` 10,
`settings` 5 vs `setting` 2, `indexes` 2 vs `index` 1.

**The last false negative was structural, and cost a schema version.** `pragmas` still returned
nothing because "pragma" appears in no fact body at all — only in the subject path
`/knowledge/dotnet-and-storage/pragma-foreign-keys-scope`. That is not a tail case: **30 of 45
live facts (67%) had at least one subject token absent from their body, 39 such tokens in total.**
An FTS5 external-content table can only index columns present on the content table, so the
entity's display name is genuinely unreachable — but `path` is denormalized onto `fact` (D2), it
is on the content table, and `unicode61` splits its slug on `/` and `-`. So schema version 2 adds
`path` to `fact_fts`, with a fourth trigger to re-index a fact whose path follows its entity on
rename. `bm25`'s IDF is what stops this flooding results: a segment appearing in every document
contributes almost nothing, so `/knowledge` does not match the store. Columns are weighted
equally, as the honest default until something is measured.

Rebuilding the index is a legal migration under D8 precisely because it destroys nothing
authored: external content means every indexed value is read back out of `fact`. The migration
carries its own copy of the FTS5 DDL, since an FTS5 table's columns cannot be altered in place and
parsing the statements back out of the schema file would fail silently on a reformatted comment.
The duplication is guarded by a test asserting a migrated store and a fresh one have byte-identical
`sqlite_master` rows for the table and its triggers.

`[retrieval]` stops being decorative: `default_budget_tokens` and `seed_k` are now read by both
`recall` and `explain`. `graph_hops` and `recency_half_life_days` stay deliberately unread — the
features behind them do not exist, and a setting that silently does nothing is worse than an
absent one.

---

### D31 — Snapshots are triggered by change, thinned by generation, and taken before every migration

The store had no copy of itself anywhere, and nothing that would notice it leaving. This was
written after `engram.db` disappeared from a live instance with the rest of the home untouched —
no logged command removed it, the test suite provably does not (a sentinel database under a
redirected `HOME` survives a full run), and the installer's verification home is a real
`mktemp -d`. The cause is still unknown, which is the strongest argument available for the
feature: the recovery story cannot depend on first explaining the loss.

**`VACUUM INTO`, never a file copy.** The store runs in WAL mode, so a committed fact lives in
`engram.db-wal` until something checkpoints it. Copying `engram.db` alone was measured here to
produce not a stale database but an unusable one — the copy had no `fact` table at all, because
everything was still in the log. `VACUUM INTO` reads through one transaction, writes a single
consistent compacted database, and takes no write lock, so a snapshot never blocks a hook trying
to record a fact. Written to `.partial` and renamed, because a truncated file whose name says it
is a snapshot is worse than no snapshot: it is the one you would reach for.

**Change is the trigger; the interval is only a ceiling.** A clock-driven backup copies identical
bytes twenty-four times on a day nothing was written and then thins twenty-three of them back
out — work performed solely to undo itself. Facts are append-only, so counts over the
authored-truth tables make a monotonic fingerprint that moves on a write, a close, a rename (which
lands a row in `entity_alias`), or a new edge. Derived state is excluded deliberately: the FTS
index and salience are rebuildable (D8), so a snapshot taken because an index changed is a copy
taken for no reason. Measured: 101 session-start invocations produced exactly one snapshot.

The comparison is against the newest snapshot's own fingerprint, read back out of it, rather than
a watermark kept on the side. A watermark is a second copy of the truth that starts lying the
moment someone deletes a snapshot by hand; a fingerprint read out of the file it describes cannot
drift from it.

**Retention is generational because the failure modes are.** Losing an hour of facts is noticed at
once; losing a fortnight is noticed long after a flat window of hourly snapshots has rolled past.
Keeping the newest in each of the last 24 hours, 7 days and 4 weeks spends a bounded number of
files on a reach measured in months. Zero is refused for any retention count — it reads like "keep
none", and one mistyped line deleting every snapshot is the failure the feature exists to prevent.

**Session start spawns it detached.** A snapshot is a `VACUUM` over the whole store, unbounded as
the store grows, and hooks have budgets; the parent pays one `fork` and never waits. Sessions are
also when facts get written. A timer would need a daemon that is not always running — hooks write
facts whether or not the server is up — and a cron entry would be a second installation surface to
own and uninstall (D28). Measured: session start goes from 11.0/12.2 ms to 13.8/13.5 ms, **+2.0 ms
mean over 100 runs**. This does not touch `file-touched`, whose rule is about that hook and its
per-edit rate, not about hooks (D4).

Fanning out from session start needed a lock, which the tests found rather than the design:
thirty concurrent session starts spawned thirty backups, which stampeded the store and outlived
the temp home the harness was deleting. One holder does the work and the rest exit at once.
`DeleteOnClose` rather than a pid file with staleness rules — the kernel closes the handle however
the process dies, so there is no such thing as a lock left behind by a crash.

**Every migration snapshots first.** It is the one moment Engram's own code rewrites structure
rather than appending to it, and it does so unattended, on open, before anyone has decided today is
a good day for it. The first migration shipped one commit before this without that guard.

**Restore is dry-run first, and assumes it is being used on a bad day.** It refuses a snapshot from
a newer schema version rather than reading it wrongly; it preserves the current store before
overwriting it, falling back to moving the raw bytes aside when that store will not open, because a
store worth restoring over is disproportionately likely to be one that will not open; and it
removes the stale `-wal`/`-shm`, since a log belonging to the database that was just replaced is
corruption with a clean exit code.

**Three guards did not survive falsification and were rewritten rather than kept.** The retention
rule's unconditional keep-the-newest is unreachable through config, since the counts are floored at
one and the newest snapshot always leads its own hourly bucket — it is reachable only from code
constructing settings directly, which is what its test now does. The WAL cleanup passed with the
code deleted twice, because anything that opens SQLite on the way past makes SQLite unlink the
sidecars itself; it only does work when the database file is gone and its log is not, which is
exactly the state this instance was found in. And the fingerprint's closed-fact count is genuinely
redundant — both paths that set `valid_to` also write a supersession row — so it is kept as cheap
insurance with a note saying no test can guard it, following the precedent set by the
`foreign_keys` pragma.

---

### D32 — The fact journal is plain text, rewritten whole, and replayed additively

D31 deferred this and predicted the wrong shape for it, which is worth recording because the
prediction was reasonable and still wrong. The claim was that append-only facts make the journal
incremental and nearly free. On building it, the whole-file rewrite won on every axis that
mattered, and the incremental version was abandoned rather than shipped.

**Why the rewrite beats the append.** Appending only what is new needs a watermark, and a
watermark is a second copy of the truth that starts lying the moment anyone touches the file — the
same objection that put the snapshot fingerprint inside the snapshot rather than beside it. It also
needs an ordering rule the append version has no natural place to enforce: a fact and the fact that
superseded it must be written closed-then-open, because for as long as both look live they violate
the live-fact uniqueness constraint. A whole-file rewrite has neither problem. Every line carries
that fact's final validity, replay is one pass, and the file can be checked against the store by
reading it. The cost traded away is O(facts) per run instead of O(new facts), which at an hourly
ceiling in a detached process is not yet a cost; when it is, it will be a measured change.

The rewrite is atomic — `.partial` then rename — for a sharper reason than the snapshots have.
Whole-file rewriting is the one operation that could destroy the archive it maintains, so a process
killed halfway must leave the previous complete journal exactly where it was.

**It is the tier that outlives a schema.** A `.db` snapshot restores only into the version that
wrote it, which is why the version is in its filename and why restore refuses a mismatch. The
journal is addressed by path and predicate rather than by row id and table shape, so it replays
forward into any later schema. Measured on the published binary: a home holding nothing but
`facts.jsonl` replayed to 45 facts that then ranked through the real retrieval path, and a second
`--apply` wrote none of them again.

**Closed facts are journalled too.** A journal of only what is currently believed cannot
reconstruct why it is believed. The supersession chain is the record of a mind changing, and D8
protects it as authored truth exactly like the facts themselves.

**Replay is additive, and that is the whole difference from restore.** Restore replaces a store
with an older one; replay reads facts into whatever is already there and skips what it recognises,
matching on subject, predicate, body and `valid_from`. So it is safe against a live store, against
a half-recovered one, and against being run twice. It is deliberately not a merge: nothing in it
rewrites or closes a fact the target already had. Facts are append-only, and a recovery tool that
could silently retire live beliefs would be worse than the problem it was called to fix.

Two smaller calls. `session_id` is dropped, because a session number means nothing in a store that
did not host that session — the provenance that survives is what travels: subject, predicate, how
it was learned, and when. And a malformed line is skipped and counted rather than fatal, because
this file is read when something has already gone wrong, and refusing to recover four thousand
facts over one truncated line is the tool preferring its own tidiness to the user's data.

Two of the eight guards written for this did not fail when first broken, and both breaks were
wrong rather than the guards. Opening the `.partial` file in append mode changes nothing — it is
freshly created every run — so the honest falsification is to write straight to the live journal in
append mode, which is precisely the rejected design; it fails. And `if (false)` will not compile
under warnings-as-errors, so forcing the `apply` parameter true does the same job. The second
attempt at each failed as intended, which is the only evidence that either guard exists.

---

### D33 — Choosing an embedding provider is a command, and the line it writes says who wrote it

`model install` ended by printing two lines to paste into a config file. Everything needed to
write them was already in hand at that moment: the model id, the provider it implies, the path to
the file. What was left was the step where a typo produces an instance that looks configured and
is silently lexical-only. So `engram init --with-embeddings` presents the rungs and writes the
answer, and `model install <id> --use-it` does the same for a model already being downloaded.
Downloading is still not the same as choosing — staging a second model before switching to it is a
real thing to want — so the config edit is asked for rather than assumed.

**Three rungs, and the first one is a real answer.** `none` is lexical recall, which works; `local`
is a model Engram runs from a file in the home; `endpoint` is anything already serving
`/v1/embeddings`. The lane costs disk, memory and startup, so a picker that treated the largest
model as the obvious choice would be selling something. Only the endpoint rung asks for `dim`, and
it asks rather than defaulting, because a wrong width does not fail — it produces vectors that
never match anything, which is the worst available way for this to be wrong.

**Line surgery, not a TOML round-trip.** The shipped config is mostly prose explaining these very
choices. Parsing it into a model and serializing it back yields a valid file with the
documentation deleted: it would still work and would no longer explain itself. So one line changes
and every other byte survives, including the commented-out suggestions the user never uncommented,
which count as absent rather than as decisions.

**The line records who wrote it.** The rule is that nothing overwrites a value it did not write,
and the obvious implementation — compare against the shipped default — refuses its own previous
edit the second time it runs, because by then the file no longer matches the shipped default. The
tests found this rather than the design. The fix is a marker comment on the line itself:
`provider = "local"   # written by engram`. It lives with the value instead of in a state file
beside the config, because a record kept elsewhere starts lying the moment someone edits the line
it describes — the same reason the snapshot fingerprint lives inside the snapshot (D31). The no-op
check then has to compare values rather than file text, or stamping the marker would itself count
as a change and the config would be backed up and rewritten to say what it already said.

**No terminal means no answer.** Piped or redirected, the rungs are printed and nothing changes,
with the flag form shown instead. A prompt that reads EOF as an answer picks on the user's behalf,
and this is the file where being wrong is quiet. With `--provider` given outright, that is the
explicit intent the dry-run rule exists to require, so it acts.

For `local`, the download runs before the config is written. A config naming a model that is not on
disk describes an instance that cannot start, and it would have been written by the very command
someone ran to avoid getting this wrong by hand.

---

### D34 — The endpoint is asked its vector width, never told it

D33 shipped the picker still asking a human for `dim`, and flagged that as the weak point. It is
the one embedding setting that does not fail loudly when it is wrong. A wrong endpoint refuses to
connect; a wrong model name comes back an error; a wrong width produces vectors that are stored,
compared and ranked, and that match nothing — retrieval degrades into confident noise and no
component anywhere reports a fault. It is also not derivable from the model name, because an
endpoint may serve a quantized or truncated variant under the same label. So the number is
obtained by observation: `engram embed --probe` embeds one short string and reports the length of
what comes back.

**The probe is the one caller allowed past the width check.** `HttpEmbedder` throws when a returned
vector disagrees with its configured width, which is right on the write path and impossible at
probe time, when the configured width is the question. Rather than duplicate the request and
response plumbing, the send and parse were extracted and the assertion left in the embedding path
alone. Falsified: deleting the assertion still fails `OpenAi_WithTheWrongWidth_Throws`, so the
extraction did not carry the guard away with it.

**A provisional width of 1.** `EmbeddingSpace` refuses a non-positive width — correctly, since a
zero-width space is meaningless everywhere else — so the probe has to construct one before it knows
the answer. One is the smallest lie that constructs, and the probe path is the only code that ever
sees it, where it is never compared against anything.

**The probe runs against a configuration that is incomplete by definition**, so the settings' own
problem list is set aside for it: that list is complaining the width is missing, which is what the
probe was called to find out. Everything else the factory checks still applies — provider,
endpoint, and the API-key rule for a non-local host. The caller still sees the problems; they are
just not grounds for refusing to ask.

Consequences, each of which removes a question someone previously had to answer from memory:
`init --with-embeddings` asks the endpoint before asking the user, and falls back to asking only
when the endpoint will not say; `init --provider openai-compat --endpoint … --model …` no longer
needs `--dim` at all; and `embed --probe` against an already-configured instance reports a
disagreement between the config and reality, which is a fault that had no other way of surfacing.
A `local` provider is answered without a request, since the width is a property of a file Engram
already has the specification for.

The probe reads and does not write unless `--use-it` is given. It is what you run when you are not
sure what is out there, and a diagnostic that edits your config as a side effect is not one.

One guard here is unreachable and is kept anyway, following the `foreign_keys` precedent: the
explicit "no model named" check cannot change whether a request is sent, because the factory
already refuses to build an embedder without one. What it changes is the sentence the user reads —
the factory's mentions the missing `dim`, which during a probe is the question rather than the
fault. So the test asserts the message rather than the silence, and that assertion does fail when
the check is removed.

---

### D35 — `local` runs llama.cpp's server as a child, and the question about bindings dissolves

`provider = "local"` had been carrying an open question — whether a .NET binding to llama.cpp can
run encoder-only BERT models at all — and the answer turned out not to be needed. D1 already keeps
llama.cpp out of the AOT binary, so the only real choice was which out-of-process shape to use, and
llama.cpp ships a server that speaks the same `POST /v1/embeddings` that `OpenAiCompatibleEmbedder`
already speaks and `embed --probe` already tests. A binding in a sidecar would have needed its own
project, its own IPC, its own native loading, and its own answer to the BERT question — to
reimplement something already installed. So local costs a process launch and **no new embedding
code**. The unconfirmed claim was not resolved; it was made irrelevant, which is the better outcome
and the reason it is recorded here rather than left on the backlog.

**Found, not fetched.** Three places in order — `[embedding] server_path`, `lib/` under the Engram
home, then `PATH`. Engram downloads `sqlite-vec`, which is one small extension with one digest per
platform; llama.cpp is a large native artifact whose build varies by accelerator, and pinning
digests across every platform-times-accelerator combination is exactly the packaging burden D28
declines. A package manager or a local build already does it better, and D28's two requirements —
Metal on a Mac, CUDA elsewhere — are properties of llama.cpp's own builds rather than of anything
Engram could ship. A `server_path` that is set but missing is an error, never a reason to fall
through: running a *different* llama-server than the one someone named is worse than running none,
because nothing in the result says which answered.

**Launching cannot live in `EmbedderFactory`.** This is the load-bearing decision. Creating an
embedder is cheap and unowned everywhere it happens — `RetrievalExplainer` calls the factory purely
to ask whether a vector lane exists and drops the result, and no caller disposes what it gets. Had
the local case started a server there, a readiness check would have loaded a model and every recall
would have leaked one. So the launch lives behind `LocalRuntime`, an object somebody has to hold
and can therefore dispose, and the factory only ever attaches to a runtime already running. Without
one, local resolves to a sentence naming the process that does host it.

Ownership follows from that. The MCP server registers `LocalRuntime` as a container singleton, so
the backlog writer and the query side share one loaded model instead of two, and the container
stops the child by the same machinery that stops everything else. `explain` builds and disposes its
own, because it is a diagnostic whose entire job is answering whether the vector lane really works
— paying seconds to load a model is the point of running it, not a cost to avoid.

Two arguments are not defaults and are worth the words. `-ngl 99` offloads every layer: these
models are 25–640 MB, there is no case where splitting one is right, and omitting it is the
difference between Metal and a CPU fallback. `--batch-size` and `--ubatch-size` are pinned to the
served window because llama.cpp will not pool an embedding across physical batches — an input
longer than the micro-batch is refused outright, and the 512-token default sits well inside the
range a long fact reaches. The served window is capped at 8192 rather than taken from the model,
since those batch buffers are sized to it and qwen3's 32k allocation dwarfs anything Engram puts
through it; facts and queries are sentences.

Verified on the published binary against a stand-in server, which is enough to exercise everything
Engram owns — locating, launching, health-waiting, embedding, killing — without llama.cpp installed
to run the suite. What it cannot prove is that the real server accepts these arguments; only running
one does. Eight guards were falsified. The one worth naming is disposal: with `Dispose` broken, a
single test run left **seven** stand-in servers alive on the machine, which is what the guard is
for. llama-server does not exit when its parent does, so a leaked handle keeps a model resident
until somebody notices.

### D36 — One vector lane, fused into recall, so the explainer keeps describing what runs

The vector track was complete and disconnected. `sqlite-vec`, the index, the backfill, the width
probe, the provider picker and the local runtime all existed and all worked, and
`RecallEngine.Pack` had no vector parameter at all — `engram_recall`, the tool agents actually
call, was lexical-only. The code said so itself: the one vector query in the system reported
`LaneState.Idle` and the words "answerable, read by nothing on the recall path". Accurate, and the
entire embedding investment was sitting behind it.

**One implementation, because D30 is a promise about drift.** The query lived inside
`RetrievalExplainer`, where it was correct to put it when nothing else queried vectors. Giving
recall a lane of its own would have made two, and the moment either was tuned the explainer would
have been describing a ranker that no longer ran. So the lane moved to `VectorLane`, which both
call, and the explainer's job shrank to reporting what it returned. The explainer also had to start
running it *before* the fusion rather than after: reporting a lane that ran too late to affect the
result is the same defect wearing a different hat.

**RRF absorbed a third lane with no retuning**, which is the payoff for the choice D30 made
originally. Ranks are comparable by construction, so the fusion is one more `Reciprocal` term; had
the lanes been combined by score, a vector distance would have needed a weight against bm25 that
nobody could justify. A regression test pins the no-vector case to byte-identical output, because a
third term that shifts scores when the lane contributed nothing would retune the other two by
accident.

Two properties matter more than the ranking. **Recall can never fail because the lane failed** —
every stop returns a reason and an empty ranking, so a provider that is down costs vector hits and
nothing else. Configuring embeddings must not be more dangerous than leaving them off. And **the
lane costs nothing when embeddings are off**: the factory refuses before any request, and the
parsed settings are passed in rather than re-read, because recall is a hot path and re-parsing a
TOML file per query to answer a question already answered buys nothing.

**A correction worth recording.** The lane loads `sqlite-vec` on the connection in hand, and the
first explanation written for that — belt and braces against connection pooling — was wrong.
`EngramDatabase.Open` already loads it on every connection and deliberately discards the result,
because an instance without embeddings is the ordinary case there. The lane's load is how it
obtains a state it must report: "sqlite-vec is not installed" sends someone to fetch it, while "no
vector index in this store yet" sends them to build an index they cannot build. Different advice,
and only the load call tells them apart. This surfaced during falsification — removing the call
broke no test, because every vector test had the extension installed. The test that catches it is
the one that deliberately installs nothing, and it does not skip when `sqlite-vec` is absent, since
absence is its subject.

Nine breaks attempted, each verified applied. Eight failed a test. The ninth could not be broken:
removing the null-vector short circuit does not compile, because `VectorIndex.Search` takes a
non-nullable `float[]` and the nullable analysis refuses it — a stronger guarantee than a test, and
recorded here so nobody adds a test that pretends to guard it. Measured end to end against real
`sqlite-vec` and a stand-in endpoint that maps text to concepts: a fact sharing no term with the
query comes back from `engram_recall`, and the same query with the lane off returns nothing, which
is the control that makes the first result mean anything.

### D37 — `doctor` reads the instance and refuses to repair it

Every `Problems` list in the settings readers, and the never-throw contract on `EmbedderFactory`
that returns a `Reason` instead of an exception, were built for a reader that did not exist. Until
now `explain` was the only thing that surfaced any of it, and only for the retrieval path — so a
local model with no weights, an endpoint serving a different width than the config claims, a
`sqlite-vec` that failed to load, and a config line that would not parse were all diagnosable in
principle and invisible in practice. `engram doctor` is that reader.

**It opens the store with `Open` and never `OpenInitialized`, and this is the decision the rest
follows from.** `OpenInitialized` migrates an out-of-date schema on open, and D31 makes that
migration snapshot first. Reaching for it here would not merely be a side effect: it would make
the single most useful thing doctor can say — *your store is a schema behind* — unsayable, because
asking the question performs the answer. A diagnostic may not repair the state it was asked to
describe. The end-to-end test snapshots every file in the home by size and mtime around a doctor
run and asserts nothing moved, which fails on the new file in `backups/` well before anyone
notices the version changed.

The same rule at the other end of the command. `provider = "local"` is checked by looking for the
weights and for `llama-server`, never by resolving an embedder, because resolving one launches
llama.cpp (D35). A diagnostic that started a model process to find out whether a model process
would start has both answered the question and changed it, and leaves several hundred megabytes
resident behind a command the user expected to read. The test installs a stand-in `llama-server`
that touches a marker file when executed and asserts the marker never appears.

**`Off` is not a failure, and the exit code says so.** Only `Broken` fails; `provider = "none"` is
a supported configuration (D18), a server that is not running is one the hooks and the CLI do not
need, and an unbuilt indexer is a fact about the milestone rather than a fault. A doctor that
reported red for a choice the user made is one people stop reading, which costs the real faults
their audience. Exit 1 therefore means *something is broken*, not *something is imperfect*, and
`doctor` is safe to put in a script.

**Every check runs inside a wrapper that turns a throwing check into one broken row.** Not
defensive habit: a diagnostic is reached for when something is already wrong, so the state most
likely to make a check throw is exactly the state someone is running it in. A report that dies on
its first bad check is useless precisely when it is needed.

**One network call, on a deadline of its own.** The endpoint is asked its width, because D34's
failure mode is silent — a mismatched `dim` errors nowhere and stores vectors that rank like
noise, so no amount of reading configuration can find it. `Diagnostics.ProbeDeadline` is three
seconds and deliberately replaces the configured timeout rather than honouring it: an indexing run
should wait a configured thirty seconds for a busy endpoint, and a person asking what is broken
should not. A merely slow provider is reported unreachable here, which is the right answer to *is
it answering now*, and is why the fix line names `engram embed --probe` — the command that waits.
Measured: a dead endpoint against a configured 120 s timeout returns in well under 30 s, and the
whole integration suite for this command runs in 612 ms.

Two things the checks are structured around rather than merely reporting. The check logic lives in
`Engram.Core` and only rendering lives in the command, on the same split as `RetrievalExplainer`
and `ExplainCommand`, so the tests assert on states rather than parsing text — and so a doctor
that reimplemented a check could not silently disagree with the code it reports on. And
`ClaudeSettingsPath` is the one input a sandboxed home cannot sandbox, since Claude Code's
settings live in the user profile wherever Engram lives; it is therefore overridable, because a
test that let it default would be asserting on whoever ran it.

`--json` is indented and omits null fixes, unlike `probe --json` which feeds a pipeline: this one
exists to be pasted into a bug report by someone who could not work out what was wrong. It is
covered end-to-end against the published binary rather than the JIT build, because a
source-generated `JsonSerializerContext` is exactly the shape that works under reflection and
fails once trimmed.

### D38 — `embed --rebuild` refuses while the server is running

Five places already told the user to run this command — two rows in `doctor`, the `SpaceMismatch`
comment in `VectorBackfill`, and two doc comments in `VectorIndex` — before any of them could.
`VectorIndex` had both halves waiting: `Clear`, which empties the table and keeps its shape, and
`Drop`, which removes the table and the space it pinned. What was missing was the thing that
chooses between them and then refills.

Which half runs is not the user's choice, because only one of the three ways an index goes stale
announces itself. A width change is caught by `vec0` at the row level. A change to what text gets
embedded is caught because `InputVersion` is pinned next to the model. But a *same-width model
swap* is caught by nothing: those vectors store cleanly, rank against each other, and produce
distances that look like ordinary numbers — the silent failure D18 names. So the plan reads the
pinned model and picks `Recreate`, and `--apply` only decides whether to proceed. Clearing where a
recreate was needed leaves the old pin in place, and every backfill pass afterwards then refuses
the very embedder the rebuild installed; that is a test, and it fails when the branch is swapped.

**The refusal is the design.** `EmbeddingBacklog` is the single owner of vector production — one
resident model per home, guarded by the pid file — and a running server holds an embedder built at
*its* startup. A rebuild prompted by a config change would therefore race a process still using
the old model, and lose: the server's own `EnsureCreated` would re-pin the recreated table to the
space the user had just moved away from. So the command refuses and says `engram stop` first. That
is not politeness about a lock; it is the only ordering in which the new space wins. It is tested
end-to-end against a real second process, because the check reads a real pid file through the real
`ProcessInspector`, and a test with fakes injected would be asserting on the fakes.

No snapshot, unlike a migration (D31). Every row here is derived from a fact body and an embedder
(D8), so a rebuild recomputes by definition and can destroy nothing authored. The cost is entirely
in embedder calls, which is why the dry run states the count before spending them.

Progress is a plain callback rather than `IProgress<T>`: the only implementation anyone reaches
for, `Progress<T>`, posts to the thread pool when there is no synchronisation context — which is
every context this runs in — and a CLI would print its batch lines interleaved with, or after, its
own summary.

### D39 — a spool entry names the file, and reading stdin is not what threatens the budget

`file-touched` wrote a single ISO timestamp per invocation and nothing else, so a thousand queued
edits answered exactly one bit between them: *something changed*. The hook is registered as
PostToolUse on `Edit|Write|MultiEdit|NotebookEdit`, and Claude Code puts the edited file in
`tool_input.file_path` on stdin — the payload was there the whole time and the hook never read it.
Nothing consumed the queue, so nothing noticed.

**Measured before changing it,** because D4's budget is the reason this hook is spartan and "read
stdin" sounds like exactly the sort of thing that breaks it. On the published binary, 100 runs each:

| | p50 | p99 |
|---|---|---|
| `file-touched`, no stdin | 8.67 ms | 9.64 ms |
| `file-touched`, payload piped but unread | 8.94 ms | 9.85 ms |
| `user-prompt` — parses stdin, opens the store, writes a fact | 9.61 ms | 10.56 ms |

Piping the payload in at all costs 0.27 ms. `user-prompt` does everything this hook would do
*plus* open the database and write, for 0.67 ms more than `file-touched` spent doing none of it.
So parsing is not what threatens this budget — opening the database is, and rule 4 stands
unchanged: this hook still never opens it. (The earlier figures in §1.5 were taken on a quieter
machine; what matters here is the difference between rows measured in one sitting, not the
absolute numbers.)

Recording the path strictly dominates the alternative. A consumer given paths can always coalesce
them into "something changed" and rescan; a consumer given timestamps can never recover which
files they were. The format is therefore a timestamp on the first line and an optional path on the
second — optional so the entries already on disk, written before this existed, still drain as an
edit whose target is unknown rather than as a corrupt file. That is not hypothetical: the queue on
a real instance held 1058 of them.

`SpoolReader.Drain` returns `SpooledEdit(At, Path)` and drops an entry it cannot parse rather than
throwing, because this is a queue written by a hook that swallows its own errors to protect the
budget — a truncated file is a thing that happens, and failing the whole drain over one would
strand every edit behind it. It deletes what it read *before* the caller has acted, which is a
durability hole that is tolerable only because a rescan is always available; a consumer that
cannot rescan must not be built on it as it stands. That is written on the method.

### D40 — the old `user-facts/` JSON is converted, not read

Before the fact store existed, user statements were kept one JSON file per statement under
`~/.engram/user-facts/`, each carrying its own `supersedes` and `retracts` pointers — a second,
weaker implementation of the validity window `fact` already has. Nothing in the current code reads
that directory or writes to it, which was verified rather than assumed: the newest file on the real
instance is timestamped an hour before the binary that replaced it was installed, and the shipped
binary contains no `user-facts` string at all. So an instance upgraded across that change keeps its
files and silently loses the memory in them. On this machine that was 38 files.

The import is a converter, not a reader. It produces `JournalFact` records and hands them to
`FactJournal.Replay`, the same path `backup replay` uses, because replay is already additive,
idempotent, and refuses to close a fact the target store already holds. That last property matters
more here than it does for a restore: this runs against a live instance with facts captured after
the cutover, and a migration that could retire them would be worse than the loss it was called to
fix. Writing a second writer would have meant re-earning all three.

**Addresses are computed by `UserFacts`, not invented here.** A migrated statement has to land where
a native capture of the same sentence would, or saying it again tomorrow files a duplicate beside
its own history. That means the path leaf is `FactStore.Fingerprint` of the text, and it is why this
is C# in the repo rather than a throwaway script — a reimplementation of that hash would agree until
it did not. A test asserts the payoff directly: import a statement, then call `UserFacts.Capture`
with the same sentence and watch it return null.

**A chain shares one address.** This is the case a naive converter gets wrong. The old model linked
a restatement to its predecessor *by id*, so the two texts differ and fingerprint to different
entities; written at their own addresses both would be live — two current beliefs where the user
expressed one that changed. Every member of a chain therefore takes the **root's** path and
predicate, which is exactly what `UserFacts.Restate` does for a live one, and the supersession is
expressed as `valid_to` + `superseded_by` rather than as a pointer only this file understands.
`RootOf` walks back through `supersedes` with a seen-set, because these pointers were written by a
store with no foreign keys and a cycle would otherwise hang the migration rather than fail it.

Retractions are operations, not statements: they close their target and are not themselves facts.
Where a statement is closed by both a restatement and a retraction, the **earlier** event is the one
that ended the belief. A pointer at an id that does not exist leaves its statement open and is
counted as a dangling link rather than dropped silently, and an unparseable file is counted rather
than failing the whole import — the reason to run this at all is that the data is already old.

It ships as `backup import [dir] [--apply]`, dry-run first like everything else destructive, and it
does not touch or delete the source directory: re-running it writes nothing, so the files stay as
their own backup. Measured on the real instance, in a copy of the home rather than the home:
38 files → 36 statements and 2 retractions, 11 chains, 0 dangling; the store went from 45 live facts
to 81 live and 13 closed, and a second `--apply` wrote 0. Four attempted breaks of the converter —
chain member keeping its own address, retraction treated as a statement, directive mapped to
`about-you`, address invented instead of fingerprinted — each failed a test.

### D41 — the edit queue is folded, not pruned, and session start does it

D39 gave `file-touched` a reason to write one file per edit and never read. It did not give anything
a reason to delete them. The consumer is the code indexer, which is not built, so the queue only
grows: 1102 entries on the author's instance, and rising with every keystroke that lands in a file.
A queue that grows without bound waiting for a consumer that does not exist is a defect in the
binary shipping today, whichever milestone the consumer arrives in.

The fix is not to prune by age or count, and the reason is worth stating because pruning is the
obvious move. **A consumer of this queue re-reads the file's current content.** The queue says
*which* files to look at, never what they said. So for a given path, knowing it was touched at t1,
t2 and t3 tells a consumer nothing that t3 alone does not — the redundancy is exact, and removing it
loses nothing rather than losing a little. Pruning by age would discard a path that is still dirty;
folding cannot. On the real instance this collapses 1102 entries to 1, because every one of them
predates D39 and carries no path, and D39's own argument settles that case too: a bare timestamp
answers one bit no matter how many of them there are.

Two rules, asymmetric on purpose. For a **path**, keep the **newest** — the timestamp there means
last touched, and the content is read fresh regardless. For a **pathless** entry keep the
**oldest**, because a bare timestamp's only possible use is as a watermark, *there are unindexed
changes at least this old*, and the earlier one is the safe reading of a set of them.

**It only ever deletes.** Nothing renames or rewrites a spool file, and that is what buys
concurrency safety without a lock: two compactions racing converge on the same directory; a
`file-touched` running alongside creates a name no listing contains, so its entry survives; a
`Drain` running alongside removes files the compactor would have removed, or files it would have
kept, which is not a loss because draining is consuming them. Surviving names still lead with
`DateTime.Ticks`, so `Drain`'s lexicographic sort is still chronological — asserted, because a
future compactor tempted to rewrite entries into one file would pass every other test.

**Unreadable is not unparseable.** The writer holds `FileShare.None`, so a compaction racing an edit
can be refused the read on Windows. An entry whose bytes could not be obtained is left alone;
deleting on a transient error would destroy an edit that was fine. An entry that *was* read and made
no sense is deleted, because `Drain` drops it anyway and keeping it means carrying it forever.

The bound does not rest on the assumption that a person edits few files: past 10,000 distinct paths
the newest are kept and the rest are reported, never dropped silently. That ceiling takes a
parameter so a test can watch it fire at a size a test can afford to write — a guard proven only by
argument is a guard nobody has seen fire.

**Where it runs is the part that matters.** `engram queue compact --apply` exists, but a bound that
depends on someone noticing is not a bound. Session start already forks one detached child for
`backup take --if-due` (D31), and that child is the housekeeping slot: it now runs the compaction
too, in the same fork, guarded by `--if-large` so an ordinary queue costs one directory listing and
no reads. `BackupLauncher` became `MaintenanceLauncher` to stop the name from lying. A second
`Process.Start` would have doubled the one cost the parent actually pays — the fork — to save
nothing, since both children are detached either way.

`doctor` still reports the queue as `Off` rather than a warning, because a backlog is the expected
state until the indexer exists and D37 reserves red for faults. Past the compaction threshold it
gains a fix, since a queue that large means the automatic pass has not been running. It counts and
does not read; `engram queue status` is where the more useful figure, how many distinct files are
behind the number, is printed, precisely so doctor does not open a thousand files to draw one row.

Six attempted breaks each failed a test: keep the oldest per path, keep the newest pathless, delete
what could not be read, skip the ceiling, ignore the threshold, and stop spawning the compaction at
session start. That last one is the only test that has ever covered the detached child at all —
`backup take --if-due` had been spawned untested since D31.

### D42 — a server's identity is its start time; a version gap is not a hang

`ServerLifecycle` proved a pid file still described *our* server by comparing two things: the
kernel's start time for that pid, and the executable path the process was launched from. The first
is the check. The second was quietly answering a different question — *was this launched from the
same file I am?* — and it made every honest answer wrong.

Measured on this instance: the installed binary reported the server up while a freshly built one
reported the same pid file dead, in the same second. That is not an edge case, it is what working on
Engram looks like. And it was not cosmetic in `stop`, which is what makes this a defect rather than
a wording problem. A path mismatch made `stop` delete the pid file, report "engram is not running",
and leave the server running with nothing left to address it by — no later `stop` from any binary
could find it, and recovery was `kill` by hand. `start` had the mirror bug: it deleted the record
and launched a second server against a bound port.

pid plus start time is already unique, and it is exactly the pair a recycled pid cannot forge — a
stranger that inherited the number started at a different instant. The path adds nothing to it. So
identity drops the path, and the guarantee that actually mattered is untouched because it never
rested on the path: nothing is terminated whose start time does not match what was recorded.

The path is demoted, not discarded. `StatusResult.LaunchedFrom` carries it, and `status` and
`doctor` print it only when it differs from the binary being asked — that difference is the entire
explanation for an otherwise baffling row, and printing it unconditionally would bury it in noise.

**A version gap got its own state.** A healthy server on another version used to collapse into
`Wedged`, whose text is "alive and not answering its health check" — said about a server that
answered immediately and correctly. That sends someone hunting a stuck process when what they have
is a server they have not restarted since upgrading. `VersionMismatch` says so plainly, and doctor
warns rather than reporting `Broken`, per D37: nothing is wrong with that server.

Splitting the states exposed the second half of the problem. Callers asked "is a server up?" by
testing `Kind is Running`, which is false for both `Wedged` and `VersionMismatch` — and both are
live processes holding whatever they loaded at startup. `embed --rebuild` refusing to run while the
server is up (D38) is the one that matters: under the old test it would have decided the server was
absent and raced it. `StatusResult.ServerIsAlive` is now the only way to ask, so the question cannot
be answered by enumerating states again and getting a different answer in each caller.

Seven attempted breaks each failed a test: restoring the path to identity (three tests), collapsing
`VersionMismatch` back into `Wedged`, narrowing `ServerIsAlive` to `Running`, dropping
`LaunchedFrom`, and inverting the doctor row's "only when it differs" condition.

### D43 — two session counts that do not subtract

`engram probe` warned: "N session(s) ran without Engram's MCP server reachable; memory was
unavailable in those sessions", with N computed as `hookSessions - mcpSessions`. Both halves of
that are wrong, and the second cannot be repaired by moving a threshold.

`session-start` is written by the hook and carries Claude Code's session id. `session-open` is
written by the MCP server on the first request of a session and carries the `Mcp-Session-Id` header
the transport minted. Measured on a real instance: 23 distinct hook ids
(`c2392759ab81425ab1874f717f5c30d6`, `ad5589fb-18fd-4767-…`) against 9 distinct MCP ids
(`WE-XuAF0PAlGRAYV7uyBWg`, `-JYLbJEIjzV6JVkA64mMpw`) — with **no value present in both sets**. They
are disjoint id spaces, so their difference is not a count of sessions, or of anything.

The first half fails independently of that. `McpSessionId` is registered `AddTransient` and injected
only as a parameter of the four tool methods, so nothing resolves it — and nothing writes
`session-open` — until a memory tool is actually called. A session in which the model never asked
for memory leaves a `session-start` and no `session-open` with the server up the whole time. That is
the ordinary case, so the warning fired constantly and asserted the reverse of the truth: an outage
report for sessions working exactly as designed. On the author's instance it claimed 14, and there
were none.

Nothing Engram records observes reachability. The `session-start` hook could probe `/health`, but
the server starts on demand and D37 already holds that "not running" is a supported state rather
than a fault, so "down at session start" would not be an outage either. The concept is not
measurable from this data and is barely meaningful against an on-demand server.

So the probe reports what it counted. Both counts print with a standing note that they are
disjoint, because the obvious thing to do with two session counts is subtract them. One comparison
survives: **zero MCP sessions against a non-zero hook count**, which needs no correspondence
between the spaces, since zero tool calls is zero however the ids are issued. It is warned and
worded as a question, not a conclusion — nobody having asked is as good an explanation as nothing
working — and it hands off to `doctor`, which can actually look.

`hook_gap_warning` is removed from the JSON rather than left null: a consumer reading `.difference`
was reading a number with no referent, and a null-valued key keeps that reading available.
`memory_never_reached` is a bool, because there is no quantity here worth reporting.

Four attempted breaks each failed a test: restoring `hook > mcp` as the condition, dropping the
`hook > 0` clause so an empty instance reports a finding, deleting the disjointness note, and
renaming the JSON key. The first failed four tests at once, which is the measure of how far the
wrong model had spread — those tests previously asserted it.

**The limitation underneath, left open.** The MCP server cannot attribute a tool call to the Claude
Code session that caused it: the client does not forward its session id, and the transport's is its
own. So "what fraction of sessions used memory" — the number D18 gates M4 on — is not computable
today. The adoption percentages are over MCP sessions, a population that by construction called a
tool at least once, and they are labelled that way rather than rounded up to "sessions".

### D44 — coverage is lane agreement, as the spec always said

Spec §"Rules" specifies an "explicit `coverage` estimate (high/partial/none) computed from lane
agreement and score mass". The implementation was `ClassifyCoverage(int matchedFactCount)`:
`0 → none, 1-2 → partial, 3+ → high`. Neither lane agreement nor score mass — how many rows came
back.

That is not a measure of whether anything was found, because bm25 hands back a row for nearly any
query. Measured on the author's store: `weekend saturday personal activity outing` returned seven
candidates, six of which were engineering notes about lint tests, documentation absence and
`BEGIN IMMEDIATE`, reached through shared porter stems. The count called that `high`.
`permissions settings grant` did the same — seven candidates, one relevant.

The damage is not the label. `high` is exactly the value that suppresses the `gaps:` line, and the
spec calls that line "the instruction that trains the *discover → remember* fallback loop". So the
model was told memory had the question covered, and the loop that would have gone and found the
answer never fired. D12 additionally makes recall coverage *the* health metric, so the same number
was the evidence for the M4 gate.

Lane agreement was already computed and already printed by `explain` — "N found only by term
overlap, M only by fts5, K by both". Measured across all seven queries this instance has ever
recorded, K separates them without a fitted threshold:

| query | candidates | corroborated | old | new |
|---|---|---|---|---|
| plugin slash-command conventions | 39 | 8 | high | high |
| work done on Saturday | 32 | 7 | high | high |
| son's favourite game | 32 | 8 | high | high |
| weekend/outing | 7 | **1** | high | partial |
| permissions/settings/grant | 7 | **1** | high | partial |
| movie/cinema | 2 | 1 | partial | partial |
| movie/theater | 2 | 1 | partial | partial |

8, 7, 8 against 1, 1, 1, 1. Any cutoff in 2..7 gives the same answer, so the existing `3+` boundary
is kept rather than fitted to this sample — the point is that the quantity changed, not that a
number was tuned.

`none` stays keyed to the total candidate count, not the corroborated one. It means the store said
nothing and selects a different response shape (under five lines), so returning facts beneath
`coverage: none` would be a worse lie than the one this fixes. Only the high/partial boundary moves.

**Score mass is deliberately still open.** The spec names two inputs and this implements one. One
unmeasured knob is a rule; two are a preference, and there is nothing yet to measure the second
against.

Four attempted breaks each failed a test: classifying on the total again, counting a single lane as
agreement, dropping a lane from the tally, and keying `none` to corroboration. The third and fourth
are the ones a reviewer would not think to check, which is why `Corroborated` is public — the
boundary is the whole rule and it is unreachable from a unit test of `Pack`, since `CannedFact`
carries no numeric id and the lexical ranks that corroborate a candidate only exist against a real
store.

**What this says about the M4 gate, which is the reason the investigation started.** The instance
showed 28.6% of recalls returning `coverage: none`, which reads as a paraphrase-miss rate and would
be evidence for embeddings under D18. It is not. Both `none` recalls fired at 03:43:23Z and
03:43:30Z on 2026-08-05; the fact that answers them, `Jim saw the new Spider-Man movie…`, has
`valid_from` 05:05:56Z — 82 minutes later. They returned nothing because the answer had not been
written yet. That is cold start, not retrieval failure, and D18's gate is still unmet: **no recorded
query has yet missed a fact that existed at the time it was asked.**

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

The premise still holds; the spare does not. That table is a 1.06 MB binary with nothing
linked into it. The shipped binary is 21.2 MB with the MCP SDK and `Microsoft.Data.Sqlite`
in it, and starts in 7.80 ms — 22% of headroom against the 10 ms budget, not an order of
magnitude. The number to carry forward is the current one; see the measurement under D4
rule 4. It is why binary size is now a latency decision.

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

**Spike D — `sqlite-vec` under AOT (2026-08-05, measured).** Work-queue item B4, the one
gating spike for M4. `sqlite-vec` v0.1.9, the prebuilt `loadable-macos-aarch64` `vec0.dylib`
(162 KB, Mach-O arm64), against a spike binary that references the real `Engram.Core` and
mirrors `Engram.Cli`'s publish settings, so this is the shipped AOT configuration rather
than a friendlier one. Publish clean: zero IL2xxx/IL3xxx warnings, 2.78 MB Mach-O arm64.

**16 of 16 assertions pass from the AOT binary, and the same 16 pass under JIT.** The
extension loads, `vec_version()` answers, a `vec0` virtual table is creatable, vectors
insert, and KNN ranks nearest-first. FTS5 keeps working on the same connection, so the two
lanes D18 wants to fuse coexist. **D1's premise holds — but not by the mechanism D1 named**;
see the corrected bullet in D1.

The load is cheap and AOT does not tax it:

| | p50 |
|---|---|
| `Open` + pragmas (warm, in-process) | 0.288 ms |
| the same, plus `LoadExtension` | 0.459 ms |
| **extension load, AOT** | **+0.171 ms** |
| extension load, JIT | +0.219 ms |

Cold, which is what a hook-shaped process pays: process start 3.55 ms, opening the database
+1.04 ms, loading `vec0` on top of that **+0.33 ms**. Enabling embeddings therefore costs a
hook that already opens the database about a third of a millisecond, and costs `file-touched`
nothing, because it opens nothing.

Three findings worth more than the pass count:

1. **Extension state is connection-scoped, and pooling hides that.** A second
   `EngramDatabase.Open` in the same process still answered `vec_version()` — the pool
   returned the same `sqlite3` handle with the module registered. After `ClearAllPools`,
   which is what another process sees, it is gone. A vector query can pass in the long-lived
   MCP server and fail in a hook on pool luck alone. This is the pragma rule from D4 in a
   second costume, and it is now recorded next to it.
2. **A missing extension degrades, it does not corrupt.** From a connection with no
   extension loaded, a database containing a `vec0` table still reads ordinary tables,
   still writes under `BEGIN IMMEDIATE`, still answers the FTS5 lane, and passes
   `PRAGMA integrity_check` clean. Touching `fact_vec` fails loudly with
   `no such module: vec0`. The spec's promise that the system is fully functional with
   embeddings disabled survives a database that was *previously* embedded.
3. **A bad `lib/` path throws rather than killing the process.** Both an absent file and a
   non-Mach-O file raise a catchable `SqliteException`, and the connection keeps working
   afterwards. Graceful degradation can be implemented in a `catch`, not a preflight.

Two build notes for the real projects: the 10 MB MCP binary sits comfortably inside the
spec's 15–40 MB budget, and `.dSYM` bundles (39.8 MB for Spike B) must be kept out of
release artifacts.

**Spike E — LLamaSharp under Native AOT (2026-08-05, measured).** Work-queue item B5, the
one D25 demoted from gating to informative. LLamaSharp 0.27.0 with `LLamaSharp.Backend.Cpu`
0.27.0 against `Qwen3-Embedding-0.6B-Q8_0.gguf` (610 MB — the exact model D18 names, already
on the author's machine from the prior-art system D18 cites), in a spike project mirroring
`Engram.Cli`'s publish settings so this is the shipped configuration. Publishes with one
warning, 3.40 MB Mach-O arm64.

**13 of 13 checks pass from the AOT binary, and the same 13 pass under JIT.** Cosines agree
to three decimals between the two builds — one value differs by 1 in the fourth — so nothing
about AOT perturbs the arithmetic. The model loads, embeds at 1024 dimensions as
`[embedding] dim` claims, returns a bit-identical vector for a repeated string, and ranks
D18's own examples the way D18 predicts:

| query vs the fact that answers it | related | unrelated |
|---|---|---|
| *"what's my kid's name"* vs *"son is Liam"* | **0.5130** | 0.2373 |
| *"how do we avoid database lock errors"* vs *"every write is `BEGIN IMMEDIATE`"* | **0.4372** | 0.3845 |
| *"where does the home directory get resolved"* vs `EngramHome.Resolve(…)` | **0.6067** | 0.3774 |

FTS5 scores zero on all three: no token is shared between any query and its answer. That is
the capability D18 exists to buy, now demonstrated rather than argued — and the third row is
the mixed code-and-prose case D18 calls "not an edge case here, it is the corpus".

**So the ladder stops at rung 2.** D25's order of attack was localhost providers, then
in-process LLamaSharp, then an owned sidecar. In-process is AOT-clean, so it can be the
default and rung 3 is not needed.

Throughput, which is item 5b: **24.9 ms per embedding, 40/sec** single-threaded, and AOT is
*faster* here than JIT (29.8 ms). The 45-fact seeded corpus backfills in about a second and
ten thousand facts in four minutes — on a background queue, off the write path, so neither
is a latency figure.

**Model load is 6.4–6.6 s, which is the number D25 was reasoning about without one.** D25
argued the embedder must be long-lived rather than spawned per batch because loading the
weights per batch "would dominate everything". Measured: a batch of 24 embeddings costs
0.6 s against a 6.5 s load, so a per-batch process spends 91% of its life loading. It also
prices `idle_unload_minutes = 5` — a user who embeds once every six minutes pays the full
6.5 s each time, which is fine on a background queue and would not be fine on a query.

**The finding worth more than the pass count: a flat `~/.engram/lib/` does not work by
itself, and nothing in the filenames says why.** Every dylib the backend ships declares an
install name one version-suffix from what it is called on disk — `libggml.dylib` announces
itself as `@rpath/libggml.0.dylib` — and `libllama` asks for the suffixed name against an
rpath of `@loader_path`. Copy the seven libraries into one directory, point
`NativeLibraryConfig.LLama.WithLibrary` at `libllama.dylib`, and it fails: dlopen looks for
`libggml.0.dylib` beside it and finds only `libggml.dylib`.

What closes the gap is loading the ggml libraries first, by explicit path, in dependency
order — `NativeLibrary.Load`, which is what D1's bullet already prescribes for llama.cpp.
dyld registers an image under its own install name, so once `libggml.dylib` is in the
process it *is* what `@rpath/libggml.0.dylib` resolves to. LLamaSharp's own probing does
exactly this walk; `WithLibrary` skips it, because it documents that calling it makes "all
the other configurations ignored" — and the dependency walk is one of them.

Measured as a controlled pair: the same binary in the same otherwise-empty directory,
differing only in whether the library path was supplied. Without it,
`TypeInitializationException` → `RuntimeError: The native library cannot be correctly
loaded`. With it, 13 of 13. The control is the part that matters — it rules out the
treatment passing for some unrelated reason.

**The single AOT warning is this same defect seen from the compiler.** `IL3000`, on
`LLama.Native.NativeLibraryUtils.TryFindPath` reading `Assembly.Location`, which is the
empty string in a single-file or AOT app. Left to its own probing the binary only finds its
libraries when it happens to sit in its publish directory — D1's SQLite erratum wearing a
different name, except warned about at compile time rather than discovered in the field.
Engram sets the path explicitly regardless, so the warning is expected rather than
suppressed, and suppressing it would hide the next wrapper that makes the same mistake.

**Spike F — what SQL surface `vec0` actually exposes (2026-08-05, measured).** Not on the
original work queue; added because step 7's design turned out to rest on operations spike D
never exercised. Spike D proved a `vec0` table is creatable, insertable, and KNN-queryable —
enough to *read* the lane and not enough to build the backfill queue or `embed --rebuild`
around, since a virtual table is exactly the kind of thing that supports some of the obvious
operations and not others. 17 of 18 probes passed, and the one failure is the useful part.

**The backfill queue is a query, not a table.** Both `LEFT JOIN fact → fact_vec` and
`id NOT IN (SELECT fact_id FROM fact_vec)` work, as does a plain `SELECT fact_id FROM
fact_vec` scan and `COUNT(*)`. So "which live facts lack a vector" is answerable directly and
there is no queue table to keep in sync — which is the right shape under D8 regardless:
derived state that is recomputed from a join cannot drift from the thing it describes, and a
queue table can.

**`INSERT OR REPLACE` does not work.** It raises `UNIQUE constraint failed on fact_vec
primary key` rather than replacing. This is the one that would have been discovered late,
because it is the idiom a C# author reaches for by reflex and it fails only on the
second write for a given fact. Re-embedding is `UPDATE fact_vec SET embedding = ?`, which
does work in place, or delete-then-insert. `DELETE FROM fact_vec` with no predicate clears
the table and `DROP TABLE` removes it, so both flavours of `--rebuild` — same width, and a
width change that invalidates the table itself — have a path.

**`vec0` is transactional.** An insert inside `BEGIN IMMEDIATE` survives commit and a
rolled-back insert leaves nothing behind. So a vector write can ride the same transaction as
the fact it describes. D18 still puts embedding on a background queue off the write path, and
this does not reopen that — it means the *backfill* writer needs no special handling to stay
consistent with D4.

**A wrong-width vector is rejected, and says so precisely**: *"Expected 8 dimensions but
received 16."* That is worth knowing because it bounds what the dimension pin in `schema_meta`
is actually for. Width is self-defending at the row level; **the model is not**. Two models of
the same width will both insert happily and rank meaninglessly against each other, which is
D18's silent-degradation case. So `schema_meta` must record the model identifier, not merely
the dimension, and the dimension pin is the cheap half of a check whose expensive half has no
other source.

`distance_metric=cosine` is accepted on the `CREATE VIRTUAL TABLE`, so the metric the ranking
assumes is declared rather than defaulted — `vec0` defaults to L2.

One flaw in the spike itself, recorded so the result is not read as stronger than it is: the
KNN probe used vectors differing only by a small per-component offset, so every cosine
distance collapsed to ~0 and the ordering it "confirmed" was degenerate. Spike D established
KNN ordering with properly separated vectors; this run adds nothing on that point and should
not be cited for it.

**Spike G — what loading `sqlite-vec` costs per connection (2026-08-05, measured).** Spike D
established that extensions are connection-scoped and that pooling hides it, and turned that
into a prohibition: never infer loadedness from a successful query. It did not say *where* the
load goes, and the two candidate answers differ in kind. An opt-in loader — the vector lane
asks for the extension on the connections it uses — keeps the cost off every other path, at
the price of a defect class that is invisible exactly where it would be caught: a caller who
forgets still passes, because some earlier connection loaded the module and the pool recycled
its handle, and the failure surfaces only in a process that draws a cold one. An unconditional
loader in `EngramDatabase.Open` removes that class outright and charges every open for it.
Which is right is a question about a number, so the number was measured rather than argued.

**0.195 ms on a cold connection, 0.036 ms on a pooled one**, over 200 opens each. The database
open it rides along with is 1.0–1.5 ms, so the eager load is under a fifth of a cost the caller
is already paying, and comfortably inside the margin the primer hooks run in. `file-touched` is
untouched by this because it never opens the database at all. **So the loader is unconditional**,
in `Open`, and no caller can forget it.

Two supporting facts make that shape possible. **Loading the extension twice on one connection
is a no-op, not an error** — which is what lets `Open` load eagerly without breaking a caller
who loads again to learn the resulting state, since the state is a return value and cannot be
looked up. And **a failed load leaves the connection fully usable**: the `SqliteException` is
catchable and `SELECT 1` still answers afterwards, so an unreadable extension costs the vector
lane and nothing else, and recall degrades to FTS5 instead of failing. That is what justifies
`Load` never throwing, and what separates the three states it reports — an absent `lib/` is the
ordinary condition of an instance that never opted into embeddings, while a file that is present
and will not load is a fault `doctor` has to name differently.

One check failed, and the failure is the useful part: **`LoadExtension` succeeds without
`EnableExtensions(true)`**. The provider enables loading around the call itself, so the
`EnableExtensions` line every sqlite-vec example carries is cargo here. It is omitted, having
been shown unnecessary rather than assumed so.

Spike D's pooling finding was re-checked in the same run and still holds, and it is now pinned
by a test rather than a memory — `AConnectionThatLoadedNothing_StillInheritsTheExtensionFromThePool`
fails loudly if the provider ever stops recycling handles, which would make the whole argument
above obsolete rather than merely wrong.

**Spike H — a KNN filter must live inside the MATCH, not after it (2026-08-05, measured).**
Step 7's obvious design is to run KNN over `fact_vec` and then join `fact` and drop anything
with `valid_to` set. That design is wrong, and it fails in the worst available shape: silently,
and worse as the instance ages.

`vec0` applies `k` before the join. So the k nearest *vectors* are chosen with no regard to
liveness, and the join then deletes some of them. Measured on ten facts whose four nearest to
the query are closed: **a post-filter join asking for 5 live facts returns 1.** No error, no
warning, just a short answer. Because facts are append-only and superseded rows accumulate
forever, the proportion of dead vectors only grows — recall that works on a young instance
quietly starves on an old one, which is precisely the degradation this project keeps writing
guards against.

**The fix is a metadata column in the `vec0` table, filtered in the same statement as the
MATCH.** The same query with `AND v.is_live = 1` returns a full five. Inequalities work too.
Both retirement paths are available and both were exercised: **`UPDATE fact_vec SET is_live = 0`
works in place** — so supersession can retire a vector without deleting it — and `DELETE` works,
which keeps the index proportional to live facts rather than to every fact ever written. D8
permits either, a vector being derived state; the choice belongs to step 7 and is not settled
here.

**Settled in step 7: retire by update, and reconcile rather than maintain.** `UPDATE`, because
a superseded fact is still answerable — `recall --as-of` and `history` both ask for beliefs that
are no longer current, and a lane that has thrown its vectors away cannot serve them. The index
therefore grows with every supersession, which is `compact`'s problem: pruning retired vectors
is exactly the derived-state pruning D8 permits, and the cost of getting one back is one
embedding.

More consequential is *when* the update runs, and the obvious answer is also wrong. Retiring the
vector inside `FactStore`'s supersession would put a `vec0` statement on the write path, so an
instance whose `lib/` went missing — the extension is side-loaded and optional by construction
(D1, D18) — would have a `fact_vec` table it can no longer address, and every `remember` would
fail with `no such module: vec0`. Authored truth would then depend on an optional accelerator.
Catching and ignoring that error is worse than failing: it leaves the index silently stale with
nothing recording that it is. So liveness is **reconciled at the head of each backfill pass**
instead of maintained at write time. Staleness is bounded by the pass interval and bounded in
the safe direction — a stale row means a superseded fact can still be returned, never that a
live one is hidden. The same pass drops vectors whose fact is gone entirely, which is what
`compact` pruning a fact leaves behind.

`PARTITION KEY` is also accepted and a partitioned KNN returns the right rows, which is the
mechanism to reach for if scope ever needs to bound the search rather than merely filter it.

Two flaws in the spike, recorded so the result is not read as stronger than it is. **The first
run was worthless and looked fine**: the insert loop never called `ExecuteNonQuery`, so every
probe queried an empty table, and the headline check — "post-filtering loses results" — passed
vacuously on a count of zero. It now asserts the row count after insert, and the check requires
`> 0 and < 5` rather than merely `< 5`, so an empty table fails instead of confirming. Second,
the `IN (...)` probe used `IN (0, 1)` on a column whose only values are 0 and 1, so the set
covered everything and the probe shows only that the syntax is *accepted* — it does not show
that `IN` filters correctly, and must not be cited for that.

### 1.6 Constraints the in-process embedder has to satisfy

These are requirements with stated reasons, **not measurements** — none has been verified here,
and each is written so it can be falsified when `LLamaSharpEmbedder` is built. They are recorded
now because every one of them is cheap to satisfy up front and expensive to retrofit, and
because several fail silently rather than loudly.

**Native logging has to be redirected before the first model load.** llama.cpp writes progress
and warnings to the process's standard streams. Engram's MCP server speaks JSON-RPC over stdio
and its hooks emit JSON on stdout, so unsuppressed native logging does not merely add noise —
it corrupts the protocol frame and takes the whole integration down in a way that looks like a
client bug. The log callback must be installed before any weights load, and must forward errors
somewhere useful rather than discarding them.

**Each sequence's KV cache must be cleared after its embedding is read.** Batched embedding
packs N texts into one decode as N sequences; if the per-sequence cache is not released after
reading, the next batch inherits state from the last. The result is not a crash but *plausible
wrong vectors* — they have the right shape, the right norm, and rank badly. Nothing downstream
can detect this, which is why it belongs in the first version rather than a later fix.

**A batch failure must degrade to per-text retries, and a text that still fails must produce no
row at all.** The tempting representation for "this one failed" is an empty or zero vector, and
it is a trap: a zero vector makes cosine similarity NaN, which surfaces later, elsewhere, as a
ranking that is neither right nor obviously wrong — the same hazard `StubEmbedder` already
guards against for empty input. Leaving the fact unembedded is strictly better, because the
backfill query is `LEFT JOIN … WHERE v.fact_id IS NULL`: a fact with no vector is already the
representation of "needs embedding", so a failure retries itself on the next pass with no
bookkeeping. **This obliges `IEmbedder.EmbedAsync` to express per-item failure**, which the
current signature cannot — it returns `IReadOnlyList<float[]>`, so the only failure it can
express is "the whole batch threw", and one poison text would then block its whole batch
permanently. The return type has to admit a null per element.

**The embedder must serialize its own callers.** One context is not concurrency-safe, and the
MCP server is concurrent by construction. Serializing inside the embedder rather than at each
call site is what keeps that from being a rule every future caller has to remember.

**Model identity and dimension must both be pinned, and a mismatch must degrade rather than
refuse.** Spike F established why the model and not just the width: two models of equal width
insert happily and rank meaninglessly. The response to a mismatch is a separate question, and
for Engram it is *not* to refuse to open the database — recall works without the vector lane,
and a memory system that will not start because an optional accelerator changed is worse than
one that answers on FTS5 and says so. `doctor` names it; `embed --rebuild` fixes it.

**External-content FTS5 deletes must run while the row they mirror still exists.** D3's FTS5
index is external-content, so its delete reads the content table to find what to unindex.
Deleting the content row first leaves the index holding a phantom. This orders every deletion
path — `forget`, compaction, and any re-embed that replaces a row.

**The compute backend is chosen per host, and the wrong one fails as slowness rather than as
an error.** Metal on macOS, CUDA on Linux and Windows where a driver is present, CPU
otherwise (D28). A CPU fallback on a machine with a usable GPU is not a crash and not a
warning — it is the same silent half-speed failure the Apple SDK field produces, arriving by a
different route. So whatever selects the backend must also *report* what it selected, and
`doctor` must show it. Note that spike E's loading order — `NativeLibrary.Load` each
dependency by path before handing the main library to LLamaSharp — was derived from macOS
install-name behaviour and has no verified Windows or Linux equivalent.

**Fetching a native library is a supply-chain decision, not a download.** `init
--with-embeddings` has to pull `sqlite-vec` and llama.cpp for the host platform, pin an exact
version — the extension version and the schema it writes are coupled — and cache them in
`lib/`. The obvious implementation fetches a release archive over HTTPS and extracts it, and
the obvious implementation is not sufficient here: Engram would be downloading a native library
and loading it into its own process, so the archive's **checksum must be pinned and verified
before the file is written**, and a mismatch must abort rather than warn. An unavailable
download degrades to embeddings-off with a message naming the manual path, per the three states
`VectorExtensionState` already distinguishes. The uninstaller removes `lib/` with everything
else it created.

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

4. ~~**Does `sqlite-vec` load under Native AOT?**~~ **Answered — yes.** Spike D in §1.5:
   16/16 from the AOT binary, identical under JIT, +0.17 ms warm and +0.33 ms cold. Not
   through `NativeLibrary.Load`, which is the wrong mechanism for a SQLite loadable
   extension; D1's bullet is corrected. **This group no longer gates M4.** What it leaves
   behind for M4 to honour: the extension is per-connection and pooling disguises that, and
   a database holding a `vec0` table stays fully usable from a connection that cannot load
   it.
5. ~~**Does LLamaSharp work under Native AOT?**~~ **Answered — yes.** Spike E in §1.5: 13 of
   13 checks from the AOT binary, the same 13 under JIT, one `IL3000` warning that is real
   and must be answered by configuration rather than suppressed. **The ladder stops at rung
   2** — in-process is the default and the owned sidecar is not needed. What it leaves behind
   for M4 to honour: the ggml libraries must be `NativeLibrary.Load`-ed by explicit path in
   dependency order before `WithLibrary`, because their install names carry a version suffix
   their filenames do not.
5b. ~~Only if both pass: confirm embedding throughput…~~ **Answered.** 24.9 ms per embedding,
    40/sec, AOT faster than JIT. The seeded corpus backfills in about a second. Model load is
    6.5 s and paid once per process, which is what makes the embedder long-lived rather than
    per-batch.

**C. M4 proper, in dependency order (only after B is green)**

6. `IEmbedder`, and a **deterministic stub embedder first** — tier-2 tests need it to
   exist before anything real does, per D18's ordering caveat.
7. Vector table + backfill queue. Embedding runs server-side, off the hook path (D4).
8. ~~**`engram explain <query>` first (D21)**~~ — shipped, and it found the ranker blind to
   morphology before fusion was written (D30). Then RRF fusion (`k≈60`) in retrieval, with
   FTS5-only as a fully supported configuration when the lane is absent — degraded
   quality, never a missing result. The order within this step is the decision: an
   explainer written after the fusion it explains is written against remembered
   intentions rather than observed behaviour. **Fusion now has a prerequisite of its own:**
   the lexical lane has to actually reach recall. Today `RecallEngine` ranks by literal term
   overlap and never queries `fact_fts`, so "fuse BM25 with vectors" is two changes, and the
   first is worth landing and measuring alone.
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
showing paraphrase misses, and the seeded corpus is 45 facts. The owner has
said they want semantic search regardless; B is pure risk-retirement and worth doing
either way, but C's payoff scales with corpus size and with the gap in time between the
session that wrote a fact and the one that queries it.

---

*Open items deliberately left for later: entity-resolution fuzziness thresholds (start
exact + alias + case-insensitive, per spec §12); whether `UserPromptSubmit` recall
earns default-on (decide from M0/M1 coverage data); archive FTS for history search
(only if `LIKE` scans prove slow).*
