# Backup fingerprint — what "the fingerprint of authored truth" should actually count

Design only. Nothing here was executed. Deliberately **separate** from
`docs/specs/edge-fact-lane-eligibility.md`: same audit, different root cause, different
fix, different blast radius. Conflating them would hide that this one is not a
graph-enhance regression at all — it is a pre-existing semantics gap that graph-enhance
made visible.

---

## 1. What it does today

`BackupFingerprint.Read` (`BackupStore.cs:40-74`):

```sql
SELECT (SELECT COUNT(*) FROM fact),
       (SELECT COALESCE(MAX(id), 0) FROM fact),
       (SELECT COUNT(*) FROM fact WHERE valid_to IS NOT NULL),
       (SELECT COUNT(*) FROM entity),
       (SELECT COUNT(*) FROM entity_alias),
       (SELECT COUNT(*) FROM supersession),
       (SELECT COUNT(*) FROM edge),
       (SELECT COUNT(*) FROM fact_relation),
       (SELECT COUNT(*) FROM fact_sync_request);
```

**`regenerable` appears nowhere.** Three call sites: `BackupStore.Due` (`:291`),
`BackupStore.FingerprintOf` (`:545`), `Diagnostics` (`:476`).

So every `index --apply` moves the fingerprint, and the next session's detached
`backup take --if-due` performs a full `VACUUM INTO` plus an atomic whole-file rewrite of
`facts.jsonl`. `CLAUDE.md` says the snapshot *"is skipped entirely unless the fingerprint of
authored truth actually moved, so an idle day costs nothing"* — **the query no longer
describes that sentence.**

The repo already knows the correct shape elsewhere: `StoreCompactor.cs:205,218` filter
`WHERE regenerable = 1 AND …`, and `:236` uses `other.regenerable = 0`. This is a gap in one
query, not a missing concept.

---

## 2. Two extensions the dispatch did not name

The brief said "it counts regenerable facts". Both halves below are in the same query and
neither is fixed by filtering the fact **counts**.

### 2.1 `MAX(id) FROM fact` moves on every index run regardless

Filtering the three `fact` count subqueries and leaving `MAX(id)` alone **leaves the bug
completely intact** — a single new code fact still moves the fingerprint. The `MAX(id)`
term needs the same predicate, and once filtered it means something slightly different and
worth naming: *the newest authored fact's id*, which is exactly what it should have meant.

### 2.2 `COUNT(*) FROM entity` is as much the bug, and cannot be fixed the same way

Indexing mints `symbol` and `symbol-name` entities in bulk — one per declaration and one per
distinct callee spelling. So the entity term moves on every index run too.

**`entity` has no `regenerable` column** (`id, path, kind, name, created_at, meta`), so it
cannot be filtered by the same predicate. Three options, and I recommend the third:

| option | shape | verdict |
|---|---|---|
| (a) count entities carrying ≥1 authored fact | `EXISTS (SELECT 1 FROM fact WHERE subject_id = e.id AND regenerable = 0)` | Correct, but a per-entity subquery on the largest table in the store — and see the `SCAN f2` precedent for what that costs unmeasured. |
| (b) exclude code-ish `kind` values by list | `kind NOT IN ('symbol','symbol-name',…)` | **Reject.** This is the same enumerated-list drift that caused the companion spec's bug. A new code entity kind would silently re-break it. |
| (c) **drop the entity terms from the fingerprint** | remove `entity` and `entity_alias` counts | **Recommended.** |

**Why (c) is right, not just cheap.** An entity is *addressing metadata* — `CLAUDE.md` says
as much of `path`, which "follows its entity on rename (D2)" and is explicitly not belief
content. A snapshot exists to protect **beliefs**. An entity carrying no authored fact holds
nothing a restore would miss; an entity carrying one is already counted through the fact
terms, which move when that fact is written. **The entity terms are either redundant or
irrelevant, and they are the noisiest inputs in the query.**

---

## 3. The rule

> The fingerprint moves when, and only when, **authored truth** moved. Authored truth is
> `fact WHERE regenerable = 0`, plus the supersession structure over those facts. Everything
> else in the store is regenerable, or is addressing metadata that follows an entity.

### 3.1 Per-term ruling

| term | today | proposed | reasoning |
|---|---|---|---|
| `COUNT(*) FROM fact` | all | `WHERE regenerable = 0` | The core fix. |
| `MAX(id) FROM fact` | all | `WHERE regenerable = 0` | §2.1 — without this the fix does nothing. |
| `COUNT(*) FROM fact WHERE valid_to IS NOT NULL` | all closed | `AND regenerable = 0` | A code fact closed by re-indexing is not a retraction of a belief. |
| `COUNT(*) FROM entity` | all | **remove** | §2.2(c). |
| `COUNT(*) FROM entity_alias` | all | **remove** | Same argument — alias is addressing. |
| `COUNT(*) FROM supersession` | all | **must be restricted** — see below | Code facts are superseded on every re-index, so an unrestricted count re-introduces the whole bug through a side door. |
| `COUNT(*) FROM edge` | all | **DETERMINE** | I do not know this table's semantics well enough to rule. |
| `COUNT(*) FROM fact_relation` | all | **DETERMINE** | Same. |
| `COUNT(*) FROM fact_sync_request` | all | **DETERMINE** | Same. |

**On `supersession`**: restrict to rows whose superseding fact is authored — the natural
form is a join to `fact` on the new id with `regenerable = 0`. Confirm which column that is
(`ix_supersession_new` is on `new_fact_id`, so the index exists for it) before writing it.

**On the three DETERMINE rows** — I am not guessing at tables I have not read, and the
honest output is the test rather than a verdict. **Default for each: exclude it if its row
count moves during an `index --apply` that writes no authored fact.** That is a single
empirical check per table (NE-2), and it is the same question in each case, so it costs one
run, not three.

---

## 4. No migration is needed — verify, then do not build one

`FingerprintOf` (`BackupStore.cs:545`) opens the snapshot's `.db` and **recomputes** the
fingerprint from it; `Due` (`:291`) recomputes the live one. Both sides are therefore
produced by whatever code is running now, so **old snapshots are re-fingerprinted under the
new query automatically** and no stored value goes stale.

**Verify before relying on this** (NE-1): if a fingerprint is *also* persisted in a manifest
alongside the snapshot, that copy would be incomparable and the first comparison after the
change would differ. Even then the consequence is benign — **one extra snapshot, once** —
which is not worth a version field. Say so explicitly in the change, because the reflex here
is to add one.

`Diagnostics.cs:476` prints these counts to a human (`"{n} live fact(s), {m} closed"`).
After the change those numbers mean *authored* facts, which is a **better** thing for
`doctor` to report but a different one. Relabel the line; a count whose meaning silently
narrows is how D43 happened.

---

## 5. What this deliberately does not fix

**The journal still exports every fact, including regenerable ones.** That is documented and
intended — `backups/facts.jsonl` is "every fact in plain text". This spec changes *when* a
snapshot is taken, never *what it contains*.

The consequence is worth stating so it is not read as an oversight: after this change, a
store whose only recent activity was `index --apply` will not snapshot, so its journal's
copy of the code facts goes stale. **That is correct under D8** — losing regenerable facts
from a backup costs one `index --apply` to recover, not authored truth. A backup that
rewrites hundreds of MB to protect state that can be recomputed is spending the wrong
resource.

---

## 6. NEEDS-EVIDENCE

**NE-1 — is a fingerprint persisted anywhere besides the snapshot `.db`?** Grep the backup
manifest/metadata writer for stored fingerprint fields.
*Decides:* whether §4's "no migration" claim holds. If a persisted copy exists, the change
still ships — the cost is one extra snapshot — but the claim in the commit message must be
corrected rather than repeated.

**NE-2 — which of `edge`, `fact_relation`, `fact_sync_request` move on a code-only index
run?** Record all three counts, run `index --apply` on a repo, record them again. Any table
whose count moved is regenerable-driven and leaves the fingerprint.
*Decides:* the three DETERMINE rows in §3.1, in one run.

**NE-3 — what does the fix actually save?** This is the audit's NE-C and it is what tells us
whether the change was worth making: time the detached `backup take` plus journal rewrite on
a store holding 50k+ code facts, and record snapshot size and `.jsonl` line count.
*Decides:* whether this is a disk-only problem or also a maintenance-window one — and
therefore how to describe it in the commit. **Take this measurement before the fix**, or
there is nothing to compare against afterwards.
