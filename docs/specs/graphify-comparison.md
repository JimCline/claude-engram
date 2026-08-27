# Graph query design: engram vs graphify

Read-only comparative analysis. Nothing here was executed; every mechanism claim is
cited from source by a fact-gathering pass over both repos. This is a research
document, not a change spec — the "next step" names at the end are names only.

**Sources.** engram: `docs/engram-schema.sql`, `docs/engram-implementation-plan.md`,
`src/`. graphify: `/Users/jimcline/git/repos/graphify` — `README.md`,
`ARCHITECTURE.md`, `BENCHMARKS.md`, `docs/how-it-works.md`, `extract.py`, `build.py`,
`serve.py`, `cli.py`, `watch.py`, `affected.py`, `cache.py`.

---

## 0. TL;DR

- The two systems are **not the same kind of thing**, and most of the gap follows from
  that. graphify is a code-comprehension *retrieval* system whose entire product is a
  traversable graph. engram's code graph is a *deterministic lookup* surface layered
  onto a temporal belief store whose primary job is recall.
- **engram is behind on breadth**: no multi-hop traversal of any kind, 4 relation
  predicates against graphify's 11–16, tree-sitter for TS/JS only against ~40 grammars,
  no ranked search over the graph, no graph-shape analytics, no token-budgeted packing.
- **engram is ahead on correctness discipline**: per-result staleness marking on every
  relation, temporality/supersession/provenance on every edge, fail-closed deletion under
  a truncated scan, and an explicit coverage caveat when a lookup misses. graphify has
  none of the first and an index-layer analogue of the third.
- **Embedding is a non-comparison.** graphify deliberately has no vector path at all
  ("Not a vector index. No embeddings, no vector store" — `README.md:34`). engram has a
  vector lane, but `engram_navigate` never calls it; it serves recall and `explain` only.
  On the code-graph axis both systems are 100% lexical/structural.
- **Verdict: not on par on capability; roughly on par or ahead on rigor.** The single
  largest concrete gap is transitive traversal. The single largest unknown is that engram
  has *no measured navigate latency anywhere* and no code-graph-specific SQL index —
  flagged below as NEEDS-EVIDENCE rather than guessed at.

---

## 1. Graph representation

### 1.1 Storage

| | engram | graphify |
|---|---|---|
| Store | SQLite, `fact` rows | in-memory NetworkX `Graph`/`DiGraph` |
| Persistence | the live database | node-link JSON `graph.json` |
| Edge shape | `fact(subject_id, predicate, object_id)` | NetworkX edge with `relation` attr |
| Temporality | `valid_from` / `valid_to` / `superseded_by` on every edge | none |
| Load cost | none (query the table) | whole graph into RAM per project |

engram (`docs/engram-schema.sql:120-159`) stores a relation as an ordinary fact row —
subject, predicate, object — guarded by `ux_fact_edge_live` (171-172):
`UNIQUE(subject_id, predicate, object_id) WHERE valid_to IS NULL AND object_id IS NOT NULL`.
A dead `edge` table survives at 223-234, explicitly retired by D70
(`schema.sql:218-221`, plan `5193-5208`): it lacked `valid_to`/`superseded_by`, so it
could not express *this call used to exist*. Folding edges into `fact` inherits
temporality, supersession and provenance for free. **This is the single design decision
that most separates the two systems**, and it is one graphify does not have an answer to
— a NetworkX edge is present or absent, never *closed*.

graphify (`build.py:1340`, `serve.py:25`) keeps the whole graph resident and serves
several projects from a bounded LRU of loaded contexts (`_GraphContextCache`,
`serve.py:102-161`, cap 8). That is what buys it cheap arbitrary traversal — and what
makes its working set proportional to the corpus rather than to the query.

### 1.2 Relation vocabulary

- **graphify** — `SEMANTIC_RELATIONS` (`extract.py:266-268`): `inherits`, `implements`,
  `mixes_in`, `embeds`, `references`, `calls`, `imports`, `imports_from`, `re_exports`,
  `contains`, `method`. `DEFAULT_AFFECTED_RELATIONS` (`affected.py:12-30`) adds
  `indirect_call`, `extends`, `uses`, `dynamic_import`, `requires`.
- **engram** — four: `about` (tier-0 doc/lead comment, `CodeAnalyzer.cs:55`),
  `declared-as` (`CodeAnalyzer.cs:83-89`, `DeepAnalysis.cs:126-129`), `imports`
  (`CodeAnalyzer.cs:116-123`, `DeepAnalysis.cs:141-149`), `calls` (tier-1/2 only,
  `DeepAnalysis.cs:151-162`).

engram has **no containment and no type hierarchy**. It cannot answer "what methods does
this class have", "what implements this interface", or "what does this type inherit from"
— all of which graphify answers structurally. For a C#-heavy codebase indexed by a
*Roslyn* sidecar, which has full semantic type information available, that is the most
surprising omission in the comparison: the fidelity is present at extraction time and is
not being emitted.

### 1.3 Extraction tiers and language coverage

- **graphify**: tier 1 = tree-sitter, ~40 grammars, deterministic, no LLM
  (`README.md:341`, `docs/how-it-works.md:3-23`). Tier 2 = local faster-whisper for
  video/audio. Tier 3 = a Claude subagent LLM pass for documents/PDFs/images — and
  **code never uses tier 3**, which is a discipline worth noting: the LLM extends the
  graph's *material*, never its code edges.
- **engram**: tier 0 = regex, generic + Markdown (`CodeAnalyzer.cs:211,370`); tier 1 =
  tree-sitter, **TypeScript/JavaScript only** (`CodeAnalyzer.cs:253,327`, D47); tier 2 =
  Roslyn, C#, out-of-process sidecar (`CodeAnalyzer.cs:223`, per D1 "Roslyn never links
  into the core binary").

engram's `analyzer_tier` column has no counterpart in graphify. Per `schema.sql:154-158`:
"0 = tier-0 regex, 1 = tree-sitter, 2 = Roslyn… Stamped at write time, never derived or
backfilled", with the monotone upgrade enforced in SQL (`FactStore.cs:207-215`,
`analyzer_tier IS NULL OR analyzer_tier < $tier`). This lets engram say *how much to
trust an edge*, which graphify's uniform tree-sitter tier never needs to and therefore
cannot. graphify's nearest analogue is per-edge `confidence`
(`EXTRACTED|INFERRED|AMBIGUOUS` plus a `confidence_score` for inferred edges,
`docs/how-it-works.md:94-99`) — a different axis: graphify grades *this particular
edge's certainty*, engram grades *the analyzer that produced it*. Both are useful;
neither subsumes the other.

**Net: engram behind on language coverage (2 real grammars vs ~40) and relation
vocabulary (4 vs 11–16); ahead on C# fidelity (semantic Roslyn vs syntactic tree-sitter)
and on recording extraction provenance.**

---

## 2. Query surface

### 2.1 What each exposes

**graphify** — CLI (`cli.py`) and MCP (`serve.py:1508-1720`):

| verb | mechanism |
|---|---|
| `query "<q>" [--dfs] [--context] [--budget]` | lexical seed ranking → BFS/DFS expansion → token-budgeted pack (`cli.py:1068`) |
| `path <src> <tgt> [--directed]` | `nx.shortest_path`, `max_hops=8` (`cli.py:1400`) |
| `explain <node>` | `cli.py:1567` |
| `affected <node> [--relation] [--depth]` | reverse-dependency closure (`cli.py:1189`, `affected.py`) |
| `god-nodes [--top]` | degree analytics (`cli.py:1257`) |
| `get_neighbors` / `get_community` / `graph_stats` | MCP-only |

**engram** — one MCP tool, `engram_navigate` (`EngramMcpTools.cs:724-737`). Description
verbatim: *"Where is a symbol defined, what does a file import, or who calls/is called by
a symbol — a deterministic lookup over indexed code, not a search."* Params: `query`,
`relation` ∈ {`defined_at`, `imports`, `callers`, `callees`, `neighbors`}, `repo`,
`limit` (default 20, clamped 1–100, line 739). `neighbors` is **recognized but
unimplemented** (743-748) — it answers "not yet indexed" rather than an empty result,
which is the honest failure mode but is still a hole in the advertised surface.

There is **no `engram navigate` CLI verb** — MCP only (confirmed against `CliApp.cs` and
a grep of `Navigate` across `src/Engram.Cli`). `engram_index_repo`
(`EngramMcpTools.cs:503-546`) is enrollment bookkeeping (enroll/decline/later), not a
read path.

### 2.2 Traversal

This is the headline difference.

- **graphify**: pure-Python BFS (`serve.py:924-952`) / DFS (`955-979`) over the resident
  graph, explicit integer `depth` (default 3), with a hub-degree guard that refuses to
  transit high-degree nodes unless they are seeds (`925-933`). Plus `shortest_path` to
  `max_hops=8`.
- **engram**: **no transitive traversal exists anywhere.** Every relation is one or two
  sequential single-hop lookups:
  - `defined_at`: `SymbolResolver.Resolve` (exact → `COLLATE NOCASE` → substring LIKE,
    `SymbolResolver.cs:59-83`) → `FactStore.History` for the live `declared-as`
    (`EngramMcpTools.cs:816-829`).
  - `imports`: exact path else suffix LIKE (`876-889`) → `FactStore.History` per file.
  - `callers`: `Resolve` (≤1000 candidates) → `LiveCallsToObjects`, leaf-name matched;
    the source comments it "No join" (`CodeCallGraph.cs:65-97`).
  - `callees`: `Resolve` → `LiveCallsFromSubjects` → **a second `SymbolResolver.Resolve`
    per callee** to bind the name to a declaration site (`CodeCallGraph.cs:99-138`) —
    the one genuine query-time join in the whole surface.

No recursive CTE, no BFS, no depth parameter. "Who calls X, transitively" and "what would
break if I change X" — graphify's `affected` — are simply unanswerable in engram today.

### 2.3 The deferred-resolution trade

engram resolves call targets **at query time**, deliberately: D72 (plan `5229-5258`)
argues that storing a resolved target would rot on rename, because the blob-SHA skip
never revisits an unchanged file. graphify resolves **at build time**, deduplicating with
Jaro-Winkler (75–92 = ambiguous, optional LLM resolve, `build.py:1371-1389`).

Neither is strictly better and the report should not pretend otherwise. engram pays a
resolver round-trip per callee forever and is rename-safe; graphify pays once and its
edges rot on rename — but its watcher-driven rebuild keeps the rot window to seconds.
graphify's choice is coherent *because* it rebuilds eagerly; engram's is coherent
*because* it does not. **A future engram that adopted eager rebuild would need to
re-litigate D72, and one that adds traversal will pay the deferred resolution cost once
per hop.** That is the design coupling to be aware of before anyone specifies multi-hop.

### 2.4 Ranking

- **graphify** ranks: `_score_query` (`serve.py:462-629`) does tiered exact/prefix/
  substring/source-file matching × IDF (`_compute_idf:290-312`, classic
  `log(1+N/(1+df))`), with a trigram prefilter and a coverage-squared multi-term bonus.
  Then it packs to a token budget. This is a search engine over a graph.
- **engram orders but does not rank**: `CallRankSignal` ascending-is-better
  (`SameFile`, `QualifierAgreement`, `ImportFilenameMatch`, `SameRepo`, `NameOnly` —
  `CodeCallGraph.cs:6-13`), tiebreak alphabetical path; `defined_at`/`imports` order by
  `SymbolMatchTier` then path (`SymbolResolver.cs:97`). There is no query-relevance
  score because there is no query beyond a symbol name.

This one is **not a defect**. engram's stated contract is "a deterministic lookup over
indexed code, not a search", and engram's *search* need is served by `recall` — three
fused lanes (FTS, token overlap, vector) with a corroboration-based coverage signal
(D44/D60) that graphify has nothing comparable to. graphify folded search and traversal
into one verb; engram split them across two tools. The split is defensible; the cost is
that nothing in engram searches the *graph*.

**Net: engram substantially behind on query surface — no traversal, no depth, no
path-finding, no reverse-dependency closure, no graph analytics, no token budgeting, one
advertised relation unimplemented.**

---

## 3. Staleness and incremental update

### 3.1 Query-time staleness — engram is clearly ahead

engram marks results. Three commits, in order:

1. `48f1a0c` — coverage caveat on a miss (`EngramMcpTools.cs:718-721`), verbatim: *"This
   is what Engram has indexed, not what exists: gitignored files are never indexed, and
   recent edits land only after the index queue drains. Fall back to Grep/Glob before
   concluding the symbol is absent."* The commit cites a measured gap: **2,276 files
   against 467 indexed.**
2. `212ce78` — `FileFreshness.cs`, wired into `defined_at`.
3. `b48301c` — extracted `AppendFreshness` (`EngramMcpTools.cs:790-806`) and applied it to
   `imports`/`callers`/`callees` too.

Mechanism: mtime, not re-hash — `file_state.indexed_at` vs `File.GetLastWriteTimeUtc`
with a 1s grace (`FileFreshness.cs:65-116`). States `Unknown|Fresh|Stale|Missing`
(35-48); a detached repo with no `disk_path` reports `Unknown`, **not** `Missing`
(85-96), which is the right call — absence of a disk path is not evidence of deletion.
Per-line marker is literally `[stale]`/`[missing]` (`FileFreshness.cs:57`), with a
summary note at `EngramMcpTools.cs:713-716`.

graphify has **no per-query staleness signal at all**. Its `graph.html` viewer carries an
unrelated stale marker; the query path never tells a caller that an answer predates an
edit. Its answer to the problem is to rebuild fast enough that it does not arise.

**This is engram's clearest win, and it is the one that matters most for an LLM consumer**
— a model cannot tell a stale answer from a current one, so a system that returns
confidently-wrong graph edges is worse than one that returns marked ones.

### 3.2 Change detection and incremental unit — roughly par

| | engram | graphify |
|---|---|---|
| Detect | blob SHA compare (`CodeIndexer.cs:265-282`) | stat signature size+`mtime_ns` (`cache.py:192-264`), SHA256 fallback (`414,500`) |
| Unit | one file (`CodeIndexer.cs:243`, spool drain) | one file (`_rebuild_code`, `watch.py:1086`) |
| Trigger | `engram index --drain --apply`; session-start maintenance child (`MaintenanceLauncher.cs:107-128`); enrollment spawn (`EngramMcpTools.cs:537-540`) | debounced fs watcher; git post-commit hook driving `git diff --name-only` |
| Preserves | unchanged files skipped by SHA | unaffected nodes/edges preserved (`watch.py:711-748`) |

Both are file-granular and both avoid full rebuilds. The difference is *latency to
freshness*: graphify's watcher reacts within a debounce window, engram's queue drains at
the next session start or an explicit `index`. **engram is behind on freshness latency and
ahead on admitting it** — the `[stale]` marker exists precisely because the drain is not
immediate. Those two facts are one design, not two.

### 3.3 Deletion — independently fail-closed on both sides

engram (`CodeIndexer.cs:192-238`): a truncated scan skips deletions entirely
(`suppressedReason="truncated"`, 214-218) because "a partial scan cannot show a file is
gone"; a zero-file scan against nonzero prior state likewise (`"empty-scan"`, 220-228);
only a complete nonzero scan computes real deletions (232) and stamps
`RepoEnrollment.StampFullScan` (317). Budget at `RepoScanner.cs:48-58`, stop reasons
`{Complete, TimeBudget, FileCeiling, Unreadable}` (23-29).

graphify (`watch.py:509,592-701`): `_reconcile_existing_graph` evicts only on *positive*
deletion evidence, backed by a shrink-guard (`_check_shrink`, `892`).

Two codebases with no shared lineage reached the same rule — **absence of evidence is not
evidence of deletion**. Genuinely on par, and the convergence is itself a signal the rule
is right.

---

## 4. Indexing strategy and measured performance

### 4.1 Indexes

- **graphify**: no SQL store, so no SQL indexes. In-memory acceleration only — a trigram
  index (`serve.py:359,383`), an IDF cache (`298`), and the bounded graph-context LRU
  (`102-161`).
- **engram**: **no code-graph-specific SQL index exists.** The full `CREATE INDEX` list in
  `docs/engram-schema.sql` (`ix_entity_kind:94`, `ix_entity_alias_entity:109`,
  `ix_fact_thread:184`, `ix_fact_path:186`, `ix_fact_session:187`, `ix_fact_scope:188`,
  `ix_fact_regenerable:192`, `ix_supersession_new:214`, `ix_edge_to:234` [dead table],
  `ix_repo_enrollment_root:382`, `ix_fact_token_fact:405`,
  `ix_fact_relation_fact/_related:453-454`) is entirely general fact-store indexing.
  `engram_navigate` rides `entity.path` (unique) plus `ix_fact_thread` / `ix_fact_path`.

### 4.2 Measured numbers

graphify publishes real figures (`BENCHMARKS.md`), quoted verbatim:

- "LOCOMO retrieval recall@10 of 0.497, about 10x mem0 (0.048) and above BM25 (0.362)"
  (19-21).
- "LongMemEval-S of 76%, tied for best with dense RAG… Zero LLM credits to build the
  graph" (22-24).
- ERPNext ~1M LOC: "lifts key-fact coverage… from 70.8%… to 82.0%, at about 140K tokens
  per query" (145-146).
- 689 weekly checkpoints, Nodes|Edges|Files: `2011-06-08 | 3,069 | 2,900 | 1,032` →
  `2026-06-24 | 22,620 | 48,710 | 3,758`; "grows about 7x in nodes and 17x in edges"
  (155-158).
- "71.5x fewer tokens per query" on a 52-file corpus (`how-it-works.md:57-63`).

engram publishes **none for navigate or code indexing.** Every millisecond figure in
`CLAUDE.md` and the implementation plan concerns hooks, recall, or embedding. The closest
adjacent number is a `RepoScanner` directory-walk fix (plan `1558-1563`): "the same two
directories walk 150 files in 60 ms and 2 files in 27 ms — from 4,485 ms and 3,257 ms" —
which is scan cost, not query cost.

> **NEEDS-EVIDENCE — navigate latency at scale.** `callers` resolves up to **1000**
> candidate symbols (`CodeCallGraph.cs:65-97`) and `callees` runs a *second* resolver
> per callee (`99-138`), all against a `fact` table with no code-graph index. This repo's
> own history says exactly why that is worth measuring rather than assuming: deleting
> `ix_fact_thread` cost **93% of every recall** (1,545 ms → 105 ms at 50,097 facts), it
> was invisible at 5k, and **"a plan is not a clock"** — `EXPLAIN QUERY PLAN` showed the
> scan and could not show it was 99% of the statement.
>
> What to run: `engram explain`-style timing, or a direct timed `engram_navigate` against
> a 50k-fact store, for each of the four relations, on a symbol with (a) one caller and
> (b) many callers; plus `EXPLAIN QUERY PLAN` for `LiveCallsToObjects` and
> `SymbolResolver.Resolve`.
>
> What each result decides: if `callers`/`callees` p50 is flat in corpus size, no index
> work is warranted and the gap in §5 drops off the list. If it grows with corpus rather
> than with match count — the same shape D58/D60 chased for recall — then a code-graph
> index is a prerequisite to *any* multi-hop traversal work, because a hop multiplies it.
>
> Per the Architect contract this is not run here. Route it to the Implementor.

---

## 5. Embedding

### 5.1 graphify: none, by design

`README.md:34`, verbatim: *"Not a vector index. No embeddings, no vector store: a real
graph you traverse."* `docs/how-it-works.md:30`: *"No embeddings needed… The graph
structure is the similarity signal."* Its retrieval benchmarks are the argument for that
position — LOCOMO recall@10 of 0.497 against BM25's 0.362 and mem0's 0.048, and
LongMemEval-S 76% "tied for best with dense RAG", with zero LLM credits.

One caveat carried forward from the gathering pass and **not independently confirmed**:
`BENCHMARKS.md:87-88` mentions **BGE-m3, 1024-d**. The reading is that this belongs to a
separate comparison harness (`memory/crosstool`, not present in this checkout) used to
give *competing* systems a fair embedding space — not to graphify's own pipeline. That is
consistent with every other statement in the repo, but the harness repo was not available
to check. **Treat "graphify uses BGE-m3" as false unless that harness says otherwise.**

### 5.2 engram: present, but not on this path

- The vector lane is **never called by `engram_navigate`**, which is pure SQL
  (`SymbolResolver` / `CodeCallGraph` / `FactStore.History`). Only `recall` and `explain`
  call it — "There is one vector lane, and recall and `explain` both call it" (D30/D36).
- Code-scope facts *are* eligible for general vector backfill —
  `VectorIndex.ReadBackfillBatch` (`VectorIndex.cs:159-167`) has no scope filter and
  selects any unembedded live fact — so code facts do get embedded, and are reachable
  through `recall`, just not through `navigate`.
- Configured models (`EmbeddingModels.cs:89-146`, none code-specific): `all-minilm-l6-v2`
  384-d / 256-tok / **Mean** (94-95,102); `nomic-embed-text-v1.5` (default) 768-d /
  8192-tok / **Mean** (112-120); `qwen3-embedding-0.6b` 1024-d / 32768-tok / **Last**
  (130-140).
- The surrounding discipline has no graphify counterpart because graphify has no
  embeddings: `dim` is **probed, never typed** (D34) since a mismatched width ranks like
  noise and errors nowhere; pooling is the third silent-failure knob (D45, measured
  cos(mean,last)=0.76 on MiniLM); `EmbedderFactory` **never loads a model** (D35) because
  creating an embedder is unowned at every call site.

### 5.3 The comparison

There is no like-for-like axis here. **On the code-graph question the two systems are both
fully embedding-free**, and engram's vector machinery is orthogonal to everything in §1–4.

The one real philosophical difference is in how each handles *prose*. graphify pulls
documents, PDFs and images into the **same graph** via an LLM extraction tier and then
traverses them structurally alongside code — one retrieval mechanism for everything.
engram keeps prose as facts and reaches them with a **fused lexical+vector recall** that
is separate from `navigate` — two mechanisms, split by data kind. graphify's benchmarks
are a claim that the first approach beats dense retrieval on memory tasks; engram has not
measured itself on any comparable benchmark, so that claim is untested here rather than
refuted.

---

## 6. Verdict

**Is engram's graph query design on par with graphify's? No — not on capability.** It is
on par or better on the properties that keep answers honest.

Where engram is **ahead**:

1. **Temporal edges.** `fact(subject_id, predicate, object_id)` with
   `valid_to`/`superseded_by` (D70) — engram can say an edge *used to* exist. NetworkX
   cannot represent that at all.
2. **Per-result staleness.** `[stale]`/`[missing]` on every relation (`b48301c`), mtime-
   based, `Unknown` for detached repos. graphify has nothing at query time.
3. **Miss honesty.** The coverage caveat naming gitignore and queue drain as the reasons a
   symbol may be absent, and `neighbors` answering "not yet indexed" rather than empty.
4. **Extraction provenance.** `analyzer_tier` with a monotone SQL-enforced upgrade.
5. **Semantic C#.** A Roslyn sidecar beats tree-sitter on the language it covers.

Where engram is **on par**: fail-closed deletion (both refuse to evict without positive
evidence), file-granular incremental update, and deterministic non-LLM code extraction.

Where engram is **behind**, each with a next step — **names only, nothing specified, and
nothing here is authorized for implementation**:

| # | Gap | Evidence | Next step (name only) |
|---|---|---|---|
| 1 | **No transitive traversal.** Every relation is single-hop; no depth, no BFS, no path-finding, no reverse-dependency closure. graphify has BFS/DFS to depth *n*, `shortest_path` to 8 hops, and `affected`. | `CodeCallGraph.cs:65-138`; `serve.py:924-979`, `cli.py:1400,1189` | **Bounded transitive navigate** — a depth-limited walk over `ux_fact_edge_live`. Note the coupling in §2.3: deferred resolution (D72) is paid per hop. Gate on the NEEDS-EVIDENCE item first. |
| 2 | **`neighbors` is advertised and unimplemented.** | `EngramMcpTools.cs:743-748` | **Implement or retire `neighbors`.** |
| 3 | **No containment or type hierarchy.** Cannot answer "methods of this class", "implementers of this interface", "base type of this". The Roslyn tier already has the information. | predicates at `CodeAnalyzer.cs:55-123`, `DeepAnalysis.cs:126-162` vs `extract.py:266-268` | **Structural predicates** — `contains` / `inherits` / `implements` from the existing tier-2 pass. |
| 4 | **Language coverage.** Tier-1 tree-sitter is TS/JS only against ~40 grammars. Everything else falls to regex. | `CodeAnalyzer.cs:253,327` vs `README.md:341` | **Widen the tier-1 grammar set.** |
| 5 | **No code-graph index, no measured latency.** ≤1000-candidate resolution plus a per-callee second resolve, over an unindexed-for-this-purpose `fact` table. | §4.2 | **NEEDS-EVIDENCE** (§4.2) → then a code-graph index only if the measurement demands one. |
| 6 | **No graph-shape analytics.** No degree/hub view, no community, no stats. | `cli.py:1257`, `serve.py` `get_community`/`graph_stats` | **Graph-shape read verbs.** |
| 7 | **No token-budgeted packing.** graphify packs a traversal to a caller-set budget; engram returns a flat `limit`-capped list. | `cli.py:1068`; `EngramMcpTools.cs:739` | **Budgeted result packing** (only meaningful after #1). |
| 8 | **Freshness latency.** Drain happens at session start, not on edit. graphify watches. | `MaintenanceLauncher.cs:107-128` vs `watch.py` | **None recommended.** The `[stale]` marker is the deliberate answer; a watcher would breach the hook-frequency rules in `CLAUDE.md`. Listed for completeness, not as a defect. |

### Decisions I did not make

- **Whether engram should close any of these gaps.** Items 1, 3 and 4 are real capability
  work, and whether a belief store *should* grow into a code-comprehension retrieval
  engine is a product call, not a design one. graphify's benchmarks argue the capability
  pays; engram's own D18/D43 gates argue nothing is proven about adoption yet. That is
  Jim's call.
- **Priority order.** The table is grouped by kind, not ranked. #5 is the only one with a
  sequencing claim attached (it gates #1).

### Confidence and escalation

Confidence is **high** on every mechanism claim in §1–§3 and §5.2 (all cited to source at
line granularity), **high** on §6's ahead/behind classification, and **moderate** on §5.1's
BGE-m3 caveat — flagged inline rather than resolved, because the harness repo was not
present to check.

No Ultra-Advisor escalation is recommended. This is a research comparison; it settles no
irreversible design question, and the one genuinely open item (#5) is an empirical
question with a defined experiment, not a judgment call.

### Unverified assumptions

- graphify was read, not run; all its numbers are quoted from `BENCHMARKS.md` rather than
  reproduced.
- engram's navigate behaviour is read from source, not observed. In particular no claim
  here rests on what a live `engram_navigate` call actually returns.
- One stale cross-reference found in engram's own docs while gathering: **D72 cites
  `CodeIndexer.cs:234` for the blob-SHA skip; the current source has it at ~line 274.**
  Line drift only, not a behaviour change — noted so it can be corrected separately, not
  as part of this analysis.
