# `callees` fan-out — batch the per-row resolution

Design only. Nothing here was executed. Code read at the current tip and cited; the
measured numbers are the Implementor's (`docs/specs/navigate-latency-results.md`, addendum
"H2, the `callees` many-callees case").

Third spec of three from the same audit pass, **deliberately independent** of
`edge-fact-lane-eligibility.md` and `backup-fingerprint-semantics.md`. Different root
cause, different fix, no shared code.

---

## 0. The finding is correct, my audit was wrong, and the shape of the error is reusable

My index audit (`graph-index-audit.md` §1.4) filed `callees` as **SERVED** and used it as the
*control arm* in NE-1. Ultra-Advisor disputed it and was right.

**What I did wrong, precisely:** I audited each relation's **edge query** against the index
list, and treated `SymbolResolver.Resolve` as a shared prefix cost already priced under
`defined_at`. `Callees` calls it **once per returned callee row** (`CodeCallGraph.cs:132`).
A per-query audit cannot see a per-row loop — the cost lived in the gap between my two units
of analysis.

> **Audit by query and you will miss every loop that spans queries. A relation's cost is
> `calls-per-invocation × cost-per-call`, and an index audit only ever reads the second
> factor.**

Worse, I had both halves and never put them together: §9.5 of `close-graph-query-gap.md` is
**my own** item saying H2 was never exercised because the fixture's `Fn_0` had one callee,
and it says in terms *"H2 is not moot."* I wrote that, then filed `callees` as SERVED in a
different document a few hours later. Two correct statements, never held side by side.
`graph-index-audit.md` §1.4 is corrected by this spec.

The mechanism is the one §0 of the gap spec already names: the per-row `Resolve` exists to
answer *where is this callee declared* — **the return leg that is not stored**. Same
structural fact as the `fact.object_id` gap, surfacing in the other direction.

---

## 1. What the loop actually does

`CodeCallGraph.Callees` (`CodeCallGraph.cs:116-150`), reduced to its cost structure:

```csharp
var declarations = SymbolResolver.Resolve(connection, query, 1000, repoNeedle);   // once
var calls = LiveCallsFromSubjects(connection, ..., repoNeedle);                   // once, ix_fact_path — fine
foreach (var (callerPath, callee, analyzerTier) in calls)          // N callee rows
{
    var leaf = CodePaths.LeafOf(callee);
    var candidates = SymbolResolver.Resolve(
        connection, leaf, 1000, repoNeedle, SymbolMatchTier.CaseInsensitive);     // N resolves
    foreach (var candidate in candidates)                          // M candidates each
    {
        var signal = RankFrom(connection, callerPath, ..., repoNeedle);           // N × M DB calls
    }
}
```

Three facts from this that the measurement does not show:

1. **The loop's `Resolve` is capped at `SymbolMatchTier.CaseInsensitive`.** Tier 3 — the
   leading-wildcard `LIKE` that no B-tree can serve — **is never reached here**. So the cost
   is at most two unindexed scans of `entity` per callee, not three. Good news for the fix:
   the batched version only has to reproduce two tiers.
2. **`limit` is `1000`, per callee.** So `M` is bounded at 1000, not at 1.
3. **`RankFrom` takes the connection and runs inside the inner loop**, so the true call count
   is `N × M` database round-trips *in addition to* the `N` resolves. §3 treats this
   separately because it is unquantified.

---

## 2. Why the measured 650 ms is the *best* case, not the worst

The addendum's `FanOutFn` calls **200 distinct, genuinely declared `FanTarget_N` functions**,
and the write-up notes every one resolves on the first (exact) tier. That means, in that arm:

- every `Resolve` short-circuits at tier 1 — the cheapest path,
- and `M ≈ 1`, so `RankFrom` ran ~200 times, not 200 × M.

**A real dispatch function does not look like that.** `Callees` resolves by **leaf**
(`CodePaths.LeafOf`), and `close-graph-query-gap.md` §0 consequence 2 is explicit that leaf
identity is *weakest at exactly the names an agent asks about* — `Get`, `Add`, `Run`,
`Dispose`, `ToString`. A `main` or an orchestrator calling twenty common-leaf methods gets a
large `M` on each, and `N × M` grows multiplicatively while the fixture held `M` at 1.

> **650 ms at 50k is the floor of this defect, measured under the conditions most favourable
> to it. Nothing has yet measured the shape that real code actually produces.** That is
> NE-2, and it should be run *before* anyone concludes the fix in §3 is sufficient.

This does not weaken the verdict — it strengthens it, and it changes what "fixed" has to
mean.

---

## 3. The fix

Three layers, in ladder order. **Layer 2 is the one that addresses the measurement**; layer
1 is nearly free and layer 3 is scoped but not specified, because I have not read the code
it touches.

### 3.1 Layer 1 — memoize by leaf within the call *(3 lines, do it regardless)*

Resolve distinct **leaves**, not rows. A `Dictionary<string, IReadOnlyList<SymbolMatch>>`
scoped to the one `Callees` invocation.

`ux_fact_edge_live` already collapses repeats of one `(subject, predicate, object)`, so this
gains nothing when the caller is a single declaration — but the outer `Resolve` can return
**many** declarations (limit 1000), and `LiveCallsFromSubjects` unions calls across all of
them, so the same leaf recurs across callers routinely. Free, exact, no SQL change.

**It will not show up in the addendum's arm** (200 distinct names, zero repeats). Do not read
a flat before/after there as evidence it does nothing.

### 3.2 Layer 2 — batch the resolution *(the fix)*

Replace `N` calls to `Resolve` with **two** queries over the distinct leaf set, one per tier,
preserving semantics exactly:

1. **Tier 1 batched.** `WHERE e.kind = 'symbol' AND e.name IN (…)` over all distinct leaves.
2. **Partition.** Leaves that returned zero rows are the tier-2 input; leaves that returned
   rows are done and tagged `Exact`.
3. **Tier 2 batched** on the residue only: `AND e.name IN (…) COLLATE NOCASE`. Tag
   `CaseInsensitive`.
4. Leaves still empty resolve to zero candidates → the existing
   `CalleeMatch(null, callee, CallRankSignal.NameOnly, …)` path, unchanged.

**This reproduces `Resolve`'s per-name tier semantics exactly**, because the partition after
each tier is precisely what "stop at the first tier that returns rows" means, applied to a
set instead of one name. `N` scans become 2.

Expected shape: ~2 unindexed `entity` scans regardless of fan-out. At the addendum's
~3.25 ms per scan @50k that is single-digit milliseconds against 650 — but **that is an
extrapolation from their number, not a measurement, and NE-1 is what confirms it.**

#### Four traps, each of which silently changes behaviour

- **`LIMIT` is per name and a batch makes it global.** `Resolve` applies
  `ORDER BY e.path LIMIT $limit` to one name; a single batched statement would apply one
  limit across all of them, silently truncating some names to nothing while another consumes
  the whole budget. **Drop the SQL `LIMIT` from the batched form and apply the per-name cap
  in C# after grouping.** This is the trap most likely to pass every existing test.
- **Ordering.** `ORDER BY e.path` per name becomes `ORDER BY e.name, e.path` in the batch,
  then group by name. Ranking downstream depends on candidate order.
- **The 32,766 SQL-variable ceiling.** `close-graph-query-gap.md` §4 already flagged this and
  §9's `explain` crash is the precedent — bound one parameter per name and a large fan-out
  dies with `SQLite Error 1: 'too many SQL variables'`. **Chunk by construction, not by
  assuming fan-out stays small** — 500 per chunk, matching `RetrievalExplainer`'s existing
  precedent. A bound that depends on nobody writing a 40,000-call function is not a bound.
- **`repoNeedle`.** The `AND e.path LIKE '%' || $repo || '%'` clause is per-statement and
  carries over unchanged; do not drop it in the rewrite.

#### Falsification requirement

A test that only checks *counts* cannot catch the `LIMIT` trap or the ordering trap — both
return the right number of rows. Assert the **exact resolved set and its order** for a fixture
with (a) a leaf exceeding the per-name cap, (b) two leaves where one hits tier 1 and the other
only tier 2, and (c) a fan-out above the chunk size. Then break each of the four traps in turn
and confirm a test reddens for each. Per this repo's rule, a guard that cannot fail is
worthless — and note the sibling failure it documents: **verify the break actually landed**
(`git diff --quiet`) rather than trusting a falsification arm that may have silently no-oped.

### 3.3 Layer 3 — `RankFrom` in the inner loop *(scoped, NOT specified)*

`RankFrom(connection, callerPath, …)` runs once per *candidate*, so `N × M` round-trips. In
the measured arm `M ≈ 1`, which is why it does not show up — and §2 argues `M ≈ 1` is exactly
what real code will not give.

**I have not read `RankFrom`, and I am not speccing a fix for code I have not read.** What I
can say from the call shape:

- It takes a connection, so it queries; `CallRankSignal`'s components (`SameFile`,
  `QualifierAgreement`, `ImportFilenameMatch`, `SameRepo`, `NameOnly`) suggest at least one
  of them reads `imports` facts, and `IsTypeDeclaration` (`CodeCallGraph.cs:339`, `LIMIT 1`)
  is a nearby per-call query.
- Several of its inputs are **loop-invariant per caller** (`callerPath`, `repoNeedle`), so
  hoisting or batching per caller is likely available.

> **Scoping item for the Implementor, not a design call:** read `RankFrom`; report how many
> statements it issues per invocation, which of its inputs are invariant across the inner
> loop, and whether its per-candidate work can be batched the way §3.2 batches resolution. If
> it issues one cheap indexed lookup, leave it. If it issues several, it is the next fix and
> it is larger than this one.

Ship §3.1 and §3.2 without waiting on this.

---

## 4. What I am NOT proposing, and why

**No `name → declaration` derived index.** It is the other obvious direction and I already
did its invariant analysis in `close-graph-query-gap.md` §1 ("If multi-hop is ever
revisited"): such an index is *derived state* — regenerable, `repair`-rebuildable, and
self-healing across renames because `entity` rows for a renamed file are rewritten while
file B's facts are not — so it is **permitted by D8 and does not breach D72**. The
invariants are not the obstacle.

The obstacle is that it is a **second thing that can silently disagree with its source**,
which is the `fact_token` failure mode `CLAUDE.md` documents at length, and it brings a
schema change, a backfill, a repair detector and a rebuild path. I declined exactly that
trade for 11.85 ms in §9.3, and §3.2 is expected to close most of a 650 ms gap for a change
with none of those costs.

**Rung 1 of the ladder: if batching lands the relation under budget, the index never needs to
exist.** Measure (NE-1) before reaching for it — and if NE-2's realistic-shape numbers are
still over budget after batching, the index becomes the live candidate again, at which point
it should be scoped against `callers`' return leg too, since one index would serve both.

---

## 5. Interaction with the open `fact.object_id` index question

**Independent. Neither changes what the other must cover.**

- `ix_fact_object(object_id, predicate) WHERE valid_to IS NULL`
  (`graph-index-audit.md` §2.1) serves the **reverse** lookup on `fact` — given an object
  entity, find the facts naming it. That is `callers` and `implementers`.
- This fix touches a loop that scans **`entity`** by name, in the **forward** direction.
  `Callees`' own fact lookup already uses `ix_fact_path` and is untouched.

They do not overlap, and neither is a prerequisite for the other. **Ship order is free.**

**One real interaction, in the deferred branch only:** if §4's `name → declaration` index is
ever built, it would also serve `callers`' return leg, which would change what
`ix_fact_object` is needed for. So *this* spec's recommended fix has no interaction; its
rejected alternative does. Worth stating because the two would otherwise be scoped by
different people from different documents.

**One thing to check rather than assume** (NE-3): whether `RankFrom` reads `fact` at all. If
it does a reverse-edge read, §3.3 *would* interact with `ix_fact_object`. I could not rule
that out without reading it.

---

## 6. NEEDS-EVIDENCE

Protocol unchanged — published binary, alternate arms, self-vs-self calibration, subtract the
`probe` floor, time through a file, `ENGRAM_HOME` on every invocation.

**NE-1 — the same high-fanout arm, after §3.2.** Re-run the addendum's `FanOutFn` arm at 5k
and 50k with batching in place.
*Decides:* whether batching is sufficient. Target: the high-fanout arm should collapse toward
the low-fanout arm. **Reuse the addendum's exact fixture and method** so the numbers are
comparable — a new fixture makes the before/after uncomparable and this is precisely the
before/after that matters.

**NE-2 — the realistic shape, which nobody has measured.** A fan-out arm where the callees
have **common leaves** (`Get`, `Add`, `Run`, `Dispose`) that each resolve to many
declarations, so `M` is large rather than 1. Run it **both before and after** §3.2.
*Decides:* whether §3.3 (`RankFrom`, the `N × M` term) is the real remaining cost. §2 argues
the existing 650 ms is the best case; this is the measurement that would show it. **If this
is skipped, the fix will be declared complete on the arm least able to disprove it.**

**NE-3 — does `RankFrom` read `fact`?** Read it and report statement count per invocation
plus which inputs are loop-invariant.
*Decides:* §3.3's scope, and whether §5's "independent" verdict has an exception.

**NE-4 — confirm the tier ceiling holds.** Assert in the batched implementation that tier 3
is never issued from this path, matching `SymbolMatchTier.CaseInsensitive` at
`CodeCallGraph.cs:132`.
*Decides:* nothing about cost — it is a guard against the rewrite silently widening the
ceiling, which would reintroduce the unindexable leading-wildcard scan per chunk and would
not show up in a correctness test.

---

## 7. Correction to file

`docs/specs/graph-index-audit.md` §1.4 and its summary table say `callees` is **SERVED**.
That verdict covered the fact lookup only and is corrected here: the fact lookup is served by
`ix_fact_path`; the relation is not, because of the per-row loop. The audit's NE-1 also used
`callees` as its *control* arm — that control is still valid for the specific question it
asks (does `LiveCallsFromSubjects` read `SEARCH … USING INDEX ix_fact_path`), but it must not
be read as "callees is fine".
