# Engram vs. Graphify — gap-closure tracker

Source of the gap analysis: `docs/engram-vs-graphify.md` (2026-08-05). That doc concluded
Graphify is not a competitor to Engram overall — it's a competitor to Engram's M3 (code
graph), and evidence M3 should stay narrow rather than grow. It named three design ideas
to import (D19, D21, D22) plus two minor items. This doc tracks closure of those five,
kept current as work lands — `docs/engram-vs-graphify.md` itself stays as the original
analysis and is not edited further.

## Closed

- [x] **D19 — typed provenance.** Every fact carries `learned_via` (`stated` / `observed`
  / `inferred`), a closed CHECK constraint on `fact` (`docs/engram-schema.sql:127`). Wired
  end-to-end: written by every fact-producing call site (`CannedFactSeeder.cs`,
  `CodeIndexer.cs`, `DirectiveFacts.cs`, `SessionFacts.cs`), surfaced to the model in
  `EngramMcpTools.cs`. Shipped ahead of the code-navigation work, not part of Phase 3/4.

  Not to be confused with Phase 4's `analyzer_tier` (regex/syntactic/semantic extraction
  *depth*, scoped only to code facts) — the two are explicitly orthogonal per
  `docs/code-navigation-phase4-spec.md`, and `analyzer_tier` does not extend or subsume
  D19's `learned_via` axis.

- [x] **D21 — explainable retrieval.** `engram explain <query>` ships
  (`RetrievalExplainer.cs`, `ExplainCommand.cs`, wired in `CliApp.cs`). Confirmed shipped
  in `docs/engram-implementation-plan.md` ("shipped, and it found the ranker blind to
  ..."). Explains why one fact outranked another post-RRF-fusion, per D21's original ask.

- [x] **Code navigation Phase 3** — call graph (`defined_at`/`imports`/`callers`/`callees`),
  landed `693db7b`, pushed to `origin/code-graph`.

- [x] **Code navigation Phase 4** — trust surface and measurement (`analyzer_tier` stamped
  at write time, `navigate` telemetry field), landed `e6bed64`, pushed to
  `origin/code-graph`.

- [x] **D22 — a readable report of everything Engram knows.** `engram report` ships
  (`MemoryReport.cs`, `ReportCommand.cs`, wired in `CliApp.cs`), per
  `docs/engram-d22-report-spec.md` (rev 3). Unlike `browse`, it includes closed and
  superseded facts (marked as such, including forget/revision reasons), does not
  truncate, and emits a Markdown artifact. Reviewed and passed. Pin state is
  deliberately excluded (§5.6.1) — it's ephemeral, per-MCP-session, and has no
  coherent cross-process reading; the report's audit surface does not and cannot
  cover it.

## Open

- [ ] **BENCHMARKS.md.** Doesn't exist at the repo root. Low effort per the original
  analysis ("Engram's culture already demands numbers over estimates ... promoting them
  to a maintained top-level document is nearly free") — mostly a matter of actually doing
  it, not a design question.

- [ ] **Multi-assistant reach.** Still Claude-Code-only. The original analysis treated
  this as "correct for now," not an active gap to close — kept here for visibility, not
  as a committed roadmap item. No work should start on this without an explicit decision
  to widen scope.

## How to use this doc

Check an item off only once it's landed (committed + pushed), same bar as the code-nav
phases above. When a new item closes, add its evidence line the way D19/D21 are recorded
here — file:line or a doc quote, not just a checkbox.
