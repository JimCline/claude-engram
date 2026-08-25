# Indexing all members in the code graph

**Status:** design, **revision 2**. Written by the Architect. Not yet implemented.

**Revision history.** Rev 1 scoped this to C# / tier 2 and deferred TypeScript and JavaScript,
flagging the language scope as Jim's call. **Jim ruled: widen both together, not sequentially.**
Rev 2 folds tier 1 in with the same rigor, and corrects two things rev 1 got wrong about tier 1
(§6.1). All rev 1 findings about C# stand unchanged.

**Requested by:** Jim — *"I think private, public, protected, all members should be indexed in
the graph"*, *"the whole point is for the LLM to know your code."*

**Reported symptom:** `engram_navigate defined_at "WriteEntry"` returns *No symbol named
'WriteEntry' found* for a `private static` method on `MemoryReport`, and still does after a forced
fresh index. Confirmed live. This is not a staleness bug; it is the extraction policy working as
D48 specified.

---

## 1. What is actually excluded today, per tier

Both tiers are described in D48 and in `engram-progress.md` as emitting *"the public surface."*
Neither does exactly that, and the two differ from each other in a way that matters for how much
this change moves.

### 1.1 Tier 2 (C#, Roslyn sidecar)

`src/Engram.Sidecar.Roslyn/Program.cs:125–131`:

```csharp
if (!inInterface && !member.Modifiers.Any(m =>
    m.IsKind(SyntaxKind.PublicKeyword)
        || m.IsKind(SyntaxKind.InternalKeyword)
        || m.IsKind(SyntaxKind.ProtectedKeyword)))
{
    return;
}
```

`internal` and `protected` are **already emitted**, as are all interface members and
`private protected` (its `ProtectedKeyword` satisfies the test). Excluded today:

1. Members whose modifier list contains `private` and nothing else from that trio.
2. Members with **no accessibility modifier at all** — implicitly private on a class or struct.
   This is the larger population and the one nobody says out loud. `static void Helper()` inside a
   class is skipped today.
3. As a side effect of (2): **static constructors**. `static Foo()` carries only `StaticKeyword`,
   so it fails the filter and is not emitted at all.

### 1.2 Tier 1 (TypeScript and JavaScript, tree-sitter)

`src/Engram.Core/TreeSitter.cs:191–198`:

```csharp
// Grammar v2 (D48): a `private` member is implementation, not surface —
// the queries cannot express negation, so the modifier is read off the
// declaration line. The `#name` form never gets this far: the member
// patterns capture (property_identifier), which a private name is not.
if (scope is not null && line.StartsWith("private ", StringComparison.Ordinal))
{
    continue;
}
```

Two independent exclusion mechanisms, and **only one of them is that `if`**:

1. **The `private ` line-prefix check.** Applies only when `scope is not null` — i.e. to members,
   never to top-level declarations.
2. **`#name` private fields, excluded structurally.** The declaration query patterns in
   `LanguageRegistry.cs:118–146` (TypeScript) and `:148–159` (JavaScript) capture
   `(property_identifier)`. A `#name` member is a `private_property_identifier`, a different node
   type, so it never produces a match at all. Deleting the `if` does nothing for these.

### 1.3 The asymmetry that makes tier 1's gap much smaller than tier 2's

**TypeScript and JavaScript class members default to public.** A member written with no modifier —
`foo() {}`, `x = 1` — has no `private ` prefix, so it is **already emitted today**. In C#, the
same no-modifier member is implicitly *private* and is excluded.

So the populations differ sharply:

| | tier 2 (C#) | tier 1 (TS/JS) |
|---|---|---|
| no modifier | **excluded** (implicitly private) | already emitted (implicitly public) |
| explicit `private` | excluded | excluded by `:195` |
| runtime-private (`#name`) | n/a | excluded **structurally**, by the query |
| `internal` / `protected` | already emitted | already emitted |

**Consequence.** Tier 2's change admits a large population. Tier 1's `if` deletion admits only
members someone explicitly typed `private` in front of — a genuinely smaller set. This asymmetry
is the main reason E3 must attribute per tier (§8.3) rather than measuring one combined number.

---

## 2. Question 1 — why non-public emission was excluded, recovered rather than guessed

There are two recorded rationales and they are **not equally strong**, which is the single most
important thing in this document.

### 2.1 D48's rationale for the visibility filters — a design position, now overridden

`docs/engram-implementation-plan.md:2722–2737`:

> **What the tiers emit is policy, and the filter is syntactic.** Tier 2 emits every type
> declaration at any depth … and the members that are surface: an explicit `public`, `internal`,
> or `protected` modifier, or membership in an interface, where the language makes them public
> implicitly. **A bare private member is implementation, not interface** — the same line the
> registry already draws for unexported `const`/`let`/`var`.

`TreeSitter.cs:191–194` states the same position for tier 1 in its own words: *"a `private` member
is implementation, not surface."*

That is a claim about what a code fact is *for*: the index describes the interface a reader has to
program against, not the implementation behind it.

Jim's instruction rejects that premise directly. *"The whole point is for the LLM to know your
code"* — the consumer is a model reading the implementation, not a caller programming against a
public API. For that consumer, a private helper is not noise; it is most of the code.

**This is the user's call to make and he has made it.** D48's policy paragraph is therefore
**revised, not silently overridden** — see §7.1. The Implementor does not get to skip that edit,
and the comment at `TreeSitter.cs:191–194` is part of it.

### 2.2 The S3 rationale — a phase-scope refusal, and it discharges on its own terms

`docs/code-navigation-phase3-spec.md:286–321`, §5.3.2:

> **Ruled in revision 4; revision 3 was silent and the silence shipped.** `EmitMember` skips
> non-public members, so the walk — which stops at the nearest node in the *emitted*-symbol map —
> passes a private method and lands on its enclosing class. … **Accept the type attribution. Do
> not extend emission to non-public members in this phase.** The reasoning is blast radius, not
> elegance. … **Phase 3 does not get to redefine what a declaration is in order to make its own
> edges finer.**

Read precisely, S3 forbids *a phase widening the definition of a declaration in order to improve
that phase's own call edges*. It is a conflict-of-interest rule: the party that benefits from the
wider definition does not get to grant it.

This request is not that. It comes from outside the call-graph work, from the user, and its
justification is about `defined_at` and readability rather than about edge precision — the finer
call attribution in §5.3 is a **consequence** of this change, not its motive. The constraint S3
imposed is satisfied by the request being made at the right level, not by being argued away.

**So S3 does not block this and needs no re-litigation.** It should be annotated as discharged
(§7.2), with the reason, so nobody reads the phase-3 spec later and thinks the rule was ignored.

### 2.3 The rationale that does *not* discharge, and is the real risk

D48's exclusion of certain **kinds** rests on something harder than a design position
(`engram-implementation-plan.md:2734`, echoed at `engram-progress.md:478–485`):

> Deliberately not emitted anywhere: enum members, indexers, operators, local functions — each is
> a large population of low-recall-value facts, and **D44 already measured what a store full of
> near-noise does to lexical ranking.**

That argument is attached to the *kind* list, not to the visibility filters. It does **not**
automatically transfer to private members — a private method with a real name and a real body is
not obviously in the same class as an enum member. But it is the same shape of risk: this change
adds a large population of facts to a store whose recall quality D44 showed is sensitive to
exactly that.

**This is the one thing in this change that must be measured rather than argued.** See E3 in §8.
If E3 shows displacement, the fix is ranking or salience, **not** a config switch — see §6.4.

---

## 3. Question 2 — what happens to coarse attribution

**It does not become dead code, and its test is a trap.**

The tier-2 walk (`Program.cs:212–232`) climbs from an invocation to the nearest ancestor present in
the emitted-symbol map; tier 1's equivalent walk is at `TreeSitter.cs:245–299`. Widening emission
makes both stop *sooner*, never later. So:

- **Calls inside private/no-modifier methods** stop folding to the enclosing type. They attribute
  to the method, which is the fix Jim asked for.
- **Calls inside local functions** now land on the enclosing method — including a private one —
  rather than on the type. Strictly finer, still coarse.
- **Calls inside indexers, operators, and enum-member initializers** still fold to the enclosing
  type, because those kinds remain unemitted (§4.3). The population is small but non-empty.
- **Calls with no emitted ancestor at all** still attribute to the file (Phase 3 §5.2.1). Top-level
  statements and file-scoped code keep that path.
- **Tier 1: calls inside `#name` members** still fold, because §6.2 leaves `#name` excluded.

**Therefore the coarse-attribution behaviour and its query-surface label both stay.** Do not
delete either. Phase 3's rule — *"coarse but labelled beats precise but wrong, and both beat
silent"* — is unchanged; only the population it applies to shrinks.

### 3.1 The trap: Phase 3 acceptance item 25 will silently invert

Item 25 is *"private C# method calls attribute to the enclosing type with a mandatory label."*
After this change that assertion is **false by design**, so the guard either fails (good, noisy) or
— if it is written loosely enough to pass on any labelled attribution — keeps passing while
guarding nothing.

**A search of `src/` did not locate this test.** That is a finding, not a claim that it is absent:
it may be named without the words searched for, or it may never have been written. **The
Implementor must locate it before touching `EmitMember`** and report which. Then:

- **If it exists:** retarget its fixture from a private method to a still-excluded kind — an
  **indexer body** is the clearest — so the guard keeps testing coarse attribution against
  something that is still coarse. Do not delete it.
- **If it does not exist:** say so in the report. Do not write it as part of this change; that is
  a separate gap in Phase 3's coverage and folding it in here hides which change it belongs to.

### 3.2 Two contract comments become wrong

**`Program.cs:203–211`:**

```
/// its 1-based line, and the `id` of the nearest emitted symbol enclosing it. Emission is
/// the public surface (`EmitMember` skips non-public members), so a call inside a private
/// method or a local function attributes to the nearest emitted ancestor — usually the
/// enclosing type — never to nothing.
```

Two of its sentences become false. Rewrite it to describe the new emission set and the *remaining*
coarse cases (local functions, indexers, operators, file-level). This is a behaviour contract on a
cross-process wire format, not decoration — it is the only place the sidecar states what
`enclosing_id` means.

**`TreeSitter.cs:191–194`** is the comment attached to the `if` being deleted. Its first sentence
states the overridden policy; its last two sentences describe `#name`'s structural exclusion, which
**remains true and must survive** (§6.2). Do not delete the comment wholesale with the `if` — keep
the `#name` explanation, relocated to the query constants it actually describes.

---

## 4. Question 3 — collision risk, and the number that already exists

### 4.1 The measured baseline

From Phase 3 spec §6.1, measurement E8, against this repo's real store:

| | |
|---|---|
| distinct symbol leaf names | 5,355 |
| exactly one declaration | 4,151 (77.5%) |
| ambiguous (>1 declaration) | 1,204 (22.5%) |
| worst offenders | `Run` 40, `Read` 36, `Dispose` 35, `Resolve` 32 |

That measurement is *why* `calls` edges keep a name-keyed `object_id` instead of resolving at
index time, and why `callers` is specified as a **labelled superset** rather than an answer.

### 4.2 Does the existing handling scale?

Structurally, yes — the design already assumes ambiguity rather than tolerating it:

- `object_id` on a code edge is *the callee as written*, never a resolved declaration. Widening the
  declaration population does not change any stored edge.
- `callers(X)` is a direct lookup on the name entity and needs no join at all. Its answer was
  already a superset for any ambiguous name; it stays a superset.
- `SymbolResolver.Resolve` (`src/Engram.Core/SymbolResolver.cs:45–84`) is a three-tier ladder —
  Exact, then CaseInsensitive, then Substring — each stopping at the first tier that matches, with
  the tier reported when it is not Exact.

Two effects, and they point in opposite directions:

**Better.** The Substring tier — described in this codebase's own notes as behaving like a
*fabrication engine* at scale — fires only when Exact and CaseInsensitive both find **nothing**.
Widening emission makes Exact hit more often, so the worst tier fires *less*. `WriteEntry` today
either misses entirely or falls to substring; after the change it is an Exact hit.

**Worse.** The ambiguity rate rises, and `callers`/`defined_at` supersets widen with it. Private
helpers are exactly the population that reuses short generic names — `Write`, `Format`, `Parse`,
`Emit`, `Run`. The 22.5% figure will go up. By how much is **not estimable from here** — see E2.

### 4.3 One new collision the design does not currently anticipate

**A private constructor emits a symbol whose leaf name equals its type's name.** `Emit(...
constructor.Identifier.Text, "constructor", ...)` uses the type name as the symbol name. Private
constructors are common (singletons, static holders, `record` guards), and today they are all
skipped. After this change, `defined_at "MemoryReport"` can return both the class and its
constructor.

This is **not** an addressing bug — D48's grammar appends the parameter list when several symbols
in one file share a base, so the two addresses stay distinct. It is a *leaf-name* ambiguity that
raises the count reported to the user. Acceptable, but it must be stated, because a `defined_at`
that starts returning two rows for a type name will otherwise read as a regression.

---

## 5. The change — tier 2 (C#)

### 5.1 `src/Engram.Sidecar.Roslyn/Program.cs` — `EmitMember`

**Delete the guard at `:125–131` in full.** No replacement condition. Every member reaching
`EmitMember` is emitted, and the `switch` at `:133–163` remains the sole gate on *kind*.

The `inInterface` parameter becomes unused by the filter. **Check whether `EmitMember` still needs
it** before removing it from the signature — if nothing else reads it, remove it and its call
sites; warnings are errors in this repo, so a dangling parameter is not an option to leave.

**Kinds are unchanged.** Methods, constructors, properties, fields, events. Indexers, operators,
enum members, local functions, and delegate members stay unemitted. Jim asked about
*visibility* — *"private, public, protected"* — and D48's kind exclusions rest on the D44
measurement (§2.3), which this change has no evidence against. Widening kinds is a separate
decision needing its own argument.

### 5.2 Partial methods produce duplicate addresses — this must be handled

`partial void OnChanged();` and its implementing `partial void OnChanged() { … }` are two
`MethodDeclarationSyntax` nodes, in the same scope, with **identical parameter lists**. Both are
implicitly private in the common form, so both are excluded today and both become emitted.

D48's collision-only overload suffix appends the parameter list — which is identical here, so it
**does not disambiguate them**. Two symbols would claim one address.

**Required behaviour:** when two emitted symbols in one file share name, scope, kind, and parameter
list, emit **one** — the one with a body. If neither or both have a body, keep the first in source
order. Do this in the sidecar, where both declarations are visible in one syntax tree.

This is a genuine new defect created by the change, not a pre-existing one worth broadening scope
for: today it can only occur for the rare C#9+ `public partial` form. Do not "fix" the general
case; handle exactly the duplicate-address condition described.

### 5.3 Static constructors become symbols

A consequence of §5.1, called out so it is not mistaken for a bug: `static Foo()` now emits a
`constructor` symbol. Calls inside it — commonly one-time initialization — attribute to it instead
of to the type. Correct and desirable; simply new.

---

## 6. The change — tier 1 (TypeScript and JavaScript)

### 6.1 Two corrections to revision 1

Rev 1 deferred tier 1 partly on the grounds that its filter was not an analogous deletable check
and that its location was unconfirmed. **Both were wrong, and the record should say so:**

- The `private ` filter **is** a single deletable `if` at `TreeSitter.cs:195`, directly analogous
  to tier 2's guard. Rev 1's caution was unnecessary for that half.
- Rev 1's measurement-separability argument **survives** and is now handled by §8.3's three-arm
  design rather than by sequencing the delivery.

What rev 1 got right, and what turns out to be the substance of the tier-1 question, is that
TS/JS privacy is not one thing. See §6.2.

### 6.2 TS/JS has two gaps, and they are different work

**Gap A — the `private ` keyword.** Delete the `if` at `TreeSitter.cs:195`. This admits members
someone explicitly typed `private` in front of. TypeScript's `private` is **erased at compile
time**: it constrains the type checker and nothing else. Nothing at runtime is hidden.

**Gap B — `#name` private fields and methods.** These are excluded *structurally*: the query
patterns capture `(property_identifier)` and a `#name` member is a `private_property_identifier`.
Closing this gap means **adding capture patterns** to `TypeScriptDeclarations`
(`LanguageRegistry.cs:118–146`) and `JavaScriptDeclarations` (`:148–159`) — authoring query text,
not deleting a check.

**The ordering is the point, and it is counterintuitive.** `#name` is the *only* true, runtime-
enforced privacy in the language. `private` is a comment the compiler checks. So closing Gap A
alone widens the **fake** private and leaves the **real** one excluded — a half-measure that
would read as done and satisfy nobody who asked for "all members."

**Ruled: close both.** Jim's instruction is about what the model can see, and `#name` members are
exactly the implementation detail he is asking to expose. Closing A without B would be the same
category of silent half-answer this repo's specs keep recording.

### 6.3 What closing Gap B requires, and its one real risk

Add `(private_property_identifier)` alongside `(property_identifier)` in the member patterns of
both declaration query constants. Constraints the Implementor must respect:

- **Tree-sitter queries are compiled at runtime by `ts_query_new`** and a malformed pattern fails
  with a structural error rather than matching nothing. A pattern that does not compile takes the
  whole language's extraction down, not just the new members. Verify against the real pinned
  grammars, not by inspection.
- **The grammar must actually have that node type.** `private_property_identifier` is the expected
  name for both the TypeScript and JavaScript grammars, but **this was not verified** during
  design and I have no way to verify it. See E6.
- **`#name` includes the `#`.** Whether the captured text carries the leading `#` determines the
  symbol's name and therefore its address and its `defined_at` key. **Ruled: keep the `#`.** It is
  part of the member's name in the language — `this.#count` is how it is written and how someone
  will search for it — and stripping it would collide a `#count` field with a public `count`
  field in the same class, which is a legal and common pairing.
- **Do not touch the `_`-prefix convention.** `Matches()` skips captures whose *capture name*
  starts with `_` (`TreeSitter.cs:446`); that is about predicate-only captures and has nothing to
  do with member visibility. It is easy to misread as a second visibility filter. It is not one.

### 6.4 The existing tier-1 filter is approximate, which affects how E3 reads

`line.StartsWith("private ", StringComparison.Ordinal)` is a **line-prefix** test. It therefore
misses `private` members whose declaration line does not begin with the keyword — a decorated
member (`@inject() private readonly svc: Svc`) is the common case, and any formatting that puts
something before the modifier has the same effect.

So **some `private` TS members are already indexed today**. The "before" state is not a clean
public-surface store; it is a store with an approximate filter. Two consequences:

1. This argues *for* deleting the check rather than against: an approximate filter enforcing a
   policy we are abandoning is strictly worse than no filter.
2. E3's tier-1 arm will show a **smaller** delta than the true `private` population implies,
   because part of that population is already present. Do not read a small tier-1 delta as evidence
   the change did nothing.

### 6.5 No configuration knob — and what to do instead if E3 is bad

Do **not** add a setting for "index private members," in either tier. One behaviour, measured. A
knob here would be a second answer to a question the store should have one answer to, and this
codebase has already paid for what happens when a retired or duplicated setting reads like a live
one.

If E3 shows private members displacing ordinary facts in recall, the correct responses, in order,
are: lower salience for private-member code facts; or exclude them from the lexical index while
keeping them as facts (so `defined_at` and call attribution still work while ranking does not see
them). Both are real designs and neither is in scope here. **Escalate rather than reaching for a
switch.**

---

## 7. Versioning, and documents that must be edited

### 7.1 One version bump covers both tiers

`src/Engram.Core/CodeAnalyzer.cs:31`:

```csharp
public const int AnalyzerVersion = 4;   // → 5
```

**`CodePaths.GrammarVersion` stays at 2.** This is the load-bearing versioning call and the reason
matters: grammar version governs *how a code subject is addressed*, and a private member receives
exactly the address the same member would receive if it were public — in both tiers. Nothing about
fragment composition changes. What changes is *which members the extractors observe*, which is
precisely what `AnalyzerVersion` exists to track — the same split Phase 3 applied when call edges
reused declaration addressing unchanged.

`CodeIndexer.CurrentVersion` is `$"{CodePaths.GrammarVersion}.{CodeAnalyzer.AnalyzerVersion}"`
(`CodeIndexer.cs:79`), so the stored `code_index_version` moves `2.4` → `2.5`, setting
`versionForcedFull` and re-reading every indexed file **regardless of tier**. One bump serves both
halves of this change; do not add a second version constant.

**No schema change, no migration, no snapshot.** New members arrive as new facts; no existing fact
body is modified, so D8 is untouched and the unchanged-body skip in `ProcessFile` is irrelevant.

**Testing note the Implementor will otherwise get wrong:** passing `full: true` bypasses the
version-forced re-read gate entirely. A test that an `AnalyzerVersion` bump re-reads existing
stores must roll the stored version back and index with `full: false`, or it exercises nothing.

### 7.2 `docs/engram-implementation-plan.md:2722–2737` — revise D48's policy paragraph

Replace the emission-policy sentences **for both tiers** so they describe the new set: every member
of an emitted kind, at every visibility, including `#name` members in TS/JS. Keep the kind
exclusions and keep their D44 rationale verbatim — that argument is untouched by this change
(§2.3).

Record *why* the visibility line moved, in D48's own voice and beside the sentence it replaces:
the filter drew the interface/implementation line for a reader programming against an API, and the
consumer is a model reading the implementation. Do not delete the old reasoning — this repo's
convention is that a superseded argument stays visible so the next person knows it was considered.

**`grammar_version` in `docs/engram-path-grammar.md` stays at 2.** Confirm nothing in that document
states an emission policy that this change falsifies; if it does, that sentence moves or is
corrected, but the version does not.

### 7.3 `docs/code-navigation-phase3-spec.md:286–321` — annotate S3 as discharged

Do not rewrite §5.3.2's reasoning; it was correct for the question it answered. Add a dated note at
the head of the section recording that emission was widened by a later, user-level decision (this
spec, by path), that S3's conflict-of-interest rule was not violated because the widening was not
granted by the phase that benefits from it, and that the coarse-attribution behaviour §5.3.2
specifies **remains live** for local functions, indexers, and operators.

### 7.4 `docs/engram-progress.md:478–485` — update the tier line

That paragraph states both tiers' old policy in one sentence — *"the sidecar … emits surface
members only … tier 1 filters `private` on the declaration line and `#name` members never match
structurally."* All three clauses become false. Rewrite; keep the sentence about kinds and D44.

### 7.5 `src/Engram.Core/CodeIndexer.cs` — no change

The original brief lists it. Nothing in it filters by visibility — it dispatches by language tier
and compares SHAs. Named here only to record that it was checked and is not in scope.

---

## 8. NEEDS-EVIDENCE

I cannot run anything. Each item names what to run and **what each outcome decides** — none is a
curiosity. Every command must set `ENGRAM_HOME` or pass `--home`, per CLAUDE.md; do not run these
against the real `~/.engram`.

### 8.1 The corpus problem, which must be solved before E1–E4 mean anything

**This repo is C#.** Measuring tier 1's effect against `engram` itself would report approximately
zero and prove nothing. The tier-1 arms need a corpus with real TypeScript or JavaScript in it.

`claudetools` is the obvious candidate — the agent-hierarchy plugin is `.mjs` — but **its file
count and size are unverified from here.** First step: count TS/JS files in candidate repos and
pick one large enough that a change in extraction policy is visible. If no available corpus has
enough TS/JS to measure, **say so and report the tier-1 arms as unmeasured** rather than reporting
a number from a corpus too small to carry it. An unmeasured arm honestly labelled is a result; a
near-zero delta from a three-file corpus presented as evidence is not.

### 8.2 The measurements

**E1 — fact and entity growth.** Index the chosen corpora into disposable homes; record code-fact
count and entity count. Report per tier. *Decides:* whether "measured, not estimated" is satisfied
for the size claim. Baseline for scale: the live instance holds ~6,400 code facts of ~15,000 total,
in a 217 MB store.

**E2 — ambiguity distribution, a re-run of E8.** Distinct symbol leaf names, count with exactly one
declaration, count with more, and the top ten by declaration count. *Decides:* how much wider
`callers`/`defined_at` supersets get. Baseline in §4.1. If the ambiguous fraction roughly doubles,
that is expected and acceptable; if the worst offenders reach three figures, `callers` on a common
name stops being a usable answer and §6.5's salience work becomes required rather than contingent.

**E3 — recall quality. The one that can veto this change.** See §8.3 for how it must be run.

**E4 — index cost.** Wall-clock for a full index, and store size, before and after, per tier.
*Decides:* nothing on its own, but a large regression is worth knowing before a user discovers it
waiting on `engram index`.

**E5 — does Phase 3 acceptance item 25's test exist?** Locate it or establish it was never written.
*Decides:* §3.1's branch. Must be answered **before** `EmitMember` is edited, because after the
edit a missing guard and a silently-inverted one look identical. **Blocking.**

**E6 — does the grammar expose `private_property_identifier`?** Compile the amended TypeScript and
JavaScript declaration queries against the **real pinned grammars** and confirm they compile and
match a `#name` member. *Decides:* whether §6.2's Gap B is closable as specified. A query that
fails to compile takes the whole language's extraction down, so this cannot be assumed from the
node name looking right. **Blocking for the tier-1 half only** — the tier-2 half can proceed
without it.

### 8.3 E3's design — how a regression stays attributable

The Orchestrator's constraint is that shipping both tiers together must not make a recall
regression unattributable. It does not, provided the measurement is structured by **commit**
rather than by configuration:

**Two commits, one delivery, three measurement points.**

| Arm | Tree | Attributes |
|---|---|---|
| 0 | before both changes | baseline |
| 1 | after the tier-2 commit only | arm 1 − arm 0 = **C#** |
| 2 | after the tier-1 commit | arm 2 − arm 1 = **TS/JS** |

This needs no configuration knob (§6.5 forbids one), uses git rather than a runtime switch, and
matches this repo's existing discipline of falsifying against a committed tree. Order the commits
tier 2 first, since it is the larger change and the one with the reported defect behind it.

**What E3 actually measures.** Before and after each arm, run a fixed set of **non-code** recall
queries drawn from real recorded history — personal facts, past decisions, session notes — and
compare what comes back, `coverage` value included. *Decides:* whether §2.3's D44 risk is real for
private members.

Two rules that decide whether the result means anything:

- **Use real recorded queries.** A hand-written query set will flatter the change, because whoever
  writes it knows what was added.
- **If ordinary facts get displaced, stop and escalate to §6.5's options.** Do not ship and do not
  reach for a switch.

---

## 9. Acceptance

**Tier 2 (C#)**

1. `EmitMember`'s visibility guard is gone; no replacement accessibility condition anywhere in
   `Program.cs`. **Falsify:** restore the guard — items 2, 3, and 5 must redden.
2. `defined_at "WriteEntry"` resolves to `MemoryReport.WriteEntry` at Exact tier after a re-index.
   This is the reported defect; it is the headline test.
3. A method with **no accessibility modifier** on a class is emitted. **Falsify:** re-add only the
   no-modifier half of the old condition — this must redden while item 2 stays green. Load-bearing:
   a guard that only tests explicit `private` passes with half the defect restored.
4. Interface members are still emitted, and `internal`/`protected`/`private protected` members are
   still emitted. Guards against a "simplification" that swaps one filter for another.
5. A call written inside a private method attributes to **that method**, not to the enclosing type.
6. A call inside a **local function** inside a private method attributes to the private method.
7. A call inside an **indexer body** still attributes to the enclosing type, with the label intact.
   This is the retargeted item-25 guard (§3.1) and the proof coarse attribution is still live.
8. Indexers, operators, enum members, and local functions are still **not** emitted as symbols.
   **Falsify:** emit one — this must redden. Guards the kind boundary against scope creep.
9. A file containing a partial-method declaration and its implementation yields **one** symbol for
   that name, the one with the body (§5.2). **Falsify:** emit both — a duplicate address must be
   detectable, not silently deduped downstream.
10. A `static` constructor is emitted as a `constructor` symbol.
11. A private constructor is emitted, and its address is distinct from its type's (§4.3).

**Tier 1 (TypeScript / JavaScript)**

12. A TS member declared `private foo()` is emitted. **Falsify:** restore the `if` at
    `TreeSitter.cs:195` — this must redden.
13. A **decorated** private member (`@dec() private x`) is emitted both before and after the
    change. Guards §6.4's observation: it passes today, and it must keep passing, so nobody
    "fixes" the approximate filter into a stricter one on the way past.
14. A `#name` private field and a `#name` private method are both emitted, with the `#` retained in
    the symbol name (§6.3). **Falsify:** revert the query-pattern addition — this must redden while
    item 12 stays green. Load-bearing: it is what separates Gap B from Gap A, and closing only A
    would otherwise look complete.
15. A class containing both `#count` and `count` yields **two distinct symbols** with distinct
    addresses (§6.3).
16. Both amended declaration queries **compile** against the real pinned grammars. This is E6
    promoted to a guard, because a non-compiling query fails the whole language rather than one
    pattern.
17. TS/JS members with no modifier are still emitted (they always were — §1.3). Guards against a
    tier-1 edit that accidentally narrows while widening.

**Shared**

18. `CodePaths.GrammarVersion` is still `2`. **Falsify:** bump it — this must redden, because a
    bump would assert an addressing change that did not happen.
19. Rolling `code_index_version` back to `2.4` and indexing with **`full: false`** re-reads both
    C# and TS/JS files and produces the new members. **Falsify:** run it with `full: true` — per
    §7.1 that bypasses the gate and proves nothing, so the test must be written the first way.
20. The `CallsOf` doc comment (`Program.cs:203–211`) no longer claims emission is the public
    surface, and `TreeSitter.cs`'s `#name` explanation survives the `if`'s deletion (§3.2).
    Reviewer-checked.
21. D48's policy paragraph, Phase 3 §5.3.2's discharge note, and `engram-progress.md:478–485` are
    all edited (§7). Reviewer-checked. A code change that leaves three documents asserting the old
    policy is not done.

**A skipped tier-1 run is not a pass.** Tier-1 tests require `ENGRAM_TEST_TREE_SITTER_DIR` and
skip silently without it, while tier-2 sidecar tests are unconditional because the test project's
own ProjectReference builds the binary. Items 12–17 therefore evaporate into the skip column on any
machine without that variable set, with the summary still reading `Passed!` — the exact failure
this repo has already recorded for tier 3. **The Implementor must report the skip count for items
12–17 explicitly**, not just the pass count.

E1–E6 are reported alongside these, not folded into them.

---

## 10. Decisions, with confidence

| # | Decision | Confidence | Note |
|---|---|---|---|
| 1 | Widen emission to all visibilities, both tiers | High | User's explicit instruction, twice; D48's counter-position is a design preference, his to override |
| 2 | Keep D48's **kind** exclusions unchanged | High | Their D44 rationale is measured and untouched by this request |
| 3 | `AnalyzerVersion` 4→5, `GrammarVersion` stays 2, one bump for both tiers | High | Addressing genuinely unchanged; `code_index_version` is tier-agnostic |
| 4 | Coarse attribution stays; item 25 retargets to an indexer | High | Population shrinks but is non-empty |
| 5 | Dedupe partial-method duplicate addresses in the sidecar | Moderate | New defect found at spec time, unmeasured; the rule is stated, its frequency is not |
| 6 | Close **both** tier-1 gaps — `private ` and `#name` | Moderate-high | Closing only Gap A widens the fake-private and leaves the real one out; but Gap B depends on E6 |
| 7 | Keep the `#` in `#name` symbol names | Moderate | Argued from the language and from the `#count`/`count` collision; not measured |
| 8 | Revise this spec in place rather than adding a companion | High | §7's doc edits and E1–E6 are shared; two docs would state the emission policy twice |
| 9 | No configuration knob, either tier | High | One behaviour, measured; a knob is the wrong answer to §2.3's risk |
| 10 | E3 attributes by commit, not by config | High | Satisfies the Orchestrator's constraint without a runtime switch |
| 11 | E3 can veto the change | High | The only unmeasured risk with a real precedent behind it |

## 11. Open, and what I am not confident about

- **§2.3's D44 risk is the whole exposure of this change** and I cannot measure it. Everything else
  here is mechanical. If E3 comes back ambiguous rather than clean, that is an Ultra-Advisor
  question, not an Implementor one: *does a large population of private-member code facts degrade
  recall for non-code queries, and if so is salience or lexical-index exclusion the right lever?*
- **E6 is a genuine unknown.** I asserted `private_property_identifier` as the node name from the
  grammar's naming convention and the existing comment's wording. If it is wrong, Gap B needs
  redesign, not a rename — and I would want to see the failing compile before specifying the
  alternative.
- **§8.1's corpus problem may have no good answer.** If nothing available carries enough TS/JS,
  the tier-1 half ships on acceptance tests without a recall measurement behind it. That is
  acceptable *if labelled*, and unacceptable if a small delta gets reported as reassurance.
- **Decisions 5 and 7 are unmeasured.** Both were found by reading rather than observing. The
  handling is cheap and safe either way, but neither has a number behind it.
