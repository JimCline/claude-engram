# 05b/F — Fixture spec for the realistic-corpus measurement

Companion to `docs/memory-expansion/05b-browse-depth-bound-spec.md`. It exists because the
outstanding arm in 05b was blocked on "what does a realistic corpus look like", and that question
has a measured answer rather than a judgement call. **Follow this mechanically. Nothing here
needs the Implementor to decide what "realistic" means.**

---

## 0. Read this first: the blocker dissolves

The brief reported two problems. One is real, the other is not a problem at all.

**Not a problem — "we cannot confirm the pre-fix baseline's shape."** Correct, and it does not
matter. D-5's memory constraint is *"the peak-RSS delta between a 5k-fact arm and a 50k-fact arm
must be flat rather than proportional to fact count."* That is a property of **one binary
measured at two corpus sizes on one shape**. It needs no pre-fix baseline and no cross-shape
comparison. The "compare against a differently-shaped pre-fix baseline" framing was loose
wording carried forward from the ruling; comparing two numbers taken on two unknown, different
shapes could never have established anything, which is exactly what the 317 ms round demonstrated.

**So R1 (0.02 s/40 MB @ 5k, 0.12–0.13 s/92 MB @ 50k) is retired to historical.** Its shape was
never recorded, so it is not comparable to anything and must not be used as a baseline again. It
stays in 05b's Recorded table as provenance for how this investigation started, and for nothing
else.

**Real problem — every fixture measured so far is the same flat shape.** That is what this
document fixes.

**A pre-fix arm is optional, not required.** If one is wanted, build it the way this repo already
does it — a controlled pair of binaries either side of the fix, run against *the same* fixture,
alternating arms. Never against a different corpus.

---

## 1. What the real shape actually is

Derived by read-only inspection of the live instance's structure (via `engram_browse`, which
reads and writes nothing) and cross-checked against `docs/engram-path-grammar.md`, which is the
authority. No fixture is derived from the real store's *contents*, and nothing was written to it.

**The grammar** (`docs/engram-path-grammar.md`, `grammar_version = 2`):

```
/projects/<project>/code/<repo>                      the repo
/projects/<project>/code/<repo>/<rel/path>           a file
/projects/<project>/code/<repo>/<rel/path>#<frag>    a symbol or doc section
```

`<rel/path>` is the repo-relative path verbatim, so it contributes **several** `/` segments. A
fragment joins nested names with `/` *after* the `#` — `FactStore.cs#FactStore/Remember` — so
depth continues past the `#` boundary.

**The measured structure of the live instance** (13,190 facts, 8 projects):

| Level | Node | Fan-out observed |
| --- | --- | --- |
| 1 | `/projects` | **1** child (everything is under it) |
| 2 | project | 8, fact counts 6015 / 3960 / 879 / 874 / 632 / 289 / 235 / 68 — steep skew |
| 3 | `code` | 1 |
| 4 | repo | 1–2 |
| 5 | top-level dir or file | ~19, of which ~4 substantive and the rest 1-fact files |
| 6 | module dir | ~3 |
| 7 | source file | **116** in `Engram.Core` — the one wide level |
| 8 | `#symbol` | 1–9, mean ≈ 4, steeply skewed (one dominant symbol takes ~60–75%) |

**Density:** 1,625 facts over 116 files ≈ **14 facts per file**; ≈ 4 symbols per file; ≈ 3.5
facts per symbol.

**The single most important number for this measurement.** Root fan-out is **1**, and the whole
tree within depth 3 of root is `/projects` + 8 projects + 8 `code` nodes = **17 nodes** — for
13,190 facts. Nodes-within-depth-3-of-root is O(number of projects). It does not track fact
count on any real shape. That is the claim Change 1 makes, and the real store already exhibits
the structure that makes it true.

**Which is why the adversarial fixture is the exact inverse of reality**: it put 50,000 children
at depth 1, where the real store has one.

---

## 2. The generator

Deterministic, no RNG, no seed to record. Same algorithm for every arm; only the parameters
change.

```
DIRS         = ["src", "tests", "docs", "scripts"]     # take the first D
SYMBOL_FACTS = [6, 2, 1, 1, 1]                          # Sym00 .. Sym04

for p in 0 .. P-1:
    project = f"proj{p:03d}"
    for d in 0 .. D-1:
        for m in 0 .. M-1:
            for f in 0 .. F-1:
                file = f"/projects/{project}/code/{project}/{DIRS[d]}/Mod{m:02d}/File{f:03d}.cs"

                emit 1 fact at  file                        # file-level summary
                for s in 0 .. 4:
                    emit SYMBOL_FACTS[s] facts at f"{file}#Sym{s:02d}"
                emit 1 fact at  f"{file}#Sym00/Member0"     # nested fragment (grammar v2)
                emit 1 fact at  f"{file}#Sym00/Member1"
```

Per file: **14 facts**, **8 entities**, deepest path 9 segments. Both match the live instance
(14 facts/file measured; 8 segments there, 9 here because the nested-fragment form is included
deliberately — the universal extraction tier rarely emits it, and it should still be exercised).

**Fact bodies must be a fixed ~120-character sentence, identical in length across every arm, and
the length must be recorded.** The constraint is **KB per fact**, so body length is a first-order
term in the number being measured. A fixture with short bodies understates the slope and a
fixture with varying bodies makes two arms incomparable. Vary only a leading index so bodies are
distinct; hold the length constant.

---

## 3. The three fixtures

| Fixture | P | D | M | F | Files | Facts | Entities | Nodes ≤ depth 3 of root |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **BASE-5K** | 8 | 3 | 3 | 5 | 360 | **5,040** | ~2,900 | **17** |
| **DEEP-50K** | 8 | 3 | 3 | 50 | 3,600 | **50,400** | ~28,920 | **17** |
| **BROAD-50K** | 80 | 3 | 3 | 5 | 3,600 | **50,400** | ~30,000 | **161** |

Three fixtures, two comparisons, and they answer different questions — the same split D-5 uses
for its own two constraints.

**Axis A — deepen (BASE-5K → DEEP-50K). This is the acceptance arm.** Facts ×10, nodes within
depth 3 **constant at 17**. It isolates D-5's claim exactly: does peak memory track fact count
when the materialized node set does not?

**Axis B — broaden (BASE-5K → BROAD-50K). The honesty arm.** Facts ×10 and nodes within depth 3
×9.5, because more projects genuinely means more top-level nodes. It tests the case that *could*
break flatness, and shows what it costs when it does.

**And the control that makes both readable: DEEP-50K vs BROAD-50K.** Identical fact count,
near-identical entity count (they differ by ~1,080 directory entities, which *is* the shape
difference, not a confound). Any RSS difference between them is attributable to tree shape alone
— that is a direct measurement of the node-map term, which no arm so far has isolated.

---

## 4. Predictions, written before the run

Recorded in advance so the result is read rather than rationalized. Each has a stated failure
reading.

| # | Prediction | What the opposite means |
| --- | --- | --- |
| P1 | **Axis A marginal slope ≪ 0.64 KB/fact, near zero.** Node set is constant, so peak memory should barely move. | ~0.64 KB/fact on Axis A means the node map still grows with facts on a corpus where it must not. **Change 1 did not land as specified — re-examine, do not wave through.** |
| P2 | **Axis B slope small and nonzero, tracking node count (+144 nodes), not fact count.** | A slope matching Axis B's *fact* growth means something scales with facts independent of shape. |
| P3 | **DEEP-50K ≈ BROAD-50K in RSS**, differing by roughly 144 nodes' worth. | A large gap at equal fact count means the node map dominates, and **D-6 (partial top-15) is the lever**, not D-2. |
| P4 | **Wall time grows ~linearly with facts on both axes while RSS does not.** The row scan is O(entities) and inherent (D-1); the materialization is not. | That divergence *is* Change 1's signature. If wall and RSS grow together, the one-pass accumulation is not doing what it claims. |
| P5 | **Both 50k arms land far under both ceilings** — well inside 200 ms, and nowhere near ~1 s. | A miss routes to NEEDS-EVIDENCE 1's three-way split (05b), and only one of those three branches is D-2. |

---

## 5. Arms to run

**Required.** Root, `depth: 3`, on BASE-5K, DEEP-50K, BROAD-50K. Wall time and peak RSS on each.

**Recommended, not gating — the realistic worst interactive arm.** On DEEP-50K, browse
`/projects/proj000/code/proj000/src` at `depth: 3`. That materializes ~3 modules × 50 files × 7
fragments ≈ 900 nodes and genuinely exercises `Take(15)` and `ChildrenOmitted`, which the root
arm never touches at fan-out 8. If any arm is going to be slow on a realistic shape, it is this
one — and it is the one a person actually navigates to.

---

## 6. Hazards. Two are new and one of them invalidates results silently

**H1 — VERIFY THE SERVER IS RUNNING THE NEW BINARY BEFORE TRUSTING ANY MCP-ROUTE NUMBER. This is
not hypothetical; it is live right now.** Browsing `/` on the currently running MCP server
returns *"Nothing in memory under /"*, while `/projects` on the same server returns 12,952 facts.
That is 05a's root bug, answering today — so **that server predates 05a**, and any measurement
taken through it measures the pre-fix implementation.

- Restart the server from the freshly published binary before measuring.
- **The check that discriminates:** browse `/` and confirm it returns content. If it says
  "Nothing in memory under /", the server is stale and the number is worthless.
- **Confirm whether the 317 ms/0.64 KB-per-fact run went through a restarted server.** If it did
  not, R2 measured pre-Change-1 code, and 05b's "memory constraint met" rests on it. Report the
  answer either way — this is cheap to check and expensive to be wrong about.

**H2 — the server's baseline RSS can swamp the slope.** `depth: 3` is reachable only through
`engram_browse`; `BrowseCommand` hardcodes `depth: 1`. The server holds an embedder as a
container singleton, and `provider = "local"` means hundreds of megabytes of resident weights
with nothing to do with browse. Measure the **delta** across the call — RSS immediately before,
peak during — and start the server with the vector provider off.

**H3 — the transport has its own floor.** Calibrate with a trivial tool call through the same
transport. The ceiling is on the user-visible operation, so the floor is context for reading the
number, not something to subtract.

**H4 — do not add a `--depth` flag to `BrowseCommand` to make this convenient.** That is a
product surface change riding in under a measurement, the same category error D-3 refuses. If no
published-binary route produces the number, report that instead of substituting a test-host
measurement (D58's trap).

---

## 7. Isolation

**The fixture is synthetic and lives in a sandbox home. `ENGRAM_HOME` is set explicitly on every
invocation, including ad-hoc ones — the real `~/.engram` has been littered once already.** The
real instance was consulted read-only for *structure* to derive the parameters in §1, and nothing
in §2 depends on its contents.

**Seeding tool and measured binary are allowed to differ, and should.** 50,400 facts through
50,400 process spawns is not a fixture, it is an afternoon. Seed in-process through a single
harness; then **measure the published binary against the resulting store**. Record which seeding
route was used — it does not affect the measurement, but it affects whether anyone can reproduce
the fixture.

**Record with every datapoint: corpus shape, route (MCP or in-process), server binary provenance,
and fact-body length.** Every one of those has already been the thing that made a number
unreadable at least once in this investigation.
