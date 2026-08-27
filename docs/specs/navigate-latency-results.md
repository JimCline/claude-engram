# navigate callers/callees latency — §4 measurement results

Executes docs/specs/close-graph-query-gap.md §4 (NEEDS-EVIDENCE). Produced by
`tests/Engram.EndToEnd.Tests/NavigateLatencyMeasurementTests.cs`, run against the published
binary (`out/engram`, built 2026-08-25) on this machine (Apple Silicon, macOS).

## Method

- Corpus: real JavaScript repos (100 functions/file), indexed through `engram index --apply`
  on the published binary — never through `Engram.Core` directly, so the numbers describe what
  ships. Two scales: 5,000 and 50,000 functions.
- Per function, one `calls` fact to either a uniquely-named callee (the common case — this is
  what makes distinct `symbol-name` entity count scale with corpus size), one of 20 spellings of
  a shared `Hub` leaf (~2% of functions, the hub arm), or `DistinctiveTargetFn` (exactly 2
  callers, the distinctive arm).
- Verified seeded before timing: `entity(kind='symbol-name')` count and live `calls` fact count
  both came in within 2% of the target function count at both scales (this repo has been bitten
  twice by a fixture that looked seeded and was not — §4 names this as step zero).
- Driven over HTTP MCP (`engram_navigate`) against the published binary's server, the only path
  available — `navigate` has no CLI verb. One warmup call per arm, then 7 timed calls per arm,
  median taken. Arms alternated every iteration, not run to completion one at a time.
- **Deviation from the spec's "3 shapes × 4 relations":** only `callers` and `callees` were
  measured. Both hypotheses (H1, H2) and the whole decision table concern only those two;
  `defined_at`/`imports` cost is a single `SymbolResolver.Resolve` call with no hypothesis
  attached in §4, already characterized by D58 for the same query shape in recall.
- **Known gap in this run:** the `callees` "hub" arm (query `Fn_0`) is *not* a genuine
  many-callees case — `Fn_0` in this corpus has exactly one callee, so H2 (`callees` scales with
  callee count) was not actually exercised. Caught while writing this doc, not before the run.
  H1 was answered decisively enough on its own (see below) that this run was not repeated to fix
  it — see "what this does not settle."

## Results

### 5,000 functions

Full re-index wall clock: 982 ms.

| relation | shape | median ms | floor-subtracted ms |
|---|---|---|---|
| callers | no-match | 1.06 | 0.00 |
| callers | distinctive | 1.77 | 0.70 |
| callers | distinctive-b | 1.65 | 0.59 |
| callers | hub | 3.58 | 2.52 |
| callees | no-match | 1.39 | 0.32 |
| callees | distinctive | 0.88 | -0.18 |
| callees | distinctive-b | 1.00 | -0.07 |
| callees | hub | 1.08 | 0.02 |

### 50,000 functions

Full re-index wall clock: 9,559 ms.

| relation | shape | median ms | floor-subtracted ms |
|---|---|---|---|
| callers | no-match | 11.85 | 0.00 |
| callers | distinctive | 13.99 | 2.14 |
| callers | distinctive-b | 13.95 | 2.10 |
| callers | hub | 29.65 | 17.80 |
| callees | no-match | 12.07 | 0.22 |
| callees | distinctive | 7.96 | -3.89 |
| callees | distinctive-b | 8.05 | -3.80 |
| callees | hub | 11.19 | -0.66 |

(distinctive/distinctive-b are the same query issued as two separately-labelled arms, the
self-vs-self calibration the protocol calls for — they agree within noise at both scales.)

## Reading the numbers

**`callers`' no-match arm scales with corpus: 1.06 ms → 11.85 ms for a 10x corpus (≈11x).**
Per §4's pre-committed decision rule, that is H1 confirmed, not the flat-floor outcome. The
mechanism is slightly different from what H1's write-up anticipated, and more precise: a query
that matches nothing at any tier pays for **all three** of `SymbolResolver.Resolve`'s fallback
scans (exact, case-insensitive COLLATE NOCASE, substring LIKE) before giving up, and none of
those three is index-assisted on `entity(kind, name)`. `MatchingSymbolNames`'s unconditional
`WHERE kind = 'symbol-name'` scan (the mechanism §4 named) only runs once `Resolve` has *found*
a declaration — so for a query that resolves to nothing, the cost floor is entirely
`SymbolResolver.Resolve`'s own three sequential scans, and it is *this* that is O(corpus).

This also explains why `callees`' distinctive/hub arms measured **faster** than its own no-match
arm at both scales (5k: 0.88–1.08 vs 1.39; 50k: 7.96–11.19 vs 12.07) — counterintuitive next to
"the no-match arm is the floor," but consistent with the mechanism: `DistinctiveTargetFn`/`Fn_0`
match on the *first* (exact) tier and return immediately, while `ZzzAbsentSymbolNotInStore`
exhausts all three tiers before reporting nothing found. A name that resolves is cheaper than one
that doesn't, and the gap is a second, independent piece of evidence that `Resolve`'s tiers scan
rather than look up.

**`callers`' hub arm shows real match-proportional cost on top of the floor**: 2.52 ms extra at
5k, 17.80 ms extra at 50k — growing faster than the floor itself, consistent with
`MatchingSymbolNames` additionally scanning every `symbol-name` row once a declaration is found,
then ranking every live `calls` fact whose object matched.

**Absolute magnitudes are still small** — 30 ms worst case at 50,000 functions, well under any
interactive budget — but the spec's decision rule is keyed to *shape* (scaling vs. flat), not to
an absolute threshold, and by that rule this is squarely the "H1 confirmed" row:

> MatchingSymbolNames is a floor-shaped scan. Index or computed-leaf column becomes the
> priority, ahead of §1 and §2.

## What this does not settle

- **H2 is unmeasured.** The `callees` "hub" arm in this run does not exercise "one subject with
  many callees" — see "known gap" above. If H1's conclusion (prioritize an index/computed-leaf
  fix) is accepted, that fix likely touches `SymbolResolver.Resolve` more than the
  callee-resolution loop `Callees` runs per callee, so H2 may be moot rather than requiring its
  own re-run — but that is a call for whoever scopes the fix, not asserted here.
- **No `EXPLAIN QUERY PLAN` capture.** The spec asks for it as corroborating (not decisive)
  evidence; the clock result here is unambiguous enough on its own that it was not captured.
- Single machine, single run per corpus size (median-of-7 per arm, not multiple full corpus
  rebuilds) — good enough to read the shape (scales vs. flat), not a tight confidence interval
  on the absolute numbers.

## Addendum — §11.2's `implementers` leaf-match arm

Architect priced the `implementers` leaf-match change at +17.80 ms @50k (§9.4), citing `callers`'
own hub-arm cost above as the estimate for the same `MatchingSymbolNames` mechanism reused on
`implementers`. §11.2's dispatch asked that this be measured on the actual change rather than
taken on the estimate. Same method as above, same corpus and binary (rebuilt to include the
leaf-match change), with `implementers` added as a third relation to the existing three-arm
harness: the corpus's classes (`class Cls_N extends …`) are seeded at the same stride as the hub/
distinctive callers, so the same repo serves both measurements — see `GenerateRepo` in
`NavigateLatencyMeasurementTests.cs`.

### 5,000 functions (implementers arm)

| relation | shape | median ms | floor-subtracted ms |
|---|---|---|---|
| implementers | no-match | 1.39 | 0.27 |
| implementers | distinctive | 1.76 | 0.64 |
| implementers | distinctive-b | 1.68 | 0.56 |
| implementers | hub | 2.11 | 0.99 |

### 50,000 functions (implementers arm)

| relation | shape | median ms | floor-subtracted ms |
|---|---|---|---|
| implementers | no-match | 11.27 | -0.34 |
| implementers | distinctive | 14.63 | 3.02 |
| implementers | distinctive-b | 14.52 | 2.91 |
| implementers | hub | 16.24 | 4.63 |

**Measured cost is +4.63 ms @50k for the hub arm, not the +17.80 ms estimate.** The estimate was
`callers`' own hub-arm number reused by analogy, not a prediction computed for `implementers`
specifically, and the two relations' hub arms do not do the same amount of work per match: this
corpus's hub base-class spellings are declared once per matching class (one `inherits` fact per
`Cls_N`, at the same 1-in-50 stride as hub callers), while `callers`' hub arm ranks a live `calls`
fact for *every call site* naming a hub spelling — the `RankFrom`/`IsTypeDeclaration` work
`CodeCallGraph.Callers` does per result, which `NavigateImplementers` does not do at all. Fewer,
cheaper-to-process matching facts is the whole difference; `MatchingSymbolNames`'s own
`symbol-name`-table scan is identical in both relations and is not what diverges here.

Well within §9.2's budget either way — this is a correction of the number cited for the decision,
not a reversal of the decision itself.
