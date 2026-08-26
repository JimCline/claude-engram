# TypeScript `abstract_class_declaration` member-pattern parity

Rev 1. Scope: `src/Engram.Core/LanguageRegistry.cs`, `TypeScriptDeclarations` only.
Follow-up to `docs/code-graph-all-members-spec.md` rev 3, items 22–24 (E7). That spec is
**not** amended — it stays at rev 3 and its Reviewer verdict stands.

## 1. Goal

Bring `abstract_class_declaration`'s member patterns to the same coverage
`class_declaration` has, and make the one absence that is *deliberate* legible as
deliberate.

## 2. The current state, read from the file

`class_declaration` (lines 150–155) carries a complete 3×2 matrix — each of
`method_definition`, `method_signature`, `public_field_definition` paired with both
`property_identifier` and `private_property_identifier`.

`abstract_class_declaration` (lines 156–161) carries six patterns, but not the same six:

| node type | `property_identifier` | `private_property_identifier` |
|---|---|---|
| `method_definition` | 156 | 157 |
| `method_signature` | 158 | **absent — defect** |
| `abstract_method_signature` | 159 | **absent — correct by design** |
| `public_field_definition` | 160 | 161 |

So the block is short **two** pairings, not one, and they have opposite causes. That is
the whole reason this document exists: at the call site the two absences are
indistinguishable — six lines that look like an arbitrary list.

## 3. What `private_property_identifier` actually matches — read this before judging severity

It matches `#name` **only**. TypeScript's `private` and `protected` keywords parse as an
`accessibility_modifier` on a member whose name is an ordinary `property_identifier`, so
`private foo(): void;` is already captured by the `property_identifier` patterns and has
never been affected by any of this.

The E7 defect, and this one, are therefore narrower than "private members are missing":
they are specifically **`#name` overload signatures**. `#foo(a: string): void;
#foo(a: number): void; #foo(a: any) { … }` is legal TypeScript, the implementation is
emitted, and the two signatures match no pattern — so the overloads collapse to one
ambiguous symbol per name, which is exactly the D48 addressing bug E7 fixed for ordinary
classes.

An abstract class may hold concrete `#name` methods with overload signatures. Nothing
about `abstract` changes that. **The gap is real, not theoretical.**

## 4. Ruling

**Extend now, as its own item — do not accept it as a gap.**

Three reasons, in order of weight:

1. **The fix is one line and it is the same line already written and reviewed.** Item 23's
   pattern, with `class_declaration` swapped for `abstract_class_declaration`. There is no
   design content left to spend; carrying it as a tracked follow-up costs more in
   bookkeeping than in execution.
2. **An accidental asymmetry adjacent to a deliberate one makes the file actively
   misleading.** Whoever reads lines 156–161 next has to re-derive which absences are
   choices. That is the same failure mode as an absent-by-design tree-sitter pairing
   looking identical to a forgotten one — already recorded as a finding during E7, and
   here it is again in the same list, one line apart.
3. Jim's framing for the whole effort — *"private, public, protected, all members should
   be indexed"* — draws no line at `abstract`. A user whose codebase is abstract-class-heavy
   gets a silently worse graph for a reason nobody could state.

**Not** an argument I accept: "abstract classes rarely carry overloaded private helpers."
Rarity is the reason the defect would go unnoticed, not a reason to keep it — and it is an
unmeasured claim about other people's code.

## 5. Changes

### 5.1 Add the missing pattern

Insert immediately after line 158, matching line 153's shape exactly:

```
(abstract_class_declaration name: (type_identifier) @scope body: (class_body (method_signature name: (private_property_identifier) @name parameters: (formal_parameters) @params)))
```

Ordering within the query is not semantically load-bearing; keep it beside its
`property_identifier` sibling so the block reads as a matrix.

### 5.2 Comment the absence that stays

`abstract_method_signature` gets **no** `private_property_identifier` pairing, and the
line above it must say why:

```
// no abstract_method_signature + private_property_identifier pairing: an abstract member
// must be reachable from a subclass, so TypeScript rejects both `abstract #foo()` and
// `private abstract foo()`. The construct does not exist, rather than being skipped.
```

Wording may vary; the two properties that must survive are (a) it states the construct is
invalid TypeScript, not merely uninteresting, and (b) it sits at the absence, not in a
header comment far from it.

### 5.3 What must not change

- No pattern is removed, reordered across scope kinds, or rewritten.
- `class_declaration`'s six patterns are untouched.
- The JavaScript block (lines 169–178) is out of scope — JS has no
  `abstract_class_declaration`, no `method_signature`, and no overload signatures at all.
  Its four member patterns are correct as they stand.
- No change to `GrammarVersion` policy is specified here. If adding a pattern requires a
  grammar-version bump under the existing rules, that follows those rules; this spec does
  not create an exception either way. **Implementor: if the rule is ambiguous, stop and
  report rather than choosing.**

## 6. Verification

### 6.1 Acceptance items

1. A tier-appropriate test indexes a TypeScript fixture containing an **abstract** class
   with a concrete `#name` method carrying **two** overload signatures plus its
   implementation, and asserts **three** distinct symbols are emitted for that name,
   parameter-disambiguated per D48's collision-only suffix rule.
2. Falsify item 1 by deleting only the pattern added in §5.1 and re-running: the assertion
   must **fail**. Restore. A guard that cannot fail is worthless — and per the standing
   falsification discipline, confirm the deletion actually landed (`git diff --quiet`
   check, or equivalent) before trusting a red or a green.
3. The same fixture's ordinary (`property_identifier`) abstract-class members still
   resolve, proving §5.1 added coverage rather than shadowing anything.
4. Report the **skip count** alongside the pass count for any tier-3 arm. A tier-3 suite
   with no binary evaporates into the skip column while the summary reads `Passed!`.

### 6.2 Coverage note — the whole abstract block is currently unguarded

`grep -rn 'abstract_class_declaration' src/ tests/ --include=*.cs` returns matches in
`LanguageRegistry.cs` **only**. No test names it. All six existing patterns are therefore
unverified, not merely the new one.

That is out of scope for this item and must not silently expand it, but item 1's fixture
is the natural place for a second, cheap assertion if the Implementor is already there:
that an abstract class's plain method, abstract method signature, and public field each
produce a symbol. **Optional. Do not block on it, and do not let it grow into a
parity-audit of the whole registry.**

## 7. NEEDS-EVIDENCE — one item, cheap, and it only affects §5.2's wording

**N1.** Confirm that TypeScript rejects `abstract #foo(): void;` and
`private abstract foo(): void;` inside an abstract class. One `tsc` invocation on a
three-line file, or the two error codes from the handbook.

- **If rejected (expected):** §5.2's comment is correct as written. Proceed.
- **If either is accepted:** §5.2's comment is false and there is a **second real gap** —
  add the corresponding `abstract_method_signature` + `private_property_identifier`
  pattern and drop the comment. Report back before doing so; that changes what this spec
  concluded.

This is a language-semantics claim I could not execute against, and it is the only load
this ruling puts on anything unverified. §5.1 does not depend on it.

## 8. Confidence

- **HIGH** that the `method_signature` + `private_property_identifier` gap is real and
  identical in kind to E7's — read directly off lines 150–161, not inferred.
- **HIGH** that `private_property_identifier` is `#name`-only and that TS-keyword
  `private` members were never affected. This narrows the severity and should be stated
  plainly rather than letting "private members are missing" stand.
- **MODERATE** on §5.2's TypeScript-legality claim; that is what N1 exists for.
- **Not decided here:** whether a grammar-version bump is owed. §5.3 routes it back
  rather than guessing.
