# Code navigation — Phase 4 implementation spec (trust surface and measurement)

**Revision 2.** Written against `docs/code-navigation-spec.md` §7, and against the code as it stands
after Phase 3 landed (`5a1069e`, `693db7b` on `code-graph`).

Revision 2 folds in two rulings: Jim's on §5.2's wording, and the Ultra-Advisor's on §4.3, which
resolved the one question revision 1 declined to decide. §4.3 is now a contract rather than an open
fork, and item 15 is required rather than conditional.

---

## 1. What Phase 4 is, and what it turns out not to be

The master spec gives Phase 4 two halves. **One of them is already shipped, and the other does not
work as sketched.** Both corrections are load-bearing, so they come first.

### 1.1 §7.2 (telemetry) is DONE — do not build it again

`TelemetryEventKind.Navigate` exists (`src/Engram.Core/Telemetry.cs:117`), is listed in `All`, and
`EngramMcpTools.Navigate` already appends a record on every call
(`src/Engram.Cli/EngramMcpTools.cs:718`) carrying `Relation`, `Found` and `Tiers`. Its own doc
comment says it "must be instrumented from Phase 1", and the master spec asked for exactly that —
*"pull it into Phase 1 if the cost is trivial, which it appears to be."* It was.

Checked against §7.2's three requirements: its own kind, not folded into `recall` — **met**. Emitted
after the answer, recording relation, found, and tiers — **met**. Reports both ends if long-running
— **not applicable**, it is a single instant event, which is what §7.2 says it should be.

`navigate` is MCP-only; there is no CLI verb, so there is no second uninstrumented entry point.

**Phase 4 adds exactly one thing to telemetry**, specified in §6: an extraction-tier field, kept
separate from the existing match-tier field.

### 1.2 §7.1 (the stamp) is right in design and does not populate on its own

§7.1's core call is correct and this spec adopts it: a nullable `analyzer_tier` on `fact`, stamped at
write time, never inferred. Its two rejections stand and are not re-argued — not `learned_via`
(D19's closed `CHECK`, an orthogonal axis), and not derived at query time from
`LanguageRegistry.Resolve(path).Tier` (the registry says what a language is *entitled* to; a missing
grammar falls back to tier 0 silently at `CodeIndexer.cs:415-418`, so deriving would report tier 1
for a fact regex produced).

**What §7.1 did not account for is the write path's unchanged-skip.** `CodeIndexer.ProcessFile`
compares each candidate against the live fact at the same address and, when
`existing.Body == candidate.Body`, counts it `Unchanged` and writes nothing
(`src/Engram.Core/CodeIndexer.cs:558-562`). `DeepTier.Deduplicate` reinforces this deliberately —
its doc comment says it *"keeps the lowest line so re-indexing an unchanged file writes nothing."*

The consequence: **under insert-only stamping, a fact that already exists never acquires a tier.**

- Editing a file re-extracts it, but every fact whose body did not change is skipped.
- A `code_index_version` bump sets `versionForcedFull`, which bypasses *per-file* staleness and
  re-reads every file — and then ProcessFile still skips every unchanged body. **Forcing a full
  re-index does not populate the column.**
- `CodePaths.GrammarVersion` is forbidden from moving (master §9).

§4.3 resolves this. It is why this phase writes the column outside of insert at all, and the rule
that licenses it is narrow and stated there.

---

## 2. Files this phase touches

| File | Change |
|---|---|
| `src/Engram.Core/EngramDatabase.cs` | `SchemaVersion` 13 → 14; new `if (from < 14)` block in `Migrate()` |
| `docs/engram-schema.sql` | `fact.analyzer_tier INTEGER` added to the `CREATE TABLE fact` statement |
| `src/Engram.Core/DeepAnalysis.cs` | `DeepAnalysis` gains `Tier`; `DeepTier.Merge` stamps each candidate |
| `src/Engram.Core/CodeAnalyzer.cs` | `CodeCandidate` gains `AnalyzerTier` |
| `src/Engram.Core/TreeSitter.cs` | every `DeepAnalysis` construction states tier 1 |
| `src/Engram.Core/RoslynSidecar.cs` | every `DeepAnalysis` construction states tier 2 |
| `src/Engram.Core/FactStore.cs` | `FactWrite` gains `AnalyzerTier`; `InsertFact` binds it |
| `src/Engram.Core/CodeIndexer.cs` | `ProcessFile` passes the tier in, and performs §4.3's observation write |
| `src/Engram.Core/CodeCallGraph.cs` | `CallerMatch`/`CalleeMatch` carry the stamped tier |
| `src/Engram.Cli/EngramMcpTools.cs` | retire `ExtractionTierUnrecordedHeader`; render per §5; add the telemetry field per §6 |

**Do not add** a per-file tier column to `file_state`, and do not add a tier to `repo_registry`.
§3.1 says why.

---

## 3. Where the stamp lives

### 3.1 On the candidate, never on the file or the run

A file's candidate list is **not** single-tier. `ProcessFile` computes tier-0 candidates with
`CodeAnalyzer.Analyze`, then merges deep results over them:

```csharp
var candidates = CodeAnalyzer.Analyze(filePath, content, language);
if (deep is not null)
{
    candidates = DeepTier.Merge(filePath, candidates, deep);
}
```

and `Merge` keeps tier 0's file-level `about` candidate while replacing everything else:

```csharp
var merged = tierZero
    .Where(c => c.EntityPath == fileEntityPath && c.Predicate == "about")
    .ToList();
```

So one file, in one run, yields candidates from two different tiers. **A per-file column could never
be correct**, and this is a stronger argument than the one about closed facts — it fails on the
first file indexed, not after a re-index.

The closed-fact argument holds too and is worth keeping: a fact closed at tier 1 must keep tier 1
after the file is re-indexed at tier 2, and only a per-fact stamp survives that.

### 3.2 `DeepAnalysis` must carry its own tier, with no default

`ProcessFile` receives a `DeepAnalysis` and **cannot currently tell whether it came from tier 1 or
tier 2** — `DeepAnalyses` (tier 2, `CodeIndexer.cs:385-429`) and `Tier1Analyses` (tier 1,
`CodeIndexer.cs:440-489`) both write into the same `Dictionary<string, DeepAnalysis>`. The tier is
genuinely unknowable at the write site today, and plumbing it is most of this phase's work.

```csharp
public sealed record DeepAnalysis(
    string Path,
    IReadOnlyList<DeepSymbol> Symbols,
    IReadOnlyList<string> Imports,
    string? Error,
    IReadOnlyList<DeepCall> Calls,
    int Tier);                    // 1 or 2 — REQUIRED, no default
```

**`Tier` takes no default value, deliberately.** A default is the one thing that can make this field
silently wrong: a construction site that forgot it would claim a tier it did not run. With no
default, every site is a compile error until it states the answer. That is the same reasoning D45
records for library-path configuration — do not let a fallback answer a question the caller is the
only one who knows.

**The friction this creates is the guard working.** It touches every `DeepAnalysis` construction in
`TreeSitter.cs` and `RoslynSidecar.cs`; adding a default to reduce that churn reintroduces exactly
the failure §7.1 exists to prevent.

Producers: `TreeSitter` constructs with `Tier: 1`, `RoslynSidecar` with `Tier: 2`. No other producer
exists; if one is added it must state its own tier.

### 3.3 `CodeCandidate` gains a tier that defaults to 0

```csharp
public sealed record CodeCandidate(
    string EntityPath,
    string Kind,
    string DisplayName,
    string Predicate,
    string Body,
    string? Object = null,
    int AnalyzerTier = 0);
```

**Here a default IS correct, and the asymmetry with §3.2 is the point.** Every candidate not built
from a `DeepAnalysis` was genuinely produced by tier-0 regex, so `0` is not a fallback standing in
for an unknown — it is the true answer at every existing construction site. `DeepTier.Merge` is the
only place deep candidates are built, so the sites that must state a non-zero value are all in one
method, which item 4 guards.

### 3.4 What `Merge` stamps

Three rules, all readable off `Merge`'s existing structure:

1. Candidates carried over from `tierZero` (the file-level `about`) keep `AnalyzerTier = 0`.
2. Candidates built from `analysis` — `declared-as`, symbol `about`, `imports`, `calls` — take
   `analysis.Tier`.
3. **The error path stamps tier 0 for the whole file.** `Merge` opens with
   `if (analysis.Error is not null) { return tierZero; }`, and that early return is exactly the
   "silently fell back" case §7.1 warns about. Returning tier-0 candidates unchanged already gives
   the right answer here — do not special-case it, and do not stamp the attempted tier.

Likewise `deep is null` (no sidecar located, sidecar did not answer, grammar absent, or a tier-0
language) yields tier-0 candidates only, correctly stamped, with no extra code.

### 3.5 `FactWrite` gains a **nullable** tier

```csharp
public sealed record FactWrite(
    ...,
    string? ObjectKind = null,
    int? AnalyzerTier = null);
```

Nullable and defaulted, so every existing call site is unchanged and correct: `remember`,
`user-prompt`, session facts and every other authored belief have **no analyzer tier at all**, and
that is a different statement from tier 0. `CodeIndexer.ProcessFile` is the only caller that passes
a value.

`InsertFact` binds it as `DBNull.Value` when null. No other write path changes.

**Reading NULL back is unambiguous when it needs to be**: a fact with `scope = 'code'` and
`regenerable = 1` and `analyzer_tier IS NULL` is a pre-v14 code fact; a non-code fact with NULL
simply has no such axis. Nothing renders a tier for a non-code fact, so the two never collide at a
surface, but a query that needs to separate them can.

---

## 4. Migration v14, and the observation write

### 4.1 The migration

```sql
ALTER TABLE fact ADD COLUMN analyzer_tier INTEGER;
```

Added to `Migrate()` as `if (from < 14) { ... WriteMeta(connection, null, "schema_version", "14"); }`,
matching the established shape. `docs/engram-schema.sql` gains the column in `CREATE TABLE fact`.

**No index on it**, and none should be added speculatively: nothing filters or joins on tier — it is
carried alongside rows already selected by other predicates, and §4.3's write targets a row already
identified by id. D60's lesson cuts the other way here (`ix_fact_thread` earned its place with a
measured 93%), and E18 checks the column costs nothing rather than assuming it.

**No `RebuildFactFts` call.** v13 needed one because it changed which rows the live-fact indexes
cover; this adds an unindexed nullable column and changes nothing FTS reads. Adding a rebuild would
put a measured 4,161 ms at 50,097 facts into an unattended on-open migration for no reason.

**No `NOT NULL`, no `DEFAULT`.** Both would be false: a pre-v14 fact genuinely has no answer, and
inventing one is D19's prohibition restated. It also keeps the ALTER metadata-only in SQLite rather
than a table rewrite (E19).

The migration populates nothing. Population is §4.3's job and happens on extraction, not on open.

### 4.2 The journal does not carry it

`backups/facts.jsonl` is unchanged: it does not gain an `analyzer_tier` field, and `backup replay`'s
identity remains subject + predicate + body + `valid_from`. **The tier plays no part in replay
identity**, exactly as `details` does not (D64).

So a `backup restore` or `replay` yields facts with `analyzer_tier IS NULL`, and the next extraction
that reproduces each body refills it under §4.3. That is the correct behaviour rather than a gap:
the journal exists to recover *authored truth* (D32), code facts are `regenerable = 1` and are
recoverable by re-indexing anyway, and adding a column to the journal format would change what
replays into older schema versions for a value that is re-observable for free.

### 4.3 RULED — `analyzer_tier` is derivation metadata, and the write is observation-licensed

Escalated in revision 1 and **ruled by the Ultra-Advisor at ~92% confidence**. The ruling, and this
spec's contract:

> `analyzer_tier` is **derivation metadata — observed provenance, not belief content.** Its meaning
> is *"the deepest tier ever observed to produce this exact body."* The write rule is
> **observation-licensed**: only an extraction run that has just reproduced the body identically may
> write it.

Three consequences, and the third is the one that matters most:

- **It licenses the insert and the fill alike.** Both are writes by a run that just observed the
  body. There is no special case for "new fact" versus "existing fact".
- **It structurally excludes `repair` and `compact`** — they recompute, they never observe — so §7's
  prohibition is not a rule bolted on beside the contract, it falls out of it. The prohibition is
  therefore narrower and sharper than revision 1 stated it: **"no unobserved write", not "no
  backfill."**
- **The classification is forced, not chosen.** Revision 1 rejected option (ii) — writing a new fact
  version when the tier differs but the body does not — because it pollutes D57's version counts.
  That rejection *entails* this classification: if the tier were belief content, a tier change would
  genuinely be a revision and (ii) would have been correct. Accepting the rejection and calling the
  field belief content are not consistent positions.

**Scoping call, left to me and now made: the write rule is MONOTONE UPGRADE, not NULL-only.**

```sql
UPDATE fact
   SET analyzer_tier = $tier
 WHERE id = $id
   AND (analyzer_tier IS NULL OR analyzer_tier < $tier);
```

The contract decides this rather than convenience. *"Deepest tier ever observed"* is not a property a
NULL-only fill can maintain: a fact stamped 0 before the grammar was installed, then re-observed at
tier 2, would keep saying `regex` forever while the contract claims otherwise. The Ultra-Advisor
flagged this as a live spec defect and it is a real one — **§5.4's prescribed fix, *install the
grammar and re-index*, is a no-op for every post-v14 stamped-0 fact under a NULL-only rule.** A
surface that prescribes a fix which cannot work is worse than one that says nothing, which is D37's
rule about `doctor` applied to this field.

**The predicate belongs in the `WHERE` clause, not in C#.** Reading the tier, comparing it, then
writing is a race with any concurrent indexer; the guarded UPDATE is atomic and cannot regress under
one.

**Downgrade is forbidden, and that is the protection, not a limitation.** `2 → 1` and `1 → 0` cannot
happen, so uninstalling the Roslyn sidecar or losing a grammar does not rewrite history to claim that
regex produced facts Roslyn produced. Which tooling is installed *right now* is a machine property;
Phase 3 §7.1 already ruled that machine properties must not be modelled per-file, and monotonicity is
how that ruling survives contact with this column.

**What may still never happen:** a write by anything that did not just observe the body — `repair`,
`compact`, a migration, a backfill script, or any future maintenance pass. §7 states this and item 13
guards it.

---

## 5. The query surface

### 5.1 Retire the placeholder

`ExtractionTierUnrecordedHeader` (`EngramMcpTools.cs:667`) and its four append sites (`:743`,
`:790`, `:836`, `:893`) go. Its comment already states the constraint that outlives it, and that
constraint now applies per row rather than per response:

> an absent field would read as tier 0

### 5.2 Three values, not two — wording ruled by Jim

| Stamp | Renders as | Means |
|---|---|---|
| `0` | `regex` | tier 0 pattern extraction — the honest floor |
| `1` | `syntactic` | tree-sitter |
| `2` | `semantic` | Roslyn |
| `NULL` | `not recorded` | written before v14, or restored from a journal and not yet re-observed |

**These four words are Jim's ruling, confirmed directly.** Words, not numbers: a bare `2` in a
navigation answer means nothing to the reader it is written for.

**`NULL` and `0` must render differently and must not sort or group together.** They are the same
character of error the retired header existed to prevent, and collapsing them would make the whole
phase a lie in the one direction that flatters it.

### 5.3 Uniform once, mixed per row

An answer's rows may carry different tiers. Follow the rule Phase 3 §7.2 and item 28 already set —
*a qualifier that always fires is noise*:

- **All rows share a tier** → say it once in the header (`extraction: semantic`).
- **Rows differ** → mark per row, and say nothing in the header.

Both halves are guarded (items 8 and 9), for the same reason item 28 needed both: a marker that
always appears carries no information.

### 5.4 This is a fourth independent report, and its prescribed fix now works

Phase 3 §7.2 holds three independent reports — coverage, name ambiguity, truncation — and
§5.3.2's attribution label as a fourth. Extraction tier is a **fifth**, and it does not fold into
any of them. Its fix is different again: not *index more files*, not *narrow the name*, not *raise
the limit*, but **install the grammar or the sidecar, then re-index**. A flag that cannot say which
fix applies is the flag nobody acts on (D37).

**That prescribed fix is only true because §4.3's rule is monotone upgrade.** Under a NULL-only rule
it would be a no-op for every fact already stamped `regex`, and this section would be prescribing
something that cannot work. The two decisions are coupled; do not weaken one without re-reading the
other.

In particular it is **not** a coverage state. Phase 3 §7.1's three states are about whether a file
was processed at all; tier is about how deeply. A file can be fully covered at tier 0.

### 5.5 The residue Phase 3 §7.1 names, now resolved

Phase 3 §7.1 records a machine-level residue: a file indexed while its grammar was missing
downgraded to tier 0, while the global version stamp says it was indexed. That residue **is what
this stamp resolves** — after Phase 4 the fact itself says `regex`, so the downgrade is visible per
fact rather than only in `TreeSitter.Downgrades` and `doctor`, and installing the grammar and
re-indexing upgrades it in place.

Phase 3 §7.1's instruction not to model that per-file stands. This does not model it per-file; it
records what actually ran, per fact, which is a different and better thing.

---

## 6. Telemetry — one field

The `navigate` record already carries `Tiers`, which is the **match** tier (how the name matched:
exact / case-insensitive / substring). Extraction tier is a different axis.

**Add a separate field. Do not overload `Tiers`.** Folding two axes into one field is exactly the
D43 failure — a nearby number standing in for the one you wanted — and D43 cost a wrong published
conclusion. Suggested `ExtractionTiers`, same comma-joined shape, listing the distinct extraction
tiers of the returned rows.

No counts in the event, no phases: it stays a single instant record (D55).

---

## 7. What must not change

- **`learned_via`'s `CHECK` stays closed.** Master §9, restated. Extraction tier is a separate axis
  and does not widen D19's enum.
- **`LanguageRegistry.Resolve(path).Tier` is never the source of a stamp.** It says what a language
  is entitled to, not what ran.
- **`CodePaths.GrammarVersion`** — not bumped. A bump forces a re-index of every store, and would
  not populate the column anyway (§1.2).
- **`CodeAnalyzer.AnalyzerVersion` stamping stays OUT of scope.** Master §7.1 raised it and left it
  *"open, deliberately — one unmeasured knob is a rule while two are a preference."* Nothing
  measured since has changed that. Do not add it because the migration is open anyway; that is how
  an unmeasured knob gets in.
- **No unobserved write of `analyzer_tier`.** The sharpest prohibition in this phase, and under
  §4.3's contract it is structural rather than stipulated: only a run that has just reproduced the
  body may write the column. `repair` and `compact` recompute rather than observe, so they are
  excluded by the contract itself — and they must be, because the tier is **not recomputable after
  the fact**: it depends on which tooling was installed at extraction time, which is unrecoverable
  later and may since have changed in either direction. A repair pass filling tiers from present-day
  tooling would manufacture provenance that was never true. Item 13 guards it.
- **Monotonicity.** Nothing may lower a recorded tier (§4.3).
- **`backups/facts.jsonl`'s shape and `backup replay`'s identity** — unchanged (§4.2).
- **`EngramDatabase.Open`** — no new ad-hoc connection opening.
- **`FactStore.ReadLive`'s default result set** — unchanged; the backup journal reads through it.
- **`file-touched` still never opens the database.** Nothing in this phase goes near that path.

---

## 8. NEEDS-EVIDENCE

Numbering continues the code-navigation series (Phase 3 ended at E16).

| # | Question | Why it decides something |
|---|---|---|
| **E17** | After v14 ships, how quickly does an actively-developed store converge? `SELECT analyzer_tier IS NULL, COUNT(*) FROM fact WHERE scope='code' AND valid_to IS NULL GROUP BY 1;` | Revision 1 framed this as deciding whether §4.3 was necessary; §4.3 is now ruled, so this is **no longer a gate** — it measures how well the shipped design works. Cannot be run until v14 has shipped and time has passed. |
| **E18** | Does adding the nullable column change any query plan or timing on the recall path? Paired `EXPLAIN QUERY PLAN` **and** timing, per D60 — a plan is not a clock. | E6 asked exactly this for Phase 1's indexes and found no cost; the method is established. A column with no index should be free, and "should be" is not measured. |
| **E19** | Is `ALTER TABLE fact ADD COLUMN analyzer_tier INTEGER` metadata-only at 50,097 facts, or a table rewrite? | It runs unattended on open (D31), behind a snapshot. Metadata-only is the expectation for a nullable column with no default; if it rewrites, the migration's cost needs stating before it ships. |

---

## 9. Acceptance

1. **The migration applies.** A v13 store opens, migrates to 14, and reports `schema_version = 14`.
   Falsify by leaving `SchemaVersion` at 13 — the guard must fail on the version, not just on the
   column's presence.
2. **A v13 store's existing facts survive with NULL.** Fact count, bodies, and validity are
   unchanged across the migration, and every pre-existing fact reads `analyzer_tier IS NULL`.
3. **The migration fixture is genuinely missing the column.** Per D60's lesson: a fixture that rolls
   a *current*-schema store back leaves `ADD COLUMN` no-opping and the test green against a broken
   migration. Build the fixture so the column is actually absent, and prove the test fails without
   the migration.
4. **`Merge` stamps by producer.** A file merged from a tier-2 analysis yields `calls`/`declared-as`
   candidates at tier 2 **and** its file-level `about` candidate at tier 0, in one result. Falsify by
   stamping the whole merged list uniformly — which is the mistake §3.1 exists to prevent, and it
   passes any test that only checks one candidate.
5. **The error path stamps tier 0.** A `DeepAnalysis` with `Error` set yields tier-0 candidates for
   the whole file, not the attempted tier.
6. **A missing deep tier stamps tier 0.** With no sidecar located, a `.cs` file's facts are written
   at tier 0 — not tier 2 from the registry. This is §7.1's central prohibition and the one that
   silently produces a false claim.
7. **The tier reaches the database.** After indexing a fixture repo, `calls` facts from a tree-sitter
   language read `analyzer_tier = 1` and a C# file's read `2`.
8. **Uniform answers say it once.** All-tier-2 callers render one header line and no per-row markers.
9. **Mixed answers mark per row and drop the header.** Both halves, same rule as item 28 — item 8
   alone passes with the header hardcoded.
10. **NULL never renders as tier 0.** A pre-v14 fact renders *not recorded*; a tier-0 fact renders
    *regex*; the two strings differ. Falsify by coalescing NULL to 0, which is the failure the
    retired header was written to prevent.
11. **The placeholder header is gone.** `ExtractionTierUnrecordedHeader` and its four append sites no
    longer exist, and no response contains its text.
12. **Telemetry carries extraction tier in its own field.** A `navigate` record has both the existing
    match-tier field and the new extraction-tier field, with different values where they differ.
    Falsify by folding them into one field — the D43 trap.
13. **`repair` and `compact` do not write the column, and the guard is proven able to fail.** Run
    each against a store holding NULL-tier and tier-0 code facts and assert every `analyzer_tier` is
    unchanged. **Then falsify it properly, per D60**: make `repair` write the column once, confirm
    this test reddens, and restore. A guard asserting that something never happens is worthless until
    it has been shown to catch the thing happening — CLAUDE.md's rule, and this is precisely the
    shape of guard that passes forever while protecting nothing.
14. **Non-code writes stay NULL.** An `engram_remember` fact has `analyzer_tier IS NULL`, not 0.
15. **The observation write fills, upgrades, and never downgrades — three assertions, all required.**
    - **Fills:** re-index a store whose code facts are NULL-tier, at a known tier, with bodies
      unchanged. Assert they become stamped, with **no new fact rows and no supersession**.
    - **Upgrades:** re-index the same store at a deeper tier, bodies still unchanged. Assert the
      recorded value moves up, again with no new fact rows.
    - **Never downgrades:** re-index at a shallower tier. Assert the recorded value does **not**
      move.

    **The third assertion is the load-bearing one** — the first two pass under an unrestricted
    `UPDATE`, which is not what §4.3 authorizes. Falsify by dropping the
    `analyzer_tier IS NULL OR analyzer_tier < $tier` predicate from the `WHERE` clause; exactly the
    third assertion must redden.

---

## 10. Confidence

**High** on §3 (where the stamp lives and how it is plumbed) — it is read directly off `Merge` and
`ProcessFile`, and the tier-mixing within one file makes the alternative untenable rather than merely
worse.

**High** on §1.1 and §1.2 being real corrections rather than readings. Both are checkable in one
command each.

**§4.3 is ruled, not mine** — Ultra-Advisor, ~92%. The scoping half (monotone upgrade rather than
NULL-only) is mine, and I hold it at high confidence because the contract decides it: *"deepest tier
ever observed"* is not a property NULL-only can maintain, and §5.4 breaks without it.

**§5.2's wording is ruled by Jim** and is no longer an open question.

**One documentary residue from Phase 3, noted here because it is mine.** I ruled a repo-scope guard
"binding as item 29" and deferred the spec retype; Phase 3 landed with all three guard tests present
(`CodeNavigationPhase3Tests.cs`) and `MatchingSymbolNames`' `repoNeedle` parameter removed, but the
Phase 3 spec's acceptance list still ends at 28. The code is correct and the spec understates what it
guards — the safe direction, but it should be folded in on that file's next revision.

---

## 11. Amendments owed to other documents

Both follow from §4.3 and neither lands with this spec. They are listed so the debt is visible
rather than remembered.

1. **`docs/code-navigation-spec.md` §7.1** — says `analyzer_tier` is *"written at insert and never
   updated, like every other piece of belief content."* Both halves are now wrong: it is not belief
   content, and it is written outside insert under an observation licence. **Mine to fix**, on that
   file's next revision.
2. **`CLAUDE.md`'s append-only clause** — *"Only `valid_to` and `superseded_by` are ever updated…
   `path` is the sole exception"* — now has a second exception of the same shape as `path`'s (D2):
   metadata that follows observation rather than belief content. **This one is Jim's call, not
   mine.** I do not amend `CLAUDE.md` on a peer's or a subagent's say-so, and a standing invariant
   in the user's own instructions is his to change. Surface it to him with §4.3's ruling attached.
