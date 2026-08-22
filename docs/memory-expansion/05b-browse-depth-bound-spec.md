# 05b — Browse read cost: bound the materialization, not the read

Status: **closed. All three changes landed, all acceptance criteria met and measured.** Change 1's
node-map bound is confirmed. D-7 removed the residual per-row allocation and hit its predicted
figure within 1%. D-5's retained-memory condition is cleared by a 300-call plateau run. D-2
remains rejected and closed; neither of its keys turned. Nothing in this document is pending.

Follows `docs/memory-expansion/05-browse-tui-spec.md` and
`docs/memory-expansion/05a-browse-root-fix-spec.md`. Fixture generator and measurement protocol
live in `docs/memory-expansion/05b-fixture-spec.md`.

**One item is deliberately left open and it is not in this document's gift:** the settled findings
have no home outside this spec chain. See *Carrying this forward*.

## Amendment log

**A1 — bar ruling folded in.** The bar referred up in the first version was ruled at the
Ultra-Advisor tier and Jim was consulted; no objection to the substance, with the right reserved
to dispute the 200 ms figure.

**A2 — ceiling split by reachability; D-2 closed.** The first acceptance run came back at 317 ms
and the Ultra-Advisor adjudicated the apparent miss: the 200 ms ceiling had been paired with the
wrong arm, and the fixture was a pathological shape rather than the bar's referee. **D-6** named
the lever that would be reached for instead of D-2.

**A3 — the outstanding arm unblocked.** Fixture specified from measured real shape and the path
grammar. R1 retired to historical (shape never recorded, and not needed). R2's provenance
questioned, because the live MCP server was answering root browse with 05a's pre-fix bug.

**A4 — measured and diagnosed.** A two-parameter model — a ~2.1 MB fixed intercept plus ~889
bytes per entity row scanned — predicts all three fixtures within 3%; the driving variable is
entities scanned rather than facts or nodes; reading the implementation accounts for ~856 of
those 889 bytes as per-row substring allocation. The growth is transient churn, not retained
state, and Change 1's node-map bound is confirmed at **under 1%** of total growth. **D-7** removes
the churn.

**A5 — D-5's memory half restated, and the fork resolved as neither branch.** The escalation
asked whether D-1 or the constraint had to give. **Ruled: neither — the constraint was
misstated.** The binding quantity became *retained* growth; transient bytes per entity became a
tracked budget adjudicated under the existing time ceilings. Endorsed in full at high confidence,
conditional on a repeated-call plateau run. Added a sensitivity-floor requirement to that run,
because the discriminator as stated could pass for the wrong reason.

**A6 — both outstanding runs closed; the document is complete.** D-7 landed and measured **154 B
per entity against 156 predicted**, a 5.8× reduction. The plateau run, re-run at 300 calls after
its first 8-call attempt was correctly flagged ambiguous under A5's floor, separated warmup from
steady state by **571×** and settled at **3.2 KB per call** — well under the 19 KB floor. D-5's
condition is cleared and the ruling stands as endorsed. The settled resident footprint that run
revealed, **~228 MB**, is recorded as a plain number in its own right.

---

## The correction: 05a's depth bound is impossible

05a's decision rule said that if root browse exceeded the bar, the remedy was to "add a depth
bound to the query, which is correct for **all** prefixes rather than root alone". **That
remedy cannot be built.** I specified it without working through the aggregation semantics, and
the Implementor was right to refuse to invent the SQL shape.

`FactsUnder` on every node is the sum of live fact counts over its **entire** subtree, at every
depth, regardless of the `depth` argument. The `depth` parameter controls how many levels of
`BrowseNode` get **materialized** — never how much data is **needed**.

So a query bounded to depth-1 entities would return `/people` and `/code` and nothing beneath
them. Both carry zero facts of their own; the facts live at `/people/jim/preferences`,
`/people/ada` and `/code/Auth.cs#ValidateToken`. `FactsUnder` at root would come back **0**
instead of 3. **An existing, currently-green test fails it:**
`MemoryBrowserRootTests.Browse_AtRoot_FactsUnderMatchesAnIndependentCount` asserts
`node.FactsUnder == SELECT count(*) FROM fact WHERE valid_to IS NULL` at `depth: 1`. The guard
written for 05a falsifies 05a's own remedy, which is the outcome that discipline is for.

**The correct statement of the rule.** A depth bound belongs on what is *materialized*, not on
what is *read*. Before Change 1 it was on neither: every row was read **and** every row was
re-scanned once per emitted node.

---

## Design

Three changes, all landed. None alters a displayed value.

### Change 1 — accumulate in one pass; never materialize `counts` — LANDED

Replace "read every row into a dictionary, then fold by re-scanning it per node" with "walk each
row's segments once, accumulating into the nodes it belongs to". Read rows straight from the
`SqliteDataReader` without retaining them.

For prefix `P` (length `L`, empty at root), requested `depth` `D`, and each row `(X, n)`:

- if `X == P`, `n` is the prefix node's own `FactsHere`;
- otherwise walk `X` from offset `L`, taking up to `D` successive segments. At each level:
  - the separator is `X[pos]`, the segment runs to the next `/` or `#` or to the end of `X`;
  - `nodePath` is `X` truncated to the end of that segment;
  - the display name is `"#" + segment` across a `#` boundary and `segment` across `/`;
  - add `n` to that node's running **total**, and register it as a child of the node one level up;
  - if the segment ended at the end of `X`, add `n` to that node's **here** as well, and stop
    walking this row.

Complexity goes from **O(entities × emitted nodes)** to **O(entities × depth)**, and *retained*
memory from **O(entities)** to **O(nodes within depth)**.

**Confirmed by measurement, twice.** The DEEP-vs-BROAD control isolates the retained node-map
term directly: pre-D-7, 144 extra nodes and 1,080 extra entity rows cost 704 KB out of a 27–28 MB
delta — **under 1%**. Post-D-7 that gap shrank to ~1.7% of a much smaller total, which is the
second confirmation: had the gap been the 144 *nodes*, it would have held its absolute size while
everything around it fell. It tracked the per-row term instead. The bound works as specified.

**The word "extracting" in the algorithm above was a specification defect, and it is mine.** It
read as an instruction to materialize substrings, which is what the implementation did, and that
was the entire residual memory term. See D-7. The algorithm is unchanged; only how its segments
are represented changed. The wording above is corrected to "taking".

### Change 2 — `FactStore.ReadAt`, replacing subtree-then-filter in `TopFacts` — LANDED

`MemoryBrowser.TopFacts` read an entire subtree and then filtered to a single path:

```csharp
var facts = FactStore.ReadSubtree(connection, path)
    .Where(f => f.SubjectPath == path.TrimEnd('/') && f.ValidTo is null)
    .ToList();
```

At root, `ReadSubtree`'s range is `['/', '0')`, matching **every live fact in the store**; all
materialized as `StoredFact`, then discarded by a filter comparing against `""`. `ReadAt` selects
`WHERE f.valid_to IS NULL AND e.path = $exact` directly. `entity.path` is `UNIQUE`, so it is an
index seek rather than a range scan, and since every row shares one path the `ORDER BY e.path,
f.id` degenerates to `f.id` — the same order the filtered list had.

**A quirk that is preserved, not fixed.** `path.TrimEnd('/')` maps `"/"` to `""`, so an entity
literally addressed as `/` would be found by neither the old code nor the new. Nothing creates
such an entity. Silently changing it while "optimizing" is the failure to avoid.

### Change 3 (D-7) — the segment walk allocates no strings per row — LANDED

See D-7 for the full rationale. The shipped shape, in `MemoryBrowser.Browse`:

- `afterSeparator` and `segment` are `ReadOnlySpan<char>` slices of `rowPath`. No copy.
- `display` is a span too, and the `#` case is one character further left in the *same* span
  rather than a concatenation — the separator already sits immediately before the segment in
  `rowPath`.
- `childPath` is `rowPath[..childEnd]`, materialized **only when the node is first seen**, via
  `siblings.GetAlternateLookup<ReadOnlySpan<char>>()`. Every later row reuses the stored instance.
- `here`/`under` go through `CollectionsMarshal.GetValueRefOrAddDefault` — one hash lookup where
  the previous shape did two.

---

## What must not change

- Every displayed value: `FactsHere`, `FactsUnder`, `ChildrenOmitted`, names, paths, ordering.
- `engram_browse`'s MCP surface, parameters and output shape.
- The `Browse` SQL text, and the `prefix` derivation from 05a.
- `FactStore.ReadSubtree`, which has other callers and no root bug.
- Any `fact` row. This is a read path; D8 is untouched.

**The regression suite must pass unmodified:** `MemoryBrowserRootTests`, `DirectiveBrowseTests`,
`BrowseTuiTests`, `BrowseCommandTests`, `BrowsePtyTests`. Needing to edit any of them is the
signal that a displayed value moved — stop and report rather than adjusting the test. **Held
through all three changes:** the final suite is 865 passed, 0 failed, with no test edited to
accommodate a change.

---

## Tests by tier (D9)

**Tier 2 — the equivalence guard, and it is the whole correctness story.**
`tests/Engram.Integration.Tests/MemoryBrowserEquivalenceTests.cs`. The pre-Change-1
implementation is ported into the **test assembly** as a reference — not into `src/`, so shipped
code keeps one implementation — and the new `Browse` must produce an identical `BrowseNode` tree,
field by field including child order, over a seeded corpus: prefixes `"/"`, mid-tree, leaf, and
non-matching; depths 1–3 at each; **more than `MaxChildrenShown` children at some level**, since
below 16 the `Take(15)` and `ChildrenOmitted` paths are dead code; at least one `#` boundary and
one path deeper than the requested depth.

**This same guard is what made D-7 safe**, and it was re-run against it unchanged. It did its job:
arm 5 broke 16 tests.

**Tier 2 — `ReadAt`.** Equal to the old subtree-then-filter expression, same order, for a path
with several facts, a path with none, a path whose subtree is much larger than itself, and `"/"`.

**Tier 3 — existing, unmodified.** `BrowseCommandTests` and `BrowsePtyTests` stay green.

**No performance test was added at any tier**, and D-5's restatement does not change that: the
retained bound is a recorded measurement like everything else here, and the transient budget is
explicitly never a CI assertion.

---

## Falsification

**Correctness — deterministic, no clock. These arms cover all three changes.**

| Arm | Break | Expected failure | Result |
| --- | --- | --- | --- |
| 1 | accumulate only at level 1 (skip deeper levels of the walk) | equivalence fails on `FactsUnder` at depth 2–3 | as expected |
| 2 | add `n` to `total` but never to `here` | equivalence fails on `FactsHere` | as expected |
| 3 | drop the `ThenBy(displayName, Ordinal)` tiebreak | equivalence fails on child order in the >15-children fixture | as expected |
| 4 | `TopFacts` keeps `ReadSubtree` but drops the `.Where` | `ReadAt` equivalence fails | as expected |
| 5 | *(D-7)* slice `childPath` one character short or long | equivalence fails on paths and on the `#` display form | **16 failed / 849 passed**, restored to 865/865 |

Arm 3 remains the one most likely to be skipped and the one that catches silent ordering drift.

**Performance — a recorded measurement, never a test.** An absolute wall-clock threshold is the
shape this repository has ruled against: `FileTouchedBudgetTests` "guards the margin, not the
absolute number, so it fails when the rule breaks rather than when the machine is busy."
Equivalence tests also cannot falsify a performance change — revert Change 1 or D-7 and the
equivalence test still passes, because the reference implementation is what it compares against.
If a margin test is ever written anyway, it must guard a **ratio** and **assert its own premise is
still live**; `ExplainCandidateScalingTests` was deleted in D60 because `seed_k` capping collapsed
both its arms onto a shared floor, and it survived only because it carried an explicit premise
assertion that failed loudly.

---

## Measurements

**Protocol.** Published binary, never `dotnet run` or `dotnet test`. `ENGRAM_HOME` or `--home`
set explicitly on every invocation. Alternate the arms. Calibrate the harness floor first.
**Every datapoint carries four labels: corpus shape, route, server binary provenance, and
fact-body length** — each has already been the thing that made some number here unreadable.

### Recorded — pre-D-7

| # | Arm | Facts | Shape | Wall (net of floor) | RSS delta | Status |
| --- | --- | --- | --- | --- | --- | --- |
| R1 | root, depth 1, CLI | 5,045 / 50,045 | **unrecorded** | 0.02 s / 0.12–0.13 s | 40 MB / 92 MB | **retired to historical** |
| R2 | root, depth 3 | 50k | adversarial siblings | 317.2 ms | 34,608 KB | upper bound only |
| R2′ | root, depth 3 | 50k | adversarial siblings | 313.9 ms (floor 26.9) | 34,256 KB | confirms R2 within noise |
| **M1** | root, depth 3, MCP | 5,085 | **BASE-5K** | 23.77 ms | 4,784 KB | verified-fresh server, 120-char bodies |
| **M2** | root, depth 3, MCP | 50,445 | **DEEP-50K** | 235.11 ms | 27,296 KB | 17 nodes ≤ depth 3, same as M1 |
| **M3** | root, depth 3, MCP | 50,445 | **BROAD-50K** | 230.22 ms | 28,000 KB | ~161 nodes ≤ depth 3 |

**R1 must not be used as a baseline** — its corpus shape was never recorded. It is provenance for
how this investigation started and nothing else. R2/R2′ are an upper bound on a shape (50,000
siblings of one node) whose real-world counterpart has fan-out 8.

**Fixture fidelity confirms itself:** the generator predicts 5,040 / 50,400 / 50,400 facts and
the store reports 5,085 / 50,445 / 50,445 — the same +45 in each, which is `init`'s seeded facts.

### The model: entities scanned, not facts and not nodes

`Browse`'s query is `SELECT e.path, count(f.id) … LEFT JOIN fact … GROUP BY e.path`, so it
returns **one row per entity** under the prefix, including entities with zero live facts.

**One two-parameter model fit all three pre-D-7 fixtures within 3%:**

> **RSS delta ≈ 2.1 MB + 0.889 KB × (entities scanned)**

- DEEP: 2,117 + 0.889 × 28,920 = 27,827 KB vs **27,296 observed** (−1.9%)
- BROAD: 2,117 + 0.889 × 30,000 = 28,787 KB vs **28,000 observed** (−2.7%)

**This is why the per-fact framing misled.** In this fixture entities and facts are proportional
(8 entities per 14 facts), so an entity-proportional term *looks* fact-proportional. But per-fact
cannot explain M2 versus M3 at all — equal fact count, yet 704 KB apart. **Entities scanned is
the driving variable**, and it stayed the right unit after D-7: the post-D-7 slope converts
cleanly between the two (0.086 KB/fact × 1.75 facts per entity = 154 B/entity).

**The 5.71× RSS against 9.92× facts was not sub-linearity.** The 2.1 MB intercept is a fixed
cost, so ratios understate the slope at small N. The *marginal* figure is what D-5 asks for.

### Diagnosis: transient allocation in the segment walk, not retained state

**Predicted from reading the code, before comparing to the measurement.** For a representative
DEEP-50K path of 65 characters at `depth: 3`, the pre-D-7 walk allocated per level a `rest`
suffix copy, a `segment` copy, and a `childPath` concatenation — plus one `reader.GetString(0)`
per row:

| Allocation | Bytes |
| --- | --- |
| `reader.GetString(0)` (65 chars) | ~156 |
| level 1: `rest` (64) + `segment` (8) + `childPath` (9) | ~240 |
| level 2: `rest` (55) + `segment` (7) + `childPath` (17) | ~236 |
| level 3: `rest` (47) + `segment` (4) + `childPath` (22) | ~224 |
| **total per row** | **~856** |

**Observed marginal: 889 bytes per entity. Predicted from the source: 856. Agreement within
4%.** Roughly 28,920 rows × 889 bytes ≈ 25.7 MB, against an observed 27.3 MB delta — the residual
growth was, to within measurement error, **one call's worth of uncollected Gen0 garbage**.

Nothing is keyed by fact id and nothing is retained per fact: `here`, `under` and `childrenOf`
are all keyed by node path exactly as specified. What was unmodelled is that the walk *copied*
each row three times per level, and the specification's word "extracting" invited it.

### Closed — O2, D-7's re-measurement

Post-D-7, published binary, same fixtures and route.

| Axis | Pre-D-7 slope | Post-D-7 slope | Change |
| --- | --- | --- | --- |
| **A** — BASE-5K → DEEP-50K (node set constant at 17) | ~0.496 KB/fact | **0.086 KB/fact** | **5.8× lower** |
| **B** — BASE-5K → BROAD-50K (nodes ×9.5) | ~0.508 KB/fact | **0.084 KB/fact** | converged with A |
| per entity | 889 B | **154 B** | **matches the 156 B prediction within 1%** |

**Predictions, adjudicated.** **P1 holds** — 0.086 KB/fact against the 0.64 reference, and Axis A
is where the node set is constant, so this is the acceptance arm. **P3 holds** — the DEEP/BROAD
RSS gap is ~1.7%, so the node-map term remains negligible. **P4 holds** — wall time fell ~5% at
both 50k arms as a side effect of the removed Gen0 churn, the O(entities) scan shape is
unchanged, and both remain far inside the ~1 s MCP ceiling.

**Axis A and Axis B converging is the strongest single confirmation of the whole diagnosis.**
Pre-D-7 the two axes differed because Axis B added 1,080 entity rows on top of its extra nodes,
and each of those rows carried 889 B of churn; remove the churn and the axes collapse onto each
other, because what remains is the same per-row `GetString` on both. A shape-dependent slope
became shape-independent, which is exactly what "the term was per-row, not per-node" predicts.

**One figure was not relayed and is not worth a run to obtain:** the absolute post-D-7 RSS deltas
per fixture. The two-parameter model's ~2.1 MB intercept is therefore **not re-fit**; only its
slope parameter is confirmed. If anyone re-opens this, that is the number to ask for first.

### Closed — O1, the retained-memory plateau

**First attempt correctly rejected.** 8 calls each pre/post-D-7. Under A5's sensitivity floor
that arm could only bound the ~27 MB-scale leak, never the ~19 KB-scale one it was run to
exclude. Flagged as ambiguous rather than reported as a pass — which is the outcome the floor
requirement exists to produce.

**Re-run: 300 calls, post-D-7 binary, DEEP-50K, one server and session, no sleep between calls.**

| Window | Slope |
| --- | --- |
| first 100 calls | 1,827 KB/call |
| last 100 calls | **3.2 KB/call** (+416 KB total across the window) |

**Read: plateau, not climb. The condition is cleared.** Three things make it decisive rather than
suggestive. The separation is **571×**, and a genuine leak has a constant rate — it does not decay
by two and a half orders of magnitude, which is what distinguishes a warmup curve (JIT tiering,
buffer pools, SQLite page cache, session state settling) from accumulation. The tail sits **6×
below A5's 19 KB/call floor**, so it excludes the specific structural leak the discriminator
names — a node map retained per call — rather than merely excluding the obvious one. And the tail
is flat rather than slowly rising, so 3.2 KB/call reads as allocator and segment noise, not a
slower leak.

**No `GC.GetTotalMemory` instrumentation was added, and that was the right call.** The lab-grade
managed-heap arm was A5's fallback for an *ambiguous* RSS read; this read is not ambiguous, and
adding an unrequested diagnostic hook to the server to confirm something already settled is a
product surface change riding in under a measurement — the same category error D-3 refuses.

**Two coverage questions, both answered without further runs.** There is no 300-call *pre*-D-7
arm, and none is owed: the retained bound is a property of the shipping code, and D-7 is shipped.
And the plateau was measured on DEEP-50K only, not BROAD-50K — where the node map is ~9.5× larger
and a leak of it would be ~180 KB/call rather than ~19. That is a *weaker* test, not a missing
one: the mechanism under examination is whether the three dictionaries are discarded at the end
of the call, which is shape-independent, and DEEP-50K is the arm where a leak of it would be
hardest to see. Passing at the hard end covers the easy one.

### Recorded — the server's settled resident footprint under heavy browse

**~228 MB**, DEEP-50K (50,445 facts, ~28,920 entities), after warmup, measured on the same
300-call same-session run as O1.

This is a **level**, and every other memory number in this document is a **slope**. It answers a
different question from either — not "does it grow" but "how much does it hold" — and it is
recorded here as a plain number so that nobody meets it later as a surprise and reopens a
settled investigation. Four things to read with it:

- **It is not a fault, and there is no bar on it.** D-5 binds retained *growth* and budgets
  transient bytes *per entity*. Neither says anything about the level a healthy warmed server
  settles at, and inventing a ceiling for it now would be the fourth instance of the mistake this
  document already catalogues three of.
- **It is the same phenomenon as O1's warmup slope, described the other way round.** 1,827 KB/call
  over the first 100 calls is ~183 MB, which is most of the 228 MB. The two figures are one
  observation, not two independent facts, and they corroborate each other: a settled level that
  did *not* roughly equal baseline-plus-warmup would mean one of the two readings was wrong.
- **It excludes embedding weights.** Per fixture hazard H2 the measurement server ran with the
  vector provider off. With `provider = "local"` the embedder is a container singleton, so a real
  server's resident figure is this **plus** the weights — a materially larger number that has
  nothing to do with browse.
- **It is corpus- and workload-specific.** DEEP-50K at root/depth-3 is the heaviest realistic
  browse this investigation constructed, driven 300 times back to back with no idle. It is a
  reasonable upper reading for browse-driven footprint at 50k facts; it is not what an ordinary
  instance sits at, and this instance holds ~13k facts.

### NEEDS-EVIDENCE 1 — never triggered, and now moot

The SQLite-versus-C# split was gated on a realistic-corpus miss of the corrected ceiling. **No
miss occurred at any point**, so it was never run and is not owed. It remains mandatory before
any D-2 revival, since D-2 only helps if row production or transfer dominates. The routing was
four-way and the answer turned out to be the fourth: **per-row allocation → D-7**.

---

## Decisions

**D-1 — The bound goes on materialization, in C#, not on the read, in SQL. UNTOUCHED, and
explicitly reaffirmed.** Derived from the aggregation semantics and pinned by an already-green
test. **The corollary that drove A5:** browse must read every entity row under the prefix to
accumulate `FactsUnder`, so an **O(entities)** term is inherent and cannot be removed by anything
short of reading fewer rows. When the escalation posed "either D-1 or the constraint has to
give", the ruling was that neither does — D-1 is forced, and the constraint was misstated.

**D-2 — The segment rule keeps exactly one implementation, and it stays in C#. REJECTED AND
CLOSED.** *Rejected — SQL-side bucket aggregation:* group entities into depth-`k` bucket paths
with `instr`/`substr` arithmetic, returning one row per child instead of one per entity. It is
buildable and it would cut row transfer. It is rejected because it puts a second implementation
of the `/`-and-`#` boundary rule in the database — the trade this repository already refused for
`fact_token`: "a second tokenizer written in SQL agrees with the first until one of them is
tuned." The analogous failure is a child that appears twice or vanishes.

**Neither key ever turned.** The wall-time ceiling was not missed (235/230 ms against ~1 s, and
~5% lower after D-7), and NEEDS-EVIDENCE 1 was never triggered. **Recorded in fairness: D-2 would
also have reduced the memory term**, since fewer rows means less per-row churn — the strongest
argument that has ever existed for it. It still lost, because **D-7 captured that benefit locally
and measured 5.8×, with no second implementation of the grammar.** You do not trade a
single-implementation rule when a local fix is available; this one is now the worked example.

**D-3 — Zero-fact entities are not pruned from the result.** *Rejected — `HAVING count(f.id) >
0`.* It changes displayed output at every prefix: a node whose whole subtree is closed beliefs
renders as `0 facts here, 0 under it`, and pruning removes it from the tree. That may well be
desirable, but it is a product decision about what browse shows, not a performance change, and it
must not ride in under one. **Invoked once more since**, to keep a diagnostic hook out of the
server during O1.

**D-4 — `ReadAt` lands regardless of the measurement.** Semantically identical, strictly cheaper
at every prefix, small.

**D-5 — Browse's bar: two wall-time ceilings split by reachability, plus a retained-memory bound
and a transient-allocation budget.** Settled at the Ultra-Advisor tier over three rounds; full
text below.

**D-6 — Partial top-15 selection, for child-sort cost. Not scheduled, and clearly not the
issue.** `BuildNode` sorts `k` registered children to `Take(15)` — `O(k log k)`, which was the
whole of the 317 ms on the adversarial fixture where `k = 50,000`. The lever is a partial
selection over the **same comparator**, `O(k)` plus a sort of 15. On the realistic fixture the
widest node has `k = 80`, so this is not reachable in practice. It keeps its address; nothing
schedules it.

**D-7 — The segment walk allocates no strings per row. LANDED, and it was the remedy.** The
pre-D-7 walk allocated, per row and per level, a `rest` suffix copy, a `segment` copy, a
`childPath` concatenation, and on `#` boundaries a `display` concatenation — up to twelve string
allocations per row at `depth: 3`, measured at **~889 bytes of garbage per entity scanned**.

The fix needed no new algorithm and no second implementation of the grammar. The
non-obvious enabling insight, and the one to preserve if this is ever touched again: **because
the path is walked prefix-first, every intermediate `childPath` is already a prefix slice of
`rowPath`** — `currentPath` is a prefix, the separator is the next character, and the segment
follows it — so it needs no concatenation at all, and on the terminal branch it *is* `rowPath`.
The same geometry gives `display` for free: the `#` a display name must keep is the character
immediately before its segment in `rowPath`, so both forms are spans one character apart.
Materializing a `string` only on first insert then bounds allocation to **node count within
depth** — 17 times on the realistic root arm, not 28,920.

**Measured: 154 B per entity against 156 predicted, a 5.8× reduction, with wall time ~5% lower
and every displayed value unchanged** (865/865, arm 5 falsified at 16 failures).

---

## D-5 in full — browse's bar, in three parts

Ruled at the Ultra-Advisor tier over three rounds. Jim was consulted on the first: no objection
to the substance, with the right reserved to dispute the 200 ms figure.

**1. Wall time — one ceiling per reachability class, because a bar is only meaningful paired with
the arm it governs.**

| Arm | Reached by | Ceiling | Status |
| --- | --- | --- | --- |
| CLI, `depth: 1` | a human, mid-keypress | **200 ms** | **met** |
| MCP `engram_browse`, any depth | an agent, mid-turn | **~1 s** | **met at ~23%** |

Grounds for 200 ms: ~100 ms is human "instant", ~200 ms is "no felt lag", and browse is entered
by a keypress. Grounds for ~1 s: the standard for an agent-reached call is **negligible against a
model turn**, not felt as instant. The 200 ms figure had first been applied to root/depth-3 — an
arm reachable *only* through `engram_browse`, since the CLI hardcodes `depth: 1`, and therefore
an arm with no human on it. The Ultra-Advisor recorded that mispairing as its own error.

**2. Binding — retained growth must be flat in corpus size. MET.**

Primary measurement: the **steady-state plateau** across repeated `engram_browse` root/depth-3
calls against one live server, run for enough iterations that the GC actually collects — a
three-call check proves nothing. Lab-grade alternative: managed heap after a forced full
collection. **Discriminator:** a linear per-call climb of roughly node-map-size × calls is a leak
and fails; a slowing asymptote is GC and segment behaviour and passes. **Cleared by O1** at 3.2
KB/call against a 19 KB/call floor, with a 571× warmup-to-tail separation.

**3. Tracked budget, not a bound — transient bytes per entity. Currently 154 B.**

Re-measured whenever the read path changes, and recorded per this document's doctrine rather than
asserted in CI. A miss here is adjudicated under the wall-time ceilings above, not treated as
fatal. History: **889 B before D-7, 154 B after.**

**Why the constraint moved rather than D-1.** The escalation posed a fork — either D-1 gives or
the constraint does — and the ruling is neither: **the constraint was misstated.** D-1 is forced,
pinned by `FactsUnder`'s green test, so every admissible design reads every row and therefore
allocates at least N strings; even D-7's irreducible floor grows peak RSS with corpus. **A bar
that no admissible design can satisfy is not a bar.**

**The converse check, which is what makes this a correction rather than a convenience.** Would
the restated bar have caught the incidents that motivated the original? **Both, yes.** D53's
unbounded walk held 7.8 GB *retained*, and pre-fix browse's `counts` dictionary was *retained*
O(entities). Every scar in this family is retained accumulation; none is transient churn. And the
"is this just moving the bar to fit a number" worry was checked explicitly and rejected: **the
retained bound was already met before the restatement**, and nothing currently failing became
passing that should not.

**The pattern these corrections form, stated so a future reader sees one discipline rather than
several wobbles.** Recall's 50 ms — a hot-path, per-hook number — was borrowed for a keypress
verb. The 200 ms perception ceiling was paired with an MCP-only arm no human reaches. And
accumulation-grounded "flatness" was applied to transient churn that never accumulates. **Each
was caught by asking what the number was actually for**, and each time the answer named a
quantity or an arm the bar had never been about. A latency or memory bar is a triple — a number,
the arm it governs, and the quantity it measures — and getting any one of the three wrong
manufactures a miss.

**A fourth instance of the same discipline, at the measurement rather than the bar.** O1's first
attempt would have reported a pass at a sensitivity floor too coarse to see the leak it was run
to exclude. The question that caught it is the same one: *what is this number actually for?* A
plateau test bounds a leak **rate**; it does not establish zero retention, so it has to be sized
against the smallest leak it is meant to rule out, and its floor reported beside its verdict.

**And the reason the ~228 MB footprint above is recorded without a ceiling.** It is a level, and
this document's bars are all about slopes. Attaching a threshold to it now — with no arm named
and no idea what a healthy level is on other corpora — would be the same mistake a fifth time.

---

## Carrying this forward — the one thing still open

**Nothing in 05b is pending. But its findings have no home outside this spec chain, and one of
them is fragile in a way the test suite cannot protect.**

D-7's span-based walk is *less* readable than the substring version it replaced, and its
justification is a measurement recorded here rather than anything visible at the call site. A
future reader tidying `MemoryBrowser.Browse` back to substrings would restore 5.8× the allocation
**with all 865 tests still green** — the equivalence guard compares displayed values and is by
construction blind to how they were computed. That is precisely the class of invariant this
repository keeps in `CLAUDE.md` under *Invariants that are easy to break by accident*, and the
comments now in `MemoryBrowser.cs` are a partial mitigation, not a substitute for it.

Three things are candidates for graduation, in descending order of how much a loss would cost:

1. **The span rule and its number** — `Browse`'s walk allocates per node, not per row; 889 → 154
   B/entity; the equivalence test cannot catch a regression.
2. **The entities-scanned framing** — `Browse` costs scale with entity rows under the prefix,
   including zero-fact entities, not with fact count. Every per-fact reading of this path in
   this investigation was wrong.
3. **The bar-triple discipline** — number, arm, quantity — with the four corrections above as its
   worked examples.

**I am not making that edit, and not only because it is outside a spec's scope.** It is a change
to project instructions, and this document's work was tasked by a peer session; a peer cannot
authorize an edit to `CLAUDE.md`. It goes to the Orchestrator and to Jim as a recommendation.

**Also still open, and unchanged since 05a:** the hint-line wording under 05a's D-5. It is a
product wording question, it was never mine to settle, and it should not be lost simply because
the performance work around it closed.

---

## Confidence

**High** that 05a's depth bound is impossible and that the bound belongs on materialization.

**High** on Change 2. Set-and-order equivalence is a property of the two queries, checkable by
reading them.

**High**, measured rather than argued, that Change 1's node-map bound works: DEEP-vs-BROAD
isolates it at under 1% of growth pre-D-7, and post-D-7 that gap shrank *with* the per-row term
rather than holding its size — two independent confirmations of the same claim.

**High** on the transient-allocation diagnosis, and it is now the best-evidenced claim in the
document. Four independent lines agree: a two-parameter per-entity model fitting three fixtures
within 3%; a per-row byte count derived from the source landing within 4% of the observed
marginal; a retained term measured directly and found negligible; and a remedy predicted at 156
B/entity that measured 154.

**High** on the retained bound, where I was previously medium. O1's 571× warmup-to-tail
separation is a stronger result than a bare plateau: it distinguishes the two curve shapes rather
than merely observing that one number stopped rising.

**Medium** on exactly one thing, and it is not worth resolving: the model's ~2.1 MB intercept was
never re-fit post-D-7, since only slopes were relayed. Nothing depends on it. It is recorded so
that anyone reopening this asks for the absolute deltas first rather than re-deriving them.
