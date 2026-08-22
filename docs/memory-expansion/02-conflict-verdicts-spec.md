# 02 — Conflict verdicts on remember

Status: design, revised (amended 2026-08-18 — candidates redesigned against the real write
path). Parent: `docs/memory-expansion-spec.md` row 2.

## Amendment note

The first draft of this spec designed same-slot candidate detection on `engram_remember`
around a `(subject, predicate)` collision that the tool's real write path cannot produce.
engram-implementor built everything else in this spec (`fact_relation`, `engram_judge`,
expand-history, the `· judged` marker, `relations.jsonl`) and correctly stopped on this one
piece rather than inventing a resolution scheme, reporting: `engram_remember`'s actual write
path is `SessionFacts.Append` (`src/Engram.Core/SessionFacts.cs:86-152`), whose subject path
is `PathFor(sessionId, agent, statement) = prefix + "/" + FactStore.Fingerprint(statement)`
(`SessionFacts.cs:70-74`) — a hash of the statement's own text. Two different statements can
never collide on `(subjectPath, predicate)` at that call site: the collision this spec's
"same-slot" design depended on is structurally impossible for a fresh `engram_remember` call.
The one case it *could* apply to — a byte-identical restatement — already collides on the
fingerprinted path and is silently deduplicated by `SessionFacts.Append`'s own
`FactStore.FindLiveFactId` check (`SessionFacts.cs:96-100`), which rolls back and returns the
existing fact's id before this feature would ever run. There is nothing left for same-slot
detection to surface: it is not rare, it is empty.

This is D57's own fingerprint-addressing rationale, not a defect: "a session note's path ends
in a fingerprint of its own statement, so rewording one addresses a different path and starts
its own history instead of extending the old note's." Same-slot collision (same
subject+predicate, different body) is exactly the case D57 designed *out* of session-note
addressing. Building it anyway would have required inventing a second, parallel
subject/predicate resolution scheme for session facts, specifically to manufacture a
collision the real design deliberately avoids — correctly flagged by the implementor as
outside their authority to decide.

`engram_revise` and `engram_remember`'s own `supersedes` parameter (`EngramMcpTools.cs:89-159`,
`309-386`) *do* use `(subject, predicate)` collision — but only because each resolves its
target fact by id first and then writes using *that fact's own* `SubjectPath`/`Predicate`
(`EngramMcpTools.cs:350-359`; `UserFacts.Restate` does the equivalent for `supersedes`). The
caller already knows which fact it means before either call; there is no surprise to surface
as a candidate there either.

Same-slot detection is dropped below, not descoped-and-deferred: there is no future version of
it to build against this write path. Near-neighbour candidates — the other half of the
original design — are real and are kept, redesigned to match the write path that actually
exists (see Design).

## Goal

`engram_remember` optionally surfaces near-duplicate or related *live* facts, found the same
way recall finds them (the existing FTS/token-overlap/vector lanes, D60/D36), so the calling
agent can record a relationship via a new `engram_judge` tool or explicitly revise via
`engram_revise`. `engram_judge` writes a verdict — supersedes, conflicts,
same-topic-different-scope, or no real conflict — to a side table that never touches `fact`.
`engram_expand … history` shows recorded verdicts.

## Non-goals

- No automatic conflict resolution — a verdict is an annotation, never a fact mutation.
- No LLM-judge subprocess spawned per verdict (parent spec, explicitly not adopted) — the
  verdict is the calling agent's, recorded through the MCP tool it already holds.
- No change to recall ranking beyond a single optional marker (see Design) — anything
  larger is out of scope pending evidence.
- No `judgment_id`/pending-row indirection (see Design) — a comparable tool needs it because
  its verdicts are pre-materialized rows; Engram's atomic design does not.
- **No same-slot / exact-`(subject, predicate)` collision surfacing.** `engram_remember`'s
  fingerprint-per-statement addressing (D57) makes this scenario structurally impossible for
  a fresh write — see Amendment note. The one case it could apply to, a byte-identical
  restatement, is already silently deduplicated by `SessionFacts.Append` before this feature
  runs, so there is nothing for it to report.
- No candidate search on the `supersedes` branch of `engram_remember`, and none inside
  `engram_revise` — both already target a specific fact by id; the caller has already made
  the identification this feature exists to assist with.

## Inspiration

A comparable memory tool lets an agent record a judgment about how two remembered items
relate — supersedes, conflicts, same-topic-different-scope, or no real conflict — as an
annotation kept apart from the memories themselves, surfaced as candidates when a new memory
is saved. Engram's version below is not a port: the candidate/verdict shape is closer to
Engram's own corroboration and versioning patterns than to anything borrowed.

## Design

**Candidates on `engram_remember` — near-neighbour only, run post-write.** After
`SessionFacts.Append` returns `factId` (`EngramMcpTools.cs:150-152`), and only on the
fresh-statement branch (i.e. not when `supersedes` is provided — see Non-goals), run the
calling `statement` text through the existing recall lanes — FTS, token-overlap, and the one
vector lane (D36) — scoped to *live* facts, store-wide. There is no cheaper scope available:
the implementor's report confirms `engram_remember`'s `subject` argument is free-text display
metadata, not a structured entity path, and a fresh statement gets its own new entity every
time (`SessionFacts.Append`'s `FactStore.EnsureEntity` call over the fingerprinted path) —
there is no existing entity grouping to scope a search to at write time, unlike the original
design's "same subject entity or its immediate path neighbours," which assumed a grouping
this write path doesn't have. Store-wide is the only scope that exists; D44's corroboration
bar (2+ lanes agreeing) is what keeps that from being noise, exactly as it already does for
recall — no new threshold invented for this feature.

Gate: only facts corroborated by 2+ lanes (D44's existing bar) are returned, capped at 3
results, with `factId` itself excluded from its own candidate search. Response shape:
`Remember` gains an optional `candidates: [{id, body}]` array, rendered with recall's
existing compact handle format. Absent when nothing clears the bar. (`relation_hint` from
the original design is dropped: with same-slot detection gone there is only one detection
path, so a field that distinguishes detection paths has nothing left to distinguish.)

**Everything else in this spec is unchanged from the original design**: `engram_judge`'s
one-tool shape, the relation set, the `fact_relation` schema, `relations.jsonl`
backup/replay, the `· judged` recall marker, and `engram_expand … history`'s verdict
listing. `engram_judge(fact_id, related_id, relation, reason)` is agnostic to how the two
ids were found — a near-neighbour candidate surfaced by `engram_remember`'s response and two
independently-chosen ids picked out-of-band are still the same call shape.

**`engram_judge` — one tool, not two.** New MCP tool:
`engram_judge(fact_id, related_id, relation, reason)`. Writes exactly one immutable row to
a new side table. The call *is* the judgment — there is no separate propose-then-judge step,
so the kind of pending/judged/orphaned/ignored state machine a two-phase design needs is
dropped entirely. That kind of state machine exists in a comparable tool because its
candidates are pre-materialized rows at save time, which can go stale before anyone acts on
them (the pre-created row's target gets deleted first). Engram's `fact_relation` row is
written atomically by the judging call itself against ids that are live *at that moment* —
there is nothing to pre-create, so nothing to orphan.

This also means Engram needs **one** tool where a two-phase design needs two — one for a
save-surfaced candidate referenced by its pending-row id, another for an out-of-band verdict
on two arbitrary ids: since `engram_judge` never depends on a pre-created pending row,
judging a candidate from `engram_remember`'s response and judging two arbitrary fact ids
picked independently are the *same call shape* — `engram_judge(fact_id, related_id, relation,
reason)` either way. Fewer tools is a direct, measurable win for spec 03's token-cost
concern, and it is a consequence of the atomic design, not a separate simplification bolted
on.

**Relation set — kept and dropped, each justified:**
- **`supersedes`** — kept. Matches Engram's own supersession concept directly.
- **`conflicts_with`** — kept. The core case this feature exists for.
- **`scoped`** — kept. Distinguishes "both true, different scope/time" from a real
  conflict; without it every same-slot candidate looks like a fight.
- **`not_conflict`** — kept, as the sole "false alarm" value. A comparable tool's own
  relation vocabulary was examined during design and, of its equivalent set, only the
  "false alarm" value was ever actually branched on in code — evidence that the rest are
  informational-only even in the system that first tried them.
- **A "compatible"-style relation** — considered, dropped. It means the same thing as
  `not_conflict` — two words for one fact-state is exactly what "one implementation per
  behaviour" argues against.
- **A "related"-style catch-all relation** — considered, dropped. A vague catch-all that
  adds no information recall's own corroboration (D44) doesn't already convey.
- **A pending/judged/orphaned/ignored status field** — considered, dropped as a concept, not
  narrowed. Beyond the "atomic write needs no pending state" argument above, one such state
  examined during design was defined but never actually set by any code path — a concrete
  instance of a state machine carrying more surface than its own implementation uses.

**Storage** — side table, rows immutable (a re-judgment is a new row, preserving full
history, consistent with the append-only philosophy applied to this new artifact too):
```sql
CREATE TABLE fact_relation (
  id INTEGER PRIMARY KEY,
  fact_id    INTEGER NOT NULL REFERENCES fact(id),
  related_id INTEGER NOT NULL REFERENCES fact(id),
  relation   TEXT NOT NULL CHECK (relation IN
             ('supersedes','conflicts_with','scoped','not_conflict')),
  reason     TEXT,
  judged_at  INTEGER NOT NULL
);
CREATE INDEX ix_fact_relation_fact    ON fact_relation(fact_id);
CREATE INDEX ix_fact_relation_related ON fact_relation(related_id);
```

**Backup/journal (D32).** `fact_relation.fact_id`/`related_id` are local integer ids, not
portable — but backup replay only has to survive *local* disaster recovery, not
cross-machine sync (spec 01 does not replicate verdicts; see Open questions). The same
`BackupService` that writes `facts.jsonl` also writes a small sibling `relations.jsonl`,
each row keyed by the 4-tuples of `fact_id` and `related_id` rather than raw ids. Replay
resolves both through the *same* `idMap` supersession-pointer resolution D32 already uses:
if either side isn't in `idMap`, the row is skipped and counted, never pointed at the wrong
fact — mirroring D32's "a conflicted fact gets no idMap entry" rule exactly, for the same
reason.

**Recall marker.** Ranking is unchanged (no measured argument meets the bar D44/D60 set for
touching it). One addition, directly mirroring D57's `· v2` version marker: a recall line
for a fact with any `fact_relation` row gets a `· judged` suffix, computed by one grouped
COUNT query shaped exactly like `FactStore.VersionCounts` (`RelationCounts`, same one-query-
for-the-whole-catalog pattern). Advertise, don't inline — same precedent, same cost shape.

**`engram_expand … history`.** Extends the existing history view (no new view parameter):
when expanding a fact, also list any `fact_relation` rows referencing it as `fact_id` or
`related_id`, with relation, reason, and timestamp.

## Invariants preserved

- **D8**: `fact_relation` never touches a `fact` column; a verdict never alters a fact.
- **D32**: `relations.jsonl` reuses the exact `idMap` resolution replay already has.
- **D57**: the `· judged` marker is the same "advertise, don't inline" pattern as `· v2`;
  and the same-slot drop above is a direct application of D57's fingerprint-addressing
  rationale, not a workaround around it.
- **D44**: recall ranking explicitly left unchanged; near-neighbour candidates reuse D44's
  corroboration bar rather than a new threshold, and corroboration logic is not duplicated.

## Tests by tier (D9)

- **Tier 1**: `fact_relation.relation` CHECK constraint. Falsify: remove the CHECK, confirm
  an insert of an invalid relation value that should fail now succeeds.
- **Tier 2**:
  - `engram_remember` returns near-neighbour candidates for a statement lexically/
    semantically close to an existing live fact, gated by the 2+-lane corroboration bar, and
    returns none for a genuinely novel statement. Falsify: drop the corroboration gate
    (return single-lane matches too) and confirm a noise test — asserting a weakly-related
    statement returns no candidates — starts failing.
  - A byte-identical restatement via `engram_remember` returns the *existing* fact's id
    (via `SessionFacts.Append`'s own `FindLiveFactId` short-circuit) and produces no
    `candidates` field at all. Falsify: disable that short-circuit and confirm a
    duplicate-fact-count test starts failing (a second row gets written instead of the
    existing id being returned).
  - No candidate search runs when `supersedes` is provided. Falsify: remove the branch
    check and confirm a test asserting "no `candidates` field when `supersedes` is given"
    starts failing.
  - `engram_judge` writes exactly one row and `expand … history` shows it (falsify: skip
    the write, confirm the missing-row test catches it), for both a save-surfaced candidate
    and two independently-chosen ids — proving one tool genuinely covers both use cases.
  - Replay of `relations.jsonl` resolves via `idMap` and skips (never mis-points) an
    unresolved reference (falsify: point an unresolved reference at an arbitrary id instead
    of skipping, confirm the test catches a wrong pointer rather than a clean skip).
- **Tier 3**: end-to-end MCP round trip (`remember` → candidates → `judge` → `expand
  history`) against the published binary.

## Measurements

- Token/byte delta of `engram_judge`'s tool description plus the `candidates` field
  addition to `engram_remember`'s description, against `docs/mcp-tool-descriptions.golden.txt`
  (feeds spec 03's measured line item). This delta is a *net saving* relative to a two-tool
  shape — one new tool instead of two — worth stating explicitly when this number is
  reported.
- Latency added to `engram_remember` by running recall's three lanes synchronously
  post-write, at two corpus sizes (5,097 / 50,097, matching this codebase's existing
  measurement scale). This is now the *only* candidate mechanism — there is no cheap
  same-slot fallback to default to instead — and it taxes every fresh `engram_remember`
  call, so ship it behind `[remember] candidates = false` (default off, via
  `ConfigEditor`/D33's marker convention, mirroring spec 01's `[sync] enabled` default-false
  pattern) until this number is in. This is an argued default, not a deferred decision: D4's
  hot-path-cost discipline and spec 01's own precedent both say a synchronous addition to a
  frequently-called path ships opt-in until measured.

## Open questions / NEEDS-EVIDENCE

1. **[measurement]** Exact byte delta above — Implementor should diff the golden file with
   and without the new tool/field.
2. **[measurement]** Latency of running recall's lanes synchronously inside `engram_remember`
   at 5,097 and 50,097 live facts. Decides whether `[remember] candidates` can default to
   `true` or must stay opt-in indefinitely. (This replaces the original spec's "same-slot-only
   is the safe v1 default if this isn't run first" fallback — that fallback no longer exists,
   since same-slot detection has been dropped entirely; the config-gate above is the new
   fallback.)
3. **Scoping decision, not evidence**: spec 01's sync does *not* replicate `fact_relation`
   rows — verdicts are local-store annotations only in this round. Flagging this explicitly
   rather than leaving it implicit; cross-machine verdict propagation is future work.
4. **[product tuning, not evidence]** Whether capping candidates at 3 and requiring 2+-lane
   corroboration is the right noise/usefulness balance for a remember-time nudge — D44's bar
   was tuned for recall's coverage question, not for this use case. No data collected yet;
   flagged as a follow-up tuning question once the latency measurement (item 2) clears this
   for default-on. **Known mechanism, not yet a decided tradeoff:** in a sparse store, the
   `fact_token` overlap lane (D59) and the `fact_fts` lexical lane correlate on a single
   shared non-stopword word, because nothing else in a small corpus competes to push that
   match out of either lane's ranked candidate set — so one ordinary shared word alone is
   enough to clear D44's 2+-lane bar and surface an otherwise-unrelated fact as a candidate.
   Confirmed structurally (candidates search calls the same `RecallRanker.Rank` D44 already
   gates on, unmodified) and empirically while writing tests
   (`tests/Engram.Integration.Tests/RememberCandidatesTests.cs:43-49` — a reliable
   zero-candidates negative case needed invented nonsense vocabulary, since topically-distant
   real English still shares too many ordinary words in a sparse test store). Not a code
   defect and nothing to fix here — D44's bar is reused unmodified exactly as designed — but
   worth knowing before ever flipping `[remember] candidates` to default-on: at small corpus
   size, this correlation *is* the noise floor, and it will not shrink by adjusting the cap
   or the lane count alone.
