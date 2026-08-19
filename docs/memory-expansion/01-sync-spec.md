# 01 — Cross-machine sync

Status: design, revised (amended 2026-08-18 — chunk-completeness gap under non-atomic
transports; extended 2026-08-18 — scoped export: `[sync] scope` baseline plus per-fact
always-sync opt-in; extended 2026-08-18 — `fact_sync_request` durability via
`sync_requests.jsonl`). Parent: `docs/memory-expansion-spec.md` row 1.

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
`<seq>.jsonl.tmp`, rename to `<seq>.jsonl` once complete) fixes visibility on the *sender's*
own filesystem but does not make that guarantee travel across a third-party sync client's
upload/download hop — the receiver can still observe the final name while the transfer is
incomplete. No configuration on Engram's side can prove a specific external sync client
replicates renames atomically; it is not something Engram is in a position to verify or rely
on.

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
requires a git repo the user set up themselves), `dir` (path override), and **`scope`**
(string, default `all` — `all | user | repo:<value>`; see the Scoped export subsection
above for the `repo:<value>` resolution algorithm). `scope` is a sync-only enumeration,
independent of the `fact.scope` column (`user | project | code | session`) — the two happen
to share the value `user` because they mean the same fact property there, but `repo:<value>`
and `all` have no `fact.scope` equivalent; don't conflate the two axes when reading either
one. Edited via `ConfigEditor` with the `# written by engram` marker (D33), same as the
existing two keys.

## Invariants preserved

- **D8 (facts append-only)**: sync only ever inserts new rows (via unchanged
  `FactJournal.Replay`) or closes via the same `valid_to`/`superseded_by` path
  `engram_revise`/`engram_forget` already use. No column on `fact` changes shape.
  `fact_sync_request` extends this the same way: insert-only, never a column on `fact`.
- **D32**: extends `facts.jsonl`'s record shape and reuses `Replay`/`idMap` rather than
  building a parallel apply path.
- **D32 (journal survives a schema restore the `.db` snapshot cannot), extended a second
  time**: spec 02 already applied this pattern once beyond `fact` itself, for
  `fact_relation`/`relations.jsonl`. `fact_sync_request`/`sync_requests.jsonl` (Schema delta,
  above) is the same extension applied to a second per-fact metadata table, using the
  identical tuple→`idMap` resolution `RelationJournal.Replay` already implements.
- **D49**: `export`/`import` dry-run by default, `--apply` required to write.
- **D4**: no work added to `file-touched`; new hook work rides the existing detached child.
- **"Derived state is repairable"**: `sync_chunk_state` and `sync_deferred_close` are
  rebuildable by re-running import from the full chunk history. `fact_sync_request` is not
  covered by this bullet — it holds an authored decision (an explicit `sync: true`), not
  anything recomputable from replayed chunks or from `fact` itself, so losing it is not
  merely a repair-cost question the way the other two tables are; that is exactly why it now
  gets its own non-derived durability path (`sync_requests.jsonl`, above) instead.
- **D51 (trigger phrases live in `[Description]`, not the primer)**: `sync`'s "share
  engram" trigger is a compile-time constant on `engram_remember`'s parameter attribute,
  the same mechanism D51 already established for `[memory] precedence`.

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
- **Tier 3**: end-to-end `sync export`/`import` against the published binary and two real
  home directories; file-snapshot invariant (nothing outside the sync dir and the target DB
  changes) mirroring `doctor`'s own end-to-end pattern.

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
