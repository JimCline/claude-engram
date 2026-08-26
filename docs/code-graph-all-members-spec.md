# Indexing all members in the code graph

**Status:** design, **revision 3**. Written by the Architect.

**Revision history.**
- **Rev 1** — scoped to C# / tier 2, deferred TypeScript and JavaScript, flagged language scope as
  Jim's call.
- **Rev 2** — Jim ruled *widen both together*. Tier 1 folded in with the same rigor; two rev-1
  claims about tier 1 corrected (§6.1).
- **Rev 3** — amendment during implementation. The Implementor reported a measured asymmetry in
  tier-1 private-member addressing and inferred an overload-collision defect from it. **The
  inferred defect is not real and §6.6 proves why** — but the instinct behind it was sound and
  located a genuine under-emission gap one pattern away (§6.7), plus a test hole (§6.6.2). Adds
  E7 and acceptance items 22–24. Nothing in rev 2 is retracted.

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
   `LanguageRegistry.cs` capture `(property_identifier)`. A `#name` member is a
   `private_property_identifier`, a different node type, so it never produces a match at all.
   Deleting the `if` does nothing for these.

### 1.3 The asymmetry that makes tier 1's gap much smaller than tier 2's

**TypeScript and JavaScript class members default to public.** A member written with no modifier —
`foo() {}`, `x = 1` — has no `private ` prefix, so it is **already emitted today**. In C#, the
same no-modifier member is implicitly *private* and is excluded.

| | tier 2 (C#) | tier 1 (TS/JS) |
|---|---|---|
| no modifier | **excluded** (implicitly private) | already emitted (implicitly public) |
| explicit `private` | excluded | excluded by `:195` |
| runtime-private (`#name`) | n/a | excluded **structurally**, by the query |
| `internal` / `protected` | already emitted | already emitted |

**Consequence.** Tier 2's change admits a large population. Tier 1's `if` deletion admits only
members someone explicitly typed `private` in front of — a genuinely smaller set. This asymmetry
is the main reason E3 must attribute per tier (§8.3) rather than measuring one combined number.

**Confirmed during implementation:** the `private` keyword does not change the node type. `private
reset(): void {}` is a `method_definition` with a `property_identifier` name, exactly like a public
one — which is why Gap A really is only the `if`, and why the same pattern serves both.

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
**revised, not silently overridden** — see §7.2. The Implementor does not get to skip that edit,
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
call attribution in §3 is a **consequence** of this change, not its motive. The constraint S3
imposed is satisfied by the request being made at the right level, not by being argued away.

**So S3 does not block this and needs no re-litigation.** It should be annotated as discharged
(§7.3), with the reason, so nobody reads the phase-3 spec later and thinks the rule was ignored.

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
If E3 shows displacement, the fix is ranking or salience, **not** a config switch — see §6.5.

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
  type, because those kinds remain unemitted (§5.1).
- **Calls with no emitted ancestor at all** still attribute to the file (Phase 3 §5.2.1). Top-level
  statements and file-scoped code keep that path.

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

**`Program.cs:203–211`** claims *"Emission is the public surface (`EmitMember` skips non-public
members)"* and that a call inside a private method attributes to the enclosing type. Both become
false. Rewrite it to describe the new emission set and the *remaining* coarse cases. This is a
behaviour contract on a cross-process wire format, not decoration — it is the only place the
sidecar states what `enclosing_id` means.

**`TreeSitter.cs:191–194`** is the comment attached to the `if` being deleted. Its first sentence
states the overridden policy; its last two sentences describe `#name`'s structural exclusion, which
**remains true as a description of the old queries** and must be replaced by an accurate account of
the new ones (§6.3). Do not delete it wholesale with the `if`.

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
Closing this gap means **adding capture patterns**, not deleting a check.

**The ordering is the point, and it is counterintuitive.** `#name` is the *only* true, runtime-
enforced privacy in the language. `private` is a comment the compiler checks. So closing Gap A
alone widens the **fake** private and leaves the **real** one excluded — a half-measure that
would read as done and satisfy nobody who asked for "all members."

**Ruled: close both.**

### 6.3 What closing Gap B requires

Add a `private_property_identifier` variant beside each `property_identifier` member pattern in
`TypeScriptDeclarations` and `JavaScriptDeclarations`. Constraints:

- **Tree-sitter queries are compiled at runtime by `ts_query_new`** and a malformed pattern fails
  with a structural error rather than matching nothing. A pattern that does not compile takes the
  whole language's extraction down, not just the new members. Verify against the real pinned
  grammars, not by inspection (E6).
- **`#name` includes the `#`.** **Ruled: keep it.** It is part of the member's name in the
  language — `this.#count` is how it is written and how someone will search for it — and stripping
  it would collide a `#count` field with a public `count` field in the same class, which is a legal
  and common pairing.
- **Every method-shaped variant must carry `parameters: (formal_parameters) @params`**, exactly as
  its public counterpart does. §6.6 explains why this is load-bearing rather than cosmetic.
- **Do not touch the `_`-prefix convention.** `Matches()` skips captures whose *capture name*
  starts with `_` (`TreeSitter.cs:446`); that is about predicate-only captures and has nothing to
  do with member visibility. It is easy to misread as a second visibility filter. It is not one.

### 6.4 The existing tier-1 filter is approximate, which affects how E3 reads

`line.StartsWith("private ", StringComparison.Ordinal)` is a **line-prefix** test. It therefore
misses `private` members whose declaration line does not begin with the keyword — a decorated
member (`@deprecated` on the line above, or `@inject() private readonly svc: Svc` inline) is the
common case.

So **some `private` TS members are already indexed today**. The "before" state is not a clean
public-surface store. Two consequences:

1. This argues *for* deleting the check rather than against: an approximate filter enforcing a
   policy we are abandoning is strictly worse than no filter.
2. E3's tier-1 arm will show a **smaller** delta than the true `private` population implies. Do not
   read a small tier-1 delta as evidence the change did nothing.

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

### 6.6 The reported private-overload collision is NOT a defect — ruled, with the proof

**Rev 3 amendment.** During implementation the following was measured and reported: the `Scanner`
fixture yields `Scanner/#clear`, `Scanner/legacy`, `Scanner/reset` — **no** parameter-list
suffix — while the public `probe` keeps `Scanner/probe(deep: boolean)`. The inference drawn was
that *"two private overloads with the same name but different parameter lists would collide on one
fragment address in a way two public overloads don't."*

**That inference is false, and the measurement is correct.** Both halves matter.

**Why the measurement is right.** D48's parameter suffix is **collision-only**: *"when several
symbols in one file share that base, each appends its parameter list."* In the fixture, `probe`
appears three times — `probe(): void;`, `probe(deep: boolean): void;`, `probe(deep?: boolean) {}` —
so it collides and every one of them takes a suffix. `#clear`, `reset`, and `legacy` each appear
exactly once, so they take none. **A private method with no suffix is D48 working, not D48
failing.** Fixing the expectation to the measured value was the right call.

**Why the inferred defect is not real.** The suffix is composed from `DeepSymbol.Params`
(`TreeSitter.cs:197–199`), which is populated from the `@params` capture. Every private and
`#name` **method** pattern carries that capture, identically to its public counterpart:

- `LanguageRegistry.cs:143` — `method_definition` + `private_property_identifier` + `@params`
- `:148` — `abstract_class_declaration` variant, same
- `:167` — the JavaScript equivalent, same
- `:142` — plain `method_definition` + `property_identifier` + `@params`, which is what a
  `private`-**keyword** method matches, because the keyword does not change the node type

So `Params` is non-null for private and `#name` methods, and two private overloads with different
parameter lists **do** collide-and-disambiguate exactly as two public ones do. **Ruled:
accept-as-is on the code. No fix, no knob, no new spec.**

#### 6.6.1 Why this was worth ruling on rather than waving off

The two worlds are indistinguishable from the reported symptom alone. If `@params` had been absent
from the new variants, every observation in the report would have been **identical** — a lone
`#clear` with no suffix, a colliding `probe` with one — and the inferred defect would have been
real. The report could not settle it; only the query text could. Flagging rather than fixing was
correct.

#### 6.6.2 The test hole this exposes — fix now

The fixture overloads **only `probe`, a public method**. So the property just ruled correct —
*private and `#name` overloads disambiguate by parameter list* — is currently **unguarded**, and
was established by reading rather than by running.

**Add to the `Scanner` fixture** a `private` overload set and a `#name` overload set with differing
parameter lists, and assert distinct addresses for each. This converts an argument into a guard, and
it is the cheap half of this amendment. Acceptance items 22 and 23.

### 6.7 The gap the instinct actually found — an overload **signature** on a `#name` method

**Rev 3 amendment, and this is the substantive one.** The member patterns pair
`private_property_identifier` with `method_definition` (`:143`, `:148`, `:167`) and with the field
forms (`:146`, `:152`, `:169`). They do **not** pair it with `method_signature`:

- `:144` — `method_signature` + `property_identifier` + `@params` ✓
- **absent** — `method_signature` + `private_property_identifier`

A `method_signature` is the bodiless *overload declaration* form. So for

```ts
class Scanner {
    #clear(): void;
    #clear(n: number): void;
    #clear(n?: number): void {}
}
```

the two signatures match no pattern and are silently dropped; only the implementation is emitted.
**That is under-emission of exactly the kind the report feared** — private overloads losing
information public ones keep — reached by a different route than the one proposed.

**Ruled: fix now if the case is real, and E7 decides that.** Two things must be confirmed together,
because either alone is insufficient:

1. **Is `#clear(): void;` legal TypeScript?** Overload signatures on `#`-private methods are
   plausible but I have **not** verified they are permitted.
2. **Does the grammar produce `method_signature` with a `private_property_identifier` child** for
   it?

- **Both yes** → add the pattern, mirroring `:144` with the private node type. Acceptance item 24.
- **Either no** → add **one comment line** at that point in the query list saying the pairing is
  absent because the language does not admit it. This matters: in a flat list of patterns an
  omission and a deliberate exclusion look identical, and the next person to read it will either
  "fix" a non-case or trust a gap.

#### 6.7.1 Two absences that are correct and should be recorded as such

Same reasoning, already resolvable without evidence:

- **`interface_declaration` + `private_property_identifier`** — interfaces cannot have `#` members.
  Correctly absent.
- **`abstract_method_signature` + `private_property_identifier`** — `abstract` and `#`-private are
  mutually exclusive. Correctly absent.

Record both in the same comment. The pattern list is a place where silence is ambiguous, and this
amendment exists because that ambiguity cost a round trip.

---

## 7. Versioning, and documents that must be edited

### 7.1 One version bump covers both tiers

`src/Engram.Core/CodeAnalyzer.cs:31`:

```csharp
public const int AnalyzerVersion = 4;   // → 5
```

**`CodePaths.GrammarVersion` stays at 2.** Grammar version governs *how a code subject is
addressed*, and a private member receives exactly the address the same member would receive if it
were public — in both tiers. Nothing about fragment composition changes. What changes is *which
members the extractors observe*, which is precisely what `AnalyzerVersion` exists to track — the
same split Phase 3 applied when call edges reused declaration addressing unchanged.

`CodeIndexer.CurrentVersion` is `$"{CodePaths.GrammarVersion}.{CodeAnalyzer.AnalyzerVersion}"`
(`CodeIndexer.cs:79`), so the stored `code_index_version` moves `2.4` → `2.5`, setting
`versionForcedFull` and re-reading every indexed file **regardless of tier**. One bump serves both
halves; do not add a second version constant.

**Note for rev 3's additions.** §6.7's pattern, if E7 says add it, changes *which members are
observed* — the same class of change as the rest of this work — so it rides the same `4 → 5` bump
provided it lands before that bump ships. **If it lands after, it needs its own bump**, or stores
indexed in between silently keep the gap.

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

I cannot run anything. Each item names what to run and **what each outcome decides**. Every command
must set `ENGRAM_HOME` or pass `--home`, per CLAUDE.md; do not run these against the real
`~/.engram`.

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
count and entity count, per tier. *Decides:* whether "measured, not estimated" is satisfied for the
size claim. Baseline: the live instance holds ~6,400 code facts of ~15,000 total, in a 217 MB store.

**E2 — ambiguity distribution, a re-run of E8.** Distinct symbol leaf names, count with exactly one
declaration, count with more, top ten by declaration count. *Decides:* how much wider
`callers`/`defined_at` supersets get. Baseline in §4.1. Roughly doubling the ambiguous fraction is
expected and acceptable; worst offenders reaching three figures means `callers` on a common name
stops being a usable answer and §6.5's salience work becomes required rather than contingent.

**E3 — recall quality. The one that can veto this change.** See §8.3.

**E4 — index cost.** Wall-clock for a full index, and store size, before and after, per tier.
*Decides:* nothing alone, but a large regression is worth knowing before a user discovers it.

**E5 — does Phase 3 acceptance item 25's test exist?** Locate it or establish it was never written.
*Decides:* §3.1's branch. Must be answered **before** `EmitMember` is edited, because after the
edit a missing guard and a silently-inverted one look identical. **Blocking.**

**E6 — do the amended queries compile?** Compile the amended TypeScript and JavaScript declaration
queries against the **real pinned grammars** and confirm they match a `#name` member. *Decides:*
whether Gap B is closable as specified. A query that fails to compile takes the whole language's
extraction down, so this cannot be assumed from a node name looking right. **Blocking for the
tier-1 half only.**

**E7 — is `method_signature` + `private_property_identifier` a real case? (rev 3)** Two parts,
both required: (a) does TypeScript permit an overload *signature* on a `#`-private method —
`#clear(): void;` beside `#clear(n?: number): void {}`; and (b) does the pinned grammar produce a
`method_signature` node whose name child is a `private_property_identifier`? Answer (a) from the TS
compiler's own behaviour, not from documentation alone. *Decides:* §6.7 — both yes, add the pattern
and acceptance item 24; either no, add the comment recording why the pairing is absent. **Blocking
for §6.7 only; the rest of the tier-1 work proceeds either way.**

### 8.3 E3's design — how a regression stays attributable

Shipping both tiers together must not make a recall regression unattributable. It does not, provided
the measurement is structured by **commit** rather than by configuration:

| Arm | Tree | Attributes |
|---|---|---|
| 0 | before both changes | baseline |
| 1 | after the tier-2 commit only | arm 1 − arm 0 = **C#** |
| 2 | after the tier-1 commit | arm 2 − arm 1 = **TS/JS** |

Two commits, one delivery, three measurement points. No configuration knob (§6.5 forbids one), git
rather than a runtime switch, and it matches this repo's discipline of falsifying against a
committed tree. Order tier 2 first — larger change, and the reported defect is behind it.

**What E3 measures.** Before and after each arm, run a fixed set of **non-code** recall queries
drawn from real recorded history — personal facts, past decisions, session notes — and compare what
comes back, `coverage` included. *Decides:* whether §2.3's D44 risk is real for private members.

- **Use real recorded queries.** A hand-written set will flatter the change, because whoever writes
  it knows what was added.
- **If ordinary facts get displaced, stop and escalate to §6.5's options.** Do not ship and do not
  reach for a switch.

---

## 9. Acceptance

**Tier 2 (C#)**

1. `EmitMember`'s visibility guard is gone; no replacement accessibility condition anywhere in
   `Program.cs`. **Falsify:** restore the guard — items 2, 3, and 5 must redden.
2. `defined_at "WriteEntry"` resolves to `MemoryReport.WriteEntry` at Exact tier after a re-index.
   The reported defect; the headline test.
3. A method with **no accessibility modifier** on a class is emitted. **Falsify:** re-add only the
   no-modifier half of the old condition — this must redden while item 2 stays green. Load-bearing:
   a guard that only tests explicit `private` passes with half the defect restored.
4. Interface members are still emitted, and `internal`/`protected`/`private protected` members are
   still emitted. Guards against a "simplification" that swaps one filter for another.
5. A call written inside a private method attributes to **that method**, not the enclosing type.
6. A call inside a **local function** inside a private method attributes to the private method.
7. A call inside an **indexer body** still attributes to the enclosing type, with the label intact.
   The retargeted item-25 guard (§3.1) and the proof coarse attribution is still live.
8. Indexers, operators, enum members, and local functions are still **not** emitted as symbols.
   **Falsify:** emit one — this must redden.
9. A file containing a partial-method declaration and its implementation yields **one** symbol for
   that name, the one with the body (§5.2). **Falsify:** emit both.
10. A `static` constructor is emitted as a `constructor` symbol.
11. A private constructor is emitted, and its address is distinct from its type's (§4.3).

**Tier 1 (TypeScript / JavaScript)**

12. A TS member declared `private foo()` is emitted. **Falsify:** restore the `if` at
    `TreeSitter.cs:195` — this must redden.
13. A **decorated** private member is emitted both before and after the change. Guards §6.4: it
    passes today and must keep passing, so nobody tightens the approximate filter on the way past.
14. A `#name` private field and a `#name` private method are both emitted, with the `#` retained
    (§6.3). **Falsify:** revert the query-pattern addition — this must redden while item 12 stays
    green. Load-bearing: it separates Gap B from Gap A, and closing only A would otherwise look
    complete.
15. A class containing both `#count` and `count` yields **two distinct symbols** with distinct
    addresses (§6.3).
16. Both amended declaration queries **compile** against the real pinned grammars. E6 promoted to a
    guard, because a non-compiling query fails the whole language rather than one pattern.
17. TS/JS members with no modifier are still emitted (they always were — §1.3). Guards against a
    tier-1 edit that accidentally narrows while widening.

**Rev 3 additions**

22. Two `private`-keyword overloads of one name with **different** parameter lists yield **two**
    symbols at **distinct** addresses, each carrying its parameter-list suffix (§6.6.2).
    **Falsify:** drop `@params` from `LanguageRegistry.cs:142` — this must redden. Load-bearing:
    it is the guard that the §6.6 ruling rests on, and without it that ruling is an argument rather
    than a fact.
23. The same for two `#name` overloads. **Falsify:** drop `@params` from `:143` — must redden while
    22 stays green. The pair must be separable, or one pattern losing its capture hides behind the
    other.
24. **Conditional on E7(a) and E7(b) both being yes.** An overload *signature* on a `#name` method
    is emitted (§6.7). **Falsify:** remove the added `method_signature` +
    `private_property_identifier` pattern. **If E7 says no**, this item is replaced by a
    reviewer-checked assertion that the comment recording the absent-by-design pairings is present
    (§6.7.1) — an omission and a deliberate exclusion must not look identical in that list.

**Shared**

18. `CodePaths.GrammarVersion` is still `2`. **Falsify:** bump it — must redden, because a bump
    would assert an addressing change that did not happen.
19. Rolling `code_index_version` back to `2.4` and indexing with **`full: false`** re-reads both C#
    and TS/JS files and produces the new members. **Falsify:** run it with `full: true` — per §7.1
    that bypasses the gate and proves nothing, so the test must be written the first way.
20. The `CallsOf` doc comment no longer claims emission is the public surface, and
    `TreeSitter.cs`'s `#name` account is replaced by an accurate one rather than deleted (§3.2).
    Reviewer-checked.
21. D48's policy paragraph, Phase 3 §5.3.2's discharge note, and `engram-progress.md:478–485` are
    all edited (§7). Reviewer-checked. A code change leaving three documents asserting the old
    policy is not done.

**A skipped tier-1 run is not a pass.** Tier-1 tests require `ENGRAM_TEST_TREE_SITTER_DIR` and skip
silently without it, while tier-2 sidecar tests are unconditional because the test project's own
ProjectReference builds the binary. Items 12–17 and 22–24 therefore evaporate into the skip column
on any machine without that variable set, **with the summary still reading `Passed!`** — the exact
failure this repo has already recorded for tier 3. **The Implementor must report the skip count for
those items explicitly**, not just the pass count.

E1–E7 are reported alongside these, not folded into them.

---

## 10. Decisions, with confidence

| # | Decision | Confidence | Note |
|---|---|---|---|
| 1 | Widen emission to all visibilities, both tiers | High | User's explicit instruction, twice |
| 2 | Keep D48's **kind** exclusions unchanged | High | Their D44 rationale is measured and untouched |
| 3 | `AnalyzerVersion` 4→5, `GrammarVersion` stays 2, one bump for both tiers | High | Addressing genuinely unchanged; `code_index_version` is tier-agnostic |
| 4 | Coarse attribution stays; item 25 retargets to an indexer | High | Population shrinks but is non-empty |
| 5 | Dedupe partial-method duplicate addresses in the sidecar | Moderate | Found by reading; rule stated, frequency unmeasured |
| 6 | Close **both** tier-1 gaps — `private ` and `#name` | Moderate-high | Gap B depends on E6 |
| 7 | Keep the `#` in `#name` symbol names | Moderate | Argued from the language and the `#count`/`count` collision |
| 8 | Revise this spec in place rather than adding a companion | High | §7's doc edits and E1–E7 are shared |
| 9 | No configuration knob, either tier | High | One behaviour, measured |
| 10 | E3 attributes by commit, not by config | High | Meets the separability constraint without a runtime switch |
| 11 | E3 can veto the change | High | The only unmeasured risk with a real precedent behind it |
| 12 | **Private-overload collision: accept as-is, no fix (rev 3)** | High | Proven from the query text: `@params` is captured identically on private, `#name`, and public method patterns. The observed suffix asymmetry is D48's collision-only rule working |
| 13 | **Add private/`#name` overload guards to the fixture (rev 3)** | High | Decision 12 currently rests on reading, not running; items 22–23 convert it |
| 14 | **`method_signature` + `private_property_identifier`: fix if E7 confirms (rev 3)** | Moderate | Real under-emission if the case exists; I could not verify that it does |

## 11. Open, and what I am not confident about

- **§2.3's D44 risk is the whole exposure of this change** and I cannot measure it. Everything else
  is mechanical. If E3 comes back ambiguous rather than clean, that is an Ultra-Advisor question,
  not an Implementor one: *does a large population of private-member code facts degrade recall for
  non-code queries, and if so is salience or lexical-index exclusion the right lever?*
- **E7 is a genuine unknown and rev 3's weakest point.** I do not know whether TypeScript permits
  an overload signature on a `#`-private method. If it does not, §6.7 collapses to a comment; if it
  does, it is a real under-emission bug. I could not settle it by reading.
- **§8.1's corpus problem may have no good answer.** If nothing available carries enough TS/JS, the
  tier-1 half ships on acceptance tests without a recall measurement behind it. Acceptable *if
  labelled*, unacceptable if a small delta gets reported as reassurance.
- **Decisions 5 and 7 are unmeasured.** Both found by reading rather than observing.
- **Decision 12 was close to going the other way.** The reported symptom is identical under the
  defect and under correct behaviour; only the query text separates them (§6.6.1). If the patterns
  are ever edited to drop a `@params` capture, that ruling silently becomes wrong — which is
  precisely what items 22 and 23 exist to catch.
