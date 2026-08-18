# 01 — Cross-machine sync

Status: design, revised. Parent: `docs/memory-expansion-spec.md` row 1.

## Goal

Replicate authored truth (facts) across a person's own machines, additively, using the
existing `backups/facts.jsonl` journal format and `FactJournal.Replay` (D32) as the sole
mechanism for both writing and applying — no second replay implementation. Git carries the
files; Engram never shells out to git.

## Non-goals

- No cloud/Postgres tier (parent spec, explicitly not adopted).
- No 3-way merge or diff logic of any kind.
- No auto-resolution of a `ux_fact_live` collision by timestamp or any other heuristic —
  sync skips, counts, and hands resolution to spec 02's verdict flow.
- Engram does not invoke `git` itself (no `git add/commit/push/pull` from Engram code).

## Inspiration

A comparable memory tool synchronizes its store across machines by writing new, immutable
chunk files that a person's own version-control system carries — never mutating a previously
written chunk, so there is nothing for that system to merge. Applying an incoming chunk is a
pure replay against the local store, with anything that cannot yet be resolved held in a
retry queue that eventually gives up rather than retrying forever.

## Design

**Core move**: a sync "chunk" is not a new file format — it is a slice of the existing
journal (D32's `JournalFact` shape), plus one new record kind. Applying a chunk is a call
to the existing `FactJournal.Replay(connection, facts, apply)` for fact records, and one
new, narrowly-scoped operation for close records. This is an extension of D32's
implementation, not a second one.

**Chunk format** (JSONL, one file per export, at `<home>/sync/<machine-id>/<seq>.jsonl`):
- `{"t":"fact", ...same fields FactJournal already emits into facts.jsonl (path-keyed,
  portable)...}` — applied via the existing `FactJournal.Replay`, unchanged.
- `{"t":"close", "subject":<path>, "predicate":<str>, "body":<str>, "valid_from":<int>,
  "valid_to":<int>, "superseded_by": {"body":<str>,"valid_from":<int>} | null}` — new.

Within one chunk, apply fact records before close records — creations before the operations
that depend on them existing, the same ordering principle a comparable sync design uses for
its own apply order.

**No manifest file — the directory listing is the index.** A design considered and rejected:
keeping a separate, small index file distinct from the chunk payloads, useful when payloads
are compressed or binary. Engram's chunks are plain JSONL (not gzipped — see below), so the
directory listing of `<home>/sync/<machine-id>/*.jsonl` *is* the index; a separate manifest
would be a second, redundant place to keep the same information in sync. Not adopting one is
a simplification, not an oversight. Chunks are left uncompressed for the same reason
facts.jsonl is uncompressed today (D32) — human-readable, directly diffable if a person ever
needs to look, and Engram's corpus sizes (measured elsewhere in this codebase at
5,097–50,097 facts) do not currently justify the complexity gzip would add to both the
writer and every future ad-hoc reader.

**Identity decision — reuse the 4-tuple, reject machine-id+local-id.** The constraint asks
me to choose between "machine id + local id" and "replay's 4-tuple" for fact identity.
I choose the 4-tuple (`subject path + predicate + body + valid_from`), already Engram's
content-address for a fact (D32), for three reasons: (1) it needs zero schema change —
introducing a separate machine-id+local-id identity would mean two identities for one
belief, which is what "one implementation per behaviour" forbids; (2) it is already
idempotency-safe and already resolved through `idMap` for supersession pointers (D32: "a
conflicted fact gets no idMap entry so a supersession aimed at it comes out unresolved");
close records reuse exactly this resolution path; (3) content identity is the correct
identity for an immutable, append-only belief record — two machines that received the same
fact via sync hold literally the same belief, and there is nothing a separate id would add.

`machine-id` still exists, but only as an opaque, locally-generated directory discriminator
(a small file at `<home>/sync/machine-id`, created on first `sync export`) so two machines
never collide writing chunk 1. It is never stored per-fact and never gates authority.

**Close-record semantics (precise).** On `sync import`, for a `close` record, resolve the
named 4-tuple against the local store:
1. No matching row exists locally → **defer**. Write a row to `sync_deferred_close`
   (below) and retry it on every future `sync import` until the matching fact arrives via
   its own chunk. This mirrors a retry-then-give-up pattern used by a similar tool for its
   own deferred-apply queue: a pending row that resolves once its dependency lands, and does
   not retry forever — after a bounded number of failed attempts it moves to a terminal state
   rather than retrying indefinitely. Engram adopts the same shape: after a configurable
   retry ceiling (exact number a product call, not this spec's to fix), `sync_deferred_close`
   rows move to a terminal `stalled` status, still visible via `sync status`, never silently
   retried forever and never deleted (the evidence a close was attempted stays on record).
2. Matching row exists and is **currently the live fact** for that `(subject, predicate)`
   slot → apply the close (`UPDATE fact SET valid_to=…, superseded_by=…`). Safe: the target's
   live belief is content-identical to what is being closed, so no other machine has
   diverged from it. This is what "a close authored on the origin machine... IS authored
   truth and replicates" means in practice — origin is provable by content match, not by a
   separate machine field.
3. Matching row exists but is **already closed** (same or idempotent close already applied)
   → no-op, counted `AlreadyPresent`.
4. The slot's **current live fact does not match** the named 4-tuple (the target authored a
   genuinely different belief for that subject+predicate) → **conflict**. Never touch it.
   Count it, surface via `engram sync status`, and stop — resolution is a human/agent
   decision recorded through spec 02's `engram_judge`, followed by an explicit
   `engram_revise`/`engram_forget` on one side, which itself syncs later. This is precisely
   "a close aimed at a fact the target authored independently."

**Schema delta** (all side tables — nothing added to `fact`):
```sql
CREATE TABLE sync_chunk_state (
  machine_id TEXT NOT NULL,
  seq        INTEGER NOT NULL,
  applied_at INTEGER NOT NULL,
  fact_count INTEGER NOT NULL,
  close_count INTEGER NOT NULL,
  PRIMARY KEY (machine_id, seq)
);
CREATE TABLE sync_deferred_close (
  subject_path TEXT NOT NULL,
  predicate    TEXT NOT NULL,
  body         TEXT NOT NULL,
  valid_from   INTEGER NOT NULL,
  valid_to     INTEGER NOT NULL,
  superseded_by_body TEXT,
  superseded_by_valid_from INTEGER,
  status TEXT NOT NULL DEFAULT 'deferred' CHECK (status IN ('deferred','stalled')),
  retry_count INTEGER NOT NULL DEFAULT 0,
  first_seen_at INTEGER NOT NULL,
  source_chunk TEXT NOT NULL,
  PRIMARY KEY (subject_path, predicate, body, valid_from)
);
```
Both are derived in the weak sense: `sync_chunk_state` is a cache of what has already been
applied (rebuildable by re-running `sync import` over the full chunk history — safe because
replay is idempotent, D32); `sync_deferred_close` is a work queue, rebuildable the same way.
Losing either costs re-scan time, never correctness (D8's "derived state is repairable"
holds for both, even though neither is regenerated *from `fact`* the way FTS/salience are —
they are regenerated from the chunk files, which is the parallel that matters here).

**CLI surface** (dry-run first, D49, matching `RepairCommand`'s `--apply` convention):
- `engram sync export [--apply]` — writes a new chunk of facts/closes since the last
  export; dry-run reports what would be written.
- `engram sync import [--apply]` — applies unapplied chunks in order; reports
  Written/AlreadyPresent/Deferred/Stalled/Conflicted.
- `engram sync status` — read-only: pending-import count per remote machine, deferred- and
  stalled-close counts, conflict count, last export/import times.

Naming follows Engram's own established subcommand convention (`backup take`, `queue
compact`, `repo ...`) rather than a single verb with mode flags — matching Engram's house
style rather than any external precedent.

**Git**: Engram does not shell out to git. `<home>/sync/` is a plain directory; the user
makes it (or a symlink to it) part of a git repo they control and pushes/pulls on their own
schedule or cron. Rejected the alternative (Engram running `git` directly) per the
constraint's own hint: it adds an auth failure mode (credentials Engram would have to hold)
and a merge failure mode (a git conflict inside Engram's process has no good recovery) for
no benefit — chunk files are already merge-free at the git level (each sync writes a new
file; nothing rewrites an existing tracked file's lines), so git's normal
fast-forward/three-way-merge machinery never has to resolve anything inside a chunk.

**Hook impact**: `sync import --if-new` (cheap directory-mtime check first) and
`sync export --if-due` (mirrors `backup take --if-due`) ride `MaintenanceLauncher`'s
detached session-start child, alongside `backup take`, `queue compact`, `repair --tokens`.
Not on the `file-touched` path — D4's 10 ms/never-opens-DB rule does not apply here, but a
measurement plan is still required (NEEDS-EVIDENCE below), matching how every other
`MaintenanceLauncher` job was measured before being added.

**Telemetry**: new kind `TelemetryEventKind.Sync = "sync"`, phases started/finished/failed
(D55 shape, matches Index/Embedding). No counts inside the event (D55); counts live in
`sync_chunk_state`/CLI output.

**Config**: new `[sync]` section — `enabled` (bool, default `false`, opt-in since it
requires a git repo the user set up themselves), `dir` (path override). Edited via
`ConfigEditor` with the `# written by engram` marker (D33).

## Invariants preserved

- **D8 (facts append-only)**: sync only ever inserts new rows (via unchanged
  `FactJournal.Replay`) or closes via the same `valid_to`/`superseded_by` path
  `engram_revise`/`engram_forget` already use. No column on `fact` changes shape.
- **D32**: extends `facts.jsonl`'s record shape and reuses `Replay`/`idMap` rather than
  building a parallel apply path.
- **D49**: `export`/`import` dry-run by default, `--apply` required to write.
- **D4**: no work added to `file-touched`; new hook work rides the existing detached child.
- **"Derived state is repairable"**: both new side tables are rebuildable by re-running
  import from the full chunk history.

## Tests by tier (D9)

- **Tier 1**: close-resolution branch logic (defer / apply / no-op / conflict / stalled) as
  a pure function over a fabricated local-fact table. Falsify: delete the live-check branch
  (case 2 vs 4) and confirm a test asserting "conflict case leaves the divergent fact
  untouched" starts failing (the fact gets wrongly closed). Falsify the retry ceiling
  separately: remove the dead-letter transition and confirm a test asserting "a close with
  no matching fact ever reaches `stalled` after N attempts" starts failing (it retries
  forever).
- **Tier 2**: two `SandboxHome` instances simulate two machines. Export from A, import into
  B, assert fact sets match. Revise on A, export, import into B, assert B's copy closes.
  Seed an independently-authored fact on B for the same slot *before* importing A's chunk,
  assert B's fact is untouched and a conflict is counted. Falsify: disable the live-match
  check and confirm this last test starts failing. Rebuildability guard: drop
  `sync_chunk_state`/`sync_deferred_close`, re-run import from the full chunk history,
  assert an identical resulting fact set.
- **Tier 3**: end-to-end `sync export`/`import` against the published binary and two real
  home directories; file-snapshot invariant (nothing outside the sync dir and the target DB
  changes) mirroring `doctor`'s own end-to-end pattern.

## Measurements

- Chunk export/import cost at two corpus sizes (mirroring the 5,097 / 50,097 scale already
  used elsewhere in this codebase), both for zero-new-chunks and for a realistic pending
  chunk (~100 facts).

## Open questions / NEEDS-EVIDENCE

1. **[measurement]** Cost of `engram sync import --if-new` with zero pending chunks (target
   comparable to `backup take --if-due`'s cheap-skip cost) and with a pending chunk, at
   5,097 and 50,097 live facts. Decides whether it is safe on every session-start or needs
   its own `--if-due`-style coarser gate.
2. **[measurement]** Same for `engram sync export --if-due`.
3. **[verify, non-empirical]** Confirm `FactJournal`'s exact `JournalFact` field names/types
   at `src/Engram.Core/FactJournal.cs` so the chunk "fact" record reuses them byte-for-byte
   rather than by inference from this spec.
4. **[product decision, not evidence]** The exact `sync_deferred_close` retry ceiling before
   a row moves to `stalled` (count-based, time-based, or both) is left open. A default should
   be chosen during implementation and can be config-overridden.
