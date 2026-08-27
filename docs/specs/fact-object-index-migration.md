# `ix_fact_object` — ruling, final wording, and migration 16

Design only. Nothing here was executed. Every code fact is cited from the tree at `6e2510a`
(`SchemaVersion = 15`); every magnitude is either quoted from the Implementor's measurement
pass or a NEEDS-EVIDENCE item in §8.

**Supersedes** `docs/specs/graph-index-audit.md` §1 (which proposed the index) and its NE-4
(which asked for the A/B that has now been run). The audit's wording is **not** the wording
to implement — §1 below narrows it, for a reason the audit did not consider.

---

## 0. Ruling: APPLY

Evidence, from the Implementor's pass and corroborated independently:

| | result |
|---|---|
| reads, `implementers` hit arm | **−23%** |
| reads, `callers` hit arm | **−11%** |
| reads, no-match arms | flat (expected — they never reach `fact`) |
| writes, full reindex | **+5.3%** |
| plan | `SCAN` → `SEARCH`, confirmed |

**The decisive argument is not the size of the win but its sign under growth.**

- The read win comes from replacing a **scan of `fact`** with a **seek**. A scan degrades
  linearly as the store grows; a seek does not. So −23% / −11% is a **floor at today's
  corpus, not a ceiling**.
- The write cost is one extra B-tree insert per indexed row — proportional to **rows
  written**, not to corpus size. +5.3% of a reindex stays +5.3% at any scale.

Two costs that diverge in opposite directions under growth make this decidable now, and
decidable in one direction.

### 0.1 Why NE-5 does not gate this, and what it becomes instead

NE-5 asks the maximum edges per `(object, predicate)` on a real corpus — i.e. how bad the
hub case is. It was flagged unanswerable from the synthetic fixtures. **It cannot change the
ruling**, and the reason is structural rather than empirical:

> A partial index seeked on `(object_id, predicate)` returns a **subset** of the rows the
> full scan it replaces returned. A hub object shrinks the win toward zero. It cannot make
> the index worse than the scan.

So NE-5 bounds the **magnitude** and cannot invert the **sign**. Holding for it would buy
precision about *how much* better while leaving a measured gap open, which is the wrong
trade. Note also that this is not the `SCAN f2` situation the repo's own rule warns about —
there the lesson was *a plan is not a clock*, and a plan finding was ranked low without a
timing. Here there is a plan **and** a clock, and they agree.

**NE-5 is therefore reclassified, not dropped** (§8). It stops gating this decision and
becomes a characterization item feeding a *different* future one: whether reverse-edge
lookups need hub handling of their own. That is a live question — `implementers` already
needed a truncation fix once (`5d4fb33`) — but it is a separate change with separate
reasoning, and folding it in here would be conflating two decisions the way today's audit
trail has repeatedly had to un-conflate them.

---

## 1. Final wording — narrower than the audit proposed

```sql
CREATE INDEX ix_fact_object ON fact(object_id, predicate)
  WHERE valid_to IS NULL AND object_id IS NOT NULL;
```

The audit proposed `WHERE valid_to IS NULL` only. **Add the second clause.** Three
independent reasons:

1. **A NULL-object row can never match `object_id = ?`.** `object_id` is nullable
   (`engram-schema.sql:120-159` — no `NOT NULL`, no default), and SQLite indexes NULLs by
   default. So every objectless fact would occupy an index entry that no reverse-edge lookup
   can ever hit: pure write cost and pure space, for zero read benefit.
2. **It mirrors the partition the store already uses.** `ux_fact_edge_live`
   (`:171-172`) is partial on `valid_to IS NULL AND object_id IS NOT NULL`; `ux_fact_live`
   (`:168-169`) takes the complement. Using the same split keeps one consistent notion of an
   edge-bearing row rather than introducing a third. Migration v13's own comment already
   reasons on exactly this axis — *"SQL treats NULLs as distinct, so adding object_id to a
   single index would constrain nothing for ordinary facts."*
3. **It should take a bite out of the measured +5.3%.** This is the part worth stating
   plainly: the narrower predicate removes index maintenance from the **entire
   authored-memory write path** — every `about` and `declared-as` fact, including
   `user-prompt`'s per-message write, which is a hook with a latency budget. Those facts are
   objectless, so under the wide form they would pay maintenance on an index that exists
   solely to serve code edges.

**Do not treat the +5.3% as still applying to this wording.** It was measured against the
audit's wider index. The narrower one indexes strictly fewer rows, so the true figure is at
or below it — but it is now an unmeasured number, and NE-1 re-measures it rather than
inheriting a figure from a different index.

---

## 2. The query-side change — state the predicate, do not bet on the inference

SQLite will only use a partial index if it can **prove** the query's `WHERE` terms imply the
index's. Its term-implication analysis is deliberately limited, and whether it derives
`object_id = ?` ⟹ `object_id IS NOT NULL` is a property of that analysis rather than
something the schema guarantees.

> **Write `AND <alias>.object_id IS NOT NULL` into the reverse-edge query text itself.**

This is semantically free — `object_id = ?` already excludes NULLs, since `= NULL` matches
nothing — and it converts the implication from something SQLite must infer into something it
can read. Do it unconditionally rather than testing whether the inference happens to work on
the current SQLite version: the failure mode is silent (the index is simply not used, every
correctness test still passes, and the change looks applied while delivering nothing).

Sites: the `callers` query and the `implementers` query. `callees` is untouched — it reads
the **forward** direction through `ix_fact_path` and was ruled independent in
`callees-fanout-resolution.md` §5. That spec flagged one conditional interaction, via the
derived `name → declaration` index it *rejected*; since the shipped fix was batching
(`c86fae4`), the interaction never materialized and this closes cleanly.

---

## 3. Migration 16 — the mechanical reasoning, not the analogy

The brief asked for this to be reasoned mechanically rather than by analogy to the v15
trigger fix. It is, and the answer **agrees on the bump and disagrees on the body**.

### 3.1 Why a bump is still required — unchanged, and this half is the load-bearing one

`docs/engram-schema.sql` is an `EmbeddedResource` (`Engram.Core.csproj:54-59`) read by
`ReadSchemaSql` (`EngramDatabase.cs:161-166`) and applied by `EnsureSchema` (`:135-157`)
**only when the store is new**. An existing store takes the `Migrate` branch, and its index
list lives in its own `sqlite_master`.

> Without a migration, the −23% / −11% accrues **only to stores created after this change**.
> Every existing store keeps the scan forever.

That is the same mechanism as v15 and it is why the analogy holds where it holds.

### 3.2 Where it differs — there is nothing to replace

The trigger case needed `DROP` + `CREATE` because an existing **wrong definition** had to be
replaced, and `CREATE TRIGGER IF NOT EXISTS` no-ops against one that already exists. That in
turn dragged in `RebuildFactFts`, because dropping the FTS table discards its content.

**None of that applies.** No `ix_fact_object` exists in any store at any schema version
(confirmed: zero occurrences outside spec documents). The change is purely additive, there is
no prior state to reconcile, and an index has no content to rebuild — SQLite populates it at
creation from the table.

**This repo has already written down exactly this distinction.** Migration v5
(`EngramDatabase.cs:295-302`) added `ix_fact_thread`, and its comment is the precedent:

> *"Version 5 adds ix_fact_thread, which is pure query planning: it creates no state, so
> unlike version 4 there is nothing to reconcile with a store that already has it, and
> IF NOT EXISTS covers a downgrade fixture that does."*

v5, not v13, is the template.

### 3.3 The migration

```csharp
if (from < 16)
{
    // Pure query planning, like ix_fact_thread at v5: creates no state, reconciles with
    // nothing, and the reverse-edge lookups it serves are correct without it — only slow.
    // Partial on object_id IS NOT NULL to match ux_fact_edge_live's partition: a row with
    // no object can never match object_id = ?, so indexing one is write cost and no read.
    Execute(
        connection,
        null,
        """
        CREATE INDEX IF NOT EXISTS ix_fact_object ON fact(object_id, predicate)
          WHERE valid_to IS NULL AND object_id IS NOT NULL;
        """);

    WriteMeta(connection, null, "schema_version", "16");
}
```

`IF NOT EXISTS` for v5's stated reason — it covers a downgrade fixture that already has the
index — **not** as a defence against re-running, which `from < 16` already handles.

---

## 4. D31 applies, and do not seek an exemption

This is a migration, so D31's unconditional `VACUUM INTO` runs before it. One snapshot per
store, once.

The tempting argument — *an additive index is not a structural rewrite, so exempt it* — is
the argument D31 exists to refuse. It is unconditional **by design**, so that nobody makes
this judgment per-migration; the per-migration judgment is the part that goes wrong. Same
ruling as `edge-fact-lane-eligibility.md` §2.4.3, three hours earlier, and it should stay the
same ruling for the same reason.

**One consequence worth stating, because it looks like an objection and is not:** a store
still on 14 that opens under the new binary does **not** take two snapshots. `Migrate` runs
15 and 16 in a single open, behind a single D31 snapshot.

---

## 5. Tests — the D60 trap, and the pattern that is already in the tree

### 5.1 The fixture must drop the index first. This is not optional.

`CLAUDE.md` records the exact failure, for exactly this DDL form: `WriteVersion1Store` rolls
a **current**-schema store back, so `CREATE INDEX IF NOT EXISTS` no-opped and a deliberately
wrong migration left **18 of 18 green** until the test dropped the index first.

`WriteVersion1Store` (`tests/Engram.Integration.Tests/SchemaMigrationTests.cs:160-180`) drops
columns and tables; **it does not drop indexes.** So a v16 test written on it alone would
prove nothing.

**The fix already exists in the tree and should be copied, not reinvented:**
`AMigratedStore_HasTheSameThreadIndexAsAFreshOne` (`:1021-1037`) calls `WriteVersion1Store`,
then opens the store separately and executes `DROP INDEX ix_fact_thread;` (`:1029`) *before*
migrating. That drop is the D60 fix, already applied to the v5 index migration. The v16 test
is that test with the names changed.

### 5.2 Compare shape, NOT DDL text — and the reason is a real mechanical difference

**Do not write a `SELECT sql FROM sqlite_master WHERE name = 'ix_fact_object'` comparison
between a fresh and a migrated store.** It would fail for a cosmetic reason, and the natural
"fix" is to mangle one of the two copies.

Measured from the tree: the schema file writes `CREATE INDEX ix_fact_thread  ON
fact(subject_id, predicate);` (column-aligned, two spaces, no `IF NOT EXISTS`) while v5's
migration writes `CREATE INDEX IF NOT EXISTS ix_fact_thread ON fact(subject_id, predicate);`.
`sqlite_master.sql` stores the text as written, so **for an index the stored DDL legitimately
differs between a fresh store and a migrated one** — which is presumably why `ThreadIndexShape`
(`:1041,1048`) reads `pragma_index_list` and `pragma_index_info` rather than using the
`LexicalDdl` text comparison (`:1055-1056`) that the trigger tests use.

> **This is a genuine mechanical difference from the v15 trigger case.** Triggers are
> compared by DDL text, so both copies had to be byte-identical. Indexes are compared by
> shape, so they need not be — and `IF NOT EXISTS` means they will not be.

Follow `ThreadIndexShape`. Assert the column list `object_id,predicate` and **`partial=1`** —
noting that `ix_fact_thread` asserts `partial=0`, so this value is the half that differs and
the half that catches a dropped `WHERE` clause.

### 5.3 Shape is not sufficient — one plan assertion is required

`pragma_index_list.partial` reports **whether** an index is partial, never **what** its
predicate says. So §5.2's assertions cannot distinguish the correct predicate from a wrong
one, and a predicate SQLite declines to match degrades silently to a scan with every
correctness test still green — the same silent-failure shape as §2.

> Add one `EXPLAIN QUERY PLAN` assertion over the **real** `callers` and `implementers`
> queries (after §2's clause is added), asserting `SEARCH` … `USING INDEX ix_fact_object`.

That is the assertion that proves the predicate is *usable*, which is the property that
actually matters and the only one that fails loudly.

### 5.4 Falsification

Per this repo's rule that a guard which cannot fail is worthless: break the migration —
reverse the column order, or drop the `WHERE` clause — and confirm each assertion reddens.
Per D60, run `git diff --quiet` first to confirm the break actually landed; a falsification
that silently no-ops reports success while proving nothing.

---

## 6. Lockstep list

| item | file | note |
|---|---|---|
| `CREATE INDEX ix_fact_object …` | `docs/engram-schema.sql`, beside the other `fact` indexes (~`:184-192`) | fresh stores. Text need not match the migration's (§5.2). |
| new `if (from < 16)` block | `EngramDatabase.Migrate`, after the `from < 15` block at `:501` | existing stores |
| `SchemaVersion = 15` → `16` | `EngramDatabase.cs:23` | |
| seeded `schema_version` `'15'` → `'16'` | `docs/engram-schema.sql:487` | **easy to miss.** If stale, every freshly created store immediately believes it needs migrating. |
| `AND object_id IS NOT NULL` in query text | `callers`, `implementers` | §2 — without this the index may simply never be used |
| new migration test | `SchemaMigrationTests.cs`, modelled on `:1021-1037` | must drop the index first (§5.1) |

---

## 7. Deferred, and deliberately not done

- **Not making it covering.** `(object_id, predicate, subject_id)` would let the lookup
  resolve the subject without touching the table. The measured win is already there at two
  columns; widening costs write and space now against an unmeasured further gain. Revisit
  only if a later measurement shows the table fetch dominating — not before.
- **Not touching `callees`** — §2.
- **Not re-opening NE-5 as a gate** — §0.1.
- **Not adjusting the hub case.** Real, separate, and named in §0.1.

---

## 8. NEEDS-EVIDENCE

**NE-1 — re-measure the write cost against *this* wording.** The +5.3% was measured on the
audit's wider index. Re-run the reindex arm with the `object_id IS NOT NULL` predicate.
*Decides:* nothing about whether to apply — §0's argument holds at +5.3%. It decides what
number goes in the commit message, and §1 reason 3 predicts a reduction, so this is a
falsifiable prediction rather than a formality. **Reuse the original arm's fixture and
method**, or the comparison is meaningless.

**NE-2 — confirm the plan on the real queries after §2's clause.** `EXPLAIN QUERY PLAN` on
`callers` and `implementers`, asserting `USING INDEX ix_fact_object`.
*Decides:* whether the partial index is actually reachable. **Run this before believing any
read improvement** — §2's failure mode returns correct results at scan speed.

**NE-3 — migration 16 on a real pre-16 store.** Open a store created before the change with
the new binary; confirm `pragma_index_list('fact')` now lists `ix_fact_object` with
`partial=1`, the D31 snapshot was taken, and `schema_version` reads 16. Re-open and confirm
nothing runs twice.
*Decides:* effective **and** idempotent — the two properties a one-line migration gets wrong
in opposite directions.

**NE-4 — time the v16 open on a 50k-fact store.** The D31 `VACUUM INTO` almost certainly
dominates the index build, but neither has been measured on this path, and this runs
unattended on open.
*Decides:* whether the one-time migration cost needs mentioning to users, or is invisible.

**NE-5 (reclassified — no longer a gate).** Max edges per `(object_id, predicate)` on a real
large open-source repository, indexed for real rather than synthesized.
*Decides:* **not** whether to apply this index (§0.1), but whether reverse-edge lookups need
hub handling — a separate change. Record the distribution, not just the maximum: the maximum
alone cannot distinguish one pathological symbol from a broadly flat corpus.

---

## 9. Off-brief observation — flagged, not ruled

`docs/engram-schema.sql:192` carries:

```sql
CREATE INDEX ix_fact_regenerable ON fact(regenerable) WHERE regenerable = 1;
```

Every value inside that partial index is identical, so it functions as a rowid list of
regenerable rows — a legitimate shape for `WHERE regenerable = 1` (which `StoreCompactor`
uses), but it **cannot serve `regenerable = 0`**, which is what
`backup-fingerprint-semantics.md` filters on.

So either it predates today and serves compaction, or it arrived with `30e44bd` pointing at
the wrong half of the column. **I have not checked which, and this is outside the dispatch.**
One `git log -S` on that line answers it. Recorded here only so it is not lost; no verdict
implied.
