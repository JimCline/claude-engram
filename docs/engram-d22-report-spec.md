# D22 — `engram report`: a readable report of everything Engram knows

Spec revision 3. Architect. Companion to `docs/code-navigation-phase4-spec.md` in shape and rigor:
numbered sections, explicit acceptance items each with a falsification, NEEDS-EVIDENCE kept
separate from design calls.

Source: `docs/engram-vs-graphify.md` §5 ("A human-readable view of what is stored"), lines 133–142.
Tracker: `docs/engram-vs-graphify-tracker.md`, lines 37–45.

**Revision 2** folded in Jim's rulings on the three questions revision 1 left open: scope and
destination confirmed as specced, **telemetry overridden** — a `report` kind ships now (§3.6,
acceptance items 16–18).

**Revision 3 corrects a spec-defect of mine.** Revision 1's §5.6 required the report to include
**pin state**, on the unchecked premise that a pin is a durable control the user set. It is not:
pins are per-MCP-session, in-memory, and deliberately unpersisted. **Pin state is now excluded**,
with the reasoning recorded in §5.6 so it is not re-added, and the tempting IPC fix rejected in §7.
See §10.6 and §11.4.

Acceptance items 1–18 are unchanged and keep their numbers.

---

## 1. What D22 actually says, and what the restatement added

### 1.1 The source text, verbatim

> Taken up as **D22** — `engram report`, Markdown rather than HTML, including superseded facts
> and with no truncation, on the grounds that retraction without enumeration is not a real
> control.

That is the whole of it. Four requirements and one rationale:

1. a verb named `report`;
2. Markdown, explicitly chosen over HTML;
3. superseded facts included;
4. no truncation;
5. **because** *retraction without enumeration is not a real control.*

### 1.2 What the brief's restatement added, and whether it holds

The dispatching brief listed three requirements: closed/superseded facts **"marked as such"**, no
truncation, a Markdown **artifact**. Checked against the source:

- **"no truncation"** — in the source verbatim. Confirmed.
- **Markdown** — in the source verbatim. **"Artifact"** is not; the source says the format, not the
  destination. §3.2 decides destination on other grounds and says so.
- **"marked as such"** — **not in the source.** It is an inference, and it is mine now rather than
  D22's. It is a sound one: an unmarked superseded fact in an enumeration is worse than an omitted
  one, because the reader cannot tell a live belief from a retracted one and the document then
  *misreports* what Engram believes. Requirement 5 is what forces it — a retraction you cannot
  see confirmed is the exact control D22 says does not exist. Adopted, with the provenance
  recorded here so a later reader does not mistake it for the original mandate.

### 1.3 Requirement 5 is the design constraint, not the preamble

*Retraction without enumeration is not a real control* is what decides every fork below. The
surface exists so a person can run `engram_forget`, run the report, and **confirm the fact is
closed and see what still stands**. Everything that makes the document shorter, prettier, or
faster at the cost of that confirmation is out — and, symmetrically, **anything that is not a
belief and cannot be retracted does not belong in it** (§5.6).

### 1.4 The near-miss confirmed: `browse` does not satisfy this

The brief's reading is correct, and it is correct for a stronger reason than volume.

- `MemoryBrowser.Browse`'s subject query (`MemoryBrowser.cs:65–73`) joins
  `fact f ON f.subject_id = e.id AND f.valid_to IS NULL` — **live facts only**. A closed fact is
  not merely ranked lower there; it is not in the result set at all. Requirement 3 is
  structurally unreachable without changing that query.
- `BrowseCommand.FactsPerNode = 5` (`BrowseCommand.cs:17`) caps per node. Requirement 4 fails.
- `BrowseCommand.Run` writes to `stdout` only and never to a file (`BrowseCommand.cs:52`, writes at
  `:75, :84, :97–104, :156–193, :242, :251, :261, :274`); it is an interactive TUI driven by
  single-letter keys (`Letters`, `:19`).

**Do not extend `browse`.** Both of its shortcuts are correct *for browse*: an interactive
navigator over live belief wants live belief and wants a screenful. Relaxing them to serve an
audit goal would make the interactive surface worse to make the audit surface exist, and the
D-7/`MemoryBrowser` allocation-shape invariant recorded in `CLAUDE.md` sits on that exact loop —
a "just remove the `valid_to` filter" edit lands in guarded code for an unrelated goal. `report`
is its own verb with its own read.

**One thing `browse` should not lose to this spec:** `MemoryBrowser` is not a dependency of
`report` and must not be edited by this work. See §6.

---

## 2. Files

| File | Change |
|---|---|
| `src/Engram.Cli/CliApp.cs` | one verb-registration line (§3.1) |
| `src/Engram.Cli/ReportCommand.cs` | **new** — argument parsing, destination, exit codes |
| `src/Engram.Core/MemoryReport.cs` | **new** — the Markdown renderer (§5) |
| `src/Engram.Core/FactJournal.cs` | expose the existing read; **no change to what it writes** (§4) |
| `src/Engram.Core/Telemetry.cs` | one new kind, added to `All` (§3.6) |
| `src/Engram.Cli/HelpText.cs` or wherever verb help lives | one entry, if that file exists |
| tests | §9 |

**Not touched:** `MemoryBrowser.cs`, `BrowseCommand.cs`, **`SessionPinStore.cs`**,
`docs/engram-schema.sql`, any migration. This adds **no schema version bump** — it is a pure read.

---

## 3. The surface

### 3.1 Verb

`report`. Verified free: the registered verbs are `home, init, serve, start, stop, restart,
status, doctor, activity, hook, probe, permissions, model, embed, scan, index, explain, timeline,
browse, backup, repo, queue, repair, review, compact, export, import, sync, directive, profile`
(`CliApp.cs`). Register in the same switch arm shape as its neighbours:

```csharp
"report" => ReportCommand.Run(homePath, rest, stdout, stderr),
```

(Precedent, verbatim: `"doctor" => DoctorCommand.Run(homePath, rest, stdout, stderr),`
`CliApp.cs:46`; `"browse" => …` `:57`; `"backup" => …` `:58`.)

### 3.2 Invocation

```
engram report [--out <path>] [--out -] [--authored-only] [--force] [--home <path>]
```

**Default destination is a file** — *ruled by Jim, confirming the spec's default* — at:

```
<home>/reports/engram-report-<yyyyMMdd-HHmmss>.md
```

and the only thing written to stdout is that path, on one line.

Three reasons, in order of weight:

1. **The document is large and is meant to be kept.** Requirement 4 forbids truncation, and this
   instance holds ~15,000 facts. A surface whose whole purpose is enumeration should produce
   something you can open, keep, and **diff against last week's** — which is how a person actually
   verifies a retraction (`diff` the two reports, see the fact move from live to closed).
2. **A timestamped default name cannot collide**, so the default path never triggers the
   overwrite question at all.
3. Markdown was chosen over HTML in D22 precisely because it is a file you read anywhere.

`--out -` writes the document to stdout instead, unmodified — a report you cannot pipe is a worse
report, and this costs one branch. `--out <path>` writes to an explicit path.

**`--out <path>` refuses to overwrite an existing file unless `--force` is passed**, and says so on
stderr with exit 1. This is the `ConfigEditor` rule (`CLAUDE.md`: *anything editing a user's file
backs it up first and refuses to overwrite a value it did not create*) applied at the coarsest
granularity available — a whole file Engram did not write. `--force` is not `--apply`: `report` is
not on the destructive-verbs list because the default path only ever adds, and the dry-run rule
governs verbs that remove or rewrite by default (D49). Overwriting on request, with a flag, is the
narrow case.

`--authored-only` is the **only** narrowing filter, and §5.2 requires the document to state that it
was applied and how many facts it excluded. See §3.4.

### 3.3 Store access

Open with `EngramDatabase.Open`, **never `OpenInitialized`**. Same rule as `doctor`
(`CLAUDE.md`): `OpenInitialized` migrates on open, and D31 makes a migration snapshot first, so
asking a read-only question would perform a write. A report of what is stored may not alter what
is stored.

**If the schema is older than the binary expects,** fail with exit 1 and a message naming the verb
that migrates, rather than migrating. Do not emit a partial report — a report of unknown
completeness is exactly what requirement 5 forbids.

**`report` never contacts a running server**, and must not gain a dependency on one. See §5.6 and
§7.

### 3.4 Scope: everything, by default

**The default report includes every fact in the store**, authored and `regenerable = 1` alike.
**Ruled by Jim, confirming the spec's default.**

The tempting default is authored-only, because ~6,400 of this instance's facts are indexed code
facts and they will dominate the document. Rejected: "everything Engram knows" with "no
truncation" cannot have a default that silently omits 43% of the rows. A person auditing what a
memory system holds about them is not well served by a document that decided for them which facts
counted.

The volume problem is solved by **structure, not omission** (§5.3): derived facts get their own
top-level section, after the authored ones, so the part a person came to read is at the top and
the rest is scrollable past.

`--authored-only` exists for when the user genuinely wants the short document. When it is passed,
the header says so and gives the excluded count (§5.2). **A report that narrows is fine; a report
that narrows silently is the defect.**

**There is no `--all` flag**, and adding one would be a defect rather than a convenience: `--all`
only means something if some other scope is the default, so shipping it would advertise a
narrowing default that does not exist and invite someone to make one later. The single narrowing
flag is `--authored-only`.

**This rule governs facts, and only facts** — the set D22 mandates enumerating. It is not a general
obligation to disclose everything Engram holds in any form anywhere; see §5.6.

### 3.5 Exit codes

- `0` — a report was written (including a report of an empty store).
- `1` — could not write (destination exists without `--force`, unwritable path, schema too old,
  store missing).

An **empty store produces a valid report saying zero facts, at exit 0.** It is not an error, and
"there is nothing here" is a true and useful answer to *what do you know about me*.

### 3.6 Telemetry — `report` gets its own kind

**Ruled by Jim, overriding revision 1's deferral.** A `report` telemetry kind ships with this
work.

**`TelemetryEventKind.Report` is a new constant and goes into `All`.** Four rules govern it, each
tracing to a documented trap:

1. **Its own kind. Never folded into an existing one.** D18 and D43 read `remember` and `recall` to
   answer whether *the model* reached for memory; a human-typed audit verb answering a different
   question must not move those numbers. This is the same rule that gave `user-prompt`'s automatic
   capture its own kind rather than reusing `remember` (D56).
2. **A single instant event, emitted after the document is written — not a `started`/`finished`
   pair.** D55's phase rule exists because *something displaying activity has to know how long to
   keep displaying it*; nothing displays a foreground CLI verb's progress, and the person who typed
   it is watching the terminal. If E1 shows `report` takes long enough that a phase pair earns its
   keep, that is a change with a number behind it — not a default.
3. **Emitted only on success, and only after the document exists.** The event means *a report was
   produced*, not *someone typed the verb* — the D56 placement rule. A failure is reported to
   stderr in front of the human who invoked it, so D54's "a service that declines records why" does
   not transfer: nobody is reading a log to find out why a command they just watched fail, failed.
4. **It must not write `fact_count`.** That field means *facts returned to the model* on a `recall`
   record and nothing at all on a primer, and a nearby number in a field meaning something else is
   exactly what D43 traced a wrong conclusion back to. A report enumerates to a human, not to the
   model. Carry the counts in fields of its own — total, closed, and the scope that produced them —
   adding fields to the record type if none fit rather than borrowing one that means something
   else. Bytes written is worth carrying too; it is the only cheap signal of whether the document
   is growing past usefulness.

Two consequences to expect:

- **The webhook delivers it** (D55) — every kind in `All` is deliverable, and a kind absent from
  `All` lands in `WebhookSettings.Unknown` and gets a `doctor` warning. Adding the constant without
  adding it to `All` is the failure mode.
- **No test may assert a total line count of `telemetry.jsonl`.** One more writer joins a shared
  log; this has already broken tests twice (`CLAUDE.md`, D55/D56). Filter by kind.

---

## 4. The read — reuse `FactJournal`'s, do not write a second one

`FactJournal` already performs exactly this read. Its SQL (`FactJournal.cs:180–189`):

```sql
SELECT f.id, se.path, se.kind, f.predicate, f.body, oe.path, oe.kind, f.scope,
       f.learned_via, f.regenerable, f.evidence, f.valid_from, f.valid_to,
       f.superseded_by, s.reason, f.created_at, f.details
FROM fact f
JOIN entity se ON se.id = f.subject_id
LEFT JOIN entity oe ON oe.id = f.object_id
LEFT JOIN supersession s ON s.old_fact_id = f.id
ORDER BY f.id;
```

**It has no `valid_to` filter, so it already returns closed facts** — requirement 3 — **and it has
no `LIMIT`** — requirement 4. It also already joins `supersession` for the retraction reason, which
is the single most load-bearing column for requirement 5.

### 4.1 The rule

**`report` calls that read. It does not get its own copy of that SQL.** Extract whatever is needed
so that one statement text serves both callers — the journal writer and the report renderer.

This is DRY as a correctness property, not a style preference. Both of D22's hard requirements
(closed-facts-included, never-truncated) are properties of *that query*. A second query written to
serve the report is a second place those properties can be lost, and the way they get lost is
someone adding `valid_to IS NULL` to make an unrelated screen faster. One statement, two callers,
and an equivalence test that fails if they ever disagree (acceptance item 8).

### 4.2 What the extraction may not change

`backups/facts.jsonl` is disaster-recovery output (D32). This work must not alter:

- the fields written (`FactJournal.ToJson`, `:687–706`),
- their order or names,
- which facts are journalled,
- the `ORDER BY f.id`.

The extraction is a **refactor with a byte-identical output obligation**, and it must be proven so
rather than assumed — see E2.

### 4.3 Ordering

`ORDER BY f.id` is the journal's order and it is correct for the journal (replay wants insertion
order). It is **wrong for the report**, which groups by subject.

`report` sorts the materialized result in memory: **subject path ascending, then predicate
ascending, then `valid_from` ascending, then `id` ascending as the tiebreak.** It does not change
the SQL's `ORDER BY` — that would change journal output (§4.2).

`valid_from` ascending within a predicate is what makes a supersession chain read as history in
the order it happened, which is the D57 version-thread shape and the thing a person is looking at
when they check a retraction. The `id` tiebreak exists so two facts sharing a `valid_from` second
still order deterministically; without it the document is not diffable, which defeats §3.2's first
reason.

### 4.4 Memory

The read materializes the whole store. At ~15,000 facts that is fine; see E1 before assuming it
stays fine. Streaming the render is a legitimate later change and this spec does not forbid it —
but it is **not** in scope now, and it must not be added speculatively.

---

## 5. The document

### 5.1 Timestamps: `MomentText`, at second resolution, in the reader's zone

Every rendered time — `valid_from`, `valid_to`, `created_at`, the generation stamp — goes through
`MomentText`, the one renderer, at **second** resolution in the **reader's local zone**.

This is a standing invariant (`CLAUDE.md`) and this surface is the one it was written for. The
case that forced it was *a superseded preference at 00:02:11 and its replacement at 00:02:20
rendering identically* — which is precisely a retraction whose confirmation the document destroys.
Do not introduce a second date format here, and do not coarsen to the minute or the day for
tidiness.

### 5.2 Header

The document opens:

```markdown
# Engram memory report

generated: <MomentText(now)>
store: <path to engram.db> (schema <n>)
facts: <total> total — <live> live, <closed> closed
scope: all facts
```

Rules:

- **`generated:` is on its own line, immediately after the title**, and no other line carries a
  wall-clock that moves per run. A diff of two reports must isolate the timestamp to one line;
  otherwise the diff-two-reports workflow that §3.2 rests on is noise.
- **`scope:` is always present**, and reads `all facts` in the default case. When `--authored-only`
  is passed it reads `authored facts only — <n> regenerable fact(s) excluded`. A scope line that
  only appears when a filter is on is a line a reader learns to skip.
- The counts are computed from the materialized set, not by a separate `COUNT(*)` — a second query
  can disagree with the body of the document, which is the D43 nearby-number trap. The telemetry
  event (§3.6) carries these same numbers and must take them from the same computation, for the
  same reason.
- **The header says nothing about pins.** §5.6.

### 5.3 Body structure

Two top-level sections, in this order:

```markdown
## Authored facts

### <subject path>

#### <predicate>

<one entry per fact, ordered per §4.3>

## Derived facts (regenerable)

<same shape>
```

Authored first, per §3.4. A section with no facts is emitted with an explicit "none" line rather
than omitted — an absent heading is indistinguishable from a bug.

### 5.4 One fact entry

```markdown
- **live** · from <MomentText(valid_from)>
  ```` 
  <body>
  ````
- **closed** · from <MomentText(valid_from)> · closed <MomentText(valid_to)> · superseded by #<id>
  · reason: <reason>
  ```` 
  <body>
  ````
  details:
  ```` 
  <details>
  ````
```

Required per entry:

- **The live/closed marker leads the line.** §1.2. It is the first thing on the bullet, before any
  timestamp, so scanning a long predicate section for "is it still believed" is a scan of one
  column.
- `superseded by #<id>` and `reason:` render **only when present**, and `reason` renders whatever
  `supersession.reason` holds. A closed fact with no successor renders `closed` with no
  `superseded by` clause — that is a forget, not a revision, and the two must be distinguishable.
- **`body` and `details` render in full. Never elided, never summarized, no `· +Nk` suffix.**
  Recall's truncation marker (D64) is correct for recall and forbidden here; this is the surface
  where the full text is the product.
- `object` / `object_kind`, `scope`, `learned_via`, `evidence` render as a compact metadata line
  when non-null. `evidence` in particular is what lets a reader check where a claim came from.

**No fact entry carries a pin marker.** §5.6.

### 5.5 Fenced bodies, with a computed fence length — this is a correctness rule

**Every `body` and every `details` renders inside a fenced code block whose fence is
`max(3, longest run of backticks in the content + 1)` backticks.**

Fact bodies are content Engram did not author and did not sanitize. A body beginning with `#`
forges a section heading; one containing a table pipe corrupts a table; one containing ``` breaks
out of a naive fence and the remainder of the document renders as the fact's continuation. In an
audit document, structural forgery is not a cosmetic bug — **it lets one fact's text make another
fact appear to be believed, or disappear.**

The computed fence length is the CommonMark rule (a fence closes only on a run of at least its own
length), so it is not a heuristic and there is no residual case. It costs one line of code and one
loop. Do not replace it with escaping, and do not add a "short simple bodies render inline"
fast path — that branch is where the one unescaped body gets through.

Fencing also preserves embedded newlines and leading whitespace exactly, which an audit surface
wants anyway.

### 5.6 Deliberately omitted

- **Salience.** Derived ranking state (D8). It answers *what would recall surface*, not *what does
  Engram know*, and a number beside every fact invites the reader to treat low-salience facts as
  less true.
- **Embedding presence / vector state.** Same reason; `embed --status` owns that question (D54).
- **Session ids and sitting metadata.** `MemoryBrowser.Sitting` (`:207`) surfaces these for
  navigation. They are addressing, not content, and they inflate the document.
- **Pin state.** See below — this one is a correction, not just an omission.

#### 5.6.1 Pin state — excluded, and why (revision 3 correction)

**Revision 1 required pin state and gave the reason "a pin is a control the user set". That premise
is false, and the requirement is withdrawn.**

What pins actually are, verified in the code rather than assumed:

- `SessionPinStore` is a 54-line class holding
  `ConcurrentDictionary<McpSessionId, HashSet<long>>` (`SessionPinStore.cs:14`). It opens no
  connection and executes no SQL.
- `engram_pin` / `engram_unpin` (`EngramMcpTools.cs:624–662`) delegate to it and write nothing to
  the database. `engram_pin`'s own tool description tells the model *"The pin does not persist past
  this session."*
- There is **no `pin` table and no `pinned` column** anywhere in `docs/engram-schema.sql`.
- This is deliberate and specified: `docs/memory-expansion/04-lifecycle-spec.md` (lines 93–135)
  says *"pin needs no database row, because it does not need to survive anything. It lives entirely
  in server memory, keyed by `McpSessionId`."*

Three reasons the exclusion is right, not merely forced:

1. **There is no "the pin state" to report.** Pins are keyed by MCP session, and several live
   sessions hold independent sets. Any single value a CLI process could print would be one
   arbitrary session's, or a union belonging to nobody — **inventing a cross-session notion of
   "pinned" that the feature deliberately does not have.**
2. **Reporting it would make the document wrong in a specific direction.** An audit surface listing
   "pinned" beside beliefs invites the reader to conclude something is *retained* that in fact
   evaporates when the server stops. A report whose job is to tell the truth about what is kept
   must not imply persistence where there is none — that is the same class of error as rendering a
   timestamp too coarse to order a supersession (§5.1).
3. **§1.3 settles it on its own.** The document exists so a retraction can be confirmed. A pin is
   not a belief, cannot be retracted, and carries no claim about the user. It is a
   ranking hint scoped to one conversation.

**§3.4's "a report that narrows silently is the defect" does not apply here.** That rule governs
*facts* — the set D22 mandates enumerating — and pins were never in it. So the document says
nothing about pins at all: no header line, no per-fact marker, no "pins not included" note. A
disclaimer about a thing that was never in scope is noise that teaches readers to skim the header.

**No acceptance item guards this.** A test asserting the renderer emits no pin output cannot fail
while no such code exists, and `CLAUDE.md` is explicit that a guard which cannot fail is worthless.
What stops a re-add is §7's prohibition and this section's reasoning, not a hollow green test.

---

## 6. What must not change

1. `backups/facts.jsonl` content — byte-identical before and after (§4.2, E2).
2. `MemoryBrowser.cs` and `BrowseCommand.cs` — untouched. In particular, do **not** relax
   `MemoryBrowser`'s `valid_to IS NULL` filter or `FactsPerNode`, and do not "simplify" the
   `ReadOnlySpan<char>` path-slicing loop in `Browse` while nearby — that loop is D-7 and its guard
   suite is blind to allocation shape (`CLAUDE.md`).
3. No schema change, no migration, no `SchemaVersion` bump.
4. No write of any kind to the store. `report` is a pure read. The telemetry append (§3.6) is a
   write to `telemetry.jsonl`, not to the store, and does not breach this.
5. Existing verbs' registration and behaviour.
6. The meaning of every existing `TelemetryEventKind`. §3.6 adds one; it changes none.
7. **`SessionPinStore` and the pin tools.** Pins stay per-session, in-memory, unpersisted, exactly
   as `docs/memory-expansion/04-lifecycle-spec.md` specifies. This work adds no persistence, no
   accessor, and no cross-session view.

---

## 7. Prohibitions

- **No truncation, anywhere, for any reason.** No `LIMIT`, no per-subject cap, no body elision, no
  "first N per predicate", no `--limit` flag. If the document is too large for something, that
  something is the wrong consumer.
- **No default that narrows**, and no `--all` flag. §3.4.
- **No second copy of the journal's fact query.** §4.1.
- **No second timestamp renderer.** §5.1.
- **No inline (unfenced) rendering of fact content.** §5.5.
- **No pin state in the document, and no mechanism built to obtain it.** §5.6.1. Specifically
  rejected: IPC or an HTTP call from the CLI to a live server to read its in-memory pins. It would
  make a pure-read verb depend on a running server — which §3.3 and §6.4 forbid, and which drags in
  the whole is-this-server-really-ours hazard D42 exists to handle — in order to synthesize a
  coherent answer to a question the design says has none. If durable pins are ever wanted, that is
  a feature with a schema row and a decision number, not something a report verb invents. **Do not
  persist pins as a side effect of this work.**
- **The telemetry event may not be folded into an existing kind, and may not write `fact_count`.**
  §3.6. *(Revision 1 prohibited a telemetry event outright; Jim overruled that. What survives is
  the narrower rule, which was the actual reasoning underneath it — the risk was never the event,
  it was the event inflating a number D18 and D43 read to answer a different question.)*
- **No HTML output, no `--format` flag.** D22 chose Markdown over HTML explicitly; adding the
  rejected option back as a flag re-opens a settled decision as a config knob.

---

## 8. NEEDS-EVIDENCE

These are empirical and are **not** design calls. Route to the Implementor; none of them blocks
starting §3–§5.

- **E1 — size and time of a full report at real corpus size.** Run `engram report` against this
  instance (~15,000 facts; `ENGRAM_HOME` set explicitly, per `CLAUDE.md`) and record: wall-clock,
  peak RSS, output file size in MB. **Decides:** whether §4.4's materialize-everything is fine as
  shipped (expected), or whether streaming needs to be scheduled; and, secondarily, whether §3.6's
  single-instant event should become a `started`/`finished` pair (only if the run is long enough
  that a consumer would need to know it is in progress). It does **not** decide whether to
  truncate — that is closed. If RSS is alarming, the answer is streaming.
- **E2 — the journal extraction is output-identical.** Capture `backups/facts.jsonl` from a
  disposable home before the §4 extraction; run the same `backup take` after; assert byte
  equality. **Per D60: do this against a committed tree and assert the patch actually landed
  (`git diff --quiet` on the extraction) before trusting the result** — a refactor "proven"
  identical by a harness that reverted it proves nothing.
- **E3 — closed-fact volume.** `SELECT count(*) FROM fact WHERE valid_to IS NOT NULL;` on this
  instance. **Decides:** nothing structural — it is a sanity read confirming the closed section is
  non-trivial, and if it comes back near zero, that itself is worth surfacing to Jim, because it
  would mean the retraction path has barely been exercised and D22's control has never been tested
  against real data.

---

## 9. Acceptance

Every guard below names how to make it fail. A guard that has not been seen to fail is worthless
(`CLAUDE.md`), and items 2, 3, 8 and 17 are the ones that actually carry D22 and Jim's override.

Items 1–18 are unchanged across revisions 2 and 3 and keep their numbers.

1. `engram report` with no arguments writes `<home>/reports/engram-report-<stamp>.md` and prints
   exactly that path on stdout, exit 0.
2. **A closed fact appears in the output and is marked closed.** Seed a store with one fact, revise
   it, report: both versions present, the older marked `closed`, carrying `superseded by` and the
   revision reason. **Falsify:** add `AND valid_to IS NULL` to the report's read; this test must
   redden. *(Load-bearing — requirement 3 and requirement 5.)*
3. **Nothing is truncated.** Seed a fact whose body exceeds recall's truncation threshold and one
   carrying `details`; assert both render in full, character for character, with no `+Nk` marker.
   **Falsify:** apply recall's truncation to the body; this test must redden. *(Load-bearing —
   requirement 4.)*
4. **Timestamps are second-resolution and local.** Seed two facts one second apart in the same
   predicate; assert they render with different, ordered timestamps. **Falsify:** render
   `yyyy-MM-dd`; must redden.
5. **A forget and a revision are distinguishable.** Seed one of each; assert the forgotten fact
   renders `closed` with no `superseded by` clause and the revised one renders with it.
6. **Fence lengths are computed.** Seed a body containing a triple-backtick fence and a line
   beginning with `#`; assert the emitted document's structure is unchanged by them — i.e. the
   heading count and section boundaries match a run with a benign body. **Falsify:** hardcode a
   three-backtick fence; must redden.
7. **Ordering is deterministic.** Two consecutive runs against an unchanged store differ only on
   the `generated:` line. **Falsify:** drop the `id` tiebreak from §4.3's sort and seed two facts
   sharing a `valid_from`; must redden (may need repetition to catch a stable-sort accident — if it
   cannot be made to fail reliably, say so rather than shipping a green test that proves nothing).
8. **The report and the journal read the same facts.** Against one seeded store containing live,
   closed, forgotten and regenerable facts, assert the set of fact ids in the report equals the set
   of ids in `backups/facts.jsonl`. **Falsify:** filter either side; must redden. *(Load-bearing —
   this is the guard that makes §4.1 structural rather than a comment.)*
9. `--authored-only` excludes `regenerable = 1` facts **and** the header's `scope:` line states the
   filter and the excluded count. **Falsify:** apply the filter without updating the header; must
   redden. *(The header half is the load-bearing half — the filter alone passes trivially.)*
10. `--out -` writes the document to stdout and creates no file.
11. `--out <existing path>` without `--force` exits 1, writes nothing, and names the conflict on
    stderr. With `--force` it overwrites.
12. An empty store yields a valid document reporting zero facts, exit 0.
13. A store whose schema predates the binary exits 1 with a message naming the migrating verb, and
    **the store's mtime is unchanged** — no migration, no snapshot. **Falsify:** switch to
    `OpenInitialized`; the mtime assertion must redden. (Same shape as `doctor`'s no-write guard.)
14. `backups/facts.jsonl` is byte-identical across the §4 extraction — E2, promoted to a standing
    test if it can be made one cheaply; otherwise a one-time verification recorded in the commit.
15. Tier 3: the published binary runs `report` end to end against a disposable home. Per
    `CLAUDE.md`, **read the skip count, not the pass count** — a tier-3 test with no binary present
    evaporates into the skip column while the summary still says `Passed!`.
16. **`TelemetryEventKind.Report` is in `All`.** Assert this **by reflecting over the kind
    constants and checking each appears in `All`** — never by iterating `All` and checking each
    entry is a valid kind. `CLAUDE.md` records that the obvious version is a tautology: a kind
    missing from `All` is simply never visited, and that test passes with the defect in place.
17. **One `report` record is written, after the document exists, on success only.** Run `report`
    against a seeded store; assert exactly one line of kind `report` in `telemetry.jsonl` **and
    zero lines of kind `recall` or `remember`**. Then run a failing invocation (`--out` at an
    existing path, no `--force`) and assert **no** new `report` line. **Falsify:** move the emit
    above the write-succeeded guard; the second half must redden. *(Load-bearing — the
    zero-`recall`/`remember` assertion is what enforces §3.6's rule 1, and the failing-invocation
    half is what enforces rule 3. The bare "a line was written" assertion passes under both
    defects.)* **Filter by kind — do not assert a total line count of `telemetry.jsonl`**; the
    session-start child and the server both write into it.
18. **The record carries no `fact_count`.** Assert the emitted `report` record's `fact_count` field
    is absent/null and that its own count fields carry the header's numbers. **Falsify:** write the
    total into `fact_count`; must redden.

**No item guards pin exclusion**, deliberately — §5.6.1 explains why a test there could not fail.

---

## 10. Decisions, and my confidence in them

| # | Decision | Confidence | Note |
|---|---|---|---|
| 10.1 | Own verb, not a `browse` extension | high | §1.4; both of browse's shortcuts are correct for browse |
| 10.2 | Reuse the journal read; one SQL statement, two callers | high | §4.1; requirement-carrying properties live in that query |
| 10.3 | File by default, `--out -` for stdout | **ruled — Jim confirmed** | was medium-high; §3.2's diff-two-reports argument stands |
| 10.4 | Everything by default; `--authored-only` opt-in; no `--all` | **ruled — Jim confirmed** | was the fork I flagged as most worth overruling, and it was not overruled. The D37 counter-argument (a document unpleasant enough that people stop running it) is now a thing to watch in E1, not an open question |
| 10.5 | Computed fence length, everything fenced | high | §5.5; correctness at a trust boundary, not formatting |
| 10.6 | **Pin state excluded; no mechanism built to obtain it** | high — **corrects revision 1** | §5.6.1. Revision 1 required it on the premise that a pin is a durable control. Verified false: `SessionPinStore` is in-memory and per-`McpSessionId`, no table, no column, unpersisted by design (`04-lifecycle-spec.md:93–135`). **The defect was mine and the premise was never checked** — the Implementor stopping rather than guessing is what a blocked spec is supposed to produce |
| 10.7 | A `report` telemetry kind ships now | **ruled — Jim overrode my deferral** | Revision 1 argued no kind, on the grounds that a human-run audit verb answers a different question from the one D18/D43 read the log for. Jim's call, and it costs little; what I kept is the reasoning *underneath* my objection, promoted to §3.6's four rules — own kind, instant not phased, post-success, and never `fact_count`. Those are what the objection was actually protecting |
| 10.8 | No schema change | high | pure read |
| 10.9 | Salience / embedding / session metadata omitted | medium-high | §5.6 |

**Nothing here is beyond me** and I am not recommending Ultra-Advisor escalation.

---

## 11. Questions raised in earlier revisions — all resolved

1. **Default scope** — *everything, including the ~6,400 code facts.* Jim confirmed the spec's
   default. Folded into §3.4; acceptance item 9 and the header's scope wording are unblocked and
   unchanged.
2. **Default destination** — *file, path printed to stdout.* Jim confirmed the spec's default.
   §3.2 unchanged.
3. **Telemetry** — *overridden: ship a kind now.* §3.6 is new as of revision 2, §7's blanket
   prohibition replaced by the narrower rule it was protecting, acceptance items 16–18 guard it.
4. **Pin state (raised by the Implementor, revision 3)** — *excluded.* §5.6.1. Not a scoping
   preference: the premise revision 1 rested on was factually wrong, and there is no coherent
   cross-session pin state for a CLI process to read even in principle.

**Nothing in this spec is now waiting on an answer.** The whole of §3–§7 and acceptance items 1–18
are buildable.

---

## 12. Debt this spec does not pay

- `docs/engram-vs-graphify-tracker.md` line 37's `- [ ] **D22 …** Not started.` needs updating when
  this lands. Orchestrator's, not mine.
- **Durable pins are not a gap this spec creates and not one it closes.** If Jim ever wants pin
  state to survive a session — and therefore to be auditable — that is a separate feature needing a
  schema row and a decision number. Flagged, not proposed: nothing observed so far suggests pins
  should be durable, and `04-lifecycle-spec.md` argues at length that they should not.
- Standing from the code-navigation work and unrelated to D22: `docs/code-navigation-spec.md`
  §7.1's *"written at insert and never updated"* wording is wrong in both halves post-Phase-4;
  mine, on that file's next revision.
