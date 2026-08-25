# Code navigation — Phase 3 implementation spec (calls and query-time resolution)

**Status:** implemented; this spec is live and amended against the built code. **Revision 5** —
§6.1(iii), §6.2 and §6.3 are amended for **scope and match tier**: the repo-scoped declaration count
is **correct and the spec's literal `COUNT(*)` was the defect** (N2), and the count is currently
computed through the **substring rung**, which is a separate and larger finding. Revision 4's §5.3
rewrite (B1, S3) stands. Revision 3's corrections stand: §6.4's signal order, §7.1's derived
coverage, §9's ranker. Each amended section says what changed.

**Relationship to the other two specs.** `docs/code-navigation-spec.md` §5 is the Phase 3 *design*
and holds the Ultra-Advisor's ruling on resolution; that ruling has been re-affirmed against new
evidence (§6.1) and this spec implements it. `docs/code-navigation-phase2-spec.md` is the edge
substrate this builds on — schema v13, `CodePredicates.EdgeBearing`, `ObjectPath` addressing, the
§7.2 lint. **Where this file and master §5 disagree, this file wins and §2 says why.**

**Ships:** *who calls X*. **Schema change: none — including for §7**, which revision 3 shows needs
no new marker.

---

## 1. What Phase 3 is

Phase 2 made a fact able to say *subject → predicate → object*. Phase 3 fills that shape with
`calls` edges and turns on the `engram_navigate` relations that previously refused to answer.

The refusal text those relations carried —

> `'callers' is not yet indexed — code edges (calls, references) are Phase 3 work that has not
> landed. This is not a negative result; it means the question cannot be answered yet, not that
> the answer is empty.`

— **is the shape of the whole phase.** It existed because an empty list reads as *nothing calls
this*. Phase 3 does not delete it; it **narrows** it to the cases that still cannot be answered, and
§7 is now able to say exactly which those are rather than guessing.

**Coverage is three languages.** `LanguageRegistry.cs` holds five rows: `text` (tier 0), `markdown`
(tier 0), `csharp` (tier 2, Roslyn sidecar), `typescript` (tier 1, tree-sitter), `javascript`
(tier 1, tree-sitter). Tier 0 gets no call extraction (§5.1), so `calls` is answerable for **C#,
TypeScript and JavaScript only**. Python/Go/Rust/Java are deferred to a separate spec.

---

## 2. Corrections to `code-navigation-spec.md` §5 and §9

**C1 — "zero extractor edits" is wrong, and E13 proved it rather than arguing it.** A declaration
query is self-contained: `TreeSitter.Analyze` (`TreeSitter.cs:130-220`) reads `@name`, `@scope`,
`@params` out of one match. **A call site is not** — the fact's *subject* is the enclosing symbol,
which the call node does not carry. `Matches()` yields a per-match capture dictionary and nothing
above it. §5.2 specifies the parent walk; E13 is why it is a walk rather than a cleverer query.

**C2 — adding `CallQuery` to `Analyze`'s null-guard would silently drop TypeScript and JavaScript
to tier 0.** The guard is:

```csharp
if (language.GrammarFor(extension) is not { } grammar
    || language.DeclarationQuery is null
    || language.ImportQuery is null)
{
    return null;                     // → the whole file falls back to tier 0
}
```

Returning null there costs the file **its declarations and its imports**, not just its calls.
**`CallQuery` is checked at its own point of use, never in this guard** (§5.2). Every other query is
in that guard, which is what makes this the natural mistake.

**C3 — `SymbolResolver` cannot be used for the join as it stands, and "one implementation" is still
right.** `Resolve` (`SymbolResolver.cs:38-63`) is a **fallback ladder** ending in `LIKE '%name%'`,
tuned for a human typing a half-remembered name. As a per-edge join it manufactures edges: callee
`join` matches `joinPath`, `rejoin`, `JoinedTable`. Fix is a caller-declared **tier ceiling**
(§6.3), not a second resolver.

**C4 — `callers(X)` is not a single-entity lookup.** `join`, `path.join` and `os.path.join` are
three `symbol-name` entities meaning one declaration. Selecting on one under-reports silently, in
the direction of *nothing calls this*. `callers` gathers **every** name entity whose leaf is `X`
(§6.2).

**Carried forward, not re-corrected:** master §9's claim that the backup journal reads through
`FactStore.ReadLive` is false (Phase 2's C1). The rule stands on other grounds; do not propagate
the stated reason.

**Confirmed, not corrected** — master §5.1's `DeepTier.Merge` trap is verbatim in source
(`DeepAnalysis.cs:83-136`). See §5.4.

---

## 3. Files this phase touches

| File | Change |
|---|---|
| `src/Engram.Core/DeepAnalysis.cs` | `DeepCall` record; `DeepAnalysis.Calls`; `DeepTier.Merge` emits `calls` candidates |
| `src/Engram.Core/LanguageRegistry.cs` | `LanguageDefinition.CallQuery`; TS and JS rows gain one |
| `src/Engram.Core/TreeSitter.cs` | `ts_node_parent` binding; flat call query + parent walk (§5.2) |
| `src/Engram.Sidecar.Roslyn/Program.cs` | walk `InvocationExpressionSyntax`; JSON output gains `calls`; each symbol gains `id`, each call carries `enclosing_id` (§5.3); **`using Engram.Core` and the `DeepTier.Fragments` call are removed** |
| `src/Engram.Sidecar.Roslyn/engram-roslyn.csproj` | **revision 4 (B1): the `ProjectReference` to `Engram.Core` is REMOVED**, with its comment |
| `src/Engram.Core/RoslynSidecar.cs` | the core half of that JSON contract, **including id → fragment resolution** (§5.3) |
| `src/Engram.Core/CodePredicates.cs` | `EdgeBearing` gains `"calls"` |
| `src/Engram.Core/CodePaths.cs` | `LeafOf` — the one leaf-name extraction |
| `src/Engram.Core/SymbolResolver.cs` | `Resolve` gains a tier ceiling |
| `src/Engram.Core/CodeCallGraph.cs` | the ranker (§6.4), both query directions (§6.2), and **the match-tier fix at `:67`** (§6.3) |
| `src/Engram.Cli/EngramMcpTools.cs` | `callers` / `callees`, with §6/§7's precision reporting; `neighbors` still refused |
| `src/Engram.Core/CodeIndexer.cs` | nothing — but §7 now **reads** its version stamp; see §7.1 |

Do **not** touch: the schema; `CodePaths.ForSymbol`'s output; `CodePaths.ForSymbolName`'s encoding;
`CodePaths.GrammarVersion`; `FactStore.ReadLive`'s default; `fact_relation`; the `learned_via`
`CHECK`.

**Do not add a `ProjectReference` from the sidecar to any Engram project** — §5.3 (B1) explains why,
and item 24 guards it.

---

## 4. What a call is, as data

```csharp
/// <summary>One observed call site: who called, what name they wrote, where.</summary>
public sealed record DeepCall(
    string? EnclosingFragment,  // the caller symbol's fragment; null = file scope (§5.2.1)
    string Callee,              // the callee name AS WRITTEN, qualifier included
    int Line);

public sealed record DeepAnalysis(
    string Path,
    IReadOnlyList<DeepSymbol> Symbols,
    IReadOnlyList<string> Imports,
    string? Error,
    IReadOnlyList<DeepCall> Calls);
```

**`Calls` gets no default value.** Defaulting it to `[]` lets a producer that was never updated
compile clean and return "this file makes no calls", which is indistinguishable from a file that
genuinely makes none. Both producers must state their answer; the compiler error is the point.

**`EnclosingFragment` is a fragment, not a path, and it is nullable.** It is what
`DeepTier.Fragments` (`DeepAnalysis.cs:39-74`) produces — `Scope/Name`, plus the collapsed parameter
list appended only on collision (D48). `Merge` turns it into an address with
`CodePaths.ForSymbol(fileEntityPath, fragment)`. Null is the file-scope case (§5.2.1).

**`DeepCall` is a core type, and only core fills it.** Tier 1 builds it inside `TreeSitter`; tier 2
builds it inside `RoslynSidecar` from the sidecar's JSON (§5.3). **The fragment is therefore always
produced by `DeepTier.Fragments` itself**, never by a second derivation of the same rule — which is
what revision 4 changes about tier 2 and why the sidecar no longer references `Engram.Core`.

**`Callee` is the name as written, qualifier and all** — `join`, `path.join`, `this.Foo`,
`await x.RunAsync`. Not slugged, not normalized; percent-encoded into `/symbol-names/…` at the
`FactWrite` boundary via `CodePaths.ForSymbolName`. **Storing the qualifier is load-bearing** —
§6.1 and §6.4 both turn on it.

---

## 5. Extraction

### 5.1 Tier 0 extracts no calls, and must say so

A tier-0 regex matching `foo(` cannot separate a call from a declaration, a cast, `if (`, `catch (`,
a string literal or a comment. A navigation answer that is mostly false is worse than one that says
it does not know. E14's proxy is incidental corroboration: `\w+\(` over C# core+cli returned 11,612
hits against 1,628 distinct tokens, and the gap is mostly the non-calls that pattern cannot exclude.

`text` and `markdown` are tier 0 by row, *and* any tier-1 row degrades to tier 0 without its grammar
— `TreeSitter.cs` records that in `downgrades` (`:236-241`, `:246-251`, `:273-282`). **§7 specifies
how the query surface reports this, and revision 3 makes most of it derivable rather than unknown.**

### 5.2 Tier 1 — tree-sitter (TypeScript, JavaScript)

**Settled by E13; there is no fork here.**

`LanguageDefinition` gains `string? CallQuery = null`. **It is a flat query — one pattern,
`call_expression` with `@callee` — and it does not attempt to name the enclosing declaration.**
Attribution is a parent walk in the extractor.

**Checked at its own point of use — NOT in `Analyze`'s null-guard** (**C2**). The guard at
`TreeSitter.cs:138-141` keeps its two conditions; call extraction sits behind its own
`if (language.CallQuery is not null)`, and a `Compile` failure adds a `downgrades` note while
leaving declarations and imports intact.

**Why a walk and not a nested query — E13's result, recorded because it is not re-derivable.**
A candidate nested query, compiled against the repo's own `libtree-sitter-typescript.dylib` through
the same `ts_query_new` path, matched top-level function, const-arrow-function, class method, getter
and object-literal shorthand method — **but only when the call sat as a direct top-level statement
of the body.** A call one level inside an `if` was missed in the *same* function form, because the
pattern names the exact `statement_block → expression_statement → call_expression` chain. The
decisive check: nesting `call_expression` as a plain descendant of `function_declaration`, with no
field chain, **fails to compile** — a structural error, not an empty result. **Tree-sitter's query
language has no descendant operator; nesting mirrors the grammar generation by generation.** So a
nested query needs a pattern per *(function form × wrapper shape)*, unbounded, with **silent**
misses. A flat query found all six calls in the same fixture: the parser is fine, the ceiling is the
query shape.

**The walk:**

- Bind `ts_node_parent` in `TreeSitter.cs` — the one new native binding this phase needs.
- From each `@callee` node, walk ancestors to the **nearest declaration node**, using the node types
  the language's `DeclarationQuery` already names. **One notion of "a declaration" per language, not
  two** — it must not be re-implemented as a private list of node types beside the query.
- Rebuild that node's fragment by the same rule `DeepTier.Fragments` uses (§4), so the call's
  subject address is byte-identical to the address the declaration's own candidate produced.
- Bounded by tree depth, terminates at the root, cannot loop.

**Captures:** `@callee` only. Per `LanguageRegistry.cs:30-51`, captures are named, a capture
beginning `_` exists only for a predicate, and **each query is verified against the compiled grammar
before it lands** (D47) — which is why TypeScript and JavaScript cannot share a query even at this
size.

### 5.2.1 A call with no enclosing declaration

A nested query never matched a module-level call, so the case did not exist; a parent walk reaches
the root and must decide. Both tiers hit it — a TS module-level `configure()`, a C# top-level-
statements program.

**Rule: attribute it to the file entity, not to nothing.** The file is already an entity with an
`about` fact, "this file calls X" is true, and dropping it loses a real edge silently — the failure
mode this phase is organized against. `EnclosingFragment` is `null`; `Merge` emits against
`fileEntityPath` with kind `file`.

**Consequence for §10 item 5:** the no-dangling-caller assertion is over `calls` facts whose subject
kind is `symbol`. **State the exemption in the test rather than widening the assertion** — a guard
loosened to accommodate one case stops catching the case it was written for.

**A C# field initializer is not this case.** It is inside a member, and the Roslyn walk stops there.

### 5.3 Tier 2 — the Roslyn sidecar (C#) — REWRITTEN in revision 4

`Program.cs` reads `{"path","content"}` per line and writes `{"path","symbols","imports"}` or
`{"path","error"}`; it uses `JsonNode`/`JsonObject`/`JsonArray` throughout with **no
`JsonSerializerContext`** (D1/AOT), and one unparseable file emits an error object while the loop
continues. **`calls` joins that output object; keep both properties** — `JsonNode` only, and a
call-extraction throw must not abort the batch.

Its parse is **syntax-only and deliberately so**: `CSharpSyntaxTree.ParseText(content).GetRoot()`,
with a preamble stating *"a semantic model needs references, and grammar v2's addresses are
syntactic on purpose (D48)"*. So:

- **Extraction is `root.DescendantNodes().OfType<InvocationExpressionSyntax>()`**.
- **The callee is `invocation.Expression.ToString()`** — exactly "as written", qualifier included,
  no normalization.
- **Do not add a `SemanticModel` to resolve calls.** It is the one tier that could bind a call to a
  declaration; letting it would make C# edges a different kind of thing from every other language's,
  while master §5.2 makes resolution derived state no tier may bake in. `object` is immutable belief
  content (D8), so a resolved target could only be churned through supersession, never repaired.
  §6.1's affirmation rests partly on this staying true.
- **`imports` objects on this tier are namespaces, not paths** — `UsingDirectiveSyntax` →
  `u.Name.ToString()`. §6.4 depends on knowing that.

#### 5.3.1 The sidecar reports structure; core does addressing (B1)

**AMENDED, and this replaces revision 3's attribution bullet.** Revision 3 said the sidecar's walk
"must produce the identical fragment `Emit`/`EmitMember` produced". The Implementor achieved that
the direct way — by calling `DeepTier.Fragments` in the sidecar, which required a
`ProjectReference` to `Engram.Core`. **That is a real defect and the spec invited it.**
`Engram.Core` carries `LLamaSharp` and `LLamaSharp.Backend.Cpu`; the backend package copies all
seven platform payloads whenever it cannot see a `RuntimeIdentifier`, and the SDK does not pass one
to a RID-agnostic project reference. That is the exact trap `Directory.Build.targets` exists to
close for the main publish (210 MB → 121 MB, D45), now reproduced on a second publish target that
had none of the machinery. The sidecar publishes framework-dependent at ~16 MB **on purpose**, and
that number is load-bearing for the tier-2 degradation story.

**The ruling is neither of the two options offered.** Extracting `Fragments` into a shared
dependency-free project keeps one implementation but buys a whole project to do it; duplicating it
behind an equivalence test creates a second implementation and hires a test to watch it drift. Both
assume the sidecar needs to *compute* a fragment. **It does not.** Everywhere else, the sidecar
emits fields — `name`, `kind`, `declaration`, `doc`, `scope`, `params` — and core assembles the
address from them. Calls are the same shape:

- **Each symbol object gains an `"id"`**: a monotonic integer assigned by the sidecar as it emits.
- **Each call object carries `"enclosing_id"`** (omitted entirely for the file-scope case, §5.2.1)
  instead of an assembled `"enclosing"` fragment string.
- **`RoslynSidecar.cs` resolves id → fragment**, from the `DeepTier.Fragments` result it already
  computes over the same file's symbols, and fills `DeepCall.EnclosingFragment` with it.

That removes the `ProjectReference`, the `using Engram.Core`, and the sidecar's use of `DeepSymbol`
altogether — the walk needs only a `Dictionary<SyntaxNode, int>`. **It strengthens the
one-implementation property rather than guarding it**: `DeepTier.Fragments` stays the single
fragment builder and simply keeps one caller instead of two. It also makes the two deep producers
symmetric — tier 1 already reports structure and lets core address it — rather than leaving tier 2
as the one tier that computes addresses out of process.

**The id must be an explicitly emitted field, never an array position.** Both sides filter
independently: `Fragments` drops empty-name symbols (the sidecar's own comment at `Program.cs:214`
records discovering this), and core's reader requires `name` and `declaration` before it will
construct a `DeepSymbol` (`RoslynSidecar.cs:161-163`). An index survives neither filter; an explicit
id survives both. **An `enclosing_id` that resolves to nothing is treated as file scope** (§5.2.1)
and noted, not silently dropped — but it should be unreachable, and a test asserting it is
unreachable on a fixture containing an empty-name symbol is what proves the id is not positional.

#### 5.3.2 A private member's calls attribute to its enclosing type (S3)

**Ruled in revision 4; revision 3 was silent and the silence shipped.** `EmitMember` skips
non-public members, so the walk — which stops at the nearest node in the *emitted*-symbol map —
passes a private method and lands on its enclosing class. A call inside a private method therefore
attributes to the type, not to the method. It does not dangle, so §10 item 5 stays green, which is
why the gap is silent.

**Accept the type attribution. Do not extend emission to non-public members in this phase.**

The reasoning is blast radius, not elegance. What a C# declaration *is* — a file's public surface —
is D48's and Phase 2's, decided for reasons that have nothing to do with calls. Widening it here
would change `declared-as` for every C# file in every store, redefine what Phase 2 built, and force
a `GrammarVersion` conversation this phase explicitly declines. **Phase 3 does not get to redefine
what a declaration is in order to make its own edges finer.**

**Do not reach for §5.2.1 as the precedent.** It looks similar and is not: there the fine-grained
entity genuinely does not exist, so the file is the only true subject available. Here the private
method exists and is simply not addressable, by a policy choice made elsewhere for another purpose.
The rule that actually governs is the one this spec has now applied four times — **coarse but
labelled beats precise but wrong, and both beat silent** (§6.1(iii)'s superset, §5.2.1's file
attribution, §7.1's three states, and this).

**So it must be labelled, not merely accepted.** A `callers` result whose subject is a `type` entity
on tier 2 says so: *"the caller is somewhere in `Foo` — C# call attribution stops at the type for
non-public members."* An unlabelled type subject reads as *the type itself calls this*, which is
false and is exactly the silent failure the label exists to prevent.

**A third option was considered and rejected**: attribute to the private member without adding it to
the declaration set. That makes the subject address dangle — no `declared-as` fact to match — which
destroys item 5's drift guard for precisely the members most likely to drift.

**This ruling is reopenable on evidence, and E16 (§9) is the evidence.** If most C# call sites are
inside non-public members, the coarse answer is nearly useless for the tier that has the most
facts, and the D48 conversation becomes worth having on its own terms. That is a measurement, not a
design question, so it does not block.

#### 5.3.3 The doc comment is wrong either way

`Program.cs:200-208` states that a call inside a local function "simply walks past it to the true
enclosing member — never a dangling caller." **`the true enclosing member` is false**: the walk
reaches the nearest *emitted* symbol, which for a non-public member is the enclosing type (§5.3.2)
and for a public one is the member. Replace it with a statement of what the walk actually does:

> One JSON call entry per `InvocationExpressionSyntax`: the callee as written, its 1-based line, and
> the `id` of the nearest **emitted** symbol enclosing it. Emission is the public surface
> (`EmitMember` skips non-public members), so a call inside a private method or a local function
> attributes to the nearest emitted ancestor — usually the enclosing type — never to nothing. A walk
> that reaches the root leaves `enclosing_id` absent, which core attributes to the file (§5.2.1 of
> the Phase 3 spec). This emits an id, not a fragment: assembling the address is core's job
> (§5.3.1).

The correction is required whichever way S3 had gone; a comment asserting a property the code does
not have is worse than no comment, because the next reader trusts it.

### 5.4 The `DeepTier.Merge` trap

`Merge` does **not** append to tier 0's candidates. It replaces them, keeping only the file-level
`about` (`DeepAnalysis.cs:97-99`):

```csharp
var merged = tierZero
    .Where(c => c.EntityPath == fileEntityPath && c.Predicate == "about")
    .ToList();
```

So **any `calls` candidate not explicitly re-emitted inside `Merge` is silently discarded** — no
error, no count, and a `callers` query that simply returns less than it should.

```csharp
foreach (var call in Deduplicate(analysis.Calls))
{
    var (path, kind, display) = call.EnclosingFragment is { } fragment
        ? (CodePaths.ForSymbol(fileEntityPath, fragment), "symbol", CodePaths.LeafOf(fragment))
        : (fileEntityPath, "file", CodePaths.LeafOf(fileEntityPath));   // §5.2.1

    merged.Add(new CodeCandidate(
        path, kind, display, "calls",
        CodeAnalyzer.Cap("calls " + call.Callee),
        Object: call.Callee));
}
```

**Required test, shown failing when the emit is removed.**

### 5.5 One fact per (caller, `calls`, callee) — not one per call site

Three calls to the same target from one function are **one belief**. One fact per site would make
`ux_fact_edge_live` useless (unique on `subject_id, predicate, object_id`) and multiply the store by
the average call count.

`Deduplicate` collapses on `(EnclosingFragment, Callee)`, ordinal, keeping the **lowest** line — so
re-indexing an unchanged file produces an identical candidate and the diff is empty. **Null
`EnclosingFragment` participates as its own group.** Line numbers ride the body/`evidence`, never
the identity.

**Note for tier 2 after §5.3.2:** several private methods calling the same target collapse to one
fact on the enclosing type. That is correct under §5.5's rule and is a second reason the label in
§5.3.2 matters — the fact count under-represents the call sites, by design.

**This is why E3's and E14's site counts are upper bounds on facts, not fact counts** (§9).

### 5.6 `calls` joins `EdgeBearing`

`CodePredicates.EdgeBearing` becomes `{ "imports", "calls" }`. Everything keyed off that set follows
— Phase 2 §5.4's `FactTokenIndex` skip and `fact_fts` trigger exclusion, §5.5's retrieval filter,
§7.1's data lint, §7.2's addressing lint.

**Confirm nothing assumed single-membership**: the `fact_fts` trigger's `NOT IN (…)` interpolation,
`VersionCounts`' exclusion, the §7.2 lint's `predicate IN (…)`. A one-element list and a two-element
list read the same in testing until the second element exists. **Report the audit with the diff.**

**Bump `CodeAnalyzer.AnalyzerVersion` 3 → 4.** **Do not bump `CodePaths.GrammarVersion`** —
addressing is unchanged, and a grammar bump forces a full re-index of every store.

---

## 6. Resolution — at query time, through one resolver

`object_id` is the callee **as observed**, never a resolved declaration; the name→declaration
binding is derived state computed on read.

### 6.1 The ruling, re-affirmed against E8

Revision 1 flagged E8 as evidence against this ruling's premise and recommended escalation rather
than reversing it. **Escalated, and AFFIRMED at ~90%.** The reasoning is recorded here because it is
not re-derivable, and because it **corrects something revision 1 of this spec said**.

E8, against the real store, `kind='symbol'`: **5,355 distinct leaf names; 4,151 (77.5%) have exactly
one declaration; 1,204 (22.5%) have two or more**, distributed `2→852, 3→256, 4→44, 5→10, 6→13,
7→5, 8→4, 9→1, 10→4, 11→4, 13→1, 14→1, 16→2, 22→1, 23→2, 32→1, 35→1, 36→1, 40→1`. The head is
`Run` 40, `Read` 36, `Dispose` 35, `Resolve` 32, `Write` 23, `Parse` 23 — interface-implementation
names, exactly what a call extractor produces most, so ambiguity weighted by call volume is worse
than 22.5%.

**Why that does not move the ruling:**

- **The missing information is absent at index time and query time equally.** No keying scheme
  recovers what no tier possesses.
- **Reversal gets worse as ambiguity rises.** Index-time resolution bakes a one-of-40 guess into an
  immutable D8 field, correctable only by supersession — so every refactor churns the guesses, and
  the churn scales with the very number that was meant to justify the reversal.
- **"Never written down" — revision 1's phrase — is half-overstated, and the correction matters.**
  The **qualifier is stored**, as written (§4). What is missing is only the receiver's *static
  type*, which no write-time amendment captures without a semantic model — which §5.3 declines for
  independent reasons. The design already stores every disambiguator it observes, and §6.4's
  qualifier signal works precisely because of that. **Do not repeat the stronger claim.**
- **The upgrade path stays cheap.** Any future observed disambiguator is an `AnalyzerVersion` bump
  plus a re-index. No schema change, no migration, no supersession storm.

**Consequences for the surface:**

**(i) Extraction is unaffected.**

**(ii) `callees(X)` returns a ranked set, never a pick** — §6.4.

**(iii) `callers(X)` is a superset for an ambiguous name, and it must be labelled.** A `calls`
fact's object is `/symbol-names/Run`; it does not record which `Run`. So `callers("Run")` answers
*"every call site that wrote the name `Run`"* — exact for the 77.5%. It is sound (no true caller is
missing) and the store knows its own ambiguity, so the answer carries it: *"12 call sites wrote
`Run`; `Run` has 40 declarations in this store, so these are calls to some `Run`, not necessarily
this one."* Returning the superset silently is the failure; returning nothing is worse.

**The count is scoped to the population the answer was drawn from — AMENDED in revision 5 (N2).**
Revision 4 said `COUNT(*)` of `entity` rows with `kind='symbol'` and that leaf name, unqualified.
**That literal reading was the defect, and the repo-scoped implementation is right.** The label's
whole job is to say *how ambiguous the set I just handed you is*; a count drawn from a wider
population than the answer describes an ambiguity the reader is not looking at. Read from source:
`repoNeedle` is applied consistently down the whole `Callers` path — to the declaration resolve
(`CodeCallGraph.cs:67`), to the name fan-out (`:179`, on `e.path`), and to the caller selection
(`:210`, on `f.path`). Scope agreement already holds; the spec was the thing out of step.

**The governing rule, stated once so it does not have to be re-derived per label: an ambiguity or
partiality label is computed over the same population the answer was drawn from.** Neither this
spec nor the code stated it before revision 5. It decides every future label of this kind, and it
is why "match the spec literally" would have been the wrong instruction here.

**(iv) The superset is ordered**, by §6.4's top two signals applied from the caller's side, **through
§6.4's implementation** — the same policy in the other direction, not a second ranker. Ordering does
not narrow the set and does not change the label.

### 6.2 The two directions are not symmetric

**`callers(X)`:**

1. Resolve `X` to its declaration(s) via `SymbolResolver` (§6.3); take the leaf name(s). **This is a
   human-typed name, so it keeps the full ladder — but the match tier reached must be reported**
   (§6.3, revision 5).
2. Select every `symbol-name` entity whose **leaf** is that name — `join`, `path.join` and
   `os.path.join` all qualify.
3. Return the live `calls` facts whose `object_id` is any of them; each fact's subject is a caller.
4. Order by §6.4's signals 1–2 from the caller's side (§6.1(iv)).
5. Attach the precision label from §6.1(iii) — the declaration count **over the same repo scope as
   the returned set**, plus the match tier from step 1 — **and, for a subject whose kind is `type`,
   §5.3.2's attribution label.**

Step 2 is what master §5.2 omits; skipping it returns only the call sites that spelled the callee
bare, an under-report that looks exactly like a complete answer.

**`callees(X)`:** select live `calls` facts whose subject is `X`'s declaration address; each object
is a name; enrich each name with its declaration site(s) via §6.3 and §6.4.

### 6.3 `SymbolResolver` stays one implementation, and gains a ceiling (C3)

```csharp
public static IReadOnlyList<SymbolMatch> Resolve(
    SqliteConnection connection, string name, int limit,
    string? pathContains = null,
    SymbolMatchTier ceiling = SymbolMatchTier.Substring);
```

- `defined_at` passes nothing and behaves **exactly as today** — the full ladder, substring included.
- The **per-edge join passes `SymbolMatchTier.CaseInsensitive`**, stopping before the substring rung.

The substring rung is a kindness to a human who typed half a name. Per-edge it is a fabrication
engine, and at E8's rate it compounds: `Run` already has 40 exact declarations before `LIKE '%Run%'`
adds `RunAsync`, `Runner`, `PreRun`. One implementation, one comparison, the *policy* stated by the
caller. A second resolver would diverge on first tune; a ceiling cannot.

**Why `CaseInsensitive` and not `Exact`:** that rung fires only when exact found nothing, and `foo()`
against a declaration `Foo` is a real JS/TS pattern. It still matches the whole name, so it cannot
merge two distinct symbols the way substring can.

#### 6.3.1 Which call sites get the ceiling — AMENDED in revision 5

**Revision 4 said "the join" without naming the call sites, and that ambiguity is live in the
code.** There are three `Resolve` calls on this path and they are not the same kind of call:

| Site | What it resolves | Ceiling |
|---|---|---|
| `CodeCallGraph.cs:67` (`Callers` step 1) | the **user's typed query** | **full ladder** — but the tier must be reported |
| `CodeCallGraph.cs:97` (`Callees` step 1) | the **user's typed query** | **full ladder** — same |
| `CodeCallGraph.cs:111` (`Callees` enrichment) | an **observed callee name**, per edge | `CaseInsensitive` — already correct |

**The user-typed sites keep the ladder.** `callers` and `callees` take a name a person typed, the
same as `defined_at`; refusing `Ru` when the store holds `Run` would be a worse surface for the
rung's actual audience. §6.3's fabrication argument is about the **per-edge** join, where the name
was observed rather than typed, and site `:111` already honours it.

**But the substring rung must not be laundered into the ambiguity label.** `:67` feeds
`declarations.Count`, which becomes §6.1(iii)'s declaration count. When exact and case-insensitive
both miss and substring answers, that count mixes `Run`, `Runner`, `RunAsync` and `PreRun` into one
number presented as *"N declarations of what you asked for"* — which is false, and false in the
direction of overstating ambiguity rather than understating it. **`SymbolMatch` already carries the
tier** (§6.4: the tier says *how the name matched*), so the fix is reporting, not re-resolving: when
the tier reached is not `Exact`, the label says so — *"no exact match for `Ru`; showing callers of 3
substring matches"*.

**This is a real finding, not a spec-tidying exercise**, and it was found while ruling on N2 rather
than reported: the count and the ladder interact, and neither was specified against the other.

#### 6.3.2 The 1000 bound is accepted, and deliberately gets no test

`:67` and `:97` pass `limit: 1000` to `Resolve`, so `declarations.Count` — and therefore the label —
saturates at 1000. **Accept it.** E8 measured the worst real case in this store at **40**
declarations for one name, so the bound sits twenty-five times above the observed maximum and cannot
fire on any corpus this design contemplates.

**Two consequences, and the second is the one that matters:**

- **If it ever does fire, it must render as `1000+`**, never a bare `1000`. A saturated count printed
  as exact makes the label itself false, which is the one thing §6.4's cap-and-label rule and D30
  do not tolerate. That is a one-line render change, not a design question.
- **No acceptance item guards it, on purpose.** Reaching it needs a fixture with 1,001 declarations
  of one name — twenty-five times the real maximum — to defend a bound that cannot be crossed.
  CLAUDE.md's rule is that a guard which cannot fail is worthless; a guard whose only reachable form
  is a synthetic corpus built to reach it is the same thing with more setup. **Recorded here as a
  known bound instead**, which is the honest form. Revisit if a store is ever observed above ~100
  declarations for one leaf name.

### 6.4 Ranking — corrected in revision 3

**AMENDED. Revisions 1 and 2 ordered import-consistency ABOVE qualifier agreement. That was wrong,
it was implemented faithfully, and the correction is a spec defect owned here rather than an
implementation error.**

Navigation is not a relevance question (master §3.4): **the answer is the set**. But at 22.5%,
40 unordered declarations for `Run` is a non-answer, so the set is **ordered** and each entry
**states why it ranked where it did** — D30's rule (a ranker that cannot explain itself is not
trustworthy) one level down.

**The governing rule, which revisions 1–2 lacked and which decides the order: an EXACT signal
outranks an APPROXIMATE one.** A signal computed from stored data that means what it says beats a
signal computed from a heuristic, regardless of which feels more powerful in principle. Ranking by
"how discriminating would this be if it were real" is how an approximation ends up above a fact.

Signals, **highest single signal wins; never blended into a score**:

1. **`SameFile`** — the declaration is in the file containing the call site. Exact.
2. **`QualifierAgreement`** — the callee was written `Foo.Run` and the declaration's `scope` is
   `Foo`. **Exact**: both sides are stored, and §6.1 shows the qualifier is stored *as written*
   precisely so this works. **The qualifier ranks; it never filters** — a qualifier is a *receiver
   expression*, not a namespace, so filtering on it discards the true target whenever the receiver
   is a local variable.
3. **`ImportFilenameMatch`** — the caller's file has a live `imports` fact whose leaf equals the
   declaration file's bare filename. **Approximate, and the name must say so.** See below.
4. **`SameRepo`** — via `pathContains`/`repoNeedle`. Exact but weak.
5. **`NameOnly`** — nothing else fired.

**On signal 3, and why it is accepted rather than fixed.** Resolving a written module string to a
file path is a genuine per-language problem — relative segments, package names, TS `paths` aliases,
`baseUrl`, `node_modules` resolution — and none of that machinery exists in this codebase. **A
filename approximation is acceptable here because this signal ranks and never filters**: a false
positive changes the *order* of a returned set, never its membership, so the worst case is a
mis-sorted list and never a wrong or missing edge. That is the same reason the qualifier is allowed
to rank, and it is why the approximation does not need to be right to be useful.

**But it must not outrank an exact signal, and it must not claim more than it did.** Two required
corrections to what was built:

- **Order:** `QualifierAgreement` is checked **before** `ImportFilenameMatch`. As shipped the chain
  tests import-consistency first, so a loose filename hit beats an exact qualifier match — the
  approximation displacing the fact.
- **Name:** the enum member and the reported rank reason are `ImportFilenameMatch`, not
  `ImportConsistent`. §6.4 requires each entry report *why* it ranked; a reason string asserting
  import-consistency when it compared two filenames makes the explanation itself wrong, which is the
  one thing D30's rule does not tolerate.

**Known limitation, stated rather than special-cased.** On C#, `imports` objects are **namespaces**
(§5.3), so `System.Text.Json` has leaf `Json` and false-matches any file named `Json.cs`. Signal 3
is therefore near-meaningless on tier 2 and occasionally wrong. **Do not add a language branch
inside the ranker** — that would put the registry's tier knowledge in a second place, and the
demotion below `QualifierAgreement` already bounds the damage to ordering. Record it here; revisit
if real import resolution is ever built.

**This implementation is shared with §6.1(iv)'s `callers` ordering**, which uses signals 1–2 only.
One ranker, two callers, the subset stated by the caller — the same shape as §6.3's ceiling.
*Note that the reorder improves the `callers` direction as a side effect*: signals 1–2 now mean
same-file plus qualifier agreement, both exact, where before they meant same-file plus a filename
heuristic.

**Cap and label.** The rendered list is capped (`limit`, clamped 1–100) and **says the true count** —
`40 declarations, showing 10`.

**`SymbolMatch` needs no new field for the ambiguity itself**: `Count > 1` within a tier *is* the
ambiguity. It does need the rank reason, which is a separate axis from the match tier — the tier
says *how the name matched*, the reason says *why this declaration ranked here*. Do not fold them
into one enum; a merged enum has to enumerate the cross product.

### 6.5 Leaf extraction is one implementation

`CodePaths.LeafOf(string name)` — last segment after `.`, and the same rule applied to the `/` in a
fragment (`Outer/Inner` → `Inner`). Two callers that disagree about `os.path.join`'s leaf silently
return different answers for the same query.

One function, two separators, one test table: `join`, `path.join`, `os.path.join`, `Outer/Inner`,
`Outer/Inner(T, U)`, `this.Foo`, a trailing separator, an empty string.

---

## 7. The query surface, and the partiality it must confess

`engram_navigate` gains `callers` and `callees`. **`neighbors` keeps refusing** — an unspecified
relation answering an empty list is the exact failure the refusal message prevents. Narrow the text;
do not delete it.

### 7.1 Call coverage is DERIVED, not confessed — corrected in revision 3

**AMENDED. Revisions 1 and 2 asked the surface to distinguish "extracted, genuinely zero calls" from
"never extracted" without saying how, and the implementation reasonably fell back to honest-
uncertainty wording. That wording under-claims: most of this is derivable from state that already
exists, and no schema change is needed.**

Two facts about the indexer make it derivable:

- `CodeIndexer.CurrentVersion` is `$"{CodePaths.GrammarVersion}.{CodeAnalyzer.AnalyzerVersion}"`,
  stored **globally** in `schema_meta` under `code_index_version`.
- When the stored value differs from the current one, `versionForcedFull` is set and **every file is
  re-read**; per-file staleness by `file_state.blob_sha` is bypassed entirely.

**That global stamp is stronger than a per-file column would be, and it is why option (ii)'s schema
change is unnecessary.** A per-file marker answers "was this file indexed at v4". The global stamp
plus the forced-full rule answers the same question for *every* file at once, because a version move
cannot leave any file behind. This is the same pattern CLAUDE.md already records for `fact_token`:
**readiness is a stamped version, never a probe.**

So the surface distinguishes **three** states, exactly, and only the third is unknown:

1. **Known out of scope.** The file's extension maps to a tier-0 row in `LanguageRegistry`. Call
   coverage is absent *by design* — a pure lookup, no store access, no schema. **Reporting a `.md`
   file as "coverage unknown" is its own inaccuracy**, and in a docs-heavy repo it is the common
   case.
2. **Known extracted.** `schema_meta.code_index_version` equals `CodeIndexer.CurrentVersion` and the
   file is present in `file_state`. Zero `calls` facts means **genuinely zero calls**.
3. **Genuinely unknown.** The stamp differs (an index run is pending), or the file is absent from
   `file_state` (never indexed). Honest-uncertainty wording belongs **here and only here**.

**The residue, stated because it is real:** a file in state 2 whose tree-sitter grammar was missing
on the run that indexed it downgraded to tier 0 and has no calls, while the stamp says v4. That is a
*machine* property rather than a per-file one — if the grammar was absent, every file of that
language downgraded — and it is reportable from `TreeSitter`'s `downgrades` and from `doctor`, which
already reports tier. **Do not model it per-file**; say it once, from the channel that knows.

**A second residue, from §5.3.2:** a C# file in state 2 whose calls all sit in non-public members
reports its calls against the enclosing *type*. Coverage is complete; granularity is not. That is
§5.3.2's label, not a coverage state — **do not fold it into these three**, which are about whether
the file was processed at all.

**Rejected, with reasons, so they are not re-proposed:**

- **Option (ii), a schema/indexer marker** — unnecessary per the above, and it would add a migration
  to a phase that declares none, to store something already implied.
- **Option (iii), inferring from whether any live `imports`/`calls` fact exists for the file** — this
  conflates *precisely* what item 18 exists to separate: a file that genuinely imports nothing and
  calls nothing is indistinguishable from one never extracted. It is a proxy that reads as
  authoritative, which is the D43 failure — a nearby number standing in for the one you wanted.
- **Option (i) alone** — correct only for state 3. Applied to states 1 and 2 it reports uncertainty
  the system does not actually have, and a surface that says "unknown" when it knows teaches the
  reader to discount every "unknown" it emits (D37's rule about `doctor`).

### 7.2 The other two reports

2. **Report name ambiguity** — §6.1(iii) for `callers`, §6.4 for `callees`. At 22.5% this fires on
   roughly one query in four and is the single most consequential line in the output. **It carries
   the match tier as well as the count** (§6.3.1).
3. **Report truncation.** A capped list says what was dropped and the true total (§6.4).

**All three are independent and can fire at once.** Do not collapse them into one "partial" flag —
they have different fixes (index more files / narrow the name / raise the limit), and a flag that
cannot say which is the flag nobody acts on (D37).

**§5.3.2's attribution label is a fourth and is likewise independent** — its fix is neither indexing
more nor narrowing a name, so it does not fold into any of the three.

---

## 8. Knock-ons

- **`VersionCounts`** — Phase 2's F1 fix excludes edge-bearing predicates. **Confirm it is generic
  over `EdgeBearing`**, not written against `"imports"`, or `calls` reintroduces the false `· vN`
  marker the fix removed.
- **`CodeIndexer`'s diff key** is `(Path, Predicate, Object)`. `calls` facts are subject-anchored on
  the *caller symbol*, so one file's candidates span several subject addresses, and §5.2.1 adds one
  anchored on the file itself. **The most likely place Phase 3 breaks something Phase 2 built.**
- **Call-site deletion needs no new design** (master §5.3): the edge is anchored on the caller's
  symbol, in the caller's file, so the file changing is when the candidate diff runs; the vanished
  edge is unmatched and `FactStore.Forget` closes it with `source changed (<sha8>)`. Only
  `regenerable` facts are touched (D19). **Target movement likewise needs nothing** — the edge never
  mentioned the target's location.
- **`StoreCompactor`** already treats `object_id` as a liveness reference. Confirm.
- **Recall volume.** Phase 2 §5.5 removed edges from the retrieval scan, keyed on `EdgeBearing`, so
  `calls` is covered **provided §5.6's audit holds**. E14's proxy puts the likely fact count in the
  thousands against a 50k corpus, lowering this without removing it.
- **Sidecar publish size** — §5.3.1. Removing the `ProjectReference` is what keeps the ~16 MB
  framework-dependent number that the tier-2 degradation story rests on. Item 24 measures it.

---

## 9. NEEDS-EVIDENCE

**E8 and E13 are answered and retired** — their results are §6.1 and §5.2. What remains gates the
**deferred** name→declaration side index, not this phase — with the exception of **E16**, which can
reopen §5.3.2.

| # | Status | Detail |
|---|---|---|
| **E14** | **partially answered; keep open** | A grep proxy (`\w+\(`, C# only) returned **1,628 distinct callee-like tokens, 11,612 total hits**. It settles the order of magnitude — **thousands, not tens of thousands** — and **1,628 is nearer the fact count**, since §5.5 makes one fact per *pair* and the pattern also matches declarations, `if (`, `catch (` and casts. It does **not** answer E14: no attribution, no dedup, no TS/JS. Re-run against the real extractor. |
| **E15** | **deferred to post-implementation** | The pre-check measured an empty join (0 rows, 9 ms) because no `calls` facts existed. A synthetic seed was **rejected**: right join shape at a cardinality orders off the real one, so "fast" teaches nothing and "slow" is an artifact — both read as evidence and neither is. |
| **E16** | **open; can reopen §5.3.2** | **What fraction of C# `calls` facts have a `type` subject rather than a `symbol` one?** That is the fraction attributed coarsely because the caller was a non-public member. Cheap: one `GROUP BY` over `entity.kind` for live `calls` facts on `.cs` paths, after an index run. **What it decides:** a small fraction leaves §5.3.2 settled as written. A majority means the tier with the most facts answers *"somewhere in this class"* most of the time, which is thin enough to be worth reopening D48's public-surface rule on its own terms. **It does not block the phase** — the answer changes a future decision, not this implementation. |

**What the E15 re-run must include, or it will be uninformative a second time:**

- **Time the fan-out, not an `e.name='Run'` probe.** The cost driver is §6.2 step 2's fan-out over
  every `symbol-name` entity whose leaf is `Run` — `Run`, `this.Run`, `x.Run`, `_host.Run` are
  separate entities — and the join against that whole set.
- **Pair the hot name with a unique-name control arm.** One absolute number cannot separate "the
  join is slow" from "the store is big".
- **Time it through the tool path, not raw SQL** — the trap CLAUDE.md records for `explain`, where
  per-candidate overhead was misattributed to the ranker.
- **NEW in revision 3: include the ranker.** `CodeCallGraph.ImportsFile` issues **one SQL query per
  candidate file**, inside an `.Any(...)`, inside a per-candidate winner-selection chain. That is an
  N+1 on the query path and it was outside E15's original framing entirely. §6.4's demotion of that
  signal below `QualifierAgreement` will also *reduce* how often it runs, since the chain
  short-circuits — so measure after the reorder, not before, or the number describes code that no
  longer exists.

---

## 10. Acceptance

Each with a test shown failing before its change:

1. A TS file with two functions, each calling a distinct target, yields two live `calls` facts with
   the correct **caller symbol** as subject and `/symbol-names/…` objects.
2. **A call nested inside an `if` inside a function attributes to that function** (§5.2) — the case
   the nested query missed and the reason the walk exists. Add the sibling for a call inside a
   callback argument.
3. Three calls to one target from one function yield **exactly one** fact (§5.5); re-indexing an
   unchanged file writes nothing.
4. A module-level call attributes to the **file** entity, kind `file` (§5.2.1) — and item 5's
   assertion exempts it explicitly rather than being widened.
5. Every `calls` fact **whose subject kind is `symbol`** matches a `declared-as` address in the same
   file. Assert for **both** tiers; the C# fragment is rebuilt by a second walk and is likelier to
   drift.
6. A language row with `CallQuery = null` still produces declarations and imports (**C2**).
   Falsify by adding `CallQuery` to `Analyze`'s null-guard.
7. A `calls` candidate survives `DeepTier.Merge`; removing the emit reddens it (§5.4).
8. A C# file with one unparseable sibling in the same batch still yields the good file's calls.
9. `fact_fts` and `fact_token` row counts unchanged by an index run writing `calls` (`fact_fts` via
   `fts5vocab`).
10. Phase 2 §7.1's `INTERSECT` and §7.2's addressing lint both empty with two `EdgeBearing` members.
11. `callers(X)` finds a call written `path.join` when asked for `join` (**C4**).
12. A `calls` edge naming `Foo` does not resolve to `FooBar` (§6.3's ceiling).
13. `LeafOf` passes its table (§6.5).
14. `callers` on a multi-declaration name returns the superset **and says so**; on a unique name it
    says nothing about ambiguity. **Both halves** — a label that always fires is as useless as one
    that never does.
15. `callers` orders a same-file caller above an unrelated one **through the same ranker `callees`
    uses** (§6.1(iv)). Falsify by giving `callers` its own copy and asserting a shared-behaviour
    test reddens — a second implementation that happens to agree today is the defect.
16. **(§6.4): an exact qualifier match outranks a filename-only import match.** Construct a
    candidate where both fire and assert `QualifierAgreement` wins. **Falsify by restoring the
    shipped order** — this is the item that catches the defect revision 3 corrects, and without it
    the reorder can be reverted with everything else green.
17. **(§6.4): the reported rank reason for signal 3 names a filename match, not
    import-consistency.** A string assertion, deliberately — the explanation is the product here.
18. `callees` ranks a same-file declaration above a same-repo one, and each entry reports which
    signal placed it (§6.4).
19. A capped list states the true total (§6.4).
20. **REPLACES the old item 18 (§7.1): the three coverage states are distinguished.** A tier-0 file
    reports **out of scope**, not unknown; a tier-1/2 file indexed at the current stamp with no calls
    reports **genuinely zero**; a file absent from `file_state`, or any file when the stamp differs,
    reports **unknown**. **All three, and the tier-0 arm is the load-bearing one** — it is the common
    case and the one the honest-uncertainty wording got wrong in the direction of sounding careful.
21. `neighbors` still refuses, with narrowed text.
22. The §5.6 single-membership audit is reported with the diff.
23. `VersionCounts` still excludes edge predicates with `calls` present (§8).
24. **(§5.3.1, B1): the sidecar carries no Engram project reference and no llama payload.** Two
    halves, both required, because they fail differently:
    - **An assertion that reddens.** `engram-roslyn.csproj` contains no `ProjectReference`, and the
      published sidecar directory contains no `libllama*`, no `LLamaSharp*` and no `runtimes/`
      tree. **Falsify by restoring the `ProjectReference`.** This is the durable guard — a size
      number in a report does not fail a build a year from now.
    - **A measured before/after publish size, recorded in the change description.** Publish the
      sidecar with the reference and without, and state both numbers. "It's fine" is not evidence:
      D45's own history with this exact package is that the unmeasured answer was wrong by 89 MB.
25. **(§5.3.2, S3): a call inside a private C# method attributes to the enclosing type, and the
    result says so.** Both halves. Assert the subject is the type entity — that is the ruled
    behaviour, not a defect — **and** assert the rendered `callers` output carries the attribution
    label. **Falsify the label half by deleting it**: without that arm the coarse answer ships
    silently, which is the failure §5.3.2 exists to prevent. Add the contrast arm: a call in a
    **public** method attributes to the method and carries **no** label.
26. **(§5.3.1): enclosing-symbol identity survives a filtered symbol.** A C# fixture whose emitted
    symbols include one that core's reader drops (empty `name`) still attributes every call to the
    correct enclosing symbol. **Falsify by changing `enclosing_id` to an array index** — this is the
    arm that proves the id is explicit rather than positional, and it is the only way that
    distinction can fail visibly.
27. **NEW in revision 5 (§6.1(iii), N2): the declaration count is scoped to the returned set.** With
    a `repoNeedle` given, seed the same leaf name in two repos and assert the label counts only the
    scoped declarations — the same population the callers were drawn from. **Falsify by removing
    `repoNeedle` from the `:67` resolve**, which restores the store-wide count the spec used to ask
    for and makes the label describe an ambiguity the reader is not looking at.
28. **NEW in revision 5 (§6.3.1): the label reports the match tier when it is not exact.** A query
    matching nothing exactly but matching by substring returns callers **and** says the match was a
    substring one. Assert both the tier string and that an **exact**-matching query says nothing
    about tiers — **both halves**, same rule as item 14: a qualifier that always fires is noise.

Every falsification asserts `git diff --quiet` first (D60): a harness restoring arms with
`git checkout --` reverts an uncommitted change under test, and every arm reds for the wrong reason.

---

## 11. Confidence

**No open design questions remain.** Both revision-1 escalations came back and neither reversed the
spec; revision 3's two gaps, revision 4's two findings and revision 5's are ruled here.

- **§4, §5.1, §5.4, §5.5, §5.6, §6.2, §6.5, §10** — high. Read from source; failure modes named
  with guards.
- **§2's corrections** — high. C2 and C4 would have shipped as silent defects; C1 is settled by
  measurement.
- **§5.2** — high. E13 compiled the alternative and it failed unpatchably: no descendant operator,
  so nested patterns need one per (function form × wrapper shape) and miss **silently**.
- **§5.3.1 — high, and it corrects a defect this spec caused.** Revision 3 told the sidecar to
  produce an identical fragment, which is an instruction to either share or duplicate core's logic;
  the third option — emit fields and let core address them — is what the sidecar already does for
  every other datum, so this is a return to the existing pattern rather than a new one. The
  explicit-id-not-index rule is the part most likely to be "simplified" later, which is why item 26
  falsifies it directly.
- **§5.3.2 — moderate, and deliberately reopenable.** The ruling is right on blast radius: Phase 3
  may not redefine what a C# declaration is. Whether the coarse answer is *useful enough* is a
  different question, unmeasured, and **E16 answers it cheaply**. If E16 comes back a majority, this
  section should be reopened rather than defended.
- **§6.1(iii)'s scope rule — high, and the spec was the thing that was wrong.** Scope agreement was
  already implemented correctly; the literal `COUNT(*)` I wrote would have made the label describe a
  population the answer was not drawn from. The generalized rule — *a label is computed over the
  population the answer came from* — is the durable half.
- **§6.3.1 — high on the finding, moderate on the remedy.** That `:67` feeds the ambiguity count
  through the full ladder is read from source and is not in doubt. Keeping the ladder and reporting
  the tier is the least-damaging fix, but it is my construction: the alternative (ceiling the
  user-typed sites too) is defensible and would simply refuse `Ru`. If the tier string proves noisy
  in practice, revisit — the choice is one argument at two call sites.
- **§6.3.2 — high.** The bound cannot fire at any observed scale, `1000+` rendering costs one line,
  and declining to write an unfalsifiable guard is the rule this repo already holds.
- **§6.1** — high, affirmed at ~90%, with a correction to revision 1's own overstatement (the
  qualifier *is* stored; only the receiver's static type is missing). §6.4 signal 2 depends on that
  correction, so revisit both together or neither.
- **§6.4's order — high, and higher than the original.** The exact-outranks-approximate rule is a
  stated principle rather than a preference, and it is what revisions 1–2 were missing. The
  *membership* of the five tiers is still my unmeasured construction.
- **§6.4 signal 3's approximation — high on accepting it, moderate on it being worth keeping.** It
  is sound to accept because it ranks and never filters. Whether it earns its N+1 (§9) once demoted
  is an open measurement, not an open design question: if it rarely changes an order, delete it.
- **§7.1 — high.** The derivation rests on two things read from source (the global `code_index_version`
  stamp, and `versionForcedFull` bypassing per-file staleness), and it matches a pattern the codebase
  already uses for `fact_token`.
- **§5.2.1 — moderate, and it is mine.** The file-scope case did not exist under a nested query.
  Attributing to the file entity is consistent with everything else here, but it is the one call
  taken without evidence. One branch in §5.4's emit reverses it.
- **§8's knock-ons** — the `CodeIndexer` diff-key item is unverified and is where I would look first.
