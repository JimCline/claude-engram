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

Six architectural forks were adjudicated with Fable. Each is now a decision, not an
option.

### D1 — Packaging: AOT core, Roslyn sidecar, native libs in the data directory

- **`engram`** — Native AOT, single file. Contains CLI, MCP server, temporal store,
  retrieval, and the universal + document analyzers. All pure managed. SQLite
  statically linked via `SQLitePCLRaw.bundle_e_sqlite3`.
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
4. **The `file-touched` hook does not open the database.** It appends one line to a
   spool file under `~/.engram/queue/`, drained by the MCP server or the indexer. This
   makes the < 10 ms budget unconditional rather than "true unless an indexer chunk is
   committing."

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

Cross-platform matters too: the spec promises four RIDs, so CI runs the full suite on
osx-arm64 and linux-x64 at minimum. A test that only ever ran on the author's Mac is a
claim, not evidence.

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

**Gate:** M0/M1 telemetry should show that missed recalls are substantially
code-structure questions. If they are not, M3 shrinks or moves behind M4.

### M4 — Embeddings

`IEmbedder` providers, `sqlite-vec` lane, RRF fusion, batch/backfill,
`embed --rebuild`. Reconsider D5 here with vector evidence.

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

---

*Open items deliberately left for later: entity-resolution fuzziness thresholds (start
exact + alias + case-insensitive, per spec §12); whether `UserPromptSubmit` recall
earns default-on (decide from M0/M1 coverage data); archive FTS for history search
(only if `LIKE` scans prove slow).*
