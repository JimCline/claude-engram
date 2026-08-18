# 04 — Lifecycle primitives

Status: design, revised. Parent: `docs/memory-expansion-spec.md` row 4.

## Goal

A review-due marker and a per-session pin, both outside `fact`'s authored columns. Confirm
how scope is actually represented today (see Design — this corrects an assumption in the
dispatching request).

## Non-goals

- No new scope values or a second scope representation.
- Pin does not persist beyond the MCP session that set it — a deliberate divergence from a
  durable pin seen elsewhere (see Design).
- Review-due does not affect recall ranking (only pin does, per the constraint).
- No per-type auto-decay of review dates (seen in a comparable tool; not adopted here, see
  Design).

## Inspiration

A comparable memory tool marks entries for later review with an expiry-style date that
resets based on the entry's kind, and lets an entry be pinned so it stays prioritized —
durably, as an ordinary mutable column, since its store already allows in-place updates.
Engram's version below diverges in both particulars, for reasons specific to Engram's own
append-only storage.

## Design — correction to the dispatching request

The request framed this spec as "confirm scopes = entity tree; do not add a scope column."
That framing is **incorrect as written**: `fact.scope` already exists as a real, non-null
column (`docs/engram-schema.sql`), and `docs/engram-path-grammar.md` documents it directly
under **D27**: five scope values are stored on each fact — the file names `user, project,
code, session` explicitly (a fifth, if any, was not confirmed). This is not the path tree by
itself; it is a first-class column *alongside* the path tree. Nothing needs adding — the
column already covers the concept of scope on a different, finer axis than a simpler
three-value `project|personal|global` split seen in a comparable tool:
- `user` roughly collapses what that simpler split treats as two separate values ("mine on
  this machine" vs. "mine everywhere") into one (Engram does not yet distinguish the two,
  which becomes relevant once spec 01's sync exists — see cross-spec note below).
- `project`/`code` split a single broader "project" bucket into stated-during-work facts vs.
  code-derived facts — finer-grained, and already Engram's own design.
- `session` is Engram-only: ephemeral, working-memory facts with no equivalent elsewhere.

**Recommendation to the Orchestrator**: update `docs/memory-expansion-spec.md` row 4's "What
we adopt" language to drop "confirm scopes = entity tree; do not add a scope column."
Replace with "confirm D27's existing `scope` column already covers this; no change." Filed
under Open questions below as well.

**Cross-spec note for 01**: once sync exists, `session`-scoped facts almost certainly should
never sync (ephemeral, single-connection working memory), while `user`-scoped facts are the
strongest candidate for syncing everywhere. This spec does not implement that filter — spec
01 as written syncs all facts unconditionally — but flags it as a follow-up worth a
NEEDS-EVIDENCE-adjacent product decision once both features exist.

**Review-due marker — explicit input kept, automatic per-type decay not adopted.** New side
table, keyed by fact:
```sql
CREATE TABLE fact_review (
  fact_id      INTEGER PRIMARY KEY REFERENCES fact(id),
  review_after INTEGER NOT NULL,
  set_at       INTEGER NOT NULL
);
```
No new MCP tool: `engram_remember` and `engram_revise` each gain an optional
`review_after` parameter (natural extension point — `Remember` already takes an optional
`supersedes` string; this follows the same shape), converted from a relative duration or
ISO date to a unix timestamp at the CLI/MCP boundary and written to `fact_review`. This
avoids adding tool count (interacts with spec 03's token-cost concern directly — no new
tool, two new optional parameters). A small CLI surface handles inspection/clearing:
`engram review list` (read-only), `engram review clear <id> [--apply]` (dry-run first, D49,
for consistency even though clearing a reminder is low-stakes).

A comparable tool instead auto-advances its review date by a per-*type* decay offset when an
entry is marked reviewed, with no explicit date required from the caller. Not adopted here:
it presupposes a fact-kind taxonomy with configured decay defaults, which Engram has no
analogue for (`learned_via ∈ stated|observed|inferred` is the nearest concept and does not
carry duration semantics). Building that taxonomy is scope beyond what this row of the
parent spec asked for. Flagged as a plausible v2 enhancement, not implemented now.

`fact_review` is honestly **not** derivable from `fact` alone (D8's narrow "regenerable from
`fact`" sense does not apply — nothing in a fact's body encodes a chosen reminder date). The
constraint permits "derived **or** side-table" state; this is side-table, not derived, and
this spec says so rather than mis-citing D8. It is backed up the same way spec 02's
`fact_relation` is: a third small journal (`review.jsonl`), written by the same
`BackupService` cycle, resolved on replay through the same `idMap` 4-tuple mechanism.

Surfacing: due-count is added to the existing `PrimerSummary` record (D46) that already
reports `long_term_fact_count` — reusing the same one-query read path, not a new hook or
query. `doctor` reports the due count as a `Warn`-level note (D37: a deferred review is a
choice, not a fault).

**Per-session pin — no schema at all, and a deliberate divergence from a durable global pin
seen elsewhere.** A comparable tool's own pin is a persistent, global boolean on its mutable
memory rows — which costs that design nothing conceptually, since its underlying table
already supports in-place updates. Engram's constraint scopes pin to "the session only"
instead, for a reason specific to Engram's own storage: `fact` is append-only (D8) — a
durable, freely re-toggleable pin cannot live on `fact` without becoming exactly the kind of
mutable, non-belief column D8 exists to keep off it, and modeling it as *yet another* side
table (alongside `fact_review` and spec 02's `fact_relation`) would triple up on "small
durable side-state table" for a feature the constraint already scoped down to ephemeral. A
per-session, in-memory pin is both what was asked and the right-sized answer given Engram's
storage model, not merely a smaller version of a feature seen elsewhere.

Mechanically: pin needs no database row, because it does not need to survive anything. It
lives entirely in server memory, keyed by `McpSessionId` (already threaded through every
`EngramMcpTools` call today), in a new small in-process class (`SessionPinStore`,
`ConcurrentDictionary<McpSessionId, HashSet<long>>`). Two new MCP tools,
`engram_pin(fact_id)` / `engram_unpin(fact_id)`, toggle membership. Nothing is persisted;
D8 is satisfied trivially — the state does not outlive the process, let alone need
regeneration.

Recall effect: a pin is a **ranking boost among already-matched candidates**, never a way to
inject a fact into results it would not otherwise match. A pinned fact that matches the
query lexically/semantically is guaranteed top position for that session; an unrelated
query with no lane match for the pinned fact returns nothing extra. This keeps D44/D60's
lane-driven relevance model intact — pin is a tie-break layered on top, not a second
relevance signal. Recall line marker: `· pinned`, following D57's `· v2` "advertise, don't
inline" pattern directly.

**Token-cost interaction**: `engram_pin`/`engram_unpin` are two new tools; recommend they
join spec 03's `default` profile (they are core recall-shaping, not lifecycle/setup — a
comparable tool's own non-admin tool tier includes an equivalent pin/unpin pair, supporting
the same call), but that membership decision belongs to spec 03.

## Invariants preserved

- **D8**: `fact_review` never touches `fact`'s authored columns; pin state is not persisted
  at all.
- **D27**: scope is confirmed as an existing column, not added.
- **D46**: due-count reuses `PrimerSummary.Read`, the same one-query pattern D46 already
  uses for `long_term_fact_count`, rather than a new primer query.
- **D57**: `· pinned` marker mirrors the exact `· v2` precedent.
- **D44**: pin never adds a fact to a result set a lane didn't already surface.

## Tests by tier (D9)

- **Tier 1**: pin ranking-boost logic as a pure function over a fabricated candidate list.
  Falsify: make the boost unconditional-inclusion instead of a tie-break, confirm a test
  asserting "an irrelevant query returns no pinned-but-unmatched fact" starts failing.
- **Tier 2**: `fact_review` due-count appears correctly in a `PrimerSummary` read;
  `review clear` requires `--apply`; two different `McpSessionId`s never see each other's
  pins. Falsify the last: replace the per-session dictionary with one global set, confirm a
  cross-session-leak test starts failing.
- **Tier 3**: end-to-end MCP `pin`/`unpin`/`recall` round trip against the published binary.

## Measurements

- Token cost of two new pin/unpin tool descriptions plus the `review_after` parameter
  additions to `remember`/`revise` (feeds spec 03).

## Open questions / NEEDS-EVIDENCE

1. **[measurement]** Token/byte delta above, same golden-file method as specs 02/03.
2. **Parent-spec correction (not evidence, action item)**: `docs/memory-expansion-spec.md`
   row 4 should be corrected per the Design section above — scope is an existing D27 column,
   not something to confirm-via-path-tree or avoid adding.
3. **Deferred product decision, not evidence**: whether `session`-scoped facts should be
   excluded from spec 01's sync by default. Not implemented in either spec as written;
   flagged for a follow-up decision once both exist. A second, unrelated deferred decision:
   whether Engram ever wants an automatic per-type decay review model like the one seen
   elsewhere, which would require defining decay defaults per some fact-kind taxonomy Engram
   does not currently have.
