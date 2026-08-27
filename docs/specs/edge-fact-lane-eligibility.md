# Edge-fact lane eligibility — fix, and repair what is already written

Design only. Nothing here was executed. Every code fact below was read from the tree at
`5d4fb33` and is cited; every magnitude is a NEEDS-EVIDENCE item in §7, not a claim.

**Companion spec:** `docs/specs/backup-fingerprint-semantics.md` (consequence 3, deliberately
separate — different root cause, different fix, different blast radius).

**Amendment 2026-08-27 — §2.4 added, §3.2 and §4 amended.** The Implementor stopped on a gap
this spec did not address: the trigger DDL is duplicated into `docs/engram-schema.sql`, and
changing it raises a schema-version question. §2.4 rules on it. The answer simplifies §3.2
rather than complicating it, and the reason is in §2.4.2.

---

## 0. The diagnosis in the brief is half right, and the wrong half is the expensive one

The dispatch stated one root cause — *`EdgeBearing` never learned the four new predicates* —
with three consequences. **That is true for two lanes and false for the third**, and the
third is the one carrying the cost.

| lane | gates on `EdgeBearing`? | so the new predicates are… | is this a graph-enhance regression? |
|---|---|---|---|
| `fact_fts` | yes — trigger literal `NOT IN ('calls','imports')` ×4 (`EngramDatabase.cs:545,552,558,565`) + `RebuildFactFts`'s copy (`:523`) | wrongly **included** | **YES** |
| `fact_token` | yes — `FactTokenIndex.Add` early return (`FactTokenIndex.cs:65`), repair copy (`:106`) | wrongly **included** | **YES** |
| `VersionCounts` | yes — `NOT IN ({EdgeBearingSqlList})` (`FactStore.cs:531`) | wrongly **included** | **YES** |
| `ReadLive` | yes — (`FactStore.cs:338`) | wrongly **included** | **YES** |
| **embedding** | **NO — no predicate filter of any kind** | included, **as `calls` and `imports` always have been** | **NO — pre-existing** |

`VectorIndex.ReadBackfillBatch` (`VectorIndex.cs:151-178`) selects:

```sql
SELECT f.id, f.body
FROM fact f
LEFT JOIN {TableName} v ON v.fact_id = f.id
WHERE f.valid_to IS NULL AND v.fact_id IS NULL
ORDER BY f.id
```

**Temporal and index-state only. It has never consulted `EdgeBearing`, or any predicate
list.** The brief's phrasing — *"no eligibility check beyond whatever `EdgeBearing`
currently lists"* — describes a check that is not there.

### Three consequences of that correction, all load-bearing

1. **Adding four strings to `EdgeBearing` fixes FTS, token, `VersionCounts` and `ReadLive`,
   and does nothing at all for embedding.** A fix that stops at the list leaves the
   hours-of-backfill problem entirely in place while looking complete.
2. **The embedding bug is older and larger than the regression.** `calls` and `imports`
   have been embedded since the vector lane existed. At a 50k-function corpus those two
   predicates dominate the count; the four new ones are an increment on an existing
   population, not the population. **Whoever runs NE-1 must group by predicate**, or the
   numbers will be attributed to graph-enhance when most of them predate it.
3. **The repair paths differ.** FTS/token repair removes rows that a correct build would
   never have written. Vector repair removes rows that were written under the rule as it
   then stood. Same operation, different justification — and the second one needs the
   pre-existing population named out loud so nobody reports it as damage this branch did.

**This is the same caution the brief itself applied to consequence 3** — *don't conflate
root causes just because one audit found them in one pass*. It simply was not applied here.

---

## 1. What an edge fact's body actually contains

Load-bearing for every decision below, because the whole argument for exclusion is a claim
about this string. All six are built in `DeepAnalysis.DeepTier.Merge`:

| predicate | body expression | file:line |
|---|---|---|
| `imports` | `Cap("imports " + m)` | `DeepAnalysis.cs:218` |
| `calls` | `Cap("calls " + call.Callee)` | `:231` |
| `contains` | `Cap("contains " + symbol.Name)` | `:183` |
| `inherits` / `implements` / `derives-from` | `Cap(inherit.Predicate + " " + inherit.BaseName)` | `:205` |

> **Every body is `predicate + " " + objectName` — a mechanical restatement of the fact's
> own `(predicate, object_id)` pair. It carries no information that is not already in the
> edge.**

That is the real justification for exclusion, and it is stronger than "edges have no
prose": these bodies are *derivable from the row that holds them*, so a text lane indexing
them adds retrievable surface without adding retrievable information — D44's
corroboration-shaped noise, exactly as the schema comment says.

It also draws the line in the right place: **`declared-as` and `about` are objectless and
their bodies are real content** (a declaration line, a distilled statement). They must stay
in FTS. Any fix that excludes them is worse than the bug.

---

## 2. The fix: stop enumerating predicates

### 2.1 Why not a shared list plus a cross-check test

The brief offered that shape and asked me to rule on it. **Rejected**, for a reason this
repo has already paid for once.

The set is currently spelled in **at least six places**: `EdgeBearing`
(`CodePredicates.cs:11-12`), `EdgeBearingSqlList`, four trigger literals, and
`RebuildFactFts`'s interpolated copy. Two of those are SQL inside triggers, and **a trigger
cannot call into C#** — which is precisely the situation `CLAUDE.md` documents for
`fact_token`: *"a trigger cannot call `Tokenizer` and a second tokenizer written in SQL
agrees with the first until one of them is tuned."*

A cross-check test would work. It is still the weaker answer, because **a guard can be
deleted, can be skipped, or can pass vacuously**, and this repo's own rule is that a lint
which cannot fail is worthless. The failure that just happened was a list that did not
learn a new member. A test that compares two lists does not remove that failure mode — it
detects it, once someone runs the suite, and only for lists it knows to compare.

### 2.2 The rule — a structural condition, no list at all

> **A fact is excluded from the text and vector lanes iff
> `regenerable = 1 AND object_id IS NOT NULL`.**

Both columns already exist on `fact`. Neither needs a column migration. The condition is
expressible in a trigger `WHEN` clause, in a `WHERE` clause, and in C# — **the same rule in
every lane, with nothing to keep in sync.**

Why each half is load-bearing:

- **`object_id IS NOT NULL`** is what "edge-bearing" *means*. The schema already treats it
  as the defining property: `ux_fact_live` is partial on `object_id IS NULL` and
  `ux_fact_edge_live` on `object_id IS NOT NULL`. A new edge predicate is excluded the day
  it is written, because it has an object — **there is no list to forget to update.**
- **`regenerable = 1`** is the fail-safe, and it is not redundant. An authored fact carrying
  an object is legal (D8 makes regenerability a separate axis from everything else). Without
  this clause, the first person to write one would find it silently absent from recall —
  a silent failure in the direction of losing authored truth from a lane, which is far worse
  than the noise being fixed. **With it, no authored fact can ever be excluded by this rule,
  whatever its shape.**

This satisfies both of the brief's asks at once: it is a single source of truth, and the
source is the data rather than a declaration.

### 2.2.1 Spell it with `IS`, not `=` — the fail-safe inverts under NULL

**`NOT (regenerable = 1 AND object_id IS NOT NULL)` is the wrong spelling.** If
`regenerable` is ever NULL, `NULL = 1` yields NULL, `NULL AND true` yields NULL, `NOT NULL`
yields NULL, and a `WHEN` clause of NULL does not fire — so the row is **excluded**. That is
the fail-safe pointing backwards: an unknown-regenerability fact would be silently dropped
from the lane, which is the exact failure §2.2 added the clause to prevent.

> **Required form, every site:** `NOT (regenerable IS 1 AND object_id IS NOT NULL)`
> (`new.` / `old.` prefixed inside triggers).

SQLite's `IS` is null-safe: `NULL IS 1` is false, so the row is **included**. Unknown
regenerability falls to the safe side — kept in the lane — in every lane and every spelling.

This does not depend on the column's current nullability, and deliberately so. **Verify it
anyway** (§2.4.5) and use `IS` regardless of the answer: a condition whose correctness rests
on a `NOT NULL` constraint elsewhere in the schema is one `ALTER TABLE` away from being
wrong, and it would fail silently.

### 2.3 What to change

**Delete `CodePredicates.EdgeBearing` and `CodePredicates.EdgeBearingSqlList` outright.**
All five production call sites become the same two-column condition:

| site | file:line | new condition |
|---|---|---|
| `FactTokenIndex.Add` guard | `FactTokenIndex.cs:65` | read `regenerable`/`object_id` in `ReadForIndexing`, return early on the condition |
| `FactTokenIndex` repair | `:106` | `WHERE NOT (regenerable IS 1 AND object_id IS NOT NULL)` |
| `FactStore.ReadLive` | `FactStore.cs:338` | same SQL form |
| `FactStore.VersionCounts` | `:531` | same SQL form |
| `RebuildFactFts` — backfill `SELECT` | `EngramDatabase.cs:523` | same SQL form |
| four FTS triggers — **C# copy** | `EngramDatabase.cs:545,552,558,565` | `WHEN … AND NOT (new.regenerable IS 1 AND new.object_id IS NOT NULL)` — note `old.` on the close/delete triggers, matching their existing column references |
| four FTS triggers — **schema copy** | `docs/engram-schema.sql:304,310,322,332` | identical text to the C# copy — **see §2.4** |
| **`VectorIndex.ReadBackfillBatch`** | `VectorIndex.cs:151-178` | **add the condition — this lane has no filter today** |

The four test call sites in `CodeNavigationPhase2Tests.cs` (`:257`, `:276-277`, `:326`,
`:335`) need updating to match; they currently assert against the list.

**Falsification requirement.** Per this repo's rule that a guard which cannot fail is
worthless, the new tests must be shown to fail: write a fact with
`regenerable = 1, object_id NOT NULL`, assert it is absent from `fact_fts`/`fact_token`/the
vector table, then flip the condition and confirm the assertion reddens. And write the
mirror case — an **authored** fact with an object — asserting it is **present** in all
three. The second is the load-bearing half: it is what proves the `regenerable` clause is
doing something, and a suite without it would pass with that clause deleted.

---

## 2.4 Schema-version ruling — the trigger change is a migration

The Implementor was right to stop, and right that this is not merely "is the edit
mechanically safe". Ruling follows, with the reasoning, because the reasoning is what makes
it checkable.

### 2.4.1 Yes — bump `SchemaVersion` 14 → 15. Not `AnalyzerVersion`, not `GrammarVersion`.

**A trigger is schema, not derived data, and that is the whole distinction.**

`AnalyzerVersion` (`CodeAnalyzer.cs:36`, currently 6) and `GrammarVersion`
(`CodePaths.cs:20`, currently 2) composite into `CodeIndexer.CurrentVersion`, whose only
effect is to force a full **re-index** when it moves. That is a derived-data stamp: it means
*regenerate what this produced*. Moving either one here would be **actively wrong** — no fact
is extracted differently, no subject is addressed differently, and §4 already rules that no
re-index is warranted. Bumping one to fix a trigger would force every store to re-read every
repository to change which lane indexes facts that are already correct.

The reason a bump of *some* kind is unavoidable is mechanical and specific to SQLite:

> **An existing store's triggers live in its own `sqlite_master`. Nothing in this codebase
> will ever replace them unless a migration does.**

`docs/engram-schema.sql` is an `EmbeddedResource` (`Engram.Core.csproj:54-59`) read by
`ReadSchemaSql` (`EngramDatabase.cs:161-166`) and applied by `EnsureSchema` (`:135-157`)
**only when the store is new**. An existing store takes the `Migrate` branch instead. So a
same-version edit to both copies would fix fresh installs and leave every store that already
exists on `NOT IN ('calls','imports')` **permanently** — two populations that silently
disagree about which facts are searchable, which is the precise failure mode §2.1 rejects a
shared list to avoid. Shipping that would be a worse instance of the bug being fixed.

There is no narrower field for this and there should not be one. This repo's own rule from
D59 is that readiness is *a stamped version, never a probe*; `SchemaVersion` is that stamp
for schema shape, and a trigger definition is schema shape.

### 2.4.2 The migration body is one line, and it does both halves at once

**`RebuildFactFts` is not a content-only rebuild.** Read it (`EngramDatabase.cs:518-576`): a
single `Execute` that drops all four triggers, drops the `fact_fts` table, recreates the
table, recreates all four triggers, and backfills from `fact`. It is the one implementation
of *what belongs in the index*, structure included.

So migration 15 is v13's exact shape (`EngramDatabase.cs:468-489`), which is the precedent —
v13 changed index DDL and called `RebuildFactFts` for exactly this reason:

```csharp
if (from < 15)
{
    // The FTS eligibility rule moved from a predicate list to the structural
    // (regenerable, object_id) condition, so the triggers in an existing store's
    // sqlite_master are stale and nothing else replaces them.
    RebuildFactFts(connection);

    WriteMeta(connection, null, "schema_version", "15");
}
```

**Three things fall out of this, and all three make the change smaller than it looked.**

1. **Trigger and content are fixed together, atomically, with no window.** It is one
   `Execute`, so no fact can be written between the trigger recreate and the backfill and be
   indexed under the old rule. A two-step design would have had that window; this one cannot.
2. **§3.2 is superseded as a delivery mechanism** — see the amendment there. Every store
   fixes itself on first open under the new binary. `repair` keeps its unchanged role as the
   general detector for a store that desynced some other way.
3. **The interpolation disappears.** `RebuildFactFts` currently interpolates
   `CodePredicates.EdgeBearingSqlList` into five places; §2.3 deletes that member, and the
   new condition has no list to interpolate. The C# copy becomes a plain string. **The two
   copies get easier to keep byte-identical, not harder** — which is worth saying explicitly
   because the brief reasonably assumed the opposite.

### 2.4.3 D31 applies — it snapshots, and do not argue for an exemption

Yes, this is a migration in this repo's terms, so D31's unconditional `VACUUM INTO` runs
before it. That is correct and should not be litigated per-migration: D31 is unconditional
*precisely* so nobody has to make this judgment call each time, and the judgment call is the
part that goes wrong. One snapshot per store, once, on first open after upgrade. Expected
cost, not a surprise, and not worth a special case for a four-trigger change.

Note what this does **not** contradict: §4's "no snapshot before the repair" was about the
token and vector work, which is derived-state regeneration outside any migration. Both
statements stand; §4 is amended to say which is which.

### 2.4.4 What moves in lockstep — the full list

| item | file | why |
|---|---|---|
| four trigger `WHEN` clauses | `docs/engram-schema.sql:304,310,322,332` | fresh stores |
| four trigger `WHEN` clauses + backfill `SELECT` | `EngramDatabase.cs` `RebuildFactFts` | existing stores, via migration and repair |
| `SchemaVersion = 14` → `15` | `EngramDatabase.cs:23` | the stamp itself |
| new `if (from < 15)` block | `EngramDatabase.Migrate` | the migration |
| **seeded `schema_version` value in the schema file** | `docs/engram-schema.sql` | **verify and update.** A fresh store writes its version from the schema file. If that value still reads `14`, every freshly created store immediately believes it needs migrating. Harmless in effect — v15 is idempotent — but it makes `VerifySchemaVersion` and the fresh/migrated guard test mean something different from what they say. |
| `AMigratedStore_HasTheSameLexicalIndexAsAFreshOne` | test tree | see §2.4.5 |

### 2.4.5 Two things to verify, not assume

**(a) The guard test may be passing vacuously — check before trusting it.** The brief treats
`AMigratedStore_HasTheSameLexicalIndexAsAFreshOne` as the thing that forces the two copies to
change together. That is exactly the assumption D60 punished: `WriteVersion1Store` rolls a
*current*-schema store back, so `CREATE INDEX IF NOT EXISTS` no-opped and a deliberately
wrong migration left **18 of 18 green** until the test dropped the index first.

The migration itself is safe from that trap — `DROP TRIGGER IF EXISTS` followed by `CREATE`
produces the new text from any starting state, so it cannot silently no-op. The **test** is
not safe from it: if its "migrated" fixture starts from a current-schema store, both sides
carry the new trigger text and the comparison proves nothing.

> **Falsify it:** change `docs/engram-schema.sql` only, leave `RebuildFactFts` on the old
> condition, run that test. If it stays green it is not holding the pair together, and the
> Implementor should say so rather than fix the test as a side quest. Per D60, confirm the
> break actually landed (`git diff --quiet`) before reading the result.

**(b) Is `fact.regenerable` declared `NOT NULL`?** One look at the schema file. This does not
change the required spelling — §2.2.1 mandates `IS` either way — but a nullable column means
the `=` spelling would have been a live silent bug rather than a latent one, and that is
worth knowing for the commit message.

### 2.4.6 The authorization is not mine to give

The Implementor's standing constraint is *never edit `docs/engram-schema.sql`*. **This ruling
establishes that the edit is technically required and how to do it safely. It does not
constitute permission to make it.** If that constraint came from Jim, only Jim can lift it,
and an Architect's finding that the change is necessary is not a substitute — a rule
originating with the user is not overridden by a peer agent concluding it is inconvenient.

Route the authorization question to Jim as a yes/no, separately from the technical ruling. It
is a small ask with a clear answer: *this change requires editing the trigger DDL in
`docs/engram-schema.sql`; the constraint says don't — may it be lifted for this change?*

---

## 3. Repair — what is already wrong, and D8's framing is correct

The brief's framing is right and I confirm it: `fact_fts`, `fact_token` and the vec0 table
are **derived state**, so this is `repair`'s job and no fact row is touched. D8 forbids
`repair` from creating, altering or deleting a fact body, predicate, validity window or
supersession row, and nothing here does.

### 3.1 `fact_token` — bump the index version stamp, and reuse what exists

`repair --apply --tokens` runs from the session-start detached child on **every** session,
and per `CLAUDE.md` it *checks the stamped tokenizer version and nothing else* — `CountMissing`
and `CountExtra` belong to the full `repair` verb because they scan the whole token table.

So the rule change is invisible to session-start repair unless the stamp moves.

> **Bump the stamped version.** It is not a tokenizer change, but the stamp's job is "the
> index disagrees with what it should contain", and it now does. Bumping forces exactly one
> from-scratch rebuild per store, in the **detached** maintenance child, so it costs no hook
> latency. `CLAUDE.md` measures that rebuild at 297 ms @5,097 and 4,161 ms @50,097.
> **Do not add a scan to session start** — the stamp is the whole mechanism and the
> measured reason it is a stamp rather than a probe.

This is a **separate stamp from §2.4's `SchemaVersion`**, deliberately. The token index is not
schema; its readiness is its own version and it heals in the detached child rather than on
open. Two stamps, two mechanisms, neither substituting for the other.

The existing guard — a from-scratch recomputation diffed against the incrementally
maintained table — already covers the new rule with no change, because it recomputes
through the same condition.

### 3.2 `fact_fts` — **amended: the migration delivers this; repair stays the detector**

**Superseded in part by §2.4.** This section originally routed the FTS fix to the full
`repair` verb on the grounds that no version stamp exists for FTS. That was wrong in one
respect: `SchemaVersion` *is* a stamp that reaches this index, because the triggers are
schema, and migration 15 calls `RebuildFactFts` — which fixes trigger and content in one
statement. **Every store therefore heals on first open under the new binary.** Nobody has to
run `repair` by hand, and no store sits indefinitely with a corrected trigger and a stale
index.

What still stands, unchanged, for `repair`'s own detector — both traps are fatal to the
obvious implementation:

1. **The obvious detector cannot see the break.** On an external-content table every
   non-`MATCH` query — `SELECT rowid FROM fact_fts` included — is answered from the
   *content* table, so an index-vs-fact set difference compares `fact` against itself and
   calls any desync healthy. **Read the real index through `fts5vocab`.**
2. **Do not use FTS5's own `'rebuild'`.** It re-reads the whole content table, closed
   beliefs included, and the index deliberately holds live facts only. Rebuild through
   `EngramDatabase.RebuildFactFts`, which is the one implementation of what belongs in the
   index — and which §2.3 updates.

### 3.3 The vector table — targeted delete, and it must not race the server

Vectors already exist for every edge fact, **including `calls` and `imports` from before
graph-enhance** (§0). Removing them is a delete from the vec0 table: derived state,
permitted by D8, no snapshot needed (rebuilding derived state is not a migration).

Two constraints, both from D38:

- **This is not `embed --rebuild`.** A full rebuild would re-embed the entire eligible
  corpus at ~28 s per committed batch — the cost we are trying to avoid. What is wanted is
  a **targeted delete of vectors whose fact is now ineligible**, leaving every eligible
  vector in place.
- **It must refuse while a server is up**, for D38's reason unchanged: `EmbeddingBacklog` is
  the one owner of vector production, and a running server holds an embedder and a live
  backlog that would re-add rows mid-delete. Ask **`ServerIsAlive`**, never `Kind is
  Running` — `Wedged` and `VersionMismatch` are live processes too.
- Use `Clear`-shaped semantics, never `Drop`: the table's space pin must survive, because
  nothing about the embedding space changed.

**Dry-run first**, per this repo's rule for anything destructive: print the count it would
delete, grouped by predicate, and require an explicit flag to act. The per-predicate
grouping is not cosmetic — it is what shows the reader that most of the deletion is
pre-existing `calls`/`imports`, not this branch's four.

---

## 4. What I am NOT proposing

- **No change to `fact` rows.** Append-only holds; the facts are correct beliefs and stay.
- **No re-index.** Nothing about *what is observed* or *how subjects are addressed*
  changes, so neither `AnalyzerVersion` nor `GrammarVersion` moves
  (`close-graph-query-gap.md` §10.1's doubt test: no existing fact's `path` would be spelled
  differently). §2.4.1 expands on why moving either would be actively wrong rather than
  merely unnecessary.
- **No reopening of whether edge facts belong in FTS.** The schema comment already ruled it
  and D44 supplies the reasoning; §1 only strengthens it.
- **No snapshot before the *token and vector* repair.** D31 requires one before a
  *migration*. §3.1 and §3.3 regenerate derived state outside any migration, and D8 says
  that can destroy nothing authored. **Amended:** §2.4's schema bump *is* a migration and
  *does* snapshot — the two statements are about different halves of this change, and the
  distinction is the boundary D31 draws.
- **No new column and no `ALTER TABLE`.** §2.2's condition reads two columns that already
  exist. The migration changes trigger definitions only.

---

## 5. Severity — one correction to the dispatch's read

The brief's "not urgent, no data-loss, real cost" is right. One amendment:

**The `fact_token` and `fact_fts` half is self-correcting once §2 lands; the embedding half
is not.** With §2.4, FTS heals on first open and the token index on the next session's
detached child. Vectors are never removed by any existing path — nothing prunes them — so
every day the current rule stands, the vec0 table accumulates rows that no future fix
removes automatically. That is not urgency in the incident sense, but it is the one
component where waiting has a monotone cost, and it argues for §3.3 shipping with §2 rather
than after it.

---

## 6. Decisions I did not make

- **Whether to delete pre-existing `calls`/`imports` vectors at all**, as opposed to only the
  four new predicates'. I recommend deleting all of them — the rule in §2.2 is not
  retroactive-versus-prospective, it is simply the rule, and leaving known-ineligible rows
  because they are old is how a store ends up with two populations nobody can reason about.
  But it is a larger delete than the regression strictly caused, so it is Jim's call whether
  that rides this change or its own.
- **Whether the vector prune is a `repair` sub-verb or an `embed` sub-verb.** Both are
  defensible: `repair` owns derived-state healing, `embed` owns the vector table and already
  holds D38's server-liveness refusal. Implementor's call; state which and be consistent.
- **Whether the Implementor's constraint against editing `docs/engram-schema.sql` may be
  lifted for this change.** §2.4.6 — not an Architect's call to make.

---

## 7. NEEDS-EVIDENCE

**NE-1 — size it, and group by predicate.** On this instance (`ENGRAM_HOME` set) and on the
50k fixture:
`SELECT predicate, COUNT(*) FROM fact WHERE valid_to IS NULL AND regenerable = 1 AND object_id IS NOT NULL GROUP BY predicate;`
then the count of those ids present in `fact_token`, and in the vec0 table.
*Decides:* the real magnitude, and — because it is grouped — **how much of it predates
graph-enhance**. An ungrouped number would be misattributed to this branch (§0 consequence 2).
This subsumes the audit's NE-A and is the one item to run first.

**NE-2 — is the D44 noise actually reproducible?** Recall a code-flavoured term (a common
member name) against an indexed store and check whether `coverage` reads `high` off
corroboration between near-identical edge bodies.
*Decides:* whether the FTS/token half is a measured defect or a predicted one. The schema
comment predicts it; nobody has reproduced it. **If it does not reproduce, say so** — §2 is
still right on structural grounds, but the severity claim would need retracting.

**NE-3 — does any authored fact carry an object today?**
`SELECT COUNT(*) FROM fact WHERE regenerable = 0 AND object_id IS NOT NULL;`
*Decides:* nothing about the design — §2.2's `regenerable` clause is a fail-safe that should
be there whether or not the population is empty today. It is worth knowing because a
non-zero answer means the clause is already doing work, and a zero answer means the mirror
test in §2.3 must construct the case synthetically rather than assuming one exists.

**NE-4 — the token rebuild cost on this instance.** `CLAUDE.md`'s 297 ms / 4,161 ms figures
are from D59's measurement, taken **without** these predicates present. Confirm the rebuild
after §3.1's stamp bump on a store that actually holds them.
*Decides:* whether one forced rebuild in the detached child is as cheap as assumed.

**NE-5 — does `AMigratedStore_HasTheSameLexicalIndexAsAFreshOne` actually hold the two copies
together?** §2.4.5(a): edit the schema file only, leave `RebuildFactFts` stale, confirm the
edit landed with `git diff --quiet`, run the test.
*Decides:* whether the guard the brief is relying on exists. **If it stays green, report it
and stop** — do not repair the test inside this change; it is a separate finding with its own
reasoning, and folding it in would hide that the pair was unguarded.

**NE-6 — migration 15 on a real pre-15 store.** Open a store created before the change with
the new binary; confirm `sqlite_master`'s four trigger texts now carry the structural
condition, the D31 snapshot was taken, and `schema_version` reads 15. Then re-open and
confirm nothing runs a second time.
*Decides:* that the migration is both effective and idempotent — the two properties a
one-line migration is most likely to get wrong in opposite directions.
