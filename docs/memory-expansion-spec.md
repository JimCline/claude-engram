# Memory expansion — high-level spec

Status: design. Author: orchestrator, 2026-08-17.
Detailed specs: `docs/memory-expansion/*.md`, one per feature below.

## Context

A comparable memory tool for coding agents was reviewed for ideas — built on a mutable
observation log with git/cloud sync, a terminal UI, an agent-judged conflict table, and
lifecycle primitives (review cycles, pins, scopes). Its memory *model* is weaker than
Engram's own (mutable rows, keyword-only retrieval, no validity intervals, nothing
measured), and none of that is adopted. What it does better is the layer *around* the
store, and this document names the pieces worth bringing over.

## Standing constraints (not up for re-argument in the feature specs)

1. **Claude Code only.** Jim's decision, 2026-08-17: designing to how Claude Code works is a
   strength. Multi-harness support, seen in a comparable tool, is explicitly *not* adopted
   and no spec below may add abstraction "for a future harness".
2. **Facts stay append-only** (D8, project CLAUDE.md). Nothing adopted here may rewrite a
   fact body, predicate, validity window, or supersession row. The in-place upsert model
   used by the tool that inspired these ideas is exactly what we are *not* copying.
3. **The measured budgets hold.** `file-touched` ≤ 10 ms and never opens the database (D4);
   hooks that open the store are a decision with a number. A feature that puts work on a hook
   path must carry its own measurement.
4. **Derived state is repairable, authored truth is not.** Anything new that can be
   regenerated (sync chunk state, verdict caches, export files) is derived and must be
   rebuildable from `fact`.
5. **Destructive verbs dry-run first** (D49). Any new verb that removes or rewrites follows
   suit.
6. **One implementation per behaviour.** Where the inspiration overlaps a feature Engram
   already half-has (journal replay, `Tui.Render`, `backups/facts.jsonl`), the adoption
   extends that implementation rather than adding a second one beside it.

## Features, in priority order

| # | Feature | Comparable idea seen elsewhere | What we adopt | Spec |
|---|---------|-------------|---------------|------|
| 1 | **Cross-machine sync** | Git-carried chunked export/import, merge-free by construction; an optional hosted sync tier | A git-carried, additive replication of authored truth built on the existing `facts.jsonl` journal and `backup replay`, which is already idempotent and conflict-counting. No cloud tier. | `memory-expansion/01-sync-spec.md` |
| 2 | **Conflict verdicts on remember** | Saving a memory can return related-candidate matches; a separate call records a verdict (conflicts_with / supersedes / scoped / not_conflict) about the relationship, kept apart from the memory rows themselves | `engram_remember` returns the live fact it would supersede (or near-neighbours) as candidates; a new `engram_judge` records a verdict row that *never* alters the facts themselves; recall and `expand history` can show why a belief closed. | `memory-expansion/02-conflict-verdicts-spec.md` |
| 3 | **Tool profiles** | A launch-time flag selects which tool set the MCP server exposes | Two profiles, chosen by `[mcp] tool_profile` in config: `default` (recall/remember/forget/revise/expand/browse) and `full` (+ `start/status/stop/index_repo`). Deliberately not a wider agent/admin split — Engram's 10-tool surface doesn't need it. Token cost of tool definitions becomes a measured line item. | `memory-expansion/03-tool-profiles-spec.md` |
| 4 | **Lifecycle primitives** | A review-due marker, a persistent pin, and named scopes (project/personal/global) | A review-due marker (side table, optional `review_after` on remember/revise) and a per-session in-memory pin, never on the fact row's authored columns. Scopes need no change: `fact.scope` already exists (D27: user/project/code/session). Automatic per-type decay and a durable global pin, both seen elsewhere, are explicitly not adopted. | `memory-expansion/04-lifecycle-spec.md` |
| 5 | **Browse & timeline surface** | An interactive terminal browser and a chronological timeline view keyed to one entry | `engram timeline <fact-id>` and an interactive browse over the entity tree using the existing `Tui.Render` (D52 row budget), read-only. | `memory-expansion/05-browse-tui-spec.md` |
| 6 | ~~**Export to notes**~~ | ~~A JSON export, plus a beta Obsidian-vault export~~ | **Scratched, 2026-08-20 — Jim's call.** Not adopted: `engram export --obsidian <dir>` will not be built. | ~~`memory-expansion/06-export-spec.md`~~ |
| 7 | **Standing directives** | — (Jim's own request, 2026-08-19, not from the comparable-tool review) | A user-authored tier of standing instruction, on par with CLAUDE.md: unconditional, undroppable delivery in the primer at session-start/subagent-start, CLI-authored only (`engram directive add "<text>"`, no promotion path from passive capture — D-10), excluded from ranked recall, enumerated via `engram_browse`. | `memory-expansion/07-directives-spec.md` |

## Backlog (not scheduled)

- **Passive-capture precision for `requires`.** E-6's census for spec 07 (2026-08-20) found 152
  live facts with predicate `requires` (the existing `UserStatementClassifier` "always/never/from
  now on/remember that" capture) across ~14 projects, of which only ~5–15 read as genuine standing
  behavioral rules — the remaining ~92% (140 of 152) are ordinary technical prose (defect reports,
  review comments, spec decisions) that happened to contain a trigger phrase. This is what made the
  spec-07 promotion surface (`directive add --from <fact-id>`) not worth building (D-10): a
  152-candidate list can't help fill an 8-slot directive budget the user already knows by heart.
  The classifier itself is still live and still misfiring at that rate on every qualifying
  statement — nobody has scoped a fix. Not spec'd, not estimated, no owner yet.

## Explicitly not adopted

- Multi-harness adapters (constraint 1).
- In-place upsert / row-mutation-based revision tracking (constraint 2).
- Cloud/Postgres backend — sync is git-carried and local-authoritative; a hosted tier is a
  different product.
- LLM-judged semantic conflict pass that spawns an agent CLI per verdict — the verdict is the
  model's, recorded through the MCP tool it is already holding.
- Raw prompt storage — Engram's `user-prompt` hook extracts facts; storing the prompt
  verbatim would duplicate what the transcript already holds.
- Post-compaction re-prime — already ours (D51, `SessionStart` matches `compact`).
- Exact-dedupe rolling window — `FactStore.Append` already returns the existing id for a live
  match.

## Ordering rationale

Sync first because it is the only item that changes what Engram *is* (one machine → the
person's machines) and the journal it needs already exists. Conflict verdicts second because
they close the one place the comparable memory model above is honestly ahead. Profiles third
because the cost is paid on every session and the fix is small. The rest are surface.

## What each detailed spec must contain

Every `memory-expansion/*.md` follows the house shape: goal, non-goals, a short Inspiration
note (generic, no identifying detail about the outside system), the Engram design (schema
delta against `docs/engram-schema.sql`, CLI/MCP surface, hook impact), invariants it must not
break (cite CLAUDE.md paragraph and D-number), tests per tier (D9), what is measured and how,
and open questions marked NEEDS-EVIDENCE for the orchestrator to route.
