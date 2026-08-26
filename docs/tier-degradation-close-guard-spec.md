# A degraded analysis tier may not delete

**Status:** design, revision 1. Written by the Architect.

**Severity: destructive on real data.** This is not a quality defect. A single degraded index run
over a populated store closes the entire nested-member code graph for every affected file, and the
operator is told nothing.

**Provenance.** Found by the Ultra-Advisor while diagnosing the `code-graph-all-members` regression
(brief `20260825-165711-1d5z`). It is **not** caused by that effort — confirmed byte-identical at
`ee40a96` (pre-effort), `c5b78db`, and `37053a9`. The widening work only made it visible, by
prompting the first measurement that compared member counts across runs.

**Separate spec, deliberately.** `docs/code-graph-all-members-spec.md` is about which members an
extractor observes. This is about what an incomplete observation entitles the indexer to delete.
Folding a destructive-data defect into a feature spec would couple their review and bury it.

---

## 1. The defect

Three correct-in-isolation decisions compose into data loss.

1. **`RoslynSidecar.Locate` returns null silently**, by design and correctly: *"a missing sidecar, a
   missing runtime, a timeout, a crash mid-batch, or one unparseable file each cost exactly the deep
   analysis and nothing else, silently, because an optional tier that can fail an index run is not
   optional."* For "this machine has no sidecar, ever," silence is right.
2. **`CodeIndexer.DeepAnalyses` returns an empty map on that path**, so every C# file in the run
   falls back to tier-0 regex extraction — top-level types only, no members.
3. **`ProcessFile` closes every live regenerable fact it did not re-match this run**
   (`CodeIndexer.cs:577–582`, `624–627`). Members were not re-matched, because nothing looked for
   them. So they are closed as deletions.

The result: a tier-0-degraded `--full` over a store built at tier 2 retires the whole member graph.
Observed as `3326 → 591` `declared-as` facts. Nothing in the run's output says a tier was missed for
that reason.

### 1.1 Who is exposed

**Not** an installed instance. `scripts/install.sh` publishes the sidecar to `<prefix>/roslyn/`,
which `Locate` finds, and when it *cannot* install it the script already says so out loud
(`install.sh:1227`: *"Tier-2 C# analysis: not installed … C# indexes at tier 0"*).

Exposed is **any tree built without that step**: a plain `dotnet build`/`dotnet publish` into
`out/`, CI, a fresh clone before `install.sh` runs, and the git-worktree builds used for this
effort's own measurement arms. Those produce an `engram` binary that indexes C# at tier 0 and says
nothing.

### 1.2 This is D53's lesson in a second subsystem

D53 established: *"a truncated scan reads as a repository whose files were all removed"*, and
therefore *"nothing may treat a partial scan as complete — the indexer skips deletions and says
so."* The remedy is already built and live at `CodeIndexer.cs:213–217`:

```csharp
if (scan.Truncated)
{
    notes.Add($"{scan.Summary()}; skipped deletions, because a partial scan cannot show a file is gone");
    suppressedReason = options.Apply ? "truncated" : null;
}
```

The identical failure is here one level down: **a degraded tier reads as members that were all
deleted.** D53 bounded *which files the run saw*; this bounds *how deeply the run saw each file*.
The same principle, a different axis of incompleteness.

`CodeIndexer.cs:64–66` already states the neighbouring half of the rule — *"tier-0 extraction does
not outrank testimony."* This spec adds the sibling: **tier-0 extraction does not outrank tier-2
extraction either.**

### 1.3 The defect is not C#-specific, and the brief understates it

`Tier1Analyses` (`CodeIndexer.cs:440–489`) has the same shape: files whose language declares tier 1
go through tree-sitter, and when the grammars are unavailable they fall back the same way. A TS/JS
file indexed without grammars degrades to tier 0 and its member facts are closed by the identical
`ProcessFile` path.

**Fixing only the tier-2 path ships the same defect for tier 1.** Everything below is specified
tier-agnostically for that reason.

---

## 2. Decision (a) — the close guard

**Ruled: the per-file blanket rule. Not as an interim — as the design.** The brief offers an
`analyzer_tier`-keyed variant as the precise fix and the blanket rule as a simpler stopgap. That
ordering is inverted, and the reason is measurable from the schema rather than a matter of taste.

### 2.1 The rule

> A run that could not perform a file's **declared** tier has made no observation of that file, and
> derives **no deletions** from it.

Per **file**, not per run: a run may analyze most files at their declared tier and degrade on some,
and only the degraded ones lose deletion authority.

Concretely, in `ProcessFile`: if `LanguageRegistry.Resolve(relativePath).Tier` is `N > 0` and this
run produced no tier-`N` analysis for that file, **skip the close set entirely for that file** —
`closes` is computed but not applied, and the skipped count is reported (§4).

**Writes are unaffected.** The degraded run still writes what it found. That is safe: tier-0 bodies
are a subset of tier-2's for the symbols both can see, and `FactStore.UpgradeAnalyzerTier`'s
predicate (`analyzer_tier IS NULL OR analyzer_tier < $tier`, `FactStore.cs:212–213`) is monotone, so
a shallow run cannot downgrade a stamp. **Only deletion is forbidden**, because only deletion
requires a complete observation.

### 2.2 Why `analyzer_tier` is the wrong key — two independent reasons

**First: the column is monotone, so it converges to the blanket rule anyway, and diverges only
where it is wrong.**

`analyzer_tier` records *the deepest tier ever observed to reproduce a fact's exact body*, and it
only ever rises. So after a single healthy tier-2 run, essentially every C# fact tier 2 can see
carries `analyzer_tier = 2` — **including the top-level types tier 0 also produces**, because the
tier-2 run re-observed them and upgraded the stamp. A predicate skipping closes for
`analyzer_tier > ranTier` therefore skips nearly everything on any store that has had one healthy
run: the blanket rule, reached by more code.

Where it *differs* is the dangerous direction. The facts still carrying `0` or `NULL` are precisely
those never re-observed at tier 2 — the oldest facts, and those written before the column existed
(`EngramDatabase.cs:496` adds it by migration, so pre-migration rows are `NULL`). A naive
`> ranTier` test does not spare `NULL` at all. So the "precise" rule closes exactly the population
most at risk and spares the population least at risk. Making it safe requires
`AnalyzerTier is null or > ranTier`, at which point it is the blanket rule with extra steps.

**Second, and the reason that would hold even if the first did not: provenance is not deletion
authority.** `analyzer_tier` answers *which extractor has been seen to produce this body*. The close
decision needs *was this run's look at this file complete*. Those are different questions. A fact
legitimately stamped `0` may still be a fact a tier-0 run cannot vouch for the absence of, because
absence is evidence only from a complete observation. CLAUDE.md's own ruling on that column — it is
derivation metadata, annotation rather than selection — argues against promoting it into a deletion
predicate, not for it.

**Consequence to accept, explicitly:** on a machine with no sidecar, C# files never have deletions
applied. Symbols genuinely removed from such a repo keep live facts until a healthy run happens.
**That is the correct trade** — a stale fact is recoverable by the next good run; a closed graph is
recovered only from a backup, and D32's journal exists because that is the expensive direction.

### 2.3 Where the data already is

`CodeIndexer.cs:773` and `:782` already carry `analyzer_tier` into the per-file `live` map, so the
precise variant would be cheap to write. **Cheapness is not the argument** — §2.2 is — and this is
recorded only so nobody re-proposes it believing the data is missing.

---

## 3. Decision (b) — the note

**The note infrastructure already exists and is one path short.** `DeepAnalyses` already emits:

- `CodeIndexer.cs:423` — `"deep analyzer did not answer; {deep.Count} file(s) took tier 0"`
- `CodeIndexer.cs:427` — `"tier 2: deep analyzer covered {results.Count} of {deep.Count} file(s)"`

Line 423 covers *the sidecar ran and failed*. The gap is narrower than the brief states: only the
**`SidecarPath is null`** path returns early with no note at all. Same for the tier-1 equivalent
when grammars are unavailable.

**Required:** when a run contains ≥1 file whose declared tier could not be performed, emit **one
note per run per tier** — not per file; 461 identical lines is noise, not signal.

**The note must name the consequence, not just the cause.** D54's rule — *the reason a number is not
moving is the answer*, and a service that declines records why. `"sidecar not found"` sends the
reader to install something. What they need to know is that this run did not delete:

> `tier 2: no deep analyzer available; 461 file(s) took tier 0 — skipped deletions for them, because a shallower tier cannot show a symbol is gone`

Word it as a sibling of `:216`'s truncation note, which is the same sentence about the other axis.
`Locate`'s own silent-null contract is **unchanged** — it is correct, and the note belongs to the
run, not to the lookup.

---

## 4. Reporting

`IndexResult.FactsClosed` (`CodeIndexer.cs:49`) must not silently under-report. Add a companion
count of closes **skipped** by this guard, surfaced the way the truncation path surfaces
`suppressedReason` (`CodeIndexer.cs:217`). A run that skipped 2,700 deletions and reports
`FactsClosed: 0` looks identical to a run with nothing to delete, which is the ambiguity this whole
spec exists to remove.

Whether that rides the existing `suppressedReason` stamp or a new field is the Implementor's call
**provided** the answer is visible in `index` output. If the stamp is reused, the reason string must
distinguish tier degradation from scan truncation — two different repairs.

---

## 5. Decision (c) — the tier-3 guard

The current suite cannot catch "sidecar missing from the publish," which is exactly how this
shipped. `RoslynSidecarTests` drive the sidecar directly, so they never exercise the absent case.

**Guard 1 — the load-bearing one, and it is about data, not messages.** Index a C#-bearing fixture
with the sidecar available so member facts land at tier 2. Then re-index the **same unchanged tree**
with the sidecar unavailable (`ENGRAM_ROSLYN_SIDECAR` pointed at a nonexistent path — `Locate`
returns null for a broken override by design, which makes this reachable without moving files).
**Assert the member facts are still live.** **Falsify:** remove the §2.1 skip — this must redden.

Do not let this be replaced by a test that the note was printed. A printed note with the facts
closed anyway is the defect.

**Guard 2 — coverage, as a disjunction.** In `TierThreeCoverageTests`, index a C#-bearing fixture
through `out/engram` and assert **either** the run reports tier-2 coverage **or** it emits the §3
note. Ultra-Advisor's phrasing — *"either covers tier 2 or says it didn't"* — is exact and must not
be tightened.

**Why the disjunction rather than asserting tier 2 ran:** a plain `dotnet publish` tree is a
legitimate configuration, and this repo already tried making tier 3 fail on an unpublished tree and
**reverted it** — *"a check people learn to route around is worth less than no check."* Asserting
tier-2 coverage outright would red every inner-loop run for anyone who has not run `install.sh`, and
would be routed around within a week. The disjunction cannot be satisfied by silence, which is the
only thing that was actually wrong.

**Skip-count discipline applies.** Every `Engram.EndToEnd.Tests` test skips without a binary, and
the summary still reads `Passed!`. The Implementor must report the **skip count** for guards 1–2,
not the pass count.

---

## 6. Out of scope, flagged not folded

**The root cause is that `out/engram` is a tree that cannot do its job.** This spec makes
degradation safe and legible; it does not make a plain publish correct. The smaller fix may be to
have the build place the sidecar where `Locate` already looks (`roslyn/` beside the executable), so
`out/` matches an install.

That is a build-system change, Ultra-Advisor deliberately scoped this dispatch to close semantics,
and the two should not land together — the close guard is right **regardless** of how the sidecar is
shipped, and it must not be reviewed as if the build fix made it unnecessary. It does not: a
timeout, a crash mid-batch, and a machine without the .NET runtime all reach the same degraded path
with the sidecar present.

**Recommend raising it as its own item after this lands.** Not decided here.

---

## 7. Acceptance

1. A C# file whose declared tier could not run this pass has **no** facts closed. **Falsify:**
   remove the skip — must redden.
2. The same for a **TS/JS** file when tier 1 could not run (§1.3). **Falsify:** apply the skip to
   tier 2 only — this must redden while item 1 stays green. Load-bearing: it is what stops the fix
   landing for half the defect.
3. A file whose declared tier **did** run still has stale facts closed normally. Guards against a
   fix that disables deletion outright.
4. A tier-0 language (markdown, text) is unaffected — `LanguageRegistry.Resolve(...).Tier == 0`
   never degrades, so its deletions still apply. Guards against the guard swallowing everything.
5. A degraded run still **writes** the facts it found (§2.1), and does not lower any
   `analyzer_tier`. **Falsify:** make the guard skip writes too — must redden.
6. Exactly **one** note per run per degraded tier, naming the file count and stating deletions were
   skipped. Not one per file.
7. `Locate` still returns null silently for a missing sidecar and for a broken explicit override —
   contract unchanged (§3).
8. The run reports how many closes were skipped; a degraded run is distinguishable from a run with
   nothing to delete (§4).
9. Guard 1 of §5 — facts survive a degraded re-index of an unchanged tree.
10. Guard 2 of §5 — the tier-3 disjunction.
11. `--dry-run` reports the same skip and the same note as `--apply`, so the brake shows what the
    apply would do (CLAUDE.md's dry-run-first rule).

**Report the skip count for items 9–10 explicitly.**

---

## 8. Confidence, and what I did not settle

- **HIGH** that the blanket rule is correct and that `analyzer_tier` is the wrong key (§2.2). The
  monotone-stamp argument is read off `FactStore.cs:212–213` and the migration at
  `EngramDatabase.cs:496`, not estimated.
- **HIGH** that tier 1 has the same defect (§1.3) — same `ProcessFile` close path, same fallback
  shape. **Not** empirically reproduced; it is read from the code. If the Implementor finds tier 1
  already guarded, item 2 becomes a no-op and that is a fine outcome, but it must be checked rather
  than assumed either way.
- **MODERATE** on §4's mechanism — I specify that the skip must be *visible*, and leave whether it
  reuses `suppressedReason` to the Implementor, because I could not read enough of that plumbing to
  rule without guessing.
- **Not settled, and not mine:** whether `out/` should ship the sidecar (§6). That is a build and
  packaging decision with its own blast radius.
