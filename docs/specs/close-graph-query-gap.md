# Closing the graph-query gap — verdicts and design

Design only. Nothing here was executed; no code was edited. Follows on from a prior
comparison against a reference code-comprehension tool, which established the gaps. This
document decides which are worth closing **for engram's actual job — code lookups for a
coding agent** — and designs only the ones that survive that test.

**Headline: three of the four items come back `skip` or `close-later`. The one thing worth
doing now is the cheapest item on the list (tier-1 grammars), and the most valuable
finding is that the gap Jim was pointed at (traversal depth) is the wrong target.**

> **Amended 2026-08-27 — see §8.** Jim then stated the product goal: `engram_navigate`
> should be an LLM's **first reach, ahead of Grep**, not a supplement. That reframing
> **changes the §2 verdict** by splitting it — the syntactic half moves to CLOSE-NOW and
> folds into §3.
>
> **Amended again — see §8.5.** The predicate-naming question was escalated and ruled:
> **per-language predicates**, unioned at the query surface.
>
> **Amended again — see §9.** §4's measurement ran. **H1 confirmed by shape, and the
> reprioritization it called for is a NO-GO.** §4's decision table is superseded by §9.2.
>
> **Amended again — see §10.** Two spec-defects found in review of commit `6aa2f33`:
> the version-bump language named the wrong constant (§10.1), and §8.5.3's marking
> obligation covered total misses but not **partial** ones (§10.2). Both corrected in
> place below; §10 records the reasoning.
>
> **Amended again — see §11.** Two follow-ups from the round-3 review (HEAD `3e6959f`).
> The nested-type caveat fires on **every** result and is therefore a banner, not a
> declaration — §8.5.3 item 4 is amended with a **discrimination test** that would have
> prevented it (§11.1). Qualifier spelling turns out **not** to be a third gap class to
> declare: it is a divergence between two implementations of one lookup, and the fix is
> the cheap half (§11.2).

---

## 0. The fact that changes the analysis

The comparison priced multi-hop traversal against a wrong model of the stored graph, and
so did my first guess at the correction. The real shape, established by reading the write
site (`CodeIndexer.cs:610-639`), is **bipartite**:

```
entity(kind='symbol')            entity(kind='symbol-name')
  path = src/Foo.cs#Bar   --calls-->   path = /symbol-names/Baz
```

- **Subjects** are declaration sites: `CodePaths.ForSymbol` → `{file}#{fragment}`, or a
  raw file path (`DeepAnalysis.cs:153-161`).
- **Objects** are *names as written*: every non-null `CodeCandidate.Object` is wrapped by
  `CodePaths.ForSymbolName` at apply time (`CodeIndexer.cs:619`) into
  `/symbol-names/{percent-encoded}`, kind `symbol-name`.
- `declared-as` and `about` pass `Object: null` and carry **no edge at all** — the
  declaration body is the fact's payload, not an object.

So there are exactly **two** object-bearing predicates, `calls` and `imports`, and the
graph they form is one hop deep by construction:

> **`decl --calls--> name` is stored. `name --is-declared-at--> decl` is not.**

The return leg is not an edge; it is `SymbolResolver.Resolve` — a `LIKE`/`COLLATE NOCASE`
scan over `entity` where `kind='symbol'` (`SymbolResolver.cs:95-99`) — plus leaf-name
matching. `CodeCallGraph`'s own class doc (`57-61`) states why: *"a `calls` fact's object
is the callee as written — `join`, `path.join`, and `os.path.join` are three distinct
symbol-name entities that all answer 'who calls `join`'"*.

Three consequences, all load-bearing below:

1. **A single recursive CTE cannot walk this graph.** Every second level of a walk is a
   resolver call, not a join. My interim guess that the graph was uniformly name-keyed and
   therefore cheap to traverse was **wrong** — recorded here so it is not re-derived.
2. **Identity is weak by design, and weakest at exactly the names an agent asks about.**
   `MatchingSymbolNames` (`CodeCallGraph.cs:183-195`) matches on *leaf*, so `Get`, `Add`,
   `Run`, `Dispose`, `ToString` collapse unrelated subgraphs into one node.
3. **The existing cost is already shaped like a scan, not a lookup.** `callers` reads
   *every* `symbol-name` entity and filters leaves in C#; `callees` calls
   `SymbolResolver.Resolve` **once per callee** (`CodeCallGraph.cs:104-137`). *(§9 measures
   this and corrects which half dominates; §11.2 shows this same scan is what `implementers`
   should have been using and is not.)*

---

## 1. Transitive / multi-hop traversal

### Verdict: **SKIP** for general traversal. The capability is already reachable, and
### automating it would make it worse.

The ask behind "multi-hop" is nearly always one question: *what breaks if I change X* —
an `affected`-style query. A coding agent can answer that today by calling `navigate
relation=callers` on X, then on each result. That composition already exists, and rung 1
of the ladder applies: **the agent is the traversal loop.**

That is not a grudging substitute, it is the better design *given consequence 2*. At
depth 1 the leaf-name over-approximation is tolerable because `CallRankSignal`
(`SameFile`, `QualifierAgreement`, `ImportFilenameMatch`, `SameRepo`, `NameOnly`) orders
the candidates and a caller can eyeball them. At depth 2 through a common leaf, the walk
merges unrelated call chains, and **a CTE cannot tell which of the eight `join`s it
should not have followed — the agent can.** Automating the walk removes the only
component in the system with the judgment to prune it, and returns a longer, more
confident, more wrong answer. That is the failure mode this repo names elsewhere: a
result that reads as coverage when it is noise (D44's `coverage: high` over six
stem-matched engineering notes).

A reference tool can traverse deeply because it earned the right to: build-time
Jaro-Winkler dedup with optional LLM resolution, plus a hub-degree guard that refuses to
transit high-degree nodes. engram has **none** of the
first and **no degree data of any kind** — confirmed absent, a grep for
`degree|fanin|fan_in|CountCalls|FanOut` over `src/` and `tests/` returns zero hits.
Shipping depth without identity resolution or a hub guard copies the feature and not the
thing that makes it work.

**What is actually missing is not depth — it is the signal that tells a caller when
depth-1 is already untrustworthy.**

### Counter-proposal (close-later, small): surface hub-ness on `callers`

`MatchingSymbolNames` already materializes every matching `symbol-name` entity before it
queries. When more than one distinct symbol-name path matched a single leaf — i.e. the
query name is ambiguous — say so on the result, in the shape the freshness marker already
established (`EngramMcpTools.cs:790-806` `AppendFreshness`):

> `note: 'Get' matched 14 distinct callee spellings (Get, cache.Get, os.Get, …); these
> callers are matched by leaf name and may include unrelated symbols.`

Properties that make this the right size:
- **No new state.** No degree table, no schema change, no migration. The count comes from
  a list the query already builds.
- **No new invariant surface.** It is presentation, alongside `[stale]`/`[missing]`, and
  follows the same rule those do — *say what the answer does not cover* (`48f1a0c`).
- **It composes with the agent-driven loop.** An agent that knows hop 1 was ambiguous
  knows not to take hop 2, which is exactly the pruning a CTE could not do.

> **§8 raises this item's priority**; **§9.4** corroborates it with latency; **§10.2**
> generalizes its discipline into a rule that covers partial answers as well as ambiguous
> ones; **§11.2** reuses this exact note as the marking that makes the `implementers` fix
> safe.

### If multi-hop is ever revisited

D72 (plan `5229-5258`) refuses to store a resolved call target, because the blob-SHA skip
(`CodeIndexer.cs:265-282`) never revisits an unchanged file, so a binding stamped into
file B's fact rots when the symbol it points at is renamed in file A. **That reasoning
binds facts, not derived indexes.** A `name → declaration` binding materialized from the
`entity` table would be *derived state* — regenerable, rebuildable by `repair`, and
self-healing across renames precisely because `entity` rows for a renamed file *are*
rewritten while file B's facts are not. So such an index is permitted by D8 and does not
breach D72.

That is an argument that multi-hop *could* be built within the invariants. It is **not**
an argument that it should be — consequence 2 stands regardless of how fast the walk is.

---

## 2. Relation coverage (4 vs a reference tool's 11–16)

> **Superseded in part by §8.** The per-relation triage below stands; the *cost* claim for
> `implements`/`inherits`/`contains` does not, and the predicate naming is settled by
> §8.5.

| reference relation | engram | verdict | why |
|---|---|---|---|
| `inherits`, `extends` | absent | **CLOSE-LATER** → *see §8* | The strongest case on the list. |
| `implements` | absent | **CLOSE-LATER** → *see §8* | Same. |
| `contains`, `method` | absent | **CLOSE-LATER (cheap tail)** → *see §8* | Ride the same change. |
| `references` | absent | **SKIP** | This is grep, and grep is good at it. High volume, low signal, and it would swamp `calls` in the same table. |
| `imports_from`, `re_exports`, `dynamic_import`, `requires` | flat `imports` | **SKIP** | Module-graph refinements. The agent's question is "what does this file pull in", which flat `imports` answers. |
| `mixes_in`, `embeds` | absent | **SKIP** | Ruby/Go constructs; engram has no tier-1 grammar for either. |
| `uses` | absent | **SKIP** | Undefined edge between `calls` and `references`; adds ambiguity, not answers. |
| `indirect_call` | absent | **SKIP** | Requires dataflow analysis neither tier does. |

### The case for `implements` / `inherits`

"What implements `IFoo`" is a question **Grep answers wrongly**: `grep ": IFoo"` finds
direct declarations, misses indirect implementers, misses multi-interface lists formatted
across lines, and false-positives on `IFooBar`.

**But the cost is higher than the comparison assumed** — *this is the claim §8 corrects*:

> The Roslyn sidecar does **not** hold a semantic `ISymbol`. `Program.cs` emits
> `id, name, kind, declaration, doc, scope?, params`, and its containment inspection is
> **syntax-level only** — `ScopeOf` walks `parent is BaseTypeDeclarationSyntax`
> (`Program.cs:82`, `:300`, `:315`). There is no `ContainingType` / `BaseType` /
> `AllInterfaces` anywhere, and no compilation from which to get them.

### Design sketch

- **New predicates**: per §8.5 — `inherits` / `implements` where the grammar decides,
  `derives-from` where it cannot, plus `contains` (type decl → member name) everywhere.
- **Object side keeps the existing scheme.** All of them use `CodePaths.ForSymbolName`
  exactly as `calls` does — names as written, resolved at query time. A new predicate that
  resolved eagerly would be the D72 breach that `calls` avoids. **Names as written implies
  qualifier-tolerant matching at read time** — see §11.2, which is where the shipped
  implementation diverged.
- **Invariant compliance**:
  - *Append-only facts*: new predicates are new fact rows. Nothing existing is mutated.
  - *`analyzer_tier`*: stamped at write, monotone upgrade rule unchanged.
  - *Derived-vs-authored*: all code facts are `Regenerable: true` (D8).
  - ***Re-index path already exists — bump `CodeAnalyzer.AnalyzerVersion`, NOT
    `CodePaths.GrammarVersion`.*** `CodeIndexer.CurrentVersion` is
    `$"{CodePaths.GrammarVersion}.{CodeAnalyzer.AnalyzerVersion}"` (`CodeIndexer.cs:78-84`),
    so **either** component forces the full re-read — but they mean different things and
    only one is right here. `GrammarVersion` tracks `docs/engram-path-grammar.md`, the
    authority for **addressing**; new predicates change *what is observed*, not *how
    subjects are addressed*, so this is an analyzer change. See **§10.1** for the rule and
    the precedent. **No migration is needed and none should be written.**
  - `ux_fact_edge_live` already enforces one live edge per (subject, predicate, object).
- **New `navigate` relations**: `implementers`, `implements`, `members` — each unioning
  the stored predicates per §8.5.2, and each obeying §8.5.3's marking rules including
  **item 4** (partial answers) and its **discrimination test** (§11.1). Retire `neighbors`
  in the same change.

### Verdict: **CLOSE-LATER** — *revised by §8 to a split: syntactic CLOSE-NOW, semantic
### closure CLOSE-LATER.* **SKIP** the other nine stands unchanged.

---

## 3. Tier-1 language coverage (TypeScript/JavaScript only)

### Verdict: **CLOSE-NOW.** Cheapest item on the list, broadest reach, no design risk.

The tier-1 path is genuinely grammar-parameterized:

- Dispatch: `runtime.Analyze(LanguageRegistry.Resolve(rel), rel, content)`
  (`CodeIndexer.cs:482`).
- The analyzer is generic over a `LanguageDefinition` (`TreeSitter.cs:135-228`), taking
  its queries from `language.DeclarationQuery` / `ImportQuery` / `CallQuery`.
- Languages are **table rows**: `LanguageRegistry.cs:217-386`, TypeScript at `:249-322`,
  JavaScript at `:323-365`.
- Grammars are compiled to `$ENGRAM_HOME/lib` by `fetch-tree-sitter.sh` and side-loaded —
  **no NuGet reference**.

So a new language is **a grammar binary plus one table row carrying three queries** (four,
with §8's inheritance query). No new C# analysis code.

Everything else falls back to tier-0 regex, which produces `declared-as` and `imports` of
markedly lower fidelity and **no `calls` at all** — `calls` is tier-1/2 only. So for every
unlisted language, `navigate relation=callers|callees` returns nothing.

**Which languages**: alongside engram (C#, tier 2), the indexed projects include
`claude-tui-line`, `claudetools`, `wrangl`, and `tower-defense`. **Python → GDScript →
Go/Rust if demanded.**

**Version-bump caveat — corrected.** Adding a language row bumps
**`CodeAnalyzer.AnalyzerVersion`**, not `GrammarVersion`. Both are components of
`CurrentVersion`, so either forces a full whole-repo re-read of every enrolled repo — that
consequence was stated correctly here before; only the constant was named wrongly.
`GrammarVersion` moves **only** when `docs/engram-path-grammar.md` itself changes, and
adding a language does not: a Python symbol is addressed `src/foo.py#bar`, an existing
form. **The precedent is exact**: `224d38a` *"Tier 1 runs: tree-sitter binding, registry
queries, indexer integration (D47)"* — the commit that introduced tree-sitter language
support — bumped `AnalyzerVersion` 1→2 and left `GrammarVersion` alone. See §10.1.

*(The one exception: if a new language needs a path form the grammar document does not
express — a novel scope-chain or collision rule — then the document changes and
`GrammarVersion` moves with it. That is a property of the language's addressing, not of
adding a language, and it is checkable by reading the grammar doc before writing the row.)*

**Measured** (§9): a full re-index is **982 ms at 5,000 functions and 9,559 ms at 50,000**
— roughly linear, small enough that batching grammars into one bump is a convenience
rather than a necessity.

**Note for whoever adds the next row**: Python and GDScript both have nested classes, so
both would set `NestedTypeEdgesDropped: true` and the flag would stand at 5 of 5. That is
the observation §11.1 turns into a rule — do not add the row's caveat wiring without
reading it first.

---

## 4. NEEDS-EVIDENCE: navigate latency at 50k facts

> **RESOLVED — see §9.** Results in `docs/specs/navigate-latency-results.md`. **The
> decision table at the end of this section is superseded by §9.2.** The measurement
> design below stands and was followed; keep it as the template for §9.5's re-measure.

> **H1 — `callers` cost is proportional to the number of *distinct callee names in the
> store*, not to the number of matches.**
>
> **H2 — `callees` cost is proportional to the number of callees**, because
> `SymbolResolver.Resolve` is called once per callee inside the loop.

### Measurement design

**Corpus.** `FixtureGenerator.cs` seeds `Generate_Base5k()`, `Generate_Deep50k()`,
`Generate_Broad50k()`. **Verify first that they seed `kind='symbol-name'` entities and
live `calls` facts** — a fixture that seeds facts without object-bearing code edges times
an empty scan and reports that everything is fast. *(Done, within 2% at both scales.)*

**Harness.** Drive the **published binary**, not `dotnet test`.

**Protocol**, each rule paid for by a prior mistake in this repo:
1. **Alternate the arms every iteration.**
2. **Calibrate by running the same binary against itself.**
3. **Subtract the process-start floor** with a `probe` arm.
4. **Time through a file, never a pipe**, if anything on the path forks.
5. Set `ENGRAM_HOME` or pass `--home` on every invocation.

**Arms** — no-match / distinctive / hub.

**Also capture** `EXPLAIN QUERY PLAN` — but **pair every plan finding with a clock.**
*(This was the one protocol item the run skipped; §9.3 explains why it is a prerequisite
for any fix, though not for the verdict.)*

### Decision rule — **SUPERSEDED BY §9.2**

*Kept for the record. The "H1 confirmed" row fired correctly on shape and pointed at the
wrong action; see §9.3.*

| observation | conclusion |
|---|---|
| flat across arms and scales | No cost problem. |
| no-match arm scales with corpus (H1 confirmed) | `MatchingSymbolNames` is a floor-shaped scan. **Index or computed-leaf column becomes the priority.** |
| hub arm scales with matches only, floor flat | Match-proportional and acceptable. |
| `callees` scales with callee count (H2 confirmed) | The per-callee `Resolve` loop is the target — mind the 32,766 SQL-variable ceiling. |

---

## 5. Summary of verdicts

| # | Gap | Verdict | One-line reason |
|---|---|---|---|
| 1 | Multi-hop traversal | **SKIP** | The agent is already the traversal loop, and it has the judgment to prune leaf-name noise that a CTE cannot. |
| 1b | Hub/ambiguity note on `callers` | **CLOSE-NOW** (shipped in `6aa2f33`) | A caller cannot tell an over-approximation from an answer. |
| 2 | Inheritance + `contains`, **syntactic** | **CLOSE-NOW** (§8) | Rides §3's rows and version bump. Puts engram *in the running* for the question at all. |
| 2a | Semantic transitive closure | **CLOSE-LATER** (§8) | The part Grep genuinely cannot do; needs a `Compilation`. |
| 2b | The other nine reference relations | **SKIP** | Grep-equivalent, or language-specific for languages engram has no grammar for. |
| 3 | Tier-1 grammars (Python, GDScript) | **CLOSE-NOW** | A grammar binary plus one registry row. |
| 4 | Navigate latency at 50k | **DONE** (§9) | Measured. H1 confirmed by shape, magnitude small. |
| 4b | Resolver / leaf-column index work | **DEFER** (§9.5) | Under budget at 10x the real corpus, and the obvious index does not fix the measured floor. |
| 5 | Nested-type caveat fires on every result | **FIX** (§11.1) — *code change* | A flag true for every language is a constant; a constant belongs in the tool description, not on every response. |
| 5b | Cost of *fixing* the nested-type drop | **UNPRICED** (§11.1) | D48 addressing already expresses scope chains, so the drop may be a query limitation rather than a design one. Nobody has asked. |
| 6 | `implementers` misses qualified spellings | **FIX** (§11.2) — *code change* | Not a gap class to declare. `callers` and `implementers` ask one question with two different matching rules. |

## 6. Decisions I did not make

- **Whether the semantic transitive closure (§2a) is worth a `Compilation`.** Scoping is
  Implementor work; deciding to spend it is Jim's.
- **Which languages beyond Python and GDScript.**
- ~~The predicate-naming question~~ — **escalated and ruled; see §8.5.**
- **What fixing the nested-type drop actually costs** (§11.1). This is a scoping question
  for the Implementor, not a design call, and it must be asked *before* the declaration in
  §11.1 is treated as the final answer — fix-or-declare means asking the price of the fix.

## 7. Confidence and unverified assumptions

Unverified:
- **Which repos are currently enrolled**, and therefore whether Python or GDScript is the
  higher-value first grammar.
- ~~That `BaseTypeDeclarationSyntax.BaseList` is reachable without a `Compilation`~~ —
  **settled by implementation**: `6aa2f33` shipped syntactic inheritance edges without one.
- **H2 remains unmeasured** — see §9.5.
- **Whether nested-type extraction is blocked by addressing or only by the tree-sitter
  query** (§11.1). Stated as unknown rather than assumed either way.

---

# 8. Amendment — "first reach over Grep"

**Question put:** Jim wants `engram_navigate` to be an LLM's *first* reach, ahead of Grep.
Does that change the §2 CLOSE-LATER verdict?

**Answer: yes — and the reason it changes is not the one the question anticipates.**

## 8.1 What I got wrong

§2 conflated two different features under one predicate name and priced both at the dearer
one.

- **Syntactic inheritance** — *what the declaration line literally says.* TypeScript's
  `class Foo extends Bar implements IBaz` yields `Foo --inherits--> Bar` and
  `Foo --implements--> IBaz`. C#'s `class Foo : Bar, IBaz` yields two
  `Foo --derives-from-->` edges — one undifferentiated base list. Either shape stores
  **names as written** and needs **no `Compilation`**.
- **Semantic closure** — transitive and resolved. **This** is what needs a `Compilation`.

Two things make the syntactic version nearly free:

1. **It is the same design as `calls`, not a new one** — same `ux_fact_edge_live`, same
   weak-identity trade, same D72 posture.
2. **It rides §3** — the same `LanguageDefinition` rows, the same `AnalyzerVersion` bump,
   the same whole-repo re-read. Shipping it separately costs a *second* forced re-index
   for no reason.

## 8.2 Why "first reach" is decisive, and it is not about coverage

The weak reading — *first reach means more coverage* — is unpersuasive: `grep ": IFoo"` is
a workable partial answer. The decisive argument is the **advertised surface**:

> **There is no `navigate` relation for the inheritance question at all.** A model asking
> "what implements `IFoo`" cannot *express* that query. So it will never reach for engram
> first — it goes to Grep, correctly, because engram is not in the running.

A first-reach tool is judged on **what happens when it does not have the answer**, not on
its hit rate — and the failure here is not a wrong answer but an *unaskable question*,
which is worse, because it is invisible. This is D37's rule about `doctor` applied to a
tool surface: every question `navigate` cannot express is a standing invitation to skip
it, and skipping generalizes.

Two corollaries: **retire or implement `neighbors`** (the one case where the model *did*
reach first and got nothing), and **§1b's hub note rises in priority** — a supplement's
over-approximation gets sanity-checked against Grep; a first reach's is trusted.

## 8.3 Where the semantic version still earns its cost

Grep cannot do transitive closure at any effort. Once syntactic inheritance edges exist, a
**bounded two-hop walk over them** approximates it without a `Compilation` — genuinely
different from §1's rejected call traversal, because inheritance hierarchies are shallow,
branch narrowly, and their names are far less collision-prone than method leaves.

The semantic tier is also **the only way a `derives-from` edge becomes a known `inherits`
or `implements` one** — a clean later upgrade under `analyzer_tier`'s monotone rule.

## 8.4 Revised verdicts

| item | was | now |
|---|---|---|
| Syntactic inheritance edges | CLOSE-LATER | **CLOSE-NOW**, folded into §3 |
| `contains` | CLOSE-LATER | **CLOSE-NOW**, folded into §3 |
| Retire/implement `neighbors` | — | **CLOSE-NOW**, same change |
| §1b hub note | CLOSE-LATER (small) | **priority raised** |
| Semantic transitive closure | (bundled) | **CLOSE-LATER, now separable** |

## 8.5 RESOLVED — predicate naming (Ultra-Advisor ruling)

### 8.5.1 The ruling

> **Emit the predicate the grammar can actually justify, per language.**
>
> - Where the grammar **syntactically decides** — TS/JS and Java (`extends` vs
>   `implements`), GDScript (`extends`, single inheritance, no interfaces) — emit
>   **`inherits`** and **`implements`**.
> - Where it **cannot** — C# (`class Foo : Bar, IBaz`) and Python (`class Foo(Bar, ABC)`)
>   — emit **`derives-from`**.
> - **No `I`-prefix naming heuristic**, in any language.
> - **`navigate` unions all three predicates** at the query surface.

### 8.5.2 Why this beats what I proposed

My single-`derives-from`-everywhere recommendation was right about the constraint and
wrong about its scope. It generalized C#'s limitation onto languages that do not have it,
**discarding information the grammar hands over for free**. The ruling holds the part that
mattered — *never stamp a guess into an append-only store* — and drops the
over-application. It also sits correctly against `analyzer_tier`: a `derives-from` fact is
not a wrong belief awaiting correction, it is an accurate statement of what a syntactic
tier could observe, so §2a *appends* rather than retracting.

### 8.5.3 What this obliges the design to do

1. **`LanguageDefinition` declares which predicate its grammar emits.** GDScript declares
   `inherits` **only** — no interface construct, so an `implements` fact from it would be
   a lie.
2. **The union must be one implementation, not three call sites.** Two copies of "which
   predicates count as inheritance" diverge the first time a language is added — the same
   argument as one `VectorLane`. *(§11.2 is this rule violated one level down: not two
   copies of the predicate list, but two copies of the name-matching rule.)*
3. **A `derives-from` hit returned to an `implements` query is an over-approximation, and
   must be marked as one.** Under first-reach the result is trusted rather than checked:

   > `note: 2 of 7 results are from languages whose syntax does not distinguish base
   > classes from interfaces (C#, Python); those are base-list entries, not confirmed
   > interface implementations.`

   Without that line the union quietly converts "the parser could not tell" into "the tool
   says yes".

4. **A partial answer must say it is partial, and the declaration is static.** *(Added by
   §10.2. This corrects an asymmetry stated wrongly in the original §8.5.3. Amended again
   by §11.1, which adds the discrimination test — the original wording of this item is
   what produced an always-on banner in `3e6959f`.)*

   The original reasoning was: *an over-approximation makes a false claim, a dropped edge
   makes none, so only the first needs marking.* **That is wrong.** A returned list makes
   an **implicit completeness claim** — "these are the implementers" — and the claim lives
   in the *shape* of the response, not in any individual row. Returning 5 of 7 asserts
   something false just as surely as returning 9 of 7 does.

   Under first-reach a partial answer is **worse than a total miss**. A total miss fires
   `NotFound`'s coverage caveat and the model falls back to Grep. A partial answer that
   looks complete **stops the search** — the model has five implementers, believes it has
   them all, and never checks. Same defect as D44's `coverage: high` suppressing the
   `gaps:` line over a result that was 86% noise.

   **The rule.** You cannot mark what you do not know you dropped, so the obligation is
   *fix-or-declare*, discharged **statically**, not by per-query detection:

   > Every known class of case an extractor or query cannot represent is **either fixed or
   > declared**. A declared limitation rides the result whenever the query could have been
   > affected by it. Neither fixed nor declared is not an option, and "we did not detect
   > it" is not a defence when the class was known at authoring time.

   **Fix-or-declare means the price of the fix is asked, not assumed.** Reaching straight
   for a declaration because it is the cheaper text to write is how a fixable limitation
   becomes permanent. §11.1 is that mistake caught late: nobody had asked what fixing the
   nested-type drop costs.

   **Which channel a declaration takes is decided by the discrimination test (§11.1), not
   by default.** A limitation that can affect *every* query the relation serves is a
   property of the tool and belongs in the tool's description; only a limitation that some
   results have and others do not may ride the result.

   The classes known today, all static:
   - **Nested types dropped** (extraction side) — a property of a language's row, but
     `true` on every shipped row, so by the discrimination test it is a **tool** property.
     See §11.1. Fix cost unpriced (§5 item 5b).
   - **Generics missed by `implementers`' exact match** (query side) — a property of the
     relation, not of the data. Genuinely variable: fires only when a type-argument marker
     is present. Stays a per-result note.
   - ~~**Qualified spellings missed by `implementers`**~~ — **not a declare class.** See
     §11.2: this is one lookup with two implementations, and it is fixed, not declared.

   **Surface it conditionally, not always.** A caveat printed on every response is a
   banner people learn to skip — D37 again, and the reason `[stale]` fires per result
   rather than per call. Fire the generics note when the query or a candidate carries a
   type-argument marker. A query that cannot have touched the gap gets no line.

   **This does not reopen the asymmetry, it corrects its statement.** Over-approximation
   and under-approximation are still marked differently — an over-approximation names
   *which returned rows* are uncertain, an under-approximation names *what class is
   absent*, because there is no row to point at. What they share is that neither may be
   silent.

### 8.5.4 Contradiction fixed

§8.1's first bullet previously illustrated split `inherits`/`implements` edges using
**C#'s** `class Foo : Bar, IBaz` — the one language §8.5 says cannot produce them.
Rewritten to show TypeScript for the split case and C# for `derives-from`. Purely
illustrative, but it was the exact sentence an implementor would have copied the shape
from.

## 8.6 What this amendment does not settle

- **The exact tree-sitter query per grammar** — per-row authoring work.
- **Whether TypeScript `interface Foo extends Bar` is `inherits` or `implements`.** The
  keyword is `extends`, the construct is an interface. A narrow per-row call.

---

# 9. Amendment — §4's measurement, and the reprioritization call

Results: `docs/specs/navigate-latency-results.md`.

## 9.1 Verdict

> ## **NO-GO on reprioritizing.** Proceed as sequenced. The index/leaf-column work becomes
> ## item **4b**, deferred, with a scheduled re-measure rather than a tripwire.

## 9.2 Reason one — my decision rule was under-specified, and it misfired

§4's rule keyed on **shape** with no magnitude term. A shape-only rule fires identically on
30 ms and on 1,545 ms, and those are not the same finding. The precedent that motivated it
was `ix_fact_thread`: **1,545 ms → 105 ms**. What was measured here is **29.65 ms worst
case** against a **50 ms** budget, at ~10x this instance's real corpus.

**Replacement rule, for this and any future latency gate:**

| shape | magnitude | action |
|---|---|---|
| flat | any | no action |
| scales with corpus | worst arm **< 50% of budget** at 10x real corpus | **record the mechanism, defer, schedule a re-measure at the next corpus-growth event** |
| scales with corpus | worst arm **≥ 50% of budget**, or over budget at a realistic corpus | fix before adding corpus |
| scales with corpus | any magnitude, growth driver **unattended** | fix now — unattended growth has no re-measure point |

The last row is why this is judgment and not arithmetic: growth here is **attended** —
corpus grows when someone runs `index --apply` or a version bumps, both deliberate acts
that give a natural place to re-measure.

**No fact-count tripwire is set**, deliberately: growth is a step function, so a
count-based bound is exactly the kind `CLAUDE.md` says cannot hold. §9.5's trigger is an
**event**.

## 9.3 Reason two — the fix the rule named would not fix what was measured

For a query matching nothing, the floor is **`SymbolResolver.Resolve`'s three sequential
fallback scans**, not `MatchingSymbolNames` (which only runs after a declaration is
*found*). Against an index on `entity(kind, name)`:

| tier | predicate | served? |
|---|---|---|
| 1 | `e.name = $name` | **Yes.** |
| 2 | `e.name = $name COLLATE NOCASE` | **No** — not unless the index itself is declared `COLLATE NOCASE`. |
| 3 | `e.name LIKE '%name%'` | **Never.** No B-tree serves a leading-wildcard `LIKE`. Property of the operator, not a tuning gap. |

**The no-match arm reaches tier 3 by definition.** So the proposed index would speed the
arms that were *already the fastest* (`callees` distinctive 7.96 ms vs its own no-match
12.07 ms) and leave the floor intact. That asymmetry — a name that resolves is cheaper
than one that does not — is the run's tidiest confirmation of the mechanism.

- **Tier-3 floor (~11.85 ms @50k)** needs a *trigram or token* index over `entity.name`.
  Reuse, not invention (`fact_fts`, `fact_token`) — but a second token index is a second
  thing that can silently disagree with its source, the failure `CLAUDE.md` documents at
  length. **Not worth it for 11.85 ms.**
- **`callers` hub cost (+17.80 ms @50k)** — larger than the floor, and *plainly*
  indexable: a computed leaf column plus an index on `(kind, leaf)`. **This is the half to
  reach for first if 4b ever triggers.** Derived state (D8), no authored-truth migration,
  but it needs a schema change and backfill, so it wants a re-index already happening.

**Prerequisite for either:** capture `EXPLAIN QUERY PLAN` for `Resolve` **before** building
anything. `Resolve` ends `ORDER BY e.path LIMIT $limit` and `entity.path` is UNIQUE, so
SQLite may already be walking the *path* index and filtering — in which case all three
tiers are uniformly O(corpus) regardless of a name index. D60 cuts both ways: a plan is
not a clock, and a clock is not a plan.

## 9.4 What the run corroborates elsewhere

`callers` hub: **+2.52 ms @5k → +17.80 ms @50k**. The queries most likely to return an
over-approximation are also the dearest to produce one — a second, independent reason §1b
is worth doing, and the fan-out signal it needs is already measured by the slow scan.

*(This number is also the price tag on §11.2's fix, and it is why that fix is cheap rather
than free: it moves `implementers` onto the same scan `callers` already pays.)*

## 9.5 Item 4b — deferred, with a scheduled re-measure

**Trigger: re-measure when §3 lands.** §3's version bump forces a full re-read anyway
(982 ms @5k / 9,559 ms @50k), it is attended, and the harness now exists. Three changes:

1. **A genuine H2 arm.** H2 is **not moot**: §9.3's candidates change `Resolve`'s per-call
   cost; H2 is the *number* of round-trips the `Callees` loop makes. Different quantities.
2. **`EXPLAIN QUERY PLAN` capture.**
3. **Include `defined_at` and `imports`.** Under first-reach `defined_at` is the
   highest-frequency relation and is a bare `Resolve` — exactly the O(corpus)-on-miss path
   — and its cost is currently assumed from D58 rather than measured here.

**Add a fourth if §11.2 ships**: an `implementers` arm. It moves from a single indexed
equality to a `MatchingSymbolNames`-shaped scan, which is a real change to that relation's
cost curve and should not be inferred from `callers`' numbers.

## 9.6 Process note

- **A pre-committed decision rule needs a magnitude term, not only a shape term.**
- **A rule that names a *remedy* rather than a *finding* can be wrong twice** — once about
  whether to act, once about what to do. §4's row prejudged the fix before the mechanism
  was known.

---

# 10. Amendment — two spec-defects from review of `6aa2f33` (2026-08-27)

Both were raised as judgment calls by the Reviewer and both are **spec bugs, not code
bugs**. The implementation was right in each case.

## 10.1 The version-bump language named the wrong constant

**Accepted, and extended — the carve-out offered with the finding is also wrong.**

The two constants are not interchangeable and the spec treated them as one:

- `CodePaths.GrammarVersion` (`CodePaths.cs:5-20`) tracks `docs/engram-path-grammar.md`,
  which is *"the authority for how code subjects are addressed… paths are promises"*. It
  moves when **addressing** changes.
- `CodeAnalyzer.AnalyzerVersion` moves when **what is observed** changes.
- `CodeIndexer.CurrentVersion` is `$"{GrammarVersion}.{AnalyzerVersion}"`
  (`CodeIndexer.cs:78-84`), so **either** forces a full re-read. My spec's *consequence*
  was therefore right; only the constant naming it was wrong.

**The rule**, stated so it does not have to be re-derived:

> Bump `AnalyzerVersion` for a change in **what the indexer observes**. Bump
> `GrammarVersion` **only** when `docs/engram-path-grammar.md` itself changes. When in
> doubt, ask whether an existing fact's `path` would be spelled differently after the
> change — if not, it is an analyzer bump.

The history is unambiguous, and every bump but one is an analyzer bump:

| sha | subject | constant | old→new |
|---|---|---|---|
| `224d38a` | Tier 1 runs: tree-sitter binding, registry queries, indexer integration (D47) | Analyzer | 1→2 |
| `3622ed8` | code-navigation Phase 2: object-bearing `imports` fact | Analyzer | 2→3 |
| `693db7b` | Phase 3: defined_at/imports/callers/callees over the call graph | Analyzer | 3→4 |
| `c5b78db` | Widen tier-2 member emission to every visibility | Analyzer | 4→5 |
| `6aa2f33` | Syntactic inheritance/contains edges + new relations | Analyzer | 5→6 |
| `d730dab` | **Grammar v2 (D48): a symbol's address is its scope chain** | **Grammar** | 1→2 |

**Where I extend the finding.** It proposed keeping §3's claim that a *new language row*
needs a `GrammarVersion` bump. That claim is **also wrong**, for the same root cause, and
`224d38a` proves it: the commit that introduced tree-sitter language support bumped
`AnalyzerVersion`. Adding Python addresses its symbols `src/foo.py#bar` — an existing path
form. Both §2 and §3 have been corrected.

**The root cause is a name collision, and naming it is the durable fix.** "Grammar" in
`GrammarVersion` means the **path grammar**; "grammar" in "tree-sitter grammar" means the
**parser**. §3 is a section entirely about adding tree-sitter grammars, so the wrong
constant read as obviously right there. Anyone writing about language support in this repo
will meet the same trap; §3 now states the distinction rather than relying on the reader
to notice it.

The `AnalyzerVersion` comment block in `CodeAnalyzer.cs:27-36` already records this
correctly per bump — including version 5's *"addressing is unchanged (GrammarVersion stays
2), only which members are observed"* and version 6's explicit statement of the same
reasoning. **The code documented the rule the spec got wrong**, which is the cheapest
possible place to have found this.

## 10.2 §8.5.3 covered total misses but not partial ones

**Accepted as a rule, not as an acceptance** — and the original reasoning was wrong, not
merely incomplete.

I wrote the asymmetry as *"an over-approximation makes a false claim; a drop makes none."*
The finding is that total misses are covered (`NotFound`'s `coverageCaveat`) while a
*partial* answer — nested types dropped, generics missed by exact match — prints a
confident list with no caveat.

The rule and its reasoning are written into **§8.5.3 item 4** above. In short: a returned
list makes an **implicit completeness claim**, so a drop does make a false claim — it just
makes it in the shape of the response rather than in a row. Under first-reach that is
*worse* than a total miss, because a total miss fires the caveat and sends the model to
Grep while a plausible partial answer stops the search entirely.

The obligation is **fix-or-declare, discharged statically**: known classes an extractor or
query cannot represent are declared per language row or per relation — the same mechanism
§8.5.3.1 already uses — and surfaced *conditionally*, only when a query could have been
affected. Conditional because an always-on caveat is a banner people learn to skip (D37),
which is why `[stale]` fires per result rather than per call.

**Why a rule rather than accepted silence.** Accepting partial silence would be defensible
under the supplement framing this spec started with. It is not defensible under §8's
first-reach mandate, which is the whole premise of the work `6aa2f33` implements: the
argument for building these relations at all was that a first-reach tool is judged on its
miss behaviour. A tool that answers *"here are the implementers"* when it means *"here are
the implementers I can see"* fails that test in the one way the model cannot detect —
which is exactly the standard §8.2 set, applied to ourselves.

**What this section got half right, and §11.1 finishes.** The rule above is correct about
*what* must be declared and silent about *where*. The Implementor read "declared per
language row … surfaced conditionally" exactly as written and produced a note that fires on
every result, because the per-language condition is satisfied by every language. The rule
needed a second clause, and it now has one.

## 10.3 Not reopened

Per the brief, nothing else in the spec was touched at that time. The Reviewer's impl-side
findings (F1 blocking, F2–F6 should-fix) were routed to the Implementor and are not
addressed here.

---

# 11. Amendment — the banner, and qualifier spelling (round-3 review, HEAD `3e6959f`)

Two follow-ups. Neither blocked GO. **Both resolve to code changes**, and saying so is the
substance of the answer rather than a deferral: §11.1 is a caveat in the wrong channel and
§11.2 is one lookup with two implementations, and neither is discharged by spec text.

## 11.1 The nested-type caveat is a banner — and the rule I wrote is what made it one

### The finding is accepted, and it is my defect

The implementation matches the spec exactly. The spec was wrong.

Read from the code rather than argued: `NestedTypeEdgesDropped: true` is set on **C#
(`LanguageRegistry.cs:305`), TypeScript (`:404`), and JavaScript (`:458`)** — every shipped
language — and the emission test (`EngramMcpTools.cs:1405-1410`) reads *only* that boolean.
It tests nothing about the query, the relation, or the returned rows. So the note fires on
every non-empty `implements` / `implementers` / `members` result there is.

### The rule — the discrimination test

> **A flag that is true for every row is not a discriminator. It is a constant.**
>
> Before adding a per-result caveat, ask what fraction of results it will fire on. At or
> near 100%, it is not a declaration — it is a banner, and D37 says a banner is read as
> noise. It belongs in the tool's description, not on the response.

The 3-of-3 is not a coverage accident that later grammars will dilute. §3's next two rows
are **Python and GDScript, and both have nested classes**, so the flag would stand at 5 of
5. The limitation is a property of **the extractor**, which the design happened to express
as a property of **each language** — a shape that reads as variable and is not.

### Where an unconditional truth goes

This is the split D51 already makes, applied to a caveat instead of a memory rule:

- The **tool description** (`[Description]` on `engram_navigate`, or the per-relation help)
  is a compile-time constant, re-sent in the system prompt **every turn**. It does not
  decay across compaction the way a primer does, and it costs zero per-result attention.
  That is the correct home for *"`implements`, `implementers` and `members` do not cover
  types nested inside other types."*
- The **response** carries only what **varies**. The generics note stays there — it fires
  on a type-argument marker, which some queries have and most do not. That one discriminates.

**Note the direction of the tradeoff.** Moving this into the description makes it *more*
reliably delivered, not less: a line the model sees every turn and never habituates to
beats a line it sees on every result and learns to skip. The rule is not being weakened to
reduce noise; the noisy channel was the weaker one.

### Recommended change (code, route to Implementor)

1. **Delete** the per-result nested-type note and the `NestedTypeGapNoteTail` path in
   `EngramMcpTools.cs:1395-1416`.
2. **State the limitation** in `engram_navigate`'s description for `implements`,
   `implementers`, and `members`.
3. **Keep `LanguageDefinition.NestedTypeEdgesDropped`.** It is correct data and it is what
   makes step 2 auditable — and if a future language row ever sets it `false`, the flag
   becomes a discriminator and the per-result note becomes correct again. Deleting the data
   because its current *presentation* is wrong would throw away the thing that decides when
   to change the presentation back.

### The half nobody priced — and it is the fix half

**§8.5.3 item 4 says fix-or-declare. Only "declare" was ever considered.** D48 made a
symbol's address its scope chain, so `Outer.Inner` is *expressible* in the path grammar
already. If that is right, nested types are dropped by the **tree-sitter / Roslyn
extraction query**, not by a design limit — which would make this a fix, and the
declaration in step 2 would then be deleted rather than kept.

I did not settle this and I am not guessing at it: I have not read the extraction queries,
and reaching for a declaration because it is the cheaper text to write is exactly the
failure the fix-or-declare rule exists to prevent.

> **Scoping question for the Implementor** (not a design call): what does it cost to emit
> base-list and containment edges for a type declared inside another type? Specifically —
> does `CodePaths` already produce a stable address for a nested declaration, and is the
> drop enforced in the tree-sitter query, in the Roslyn sidecar's `ScopeOf`, or in
> `CodeIndexer`? If the answer is "one query pattern per language", fix it and delete the
> declaration. If it is "addressing does not express it", the declaration is the right
> answer and §5 item 5b closes.

Steps 1–3 are worth doing **either way** — the banner is wrong whether or not the drop is
later fixed — so do not block them on this question.

### Minor, found while reading the string

The note reads *"do not address nested-type declarations (§8.6)"*. **§8.6 of this document
is about tree-sitter query authoring and the TypeScript `interface … extends` call — not
nested types.** The citation is wrong, and more importantly a spec-section number is the
wrong thing to put in tool output at all: the audience is a model in some other repo that
cannot resolve it, and section numbers renumber under exactly the kind of amendment this
document keeps receiving. Drop the citation with the note.

## 11.2 Qualifier spelling is not a third gap class — it is one lookup with two implementations

**The finding is real. The framing is not, and the correct verdict is fix rather than
declare.**

### What the code does

| | subject side | object-name matching |
|---|---|---|
| `callers` (`CodeCallGraph.MatchingSymbolNames`, `:183-195`) | — | reads every `symbol-name` entity, matches on **leaf** |
| `implementers` (`EngramMcpTools.cs:1191-1206`) | — | `o.path = CodePaths.ForSymbolName(query)` — **exact string equality**, `LIMIT 1000` |
| `implements` (`EngramMcpTools`, via `SymbolResolver.Resolve`) | three-tier exact → NOCASE → substring | — |

So a query for `IFoo` does not find a stored `NS.IFoo`. The error path says as much —
*"checked exact spelling only"*.

### Why this is not a declare class

**`callers` and `implementers` ask the structurally identical question**: *find stored
`symbol-name` objects that name X, return their subjects.* They answer it with two
different matching rules, and nobody argued for the difference — it is two call sites, which
is precisely the divergence §8.5.3.2 forbids one level up ("two copies of which predicates
count as inheritance") and that `CLAUDE.md` forbids for `VectorLane`. **The first
divergence of two implementations of one rule is the bug, not the drift that comes later.**

Three things settle which side is right:

1. **The design already chose.** §0 is explicit that objects are *names as written* and
   `CodeCallGraph`'s own doc says `join`, `path.join` and `os.path.join` are three entities
   that all answer *"who calls `join`"*. Qualifier-tolerant read-time matching is not a new
   concession — it is the half of the bipartite design that makes storing names as written
   workable at all. `implementers` stores under that scheme and reads as if it did not.
2. **The collision argument runs the *other* way here.** §8.3 already established that type
   names collide far less than method leaves. So the trade that was accepted for the
   **more** collision-prone case is being refused for the **less** collision-prone one.
3. **It fails in the direction §10.2 just ruled is the worse one.** `callers`'
   leaf-matching **over**-approximates, and §1b's hub note marks it. `implementers`' exact
   matching **under**-approximates silently — a confident list of implementers with the
   qualified ones missing, which is the completeness claim §8.5.3 item 4 calls worse than a
   total miss.

Declaring it would be worse than either fixing or leaving it: it would put a permanent
caveat on a relation to describe an inconsistency rather than a limit.

### Recommended change (code, route to Implementor)

Match `implementers` on **leaf**, both sides, reusing `MatchingSymbolNames` rather than
writing a second leaf matcher — one implementation, per §8.5.3.2. Symmetric leaf matching
also handles the mirror case (query `NS.IFoo`, store holds bare `IFoo`), which exact
matching misses in both directions.

Two things this needs and one it does not:

- **It needs the §1b marking.** Leaf matching makes `IFoo` match `NS1.IFoo` and
  `NS2.IFoo`. That is an over-approximation, and §1b's hub note is exactly the mechanism
  for it — already built, already shipped in `6aa2f33`, reused rather than reinvented.
- **It needs a latency arm** — see §9.5. It moves `implementers` from one indexed equality
  onto a `MatchingSymbolNames`-shaped scan, whose price §9.4 measured at **+17.80 ms @50k**.
  That is within §9.2's budget and is why this is *cheap*, not free; it should be measured
  rather than inferred from `callers`' numbers.
- **It does not need `SymbolResolver`'s NOCASE or substring tiers.** Case-insensitive type
  matching is a different question nobody asked, and substring matching would return
  `IFooBar` for `IFoo` — the exact false positive §2 cites as Grep's failure. Leaf only.

### Fallback if the fix is not taken

Then it *is* a declare class, and it goes in as one — a per-relation note on `implementers`,
firing when the query contains a qualifier separator (`.` or `::`), stating that only the
exact spelling was checked. That is a real discriminator, so it passes §11.1's test. **But
take the fix**: the note describes a divergence rather than a limit, and a caveat that
explains an inconsistency tends to outlive the inconsistency.

## 11.3 Why both answers are code changes, and one process note

The brief offered "spec change or ruling" for each. Neither is dischargeable in spec text,
and pretending otherwise would be the failure mode this document has now recorded twice: in
§10.1 the code documented the rule the spec got wrong, and here the spec's rule was right
about *what* and silent about *where*, so the Implementor built a banner while following it
exactly.

> **A rule that specifies an obligation without specifying its channel will be discharged
> through whichever channel is nearest to hand.** §8.5.3 item 4 said "declare it"; the
> nearest channel was the one the sibling caveat already used; the result was 100%-firing
> noise. Item 4 now carries the discrimination test, which is the missing clause.

Both changes are small, neither is blocking, and both should ride the next `navigate` touch
rather than a change of their own. Neither needs a version bump: §11.1 is presentation and
§11.2 is read-path matching — **no fact changes, so no re-index** (§10.1's doubt test: no
existing fact's `path` would be spelled differently).
