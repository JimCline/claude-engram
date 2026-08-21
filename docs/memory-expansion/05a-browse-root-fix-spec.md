# 05a — Browse at root: the prefix boundary off-by-one

Status: **ready for implementation**, with one referred decision (see Open questions) and two
NEEDS-EVIDENCE items that do not block the fix.

Amends `docs/memory-expansion/05-browse-tui-spec.md`, which specifies that `engram browse`
"Starts at the root, lists child path segments … lets the user descend". Root is that verb's
entry point, so this defect blocks spec 05 entirely. It is a bug fix to shipped code in
`src/Engram.Core/MemoryBrowser.cs`, which `engram_browse` (MCP) also depends on, so the
zero-regression obligation in "What must not change" is the load-bearing half of this document.

---

## Goal

`MemoryBrowser.Browse(connection, "/", depth)` returns the real tree of root's direct children,
at the right depth, for any non-empty store — without altering the result of any non-root call.

## Non-goals

- Changing the external path representation. Callers keep addressing root as `"/"`.
- Fixing the eight sibling copies of the same boundary idiom (inventoried under D-4).
- Changing `engram_browse`'s MCP surface, parameters, or output shape.
- Any schema change. This is a read path; no `fact` row is written, so D8 is untouched.

---

## The defect

`MemoryBrowser.cs:53-54`, verbatim:

```sql
WHERE e.path = $path
   OR (substr(e.path, 1, $len) = $path AND substr(e.path, $len + 1, 1) IN ('/', '#'))
```

The second clause finds strict descendants: the prefix must match, and the character
immediately after it must be a genuine separator, so `/code/api-docs` never counts under
`/code/api`. That is correct for every prefix that ends in a *segment*.

Root is the one prefix that **is** the separator. `MemoryBrowser.cs:57-58` binds
`$path = "/"`, `$len = 1`, so `substr(e.path, 2, 1)` reads the first character of the child's
own first segment — `"p"` for `/people/jim` — which is never `/` or `#`. The equality clause
cannot rescue it either: no entity is addressed as `/`. So `counts` is empty, `Browse` returns
null at `:67-70`, and `BrowseCommand.Loop` prints `Nothing in memory under /.` and returns
before ever reaching its prompt.

**The same off-by-one is in `Fold`, and the brief did not name it.** `MemoryBrowser.cs:173-175`:

```csharp
if (entityPath.Length <= path.Length
    || !entityPath.StartsWith(path, StringComparison.Ordinal)
    || entityPath[path.Length] is not ('/' or '#'))
{
    continue;
}
```

With `path = "/"`, `entityPath[1]` is `'p'`, so `Fold` skips every descendant too. **A fix
confined to line 54 is not a fix**: it produces a non-null root node with zero children and
`FactsUnder == 0`. That is a quieter failure than the present one, not a repaired one. Every
subsequent line of `Fold` shares the assumption — `separator = entityPath[path.Length]`,
`rest = entityPath[(path.Length + 1)..]`, `childPath = path + separator + segment` — so at root
they would compose `"/" + 'p' + "eople"`.

`LastSegment` (`:212-216`) is the only site that already handles a trailing separator: it
returns `path` unchanged when the separator is the last character, so `LastSegment("/")` is
`"/"`. That is precedent for the shape of the fix, not an exception to it.

### Why it has been latent

`engram_browse` requires a `path` argument (`EngramMcpTools.cs:300` —
`MemoryBrowser.Browse(connection, path, depth ?? 1)`) and every existing caller passes a real
subpath. Verified: no test in the repository calls `Browse` with `"/"`. The six existing call
sites are `DirectiveBrowseTests.cs:21,40` (`DirectiveFacts.Root`, a directives subtree, not
`/`), `DirectiveBrowseTests.cs:59,63` (`/facts`), and `BrowseTuiTests.cs:40,69` (`/knowledge`).
So nothing pins the broken behaviour as intentional, and nothing has to be rewritten to allow
the fix.

---

## Design

Root's separator is its own last character. Nothing precedes a root child's first segment, so
the prefix the boundary test should measure from is the **empty string** — and then the leading
`/` of every child *is* the separator the existing test is looking for.

`MemoryBrowser.cs:37-41` already computes that empty string and throws it away:

```csharp
var normalized = path.TrimEnd('/');
if (normalized.Length == 0)
{
    normalized = "/";
}
```

The restore to `"/"` exists so the returned node has a displayable, addressable path. Keep it,
and derive the query prefix beside it.

### The change, in full

In `Browse`, after the existing normalization:

```csharp
// Root's separator is its own last character, so nothing precedes a root child's first
// segment: the prefix the boundary test measures from is empty, and each child's leading
// '/' is the separator that test looks for. Every other prefix ends in a segment, where
// this is the same string and the query is unchanged.
var prefix = normalized == "/" ? string.Empty : normalized;
```

Bind that instead of `normalized` — the SQL text itself is **not modified**:

```csharp
command.Parameters.AddWithValue("$path", prefix);
command.Parameters.AddWithValue("$len", prefix.Length);
```

And replace the return at `:72`:

```csharp
var node = Fold(prefix, prefix.Length == 0 ? "/" : LastSegment(prefix), counts, depth);

// Fold addresses root as the empty prefix; every caller addresses it as "/" —
// BrowseCommand.Loop compares its own path against "/", prints node.Path in the header,
// and passes node.Path to TopFacts.
return prefix.Length == 0 ? node with { Path = normalized } : node;
```

`BrowseNode` is a `record`, so `with` rewrites only the root node; children were built from real
`childPath` values and are untouched.

That is the entire fix: one new local, two parameter bindings, one return statement.

### Why nothing else needs to change

**The SQL.** `substr(e.path, 1, 0)` is `''` in SQLite, so `'' = $path` holds for every row; then
`substr(e.path, 0 + 1, 1)` is the first character, which is `/` for every rooted path. Root
therefore matches every entity — which is the correct set, since every entity is under root.
`AddWithValue` binds `string.Empty` as TEXT `''`, not NULL.

**`Fold`.** With `path = ""`: `entityPath.Length <= 0` is false; `StartsWith("")` is true;
`entityPath[0]` is `/`, a real separator. Then `separator = '/'`, `rest = entityPath[1..]`, and
`childPath = "" + '/' + segment` — a real path like `/people` that keys correctly into `counts`
and is what `BrowseCommand`'s `Descend` assigns to its loop path. `here =
counts.GetValueOrDefault("", 0)` is 0, which is correct: root addresses no entity.

**The equality clause.** `e.path = ''` matches nothing in practice, and if an entity with an
empty path ever existed it would be counted at root, which is where it belongs. The fix does
not depend on either being true.

**Depth.** Unchanged. `Fold(prefix, …, 1)` yields root's direct children; at 3 it reaches
grandchildren. `BrowseCommand.Loop` passes `depth: 1` and descends one level per keystroke.

---

## Decisions

**D-1 — Root becomes a legal value of the existing prefix rule, not an exception to it.**
The brief asked what the boundary check should *become* for root. The answer is that it needs
no case at all. *Rejected — a conditional at each site:* add `$len = 1 AND $path = '/'` to the
SQL and a `skip` local replacing `path.Length` in three places in `Fold` (plus `path[..skip]` in
`childPath`). That works, but it adds the special case at exactly the sites that already carry
the bug — and the idiom is copied eight times across the codebase, so an exception to it would
have to be copied too. *Rejected — normalize root to `""` at the API boundary:* `Browse` already
maps `""` to `"/"` at `:38-41`, `BrowseCommand.Loop` compares `path == "/"` in three places, and
the MCP tool's `path` parameter is documented to users. Changing the external representation is
a larger blast radius than the bug.

**D-2 — The root node's `Path` is restored to `"/"` before returning, and this is load-bearing
rather than cosmetic.** `BrowseCommand.Loop` passes `node.Path` to
`MemoryBrowser.TopFacts(connection, node.Path, FactsPerNode)` and prints it in both the
`— nothing here or beneath it.` line and the `— N facts here, M under it` header, while
comparing its own loop variable against the literal `"/"`. Letting `""` escape would print an
empty path and would push `""` into `FactStore.ReadSubtree`, whose range for an empty prefix
spans every rooted path (see D-6).

**D-3 — `Fold` and `LastSegment` are not modified.** The empty prefix satisfies `Fold`'s
existing preconditions, and `LastSegment` is bypassed at root by passing the name directly. Any
diff that touches either body has taken Design B by accident and should be re-read against D-1.

**D-4 — The eight sibling copies of the idiom are inventoried and deliberately not fixed.**
Every one carries the same root blind spot:

| Site | Form |
| --- | --- |
| `src/Engram.Core/FactStore.cs:220` | `AND (length(path) = $len OR substr(path, $len + 1, 1) IN ('/', '#'))` |
| `src/Engram.Core/CodeIndexer.cs:705` | same shape |
| `src/Engram.Core/CodeIndexer.cs:718` | `substr(path, $len + 1, 1) = '#'` (symbol children only) |
| `src/Engram.Core/StoreCompactor.cs:245` | `AND substr({column}, length($p) + 1, 1) IN ('/', '#')` |
| `src/Engram.Core/StoreCompactor.cs:264` | `AND substr($p, length(repo_path \|\| '/' \|\| path) + 1, 1) IN ('/', '#')` |
| `src/Engram.Core/FactJournal.cs:172` | `(subject.Length == prefix.Length \|\| subject[prefix.Length] is '/' or '#')` |
| `src/Engram.Core/MemoryBrowser.cs:173-175` | fixed here, via the prefix |
| `src/Engram.Cli/BrowseCommand.cs:273` | `path.LastIndexOfAny(['/', '#'])` — a second `LastSegment` |

No caller passes root to any of them: they serve subtree moves, compaction, journal replay and
code indexing, none of which address root. A change there would therefore be unfalsifiable —
no test can be written that fails before it and passes after — which this repository's tier
discipline forbids. Revisit when a *second* caller genuinely needs root, and extract one shared
predicate at that point rather than now.

Worth recording alongside it: **`FactStore.ReadSubtree` has no root bug**, because it is written
as a range scan rather than a `substr` boundary — `exact = pathPrefix.TrimEnd('/')`,
`low = exact + "/"`, `high` = `low` with its last byte incremented, so root's range is
`['/', '0')`, which covers every rooted path exactly. The formulation D2 chose for index
friendliness is also the one that handles root naturally. That is the direction any future
unification should take.

**D-5 — Browse must not emit a row wider than the terminal, and that is fixed in this change.**
See "What this fix exposes" below: repairing root turns a currently-hollow tier-3 test red, for
a genuine, separate defect. D52 already forbids emitting a row the redraw cannot count, so this
is enforcement of an existing rule rather than new scope. The *mechanism* is specified below;
the *wording* of the hint line is a product choice and is referred in Open questions.

**D-6 — The whole-store read that root newly reaches is measured before it is changed.**
Designed remedy is recorded so the measurement does not require another design round-trip. See
NEEDS-EVIDENCE 1.

---

## What must not change

For any `path` whose `TrimEnd('/')` is non-empty, `prefix == normalized`, so `$path`, `$len`,
and both arguments to `Fold` are **identical** to today's values, and the returned node is
returned unmodified. No non-root browse can therefore change. That is a checkable claim, not an
aspiration, and it has a checkable consequence:

> **The regression suite for this change already exists and must pass unmodified.**
> `DirectiveBrowseTests` (4 calls), `BrowseTuiTests` (2 calls), `BrowseCommandTests`,
> and `EngramMcpTools.cs:300`. If any of them has to be edited to make this fix pass, the fix
> overreached — stop and report the gap rather than adjusting the test.

Also unchanged: the `CommandText` string in `Browse`; the bodies of `Fold` and `LastSegment`;
every site in the D-4 table; `FactStore.ReadSubtree`; the `entity` schema.

---

## What this fix exposes

**`BrowsePtyTests.UnderANarrowPty_BrowseNeverEmitsARowThatWouldWrap` will go from green to
red, and that is correct.** The brief describes it as "passes but hollow", which is right about
today and understates what happens next.

Today: the test seeds one fact, runs `browse` under a 40-column pty, and root returns null — so
the only output is `Nothing in memory under /.` at 26 characters, every assertion passes, and
`Tui.Draw` is never reached. After the fix, root has children, so `BrowseCommand.Loop` reaches
its header and hint lines, which are written **directly to stdout and never clipped**:

```csharp
stdout.WriteLine(
    path == "/"
        ? "  select then Enter to open a folder · t timeline / h history on a fact · q quit"
        : "  select then Enter to open a folder · t timeline / h history on a fact · b back · q quit");
```

That root variant is roughly 80 characters against `narrowColumns = 40`, so the test's
`Assert.True(line.Length < narrowColumns, …)` fails on it. The failure is real: an unclipped
80-column row on a 40-column terminal is precisely the D52 defect — the row budget counts
*physical* rows, and a line the terminal wraps costs rows the redraw never moves back over.

Required: every line `engram browse` emits must fit the width, including the header and hint
lines emitted outside `Tui.Draw`. Prefer routing both through whatever clipping `Tui.Render`
already applies, so there is one implementation of the width rule rather than two. Note that
the assertion measures `string.Length` — **characters** — and both lines contain `·` (U+00B7,
one character, two UTF-8 bytes) and `—` (U+2014, one character, three bytes), so any byte-based
reasoning about their length will be wrong in the direction of looking safe.

The non-pty `BrowseCommandTests.Browse_QuitsCleanly_AfterListingAFact` goes green with no edit:
it is not a terminal, so `RunPlain` prints the `browse>` prompt its assertion looks for.

**A self-correction, made while reading `BrowseCommand.cs`, not something relayed from the
tasking session:** an earlier pass through this analysis assumed that test would pass on a
SQL-only half-fix. It does not. `BrowseCommand.Loop` already
carries a root sentinel — when `entries.Count == 0` it prints `— nothing here or beneath it.`
and, if `path == "/"`, returns 0 without printing the prompt. So the half-fix still fails it.
The tier-2 test below remains the primary guard because tier 3 skips silently without a
published binary, not because tier 3 cannot tell the difference.

---

## Tests by tier (D9)

**Tier 2 — the primary guard.** New file
`tests/Engram.Integration.Tests/MemoryBrowserRootTests.cs`, against a real SQLite file, seeded
with the same helper `BrowseTuiTests` and `DirectiveBrowseTests` already use. Seed live facts at
three subjects, chosen so a `#` boundary is reachable from root within `MaxDepth`:

- `/people/jim/preferences`
- `/people/ada`
- `/code/Auth.cs#ValidateToken`

Assertions on `Browse(connection, "/", depth: 1)`:

1. the result is not null;
2. `node.Path == "/"` and `node.Name == "/"`;
3. `node.FactsHere == 0`;
4. **child names, ordinal-sorted, are exactly `["code", "people"]`**;
5. **child paths, ordinal-sorted, are exactly `["/code", "/people"]`** — this is what `Descend`
   assigns, so a wrong `childPath` composition must fail here rather than at the next keystroke;
6. `node.FactsUnder` equals the store's total live-fact count, obtained by an independent count
   rather than by summing the node — two derivations that must agree, in the same spirit as the
   `DetailsChars` equivalence fixture.

On `Browse(connection, "/", depth: 3)`:

7. `/people` has a child named `jim` with path `/people/jim`;
8. `/code` has a child named `Auth.cs`, which has a child whose **name is `#ValidateToken`** and
   whose path is `/code/Auth.cs#ValidateToken` — pinning the `'#'` branch of `Fold` through a
   root traversal.

Normalization and negative cases:

9. `Browse(connection, "", 1)` and `Browse(connection, "//", 1)` return the same `Path` and the
   same child set as `Browse(connection, "/", 1)`;
10. against a store with no entities, `Browse(connection, "/", 1)` returns **null** — the honest
    "nothing in memory" case must survive;
11. `Browse(connection, "/people", 1)` returns children `jim` and `ada` — a non-root control in
    the same fixture.

Assertions 4 and 5 are the ones that matter. **Asserting only non-null is exactly the assertion
a `Fold`-unfixed build passes**, so a test that stops at 1–3 would certify the half-fix.

**Tier 3 — existing, unmodified.** `BrowseCommandTests.Browse_QuitsCleanly_AfterListingAFact`
must go green without being edited. `BrowsePtyTests.UnderANarrowPty_…` must go green *after* the
D-5 clipping fix, and must then be re-falsified (arm 4).

---

## Falsification

Four arms. Three of them exist because the fix has three separable halves and one arm cannot
distinguish them.

| Arm | Break | Expected failure |
| --- | --- | --- |
| 1 | bind `normalized` instead of `prefix` for `$path`/`$len`; leave the `Fold` prefix | tier-2 assertion 1 fails — result is **null** |
| 2 | pass `normalized` to `Fold`; leave the SQL binding as `prefix` | assertion 1 **passes**, assertions 4–5 fail — **empty children** |
| 3 | drop `with { Path = normalized }` | assertion 2 fails |
| 4 | after the D-5 fix, break the width clipping | `BrowsePtyTests.UnderANarrowPty_…` must fail |

**Arm 2 is the one that must not be skipped.** If it stays green, assertions 4–5 are missing or
too weak, and the guard would not catch the half-fix that this document exists to prevent.

**Arm 4 is a re-falsification of a test that was already green.** It passed before the fix and
will pass after, so "still passing" proves nothing about whether it now tests its property.
Breaking the clipping and confirming it reddens is the only evidence that it stopped being
hollow.

### Falsification hazard specific to this working tree

`git status` shows spec 05's work is **uncommitted**, and this changes how the arms may be run
(D60 — falsify against a committed tree, and assert the patch landed):

```
 M src/Engram.Core/FactStore.cs
?? src/Engram.Cli/BrowseCommand.cs
?? tests/Engram.EndToEnd.Tests/BrowseCommandTests.cs
?? tests/Engram.EndToEnd.Tests/BrowsePtyTests.cs
?? tests/Engram.Integration.Tests/BrowseTuiTests.cs
```

`src/Engram.Core/MemoryBrowser.cs` is itself clean, but:

- `git checkout -- <path>` restores to HEAD, so using it to revert an arm would **discard spec
  05's uncommitted work**, not just the arm;
- `git clean` would **delete** the untracked test files the arms are being run against —
  including the new tier-2 file.

So either commit spec 05's work before falsifying (recommended — it makes every arm a plain
`git checkout --` again), or edit and restore by hand. Either way, run `git diff` and
`git status --porcelain` before trusting an arm, to confirm the intended break is actually
present and that nothing else moved. A falsification that silently no-ops reports success while
proving nothing.

---

## Measurements / NEEDS-EVIDENCE

Neither item blocks the fix. Both need a number I cannot obtain.

**1 — The cost of a root browse.** `Browse(connection, "/", …)` now returns every `entity` row
through a LEFT JOIN and GROUP BY, because no prefix narrows it. Measure wall time and peak
resident size for `engram browse` entering root on stores of roughly 5,000 and 50,000 live
facts, from the **published binary**, with `ENGRAM_HOME` set (never against the real instance).

- *Decision rule:* at or under the nearest existing interactive bar in this repo — recall's
  p50-under-50 ms target — record the number in this section and ship as designed. Above it,
  add a depth bound to the query, which is correct for **all** prefixes rather than root alone,
  and measure again.
- *Do not pre-add the bound.* It would change non-root behaviour, which is the zero-regression
  zone, in exchange for an unmeasured saving.
- *Framing, so the number is read correctly:* this fix does not introduce over-fetch. Today's
  non-root query already returns the whole subtree regardless of `depth`, and `Fold` discards
  what it does not need. Root simply makes that subtree the entire store — an existing property
  becoming visible at its maximum, not a new defect.

**2 — `TopFacts` at root is newly reachable and reads the whole live table.** Today
`BrowseCommand.Loop` returns at the null check and never reaches
`MemoryBrowser.TopFacts(connection, node.Path, FactsPerNode)`. After the fix it runs on every
root visit, and `TopFacts` delegates to `FactStore.ReadSubtree(connection, "/")`, whose range
`['/', '0')` matches **every live fact in the store**; the C# filter
`.Where(f => f.SubjectPath == path.TrimEnd('/'))` then compares against `""` and discards all of
them. The result is correct — no fact is at root — but every fact is materialized as a
`StoredFact` to be thrown away, which is allocation as much as latency.

- *Measure* it as part of item 1; it is on the same path.
- *Designed remedy, if the number warrants it:* give `FactStore` an exact read —
  `SelectFactColumns` with `WHERE f.valid_to IS NULL AND e.path = $exact ORDER BY e.path, f.id`
  — and have `TopFacts` call it instead of reading a subtree and filtering to one path. That
  returns the identical set in the identical order for every path, is strictly cheaper
  everywhere, and needs no inference about whether root can be an entity.
- *Rejected alternative:* short-circuit `TopFacts` to return empty when the path normalizes to
  root. Cheaper, but it rests on "root is never an entity", which the schema does not guarantee,
  and it leaves the scan-and-filter in place at every other path.

---

## Open questions

**Referred, not decided — the hint line's wording.** D-5 requires that `browse` emit no row
wider than the terminal. Clipping the existing text is the mechanical answer, but at 40 columns
a clipped hint loses most of its content, so shortening or splitting the text may be the better
product answer. What the hint *says* is a UX choice that belongs to whoever owns the verb's
feel, not to this document. Implementor: clip it so the test passes, and flag the wording for a
decision rather than inventing final copy.

**Unverified assumption.** I have not read `Tui`'s public surface, so I do not know whether it
exposes the terminal width and a clip helper that `BrowseCommand.Loop` can call. If it does not,
that is a spec gap — report it rather than duplicating a second clipping implementation, which
is the failure D52 names.

**Recommended, not decided — commit spec 05 before falsifying.** See the falsification hazard
above. This is a workflow call with a cost either way and it is not mine to make.

---

## Confidence

**High** on the defect, the fix, and the zero-regression argument: all three were read from
source rather than inferred, and the non-root invariance is a property of the code path rather
than an empirical claim.

**High** on `Fold` being the second half of the bug, and on assertions 4–5 being the guard that
distinguishes a fix from a half-fix.

**Medium-high** on the predicted `BrowsePtyTests` failure. The mechanism is certain — those two
lines bypass clipping and root is the branch that reaches them — but the exact character count
came from a transcription, not from my own read, so treat "roughly 80 characters" as the shape
of the problem rather than a measurement. It does not change what to do.

**Not applicable** on the two NEEDS-EVIDENCE items: they are open by construction, and the
remedy for each is designed so that resolving them needs a measurement, not another design pass.

Line numbers in `MemoryBrowser.cs` were read directly and are reliable. Line numbers cited for
`BrowseCommand.cs`, `FactStore.cs` and the D-4 table came through a retrieval agent whose output
showed duplicated line numbers in places — **grep by symbol rather than trusting those numbers**.
