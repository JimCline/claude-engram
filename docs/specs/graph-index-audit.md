# Index audit — every `engram_navigate` relation (tip `4c2fe07`)

Read-only. Nothing here was executed; no `EXPLAIN QUERY PLAN` was run and no timing was
taken. Index *absence* below is read off `docs/engram-schema.sql` and is fact. Every
*magnitude* claim is a NEEDS-EVIDENCE item in §5, not a verdict.

> **Path note.** The dispatching brief named files to read but did not dictate an output
> path. I chose `docs/specs/graph-index-audit.md`. Move it if that is wrong — nothing
> references it yet.

---

## 0. Headline

> **There is no index on `fact.object_id`.** The code graph is stored *directionally*
> (`docs/specs/close-graph-query-gap.md` §0: `decl --calls--> name` is stored, the return
> leg is not), so **every "who points at X" question is a reverse-edge lookup**, and the
> reverse direction is unindexed. This hits `callers` and — three times per call —
> `implementers`.

The complete `fact` index list is:

| index | columns | partial on |
|---|---|---|
| `ux_fact_live` | `(subject_id, predicate)` | `valid_to IS NULL AND object_id IS NULL` |
| `ux_fact_edge_live` | `(subject_id, predicate, object_id)` | `valid_to IS NULL AND object_id IS NOT NULL` |
| `ix_fact_thread` | `(subject_id, predicate)` | — |
| `ix_fact_path` | `(path)` | — |
| `ix_fact_session` | `(session_id)` | — |
| `ix_fact_scope` | `(scope)` | `valid_to IS NULL` |
| `ix_fact_regenerable` | `(regenerable)` | `regenerable = 1` |

`ux_fact_edge_live` *does* name `object_id` — as its **third** column, behind `subject_id`,
which is unconstrained in every reverse-edge query. A B-tree cannot seek on its third
column with the first unbound. **This is the `ix_fact_thread` situation exactly**: an index
that names the right columns and cannot answer the question, because the leading column or
the partial predicate is wrong. That precedent cost 93% of recall latency and was found by
pairing a plan with a clock; the same pairing is what §5 asks for here.

`predicate = ?` alone is also unindexed — every index naming `predicate` leads with
`subject_id`. So for the reverse-edge queries **both plausible join orders end in a full
scan of `fact`**: start at `entity` (one or N rows via the UNIQUE `path` autoindex) and
there is no index to descend into `fact` with; or start at `fact` and there is no index for
`predicate`/`valid_to`.

### The inventory predicts the measured shape

This is the part that raises confidence above plan-reading. From
`docs/specs/navigate-latency-results.md` @50k:

| arm | callers | callees |
|---|---|---|
| no-match | 11.85 ms | 12.07 ms |
| distinctive | 13.99 ms | 7.96 ms |
| hub | 29.65 ms | 11.19 ms |

- **No-match ties** (11.85 / 12.07) — both are dominated by `SymbolResolver.Resolve`'s three
  failed tiers, and neither reaches its edge query. Already explained by §9.3.
- **Distinctive and hub diverge sharply**, and the index inventory says why:
  `callees` filters `f.path IN (...)`, which **`ix_fact_path` serves**; `callers` filters
  `o.path IN (...)` on the *entity* table and then has no index to reach `fact`. One is an
  index seek, the other is a scan.

§9 attributed the `callers` cost to `MatchingSymbolNames` scanning `entity`. That is real
and it is not the whole story: **there are two O(corpus) reads on the `callers` path**, and
the second one — the `fact` scan — sat behind an explanation that already accounted for the
symptom. That is precisely how `SCAN f2` stayed ranked low.

*Stated as consistent-with, not as proof. §5 NE-1 is what would settle it.*

---

## 1. Per-relation verdicts

| relation | edge query indexed? | verdict |
|---|---|---|
| `defined_at` | n/a (entity-only) | **KNOWN / PRICED** — §9.3, unchanged |
| `imports` | `ix_fact_thread` | **SERVED** |
| `callers` | **none** | **GAP** — full `fact` scan |
| `callees` | `ix_fact_path` | **SERVED** |
| `implements` | `ix_fact_thread` | **SERVED** (note round-trip count) |
| `implementers` | **none, ×3** | **GAP — worst on the list, net-new, never audited** |
| `members` | `ix_fact_thread` | **SERVED** |

`inherits`, `derives-from` and `contains` are **not separate relations** — they are
predicates reached through `implements` / `implementers` / `members`. Audited there.

### 1.1 `defined_at` — KNOWN, already priced, not re-flagged

`SymbolResolver.Resolve`, three tiers on `entity`
(`SymbolResolver.cs:59-83`). `entity.name` has **no index at all**; `ix_entity_kind(kind)`
is the only entity index besides the UNIQUE `path` autoindex.

Already documented in `close-graph-query-gap.md` §9.3, including the two facts that make it
unfixable by an ordinary B-tree: a default-collation index cannot serve tier 2's
`COLLATE NOCASE`, and no B-tree serves tier 3's leading-wildcard `LIKE`. Per the brief, not
re-flagged. **The one open question there stays open** — all three tiers end
`ORDER BY e.path LIMIT $limit` against a UNIQUE `path`, so SQLite may already be walking the
path autoindex in order and filtering, which would make all three tiers uniformly O(corpus)
regardless of any name index. That is §5 NE-3 and it is a **prerequisite for any fix**, not
for this verdict.

### 1.2 `imports` — SERVED

`QueryFileEntities` (`EngramMcpTools.cs:1465`) then `FactStore.History` per file
(`FactStore.cs:444`):

```sql
... FROM fact f JOIN entity e ON e.id = f.subject_id
WHERE e.path = $path AND f.predicate = 'imports' ORDER BY f.id;
```

UNIQUE `entity.path` → one row → `(subject_id, predicate)` → **`ix_fact_thread` serves this
exactly**. This is the index doing the job it was added for. `ORDER BY f.id` sorts a
single file's imports; negligible.

### 1.3 `callers` — GAP

`Resolve`, then `MatchingSymbolNames` (`CodeCallGraph.cs:195-214`), then
`LiveCallsToObjects` (`:216-248`):

```sql
SELECT f.path, o.name, f.analyzer_tier FROM fact f JOIN entity o ON o.id = f.object_id
WHERE f.predicate = 'calls' AND f.valid_to IS NULL AND o.path IN (...)
```

`o.path IN (...)` seeks the entity autoindex fine. Descending into `fact` by `object_id`
has **no index**. Full scan.

**Second read on the same path**: `MatchingSymbolNames` is
`SELECT e.path, e.name FROM entity e WHERE e.kind = 'symbol-name';` — **no `LIMIT`, no
`WHERE` beyond `kind`**, every matching row into C# for leaf filtering. `ix_entity_kind` is
single-column and low-cardinality; SQLite may well decline it and scan. Known and priced
(§9.3's computed-leaf column), but see §2.2 — graph-enhance changed its priority.

### 1.4 `callees` — SERVED

`LiveCallsFromSubjects` (`CodeCallGraph.cs:250-282`) filters **`f.path IN (...)`**, served
by `ix_fact_path`. The `JOIN entity o ON o.id = f.object_id` is a primary-key lookup per
returned row. This is the well-indexed member of the pair, and the measured numbers agree.

### 1.5 `implements` — SERVED, with a round-trip note

`Resolve`, then per symbol match, a loop over `["inherits", "implements", "derives-from"]`
calling `FactStore.History` (`EngramMcpTools.cs:1119-1181`). Each call is an
`ix_fact_thread` seek — correctly indexed.

Note, not a gap: this is **3 × N round trips** for N resolved matches. Each is cheap; the
count is the H2 shape (`close-graph-query-gap.md` §9.5), which remains unmeasured. Also
worth knowing: `History` deliberately returns **closed rows too** (no `valid_to` filter) —
correct for a history call and the reason `ix_fact_thread` is non-partial, but it means
liveness filtering happens in C# on a row set that includes superseded versions.

### 1.6 `implementers` — GAP, and the worst on the list

`MatchingSymbolNames`, then a **loop over three predicates**
(`EngramMcpTools.cs:1183-1303`), each running:

```sql
SELECT f.path, f.analyzer_tier FROM fact f JOIN entity o ON o.id = f.object_id
WHERE f.predicate = $predicate AND f.valid_to IS NULL AND o.path IN (...)
[AND f.path LIKE '%' || $repo || '%'] LIMIT 1000;
```

Same missing index as `callers`, **three times per call**, plus the unbounded symbol-name
read. Net-new in graph-enhance; reviewed for correctness (append-only, dedup, caveats) but
never for query plan.

Note the repo filter is `f.path LIKE '%' || $repo || '%'` — a **leading wildcard**, so
`ix_fact_path` cannot serve it either. It is a post-filter in every plan.

**This relation's matching changed today** (leaf-match via `MatchingSymbolNames`). The
+4.63 ms figure in circulation is the **hub** arm — one shape. Per §9.3's asymmetry the arm
that should worry us is **no-match**, where `Resolve` exhausts all three tiers; that is
NE-2.

### 1.7 `members` — SERVED

`Resolve` then one `FactStore.History` with `predicate = 'contains'` — `ix_fact_thread`.

---

## 2. Gaps ranked by expected cost

### 2.1 GAP-1 — no index on `fact.object_id` *(highest)*

Affects `callers` (one full `fact` scan) and `implementers` (three). Both are the reverse
direction, which §0 of the gap spec says is the *entire shape* of the code graph.

**Proposed:**

```sql
CREATE INDEX ix_fact_object ON fact(object_id, predicate) WHERE valid_to IS NULL;
```

- **Leading `object_id`** is the seek these queries need.
- **`predicate` second** serves `implementers`' per-predicate loop and `callers`' single
  `predicate = 'calls'` without a second index.
- **Partial on `valid_to IS NULL`** because *every* reverse-edge query filters it, and it
  keeps closed edges out of the index — which matters on an append-only store where the
  closed set only grows.

**Carry the `ix_fact_thread` lesson forward explicitly, in a comment on the index.** A
partial index cannot answer outside its predicate. If a future query ever wants object-side
*history* (closed edges included — e.g. "what did this type used to implement"), it will
need a **second, non-partial** index on the same columns, and the two will look redundant to
whoever reads the schema next. That pair already exists once here (`ux_fact_live` /
`ix_fact_thread`) and deleting the "redundant" one cost 93% of recall latency. Say so at the
definition site so it is not rediscovered the expensive way.

**Cost side, and it is not free.** `fact` already carries seven indexes and the code indexer
writes edge facts in bulk — a full re-index is 9,559 ms @50k. One more partial index adds
write cost on exactly that path. That is NE-4: measure the write side, not just the read
side, before landing.

### 2.2 GAP-2 — `MatchingSymbolNames` is an unbounded full read *(known; priority changed)*

The scan itself is already priced in §9.3, and the fix already named there is the computed
leaf column plus `(kind, leaf)` index — **not** the `(kind, name)` index, which cannot serve
leaf matching done in C#.

**What is net-new:** graph-enhance gave this scan a **second caller**. It now runs for
`implementers` as well as `callers`, and today's leaf-match change is what put it there. A
scan whose cost was accepted on one relation is being inherited by another without that
acceptance being re-examined. Not a new gap — a known gap whose blast radius grew.

### 2.3 GAP-3 — `ix_entity_kind` is single-column and low-cardinality *(note only)*

`kind` takes a small set of values and `symbol-name` is plausibly a large fraction of
`entity`. SQLite may decline the index and scan outright. This is **not independently
fixable** — the answer is 2.2's computed leaf column, which would give
`WHERE kind = 'symbol-name' AND leaf = ?` a composite index worth choosing. Recorded so
nobody adds an `(kind, name)` index expecting it to help; it would not, because the leaf
match is in C#.

### 2.4 NOT AN INDEX ISSUE, found by this audit — `implementers` truncates silently

`LIMIT 1000` is a **constant, applied per predicate** (`EngramMcpTools.cs:1214-1216`), so
the effective ceiling is 3000 across the loop and nothing tells the caller when it was hit.

Under `close-graph-query-gap.md` §8.5.3 item 4 — written earlier today — **a returned list
makes an implicit completeness claim**, and a truncated list of implementers is exactly the
"plausible partial answer that stops the search" that rule exists to forbid. This is a live
instance of the rule in shipped code, and it is cheap to fix: detect the cap and mark it.
It also passes §11.1's discrimination test cleanly — it fires only when the cap is actually
reached, which is rare, so it is a legitimate per-result note rather than a banner.

**Ranked here because it is a correctness finding, not a performance one**, and because it
would survive every index change above unchanged.

---

## 3. What graph-enhance did *not* invalidate

The original four relations' measurements stand. `callees`, `imports` and `members` are
correctly served by existing indexes; `defined_at`'s cost is unchanged and already priced.
Nothing in graph-enhance altered `SymbolResolver.Resolve` or `ix_fact_path`.

---

## 4. What I did not decide

- **Whether to add `ix_fact_object` at all.** The read case is strong and the write cost is
  unmeasured (NE-4). If the measured read win is small, the honest answer is to leave it and
  record the mechanism — §9.2's rule table, applied to this instead of to the resolver.
- **Whether `implementers`' three-predicate loop should be one query** with
  `predicate IN (...)`. That collapses three scans into one and would change the ranking
  above, but it interacts with the per-predicate `LIMIT 1000` and with §8.5.3.2's
  one-implementation rule for the predicate union. A real design question, not a tuning
  one — flag it if you want it answered.

---

## 5. NEEDS-EVIDENCE — nothing below is asserted, all of it needs a run

Every item pairs a **plan** with a **clock**, per the `ix_fact_thread` and `SCAN f2`
precedents: a plan can show the scan and cannot show what fraction of the statement it is.

Protocol for all timing items is `close-graph-query-gap.md` §4 unchanged — published binary,
alternate the arms, self-vs-self calibration, subtract the `probe` floor, time through a
file, `ENGRAM_HOME` set on every invocation.

**NE-1 — confirm the scan.** `EXPLAIN QUERY PLAN` on the 50k fixture for:
(a) `LiveCallsToObjects` (`CodeCallGraph.cs:216-248`), (b) the `implementers` per-predicate
query (`EngramMcpTools.cs:1214-1216`), (c) `LiveCallsFromSubjects` (`:250-282`) as the
control that *should* read `SEARCH … USING INDEX ix_fact_path`.
*Decides:* whether GAP-1 is real. `SCAN f` on (a)/(b) with `SEARCH` on (c) confirms it.

**NE-2 — `implementers` across all three arms, 5k and 50k.** no-match / distinctive / hub.
The circulating +4.63 ms is hub-only. §9.3 established that the **no-match** arm is the
expensive one because it exhausts all three resolver tiers, so benchmarking only hits would
miss the worst case — which is how this class of finding was missed before.
*Decides:* where `implementers` sits against the 50 ms budget, and therefore whether
§9.2's rule table says fix-now or defer.

**NE-3 — the `ORDER BY e.path LIMIT` question** (carried over from §9.3, unchanged).
`EXPLAIN QUERY PLAN` all three `Resolve` tiers. If SQLite is already walking the `path`
autoindex to satisfy the ORDER BY, all three tiers are uniformly O(corpus) and no name-side
index changes anything.
*Decides:* whether any resolver-side fix is worth scoping at all. **Prerequisite** for
GAP-2/GAP-3 work.

**NE-4 — A/B `ix_fact_object`, both directions.** Read: `callers` and `implementers`, all
arms, with and without the index at 50k. **Write: full re-index wall time with and without**
(baseline 9,559 ms @50k). Build the two arms as a controlled pair of binaries either side of
the index, and calibrate same-binary-against-itself first.
*Decides:* whether GAP-1's fix is worth its write cost.

**NE-5 — does `implementers` actually hit `LIMIT 1000` on any real corpus?** Cheap: count
live facts per inheritance predicate grouped by `object_id` on this instance and on the 50k
fixture, and report the max.
*Decides:* whether §2.4's marker is urgent or merely correct. It should be built either way
— the rule does not depend on the frequency — but the frequency decides the ordering.
