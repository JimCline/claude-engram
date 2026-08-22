# 01 — Cross-machine sync

Status: design, revised (amended 2026-08-18 — chunk-completeness gap under non-atomic
transports; extended 2026-08-18 — scoped export: `[sync] scope` baseline plus per-fact
always-sync opt-in; extended 2026-08-18 — `fact_sync_request` durability via
`sync_requests.jsonl`; extended 2026-08-19 — iCloud/Drive/Dropbox folder-sync: confirmed
`[sync] dir` already covers it, no new transport code, 3 product-decision forks flagged;
extended 2026-08-19 — Jim decided the 3 forks: staleness detection designed (fork 1),
chunk retention/pruning via `sync compact` designed (fork 2), dir preflight validation
closed as blind lazy-create (fork 3); extended 2026-08-19 — Jim resolved fork 2's residual
sub-fork: time-window retention confirmed over ack-based, both defaults confirmed as
proposed (`stale_after_days=14`, `retain_days=90`) — forks 1 and 2 are now both fully
settled, no open forks remain for this transport work; extended 2026-08-19 — closed a
review-found implementation gap: none of `sync export/import/compact` checked `[sync]
enabled` before writing, so a disabled-by-default install got a full, unfiltered fact
export written to `<home>/sync/` at its first session start after this shipped, not merely
a marker file — two-layer fix designed (`MaintenanceLauncher` stops invoking sync when
disabled; each CLI handler also refuses `--apply` when disabled) plus a `doctor` warning
for installs that already have stray content, 2 new product-decision forks flagged: open
questions 14 and 15; extended 2026-08-19 — Jim resolved both forks: fork 14 — mirror the
`index`/`--auto` precedent, gating only `MaintenanceLauncher`'s ambient invocation, not the
CLI handlers, so an explicitly typed `sync export/import/compact --apply` bypasses `[sync]
enabled`; fork 15 — no backward-compatibility handling needed, since this fix ships before
the gap reached any real install, so there is no stray pre-fix export anywhere to warn
about or clean up, and the `doctor`/`CheckSync` `Warn`-upgrade design is dropped entirely
— the fix is now a single gate at `MaintenanceLauncher`, no open forks remain for this
fix).
Parent: `docs/memory-expansion-spec.md` row 1.

## Amendment note

The design below assumes chunk delivery is atomic — a `sync import` never observes a chunk
file mid-write, because git's own `fetch`/checkout gives you the whole file or none of it.
That assumption is baked into how the implementation (`Sync.cs`, committed at `4801c01`)
treats a parse failure. `EnumerateChunkFiles` (`Sync.cs:685-701`) reads each chunk whole via
`File.ReadAllLines`, and `DiscoverPendingChunks` (`Sync.cs:628-682`) hands every line to
`TryParseLine` (`Sync.cs:704-719`), which wraps `JsonNode.Parse` in a try/catch and returns
`null` on any `JsonException`; the caller `continue`s past a `null` line and moves on. A
malformed line and a truncated final line are indistinguishable at that point — both are
simply skipped — and because `sync_chunk_state` is keyed `(machine_id, seq)` with no
mechanism to revisit a seq once recorded, a line dropped this way is dropped forever, even
after the rest of the file finishes arriving.

Under git this is inert: a checkout is all-or-nothing, so a parse failure really does mean
corruption, which practically never happens. It stops being inert the moment `<home>/sync/`
is carried by anything that replicates content progressively rather than as an atomic
transfer — iCloud Drive, Dropbox, Google Drive, or any consumer file-sync client. None of
those replicate a `rename()` as a single atomic operation on the receiving machine the way
git's protocol does; a receiver's directory listing can show `<seq>.jsonl` before its bytes
have finished arriving. Writing the sender's side with a tmp-then-rename convention (write
`<seq>.jsonl.partial`, rename to `<seq>.jsonl` once complete — already how `Sync.Export`
writes every chunk, confirmed at `Sync.cs:296-315`; see the Folder-sync transport section
below) fixes visibility on the *sender's* own filesystem but does not make that guarantee
travel across a third-party sync client's upload/download hop — the receiver can still
observe the final name while the transfer is incomplete. No configuration on Engram's side
can prove a specific external sync client replicates renames atomically; it is not something
Engram is in a position to verify or rely on.

**Resolution: the reader must be defensive regardless of transport, and "defensive" here
means more than "don't crash."** The fix touches neither the writer nor the chunk format — it
changes when a chunk is allowed to be finalized:

1. A `sync_chunk_state` row for `(machine_id, seq)` may only be written once an import pass
   parses that chunk file cleanly end-to-end — every non-blank line valid JSON, and the
   file's last line newline-terminated. A parse failure on any line means the *whole chunk*
   is not yet finalizable this pass, not that the one offending line is discarded.
2. An unfinalized chunk is retried on every subsequent `sync import`, the same shape already
   used for `sync_deferred_close` (retry until resolved, then a bounded ceiling). If a chunk
   still fails to parse cleanly after that ceiling, it moves to a `stalled` state surfaced via
   `sync status`, exactly like a stalled close — never silently dropped, never retried
   forever. The exact retry-ceiling/schema shape for tracking an unfinalized chunk (a new
   table mirroring `sync_deferred_close`, or extra columns on `sync_chunk_state`) is left to
   implementation, the same way item 4 in Open questions leaves the close-record ceiling open.
3. This applies independent of which transport carries `<home>/sync/`. It costs nothing under
   git (a chunk parses clean on the first pass, so it finalizes immediately) and is what makes
   a non-git, non-atomic transport (iCloud/Drive/Dropbox) safe to point `<home>/sync/` at
   without any other change to Engram.

This also loosens the Git section below: git was chosen partly because its fetch protocol
gives all-or-nothing delivery for free, avoiding the need to invent this handshake. With the
reader made defensive regardless, that argument for git specifically no longer holds —
`<home>/sync/` can be "any directory a sync mechanism the user already runs replicates," not
specifically a git repository. Engram still never shells out to git or any other sync tool
either way.

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
(a small file at `<home>/sync/machine-id`, created on first `sync export` — confirmed as
`Sync.ResolveMachineId()`/`GenerateMachineId()`, a fresh 4-byte hex id, `Sync.cs:147`) so two
machines never collide writing chunk 1. It is never stored per-fact and never gates
authority.

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

**Scoped export.** `sync export` by default exports every live fact new since the last
export (unchanged). A `[sync] scope` config value — `all` (default), `user`, or
`repo:<identity>` — narrows that to a baseline, and an independent per-fact "always sync"
flag rides on top of it: a fact flagged this way exports regardless of the current baseline,
so switching to a narrow scope never locks out one ad-hoc fact someone explicitly asked to
replicate. The two are composable by design (OR, not a mode switch), per the product
requirement: someone running `scope=user` can still sync a single project fact without
leaving user scope.

Baseline evaluation:
- `all` — no restriction (`1=1`); unchanged behavior for anyone not opting in.
- `user` — `fact.scope = 'user'`.
- `repo:<identity>` — a fact counts as repo-tied if either of two things is true, because a
  repo's facts arrive through two different writers with two different addressing schemes:
  - it is a code-indexed fact under that repo's registry path: `fact.path = :repoPath OR
    fact.path LIKE :repoPath || '/%'` (mirror the existing repo-path prefix query
    `CodeIndexer` already runs for its own deletion-detection scan — do not reinvent the
    prefix-scan idiom separately);
  - **or** it is a session-scope fact (`fact.scope = 'session'`) recorded during a session
    that ran in that repo: `fact.session_id IN (SELECT id FROM session WHERE repo_path =
    :repoPath)`. `session.repo_path` (`docs/engram-schema.sql:239`) records the memory path
    a session ran in; `SessionFacts.PathFor` (`SessionFacts.cs:70-74`) builds a session
    fact's own `path` from session id / agent name / a fingerprint of the statement text —
    **it never embeds a repo or project identity** — so path-prefix matching alone (the
    original grounding for this feature) would only ever catch code-indexed facts and
    silently exclude the session notes a person actually means by "facts tied to this repo."
    That is most of what use case [2] in the user's four is actually asking for, so the join
    through `session.repo_path` is load-bearing, not an enhancement.
  - `user`-scope facts are excluded from `repo:<identity>` even when their `session_id`
    happens to point at a session that ran in that repo — user facts are meant to be
    durable and cross-project by the same distinction `fact.scope` already draws; an
    incidental session location doesn't change that. Someone who wants one particular user
    fact synced under a narrow repo scope already has the always-sync flag for exactly that.

  `:repoPath` is resolved once, before filtering, from the CLI's `--scope=repo:<value>` (or
  config's `scope = repo:<value>`) by matching `value` against `repo_registry` (`repo_path`
  PRIMARY KEY, `identity` TEXT NOT NULL — `docs/engram-schema.sql:330-342`; there is no
  `RepoRegistry` C# type, this is a direct SQL lookup): `SELECT repo_path, identity FROM
  repo_registry`, then in application code match rows where `identity == value` (the
  normalized git remote URL or root path `CodeIndexer.ResolveIdentity` produces — not
  typically what a person types from memory) **or** `repo_path` ends with `/` + `value` (the
  trailing registry-path segment, e.g. `acme-api` — the friendlier match most people will
  actually type). Zero matches is an error naming the value and pointing at the existing
  `engram repo` command surface to list enrolled repos; more than one match is an error
  listing every matching `repo_path` and asking for the full `identity` instead of the short
  form.

**Where this hooks into `Sync.Export()` (verified against the existing implementation,
`Sync.cs:209-279`).** Export today has two independent selection paths, not one, and this
feature only touches the first. Fact-selection reads the *entire* `fact` table
unconditionally via `FactJournal.Read` (`FactJournal.cs:180-189`, no WHERE clause — it also
backs `backup take`'s full journal write, so it must not grow a scope-specific filter itself)
and keeps a candidate only if `!allExported.Contains(identity)` (`Sync.cs:226`, `allExported`
built by `ScanOwnChunks` re-reading this machine's own past chunk files). Scope filtering
adds a second, sync-only set alongside that check rather than touching `FactJournal.Read`: a
dedicated query, run once per export —
```sql
SELECT se.path, f.predicate, f.body, f.valid_from
FROM fact f JOIN entity se ON se.id = f.subject_id
WHERE {scope-clause} OR EXISTS (SELECT 1 FROM fact_sync_request WHERE fact_id = f.id)
```
— collected into a `scopeEligible` set keyed by the same 4-tuple identity `allExported`
already uses, and `Sync.cs:226`'s guard becomes
`!allExported.Contains(identity) && scopeEligible.Contains(identity)`.

Close-selection (`Sync.cs:232-245`) needs **no change and gets none**. It does not re-derive
its candidates from `fact` at all: it iterates `openAtExport` (identities live at some *past*
export, from the same `ScanOwnChunks` chunk-file rescan), drops anything already in
`closedExported`, and includes what `LookupExact` (`Sync.cs:892-894`) now reports as closed.
Every identity that can ever reach `openAtExport` got there by having previously passed the
fact-selection filter above — flagged or scope-matched, it makes no difference once exported
— so a fact's close transmits whenever it closes locally, regardless of what `[sync] scope`
says *at that later moment*. This is why Open question 6 below closes as "not a gap": the
"was this ever exported by me" record a new table would provide already exists, in the form
of this machine's own chunk-file history that `ScanOwnChunks` re-reads on every export.

**Per-fact opt-in.** `engram_remember` gains an optional `sync: bool = false` parameter
(appended after `supersedes` in the tool's existing parameter list, `EngramMcpTools.cs:90-
102`). Per D51 (a trigger phrase has to live in a compile-time-constant tool description,
not the primer, which decays between compactions and doesn't fire for every session/agent),
its `[Description]` carries the trigger explicitly:

> True to flag this fact for cross-machine sync regardless of the current sync scope. Set
> true when the user says "share engram" (or otherwise explicitly asks to sync or share this
> across machines) followed by content — that phrase means store AND flag for sync, not
> just store.

(Content, not final formatting — match the style of this tool's existing parameter
descriptions, `EngramMcpTools.cs:94-101`, when wiring the literal attribute text.)

When true, the write inserts a `fact_sync_request` row for the new fact id. This is "decided
at write time" only in the sense that the *initial* write only happens once —
`engram_revise` (which creates a new fact row and closes the old one, same as any other
supersession) auto-propagates it: if the fact being revised has a live `fact_sync_request`
row, `Revise` inserts an equivalent new row (fresh `requested_at`) for the freshly created
fact id. `engram_revise` also gets its own optional `sync: bool?` parameter — default `null`
means inherit the target's current flag; `true`/`false` explicitly overrides it either
direction on the new fact. Without the auto-propagation, a fact someone deliberately flagged
to always sync would silently stop syncing the moment they revised it, which is a worse
failure mode than the one D64's "don't carry `details` forward on revise" guards against:
`details` is belief content that can go stale next to a changed body, while `sync` is an
operational replication flag with nothing to go stale, so the two cases warrant opposite
defaults. `engram_forget` needs no change: it closes the fact (`valid_to` set), which
export's existing `valid_to IS NULL` filter already excludes from every future scope
evaluation, sync-flagged or not — the close record is what still needs to propagate, and
that is the existing close-record machinery (see above), untouched by this feature.

**Naming collision avoided.** The trigger phrase is "share engram," not "send message" —
chosen specifically to avoid colliding with this environment's own `SendMessage`
agent-to-agent tool (see Open question #7).

**Interaction with the chunk-completeness amendment above: none.** That amendment governs
when an already-written chunk file is allowed to finalize on the *import* side (does it
parse cleanly end-to-end) — a property of chunk file syntax, not of which facts a chunk
contains. Scope filtering only changes what gets selected into a chunk at *export* time; the
emitted records are the same `{"t":"fact",...}` / `{"t":"close",...}` shapes either way, so a
scope-filtered chunk is exactly as parseable, and finalizes under exactly the same rule, as
an unfiltered one.

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
CREATE TABLE fact_sync_request (
  fact_id      INTEGER NOT NULL PRIMARY KEY REFERENCES fact(id),
  requested_at INTEGER NOT NULL
);
```
`sync_chunk_state` and `sync_deferred_close` are derived in the weak sense: `sync_chunk_state`
is a cache of what has already been applied (rebuildable by re-running `sync import` over the
full chunk history — safe because replay is idempotent, D32); `sync_deferred_close` is a work
queue, rebuildable the same way. Losing either costs re-scan time, never correctness (D8's
"derived state is repairable" holds for both, even though neither is regenerated *from
`fact`* the way FTS/salience are — they are regenerated from the chunk files, which is the
parallel that matters here).

`fact_sync_request` is different in kind and does **not** get that same "derived, rebuildable"
treatment: nothing about a fact's content or history says whether its author explicitly asked
for it to always sync, so there is no source to rebuild the table from — it is the one
authoritative record of that decision, and losing a row is a real loss (a fact silently stops
being always-synced), not a cache eviction. In shape — insert-only, joined at read time, no
column added to `fact` — it follows this spec's own existing side-table convention for its
first two tables, and also mirrors spec 02's `fact_relation` (`docs/engram-schema.sql:419-
427`), which is the same pattern applied to a different kind of per-fact metadata.

**Durability for `fact_sync_request` — `sync_requests.jsonl`.** The paragraph above asserts
`fact_sync_request` is authoritative and must not be silently lost, and cites spec 02's
`fact_relation` as the pattern it mirrors — but only in shape (insert-only, joined at read
time), not in durability, and that gap is real (found by review): `fact_relation` got a
portable journal (`RelationJournal.cs` → `relations.jsonl`, wired into `backup take`/
`backup replay`, commit `770519d`) that `fact_sync_request` never received. Unlike
`sync_chunk_state`/`sync_deferred_close` above, `fact_sync_request` has no external durable
copy at all today — the live SQLite table is the only record, and D31's `.db` snapshot only
restores into the exact schema version that wrote it, which is precisely the case D32
invented `facts.jsonl` to survive. A `fact_sync_request` row would not.

Decided: mandate the missing journal rather than exempt the table from the durability claim
this spec already makes for it — there is no new evidence that would justify weakening that
claim, and the fix is cheap because it is a direct mirror of code that already exists and
already solves this exact problem (a table referencing `fact(id)` that must survive a restore
into a newer schema where those ids do not exist yet):
- **Record** (`JournalSyncRequest`, mirroring `RelationJournal.cs`'s `JournalRelation`
  record): the fact's portable identity, not its id — `FactSubject`, `FactPredicate`,
  `FactBody`, `FactValidFrom` (the same 4-tuple used everywhere else in this spec) — plus
  `RequestedAt`. JSON field names on disk: `fact_subject`, `fact_predicate`, `fact_body`,
  `fact_valid_from`, `requested_at`, under the same header shape (`format`, `format_version`,
  `schema_version`, `written_at`) `relations.jsonl` already writes, living beside
  `facts.jsonl`/`relations.jsonl` (`SyncRequestJournal.PathIn(EngramHome)`, mirroring
  `RelationJournal.PathIn`).
- **Write**: `SyncRequestJournal.Write(connection, home, now)` — `SELECT ... FROM
  fact_sync_request JOIN fact ON fact.id = fact_sync_request.fact_id JOIN entity ON
  entity.id = fact.subject_id`, the same identity-resolution shape `RelationJournal.Write`
  uses per side of a relation, run once per `fact_sync_request` row instead of twice. Called
  from `BackupCommand.Take` in the same `if (settings.Journal)` block that already calls
  `FactJournal.Write`/`RelationJournal.Write` (`BackupCommand.cs:99-116`), under the same
  try/catch that treats a journal failure as a warning rather than failing a backup whose
  snapshot already landed.
- **Replay**: `SyncRequestJournal.Replay(connection, syncRequests, facts, idMap, apply)`,
  resolving each row through the identical tuple → journal-id → `idMap` → target-id chain
  `RelationJournal.Replay` already implements (`RelationJournal.cs:216-226`'s `Resolve`
  helper, applied once per row instead of twice). A resolved row is `Written` if
  `fact_sync_request` has no row yet for the target fact id, `AlreadyPresent` if it does
  (never updates `requested_at` — the table is insert-only, so the earliest recorded request
  wins). An unresolved row (its fact isn't in this replay's `idMap` — the conflicted-fact
  case D32 already describes) is skipped and counted `Unresolved`, mirroring
  `RelationReplayResult` minus the "conflict" bucket, which does not apply here: a
  sync-request row has no content that can diverge from another one, only "requested" or "not
  requested." Called from `BackupCommand.Replay` alongside `ReplayRelations`
  (`BackupCommand.cs:367-433` is the method to mirror) — independently of it, since
  `fact_sync_request` and `fact_relation` don't reference each other, so the two replay calls
  have no ordering constraint between them.
- **Fingerprint**: add a `SyncRequests` field to `BackupFingerprint` (`BackupStore.cs:27-37`)
  and a `(SELECT COUNT(*) FROM fact_sync_request)` column to its `Read` query
  (`BackupStore.cs:39-71`) — the same two-line addition commit `770519d` made for `Relations`
  to fix "a judge-only session goes unbacked-up indefinitely," the same bug class review just
  found again here. Do this now rather than waiting for it to be rediscovered for
  sync-request-only sessions: `BackupFingerprint` is a record struct compared by value, so
  adding the field is sufficient on its own for `backup take --if-due`'s change detection to
  notice a sync-request-only session — no other code path needs to change. `IsEmpty`
  (`Facts == 0 && Entities == 0 && Edges == 0`) needs no matching update: `fact_sync_request
  .fact_id REFERENCES fact(id) NOT NULL`, so a store with zero facts cannot have a nonzero
  `fact_sync_request` count either — the same reason `IsEmpty` never needed to check
  `Relations`.

No new CLI surface and no new config: this rides `backup take`/`backup replay`'s existing
`--apply`/dry-run behavior (D49) exactly as `relations.jsonl` does, and needs no measurement
gate of its own — `fact_sync_request` is expected to stay small (only facts someone
explicitly flagged), the same reasoning that let `relations.jsonl` join the existing
`if (settings.Journal)` block without a separate NEEDS-EVIDENCE item.

**CLI surface** (dry-run first, D49, matching `RepairCommand`'s `--apply` convention):
- `engram sync export [--apply] [--scope=all|user|repo:<value>]` — writes a new chunk. *New
  fact* records are restricted to the scope baseline (default: `[sync] scope`, itself
  defaulting to `all`) OR flagged `fact_sync_request` rows; `--scope` overrides the config
  value for this run only. *Close* records are unaffected by `--scope`/`[sync] scope` — see
  the Scoped export subsection above — so a fact already exported keeps closing correctly on
  every future run even after scope later narrows. Dry-run reports what would be written.
- `engram sync import [--apply]` — applies unapplied chunks in order; reports
  Written/AlreadyPresent/Deferred/Stalled/Conflicted.
- `engram sync status` — read-only: pending-import count per remote machine, deferred- and
  stalled-close counts, conflict count, last export/import times, and — per known peer
  machine-id — last-observed time and a `STALE` marker past `[sync] stale_after_days` (see
  Staleness/liveness detection, below).
- `engram sync compact [--apply] [--if-large]` — folds this machine's own chunk history down
  to its current live-export state, dropping closed-and-old fact/close pairs past
  `[sync] retain_days` (see Chunk retention/pruning, below). Dry-run reports what would be
  merged/dropped.

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

**Folder-sync transport (iCloud Drive / Google Drive / Dropbox) — no new transport,
`[sync] dir` already covers it.** Verified against the existing implementation before
designing anything new, per the constraint that this must not duplicate or reopen the
scoped-sync work above: there is no git-specific code anywhere in the sync path to be
"instead of." `Sync.Export`/`Sync.cs`'s reader (`EnumerateChunkFiles`/`DiscoverPendingChunks`,
cited in the Amendment note above) and writer (`Sync.Export`, `Sync.cs:222-318`) both operate
on a plain directory; Non-goals already states "Engram does not invoke git itself." Git was
never a mode Engram implements — it is what the user does *outside* Engram with the directory
`[sync] dir`/`home.SyncDir` already points at (commit + push/pull, on their own schedule, per
the Git section above). Pointing that same directory at an iCloud Drive, Google Drive
Desktop, or Dropbox-synced folder instead of a git working tree requires **zero code change**
and **zero new config key** — it is already exactly what `[sync] dir` is for.

So the question of whether folder-sync is a mutually-exclusive transport setting or something
configurable alongside git doesn't apply: there is one directory, one config surface
(`[sync] dir`), and no transport enum to add. A user could even point `dir` at a folder that
is simultaneously git-tracked *and* iCloud-synced (e.g. a git repo living inside
`~/Library/Mobile Documents/com~apple~CloudDocs/`) and both mechanisms would carry the same
files without conflict — Engram only ever sees a directory of JSONL chunk files, never
anything git- or iCloud-specific.

*Path resolution (D42).* Already correctly built, nothing new needed: `EngramHome.SyncDir`
is the default (`<home>/sync/`, lazily created on first `sync export` — `EngramHome.cs:56-58`'s
own doc comment already states this and that callers, not `EngramHome`, resolve the config
override); `SyncSettings.Read(config)` reads the `[sync] dir` override as a plain string
(`SyncSettings.cs:55`); `SyncSettings.ResolveDir(home)` picks the override if present, else
`home.SyncDir` (`SyncSettings.cs:62-66`). `SyncDir`'s doc comment already states the exact
rule this spec would otherwise have to invent: "`[sync] dir` in config overrides this
default; callers resolve that override themselves rather than this type reaching into
`ConfigFile`, which is not this type's job."

One constraint this spec does add, because nothing in the codebase resolves it today:
**`[sync] dir` must be an absolute, already-expanded path — no `~`.** Expanding `~` needs
`HOME`, and D42 permits exactly one reader of `HOME`/`USERPROFILE` (`EngramHome`);
`SyncSettings` is deliberately not that type (its own doc comment says so, above), so it must
not gain ad hoc tilde-expansion, and no existing config key in this codebase does either
(verified — `SyncSettings.cs:55` reads the raw string with no expansion step, and `dir` is the
only config key of this shape in the codebase). The fix is not a new resolver — it's telling
the user to paste the real path. iCloud Drive's actual on-disk location (e.g.
`~/Library/Mobile Documents/com~apple~CloudDocs/EngramSync` on macOS, fully expanded) is
discoverable via Finder's "Copy … as Pathname," and Google Drive Desktop / Dropbox both expose
an ordinary mounted folder path the same way. Document this in the CLI/config help text; do
not add expansion code for it.

*Write/read path — already sufficient, verified rather than assumed* (the task this section
answers explicitly asked not to take the Amendment note's coverage on faith). The Amendment
note above describes "writing the sender's side with a tmp-then-rename convention" as the fix
for sender-visibility; it is not hypothetical — it is what `Sync.Export` already does
(`Sync.cs:296-315`): writes every fact/close line to `<seq>.jsonl.partial` under
`FileShare.None`, then `File.Move(partial, final, overwrite: false)` once writing completes.
`partial` and `final` are both under the same `<syncRoot>/<machineId>/` directory, so the move
is a same-volume rename — atomic by construction (POSIX `rename(2)`/.NET `File.Move`
same-directory semantics), not merely convention. This means:
- A folder-sync client watching the directory (iCloud/Drive/Dropbox all use filesystem-event
  or polling watchers) can never observe `<seq>.jsonl` in a partially-written state *on the
  machine that wrote it* — the name simply doesn't exist until the whole file is already
  complete on disk.
- The defensive-reader fix from the Amendment note (finalize `sync_chunk_state` only on a
  clean end-to-end parse) is therefore a backstop against a *different* failure point than
  the writer: the sync client's own upload/download hop on the *receiving* machine, where the
  client can present the final filename before its bytes have finished arriving over the
  network, regardless of how cleanly the sending machine wrote it. Both pieces are required
  together and neither alone would suffice — the atomic write prevents the near machine from
  ever producing a torn file to sync; the defensive read protects against the far machine
  observing one anyway, mid-transfer, through the sync client rather than through Engram.

No writer or reader change needed for this transport.

*Durability/journal handling.* Reuses everything spec 01 already has, unchanged — this
transport changes *where the directory lives*, not what's in it or how it's replayed.
`sync_chunk_state`/`sync_deferred_close` rebuildability (Schema delta, above) and
`sync_requests.jsonl` (Durability for `fact_sync_request`, above) are both
transport-independent already; neither references git or any transport concept. Nothing to
add.

*Open forks for Jim (not decided here, as first raised 2026-08-19):*
1. **Staleness/liveness detection.** Git gives an implicit staleness signal — the user
   manually runs `git pull`, so silence is expected and self-explained. A folder-sync client
   is expected to run continuously in the background; if it's paused, signed out, over quota,
   or the folder was never actually enrolled in sync, chunks pile up locally and Engram has
   no way to tell "nothing new to sync" from "sync is broken and nothing is arriving." Should
   `sync status` or `doctor` track "time since any peer machine's chunk last appeared" and
   warn past some threshold? Monitoring/complexity-vs-silence tradeoff, not something to
   default without input.
2. **Chunk retention/pruning.** Spec 01 has no pruning step today for *any* transport —
   chunks accumulate forever in `<machine-id>/*.jsonl`, a pre-existing gap this transport
   doesn't create but does change the cost of: a git repo's own history/GC absorbs old chunks
   essentially for free, while a continuously-synced consumer folder (iCloud/Drive) may have
   per-file sync overhead or storage quota that makes unbounded accumulation actually cost
   something. Address retention now as part of this work, or leave it as the existing open
   gap for later? Depends on actual usage volume.
3. **Preflight validation of `dir`.** `home.SyncDir`'s default is lazily created on first
   `sync export` (existing behavior) — should a *configured* `dir` get the same blind
   `Directory.CreateDirectory` treatment, or should `sync export`/`doctor` check first that
   the path exists and looks like it's inside an actual synced location, to catch "`dir` was
   set but iCloud/Drive was never actually finished setting up on this folder" before chunks
   silently pile up somewhere nothing will ever sync? Related: iCloud Drive can evict a local
   file to a cloud-only placeholder it re-downloads on demand — whether that's worth detecting
   explicitly, or is just left to surface as an ordinary read error the way any other
   unreadable chunk already does, is bundled into this same "how much do we proactively
   validate" call.

**Jim's decisions on the three forks above (relayed 2026-08-19):** fork 1 — build it, design
below. Fork 2 — address now, with the same rigor as the rest of this spec, not deferred;
design below, argued rather than defaulted; its one residual sub-fork (time-window vs.
ack-based mechanism, exact defaults) was resolved same-day too — see the design's own "Jim's
decision" note below, and Open question 11. Fork 3 — closed: blind lazy-create, matching
`home.SyncDir`'s existing default behavior; no preflight validation, no further work.

**Staleness/liveness detection (`sync status`/`doctor`) — decided.** The detection needs a
notion of "known peer" and, per peer, "when was something from it last observed" — both
answerable from what already exists, no new schema.

*Known peer* = any subdirectory of `[sync] dir` other than this machine's own `machine-id`
(read from `<dir>/machine-id`) and other than non-directory entries (the `machine-id` file
itself sits as a sibling file at `<dir>/machine-id`, not nested under a peer directory — this
is the same exclusion `DiscoverPendingChunks` already has to make to avoid trying to import a
machine's own exports). A peer becomes "known" the moment its directory exists at all, even
before this machine has successfully applied anything from it — enrollment, not successful
import, is what makes a peer worth tracking.

*Last-observed time for a known peer*, computed at read time (no new column, no new table):
`MAX` of two independently-gathered values, either of which may be absent:
1. `MAX(applied_at)` from `sync_chunk_state WHERE machine_id = :peer` — the newest chunk this
   machine has *successfully finalized* from that peer.
2. The newest filesystem last-write-time across every file (including `.partial`, deliberately
   not excluded) under `<dir>/<peer>/*.jsonl*` — catches "something arrived" even for a chunk
   that has never cleanly finalized per the Amendment note's defensive-reader rule, which is
   itself evidence the peer is active, just stuck, a different symptom from silence.

A peer with neither value present (directory exists, nothing in it, e.g. just enrolled and
hasn't exported yet) reports "never observed" and is never flagged stale — there is nothing
yet to compare against, the same reasoning D37 applies to a deliberately-off feature: absence
is not automatically a fault.

*Evaluation is a pure function*, `SyncStaleness.Evaluate(peers, now, staleAfter)` over
already-gathered `(machineId, lastObservedUtc?)` pairs — kept separate from the I/O above
specifically so it is Tier-1-testable without a real `sync_chunk_state`/filesystem, matching
this spec's existing pure-function testing convention (see Close-record semantics, Tests by
tier). `IsStale(peer) = lastObservedUtc.HasValue && (now - lastObservedUtc.Value) > staleAfter`.

*Threshold*: new config `[sync] stale_after_days` (int, default **14**). Argued, not
measured — there is no usage-cadence data to measure this from yet, and Jim asked me to pick
it rather than leave it open. Reasoning: long enough that a second machine used roughly
weekly, or offline for a one-to-two-week trip, doesn't false-positive on the very next check;
short enough to surface a genuinely broken folder-sync client (paused, signed out, over
quota) well inside a month, before a person forgets they ever configured it. Config-overridable
per the same `ConfigEditor`/D33 marker convention as every other key in this section.

*Where it surfaces — both, at different granularity, mirroring the split D54 already uses
between `embedding.json`'s full detail and `doctor`'s one-line summary*:
- `engram sync status` (CLI surface, above): full detail — every known peer's last-observed
  time (or "never") and age, with a `STALE` marker past the threshold. This is the surface
  for someone actively investigating.
- `doctor`: one summary row, using the existing `Diagnosis(string Name, DiagnosisState State,
  string Detail, string? Fix)` shape (`Diagnostics.cs:24`, states `Ok | Off | Warn | Broken`
  at `Diagnostics.cs:8-20`), wrapped in the existing `Try(checks, name, action)` guard
  (`Diagnostics.cs:120`) the same as every other check, so a throwing check degrades to one
  broken row rather than killing the report (D37). `new Diagnosis("sync", ...)`:
  - `DiagnosisState.Off` when `[sync] enabled = false` — the existing precedent for a
    deliberately-disabled feature (`Diagnostics.cs:170-184` for the analogous embedding-off
    case), not a fault.
  - `DiagnosisState.Ok` when enabled and either no known peers exist yet or every known peer
    with a last-observed time is within `stale_after_days`.
  - `DiagnosisState.Warn` when at least one known peer exceeds the threshold; `Detail` names
    the stale peer machine-id(s) and how long since each was last observed; `Fix` suggests
    checking that machine's folder-sync client is running and that it has run `engram sync
    import` recently.
  - **Never `Broken`.** A stale peer is not necessarily wrong — it may be a machine the user
    deliberately retired, or one they simply haven't picked up in a while — and D37's own
    rule is that only a state nobody could have chosen on purpose sets exit 1. This mirrors
    the existing `Broken`-never-for-a-legitimate-choice precedent verbatim.

*Interaction with `[sync] scope`: none.* Scope narrows what a machine exports; "known peer"
is about which peer directories exist and when they were last observed, unrelated to what
this machine chooses to send. Narrowing scope never hides or reveals a peer.

**Chunk retention/pruning (`sync compact`) — decided, with one residual fork.** This is a
real scope expansion beyond "the transport needs zero code changes" (fork 2, above), argued
here rather than defaulted, per Jim's explicit instruction.

*The core hazard, found by tracing the mechanism rather than assumed.* `allExported` and
`openAtExport` — the two sets `ScanOwnChunks` derives by rescanning a machine's *own* full
chunk history on every export (see "Where this hooks into `Sync.Export()`," above) — are not
persisted anywhere except as the literal content of the chunk files themselves. This has two
consequences that any pruning design must respect:
1. If a chunk file recording a still-*live* fact's original export is deleted outright, the
   next `ScanOwnChunks` rescan no longer sees that fact as previously exported, so the next
   `sync export` re-exports it into a fresh chunk. Harmless on its own — replay is idempotent
   (D32) — but only if that re-export actually happens; blind deletion with no accompanying
   rewrite is not safe.
2. If a chunk file recording a fact's original export is deleted *after* that fact was later
   closed (its close now sitting in some newer, still-present chunk), and a peer had already
   applied the *original* fact record but not yet the close (a real, ordinary case — a slow
   or intermittently-connected peer, not a bug), that peer's `sync_deferred_close` entry for
   the close can never resolve: the only chunk that could satisfy "the matching fact arrives
   via its own chunk" (Close-record semantics, item 1, above) is gone. The close eventually
   reaches `stalled`, permanently, for that one peer. This is the concrete shape of "what's
   safe to prune" the fork's own question was asking about — pruning is unsafe exactly when
   it can delete a record some peer still needs and has no other way to get.

*Design: compact-to-current-state, not raw file deletion.* `sync compact` never deletes an
existing chunk file in place; it always **replaces** this machine's own chunk history with a
freshly written, smaller equivalent, then deletes what the replacement supersedes:
1. Resolve this machine's own `machine-id` — see the ownership invariant below; nothing else
   in this operation ever touches any other machine's subdirectory.
2. Re-run `ScanOwnChunks` (the existing mechanism `Sync.Export` already uses, not a new
   implementation) to get `allExported` and `openAtExport` from `<dir>/<machine-id>/*.jsonl`.
3. Partition every previously-exported identity into three buckets:
   - **live** (`openAtExport`) — always retained, unconditionally, regardless of age.
   - **closed, within `[sync] retain_days` of now** — retained.
   - **closed, older than `retain_days`** — eligible to drop.
4. Write one new chunk (or more, if a size bound applies — reuse whatever chunking limit
   `Sync.Export` already has, if any; otherwise one file) at a fresh `seq` via the existing
   `NextSeq()` (`Sync.cs:419-434`), containing a `{"t":"fact",...}` record — byte-faithful to
   the original 4-tuple, never re-synthesized, so replay identity is unaffected — for every
   identity in the live and within-window buckets, plus a `{"t":"close",...}` record for
   every identity in the within-window bucket. The dropped bucket contributes nothing.
5. Delete every chunk file in `<dir>/<machine-id>/` with `seq` less than the new chunk's —
   their entire content is now a strict subset of what the new chunk carries (for live and
   within-window facts) or has aged out on purpose (for the dropped bucket).
6. Report, in both dry-run and `--apply`: chunk-file count and bytes before/after, and the
   dropped identities explicitly — e.g. "N closed fact(s) older than `retain_days` dropped
   from future sync history (any machine that had already caught up keeps them locally
   forever; a machine that has not synced in over `retain_days` and reconnects later will not
   receive them)." The tradeoff is visible in the tool's own output, not only in this spec.

Because replay is idempotent (D32) and every carried-forward record is content-identical to
its original, a peer mid-way through importing this machine's old chunks is unaffected by
compaction as long as it is still within `retain_days` — the new consolidated chunk simply
re-states everything it already applied (no-ops) plus whatever it hadn't yet gotten. This is
what makes compaction safe *without* needing to know what any specific peer has applied: it
never removes something a peer still within the grace window could need, and it always
replaces rather than subtracts.

*What this deliberately does not touch:* `fact`, `facts.jsonl`, and `sync_chunk_state`/
`sync_deferred_close`'s own already-applied rows on *this* machine are all untouched — this
operates purely on this machine's outgoing chunk *files*, the transport artifacts, never on
authored truth or on anything already durably recorded locally. A closed fact dropped from
future chunks remains exactly as recoverable as it always was via `backup replay` on any
machine that already has it (D31/D32) — pruning only affects a peer that has *not yet*
received it via chunk transport specifically.

*The ownership invariant — load-bearing, and stricter under folder-sync than under git.*
`sync compact` may only ever read, write, or delete inside `<dir>/<this machine's own
machine-id>/`. It must never touch any other machine's subdirectory. Under git this would
merely be bad practice (nothing propagates until someone commits and pushes); under a
real-time folder-sync client (the transport this fork exists to address) it is a correctness
requirement — iCloud Drive/Google Drive/Dropbox present one logically shared folder, so a
machine deleting a file under a peer's subdirectory would propagate that deletion back to the
peer itself (and every other peer) the next time the client syncs, potentially destroying
data the owning machine never agreed to prune. Compaction that stays within its own
subdirectory cannot cause this regardless of transport — it is the same property that keeps
two machines from colliding on chunk 1 in the first place (Identity decision, above),
extended to deletion.

*How this answers "how is 'every peer has applied it' even known" — it deliberately isn't
tracked.* No new ack/coordination mechanism is introduced. Safety instead comes from
`retain_days` being generous enough that a peer still within its window has almost certainly
caught up, composed with the staleness detection above: a peer that goes stale (per
`stale_after_days`) is visible to the user well before it also falls outside `retain_days`
(default 90 vs. 14 — see below), giving a real window to notice and force that peer to catch
up before compaction can discard anything it still needed. This is the intentional trade this
design makes, named rather than hidden.

*Trigger.* `engram sync compact [--apply]` — dry-run by default (D49; this is a destructive
operation, it deletes files). An automated path, `sync compact --if-large --apply`, rides the
same detached `MaintenanceLauncher` session-start child as `backup take --if-due`/`queue
compact --if-large`/`repair --tokens` (Hook impact, below) — gated by a size/count threshold
on this machine's own chunk directory (mirroring `queue compact --if-large`'s own gate, D41),
not by a time-based cadence; the `--apply` baked into that automated invocation is the same
"a human already opted in once, by configuring the maintenance launcher" reasoning that
already justifies `queue compact`'s and `backup take`'s unattended `--apply`.

*Config*: new `[sync] retain_days` (int, default **90**), constrained `>= stale_after_days`
(`doctor` warns if violated — a config where retention is shorter than the staleness warning
could destroy a peer's still-needed history before the user would even have been warned about
it). Argued, not measured, same as `stale_after_days`: roughly 6× the staleness default, wide
margin past the point the user is already warned, while still bounding worst-case chunk
growth to a few months rather than forever.

**Residual fork, flagged rather than picked at the time — now resolved by Jim (relayed
2026-08-19), see the decision paragraph below.** A more precise design was considered: each
peer, after successfully importing chunks from machine A through some seq S, writes a small
marker back — e.g. `<dir>/A/acks/<peer-id>.json` — recording "I have applied A's chunks
through seq S," using the same temp-then-rename atomic-write convention every other file in
this spec uses. A could then prune only past `MIN(acked seq)` across every peer that has ever
acked, which is provably safe rather than probabilistically safe. Rejected as the v1 default,
not as a bad idea: it requires every peer to gain write access to a location other than its
own subdirectory (a bounded, per-(owner, peer) filename, so it wouldn't collide the way a
shared file would — but it is new machinery nonetheless), and it still needs its own timeout
for a peer that never acks at all (dead, retired, or simply never enrolled), which
reintroduces a time window one level down — so much of the precision gain is spent covering a
corner case that time-window retention already handles directly. My recommendation was to
ship time-window retention now and revisit the ack-based design only if real usage shows
machines going stale-then-reconnecting often enough that lost closed-fact history becomes an
actual, recurring complaint rather than a theoretical one — leaving the choice of mechanism,
and the exact `retain_days` number, to Jim to confirm.

**Jim's decision on the residual fork above (relayed 2026-08-19):** time-window retention, as
designed above — not ack-based. Reasoning matches mine: the ack-based alternative still needs
its own timeout fallback for a peer that never acks at all, which reintroduces a time window
one level down rather than eliminating it — it would only add coordination machinery on top
of a risk that isn't actually removed. Both defaults confirmed as proposed: `stale_after_days
= 14`, `retain_days = 90`. Nothing above changes as a result — the design, config keys, and
defaults already written in this section are final, not provisional. See Open question 11,
below.

**`[sync] enabled` gates `MaintenanceLauncher`'s automatic invocation — decided (closing a
gap review found; explicit CLI invocation bypasses it, mirroring the `index`/`--auto`
precedent).** `engram-reviewer` found, while reviewing the staleness/retention work above,
that none of the four `sync` CLI handlers (`SyncCommand.cs`) check `settings.Enabled`
anywhere. This subsection was scoped narrowly to closing that one gap — not reopening sync
design generally — and was investigated against source before designing anything, per that
scoping.

*The gap, and how much worse it is than a marker file, found by tracing the mechanism rather
than assumed.* Confirmed: zero matches for `settings.Enabled` anywhere in `SyncCommand.cs`.
`Export`, `Import`, and `Compact` (`SyncCommand.cs:57-138`, `:185-275`, `:370-438`) each
compute `machineId = apply ? Sync.ResolveMachineId(home.SyncDir) : Sync.TryReadMachineId(...)
?? Sync.GenerateMachineId()` before doing anything else, and `Sync.ResolveMachineId`
(`Sync.cs:153-171`) `Directory.CreateDirectory(syncRoot)`s and writes a `machine-id` file on
first call, unconditionally, whenever `apply` is `true` — none of the three handlers consult
`settings.Enabled` first. `MaintenanceLauncher.BuildScript` (`MaintenanceLauncher.cs:67-125`)
appends `sync import --if-new --apply`, `sync export --if-due --apply`, and `sync compact
--apply --if-large` to every session-start script unconditionally for
`MaintenanceJobs.SessionStart` — the method's own handling of `--auto`/
`auto_index_on_session_start` for the adjacent index job, a few lines above, shows this
codebase already has the vocabulary for "ambient work gated by a setting"; it was simply
never applied to sync's three lines. Net effect: a user who has never touched `[sync]` (the
default: `enabled = false`) gets `<home>/sync/machine-id` written at their very next session
start after this shipped, with no action on their part.

That much matches what review reported. Tracing the mechanism further adds one thing review
did not characterize: **`Export`'s first run does not stop at a marker file.** `Export`
(`Sync.cs:222-318`) computes `factsToExport` from every fact not yet in this machine's own
(empty, first-run) chunk history and scope-eligible under `[sync] scope` — default
`SyncScope.Default = "all"` (`SyncScope.cs:36`), whose `Clause` resolves to the literal SQL
`"1=1"` for `SyncScopeKind.All` (`SyncScope.cs:105-116`), i.e. unfiltered, every fact in the
store. `--if-due` does not prevent this: `IsExportDue` (`SyncCommand.cs:164-183`) returns
`true` whenever the machine's own chunk directory does not exist yet, which is exactly the
first-run case. So for any installation with live facts, the first automatic `sync export
--if-due --apply` since this shipped has already written a full, unfiltered copy of that
store's fact content to `<home>/sync/<machine-id>/1.jsonl` — an actual export, not an empty
scaffold — regardless of `[sync] enabled`. `sync compact`'s own exposure is smaller only
because it runs after export/import in the same script and reuses the machine-id file they
already created (`ResolveMachineId` re-reads rather than re-writes when the file already
exists); its `--if-large` threshold (20 chunk files) is never reached on a first run, so it
contributes no additional chunk rewrite of its own — this matches what review already
confirmed. `sync status` was never implicated: it already uses the read-only
`Sync.TryReadMachineId` (`Sync.cs:188-200`) by design, and creates nothing.

*Design: one gate, at the ambient-invocation boundary.*

1. **`MaintenanceLauncher` stops asking.** `Spawn`/`BuildScript` (`MaintenanceLauncher.cs:46-
   125`) gain a new required `bool syncEnabled` parameter, placed before the existing
   optional `indexRoot`/`jobs` parameters so every call site must be touched — a parameter
   with a silent default here is exactly the shape of bug being fixed, so this spec
   deliberately gives it none. Inside `BuildScript`'s `jobs == MaintenanceJobs.SessionStart`
   block, the three `sync import`/`sync export`/`sync compact` `.Append` calls move inside a
   new `if (syncEnabled) { ... }`, following the same conditional-block shape the method
   already uses a few lines below for `if (indexRoot is not null)`. Two call sites need
   updating:
   - `HookCommand.cs:430` (the session-start job — the call that matters here) passes the
     real value: read `SyncSettings.Read(config).Enabled` for the already-in-scope `home`
     before calling `Spawn`, reusing an already-loaded `ConfigFile`/`SyncSettings` if the
     enclosing method has one nearby rather than loading a second copy.
   - `RepoCommand.cs:418` (`TrySpawnFirstIndex`, `MaintenanceJobs.EnrollmentIndex`) passes a
     literal `syncEnabled: false` — inert by construction, since the sync lines live strictly
     inside the `SessionStart`-only block and this call always passes `EnrollmentIndex`, but
     the parameter must still be supplied.

   This closes the actual reported defect: a user who has never touched `[sync]` (the
   default) gets nothing written to `<home>/sync/` at session start, ever. It is also the
   cheapest, most precise regression test available: a Tier-1 assertion on `BuildScript`'s
   returned *string* (no process, no filesystem) that it contains none of `"sync
   import"`/`"sync export"`/`"sync compact"` when `syncEnabled: false`.

   `sync status` also gains one informational line, not a gate — status is already
   read-only: immediately before its existing `"This machine: ..."` output, print `"[sync]
   enabled = false — automatic export/import/compact are off; this machine will not send or
   receive updates until it is turned on."` when `!settings.Enabled`, matching D37's "off is
   a supported configuration" framing rather than treating the state as an error.

**Considered and rejected: a second, handler-level gate inside `Export`/`Import`/`Compact`,
independent of how they were invoked.** The design originally proposed here (2026-08-19) had
each handler additionally refuse `--apply` whenever `[sync] enabled` was false, reasoned from
`SyncSettings`'s own doc comment calling the feature "opt-in... requires the user to have
already set up *some* replication mechanism... which nothing here creates or manages" — the
argument being that a user who hasn't enabled sync has, overwhelmingly, also not pointed
`[sync] dir` at anything real, so an explicit `--apply` for them would write a full export
into an unconfigured default directory nobody is going to sync anywhere. Flagged as Open
question 14 rather than committed, because it was a deliberate departure from this codebase's
own `index`/`--auto` precedent (`repo enroll` legitimately bypasses
`auto_index_on_session_start` — indexing a named repo is self-contained and meaningful
regardless of any ambient-work setting, `IndexCommand.cs:93-105`), not a forced conclusion
from the code.

**Jim's decision (relayed 2026-08-19): mirror the `index`/`--auto` precedent instead.** A
user who explicitly types `sync export`/`import`/`compact --apply` should go through even
with `[sync] enabled = false`, the same way `repo enroll` bypasses
`auto_index_on_session_start` — an explicit, human-typed command is a deliberate act, and the
opt-in reasoning above governs *ambient* invocation, not a command someone chose to run.
`SyncCommand.cs`'s `Export`/`Import`/`Compact` handlers therefore get **no**
`settings.Enabled` check of any kind; they behave exactly as they did before this fix when
invoked directly, whatever the flag's value. Only `MaintenanceLauncher`'s `syncEnabled`
parameter (item 1, above) gates anything, by omitting the sync lines from the generated
script rather than by refusing a write once asked for.

*Backward compatibility — not needed.* A `doctor`-warning design for pre-existing stray sync
content was also drafted alongside the above (a `CheckSync` upgrade from `DiagnosisState.Off`
to `Warn` when `[sync] enabled = false` and `home.SyncDir` has content, flagged as Open
question 15) and is dropped in full: Jim's word on it (relayed 2026-08-19) — "This is not an
issue. No one has [hit it]" — because this fix ships before the gap it addresses reached any
real install, so there is no stray pre-fix export anywhere to warn about or clean up.
`CheckSync` (`Diagnostics.cs:879-937`) is unchanged by this spec.

**Hook impact**: gated by `[sync] enabled` as of the decision immediately above — the three
sync lines below are appended to `MaintenanceLauncher`'s generated script only when it is
`true`; the rest of this paragraph describes their placement once that condition holds.
`sync import --if-new` (cheap directory-mtime check first) and `sync export --if-due`
(mirrors `backup take --if-due`) ride `MaintenanceLauncher`'s detached session-start child,
alongside `backup take`, `queue compact`, `repair --tokens`. `sync compact --if-large --apply`
(Chunk retention/pruning, above) joins the same detached child, gated the same way `queue
compact --if-large` is — a size/count threshold on this machine's own chunk directory, not a
time-based cadence. Not on the `file-touched` path — D4's 10 ms/never-opens-DB rule does not
apply here, but a measurement plan is still required (NEEDS-EVIDENCE below), matching how
every other `MaintenanceLauncher` job was measured before being added.

**Telemetry**: new kind `TelemetryEventKind.Sync = "sync"`, phases started/finished/failed
(D55 shape, matches Index/Embedding). No counts inside the event (D55); counts live in
`sync_chunk_state`/CLI output. `sync compact` emits under this same kind/phase shape — no new
kind: D55's "a kind that is declared but never emitted reads as a feature switched off"
caution runs the other direction here too — inventing a second kind for a materially similar
operation (an unattended maintenance pass over the sync directory) would be the same "two
implementations of one comparison" trap D42 warns about elsewhere, just for telemetry kinds
instead of process identity. `sync compact`'s counts (chunks merged, bytes reclaimed,
fact-pairs dropped) live in its own CLI report output, the same rule export/import counts
already follow.

**Config**: new `[sync]` section — `enabled` (bool, default `false`, opt-in since it requires
the user to have already set up *some* replication mechanism for the directory themselves —
git, iCloud Drive, Google Drive, Dropbox; Engram is agnostic to which, see Folder-sync
transport, above; as of the gating decision above, `false` gates `MaintenanceLauncher`'s
automatic session-start invocation only — an explicitly typed `sync export/import/compact
--apply` bypasses it, mirroring the `index`/`auto_index_on_session_start` precedent; see the
Design subsection above), `dir` (path override — must be
an absolute, already-expanded path, no `~`; see Folder-sync transport, above, for why),
**`scope`** (string, default `all` — `all | user | repo:<value>`; see the Scoped export
subsection above for the `repo:<value>` resolution algorithm), **`stale_after_days`** (int,
default `14` — see Staleness/liveness detection, above, for the reasoning), and
**`retain_days`** (int, default `90`, must be `>= stale_after_days`; see Chunk
retention/pruning, above). `scope` is a sync-only enumeration, independent of the `fact.scope`
column (`user | project | code | session`) — the two happen to share the value `user` because
they mean the same fact property there, but `repo:<value>` and `all` have no `fact.scope`
equivalent; don't conflate the two axes when reading either one. All five keys are edited via
`ConfigEditor` with the `# written by engram` marker (D33), the same convention this section
has used from the start.

## Invariants preserved

- **D8 (facts append-only)**: sync only ever inserts new rows (via unchanged
  `FactJournal.Replay`) or closes via the same `valid_to`/`superseded_by` path
  `engram_revise`/`engram_forget` already use. No column on `fact` changes shape.
  `fact_sync_request` extends this the same way: insert-only, never a column on `fact`.
  `sync compact` extends it a third way: it rewrites and deletes chunk *files* (transport
  artifacts), never `fact` rows and never `facts.jsonl` — a closed fact dropped from future
  chunks stays exactly as durable locally as it always was.
- **D32**: extends `facts.jsonl`'s record shape and reuses `Replay`/`idMap` rather than
  building a parallel apply path.
- **D32 (journal survives a schema restore the `.db` snapshot cannot), extended a second
  time**: spec 02 already applied this pattern once beyond `fact` itself, for
  `fact_relation`/`relations.jsonl`. `fact_sync_request`/`sync_requests.jsonl` (Schema delta,
  above) is the same extension applied to a second per-fact metadata table, using the
  identical tuple→`idMap` resolution `RelationJournal.Replay` already implements.
- **D49**: `export`/`import` dry-run by default, `--apply` required to write. `sync compact`
  follows the same rule — it deletes files, so it is dry-run first exactly like `repair`,
  `compact`, `forget`, `backup prune`, `queue compact`, and every other destructive verb in
  this codebase; its automated `--if-large` path bakes in `--apply` the same way `queue
  compact --if-large`'s does, on the same "the human already opted in by configuring the
  maintenance launcher" reasoning.
- **New: `[sync] enabled` gates `MaintenanceLauncher`'s ambient session-start invocation,
  not explicit CLI invocation.** `MaintenanceLauncher` never appends `sync
  import`/`export`/`compact` to the generated script when `[sync] enabled` is false (see the
  `[sync] enabled` gating design, above); `SyncCommand`'s `Export`/`Import`/`Compact`
  handlers carry no `settings.Enabled` check and run exactly as before this fix when typed
  directly — the same `index`/`--auto` precedent that lets `repo enroll` bypass
  `auto_index_on_session_start`. This mirrors an existing pattern rather than introducing a
  new one; the boundary (ambient-only, never commanded) is the point, and it does not license
  gating another verb's handlers this loosely without its own argument.
- **D4**: no work added to `file-touched`; new hook work rides the existing detached child.
- **D42 (one home resolver)**: the folder-sync transport (Folder-sync transport, above) adds
  no new path resolution — `SyncSettings.ResolveDir`/`EngramHome.SyncDir` already split
  resolver-from-override correctly; the one new constraint this spec adds (`[sync] dir` must
  be pre-expanded, no `~`) exists specifically so the feature never needs a second `HOME`
  reader.
- **"Derived state is repairable"**: `sync_chunk_state` and `sync_deferred_close` are
  rebuildable by re-running import from the full chunk history. `fact_sync_request` is not
  covered by this bullet — it holds an authored decision (an explicit `sync: true`), not
  anything recomputable from replayed chunks or from `fact` itself, so losing it is not
  merely a repair-cost question the way the other two tables are; that is exactly why it now
  gets its own non-derived durability path (`sync_requests.jsonl`, above) instead.
- **D51 (trigger phrases live in `[Description]`, not the primer)**: `sync`'s "share
  engram" trigger is a compile-time constant on `engram_remember`'s parameter attribute,
  the same mechanism D51 already established for `[memory] precedence`.
- **New: a machine only ever deletes chunk files it owns.** `sync compact` (Chunk
  retention/pruning, above) reads, writes, and deletes exclusively inside `<dir>/<this
  machine's own machine-id>/`. This is not an existing numbered decision — it is new to this
  spec, introduced because folder-sync transports (unlike git) propagate a deletion to every
  peer in near-real-time, which makes cross-machine deletion an active hazard rather than a
  merely-unwise pattern. It composes with the Identity decision's own "two machines never
  collide writing chunk 1" property, extended to cover deletion as well as creation.

## Tests by tier (D9)

- **Tier 1**: close-resolution branch logic (defer / apply / no-op / conflict / stalled) as
  a pure function over a fabricated local-fact table. Falsify: delete the live-check branch
  (case 2 vs 4) and confirm a test asserting "conflict case leaves the divergent fact
  untouched" starts failing (the fact gets wrongly closed). Falsify the retry ceiling
  separately: remove the dead-letter transition and confirm a test asserting "a close with
  no matching fact ever reaches `stalled` after N attempts" starts failing (it retries
  forever). Scope-clause evaluation as a pure function over a fabricated `fact`+`session`
  table, for `all`/`user`/`repo:<identity>`: falsify by deleting the session-join half of
  the repo clause and confirm a test asserting "a session fact recorded in the target repo
  is included under `repo:<identity>` scope" starts failing. Repo-identity resolution
  (zero/one/many `repo_registry` matches) as a pure function. Sync-request journal
  resolution as a pure function over a fabricated `idMap`: falsify by deleting the
  unresolved-skip branch and confirm a test asserting "a sync-request row whose fact isn't
  in this journal's `idMap` is skipped, not inserted against the wrong id" starts failing.
  `SyncStaleness.Evaluate` as a pure function over fabricated `(machineId, lastObservedUtc?)`
  pairs: falsify by deleting the "never observed → never stale" branch and confirm a test
  asserting "a freshly enrolled peer with no chunks yet is not flagged stale" starts failing.
  `sync compact`'s bucket-partition logic as a pure function over a fabricated
  `allExported`/`openAtExport` set plus ages: falsify by deleting the live-fact-always-retain
  branch and confirm a test asserting "a still-live fact is retained regardless of how old
  its original export was" starts failing; falsify separately by deleting the
  within-window-retain branch and confirm a test asserting "a fact closed 1 day ago is
  retained even though it's closed" starts failing. `MaintenanceLauncher.BuildScript` with
  `syncEnabled: false` and `jobs: MaintenanceJobs.SessionStart`: assert the returned script
  string contains none of `"sync import"`/`"sync export"`/`"sync compact"`; with
  `syncEnabled: true`, assert it contains all three, in the existing order. Falsify: delete
  the new `if (syncEnabled)` guard and confirm the `false`-case assertion starts failing (the
  sync lines are present even when disabled).
- **Tier 2**: two `SandboxHome` instances simulate two machines. Export from A, import into
  B, assert fact sets match. Revise on A, export, import into B, assert B's copy closes.
  Seed an independently-authored fact on B for the same slot *before* importing A's chunk,
  assert B's fact is untouched and a conflict is counted. Falsify: disable the live-match
  check and confirm this last test starts failing. Rebuildability guard: drop
  `sync_chunk_state`/`sync_deferred_close`, re-run import from the full chunk history,
  assert an identical resulting fact set. Scoped-export case: on A, write a session fact
  under a session whose `repo_path` is set, a code-indexed fact under the same repo path, a
  plain user fact, and a fact written with `sync: true` under an unrelated scope; export with
  `--scope=repo:<that repo>`, import into B, and assert exactly the repo-tied session fact,
  the repo-tied code fact, and the sync-flagged fact arrived — and the plain user fact did
  not. Revise the sync-flagged fact on A and assert the newly created fact also carries a
  `fact_sync_request` row (i.e. exports under a narrower scope) without a second explicit
  `sync: true`. Scope-narrowing case: export a plain (unflagged) in-scope fact under
  `--scope=all` from A, import into B; narrow A's scope to `repo:<unrelated>`; revise (close)
  that fact on A; export again under the narrowed scope; import into B; assert the close
  still applies on B — proving close-selection is unaffected by the later scope change.
  `sync_requests.jsonl` round-trip: flag a fact `sync: true`, run `backup take --apply`,
  assert `sync_requests.jsonl` contains its portable identity; drop `fact_sync_request`
  entirely, run `backup replay --apply`, assert the row is restored resolved against the
  (unchanged) fact id. Fingerprint regression guard, directly for the bug class `770519d`
  already fixed once for `fact_relation`: flag an existing fact `sync: true` with no other
  store change, assert `backup take --if-due` still takes a snapshot (fingerprint moved).
  Folder-sync transport-agnosticism: configure `[sync] dir` to a plain non-git tmp directory
  (no `.git`, no version control of any kind) and re-run the two-machine export/import test
  above against it unmodified; assert identical results — the code path does not know or
  care that the directory isn't git-tracked. Staleness end-to-end: two `SandboxHome`
  instances, export from A, import into B, assert B's `sync status` shows A within
  `stale_after_days`; advance the clock past the threshold with no further export from A,
  assert B's `sync status` now marks A `STALE` and `doctor` reports a `Warn` row naming A.
  Compaction end-to-end, three machines: A exports several facts across multiple chunks, some
  later closed, all older than `retain_days`; B fully imports everything before compaction;
  A runs `sync compact --apply`; assert (1) a freshly enrolled machine C, importing only A's
  post-compaction chunk, ends up with a live-fact set identical to B's, and (2) C does *not*
  receive the closed-and-dropped facts' history — asserted explicitly, as the accepted
  tradeoff, not treated as an incidental gap. Mid-flight-peer safety case: B imports only the
  chunk containing a fact's original `{"t":"fact"}` record and not yet the later chunk
  containing its `{"t":"close"}`, both still within `retain_days`; A runs `sync compact
  --apply`; assert B, importing the new consolidated chunk, still receives and applies that
  close correctly (the within-window bucket must have carried it forward). Ownership
  invariant: assert `sync compact --apply` never creates, modifies, or deletes any path
  outside `<dir>/<the running machine's own machine-id>/` — snapshot every file under `[sync]
  dir` outside that one subdirectory before and after, by size and mtime, and assert nothing
  moved (mirroring `doctor`'s own end-to-end file-snapshot pattern). `[sync] enabled` gating —
  ambient path: with `MaintenanceLauncher`'s `syncEnabled: false` (the actual gate — see
  Tier 1), drive the detached session-start child against a `SandboxHome` holding live facts;
  assert `home.SyncDir` does not exist afterward. Explicit-bypass proof — the direct
  falsification of fork 14's resolution: with `[sync] enabled` unset/false, call
  `SyncCommand`'s `Export`/`Import`/`Compact` handlers directly (the CLI surface, not through
  `MaintenanceLauncher`) against a `SandboxHome` holding live facts; assert `home.SyncDir`
  **does** exist afterward with the same chunk content as the `enabled = true` case, proving
  the handlers are unaffected by the flag — mirroring the existing bypass proof for `repo
  enroll` vs. `auto_index_on_session_start`. Falsify: add a `settings.Enabled` check into any
  handler and confirm this test starts failing (the explicit invocation stops writing).
  `sync status` with `enabled = false`: assert it still reports normally and now also prints
  the disabled-state note. Mechanical fallout, not a behavior regression: every existing call
  site of `MaintenanceLauncher.Spawn`/`BuildScript` must now pass the new `syncEnabled`
  parameter (`HookCommand.cs:430`, `RepoCommand.cs:418` — see Design, above) — a
  compile-time-forced update, not a test needing new assertions.
- **Tier 3**: end-to-end `sync export`/`import`/`compact` against the published binary and
  two real home directories; file-snapshot invariant (nothing outside the sync dir and the
  target DB changes) mirroring `doctor`'s own end-to-end pattern. `[sync] enabled` gating,
  end-to-end: a fresh real home directory, `[sync] enabled` absent from config (default), a
  populated store with live facts, drive the actual session-start maintenance path; assert
  `home.SyncDir` does not exist on disk afterward. This is the direct falsification of the
  originally reported defect, matching D9's tier-3 discipline that CI passing on the JIT
  build proves nothing about what ships.

## Measurements

- Chunk export/import cost at two corpus sizes (mirroring the 5,097 / 50,097 scale already
  used elsewhere in this codebase), both for zero-new-chunks and for a realistic pending
  chunk (~100 facts).
- **MCP tool-surface budget.** Adding `engram_remember`'s and `engram_revise`'s `sync`
  parameters (Per-fact opt-in, above) pushes `McpToolSurfaceBudgetTests`'s
  `ToolDefinitions_StayUnderCharacterCeiling` total to 4,789 chars, even after compressing
  both new `[Description]`s to minimal telegraphic wording (`remember`: `"Flags for sync.
  Triggered by \"share engram\" plus content."`; `revise`: `"Sync flag; omit inherits, else
  overrides."`) — 81 over the existing ceiling of 4,708.

  Investigated what 4,708 protects before treating it as movable: nothing external. No doc
  in this repo ties it to a real MCP-client context budget, and the test's own comment says
  `EngramServerTools`'s three tools are excluded from the count as "a separate, unmeasured
  gap" — so the figure was never a measurement of total exposed surface either. It is a
  self-imposed regression guard, derived as `measured_actual + 137` when `engram_judge`
  landed (`4,571 + 137 = 4,708`, commit `770519d`), where 137 is fixed headroom the
  comment calls out for *unreviewed, ordinary* wording drift — not a stand-in for a
  downstream limit, and not a knob to nudge casually either; the test's own failure message
  says so ("Raise the ceiling only with a rationale, or make the descriptions carry their
  cost").

  **Decision: raise it, by the same formula, rather than compress further or trim
  elsewhere.** This is a deliberate, spec-mandated addition — D51 requires the literal
  trigger phrase "share engram" in `engram_remember`'s description, and both descriptions
  above are already near a compression floor where shortening further risks losing that
  phrase or the inherit/override contract on `revise`, which is worse than a wider ceiling.
  That's exactly the case the guard's own comment anticipates as earning a raise, not the
  "ordinary wording changes" case the 137 headroom exists to absorb without a rationale.
  New ceiling: `4,789 + 137 = 4,926`, carrying the same headroom amount forward rather than
  re-deriving it — it already survived one full cycle (set at `engram_judge`, held until
  this feature) unconsumed by anything but this deliberate addition. `EngramServerTools`'s
  exclusion from the count is a separate, pre-existing gap (D17-argued, never folded into
  `ToolMethods()`) and is out of scope for this change — do not fold it in here.

  **Mechanical follow-up, not specified further** (hand to whoever applies the diff): in
  `tests/Engram.Integration.Tests/McpToolSurfaceBudgetTests.cs`, change `MaxDefinitionChars`
  from `4708` to `4926` and rewrite the comment above it to record *this* measurement in the
  same style the existing comment records its own (date, what changed, the new actual, the
  carried-forward headroom and why) — do not leave the old comment describing the
  `engram_judge` measurement standing next to a new number. Then regenerate
  `docs/mcp-tool-descriptions.golden.txt`: run `McpToolDescriptionGoldenTests`, let it write
  its `.actual` file, diff it against the golden as the test's own failure message directs,
  and replace the golden file with the new rendering so the test passes with the `sync`
  parameter text included.

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
5. **[product decision, not evidence]** Per the Amendment note above: the exact tracking shape
   for an unfinalized (partially-arrived) chunk — a new table mirroring `sync_deferred_close`,
   or extra `status`/`retry_count` columns on `sync_chunk_state` — and its retry ceiling before
   moving to `stalled`, are left open for implementation.
6. **RESOLVED — not a gap.** Originally raised as: does narrowing `[sync] scope` after a
   fact already synced under a broader baseline silently stop that fact's future close from
   transmitting? Verified against the existing `Sync.Export()` implementation and closed: no
   tracking table is needed, and this is not limited to explicitly-flagged facts as first
   assumed. Close-selection (`Sync.cs:232-245`) never consults `[sync] scope` at all — it
   only ever emits a close for a fact this machine has *already* exported, tracked via the
   existing `ScanOwnChunks` chunk-file rescan, independent of the current scope config at
   close time. So every previously-exported fact's close is safe from scope narrowing, by
   construction, with nothing new to build; see the "Where this hooks into `Sync.Export()`"
   paragraph in Design for the verified mechanism. A `sync status` line reporting how many
   currently-live facts fall outside the active scope (informational visibility into what
   *won't* be picked up as new — not a "skipped close" count, since closes are never skipped
   for this reason) is a nice-to-have, not specified here.
7. **RESOLVED.** Originally raised as: the "send message" trigger phrase for `sync=true`
   (Design's Per-fact opt-in subsection) collides in wording with this environment's own
   `SendMessage` agent-to-agent messaging tool, in sessions where both are loaded. Jim's
   decision (relayed 2026-08-18): use "share engram" instead — swapped into the `sync`
   parameter's `[Description]` text above, same D51 rationale, same location. No further
   spec change needed.
8. **RESOLVED.** Originally raised as: `engram_remember`/`engram_revise`'s new `sync`
   parameters push `McpToolSurfaceBudgetTests`'s total 81 chars over its 4,708 ceiling even
   after minimal-wording compression — is that ceiling protecting something real, and if
   not, is raising it, keeping the compressed wording, or trimming elsewhere the right fix?
   Investigated and decided: see the "MCP tool-surface budget" entry under Measurements
   above. Ceiling raised to 4,926 by the same `measured_actual + 137` formula the existing
   ceiling was itself derived by; no wording changed beyond what was already compressed.
9. **RESOLVED.** Originally raised as: `fact_sync_request` calls itself authoritative and
   not-losable, and claims to mirror spec 02's `fact_relation` pattern, but `fact_relation`
   got full journal/backup coverage (`relations.jsonl`) that `fact_sync_request` never did —
   so a `.db` snapshot restored into a newer schema (D31) silently drops every always-sync
   flag today. Investigated and decided: mandate the missing journal rather than exempt the
   table — the exemption path would have contradicted the durability claim this spec already
   makes for it, with no new evidence to justify weakening that claim. See "Durability for
   `fact_sync_request` — `sync_requests.jsonl`" under Schema delta, above: a fourth journal
   mirroring `RelationJournal.cs` directly, plus the same `BackupFingerprint` fix commit
   `770519d` already made once for `fact_relation`, applied now rather than after the
   identical bug is rediscovered for sync-request-only sessions.
10. **RESOLVED — designed.** Folder-sync staleness/liveness detection — whether `sync
    status`/`doctor` should track and warn on "time since any peer machine's chunk last
    appeared." Jim decided: build it (relayed 2026-08-19). See "Staleness/liveness detection
    (`sync status`/`doctor`) — decided," above, for the full design: `[sync]
    stale_after_days` (default 14, argued not measured), a pure `SyncStaleness.Evaluate`
    function, surfaced as full detail in `sync status` and a summary `Warn`-or-`Ok`/`Off` row
    in `doctor` (never `Broken`, per D37).
11. **RESOLVED.** Chunk retention/pruning. Jim decided: address now, with full rigor, not
    deferred (relayed 2026-08-19). See "Chunk retention/pruning (`sync compact`) — decided,
    with one residual fork," above, for the full design: `sync compact` rewrites (never
    bare-deletes) a machine's own chunk history down to its current live-export state plus a
    `[sync] retain_days` grace window for recently-closed facts, requiring no peer-ack
    coordination — safety comes from the retention window being generous relative to
    `stale_after_days`, not from knowing what any peer has actually applied. The residual
    fork this design flagged — time-window retention versus a more precise ack-based
    alternative, and the exact `retain_days`/`stale_after_days` numbers — is now resolved too
    (relayed 2026-08-19): Jim confirmed time-window retention over ack-based, for the same
    reason the design's "Residual fork" paragraph argued it — an ack-based scheme still needs
    its own timeout fallback for a peer that never acks, which reintroduces a time window one
    level down rather than removing it, so it would only add coordination machinery without
    eliminating the risk. Both defaults confirmed as proposed: `stale_after_days = 14`,
    `retain_days = 90`. No further spec change needed; the design above already reflects
    this as final, not provisional.
12. **RESOLVED.** Preflight validation of a configured `[sync] dir`. Jim decided: blind
    lazy-create, matching `home.SyncDir`'s existing default behavior — no preflight
    existence/health check, no iCloud cloud-only-placeholder detection (relayed 2026-08-19).
    No further spec change needed; the existing lazy-create behavior already does this.
13. **[measurement]** Cost of `engram sync compact` at two corpus sizes (mirroring items 1-2
    above and the 5,097/50,097 scale used elsewhere), both for a chunk history small enough
    that nothing is prunable yet and for a realistic accumulated history (e.g. dozens of
    chunks spanning months). Decides whether `--if-large`'s threshold needs to be tuned
    tighter or looser than a naive guess, the same way items 1-2 gate `sync import`/`export`'s
    hook placement.
14. **RESOLVED.** Originally raised as: should `export`/`import`/`compact --apply` hard-refuse
    when `[sync] enabled = false` even for a command typed directly by a user, or instead
    mirror the `index`/`--auto` precedent exactly — gate only `MaintenanceLauncher`'s ambient
    invocation and let an explicit CLI invocation bypass the flag, the same way `repo enroll`
    bypasses `auto_index_on_session_start`. Jim decided (relayed 2026-08-19): mirror the
    `index`/`--auto` precedent — a user who explicitly types `sync export/import/compact
    --apply` should go through even with `[sync] enabled = false`. `SyncCommand`'s
    `Export`/`Import`/`Compact` handlers carry no `settings.Enabled` check; only
    `MaintenanceLauncher`'s `syncEnabled` parameter gates anything, by omitting the sync
    lines from the generated session-start script. See the Design subsection above, revised
    accordingly.
15. **RESOLVED.** Originally raised as: is `doctor`'s `Warn` row sufficient for existing
    installations that likely already have a full fact export sitting in
    `<home>/sync/<machine-id>/1.jsonl` from before this fix, or does that warrant an active,
    explicit, dry-run-first cleanup verb? Jim decided (relayed 2026-08-19): not needed — this
    fix ships before the gap it addresses has reached any real install, so there is no stray
    pre-fix export anywhere to clean up or warn about. The `CheckSync`/`doctor` `Warn`-upgrade
    design is dropped entirely; `CheckSync` (`Diagnostics.cs:879-937`) is unchanged by this
    spec. See "Backward compatibility — not needed," above.
