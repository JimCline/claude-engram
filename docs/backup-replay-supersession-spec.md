# `backup replay` — supersession writes must respect what the target already believes

**Status:** FINAL. Ready to implement.
**Scope:** one commit, one D-entry. Independent of `docs/repo-index-remediation-spec.md`; do not fold into that
sequence. See §9 for why.
**Single file changed:** `src/Engram.Core/FactJournal.cs`. Tests as listed in §7.

---

## 1. The goal in one paragraph

`backup replay` is documented as additive and idempotent: it never rewrites or closes a fact the target store
already had. That discipline is implemented for fact *bodies* — `Existing()` suppresses a duplicate insert,
`WouldDisplaceALiveBelief` refuses to close a live belief to make room. It is **not** implemented at the one
site that writes to an existing row: `Link`, which sets `fact.superseded_by`. `Link` will happily write that
column into a row the target already had, in any state, and the row it writes to may have been chosen
non-deterministically. This spec closes both halves.

Nothing here is a new policy. The rule is already stated in the code, at `FactJournal.cs:355-356`:

```
// No idMap entry on purpose: nothing was written, so a supersession pointing at
// this fact has to come out as unresolved rather than aimed at some other row.
```

An `idMap` entry means *this replay wrote that row and can vouch for it*. The conflict branch honours that. The
`Existing()` branch at `:348` creates an entry from a **match** rather than a **write**, and pass 2 at `:381`
then hands that id to `Link` as the row to modify. The fix is to make `:348` obey the rule `:355` states.

---

## 2. Current behaviour, precisely

`Replay` (`FactJournal.cs:299-386`) runs two passes over the journal inside one `BEGIN IMMEDIATE`.

**Pass 1, `:343-363`** — for each journalled fact:
- `Existing()` returns a target-store row id → `idMap[fact.Id] = id`, `present++`, `continue`.
- else `WouldDisplaceALiveBelief` → `conflicted++`, `continue`, **no `idMap` entry** (deliberate, `:355`).
- else → `idMap[fact.Id] = Insert(...)`, `written++`.

**Pass 2, `:365-382`** — for each journalled fact carrying `SupersededBy`:
- `newId  = idMap[fact.Id]` — **the row to modify**.
- `newTarget = idMap[fact.SupersededBy]` — **the row to point at**; missing → `unresolved++`.
- `Link(connection, transaction, newId, newTarget, fact)`.

`Existing()` (`:428-446`) matches on `e.path`, `f.predicate`, `f.body`, `f.valid_from` with `LIMIT 1` and **no
`ORDER BY`**. Where the target holds two rows sharing that tuple — which is reachable; see §9 — the row returned
is whichever SQLite's B-tree hands back.

`Link` (`:494-527`) does three things: `UPDATE fact SET superseded_by = $target WHERE id = $id`, an
unconditional `FactTokenIndex.Remove`, and an `INSERT INTO supersession ... ON CONFLICT(old_fact_id) DO NOTHING`.
It never sets `valid_to` — `Insert` does that, from the journal record, at insert time.

### 2.1 The two harms

**A — target already closed and superseded.** `Link` overwrites `fact.superseded_by`, while the `supersession`
insert's `ON CONFLICT(old_fact_id) DO NOTHING` preserves the original row. The store then holds two records of
one relationship that contradict each other, with nothing that reconciles them. The two halves of a single
operation disagree about idempotency.

**B — target live.** `Link` writes `superseded_by` and does not set `valid_to`, so the row becomes **live and
superseded simultaneously** — a state `ux_fact_live` does not constrain (it is partial on `valid_to IS NULL` and
indexes nothing else) and that nothing downstream expects. `FactTokenIndex.Remove` at `:512` then fires
unconditionally, stripping a **live** fact from an index documented to hold live facts only, while `fact_fts`
still carries it. That is a silent index disagreement on a currently-held belief, costing the overlap lane.

### 2.2 The comment at `:508-511` is where this went wrong

> a fact only reaches Link when its journal record already carried superseded_by, which in the source store
> means valid_to was already set — so Insert never indexed it

Every clause is true **of the journal's fact**. The row being modified is the **target's** row. The two are the
same object only when this replay inserted it. §3's guard is what makes that substitution valid; under it the
comment becomes true rather than aspirational. **Leave the comment and the `Remove` call in place.**

---

## 3. Ruling 1 — the guard axis is provenance, not row state

> **`Link` may write `superseded_by` into a row only if this replay inserted that row. Never into a row the
> target already had — regardless of that row's state.**

### 3.1 Why not a row-state predicate

The obvious alternative is to guard in SQL:

```sql
UPDATE fact SET superseded_by = $target
WHERE id = $id AND valid_to IS NOT NULL AND superseded_by IS NULL;
```

A row **this replay inserted** always satisfies that: `Insert` wrote `valid_to` from the journal, and never
writes `superseded_by`. So the predicate admits every row provenance admits, **plus one class** — pre-existing
rows that are closed with `superseded_by` NULL.

That class is the recorded shape of a `Forget`: closed, no replacement, therefore no `superseded_by` and no
`supersession` row. There is no other shape a retraction has. It is reachable by ordinary means — the source
store superseded a belief, the target forgot the same belief, and the journal carries the source's account.
Divergent histories for one belief is the case replay exists to handle carefully.

Under the row-state predicate that row is written. The retraction becomes a supersession, the `supersession`
insert fabricates a record of a replacement that never happened, and — because the predicate *matched* — it is
counted as a **success rather than a conflict**. That is the failure this whole change is about, surviving
inside the guard meant to stop it.

It cannot be patched in row-state terms. A replay-inserted row and a forgotten row are structurally identical:
closed, `superseded_by` NULL, no `supersession` row. **No predicate over the row's own state separates them.**
Provenance is the only discriminator that exists — this is not a choice between two workable guards.

### 3.2 Keep the SQL predicate anyway, as an assertion

Add `AND valid_to IS NOT NULL AND superseded_by IS NULL` to `Link`'s `UPDATE`. Under the provenance guard it
should never be the thing that stops a write. It is cheap insurance against a future caller reaching `Link` by
another route.

**The provenance check is load-bearing and the test must target it.** If a test can be made to pass by the SQL
predicate alone, it is testing the wrong guard — see §7, test 3, which exists precisely to fail under the SQL
predicate and pass under provenance.

### 3.3 What happens on a decline

Do **not** add a counter, and do **not** count an idempotent link at all. Re-read the row's current
`superseded_by`:

- anything other than the resolved `newTarget` — still live, NULL, or pointing elsewhere → **`conflicted`**.
- already equal to `newTarget` → **count nothing**.

*(Amended after implementation. This section first ruled the idempotent case `present`, which double-counts:
the pre-existing `FactJournalTests.Replay_Twice_WritesNothingTheSecondTime` went 2 → 3 on a 2-fact journal.
The reasoning below replaces it, and §5.3's code changed with it.)*

The two cases are not symmetric, and counting them alike is what makes the number unreadable. **Every fact
that reaches this branch was already counted `present` in pass 1** — by construction, since the only `idMap`
entries pass 2 can resolve come from an `Existing()` match (`present++`) or from `Insert` (`inserted`, which
takes the `Link` branch instead). The idempotent increment therefore never occurs on its own; it is one
journal record counted twice in every reachable case, and a counter whose increment cannot occur
independently of another is not measuring a distinct thing. Nothing needs tracking to suppress it — the
increment is unconditionally redundant, so it is deleted rather than guarded.

The conflict increment is the opposite: it is the only place its information exists, because pass 1 saw a
body that was present and could not see that the target disagrees about the edge. So a record counted
`present` in pass 1 and `conflicted` in pass 2 is intended, and it is the honest report — the body was
already there, the edge was not recovered. That shape is not new either: `unresolved` has always been a
per-edge count riding beside a per-fact one, and nothing decrements. **Do not "fix" the asymmetry by
decrementing `present`** — pass 1's `present` is a true statement about the body, and a report that withdraws
it sends someone to restore a snapshot they do not need.

The rule the counters obey, stated once so it is not rediscovered:

> **No increment may restate what another increment for the same record already said.** Pass 1's `present`
> says the target had this record; an idempotent edge says the same thing again and is silent. A refused edge
> says part of the record was not recovered, which nothing else says. `unresolved` says an edge could not be
> aimed anywhere, which nothing else says.

The documented meaning of the split is *"already there"* versus *"not recovered"*, and a declined link is
precisely **the journal held an edge, the target disagreed, and it was not recovered**. The meanings coincide,
so this is not a second meaning riding an existing field. `ReplayResult` keeps its shape and no caller changes.

---

## 4. Ruling 2 — presence and address are different questions

`Existing()` currently answers both with one call, and they need different treatment when the match is
ambiguous.

### 4.1 Presence must stay YES on ambiguity

`WouldDisplaceALiveBelief` is live-only, because `ux_fact_live` is partial on `valid_to IS NULL`. So a
**closed-duplicate pair with no live member does not trip it.** If ambiguity were allowed to suppress the
presence answer, replay would insert a third copy — and a fourth on the next replay, unbounded on every repeat.
Any `Existing()` match counts `present++` and suppresses the insert, ambiguous or not.

### 4.2 The address is where ambiguity bites

- **Exactly one match** → use it. Single candidate, no choice to make.
- **Several matches, exactly one live** → use the live one. At most one can be live, by `ux_fact_live`. A
  supersession pointer means *this belief was replaced by that one*, and "that one" is the belief currently
  held; naming a closed duplicate names a row that is not the current belief. This is a **basis**, not a
  tiebreak.
- **Several matches, all closed** → **no basis.** `f.id ASC` here is arbitrary-but-deterministic, which is still
  arbitrary; a deterministic wrong address is worse than none, because it reads as a decision. Withhold the
  `idMap` entry.

A withheld address means the pointer cannot be written, which is what `unresolved` already means — *"the belief
still closed at the recorded time; only the pointer to what replaced it is lost."* Reuse it; do not invent a
bucket.

---

## 5. Exact changes to `src/Engram.Core/FactJournal.cs`

### 5.1 `Existing()` — return an address decision, not a bare id

Replace the current signature and body. Add, near `JournalFact`:

```csharp
private readonly record struct ExistingMatch(long Id, bool AddressUsable);
```

```csharp
private static ExistingMatch? Existing(
    SqliteConnection connection,
    SqliteTransaction? transaction,
    JournalFact fact)
{
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    // Two rows, not one: the second says only "there is more than one", which is what decides whether an
    // address exists. Live first, so row 0 is the live one when any match is live — ux_fact_live guarantees
    // at most one is.
    command.CommandText =
        """
        SELECT f.id, f.valid_to IS NULL AS is_live FROM fact f
        JOIN entity e ON e.id = f.subject_id
        WHERE e.path = $path AND f.predicate = $predicate AND f.body = $body
          AND f.valid_from = $validFrom
        ORDER BY is_live DESC, f.id ASC
        LIMIT 2;
        """;
    // ... same four parameters as today ...

    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
        return null;
    }

    var id = reader.GetInt64(0);
    var live = reader.GetInt64(1) != 0;
    var ambiguous = reader.Read();

    // Ambiguity does not affect the presence answer — see spec §4.1 — only whether this row may be used as
    // the address a supersession points at. Several closed duplicates give no basis for preferring one.
    return new ExistingMatch(id, AddressUsable: !ambiguous || live);
}
```

### 5.2 Pass 1 — separate presence from address, and record provenance

Add beside `idMap` at `:311`:

```csharp
// Journal ids whose target row this replay inserted. Link may write superseded_by into those rows and no
// others: a row that was already here carries the target's own account of how the belief closed, and
// replay does not rewrite that (spec §3).
var inserted = new HashSet<long>();
```

Replace `:345-351`:

```csharp
var existing = Existing(connection, transaction, fact);
if (existing is { } match)
{
    if (match.AddressUsable)
    {
        idMap[fact.Id] = match.Id;
    }
    else if (fact.SupersededBy is not null)
    {
        unresolved++;
    }

    present++;
    continue;
}
```

and `:361`:

```csharp
idMap[fact.Id] = Insert(connection, transaction, fact);
inserted.Add(fact.Id);
written++;
```

The `unresolved++` sits here rather than in pass 2 because this is the point at which the reason the pointer is
lost is known, and because moving it to pass 2 would make it indistinguishable from the pre-existing
"conflicted fact has no `idMap` entry" skip at `:367`, which must keep its current silent behaviour — a
conflicted fact is already counted once and must not be counted twice.

### 5.3 Pass 2 — guard the write, classify the decline

Replace `:381`:

```csharp
if (inserted.Contains(fact.Id))
{
    Link(connection, transaction, newId, newTarget, fact);
}
else if (CurrentSupersededBy(connection, transaction, newId) != newTarget)
{
    conflicted++;
}
```

Add a scalar helper alongside `Existing`:

```csharp
private static long? CurrentSupersededBy(
    SqliteConnection connection,
    SqliteTransaction transaction,
    long factId)
{
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT superseded_by FROM fact WHERE id = $id;";
    command.Parameters.AddWithValue("$id", factId);
    return command.ExecuteScalar() as long?;
}
```

One scalar read per declined link, on a recovery path that is not latency-bound. Do not batch it.

`CurrentSupersededBy` returns `long?` and `newTarget` is `long`, so the comparison lifts and a NULL
`superseded_by` falls into `conflicted` — which is correct, and is the same classification §3.3's list gives
it. Do not "tidy" it into `!CurrentSupersededBy(...).Equals(newTarget)`, which dereferences the null.

### 5.4 `Link` — add the assertion predicate

```csharp
update.CommandText =
    "UPDATE fact SET superseded_by = $target "
        + "WHERE id = $id AND valid_to IS NOT NULL AND superseded_by IS NULL;";
```

Nothing else in `Link` changes. In particular **`FactTokenIndex.Remove` stays unconditional and its comment
stays as written** — §2.2 explains why the guard is what makes that comment correct.

### 5.5 Dry run

The dry-run branch at `:320-339` calls `Existing()` for its presence count. Update it to the new return type and
**ignore `AddressUsable`** — the dry run answers "would this fact be written", which ambiguity does not change.
Its `written`/`present`/`conflicted` arithmetic is unchanged. The dry run does not model declined links; that is
acceptable and deliberate, because a decline writes nothing and so cannot change what the apply would write.

---

## 6. What must not change

- **`ReplayResult`'s shape.** No new field, no renamed field. §3.3 exists so that this holds.
- **The `conflicted` branch at `:353-359` and its comment.** It already implements the rule; leave it alone.
- **`Insert`'s live-only `FactTokenIndex.Add` at `:486-489`**, and its comment.
- **`Link`'s `ON CONFLICT(old_fact_id) DO NOTHING`.** Do not "fix" the asymmetry described in §2.1-A by
  removing it — the asymmetry is resolved by the write never landing on a foreign row, not by making both halves
  overwrite.
- **`WouldDisplaceALiveBelief`** and the `claimed` set. Untouched. `claimed` remains dry-run-only.
- **Replay stays additive.** No `valid_to` is written by this change, no fact body is altered, no row is deleted.
  D8 is not approached.
- **`FactJournalTests.Replay_Twice_WritesNothingTheSecondTime`'s expectation.** It asserts a 2-fact journal
  replayed twice reports `AlreadyPresent` 2. It predates this change and independently encodes the per-record
  rule, so it is evidence about the invariant rather than a baseline to update. It must pass unmodified.

---

## 7. Tests

Tier 2 (integration, real SQLite file) unless stated. All five are required; 3 and 4 are the ones that
discriminate between this design and the rejected ones, so neither may be dropped as redundant.

**1 — planted duplicate pair, chain unchanged.** Seed a store with two rows sharing
`(subject_id, predicate, body, valid_from)`, one closed and superseded, one live. The partial index permits this,
so insert them directly — **do not build this fixture by running the concurrent-index race.** A test that passes
because a 1-in-80 race did not fire is indistinguishable from one that passes because the bug is fixed. Replay a
journal whose record for that belief carries a `superseded_by`. Assert: neither row's `superseded_by` changed,
no new `supersession` row for either, the live row is still live, the live row's tokens are still in
`fact_token`, and the outcome counted `conflicted`.

**2 — idempotency pair.** Replay one journal into one store **twice, as two genuinely separate `Replay`
invocations.** Not one transaction reused: `claimed` is dry-run-only, and a single invocation would see its own
writes and prove nothing about the second run. Assert run 2 returns counts identical to run 1 and that no row
count anywhere changed.

**3 — the forgotten row. This is the falsification for the rejected row-state guard.** Target holds a fact
closed with `superseded_by` NULL and no `supersession` row (the shape `Forget` leaves). The journal holds the
same fact — same subject, predicate, body, `valid_from` — carrying a `superseded_by` whose target is also in the
journal. Assert: `superseded_by` is still NULL, no `supersession` row was created for it, and it counted
`conflicted`. **Prove this test fails**: implement §3.1's rejected `WHERE valid_to IS NOT NULL AND superseded_by
IS NULL` guard *instead of* the provenance check and confirm it reddens, then restore. If it stays green under
that substitution, the test is targeting the assertion predicate and not the guard, and must be rewritten.

**4 — all-closed ambiguity yields no address.** Seed two rows sharing the tuple, **both closed**, no live
member. Journal carries a supersession aimed at that belief. Assert: `unresolved` was counted, neither duplicate
gained a `superseded_by`, and no third copy of the fact was inserted. The last clause is the §4.1 half — run the
replay twice and assert the fact's row count is unchanged both times.

**5 — the counts, both halves.** Two assertions the four above leave free, both cheap and both silent if they
rot. (i) In test 3's scenario — the target holds the body and refuses the edge — assert `AlreadyPresent` is 1
*as well as* `Conflicted` being 1. That pins §3.3's "do not decrement": without it, a later change could
report the body as absent with every other assertion still green. (ii) The idempotent case is already
covered — `FactJournalTests.Replay_Twice_WritesNothingTheSecondTime` replays a 2-record journal carrying a
supersession twice and asserts `AlreadyPresent` 2. It must pass unmodified; do not re-baseline it.

**Falsification discipline for all five**: falsify against a committed tree and run `git diff --quiet` to confirm
the break actually landed before trusting a red arm. A harness that restores with `git checkout --` reverts an
uncommitted change under test, and a no-op patch reports green while proving nothing.

**Not a counterexample, and worth a line in the commit message:** restore-into-empty-then-replay never reaches
the guard, because an empty target has nothing to conflict with. It does not test this change either way.

---

## 8. Pre-flight checks for the Implementor

Neither is a design question; both are things I did not verify and that would change the shape if false.

1. **`Link` has no caller outside `Replay:381`.** `grep -n "Link(" src/Engram.Core/FactJournal.cs`. If another
   call site exists, provenance is not well-defined there and this spec needs amending — stop and report rather
   than choosing.
2. **No caller destructures `ReplayResult` positionally in a way a field reorder would break.** Nothing here
   reorders it, but confirm before assuming §6's first bullet is free.

---

## 9. Why this is its own commit

The defect is not specific to the concurrent-indexing race. Any two rows sharing
`(subject_id, predicate, body, valid_from)` trigger it, and the race is merely the one mechanism observed
producing such a pair (one group in 401 rows, in the concurrent arm only; absent from the serial baseline and
from both `--drain`-shaped arms). Filing it inside `docs/repo-index-remediation-spec.md` would encode a false
causal story — that this is a symptom of that race — into the commit history.

**One consequence that does belong in the remediation spec's lock commit:** duplicate tuples are `fact` rows, so
by D8 `repair` may never delete one. The duplicates the race creates are **permanent**. This change makes replay
survive them; nothing heals them. That is a real strengthening of the case for the index lock and should be stated where the lock lands
rather than discovered later — it now is, in `docs/repo-index-remediation-spec.md` §11.2, which records
that the lock prevents new pairs and heals none of the ones already written, so this change is required
whether or not the lock ships.

---

## 10. D-entry text

To be appended to `docs/engram-implementation-plan.md` by whoever holds an editor for that file. Numbering is
theirs to assign; the text assumes D68.

> **D68 — A supersession may only be written into a row this replay inserted, and provenance is the only thing
> that can express that.** `backup replay` is additive: it never rewrites or closes a fact the target already
> had. That was implemented for fact bodies and not at `FactJournal.Link`, the one site that writes to an
> existing row. `Link` sets `superseded_by` and never `valid_to` — `Insert` writes that from the journal — so a
> supersession applied to a **live** target produces a row simultaneously live and superseded, a state
> `ux_fact_live` does not constrain because it is partial on `valid_to IS NULL`; and `FactTokenIndex.Remove`
> then fires unconditionally, stripping a live fact from an index that holds live facts only while `fact_fts`
> keeps it. Applied to an **already-superseded** target it overwrites `fact.superseded_by` while the
> `supersession` insert's `ON CONFLICT(old_fact_id) DO NOTHING` preserves the original, leaving two records of
> one relationship that contradict each other — the two halves of one operation disagreeing about idempotency.
>
> **The guard cannot be written as a row-state predicate, and the reason is the whole decision.** The obvious
> `WHERE valid_to IS NOT NULL AND superseded_by IS NULL` admits every row provenance admits *plus* pre-existing
> rows closed with `superseded_by` NULL — which is the recorded shape of a `Forget`, closed with no
> replacement, and there is no other shape a retraction has. Writing there converts a retraction into a
> supersession and fabricates a `supersession` row for a replacement that never happened, counted as success
> rather than conflict. A replay-inserted row and a forgotten row are structurally identical, so no predicate
> over row state separates them. The predicate is kept on the `UPDATE` as an assertion, but the test targets
> provenance: implement the predicate *instead* and the forgotten-row test reddens, which is how it was
> accepted.
>
> The rule was already in the file. `FactJournal.cs:355` says an absent `idMap` entry must make a supersession
> "come out as unresolved rather than aimed at some other row" — an entry means *this replay wrote that row*.
> The `Existing()` branch created entries from a **match** instead, and pass 2 fed those to `Link`. That branch
> now obeys the comment the other branch already had.
>
> **Presence and address are two questions, and `Existing()` answered both with one `LIMIT 1` and no
> `ORDER BY`.** Where the target holds duplicate `(subject, predicate, body, valid_from)` tuples — reachable,
> and measured once in 401 rows under two concurrent `index --apply --full` runs — the row returned was
> whichever the B-tree handed back. Presence must stay YES on ambiguity: `WouldDisplaceALiveBelief` is live-only
> for the same partial-index reason, so a closed-duplicate pair with no live member does not trip it, and
> suppressing presence would insert a third copy, then a fourth, unbounded on every replay. The **address** is
> where ambiguity bites: exactly one live match is a basis (a supersession names the belief currently held, and
> at most one duplicate can be live), while several closed matches give none — `f.id ASC` there is
> arbitrary-but-deterministic, which is still arbitrary, and a deterministic wrong address is worse than none
> because it reads as a decision. All-closed ambiguity withholds the `idMap` entry and comes out `unresolved`,
> which already means the pointer was lost.
>
> A declined link is classified into the buckets that exist rather than a new counter — anything other than
> the intended target is `Conflicted` — and an idempotent one is **not counted at all**. Every fact reaching
> that branch was already counted `AlreadyPresent` in pass 1, by construction, since pass 2's only resolvable
> `idMap` entries come from an `Existing()` match or from `Insert`; so the increment could never occur on its
> own, and a counter whose increment cannot occur independently of another is not measuring a distinct thing.
> The conflict increment is kept precisely because it is not redundant: pass 1 saw a body that was present and
> could not see that the target disagrees about the edge. A record counted `AlreadyPresent` and `Conflicted`
> is therefore intended and honest — the body was already there, the edge was not recovered — and it is the
> established shape, since `Unresolved` has always been a per-edge count riding beside a per-fact one. Pass
> 1's `AlreadyPresent` is not decremented to compensate: it is a true statement about the body, and a report
> that withdraws it sends someone to restore a snapshot they do not need. The rule is that **no increment may
> restate what another increment for the same record already said**. The first version of this ruling counted
> the idempotent link `AlreadyPresent`, and a pre-existing test caught it: a 2-record journal replayed twice
> reported 3 already present, which the CLI prints as "leaving 3 already in the store".
>
> Duplicate tuples are `fact` rows, so by D8 `repair` may never delete one: the duplicates are permanent, and
> this change makes replay survive them rather than removing them.

---

## 11. Confidence and what would change it

Good on both rulings. Two things would change the design rather than the wording, and either should come back
rather than be worked around:

1. **A `Link` caller outside `Replay`** — provenance would not be well-defined at that site. §8.1.
2. **A consumer that depends on replay filling in a missing `superseded_by` on a pre-existing closed row.** I
   claim none exists, and that §3.1's `Forget` argument makes it wrong even if one did — but that is a reading
   of intent, not a measurement.

No NEEDS-EVIDENCE items. Nothing here required an experiment; the two rulings turn on reachable code paths and
on `ux_fact_live`'s documented partiality, both established by reading.
