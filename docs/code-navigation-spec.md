# Code navigation — specification

**Status:** Phase 1 shipped (TS/JS + C# only — see §3.0), with one defect open (§3.5).
Phases 2–4 design, not implemented.
**Goal:** an LLM answers *where is Z defined*, *what does Y import*, *who calls X* from engram's
own surface instead of `Read`/`Grep`, across more languages than C# alone.

**Read `docs/engram-implementation-plan.md` (D1–D72) and `docs/engram-schema.sql` before
implementing.** This spec cites them by number and does not restate their reasoning.

---

## 0. What is actually true today

Every claim in this section was verified against the files during this spec's authoring, not
carried over from `docs/scratch/code-graph-gap-analysis.html`. The gap analysis had already been
corrected once; several of its surviving premises — and several in the dispatch that commissioned
this spec — are still wrong. They are corrected here because the phasing depends on them.

**The three-tier extractor is fully built.** All three tiers exist and run:

| Tier | Mechanism | Entry point | Languages |
|---|---|---|---|
| 0 | Regex, in-core, universal | `CodeAnalyzer.Analyze` | every file; `Text` catch-all, `markdown` |
| 1 | tree-sitter, grammars side-loaded from `~/.engram/lib/` | `CodeIndexer.Tier1Analyses` → `TreeSitter.Analyze` | `typescript` (+`.tsx`), `javascript` |
| 2 | Roslyn, out-of-process sidecar | `CodeIndexer.DeepAnalyses` → `RoslynSidecar.Analyze` | `csharp` |

Tiers 1 and 2 both produce `DeepAnalysis` and converge through `DeepTier.Merge`
(`src/Engram.Core/DeepAnalysis.cs:83`) onto tier 0's candidate list — one merge implementation,
per D24. A file is tier 1 **or** tier 2 by its language, never both, so the two passes cannot
collide on a key (`CodeIndexer.cs:392-400`).

Consequences the dispatch got wrong:

- **Tier 1 is not aspirational.** `TreeSitter.cs` is 496 lines of working `NativeLibrary.Load`
  against `libtree-sitter` plus one dylib per grammar, driven by S-expression queries carried as
  registry columns (`DeclarationQuery`, `ImportQuery`). D47 settled acquisition: pinned,
  digest-checked C source compiled by `cc` at install through `scripts/fetch-tree-sitter.sh`.
- **Adding a tier-1 language is genuinely one registry row.** D24 and D47 both commit to this in
  writing, and the code honours it: `TreeSitter.Analyze` takes a `LanguageDefinition` and reads
  its queries; there is no per-language `switch` anywhere in the extractor. This is the single
  biggest correction to the proposed phasing — language expansion is *cheap and unblocked*, not a
  phase that has to wait behind edge design. **Not yet verified end-to-end** — see §3.0.
- **The Roslyn sidecar is a real project**, `src/Engram.Sidecar.Roslyn/engram-roslyn.csproj`. D6's
  stated rationale for gating M3 — "the one carrying the D1 sidecar risk" — is therefore
  **retired**: that risk was paid down already. See §6.

**How a symbol is addressed and named.** Both matter to §3.5 and were verified:

```csharp
// CodePaths.cs:37
public static string ForSymbol(string filePath, string symbolName) =>
    $"{filePath}#{symbolName}";
```

Callers pass a grammar-v2 *fragment* as `symbolName` — a scope chain, so a nested symbol is
`Outer.Inner` (`DeepAnalysis.cs:112`, `CodeAnalyzer.cs:79`). `entity.name` is then derived inside
`FactStore.EnsureEntity` (`FactStore.cs:663-676`): a caller-supplied `displayName` if given,
otherwise **everything after the last `/` in the path**. The code write path
(`FactStore.cs:80`) passes no `displayName`.

**No structural edges exist, at any tier, and no component even models them.** `CodeAnalyzer`
emits exactly three predicates — `about`, `declared-as`, `imports`. There is no `calls`,
`references`, or `implements` predicate anywhere in `src/`. `imports` is a single fact whose body
is a sorted, comma-joined module list resolved to nothing (`CodeAnalyzer.cs:109-119`). The
transport records carry no invocation data either:

```csharp
public sealed record DeepSymbol(
    string Name, string Kind, string Declaration, string? Doc,
    string? Scope = null, string? Params = null);

public sealed record DeepAnalysis(
    string Path, IReadOnlyList<DeepSymbol> Symbols, IReadOnlyList<string> Imports, string? Error);
```

`src/Engram.Sidecar.Roslyn/Program.cs` (283 lines) contains no occurrence of `Invocation`, `Call`,
`Symbol`, or `Reference` at all. So call extraction is not a matter of surfacing something the
sidecar already computes — see §5.1.

**`fact.object_id` is unreachable from the write path the indexer uses.** Two write paths exist
and only one can set it:

- `FactStore.Remember` ← what `CodeIndexer` calls (`CodeIndexer.cs:547`). Its `FactWrite` record
  (`FactStore.cs:6-16`) **has no `Object` or `ObjectKind` field**, and its `INSERT`
  (`FactStore.cs:723-728`) does not name `object_id`.
- `FactJournal.Insert` ← backup replay only. Its `JournalFact` carries `Object`/`ObjectKind` and
  it does populate `object_id` (`FactJournal.cs:533-535`).

So "populate `fact.object_id`" is not a matter of passing a value that is already plumbed. The
ordinary write path cannot express an object at all.

**Every live fact is read on the recall path, unfiltered.** `FactStore.ReadLive`
(`FactStore.cs:283`) is `SELECT … FROM fact f JOIN entity e … WHERE f.valid_to IS NULL` with an
optional *scope* filter and no predicate filter, and `RecallEngine.BuildCandidates`
(`RecallEngine.cs:405`) iterates the result with no skip condition (`:417`, `:430`, `:443`).
`ReadLive` has 21 call sites, including `PrimerSummary`, `VectorIndex`, `FactTokenIndex` and the
backup path. This is what makes §4.5 a correctness constraint rather than an optimization.

**The unbuilt `edge` table.** `docs/engram-schema.sql:206-217` declares it; nothing reads or
writes it except `SELECT COUNT(*)` at `BackupStore.cs:53`. It has `created_at` and no `valid_to`
or `superseded_by` — a snapshot structure, which is the same limitation the gap analysis correctly
identifies in the tool it compares against.

**Deletion and supersession of code facts already work.** `CodeIndexer.ProcessFile` diffs
candidates against live facts under the file's path and `FactStore.Forget`s the unmatched ones;
`ProcessDeletion` closes all of a removed file's facts. Only `regenerable` facts are touched, so
an agent's or the user's testimony about a symbol is never superseded by extraction (D19,
`CodeIndexer.cs:508-515`). **This needs no new design** — see §5.3.

---

## 1. The constraint that shapes everything: one live fact per subject+predicate

```sql
CREATE UNIQUE INDEX ux_fact_live ON fact(subject_id, predicate) WHERE valid_to IS NULL;
```

`FactStore.Remember` enforces the same rule in code: it finds the live fact for
(subject, predicate), closes it, then inserts (`FactStore.cs:81-98`). `CodeIndexer` keys its
candidate diff on the same pair — `var key = (candidate.EntityPath, candidate.Predicate);`
(`CodeIndexer.cs:505`).

**A symbol can therefore hold exactly one live `calls` fact.** One fact per edge is impossible
under the current schema. This is the real Phase-2 problem, and it is not mentioned in the gap
analysis or the dispatch.

Three encodings were considered:

- **Set-in-body** — one `calls` fact whose body lists every callee, as `imports` does today. No
  schema change. Rejected: the body is opaque to traversal, which is the entire point, and a
  one-character edit to any callee rewrites the whole belief, destroying per-edge temporality.
- **Target-in-predicate** — `calls:Foo`. Rejected: unbounded predicate cardinality, and the
  predicate is a term in the lexical lanes, so it would pollute `fact_fts` and `fact_token` with
  one term per callee.
- **Object-bearing facts with an edge-aware uniqueness index** — chosen, specified in §4.1.

### 1.1 The trap in the obvious fix

Adding `object_id` to the existing index —

```sql
-- WRONG. Do not implement this.
CREATE UNIQUE INDEX ux_fact_live ON fact(subject_id, predicate, object_id) WHERE valid_to IS NULL;
```

— is a silent catastrophe. **SQL unique indexes treat NULLs as distinct.** Every ordinary belief
has `object_id IS NULL`, so `(subject, predicate, NULL)` would no longer collide with itself, and
`ux_fact_live` would stop constraining anything it constrains today. Revising a preference would
insert a second live row instead of superseding the first, and recall would return both. Nothing
would error. The existing test suite would very likely stay green, because
`FactStore.Remember` closes the incumbent in C# *before* inserting — the index is a backstop, and
a backstop that has stopped working looks exactly like one that was never needed.

The correct migration is two disjoint partial indexes (§4.1).

---

## 2. Phasing

I recommend a different order than the dispatch proposed, on one finding: **language coverage
does not depend on the edge model.** Declarations and imports already flow end-to-end for any
tier-1 language, so Python/Go/Rust/Java can ship *before* any schema change — and shipping them
first means the edge model is later designed once against two known-good languages rather than
re-cut across six.

| Phase | Delivers | Schema change | Answers |
|---|---|---|---|
| 1 | Language expansion + a read-only navigation verb | none | *where is Z defined*, *what does Y import* |
| 2 | Edge substrate: object-bearing facts | migration v13 | — (enables 3) |
| 3 | Call extraction + query-time resolution | none | *who calls X* |
| 4 | Trust surface and measurement | migration v14 | how good is the answer |

Phase 1 answers two of the three questions asked, across six-plus languages, without touching the
store's shape. That is deliberate: it puts a usable navigation surface in front of the model
early, and it makes the D6 override (§6) answerable with real adoption data *before* the expensive
phases are spent.

**Where I disagree with the dispatch, explicitly:** it placed language expansion at Phase 2, behind
edge representation. That ordering pays the highest-uncertainty work first and delivers nothing
until Phase 4. If the Orchestrator or Jim prefers the original order, the phases below are still
individually correct — only their sequence changes — but I do not recommend it and the reason is
in §0: the registry work is already unblocked.

---

## 3. Phase 1 — Definitions and imports

### 3.0 What actually shipped, and what it leaves unproven

**Phase 1 shipped `engram_navigate` + `SymbolResolver` over TypeScript/JavaScript and C# only.**
The language-expansion half of this phase — Python, Go, Rust, Java — was **deferred by the product
owner** to a later iteration, despite E1/E2 measuring it as cheap. §3.1–§3.3 below describe that
deferred work and are still current; they are not a record of what shipped.

Three consequences of the split, none blocking:

- **D24's registry-row claim is now unverified in-repo.** E1/E2 proved the four grammars compile,
  load and query. They did **not** prove the end-to-end claim in §3.1 — that a new language costs
  one row and zero edits to `CodeAnalyzer`/`TreeSitter`/`CodeIndexer`/`DeepTier`. That proof is the
  conformance suite needing no edit when a row is added, and it now waits for the later iteration.
  Treat it as unproven rather than assumed: if the abstraction has drifted, it will surface then
  and will look like a language problem when it is a registry problem.
- **Phase 1 touched nothing native, so its tier-3 exemption is narrow.** §3.3 requires tier-3
  coverage *because new grammars mean `NativeLibrary.Load` under AOT*. With no new grammars that
  argument lapses, and skipping tier 3 was correct. It does not generalize: the exemption is about
  native loading, not about `engram_navigate`.
- **E1/E2's evidence has a shelf life.** Grammar pins age. If the deferred iteration is more than a
  few weeks out, re-run the fetch rather than trusting the P0 result — those versions
  (java v0.23.5 among them) were measured at one moment.

### 3.1 Registry rows

Add one `LanguageDefinition` per language to `LanguageRegistry.cs`, following the `typescript`
entry as the model:

```csharp
new(
    Id: "python",
    DisplayName: "Python",
    Extensions: [".py", ".pyi"],
    Tier: 1,
    DeclarationPatterns: [ /* tier-0 fallback regexes, per the csharp row's style */ ],
    ImportPatterns:      [ /* ditto */ ],
    DocHeadings: false,
    Fixture: new( /* required — see 3.3 */ ),
    Grammars: [new(Library: "python", Symbol: "tree_sitter_python", Extensions: [])],
    DeclarationQuery: PythonDeclarations,
    ImportQuery: PythonImports,
    ExpectedDeepSymbols: [ /* grammar-v2 fragment list the conformance suite asserts */ ])
```

Per language: one row, two query constants, one entry in `fetch-tree-sitter.sh`. **Zero edits to
`CodeAnalyzer`, `TreeSitter`, `CodeIndexer`, `DeepTier`, the CLI, or the report.** If the
implementation finds itself editing any of those, the abstraction D24 promised has not held and
that is a spec-defect to report back, not a thing to work around.

Tier-0 patterns are still required on a tier-1 row (the C#, TS and JS rows all carry both):
they are what runs when the grammar is not installed, and D47 makes that a supported state.

**These languages get tier 1 and not tier 2**, and the argument is D24's, unchanged: tier-2 depth
means the language's own toolchain, which means a runtime — a Node runtime for TS, and equally a
Python/Go/JVM runtime here. D20 rejected exactly that coupling shape. Do not revisit it in this
spec.

### 3.2 Query authorship

`ts_query_new` validates node types against the grammar and errors on unknown ones (D47), so a
wrong query fails loudly at first use rather than matching nothing forever. **Every query must be
run against the real compiled grammar before it lands** — D47's rule, restated because it is the
one that gets skipped: *a query literal nobody ran is a regex nobody tested.*

Symbol addressing stays as it is: `CodePaths.ForSymbol` with fragments composed by
`DeepTier.Fragments`, grammar v2, per D48. **Do not bump `CodePaths.GrammarVersion`** — these
languages introduce no new addressing rule, and a bump forces a full re-index of every store.

### 3.3 The conformance suite is the test

D24 requires that one fixture-driven suite iterate the registry, and that a harness carrying its
own copy of the language list is the failure the decision exists to prevent. Each new row supplies
a `Fixture` and an `ExpectedDeepSymbols` list; the existing suite must pick them up **with no
edit**. Confirm that by adding a row and running the suite — if the suite needs editing, that is
the defect.

- **Tier 2 (integration):** per-language extraction against a real grammar — declarations found,
  imports found, re-index of an unchanged file is a no-op, a renamed symbol keeps its `entity.id`
  (D2).
- **Tier 3 (published binary):** required, because this touches native loading. `NativeLibrary.Load`
  under Native AOT is exactly the class of thing that works under `dotnet test` and fails in the
  shipped binary. Drive `./out/engram index --apply` against a fixture repo per language and assert
  the facts land. Read the skip count, not the pass count — `Engram.EndToEnd.Tests` evaporates into
  the skip column without a binary.

### 3.4 The `engram_navigate` MCP verb

One new tool in `src/Engram.Cli/EngramMcpTools.cs`, beside the existing eleven.

```
engram_navigate(query: string, relation: string, repo?: string, limit?: int = 20)
```

`relation` ∈ `defined_at` | `imports` in this phase; `callers` | `callees` | `neighbors` are added
in Phase 3 and must return an explicit *not yet indexed* answer until then — never an empty list,
which is indistinguishable from *nothing calls this* and is the exact failure D60 records for the
coverage digest.

- `defined_at(name)` — resolve against `entity` where `kind = 'symbol'`, returning `path` and the
  `declared-as` body (the declaration line). **Matching is on the symbol's name, not on its path**
  — three tiers, tried in order: exact, then case-insensitive, then substring. Each row carries
  which tier matched, so a substring hit is never mistaken for an exact one.
- `imports(path)` — the `imports` fact for a file. This one *is* addressed by path.

An earlier draft of this section said matching was "exact on the last path segment first", which
described `imports`'s addressing and was applied to both. That wording is what §3.5 is about: it
is wrong for `defined_at`, and the implementation it produced does not do what a reader of either
sentence would expect.

Answers are `file:line`-handled and compact, in the style `FactStore` output already uses.

**Extraction tier is a Phase 4 field and Phase 1 must not report it.** An earlier draft also said
every row carries the tier that produced it, which contradicted §7.1. The two labels are different
things and both belong here eventually:

- **Match tier** — exact / case-insensitive / substring. Phase 1 ships it. It is a property of the
  *lookup*, derivable at query time, and true.
- **Extraction tier** — tier 0/1/2. Phase 4 ships it, from the `analyzer_tier` stamp (§7.1). Phase 1
  has no stamp and cannot derive one honestly, so it reports nothing.

**But "nothing" must be said, not omitted.** A missing tier field is indistinguishable from tier 0,
which is the same failure this section already forbids for `callers` and D60 records for the
coverage digest. Until Phase 4 lands, the answer carries one header line — *extraction tier: not
recorded (stamped from Phase 4)* — once per response rather than per row, since the value is
constant across every row and a repeated constant is noise.

**`repo` filters by path segment, in SQL.** The Implementor's Phase-1 call — substring match on
`/code/{CodePaths.Slug(repo)}/` — is **confirmed**, with four constraints made explicit:

- **The bracketing slashes are load-bearing.** They are what makes a substring test behave as a
  segment-exact one: `/code/engram/` cannot match `/code/engram-docs/`. Dropping either slash
  makes the filter silently prefix-ambiguous, and the failure is a repo returning another repo's
  symbols. Any test here must include a fixture with two repos whose slugs share a prefix.
- **Filter in SQL, not after the read**, as a prefix predicate on `entity.path` — the reasoning is
  §4.5's, one phase early: an in-memory filter still pays for every row crossing the boundary.
- **The slug function must be the one the indexer wrote with.** If `engram_navigate` normalizes a
  repo name differently from whatever produced the stored paths, the filter matches nothing and
  reads exactly like *this repo has no such symbol*. One implementation, per rule 1.
- **An unknown repo says so.** If the slug matches no entity at all, answer *repo not indexed*
  rather than returning an empty result — same rule as everything else in this section.

**`defined_at`'s name→declaration lookup is the one resolver, and Phase 3 reuses it.** Under §5.2's
query-time resolution that same lookup is what binds a callee name to a declaration site. There
must be exactly one implementation of it — rule 1, and the same argument D30 makes for the vector
lane: two resolvers diverge the first time one is tuned, and then `defined_at` and `callees`
disagree about where the same symbol lives. Phase 1 built it as a `SymbolResolver` seam rather than
inlining it into the tool method; keep it that way.

**Recall must not be the transport for this.** `engram_navigate` reads the entity and fact tables
directly and does not go through `RecallRanker`: navigation is a deterministic lookup, and routing
it through a fused lexical/vector ranker would make *who calls X* a relevance question. That is
G3 in the gap analysis and it is correct.

### 3.5 OPEN DEFECT — `entity.name` is not a symbol name, so two of three match tiers are dead

Found while correcting §3.4's wording; it is a real behaviour defect, not a documentation one. The
chain is short and every link is verified in §0:

1. A symbol's path is `{fileEntityPath}#{fragment}` — `CodePaths.ForSymbol`, `CodePaths.cs:37`.
2. The code write path calls `EnsureEntity` **without** a `displayName` (`FactStore.cs:80`).
3. `EnsureEntity` therefore derives the name as everything after the **last `/`**
   (`FactStore.cs:674-675`).
4. `SymbolResolver` matches `e.name = $name`, then `= $name COLLATE NOCASE`, then
   `LIKE '%' || $name || '%'` (`SymbolResolver.cs:39/45/51`).

So `entity.name` for a symbol is `FactStore.cs#Remember` — filename, separator and fragment — and
a lookup for `Remember` cannot match tiers 1 or 2. **Every `defined_at` call falls through to the
substring tier.** Three consequences, in rising order of severity:

- Every answer is labelled *substring*, so the match-tier signal §3.4 just specified is a constant
  and tells the model nothing.
- Precision is poor with no better tier to prefer: `Remember` also matches `RememberBatch` and
  `NotRemembered`.
- **The `LIMIT` can truncate the true answer.** The query orders by `e.path`
  (`SymbolResolver.cs:60-61`), so results are path-ordered rather than match-quality-ordered, and
  a common name in a large store can fill the limit with near-misses while the exact declaration
  never appears. That is a wrong answer, not a slow one.

**Fix at the write, not at the reader.** `EnsureEntity` already takes `displayName` for exactly
this case — its own comment says a caller supplies it when the name "is not recoverable from the
path". Pass the symbol's **leaf name**: for fragment `Outer.Inner`, the name is `Inner`, and the
scope chain stays in `path`, where it already lives and already disambiguates. Rejected
alternative: teaching `SymbolResolver` to parse after `#`. It moves the parsing to every reader,
leaves `entity.name` meaning something no comment claims, and Phase 3 would need the same parse a
second time — rule 1 again.

**Existing stores hold the wrong names, and that is repairable.** `entity.name` is denormalized
display metadata derived from `path`, so by D8 it is derived state that `repair` may recompute —
it is explicitly not belief content, and no fact body, predicate or validity window is touched.
Either a `repair` pass or an `AnalyzerVersion` bump forcing re-extraction is acceptable; **the
repair is preferable**, because a bump re-runs extraction to fix a field extraction did not
produce.

**Falsification, per D60:** the guard is a test asserting `defined_at` on a bare symbol name
returns an **exact**-tier match. It must be shown to fail before the change — and note that a test
merely asserting *the right symbol is returned* passes today via substring, which is how this
survived a green suite. Assert the tier.

---

## 4. Phase 2 — The edge substrate

**Ships:** nothing user-visible. **Schema change:** migration v13. This is the phase to be
careful in.

### 4.1 Migration v13

`EngramDatabase.SchemaVersion` is currently 12; migrations live in `EngramDatabase.Migrate`.

```sql
DROP INDEX ux_fact_live;

CREATE UNIQUE INDEX ux_fact_live ON fact(subject_id, predicate)
  WHERE valid_to IS NULL AND object_id IS NULL;

CREATE UNIQUE INDEX ux_fact_edge_live ON fact(subject_id, predicate, object_id)
  WHERE valid_to IS NULL AND object_id IS NOT NULL;
```

Two disjoint partial indexes, so no NULL ever participates in a uniqueness comparison (§1.1).
Together they say: one live belief per subject+predicate for ordinary facts, one live edge per
subject+predicate+object for edges.

**Required invariant — a predicate is either always object-bearing or never.** The two indexes do
not compose otherwise: an objectless and an object-bearing live fact could coexist on one
subject+predicate, and both would be returned. Enforce with a lint test (tier 1) over a declared
edge-predicate set, asserting no predicate appears on both sides. This is why `imports` must be
converted wholesale in this phase rather than gaining objects incrementally.

**Migration safety:** D31 already snapshots before migrating, so no additional protection is
specified. Per D8 this touches only derived state — indexes — and creates, alters, and deletes no
fact body, predicate, validity window, or supersession row.

**Falsification, per D60:** the test fixture must be a genuine v12 store. `WriteVersion1Store`
rolls a *current*-schema store back, so a fixture built that way already has the new indexes and
the migration no-ops while every test stays green. Drop the indexes explicitly in the fixture, and
assert with `git diff --quiet` that the patch under test actually landed.

### 4.2 What an object *is*: a name-keyed symbol entity

**Settled by Ultra-Advisor ruling, ~85% confidence — see §5.2 for the reasoning.** An edge's
`object_id` points at an entity keyed by the **name as written at the call site**, never at a
resolved declaration. Binding that name to a declaration is a query-time join (§5.2), not stored
belief content.

- Object entities live in their own addressing namespace, distinct from `CodePaths.ForSymbol`'s
  `file#Fragment` addresses — a name is not a location and must not be spellable as one. Use
  `CodePaths.ForSymbolName(name)` with `entity.kind = 'symbol-name'`, **confirmed collision-free
  against existing `kind` values** during P0. The helper does not exist yet (`CodePaths.cs` has
  `ForSymbol` only); Phase 2 adds it.
- One entity row per distinct callee name per store — bounded by distinct identifiers, not by call
  sites.
- Qualifiers are kept as written (`Foo.Bar`, `os.path.join`) rather than normalized. Normalizing is
  resolution, and resolution does not live here. §5.2 specifies how the join handles them.

This keeps Phase 2 **uniform**: `imports` objects are name-keyed module strings and were always
going to be — `calls` is not a special case bolted onto a substrate designed for something else.

### 4.3 Write path

- `FactWrite` gains `string? Object = null, string? ObjectKind = null`, defaulted so no existing
  call site changes.
- `FactStore.Remember` resolves the object through `FactStore.EnsureEntity` (the same call
  `FactJournal.cs:534` already makes) and names `object_id` in its `INSERT`.
- **Object entities must pass `displayName` explicitly**, for §3.5's reason: an object path's last
  `/`-segment is not its name either, and repeating that defect in a table three to six times the
  size of the corpus would be considerably worse.
- **`FindLiveFactId` must become object-aware.** For an edge write it matches
  (subject, predicate, object); for an objectless write, (subject, predicate) with
  `object_id IS NULL`. **Getting this wrong is silent and destructive**: left as-is, writing
  `A calls B` finds and closes `A calls C`, so a symbol with five callees ends each index run with
  one live edge and four spuriously superseded ones — which reads as a call graph that keeps
  changing its mind. A tier-2 test writing two distinct edges from one subject and asserting both
  stay live is the guard, and it must be shown to fail against the unmodified `FindLiveFactId`.
- `CodeIndexer`'s diff key at `CodeIndexer.cs:505` becomes
  `(candidate.EntityPath, candidate.Predicate, candidate.Object)`, and `CodeCandidate` gains the
  matching nullable field.
- `FactTokenIndex.Add` / `RebuildFactFts` stay the chokepoints — no new write path bypasses them
  (§4.4 changes *what* they index, never *where* it is decided).

### 4.4 Edges stay out of the lexical lanes

**Code edges must not enter `fact_fts` or `fact_token`.** This is a design constraint, not an
optimization, and it has two independent arguments.

*Correctness.* D44 computes `coverage` from lane agreement across the scored set. Tens of
thousands of near-identical edge bodies (`calls Foo`, `calls Bar`, …) are corroboration-shaped
noise: they would inflate coverage in the direction that looks like success, which is precisely
the defect D44 exists to correct. An edge body is not something anyone recalls in words.

*Cost.* Both lanes are corpus-proportional in measured ways: `fact_token` holds 701,358 rows at
50,097 live facts and rebuilds in 4,161 ms, and `repair --apply --tokens` runs from the
session-start child on every session. **E3 has now bounded the input**, and the framing this
section previously carried — "cheap insurance" — was wrong: at 18,307 call-shaped sites against
~5,308 live facts in this repository, a single repository's call graph is the same order as the
entire current corpus, and a store indexing several checkouts multiplies that. The exclusion is
**load-bearing**. See §8's E3 row for what that number does and does not establish.

The exclusion belongs where the decision already lives — `EngramDatabase.RebuildFactFts` is "the
one implementation of what belongs in the index", and `FactTokenIndex.Add`/`Remove` are its
counterparts. Key the exclusion on the edge-predicate set from §4.1, **not** on
`scope = 'code'`: `about` and `declared-as` are code-scoped, are useful in lexical recall today,
and must keep working.

### 4.5 Edges must also leave the recall candidate scan — and this is the larger exposure

**Excluding edges from the lexical indexes stops them *matching*. It does not stop them being
*read*.** Verified:

- `FactStore.ReadLive` (`FactStore.cs:283`) selects every row `WHERE f.valid_to IS NULL`, joined to
  `entity`, with an optional *scope* filter and **no predicate filter**.
- `RecallEngine.BuildCandidates` (`RecallEngine.cs:405`) iterates that list in three loops
  (`:417`, `:430`, `:443`) with **no skip condition**.

So with §4.4 fully implemented, a 3–6× corpus still makes recall's candidate construction 3–6×
more work per call, and the primer's topic histogram — already ~40 ms at 50,097 facts and
described as scanning every live fact — scales with it. D58 records recall as paying for the match
set rather than the corpus; edges in `ReadLive` would silently undo that for every query, including
ones with nothing to do with code. **A store that indexed two repositories could regress the
session-start primer past its measured envelope without a single failing test.**

The requirement: the retrieval path must not read edge facts at all. Three rules on how:

1. **`ReadLive`'s default must stay "everything".** It has 21 call sites and the backup/journal
   path is among them. A default that excluded edges would silently drop them from
   `backups/facts.jsonl`, which is a recovery tool losing authored rows — far worse than the
   latency it was added to fix. Exclusion is **opt-in at the reader**, never a new default.
2. **Every one of the 21 call sites is classified before any of them changes**, into *must see
   edges* (backup, journal, replay, `repair`'s from-scratch recomputations, `CodeIndexer`'s own
   diff) and *must not* (recall, primer, vector index, `fact_token` maintenance). A classification
   the Implementor cannot make on the spec's face is a spec-defect to report back, not a judgment
   call to take. Report the classification with the diff.
3. **Filter by predicate, in SQL, not in the loop.** A `WHERE f.predicate NOT IN (…)` on the
   retrieval reader is what avoids the transfer; filtering inside `BuildCandidates` still pays for
   every row crossing the boundary, which is the cost being removed.

**Falsification:** seed a store with N ordinary facts and 5N edges, and assert the recall path's
row count is N. Shown to fail before the change, per D60 — and note that a timing assertion alone
would not hold this, because the ratio collapses to nothing on a small fixture. Count rows.

**On an explicit volume bound: not now, and not a cap.** D53's lesson is that bounding enumeration
without reporting partiality turns a slow scan into a destructive one — there, a truncated file
walk read as a repository whose files were all deleted. A cap on edges per symbol has exactly that
shape: a truncated `callers` list is indistinguishable from *nothing calls this*, which §3.4
already forbids as an answer. If Phase 3's measured volume demands a bound, it must be a bound that
**says it was hit**, at the query surface, in the same breath as the answer. Specify it against a
real extractor's numbers, not against E3's proxy.

### 4.6 `imports` becomes edges, and the `edge` table is superseded

`imports` converts from one joined-string fact per file to one object-bearing fact per module,
the object being the module name as written (§4.2). This is the phase's only visible behaviour
change and it exercises the whole substrate on a predicate that already has data.

Because `imports` fact bodies change shape, existing stores must re-extract. Bump
`CodeAnalyzer.AnalyzerVersion` 2 → 3; that is the mechanism already in place for exactly this
("the bump is what makes existing stores re-read under the better extractor",
`CodeAnalyzer.cs:26-28`). Do **not** bump `CodePaths.GrammarVersion` — addressing is unchanged.

Note that `DeepTier.Merge` re-emits the `imports` candidate itself, from `analysis.Imports`
(`DeepAnalysis.cs:125-133`), so the conversion has to happen in **both** `CodeAnalyzer.AddImports`
and `Merge` or tier-1/2 files will keep writing the joined-string form. See §5.1's warning about
`Merge` — it is the same trap.

`imports` is also the **cheap rehearsal for §4.5**: it is the first predicate to become
object-bearing, at a volume small enough to be safe, so the reader classification and the
predicate filter should be built and tested here rather than first meeting real volume in Phase 3.

The `edge` table is left in the schema, unused, and marked superseded in a comment pointing at
D70. Dropping it is a separate change with no benefit here, and `BackupStore.cs:53`'s count would
need attention.

---

## 5. Phase 3 — Calls and query-time resolution

**Ships:** *who calls X*. This is the expensive phase. **The fork that previously blocked it is
resolved** (§5.2).

### 5.1 Extraction — three components, not one

E4 is resolved: **no component models invocations today.** `DeepSymbol` carries
`Name/Kind/Declaration/Doc/Scope/Params`; `DeepAnalysis` carries `Symbols` and `Imports`; and the
Roslyn sidecar's `Program.cs` contains no occurrence of `Invocation`, `Call`, `Symbol`, or
`Reference`. So Phase 3 extends all of:

1. `DeepAnalysis` — a new `IReadOnlyList<DeepCall> Calls` member, and a `DeepCall` record carrying
   at minimum the enclosing symbol's fragment, the callee name as written, any receiver/qualifier,
   and the line.
2. `src/Engram.Sidecar.Roslyn/Program.cs` — walk invocation expressions and emit them. The JSON
   contract between core and sidecar changes, so both sides move together; per D1 the sidecar
   still never opens the database.
3. `TreeSitter.Analyze` + a `CallQuery` registry column — same pattern as `DeclarationQuery`,
   one query per language, zero extractor edits.

**Roslyn must not resolve either.** It can bind a call to a declaration and the others cannot;
letting it do so would make C# edges a different *kind* of thing from every other language's, and
§5.2 makes resolution derived state that no tier is permitted to bake in. The sidecar emits the
name as written, like everyone else. Its extra fidelity belongs to *which* names it finds and how
accurately it attributes them to an enclosing symbol, which is what the tier stamp (§7.1) reports.

**The trap in `DeepTier.Merge`.** `Merge` does not append to tier 0's candidates — it *replaces*
them, keeping only the file-level `about`:

```csharp
var merged = tierZero
    .Where(c => c.EntityPath == fileEntityPath && c.Predicate == "about")
    .ToList();
```

Any `calls` candidate produced at tier 0, or produced at tier 1/2 but not explicitly re-emitted
inside `Merge`, is **silently discarded** — no error, no count, and a `callers` query that simply
returns less than it should. Every new predicate must be emitted from inside `Merge` for tier-1
and tier-2 files. A test asserting a `calls` candidate survives `Merge` is required, and must be
shown to fail when the emit is removed.

Tier 0 gets **no** call extraction: a regex that matches `foo(` cannot distinguish a call from a
declaration, a comment, or a string, and a navigation answer that is mostly false is worse than one
that says it does not know.

**One fact per (caller, `calls`, callee) — not one per call site.** Three calls to the same target
from one function are one belief; the line numbers belong in the body and `evidence`. One fact per
site would make `ux_fact_edge_live` useless and multiply the store by the average call count. This
is also why E3's site count is an **upper bound on facts, not a fact count** (§8).

### 5.2 Resolution happens at query time — RESOLVED

I escalated this fork to the Ultra-Advisor rather than settling it: given incremental blob-SHA
indexing that never revisits an unchanged caller, should cross-file edges resolve at index time, at
query time, or dually — and if dually, is stale resolution repairable derived state under D8 or
authored belief content that may not be rewritten?

**Ruled: resolve at query time.** `object_id` is a name-keyed symbol entity — the callee as
observed — never a resolved declaration. Confidence ~85%. I accept it, and it corrects my own
lean, so the reasoning is recorded rather than just the outcome:

- My hybrid (write the best-resolved target *and* keep the unresolved name, treating staleness as
  D8-repairable) had the right instinct about *staleness*, and the wrong **storage shape**. The
  append-only invariant lists **object** among immutable belief content. A resolved target baked
  into `object_id` therefore cannot be repaired at all — it can only be churned through
  supersession, which manufactures belief revisions for an event nobody observed. That is the same
  objection §5.3 makes to closing a fact at a moment nobody observed, arriving from the other end.
- Split along what each thing actually is. The **edge** is observed belief content: *this caller
  calls something spelled `Foo`.* That claim never rots, and changes only when the caller's own
  file changes — which is exactly when the indexer revisits it. The **name→declaration binding** is
  derived state, so it lives in a query-time join and is recomputable by definition.
- This dissolves the index-time defect entirely rather than mitigating it. When `B` moves file,
  every caller's answer is correct on the next query with no re-index, no repair pass, and no
  supersession churn.

**The join binds on the leaf name, and the qualifier ranks rather than filters.** §4.2 keeps callee
names as written, so the join's left side may be `join`, `path.join`, or `os.path.join` while the
right side is a declaration whose leaf name is `join`. Match on the leaf; use the qualifier to
order candidates, never to exclude them — a qualifier is a receiver expression, not a namespace,
and filtering on it discards the true target whenever the receiver is a local variable. This is the
same rule as *ambiguity is reported, not resolved by preference*, applied one level down. **It also
depends on §3.5 being fixed**: while `entity.name` is `file.cs#Frag`, a leaf-name join matches
nothing and `callees` returns empty, which reads as *this calls nothing*.

What query-time resolution costs, stated plainly:

- **Ambiguity is paid per query, and must be reported rather than resolved by preference.** A
  common name binding to several declarations returns all of them, marked ambiguous. Picking one by
  ranking would make navigation a relevance question, which §3.4 already rejects.
- **`callers(X)` needs no join at all** — resolve `X` to its name entity and select facts whose
  `object_id` is that entity. It is `callees` that joins, to enrich each name with a declaration
  site. Worth knowing before anyone assumes query-time resolution is the slower direction: the
  question most often asked is the cheap one.
- Both directions go through Phase 1's single `SymbolResolver` seam (§3.4). No second resolver.

**Deliberately deferred:** a `fact_token`-style side index over name→declaration, if the join is
measured too slow. **Gated on E3's Phase-3 re-measurement** and on a timing, not a plan (D60). Do
not build it speculatively; the join is over `entity`, which is small relative to `fact`, and one
unmeasured index is a rule while two are a preference.

### 5.3 What needs no new design

Deletion and movement of a *call site* is already handled. The edge is subject-anchored on the
caller's symbol, which lives in the caller's file, so the file changing is exactly when
`ProcessFile`'s candidate diff runs — the vanished edge is unmatched and `FactStore.Forget` closes
it with reason `source changed (<sha8>)`. Only `regenerable` facts are touched (D19). The dispatch
asks whether to "close the fact vs. leave it live-until-reindex"; the existing mechanism already
closes it at the next index of that file, which is the correct and only coherent answer — a fact
cannot be closed at a moment nobody observed.

Movement of a call *target* now also needs no new design, which is §5.2's whole point: the edge
does not mention the target's location, so nothing about it went stale.

---

## 6. The D6 gate — an override, not a satisfied condition

The dispatch asks me to record Jim's direct request as "legitimate grounds to open the D6/M3
gate." I will record it, but **not as the gate being met**, and the distinction is load-bearing.

D6 holds M3 behind *evidence that missed recalls are substantially code-structure questions*. That
gate exists because the spec's §1.2 says every predecessor died when **the LLM never called the
memory tool** — it is a question about model behaviour. A user asking for code navigation is
evidence that *the user* wants it. It is not evidence that *the model* will call it, which is the
only thing D6 ever asked about. Recording the request as satisfying the gate would corrupt a gate
that has already caught one wrong conclusion (D43), and in the direction that looks like success.

The honest record is: **D6's gate is unmet, and is being overridden by the product owner.** That is
entirely legitimate — Jim owns the product and a gate is not a veto over its owner — but it must be
written down as an override so the risk stays visible and the gate stays usable for the next thing
it guards.

Two things materially reduce the risk, and both belong in the record:

1. **D6's own stated rationale is partly retired.** It gates M3 as "the most expensive milestone,
   and the one carrying the D1 sidecar risk." The sidecar exists, works, and is covered by tests
   (`src/Engram.Sidecar.Roslyn/`, `RoslynSidecarTests.cs`). That risk was paid down; M3 in 2026 is
   cheaper than M3 as costed when D6 was written.
2. **The override is made answerable rather than blind.** Phase 1 ships a navigation surface for
   a fraction of M3's cost, and Phase 4 instruments it. Within weeks there is real data on whether
   the model reaches for `engram_navigate` — which is the evidence D6 wanted, obtained by building
   the cheap end of the thing rather than by waiting for telemetry that D44 shows cannot currently
   attribute a tool call to a session anyway. The Phase-1 scope cut (§3.0) does not weaken this and
   arguably sharpens it: C# and TS/JS in a C# repository is the population whose behaviour matters.

**Adoption data is not readable until §3.5 is fixed.** Every `defined_at` answer is currently a
substring match, so low-quality results would depress usage for a reason that has nothing to do
with whether the model wants the surface. Do not read the first adoption numbers as D6's answer
until the exact-match tier works.

**Still unconfirmed by Jim.** This spec records the override on the Orchestrator's report of his
request; I have no direct confirmation from him, and D71 has now been committed to the decision
log on that basis. If the attribution is wrong, D71 needs a correcting entry — not a silent edit,
since the plan is append-only in practice and a decision that was recorded on a mistaken premise is
itself worth recording.

---

## 7. Phase 4 — Trust surface and measurement

### 7.1 Extraction tier is stamped, never inferred

Every navigation answer marks which tier produced each edge, so the model can weight it. Two
things this must **not** be:

- **Not `learned_via`.** D19's tiers are `stated` / `observed` / `inferred` under a closed `CHECK`,
  and every code fact is `observed` regardless of tier (D24, explicitly). `learned_via` answers
  *what kind of claim is this*; extraction tier answers *how deeply was it parsed*. Widening that
  `CHECK` would conflate two orthogonal axes and break D19's guarantee that provenance is
  authored testimony rather than a parser detail.
- **Not derived at query time from `LanguageRegistry.Resolve(path).Tier`.** The registry says what
  a language is *entitled* to; a file whose grammar was not installed silently fell back to tier 0
  (`CodeIndexer.cs:415-418` returns quietly). Deriving the tier would report tier 1 for a fact
  regex produced. This is the `fact_token` lesson exactly: **readiness is a stamped version, never
  a probe.**

So: stamp it at write time. `fact` gains a nullable `analyzer_tier INTEGER` (migration v14),
written at insert and never updated, like every other piece of belief content. Nullable because
facts written before it existed genuinely have no answer, and guessing one would be inventing
provenance — D19's prohibition, restated.

**This is why Phase 1 reports no extraction tier** (§3.4). There is no honest source for it before
this migration, and the only query-time shortcut is the one forbidden above. Phase 1 says so in its
answer rather than omitting the field, because an absent tier reads as tier 0.

Also stamp `CodeAnalyzer.AnalyzerVersion`, or accept that a tier-1 fact from an older query set is
indistinguishable from a current one. **Open, deliberately** — I do not have evidence that this
matters yet, and one unmeasured knob is a rule while two are a preference.

### 7.2 Telemetry

A `navigate` telemetry kind, per D55/D56:

- Its own kind — **never folded into `recall`**, which D18 and D43 read to answer whether the model
  reached for memory. Inflating that number is the exact defect D43 traced back.
- Emitted after the answer is produced, recording relation, whether anything was found, and the
  tiers of what was returned.
- Reports both ends if it is ever long-running (D55). It should not be.

This is what makes §6's override answerable, so it ships in the first phase that has a surface to
instrument — pull it into Phase 1 if the cost is trivial, which it appears to be.

---

## 8. NEEDS-EVIDENCE

Design questions I could not settle by reasoning. **E1, E2, E5 and E6 were run by the Implementor
during P0 and are resolved; their outcomes are recorded here rather than removed, because a spec
that deletes its own evidence trail cannot be audited.**

| # | Question | Status |
|---|---|---|
| **E1** | Do pinned tree-sitter grammars for Python/Go/Rust/Java compile under `scripts/fetch-tree-sitter.sh` and load at the core's ABI? | **RESOLVED.** All four compile, load and query cleanly. **Correction to this spec's earlier claim:** it said "Rust and Java have external scanners, Go is plain." Measured at the pinned versions — java v0.23.5 — **Java has no external scanner; only Python and Rust do.** The wrong claim did not change E1's verdict, but it must not be carried into build tooling or CI gating. |
| **E2** | Does each grammar's node vocabulary support a declaration query at grammar-v2 fragment depth (D48)? | **RESOLVED** by the same spike; no language needs addressing work, so none moves out of Phase 1. |
| **E3** | How many `calls` edges does a real repository produce, against a store's live-fact count? | **PARTIALLY RESOLVED — see the note below.** Proxy measurement on this repository: 18,307 call-shaped matches (~3.4×) and 32,784 including declarations (~6.2×) against ~5,308 live facts. |
| **E5** | Does `NativeLibrary.Load` of four more grammars hold under Native AOT in the published binary? | **RESOLVED.** Holds in a published AOT binary — the only honest answer, per D45's IL3000 history. |
| **E6** | Does adding two partial indexes (§4.1) change any query plan on the recall path? | **RESOLVED.** Paired plan and timing, per D60; no measurable cost. |
| **E7** | What are the actual `entity.name` values for symbols in a real store, and how many distinct symbol names collide once §3.5 is fixed? | **OPEN, cheap.** `SELECT path, name FROM entity WHERE kind='symbol' LIMIT 20;` against a store with this repo indexed, then `SELECT name, COUNT(*) FROM entity WHERE kind='symbol' GROUP BY name HAVING COUNT(*) > 1 ORDER BY 2 DESC LIMIT 20;`. The first **confirms §3.5's chain end-to-end** — the one link I inferred rather than read is `fileEntityPath`'s exact shape. The second sizes the ambiguity the exact tier will surface, which decides whether `defined_at` needs a disambiguation story beyond *return all and mark ambiguous*. |

**E4 is resolved** and folded into §0 and §5.1: no component carries invocation data, so Phase 3
extends `DeepAnalysis`, the Roslyn sidecar, and the tree-sitter extractor together.

**E1/E2 were measured but their subject is now deferred** (§3.0). Both hold as evidence about the
grammars; neither proves the registry-row claim end-to-end, and both age with the pins.

**What E3's number does and does not establish.** It is enough to settle §4.4 — one repository's
call graph is the same order as this store's entire corpus, so the lexical exclusion is
load-bearing rather than hygiene, and §4.5 follows from the same magnitude. Three things it does
**not** settle, and none should be treated as measured:

- **Sites are not facts.** §5.1 specifies one fact per `(caller, calls, callee)`, so repeated calls
  to one target from one function collapse into one belief. 18,307 is an **upper bound**; the true
  edge count is lower by the average duplicate-call factor, which is unmeasured.
- **Per repository, not per store.** A store indexing four checkouts pays roughly four times this.
  The number that decides any future bound is edges-per-store, and nobody has one.
- **A grep proxy, not the extractor.** Phase 3's extractor does not exist yet, and its recall and
  precision against these matches are unknown in both directions — it will miss constructs the
  regex caught and catch ones it did not.

**E3 must be re-run against the real extractor at the end of Phase 3**, reporting distinct
`(caller, callee)` pairs per repository and the store total. That number, not this one, is what any
volume bound (§4.5) or side index (§5.2) is specified against.

---

## 9. What must not change

- `fact_relation` — schema-v10 conflict verdicts, `CHECK`-constrained to
  `supersedes|conflicts_with|scoped|not_conflict`. Unrelated to code structure. Do not touch it.
- `learned_via` — the closed `CHECK` stays closed (§7.1).
- `EngramDatabase.Open` — no new ad-hoc connection opening; pragmas are connection-scoped.
- `FactTokenIndex.Add`/`Remove` and `RebuildFactFts` — remain the only chokepoints. §4.4 changes
  what they index, never who decides it.
- **`FactStore.ReadLive`'s default result set** — everything live, edges included. The backup
  journal reads through it and a recovery tool must not lose rows to a latency change (§4.5).
- **`CodePaths.ForSymbol`'s output** — `{filePath}#{fragment}` is the address, and §3.5 changes
  `entity.name` only. A path change forces a re-index of every store and breaks D2's rename
  identity; the defect is in the *name*, which is denormalized display metadata.
- Non-`regenerable` facts — extraction never supersedes testimony (D19).
- `CodePaths.GrammarVersion` — not bumped by this spec; a bump forces a full re-index everywhere.
- **`object_id` never holds a resolved declaration** (§4.2/§5.2). It is name-keyed, it is immutable
  belief content, and resolution is derived state computed on read.
- **`SymbolResolver` stays one implementation** (§3.4). Phase 3's join is the same lookup
  `defined_at` performs; a second one diverges on first tune.
- D20 — nothing here requires, invokes, or assumes an external indexer.
- Tier 0 must keep working for every language, with every tier above it an upgrade that may fail
  quietly. An optional tier that can fail an index run is not optional.

---

## 10. Decision addendum

**D70, D71 and D72 were appended to `docs/engram-implementation-plan.md` during P0.** The drafts
that were here have served their purpose and are not restated; read them in the plan, which is
authoritative. Two notes survive:

- **D71 records an owner override on an unconfirmed attribution** (§6). If Jim did not make the
  request attributed to him, D71 needs a correcting entry rather than an edit.
- **D70's cost paragraph should gain a pointer to §4.5** the next time the plan is touched for
  another reason. It states the index cost of object-bearing facts and does not mention the
  unfiltered `ReadLive` scan, which is the larger exposure and was found later. Not worth a
  decision entry of its own until Phase 3 measures it.

§3.5 does **not** get a decision entry. It is a defect with a fix, not a decision between
alternatives — the plan records arguments and measurements, and "the name field held the wrong
string" is neither.
