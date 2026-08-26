# Code navigation — Phase 2 implementation spec (the edge substrate)

**Status:** design, not implemented. Written to be handed to an implementor who has read nothing
else.

**Relationship to `docs/code-navigation-spec.md`.** That document is the source of truth for scope,
phasing and rationale; its §4 is the design this spec implements. This file is the *implementable*
form: exact files, signatures, ordering, edge cases, tests. Where the two disagree on an
implementation detail, **this file wins and says why** — §2 below lists every correction, because
three claims in §4 turned out not to survive a source read.

**Ships:** nothing user-visible except one change to `imports` fact shape. **Schema change:**
migration v13. **This is the phase to be careful in** — every defect available here is silent.

---

## 1. What Phase 2 is

Today a `fact` row can only say *subject → predicate → body*. `fact.object_id` exists in the
schema (`docs/engram-schema.sql:125`, `object_id INTEGER REFERENCES entity(id)`) and **nothing in
`src/` writes it** except the backup journal's insert and its read-back join. Code navigation needs
edges — `A calls B`, `F imports M` — which are subject → predicate → *object* triples, many per
subject.

Phase 2 builds that substrate and proves it on one predicate (`imports`). Phase 3 puts `calls`
through it. **No `calls` extraction in this phase.** No new languages (Python/Go/Rust/Java are
deferred by the owner to a separate spec — do not fold them in).

The single blocking constraint: `ux_fact_live` is `UNIQUE (subject_id, predicate) WHERE valid_to IS
NULL` (`docs/engram-schema.sql:158`), so a subject may hold exactly one live fact per predicate.
One-fact-per-edge is impossible without a migration.

---

## 2. Corrections to `code-navigation-spec.md` §4

Recorded here rather than silently implemented. Each was read from source during this spec's
authoring.

**C1 — §4.5's "21 call sites" and "the backup journal reads through it" are both wrong.**
`grep -rn 'ReadLive(' src/ --include=*.cs` returns eight hits, of which **only two are
`FactStore.ReadLive` calls**: `FactCatalog.cs:38` and `CannedFactSeeder.cs:127`. The rest are
`DirectiveFacts.ReadLive`, a different method on a different type. **`FactJournal` does not call
it** — the journal runs its own SQL. So §4.5's stated reason for rule 1 (*a recovery tool would
lose rows*) does not apply to `FactStore.ReadLive`.

**Confirmed by E9** (§9): the test assemblies hold 34 `ReadLive(` calls across 12 files, all
against the same two methods, and the four bare-`ReadLive` hits are test *method names*, not
method-group usage. There is no hidden caller of a third overload.

The **rule still stands**, on different and weaker grounds: `ReadLive` is a public general-purpose
"every live fact" reader, its own XML doc and `ReadEverWritten`'s remarks lean on that meaning, and
a filtering default would change what every future caller gets without any of them asking — and
34 test call sites is itself evidence of how load-bearing that meaning is. So: **exclusion is
opt-in at the reader, never a new default.** The "classify 21 call sites" work item in §4.5 rule 2
shrinks to classifying two.

**C2 — `fact_fts` is maintained by SQL triggers, not by C# call sites.**
`EngramDatabase.RebuildFactFts` (`EngramDatabase.cs:485`) drops and recreates `fact_fts` **and four
triggers** (`fact_fts_insert`, `fact_fts_close`, `fact_fts_delete`, `fact_fts_repath`), with
`fact_fts_insert` being `AFTER INSERT ON fact`. §4.4 says to exclude edges "where the decision
already lives", which is right, but the mechanics differ per lane: `FactTokenIndex.Add`/`Remove`
are C# and take a plain guard; **FTS exclusion must go into the trigger bodies as SQL.** §5.4 below
specifies how, and why the predicate list may not be typed twice.

**C3 — the `imports` orphan risk §4.6 does not mention is already handled, and a different one is
not.** `CodeIndexer.cs:528-530` closes every live regenerable fact for the file whose
`(Path, Predicate)` key was not matched by a candidate, so once candidate keys gain an object
dimension the old objectless `imports` fact is unmatched and closed on the same run. Good. But
`live` is a **dictionary keyed on `(Path, Predicate)`** (`CodeIndexer.cs:505-508`,
`live.TryGetValue(key, …)`): the first time one file holds two live `imports` facts, building that
dictionary throws `ArgumentException` on the duplicate key. **This is why §5 must land as one
change and not incrementally** — see §5.0.

---

## 3. Files this phase touches

| File | Change |
|---|---|
| `docs/engram-schema.sql` | replace the `ux_fact_live` DDL; add `ux_fact_edge_live` |
| `src/Engram.Core/EngramDatabase.cs` | `SchemaVersion` 12 → 13; a v13 arm in `Migrate`; edge-predicate exclusion inside `RebuildFactFts`'s trigger DDL |
| `src/Engram.Core/FactStore.cs` | `FactWrite` gains **`ObjectPath`** + `ObjectKind`; `Remember` writes `object_id`; `FindLiveFactId` becomes object-aware; a predicate-filtered read for the retrieval path |
| `src/Engram.Core/FactTokenIndex.cs` | `Add` skips edge predicates; `CountMissing` learns the same exclusion |
| `src/Engram.Core/CodePaths.cs` | new `ForSymbolName` / `SymbolNameOf` / `SymbolNameRoot` |
| `src/Engram.Core/CodePredicates.cs` | **new file** — the one declaration of the edge-predicate set |
| `src/Engram.Core/CodeIndexer.cs` | diff key and `live` dictionary gain the object dimension; the write loop names object entities |
| `src/Engram.Core/CodeAnalyzer.cs` | `AddImports` emits one candidate per module; `AnalyzerVersion` 2 → 3 |
| `src/Engram.Core/DeepAnalysis.cs` | `Merge`'s import emission, identically |
| `src/Engram.Core/FactCatalog.cs` | reads through the filtered path |
| `src/Engram.Core/FactJournal.cs` | **four edits** — §6 |

Do **not** touch: `fact_relation`; the `learned_via` `CHECK`; `EngramDatabase.Open`;
`CodePaths.ForSymbol`'s output; `CodePaths.GrammarVersion`; the `edge` table (leave it, unused,
with a comment marking it superseded by D70 — `BackupStore.cs:53` still counts it).

---

## 4. The one declaration of what an edge predicate is

New file `src/Engram.Core/CodePredicates.cs`:

```csharp
namespace Engram.Core;

/// <summary>
/// The predicates whose facts carry an <c>object_id</c>. A predicate is either always
/// object-bearing or never — the two partial unique indexes of schema v13 do not compose
/// otherwise, and an objectless and an object-bearing live fact would coexist on one
/// subject+predicate with both returned.
/// </summary>
public static class CodePredicates
{
    public static readonly IReadOnlySet<string> EdgeBearing =
        new HashSet<string>(StringComparer.Ordinal) { "imports" };   // Phase 3 adds "calls"
}
```

Every consumer reads this set: the §5.4 lexical exclusions, the §5.5 retrieval filter, and the
§7 lints. **`RebuildFactFts` interpolates it into the trigger SQL rather than repeating it as a
literal** — a second, hand-typed list in SQL is the same defect class CLAUDE.md records for the
tokenizer (`fact_token` is C#-maintained precisely because a SQL twin agrees until one is tuned).

**Do not key the exclusion on `scope = 'code'`.** `about` and `declared-as` are code-scoped, are
useful in lexical recall today, and must keep working.

---

## 5. The work, in the order it must land

### 5.0 Ordering constraint

§5.1 through §5.6 **land as one change**. Per **C3**, `CodeIndexer`'s `live` dictionary throws the
first time a file has two live facts on one predicate, and the migration is what makes that state
reachable. Splitting the commit gives a window where the store is migrated and the indexer crashes
on the second import. §6's edits may land in the same commit or immediately after — nothing in the
index path depends on them — but they may not be deferred past the phase, because from the moment a
store holds two edges its journal is silently lossy.

Intermediate commits must still build.

### 5.1 Migration v13

`EngramDatabase.SchemaVersion` is `12` (`EngramDatabase.cs:23`); `Migrate(connection, from)` is at
`:248`. D31's pre-migration snapshot already runs at `:145` — **no additional protection is
specified**, and per D8 this touches only derived state (indexes, and the FTS index rebuilt below):
it creates, alters and deletes no fact body, predicate, validity window or supersession row.

```sql
DROP INDEX ux_fact_live;

CREATE UNIQUE INDEX ux_fact_live ON fact(subject_id, predicate)
  WHERE valid_to IS NULL AND object_id IS NULL;

CREATE UNIQUE INDEX ux_fact_edge_live ON fact(subject_id, predicate, object_id)
  WHERE valid_to IS NULL AND object_id IS NOT NULL;
```

Two **disjoint** partial indexes. This is the whole point and the obvious alternative is silently
wrong: adding `object_id` to the existing unique index would make it constrain nothing, because SQL
treats NULLs as distinct and every ordinary fact has a NULL object. Together the two say: one live
belief per subject+predicate for ordinary facts, one live edge per subject+predicate+object.

`ix_fact_thread` (`schema.sql:170`, non-partial, `(subject_id, predicate)`) is unchanged and must
not be dropped — CLAUDE.md records it as 93% of every recall.

The v13 arm must also call `EngramDatabase.RebuildFactFts`, because §5.4 changes the trigger DDL
and existing stores carry the old triggers.

**Falsification, per D60.** The fixture must be a **genuine v12 store**. `WriteVersion1Store` rolls
a *current*-schema store back, so a fixture built that way already has the new indexes, the
migration no-ops, and every test stays green — this exact trap cost a session during D60's index
work. Drop the indexes explicitly in the fixture, and assert with `git diff --quiet` that the patch
under test actually landed before trusting any arm.

### 5.2 The object is a name, never a resolved declaration

Settled by Ultra-Advisor ruling (~85% confidence; reasoning in `code-navigation-spec.md` §5.2).
`object_id` points at an entity keyed by **the name as written at the call/import site**. Binding
that name to a declaration is a query-time join in Phase 3, not stored belief content — `object` is
immutable belief content, and a baked-in resolved target could only be supersession-churned, never
repaired.

Object entities live in **their own addressing namespace**, distinct from `CodePaths.ForSymbol`'s
`{filePath}#{fragment}` addresses. A name is not a location and must not be spellable as one.

```csharp
public const string SymbolNameRoot = "/symbol-names";

/// <summary>Address for a callee/module name as written. Not a location.</summary>
public static string ForSymbolName(string name) =>
    $"{SymbolNameRoot}/{EncodeNameSegment(name)}";

public static string? SymbolNameOf(string path) => /* inverse; null if not under the root */;
```

Three rules on the encoding, each load-bearing:

1. **The name is not slugged.** `CodePaths.Slug` lowercases; `Foo` and `foo` are different symbols
   in every language this indexes. Slugging an object name merges them permanently.
2. **`/` and `%` in the name are percent-encoded** (`%2F`, `%25`). Module names legitimately
   contain slashes (`./utils/foo`, `@scope/pkg`), and an unencoded one manufactures fake path
   segments that every prefix-scan surface — `MemoryBrowser`, `repair`'s denormalized paths —
   renders as a hierarchy that does not exist. Encoding keeps the map injective and the round trip
   testable. Guard: `SymbolNameOf(ForSymbolName(n)) == n` over a table including `./a/b`,
   `@scope/pkg`, `a%b`, `Foo.Bar`, `os.path.join`.
3. **Qualifiers are kept as written** — `Foo.Bar`, `os.path.join`, not normalized. Normalizing is
   resolution, and resolution does not live here.

`entity.kind = 'symbol-name'`. **Confirmed collision-free by E10** (§9): a live store holds
`agent, concept, file, note, repo, section, session, statement, symbol, topic` — no `symbol-name`.

**`SymbolNameOf` is not a convenience — it is the derivation two write paths depend on.** The
indexer knows an object's display name because it just read it out of the source; **replay does
not**, and must recover it from the path (§6, edit 4). That is the whole reason the encoding is
required to be injective, and why the round-trip guard above is the load-bearing test rather than a
tidiness check.

**The object's *path* is what the journal carries** (`FactJournal` joins the object entity on read
and serializes `object` plus `object_kind`), which is what makes §6's widening portable across
stores. Never the id.

One entity row per distinct name per store — bounded by distinct identifiers, not by call sites.

### 5.3 Write path

**`FactWrite`** (`FactStore.cs:6`) gains two defaulted fields, so no existing call site changes:

```csharp
public sealed record FactWrite(
    string SubjectPath, string SubjectKind, string Predicate, string Body,
    string Scope, string LearnedVia,
    string? Evidence = null, bool Regenerable = false,
    long? SessionId = null, string? Details = null,
    string? ObjectPath = null, string? ObjectKind = null);
```

**The field is `ObjectPath`, and the name is the specification.** An earlier draft of this spec
called it `Object`, which reads as *the name* and invites a caller to pass `"react"`. It is an
**entity path** — exactly like `SubjectPath` beside it, and it is handed to `EnsureEntity` verbatim.
Callers writing a symbol-name edge produce it with `CodePaths.ForSymbolName(name)`. Rationale in
§5.3.1.

**`FactStore.Remember`** resolves the object through `FactStore.EnsureEntity` and names `object_id`
in its `INSERT`.

#### 5.3.1 `Remember` does not apply `ForSymbolName`, and must not

The tempting fix — have `Remember` call `CodePaths.ForSymbolName` on whatever it is given — is
rejected on three grounds:

1. **`object_id` is a general `entity` reference, not a symbol-name column.** The schema constrains
   it to `entity(id)` and nothing narrower, and §6.4's replay ruling depends on that generality:
   `SymbolNameOf` returning null for a non-symbol object is specified behaviour, not a gap. A
   `Remember` that converted unconditionally would make `/symbol-names` the only addressable object
   namespace by accident, foreclosing a design decision this phase has no reason to take.
2. **It double-encodes the correct caller.** A caller that already did the right thing hands over
   `/symbol-names/react`; converting again yields `/symbol-names/%2Fsymbol-names%2Freact`. Any
   guard against that is a "does this already look converted?" sniff, which is a heuristic standing
   where an explicit contract should be.
3. **It is asymmetric with `SubjectPath`.** `Remember` does not transform the subject either — the
   caller addresses it. One rule for both sides is what makes the record readable.

**So the convention is enforced by the type name, by §7.2's data lint, and by fixing the call
sites — never by a transform inside `Remember`.**

#### 5.3.2 Call-site conformance

Existing tests pass raw un-encoded names (`Object: "react"`) rather than
`CodePaths.ForSymbolName("react")`. Harmless today only because nothing round-trips them — and that
is precisely the failure class §6.4 was just ruled on: an object entity addressed outside
`SymbolNameRoot` makes `SymbolNameOf` return null, so replay creates it nameless and **nothing
reports it**.

The rename `Object` → `ObjectPath` is what makes this a mechanical fix: it is a compile error at
every call site, so the compiler enumerates the work rather than a grep. Update each to
`ObjectPath: CodePaths.ForSymbolName("react")`. Do this **in this phase**, while the call sites
number a handful; Phase 3's `calls` extraction multiplies them.

#### 5.3.3 Naming the object entity

**Object entities are named explicitly, by the caller, before `Remember`.** This is §3.6's rule and
it applies to the object side exactly as to the subject side: `EnsureEntity` returns early when the
path already exists (`FactStore.cs:652-661`), so an entity's `name` is **write-once** — whoever
creates the row wins permanently, and no later call corrects it. `Remember`'s own `EnsureEntity`
call cannot carry a display name (that is what the comment at `CodeIndexer.cs:545-546` says). So
the caller does `EnsureEntity(objectPath, "symbol-name", …, displayName: nameAsWritten)` **first**,
then `Remember`. Preferred over giving `Remember` an object display-name parameter, for symmetry
with the subject side and to keep `FactWrite` from growing a field whose only job is naming.

**Write-once applies to every path that can create the row first, not just this one.** Replay is
the other one; see §6, edit 4.

#### 5.3.4 `FindLiveFactId` must become object-aware

Current signature (`FactStore.cs:259`, `public static`, so check for callers outside `FactStore`):

```sql
SELECT f.id FROM fact f JOIN entity e ON e.id = f.subject_id
 WHERE e.path = $path AND f.predicate = $predicate AND f.valid_to IS NULL;
```

For an edge write it must match `(subject, predicate, object)`; for an objectless write,
`(subject, predicate)` **with `object_id IS NULL`** — not "ignoring object", which would find an
edge and close it.

**Getting this wrong is silent and destructive.** Left as-is, writing `A imports B` finds and
closes `A imports C`: a file with five imports ends each index run with one live edge and four
spuriously superseded ones, which reads as a codebase that keeps changing its mind. The guard is a
tier-2 test that writes two distinct edges from one subject and asserts **both stay live**, and it
must be shown to fail against the unmodified `FindLiveFactId` before it is trusted.

`Remember`'s close-then-insert ordering (`FactStore.cs:83-100`) stays exactly as it is — the
comment there explains that SQLite checks the partial unique index per statement, not at commit.
That reasoning transfers unchanged to `ux_fact_edge_live`.

#### 5.3.5 `CodeIndexer`

`CodeCandidate` (`CodeIndexer.cs:12`) gains `string? Object = null`; the diff key at `:505` and the
`live` dictionary's key and the `closes` comparison at `:529` all become `(Path, Predicate,
Object)`. All three, or the dictionary throws (**C3**). The write loop keeps
`EnsureEntity`-then-`Remember` for the subject and adds the same two-step for the object, passing
`ObjectPath: CodePaths.ForSymbolName(candidate.Object)`.

`CodeCandidate.Object` **is** the raw name, deliberately — the indexer works in names and converts
at the `FactWrite` boundary, which is the one place the two representations meet. `CodeCandidate`
is compared against source; `FactWrite` is compared against the store.

### 5.4 Edges stay out of the lexical lanes

**Code edges must not enter `fact_fts` or `fact_token`.** A design constraint, not an optimization,
with two independent arguments.

*Correctness.* D44 computes `coverage` from lane agreement across the scored set. Tens of thousands
of near-identical edge bodies (`imports react`, `imports react-dom`, …) are corroboration-shaped
noise that would inflate coverage in the direction that looks like success — precisely the defect
D44 exists to correct. Nobody recalls an edge body in words.

*Cost.* Both lanes are corpus-proportional in measured ways: `fact_token` holds 701,358 rows at
50,097 live facts and rebuilds in 4,161 ms, and `repair --apply --tokens` runs from the
session-start child on **every** session. E3 bounded the input: 18,307 call-shaped sites against
~5,308 live facts in this repository — one repository's call graph is the same order as this
store's entire corpus, and a store indexing several checkouts multiplies it. The exclusion is
load-bearing, not hygiene.

Two lanes, two mechanisms (**C2**):

- **`fact_token`** — C#. `FactTokenIndex.Add` skips when the fact's predicate is in
  `CodePredicates.EdgeBearing`. `Remove` stays unconditional (removing what was never added is a
  no-op and a conditional there would strand rows if the set ever shrinks).
  **`CountMissing` must learn the same exclusion**, or `TokenIndexNeedsRebuild` goes permanently
  true and every `repair` rebuilds while none stops the next — the same failure mode the
  zero-token exclusion already guards, per D59.
- **`fact_fts`** — SQL triggers inside `EngramDatabase.RebuildFactFts` (`EngramDatabase.cs:485`).
  Add `WHERE new.predicate NOT IN (…)` to `fact_fts_insert`; audit `fact_fts_close`,
  `fact_fts_delete` and `fact_fts_repath` for the same need (a delete/close of a row that was never
  indexed must be harmless, not an error). The predicate list is **interpolated from
  `CodePredicates.EdgeBearing`**, never typed as a SQL literal.

**Falsification:** index a file with imports, then assert `SELECT count(*) FROM fact_fts` and the
`fact_token` row count are unchanged from before. And verify against `fts5vocab`, not a plain
`SELECT rowid FROM fact_fts` — on an external-content table every non-MATCH query is answered from
the *content* table, so the obvious check compares `fact` against itself and calls any state
healthy. CLAUDE.md records that trap costing a whole detector.

### 5.5 Edges must also leave the recall candidate scan

**Excluding edges from the lexical indexes stops them *matching*. It does not stop them being
*read*.** Verified:

- `FactStore.ReadLive` (`:283`) selects every row `WHERE f.valid_to IS NULL` with an optional
  *scope* filter and **no predicate filter**.
- `RecallEngine.BuildCandidates` (`RecallEngine.cs:405`) iterates that list in three loops
  (`:417`, `:430`, `:443`) with **no skip condition**.

So with §5.4 fully implemented, a 3–6× corpus still makes recall's candidate construction 3–6×
more work *per call*, and the primer's topic histogram — already ~40 ms at 50,097 facts, scanning
every live fact — scales with it. D58 records recall as paying for the match set rather than the
corpus; edges in `ReadLive` would silently undo that for every query, including ones with nothing
to do with code. A store indexing two repositories could regress the session-start primer past its
measured envelope **without a single failing test**.

Requirement: the retrieval path must not read edge facts at all.

1. **`ReadLive`'s default stays "everything live", edges included.** Exclusion is a new opt-in
   parameter (`bool excludeEdges = false`, or a separate `ReadLiveForRetrieval`), never a changed
   default. See **C1** — the grounds are that it is a general "every live fact" reader with 34 test
   call sites resting on that meaning, not the backup-journal claim the parent spec made.
2. **Classify the call sites before changing any of them.** In `src/` there are exactly two:
   `FactCatalog.cs:38` is the retrieval path and takes the exclusion; `CannedFactSeeder.cs:127`
   does not. **Report the classification with the diff.** A call site the spec does not settle is a
   spec-defect to report back, not a judgment call to take.
3. **Filter by predicate, in SQL, not in the loop.** `WHERE f.predicate NOT IN (…)` on the
   retrieval reader is what avoids the transfer; filtering inside `BuildCandidates` still pays for
   every row crossing the boundary, which is the cost being removed.

**Falsification:** seed a store with N ordinary facts and 5N edges and assert the recall path's
**row count** is N. Shown failing before the change, per D60. A timing assertion alone will not
hold this — the ratio collapses to nothing on a small fixture. Count rows.

**On an explicit volume bound: not now, and not a cap.** D53's lesson is that bounding enumeration
without reporting partiality turns a slow scan into a destructive one — a truncated file walk read
as a repository whose files were all deleted. A cap on edges per symbol has exactly that shape: a
truncated `callers` list is indistinguishable from *nothing calls this*, which `engram_navigate`
already forbids as an answer. If Phase 3's measured volume demands a bound, it must be a bound that
**says it was hit**, at the query surface, in the same breath as the answer, specified against a
real extractor's numbers rather than E3's grep proxy.

### 5.6 `imports` becomes edges

`imports` converts from one joined-string fact per file to **one object-bearing fact per module**,
the object being the module name as written. This is the phase's only visible behaviour change, and
it exercises the whole substrate on a predicate that already has data — the cheap rehearsal for
§5.5, at a volume small enough to be safe.

Two emission sites, and **both must change or tier-1/2 files keep writing the joined form**:

- `CodeAnalyzer.AddImports` (`CodeAnalyzer.cs:88-118`) — currently
  `Cap("imports " + string.Join(", ", modules))` as one candidate.
- `DeepAnalysis.Merge` (`DeepAnalysis.cs:123-132`) — re-emits the identical shape from
  `analysis.Imports`.

New shape, per module `m`, in the same `SortedSet` ordinal order:

```csharp
new CodeCandidate(fileEntityPath, "file", fileName, "imports", CodeAnalyzer.Cap("imports " + m),
                  Object: m)
```

Keep `Cap`.

**Bump `CodeAnalyzer.AnalyzerVersion` 2 → 3.** That is the existing mechanism for exactly this
("the bump is what makes existing stores re-read under the better extractor",
`CodeAnalyzer.cs:26-28`). **Do not bump `CodePaths.GrammarVersion`** — addressing is unchanged, and
a grammar bump forces a full re-index of every store.

**The old objectless `imports` facts are closed automatically**, provided §5.3's key change lands:
`CodeIndexer.cs:528-530` closes every live regenerable fact for the file not matched by a
candidate, and the old fact's key `(path, "imports", null)` matches no new candidate. **Assert
this** — index a file under v2, migrate, re-index under v3, and assert zero live facts with
`predicate = 'imports' AND object_id IS NULL`. That assertion is what holds §7.1's invariant across
the conversion; without it the store ends with `imports` on both sides of the two indexes and both
facts are returned.

---

## 6. Backup replay — four edits in one file

**Edits 1–3 were ruled by the Ultra-Advisor (~90%, source-read) and have landed.** Edit 4 is an
amendment: the Reviewer's finding N1, ruled here.

**Line anchors below are from the tree with edits 1–3 applied** and were verified directly. They
sit ~26 lines below the pre-edit numbers, so do not cross-reference them against the ruling's.

**Note on naming:** `JournalFact.Object` (`FactJournal.cs:15`) is a **path**, correctly — the
journal serializes the object entity's `path` under the key `object`. It is a different record from
`FactWrite`, whose field §5.3 renames to `ObjectPath` to buy in C# the clarity the journal already
gets from its serialization format. **Do not rename `JournalFact.Object`** — that would change the
journal format §6.5 preserves.

### 6.1 Why replay needs anything at all

`backup replay` is additive and idempotent: it may never rewrite or close a fact the target already
had, and what it cannot write it **skips and counts** rather than aborting — "already there" and
"not recovered" are the two answers a recovery tool exists to tell apart. Both mechanisms that
enforce that were written against **one** live-uniqueness rule; v13 introduces a second. Separately,
replay is the one path other than the indexer that can *create* an object entity, which is what
edit 4 is about.

### 6.2 The four edits

| # | Site | Now | Must become | State |
|---|---|---|---|---|
| **1** | `Existing`'s match SQL | `e.path` + `f.predicate` + `f.body` + `f.valid_from` | the same **plus the object**, matched by the journal-carried object **path** (`IS NULL` when absent) | **landed** |
| **2** | `WouldDisplaceALiveBelief`'s live probe (`:469`/`:510` branch on `fact.Object is null`) | `e.path` + `f.predicate` + `f.valid_to IS NULL` | **partitioned to mirror the two v13 indexes exactly** | **landed** |
| **3** | the `claimed` set (`:343`, tested `:459`) | `HashSet<(string Subject, string Predicate)>` | `HashSet<(string Subject, string Predicate, string? Object)>` | **landed** |
| **4** | `Insert`'s object resolution, **`FactJournal.cs:565-566`** | `EnsureEntity(…, fact.Object, fact.ObjectKind ?? "concept", fact.CreatedAt)` — **no display name** | pass a display name derived from the object path | **RULED, open** |

### 6.3 What edits 1–3 prevent

**Edit 3 — the `claimed` set alone lost every edge after the first, even into an empty store.**
`:459` is `if (!claimed.Add(…)) return true;` — *true* meaning *would displace, skip it*. A journal
carrying `A imports X` and `A imports Y` added `(A, imports)` on the first and was refused on the
second, which was counted as a conflict and dropped. No store state consulted at all.

**Edit 2 — unwidened, replay could not abort; it under-recovered silently.** This corrected the
risk framing this spec previously carried, and the correction matters because it changes what to
watch for. The probe tested `(subject, predicate)` liveness, which is **strictly broader** than
`ux_fact_edge_live`'s `(subject, predicate, object)`: everything the new index would reject was
already caught earlier, so no index violation was reachable and the catastrophic "recovered
nothing" abort was **not** the failure mode. It was first-edge-wins — a smaller blast radius with a
worse detection story, because the run reports success.

**Edit 1 — addressing correctness, not deduplication.** `Existing`'s own comment states the
assumption out loud: *"Live first, so row 0 is the live one when any match is live — **`ux_fact_live`
guarantees at most one is**."* v13 breaks that guarantee for edges. Two edges sharing
`(subject, predicate, body, valid_from)` make `ambiguous` (`:545`) true, `AddressUsable` (`:550`)
false, and `idMapBuilder` then either goes unset or points at the wrong row — so a supersession in
the journal lands on an edge it was never about.

**Update that comment with the tuple.** A comment stating a guarantee the schema no longer gives is
worse than none.

### 6.4 Edit 4 — RULING on N1: name the object on replay

**The Reviewer is right, and the gap was mine.** §6 enumerated three edits about *comparison* and
never asked what replay *creates*. `Insert` at `:562-567` calls `EnsureEntity` for the object with
no display name, so on the path that matters most — recovery into a fresh store, where replay
creates the row first — every `symbol-name` entity is **nameless forever**, because §5.3.3's
write-once rule cuts both ways. A later `engram index --apply` cannot correct it: `EnsureEntity`
short-circuits on the existing path and never touches `name`.

**Ruling: fix it. Pass a display name derived from the object path.**

```csharp
long? objectId = fact.Object is { Length: > 0 }
    ? EnsureEntity(connection, transaction, fact.Object, fact.ObjectKind ?? "concept",
                   fact.CreatedAt, displayName: CodePaths.SymbolNameOf(fact.Object))
    : null;
```

Four reasons, in order of weight:

1. **A recovery tool that produces a store differing from the original is defective**, even when the
   difference is cosmetic. This is the project's most-guarded property: replay already refuses to
   close a live belief and counts what it skips rather than aborting. "Recovered, but every symbol
   name is blank" is exactly the quiet divergence that discipline exists to prevent.
2. **Write-once makes it unrecoverable in practice, not just unrecovered.** Nameless is not data
   loss — the name is derivable — but nothing in the normal flow will ever derive it, because the
   only code that would is short-circuited by the row already existing.
3. **The repair route costs more than the fix.** `entity.name` is D8-repairable derived state, so
   `repair` *could* rebuild it — but only by learning the same path→name derivation, which is a
   second implementation of §5.2's rule and diverges the first time either is tuned. One argument
   at the write is strictly cheaper than a rule in two places.
4. **It is nearly free.** `SymbolNameOf` is deterministic, already required by §5.2, and already
   guarded by the round-trip test.

**Rejected: carrying the display name in the journal.** It is the obvious alternative and it is
wrong three times over — it changes the journal format, which §6.5 deliberately preserves; it
stores what is derivable, against D8; and §5.2's encoding is injective *by construction and by
test*, so the path already carries the name losslessly. The journal carries `object` and
`object_kind`; it does not need `object_name`.

**Fallback behaviour, specified.** `SymbolNameOf` returns null for a path outside
`/symbol-names` — a general object entity, which the `object_id` column permits. Passing null means
"no display name", i.e. today's behaviour, which is correct: **derive the name when a rule applies,
and never invent one when none does.** Do not build a kind→derivation dispatch table for one kind;
when a second object kind arrives, that is the point to generalize, and this is the extension point.

That fallback is also **why §5.3.1 forbids `Remember` from converting silently**: an object
addressed outside `SymbolNameRoot` is legal by design, so nothing on this path can distinguish "a
general object entity" from "a symbol name a caller forgot to encode". Only §7.2's lint can.

**The `?? "concept"` kind fallback stays as-is.** Kind is write-once for the same reason name is, so
a wrong kind is equally permanent — but no writer can produce a journal line with an object and no
`object_kind` (§5.3 requires both), so the fallback is unreachable defensive code rather than a live
defect. Leave it; do not build a second guess on top of it.

**Falsification:** replay a journal containing one edge into an **empty** store and assert the
object entity's `entity.name` is the module as written. Show it failing by removing the
`displayName` argument. An assertion on the *fact* passes either way — the name is the only thing
that differs — so the assertion has to read `entity.name` directly.

### 6.5 Pre-v13 journals

**They replay byte-for-byte identically.** Every line written before this phase has a null object —
`FactJournal` is `object_id`'s only current reader and writer, and nothing has ever populated it —
so all three widened comparisons take their `IS NULL` branch and edit 4's derivation is never
reached. **No journal format bump.**

### 6.6 Tests

- Journal a store holding two live edges from one subject on one predicate; replay into a fresh
  store; assert **both arrive live** and the run reports **zero conflicts**. Replay again and
  assert both are counted `AlreadyPresent` with nothing written.
- Run the same over the **dry-run** arm and assert its counts match the apply's. Both arms failed
  before edit 3.
- A **null-object journal written pre-v13, replayed into a v13 store**, matches the v12 counts
  exactly — the regression guard for §6.5.
- **Edit 4's guard:** replay into an empty store, read `entity.name` for the object, assert it is
  the module as written (§6.4).

---

## 7. Invariants and their lints

### 7.1 A predicate is either always object-bearing, or never

The two partial indexes do not compose otherwise — an objectless and an object-bearing live fact
could coexist on one subject+predicate, and **both would be returned**. This is why `imports` is
converted wholesale in this phase rather than gaining objects incrementally.

- **Static (tier 1).** Every predicate written with an object is in `CodePredicates.EdgeBearing`
  and every predicate in that set is only ever written with a non-null object. Enforce over the
  emission sites.
- **Data (tier 2), after a real index run.** This must be empty:

  ```sql
  SELECT predicate FROM fact WHERE valid_to IS NULL AND object_id IS     NULL
  INTERSECT
  SELECT predicate FROM fact WHERE valid_to IS NULL AND object_id IS NOT NULL;
  ```

### 7.2 An edge's object is addressed under `SymbolNameRoot`

The lint that catches §5.3.2's defect class. Because `Remember` deliberately does not transform
(§5.3.1) and `SymbolNameOf`'s null is legal (§6.4), **this is the only place a mis-addressed edge
object is detectable.** Tier 2, after a real index run — this must be empty:

```sql
SELECT e.path FROM fact f JOIN entity e ON e.id = f.object_id
 WHERE f.valid_to IS NULL AND f.predicate IN (…EdgeBearing…)
   AND e.path NOT LIKE '/symbol-names/%';
```

Scoped to `EdgeBearing` predicates on purpose: a future non-code object-bearing fact addressed
elsewhere is legal and must not red this.

A lint that cannot fail is worthless: prove each of the three by breaking what it guards, then
restore.

---

## 8. Knock-ons to check, each cheap and each silent if missed

- **`FactStore.VersionCounts`** groups on `(e.path, f.predicate)` to produce D57's `· vN` recall
  marker. With multiple live edges under one `(path, predicate)`, that count becomes the edge count
  rather than a revision count — it would advertise history that does not exist. Edges leave the
  retrieval read in §5.5, so the marker should never be computed for one; **confirm that, don't
  assume it**, and confirm the grouping query itself isn't reading edges.
- **`StoreCompactor.cs:253`** already matches `fact.object_id = entity.id` when deciding an
  entity's liveness, so `symbol-name` entities are covered by the existing entity GC. Verify that
  a name entity whose last edge is closed is handled the way the design wants (probably: kept, since
  closed facts still reference it).
- **`repair`** — its from-scratch recomputations must see edges (they are the authority on what the
  derived indexes should hold), so they read through the unfiltered path, not §5.5's.
- **`engram_navigate`** — Phase 1's `imports` answer reads the joined-string body. It must be
  updated to read edges, or it silently returns nothing after the conversion. This is the one
  user-visible regression available in Phase 2.
- **`doctor`** — reads only; nothing here should make it write. It opens with
  `EngramDatabase.Open`, never `OpenInitialized`, so it will *report* a v12 store as a schema
  behind rather than migrating it. That is correct and must stay.

---

## 9. NEEDS-EVIDENCE

| # | Question | Status |
|---|---|---|
| **E9** | What calls `FactStore.ReadLive` outside `src/`? | **RESOLVED.** 34 `ReadLive(` calls across 12 test files, all against the same two methods C1 identified; the four bare-`ReadLive` hits are test *method names*. No hidden overload. |
| **E10** | Is `symbol-name` collision-free as an `entity.kind`? | **RESOLVED.** A live store holds `agent, concept, file, note, repo, section, session, statement, symbol, topic`. No collision. |
| **E11** | Does replay's identity matching read `object_id`? | **RESOLVED.** It did not; D64's tuple, confirmed from source. Folded into §6, edit 1. |
| **E12** | Where is replay's *conflict* check, and does it test `(subject, predicate)` liveness? | **RESOLVED.** `WouldDisplaceALiveBelief` plus the `claimed` set; yes, `(s, p)` only. Folded into §6, edits 2 and 3. |
| **E8** *(carried)* | `SELECT name, COUNT(*) FROM entity WHERE kind='symbol' GROUP BY name HAVING COUNT(*)>1 ORDER BY 2 DESC LIMIT 20;` | **OPEN, not blocking.** Sizes leaf-name ambiguity for Phase 3's join. |
| **E3** *(carried)* | Re-run against the **real extractor** at the end of Phase 3, reporting distinct `(caller, callee)` pairs per repository and per store. | **OPEN.** Any future volume bound (§5.5) is specified against that number, not against E3's grep proxy. |

Nothing in this spec is waiting on advice.

---

## 10. Acceptance

Phase 2 is done when all of the following hold, each with a test shown failing before its change:

1. A genuine v12 fixture store migrates to v13, both indexes present, `ix_fact_thread` intact.
2. Two distinct edges written from one subject on one predicate both stay live.
3. Writing an ordinary fact still closes and supersedes its predecessor exactly as before.
4. Indexing a file with three imports yields three live `imports` facts with three distinct
   `symbol-name` objects, correctly named (`entity.name` is the module as written).
5. Re-indexing a store written under `AnalyzerVersion` 2 leaves **zero** live
   `imports` facts with `object_id IS NULL`.
6. `fact_fts` and `fact_token` row counts are unchanged by an index run that writes edges
   (`fact_fts` verified through `fts5vocab`).
7. The recall path's candidate row count over a store of N facts + 5N edges is N.
8. §7.1's `INTERSECT` returns empty.
9. `SymbolNameOf(ForSymbolName(n)) == n` for the §5.2 table.
10. `engram_navigate imports` still answers correctly.
11. The §5.5 call-site classification is reported with the diff.
12. All four of §6.6 pass — two-edge replay live on both arms with zero conflicts, dry-run counts
    matching apply, a pre-v13 null-object journal replaying at v12 counts, and **a replayed object
    entity carrying its derived name**.
13. §7.2's query returns empty, and **every `ObjectPath:` in `src/` and the test assemblies is
    produced by `CodePaths.ForSymbolName`** — the rename makes the compiler enumerate them.

Every falsification asserts `git diff --quiet` first: a harness that restores arms with
`git checkout --` restores to HEAD, so an uncommitted change under test is reverted by its own
falsification and every arm goes red for the wrong reason.

---

## 11. Confidence, stated plainly

- **§5.1, §5.4, §5.6, §7.1** — high. Read from source, and the failure modes are named with their
  guards.
- **§5.2** — high throughout now that E10 confirms the `kind` value. High on the encoding
  *requirement* (a name is not a location; case must survive), moderate on percent-encoding as the
  specific mechanism: any injective, reversible encoding is acceptable and the round-trip test is
  the real requirement — and §6.4 makes that injectivity load-bearing rather than tidy.
- **§5.3** — high, and **corrected**: the field was named `Object` in an earlier draft, which is
  what let raw names into call sites. `ObjectPath` plus §7.2's lint is the fix; §5.3.1 states why
  the transform does *not* go inside `Remember`, which is the alternative worth arguing about.
- **§5.5** — high. E9 closed the last gap, and **C1 corrects the parent spec's stated reason
  without changing the rule**.
- **§6** — high. Edits 1–3 settled by the Ultra-Advisor plus E11/E12, anchors independently
  verified. Edit 4 ruled at high confidence: the mechanism is one argument, the alternative
  (journal-carried name) is rejected on three independent grounds, and the fallback is specified
  rather than left to the caller.
- **§7.2** — high on the need, moderate on `LIKE '/symbol-names/%'` as the exact predicate. It is
  correct given §5.2's encoding percent-escapes `/`, and it is the assertion to revisit first if
  the encoding changes.
- **§8's knock-ons** — these are things to check, not things I have verified. Each is written as
  "confirm", deliberately.
