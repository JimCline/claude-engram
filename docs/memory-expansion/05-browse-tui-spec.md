# 05 — Browse & timeline surface

Status: design, revised. Parent: `docs/memory-expansion-spec.md` row 5.

## Amendment note

The first draft of this spec designed `engram timeline <path>` as a full chronological
history for one entity's facts. Closer review of a comparable tool's own timeline feature
showed that is the wrong shape: its equivalent centers on **one entry** and shows
before/after neighbours **in time, across the whole store**, not one entity's own thread.
Engram already has the "one entity's own thread" feature — that is what
`engram_expand ... history` does (D57's `· v2` marker exists precisely to advertise it).
Keeping the original design would have shipped a second implementation of `expand`'s job
under a different name, which is exactly what "one implementation per behaviour" forbids.
`timeline` is redesigned below to be a temporal-neighbour view instead, which is genuinely
new capability, not a duplicate.

## Goal

`engram timeline <fact-id> [--before N] [--after N]` (a chronological window of facts
around one fact, irrespective of subject — "what else was I recording around this time")
and an interactive `engram browse` (entity-tree navigation) — both read-only, both built on
`Tui.Render` (D52) and `MomentText` (second-resolution, local zone), neither introducing a
second query path where `engram_browse`/`FactStore`/`engram_expand` already answer.

## Non-goals

- No write path anywhere in this spec.
- No new rendering engine — `Tui.Render` is reused exactly as the existing model-menu uses
  it, including its row-budget contract. Explicitly not adopting the terminal-UI framework
  used by a comparable tool (see Inspiration) — a second TUI engine alongside `Tui.Render`
  would itself violate "one implementation."
- No per-entity full-history view under the `timeline` name — that already exists as
  `engram_expand ... history` (D57); `timeline` does not duplicate it.
- No new date formatter — reuses `MomentText` from the core library rather than a
  browse-local one.

## Inspiration

A comparable memory tool ships an interactive terminal browser built on a much larger,
general-purpose TUI framework spanning many unrelated screens, plus a timeline view centered
on one entry that shows its neighbours in time across the whole store. Engram adopts the
underlying idea — a temporal-neighbour view, and an interactive browser — while reusing
Engram's own much smaller, purpose-built rendering layer rather than a second UI engine.

## Design

**What already exists.** `engram_browse` is a live MCP tool
(`Browse(home, session, homeState, path, depth)`). Whatever query it calls internally to
list children/fact-counts under a path is the one and only browse query — this spec adds no
second implementation of it. `engram_expand ... history` already answers "this entity's full
fact history" (D57). Nothing in the CLI verb list retrieved for this project (`home, init,
serve, start, stop, restart, status, doctor, hook, probe, permissions, model, embed, scan,
index, explain, backup, repo, queue, repair, compact, export, import`) currently covers
interactive browsing or a temporal-neighbour view — both `browse` and `timeline` are
genuinely new CLI verbs, but neither adds a query `expand`/`browse` already provide.

**`engram timeline <fact-id> [--before N] [--after N]`** — new, read-only CLI verb, designed
around a temporal-neighbour view rather than a per-entity history. Given a fact id, finds its
position in the store's global chronological order (by `valid_from`, falling back to
`created_at` for ties) and returns up to `N` facts immediately before and `N` immediately
after it — **across all subjects**, not scoped to the target's own entity. Default `N` is
5/5, matching a sensible default window seen in a comparable feature elsewhere. This is a
plain windowed query over `fact` (`ORDER BY valid_from LIMIT ... OFFSET ...` centered on the
target row's rank, or an equivalent two-sided `LIMIT`), not a per-path grouping —
structurally distinct from `VersionCounts`/`expand history`'s subject-scoped query, which is
exactly what makes it non-duplicative: it answers "what was happening around this fact"
(session/working-context reconstruction), a question `expand`'s subject-scoped thread cannot
answer. Each row renders via `MomentText.Local` (or an explicit `TimeZoneInfo` per its
existing signature) at second resolution — the same formatter spec 06 also reuses, not a
second one. No schema delta: this is a read over existing `fact`/`entity` rows.

**Interactive `engram browse`** — new CLI verb, a `Tui.Render`-driven path-tree navigator.
Starts at the root, lists child path segments (from the entity tree already backing
`engram_browse`), lets the user descend, shows the live-fact count per node (the same count
`engram_browse` already computes), and a keybinding jumps the current node's selected fact
into the new `timeline` view (temporal neighbours) or the existing `expand ... history` view
(this entity's own thread) — both are useful, distinct next steps from a browse selection,
and browse should expose both rather than picking one. Must obey D52's invariants exactly:
clip every line to width before composing, give the detail block a fixed height, and return
the caller the actual row count written — the same contract `Tui.Render`'s existing
model-menu caller already satisfies. No second renderer, no second row-accounting scheme.

**`engram_browse` MCP tool**: unchanged. This spec adds CLI surface around the existing
tool and its underlying query, not a new tool.

**Telemetry**: `TelemetryEventKind.Browse` already exists in the enum (presumably emitted
by the existing `engram_browse` MCP tool today — not newly added here). Add
`TelemetryEventKind.Timeline = "timeline"` for the new CLI verb — a single completion event
(no started/finished/failed phases; timeline is an instant read, not a background job, same
shape as `recall`'s own telemetry).

## Invariants preserved

- **D52**: `Tui.Render`'s row-budget contract (clip to width, fixed detail height, return
  actual rows written) reused exactly, not reimplemented.
- **MomentText's local-zone, second-resolution rule**: reused directly by both new verbs.
- **"No new query implementations if `engram_browse`/`FactStore`/`expand` already
  answers"**: browse reuses the existing tool's query; the redesigned timeline is a
  genuinely different (cross-subject, temporal-window) query rather than a re-derivation of
  `expand ... history`'s (subject-scoped, full-thread) one.
- **D8/D37 spirit extended**: both verbs are read-only; neither writes to `fact` or repairs
  anything.

## Tests by tier (D9)

- **Tier 1**: timeline windowing (correct `N`-before/`N`-after selection, ties broken by
  `created_at`) on a canned fact list spanning several subjects. Falsify: scope the query to
  the target's own subject instead of the whole table, confirm a test asserting "neighbours
  from a different subject appear in the window" starts failing — this is the specific
  regression that would silently turn `timeline` back into a duplicate of `expand history`.
- **Tier 2**: the exact D52 pitfall this codebase has already hit once — a test that builds
  its own `TuiChoice` list proves nothing about the real picker. Draw browse's *actual*
  `TuiChoice` list (not a hand-built stub) and assert the returned row count matches what
  was written, mirroring `ModelMenu_SpecsFitBesideTheLabel_WithoutBeingEllipsed`. Falsify:
  replace the real list with a hand-built one in the test and confirm it would pass with a
  reintroduced clipping defect — proving the test needs the real data source, exactly the
  documented D52 trap.
- **Tier 3**: end-to-end CLI `browse`/`timeline` against the published binary, including a
  narrow-terminal-width scenario.

## Measurements

None. Neither verb adds an MCP tool schema or touches a hook path — no token-cost or
hook-budget measurement applies to this spec.

## Open questions / NEEDS-EVIDENCE

1. **[verify, non-empirical]** Confirm the exact name/signature of `engram_browse`'s
   internal query method (`FactStore` or wherever it lives) so the CLI verbs call it
   directly rather than re-deriving the same logic by inference from this spec.
2. **[product decision, not evidence]** Whether `timeline`'s default window (`--before 5
   --after 5`) is the right default for Engram's own usage patterns, or should differ — no
   data collected either way yet.
