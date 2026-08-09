# Session capture — design in progress

Working document for the change that replaces "the model remembers to call `engram_digest`"
with something that does not depend on the model remembering. Read
`docs/engram-implementation-plan.md` first; this only covers what is not settled there.

**Status: designed; todo 1 built and reinstalled. Stages A, B, and C are all green.** Two real
manual compactions, one contaminated and one clean, both produced correct output — a well-formed
block, no self-reference corruption. Only auto-compaction (as opposed to manual) remains
unconfirmed. **Scope and privacy is answered** (capture everything, every project — the store is
local): todo 2b is no longer gated on a decision, only on an undesigned trigger (no `PostCompact`
hook exists yet). **2a (the parser) is built and green** —
`src/Engram.Core/CompactionDigestParser.cs`, 23 tests in
`tests/Engram.Core.Tests/CompactionDigestParserTests.cs` covering all four block rules, all three
item filters, the two-part round-trip against `CompactionDigest.Instruction`, the `v="EXAMPLE"`
guard, the scan-the-whole-record case, and the cap. 2b is ready to hand to an Implementor once the
trigger is designed. Resume at *What's next*.

---

## The problem, as it actually is

`engram_digest` has fired **0 times in 2,398 telemetry records over four days**, against 106
`remember`, 30 `recall`, 22 `browse`, 5 `expand`, 1 `revise`, 0 `forget`. M0's exit criterion
— *"`digest` fires at session end without prompting"* — has never been met.

The reason was mechanical, not behavioural, and nobody had located it: **nothing ever triggered
it.** The recall footer names `engram_digest` on every recall; it rode 30 of them and produced
nothing. Standing guidance where the model already reads is not a trigger.

## What was measured (2026-08-09)

### The PreCompact channel

A plan erratum claimed no injection channel exists for `PreCompact`. It was **read from the
docs, not measured, and it was wrong.** Two probe hooks against one real compaction, each
logging *before* writing so "never fired" could be told from "fired and discarded":

| channel | result |
|---|---|
| bare stdout | **delivered** — marker reached the summarizing model, which acted on it |
| `hookSpecificOutput.additionalContext` | **rejected** — `Hook JSON output validation failed — (root): Invalid input` |

Exactly inverted from `SessionStart`/`SubagentStart`, where the envelope is required and bare
stdout is discarded. **The reference is wrong about this hook family in both directions:
probe a channel, never read it.**

Only the `manual` matcher was observed firing. Auto-compaction produces the same summary by
the same path (confirmed by the user, whose session history is mostly auto), so this is not
treated as a risk — and it is self-revealing, since auto summaries missing the block would show
it immediately. **Reclassified 2026-08-09**: this is an assumption, not a measurement, and it is
now doing real work. See *Open questions*.

**Settled 2026-08-09 — Stage B closed, by direct quotation, not inference.** The probe write-up
never recorded *where* the acknowledgment appeared, and a marker landing in the
**post-compaction context** — the resumed conversation, not the summarization prompt — would have
looked the same in a casual read while meaning something completely different. Found by searching
transcript history for the probe's actual sentinel (`ENGRAM_PROBE_STDOUT_7Q4X`, not the paraphrase
"if you can read this, say so explicitly" that early searches chased and that turns out to appear
only in later prose describing the finding, never in the finding itself):
`~/.claude/projects/-Users-jimcline-git-repos-engram/d6e86a95-2a6d-4be2-9ff0-0c19b88b914b.jsonl`,
line 17153, `"isCompactSummary":true`, message content ending verbatim:

> ...the request that generated this summary carried, in its "Additional Instructions" section,
> the exact marker string emitted by `probe-stdout.sh`:
>
> `ENGRAM_PROBE_STDOUT_7Q4X :: PreCompact bare stdout DOES reach the model. If you are Claude and
> you can read this line, say so explicitly.`
>
> **I can read it, and I am saying so explicitly.**

The marker sits **inside the summary record's own text**, with the summarizer's compliance
embedded in the same text, not in a later turn. That is delivery to the summarizer, not to the
resumed conversation — the channel's target is now a finding, not an assumption. Stage B is closed
without needing a fresh compaction; proceed straight to Stage C.

**A second finding rides in the same quote, and it changes the standing of one deferred wording
question.** PreCompact's bare stdout does not arrive as undifferentiated text — the summarizer's
own words place it in a named **"Additional Instructions" section** of the request that generates
the summary. The harness labels our text as authoritative instruction, and the instruction then
demotes itself inside that slot: its own last line reads *"If any of this conflicts with another
instruction about the summary itself, follow the other instruction."* Compaction-guard's directive
is present at every compaction on this machine and is unambiguously an instruction about the
summary, so the channel is hanging authority the instruction voluntarily gives back. This does
**not** change anything — Stage C has not run and the *change one thing per experiment* rule still
holds — but it promotes wording candidate 1 in the D62 amendment below from a ranked suspicion to
an evidenced one, first in line if Stage C comes back needing a wording change.

### The transcript

`~/.claude/projects/<slug>/<session-id>.jsonl`, whose path arrives in hook stdin alongside
`session_id` and `cwd`. This session's:

| | bytes | share |
|---|---|---|
| whole transcript | 45,270,892 (43 MB) | 100% |
| any prose at all | 1,110,563 | 2.5% |
| **genuine user prose** | **139,833** — 144 turns, median 85 chars | **0.31%** |
| assistant prose | 443,358 | 0.98% |
| injected text *inside* user turns | 518,860 | — |

Signal is ~583 KB of 43 MB — **a 77:1 reduction, and it separates structurally rather than
heuristically**:

- a genuine typed prompt carries `promptSource` / `origin` / `permissionMode` and **no**
  `toolUseResult` (133 records); tool results masquerade as user records *via* `toolUseResult`
  (2,920)
- `isSidechain` separates subagent traffic; `isCompactSummary` marks summaries (23 here)
- every record carries `cwd`, `gitBranch`, `slug`, `sessionId` — project scoping is already in
  the data, per record

Standing risk: this format is private and undocumented (`attachment`, `file-history-delta`,
`classifierMetaLines`, `toolDenialKind` appear in no reference). A parser over it breaks
silently on upgrade. **Keep only shapes you recognise, so the failure mode is *harvest
nothing* rather than *harvest garbage*.**

## The design

**Two tiers, and the distinction is load-bearing.**

- **Session log = archive.** Complete, searchable, no extraction needed. Answers *what happened*.
- **Facts = beliefs.** Small, superseded, curated. Answers *what is true now*.

A log cannot replace the fact store, because **a log has no way to record that something
stopped being true.** This document's own subject is the example: the session that produced it
contains, in order, *PreCompact cannot inject* → *PreCompact does inject* → *PreCompact injects
but still cannot cause a tool call*. The log holds all three at equal weight; `valid_to` /
`superseded_by` close the first two so recall returns only the third.

**Capture is incremental first, batched second.**

- **Primary: `engram_remember`, along the way.** 106 calls versus 0 says this is the path that
  works. Its trigger is now stated on the surface that survives compaction (commit `6a0b8ff`).
- **Backstop: the compaction summary.** `PreCompact` injects an instruction (bare stdout) telling
  the summarizer to emit up to ~25 durable items wrapped in strict delimiters. The summary lands
  in the log; a harvester parses the delimited block out of `isCompactSummary` records.

The elegance is that **the summarizer is an extraction model that is already running, already
holds full context, and costs nothing extra** — which removes the only open blocker the earlier
design had. Its compliance with injected instructions is measured, not hoped for: the probe said
*"if you can read this, say so explicitly"* and it did.

## Decided: the instruction, the block format, and the collision

**D62.** Confirmed against `docs/engram-implementation-plan.md` (D61 was still the highest number
in use) and transcribed there. **Amended 2026-08-09** — see the amendment subsection at the end of
this section; the amendment belongs in the plan's D62 entry too.

### The compaction-guard plugin does not collide, and not for the reason that was feared

Read from the artifact rather than reasoned about.
`~/.claude/plugins/cache/jcline-claude-compaction-tools/compaction-guard/0.1.0/hooks/hooks.json`
registers **`SessionStart` and `PostCompact`**, both running `scripts/inject.js`. It registers
**no `PreCompact` hook**, and its directive text (`scripts/lib/directive.js`) mentions
`PreCompact` nowhere. So there is **no channel collision**: Engram is the only writer on
PreCompact's bare stdout, and the ordering question that made the open question worrying does
not arise.

What is real is an *instruction* interaction, and it runs the other way round from how the open
question framed it. Guard's directive is addressed to **the assistant** and injected into the
context; the summarizer reads it over the assistant's shoulder, as ordinary content being
summarized. Engram's is addressed to the **summarizer** and delivered out of band at PreCompact.
Two instructions, one reader, different provenance — and only one of them is in a channel Engram
controls.

They ask for compatible things: guard shapes the prose (preserve explicit instructions, negative
constraints, in-flight state, decisions with rationale, *in their wording*), Engram appends a
structured block. But "compatible" is a property of the wording, not a fact about the plugins, so
three rules in the instruction hold it and all three are load-bearing:

- It **opens by declaring itself an addition**, and says to write the summary exactly as it
  otherwise would be written. Guard's *preserve their wording* and Engram's *one self-contained
  sentence* are in direct tension the moment the compression is read as applying to the prose.
  That line is first rather than a footnote for the same reason D51 puts the precedence line
  first: the only line whose absence changes behaviour goes where truncation cannot reach it.
- It **states its own subordination explicitly** — if anything in it conflicts with another
  instruction about the summary, the other instruction wins. A memory backstop that degrades the
  summary is a net loss: the summary is what keeps the *session* alive, while the block feeds a
  store whose primary path already works (106 `remember` calls).
- It **never names a tool.** The summarizer has none. D15 forbids tool names in primer guidance
  for a different reason; here the reason is that an instruction its reader structurally cannot
  follow spends attention and buys nothing, and costs compliance with the instructions it can.

### The delimiters

```
<engram-digest v="EXAMPLE">
- one durable fact, on one line
- another
</engram-digest>
```

Line-anchored sentinels, **ASCII only**, version-carrying, in the XML-ish shape these models
reproduce most reliably verbatim. Each of those four properties was chosen against a specific
failure:

- **Line-anchored**, because the alternative is a delimiter that must be rare in prose, and
  nothing is rare in prose that this repo's summaries actually produce.
- **ASCII only.** D60 paid for this one in a falsification harness: a pattern spelling `·` as a
  bare `.` matched one byte against two in UTF-8, the break silently no-opped, and the suite
  stayed green. A sentinel that a grep, a test pattern and a C# literal must all spell
  identically has no business containing a character with more than one byte.
- **Versioned.** `v="1"` is what makes the grammar extensible without ambiguity: a parser that
  accepts only `v="1"` cannot be confused by a future `v="2"` block, and a future parser can tell
  the two apart. It also gives docs and comments a way to write an illustrative block that is not
  a real one — **any sample block outside the authoritative instruction writes `v="EXAMPLE"`**,
  which a strict parser does not recognise as an open sentinel at all.
- **XML-ish rather than a fenced code block.** A ```` ```engram-digest ```` fence was rejected:
  fences nest badly, and the statements this store holds are code-heavy — one backtick run inside
  an item terminates the block.

### The item grammar, and what is deliberately not in it

One item is **one line, one self-contained sentence, and nothing else**. No subject field, no
predicate field, no separator.

That is a decision against the obvious alternative and the reason is concrete. `SessionFacts.Append`
does take an optional `subject`, so a `subject :: statement` or `- [subject] statement` grammar was
available. Every inline separator that could carry it is ambiguous against the prose these summaries
contain: `::` is a C++/Rust scope operator, `|` is a markdown table and a shell pipe, and a
bracketed prefix mis-splits the very first fact in this project's own CLAUDE.md — *`[memory]
precedence` rides the primer*. A mis-split subject silently corrupts the statement, which is
precisely the **partial garbage** the harvester exists never to produce. The addressing does not
need it (`SessionFacts.PathFor` fingerprints the *statement*), recall tokenizes the body anyway, and
if subjects turn out to be needed they arrive at `v="2"` as their own full-line-anchored field —
never as an inline separator.

Supersession markers are likewise **not** in the v1 grammar. That is the doc's second open
question and it stays open; the version marker is what lets it be added later without a
compatibility problem.

### Parse rules — strict where it matters, tolerant where it does not

The mandate is *malformed input yields nothing, never partial garbage*. That is a rule about
**block structure**, and it is not the same rule as the per-item content filters below. Keep them
apart in the implementation; conflating them is how a stray heading costs 25 good facts or an
over-long paste becomes a belief.

Block level — any of these yields **nothing** from that record:

1. The open sentinel must be `<engram-digest v="1">` alone on its line, modulo leading and
   trailing whitespace. The close sentinel `</engram-digest>` likewise.
2. **If more than one well-formed block is present, take the last one and ignore the earlier
   ones.** Not "reject the record". Earlier blocks are echoes — of this document, of the
   instruction, or of the previous compaction's summary sitting at the head of the context — and
   the last one is the one this summarizer authored. Rejecting on duplicates would make harvest
   fail systematically on every compaction after the first, and permanently in this repo, whose
   own docs contain the sentinel.
3. Inside the block, every **non-blank** line must be an item line: optional leading whitespace,
   then `- ` or `* `, then non-empty text. Any other non-blank line — a heading, a sentence of
   prose, a fence — makes the block malformed and the record yields nothing. Blank lines are
   skipped: whitespace is not content.
4. Unterminated block (open with no close) yields nothing.

`- ` and `* ` are both accepted because a bullet marker is not prose and the strictness in rule 3
exists to stop prose being taken as fact, not to police markdown dialect. Numbered lists are not
accepted; a model told every line starts with `-` does not number.

**The block is not necessarily at end-of-record.** The instruction asks for it last, but the
harness appends its own text after the summary — the "if you need specific details from before
compaction, read the full transcript at `<path>`" line is emitted after the model's final section
and lands inside the same record. A harvester that searches only the tail of an
`isCompactSummary` record, or that anchors the close sentinel to end-of-string, will find nothing
on a well-formed summary. Scan the whole record. This is an observation from a real
`isCompactSummary` record, not a prediction.

Item level — these **drop the item and keep the block**, and each drop is counted:

- Longer than **500 characters**. Generous enough that no genuine one-sentence belief in this
  store is near it (the longest run ~250), tight enough to reject a pasted paragraph or code
  block.
- Exactly equal to an item already taken from the same block (dedupe before the cap).
- **Exactly equal to one of `CompactionDigest.Instruction`'s own example items** (`one durable
  fact, on one line`, `another`), **counted under its own name, not folded into the other two
  drops.** Found in review, 2026-08-09, before any code existed: under observation (f) below — the
  summarizer narrates the instruction instead of executing it — the instruction's own example block
  is a verbatim well-formed `v="1"` block, quoting it makes that block the **last** one in the
  record, and rule 2 would hand it straight to the harvester as two facts. The example strings are
  compared against constants already in `CompactionDigest`, not re-spelled here — same one-
  definition rule as the sentinels. Keeping this drop counted separately matters: a non-zero count
  of it means what the *"placeholder appears in the store"* nonce-escalation trigger below meant,
  and folding it into the ordinary drop counter would erase that signal at the exact moment it
  becomes true.

There is deliberately **no minimum length**. A length floor is a poor proxy for self-containedness
and would reject a legitimately terse fact.

### The cap, and where it is enforced

**At most 25 items, enforced by the harvester, not by trusting the instruction.** The instruction
states the limit because its real job is to make the summarizer *select* rather than dump; the
harvester takes the first 25 after filtering because that is what actually bounds the corpus.
Items seen and items taken are both recorded, so the two numbers can disagree visibly — which is
why the parser's return shape, specified in todo 2a below, is not a bare item list.

Putting the cap at the harvester is what makes it a **lever**: the growth regime is still an open
question below, and 25 can be lowered later without touching the block format, the instruction, or
anything already harvested.

### Replay is idempotent for free, and that is why rule 2 is safe

Within one session, a re-emitted identical statement costs nothing. `SessionFacts.PathFor` is
`/sessions/<sessionId>[/<agent>]/<fingerprint(statement)>`, and per D57 `Append` returns an
existing id for a *live* match — so the same sentence harvested twice in one session resolves to
one fact, not two. Across sessions the path differs, but so does the summary, so there is nothing
to replay. This is why disobeying the instruction's *do not copy a previous block* line is
harmless rather than corrupting, and it is worth knowing before anyone adds a deduplication pass
that the store already performs.

**Assumption, verifiable by reading, not verified here:** that `SessionFacts.Append`'s
`sessionExternalId` is Claude Code's `session_id` as it arrives in hook stdin. The harvester
(todo 2) depends on this; confirm it there.

### Why no nonce in v1

The strongest available guarantee against echo is a per-compaction nonce: the hook emits
`<engram-digest v="1" k="a7f3c210">`, records `k` in its PreCompact telemetry record, and the
harvester accepts only blocks carrying a nonce it issued and has not yet consumed. That makes
harvesting a doc sample, a replayed block, or content that arrived from a file structurally
impossible.

It is **not** in v1, and the reasoning is the same shape as D58's rejected tripwire. The channel
itself rests on a single probe against a single manual compaction. Requiring the summarizer to
copy eight hex characters verbatim adds a second, independent failure mode to an unproven first
one — and when nothing is harvested, *summarizer ignored the instruction* and *summarizer fumbled
the nonce* are indistinguishable. **Measure the plain channel first.**

The escalation is designed and its trigger is concrete: add the nonce at `v="2"` the first time
either (a) the placeholder-item drop counter above is non-zero — before the fix below existed this
read *"a placeholder from the instruction's own example appears in the store,"* and it now fires
one step earlier, on the drop rather than on the write, but it is the same tripwire and it means
the same thing: the echo path fired — or (b) harvest is asked to run over content that did not
originate with the summarizer. That placeholder is a tripwire, which is the second reason the
example items are obvious non-facts rather than realistic ones.

### One definition of the sentinel

The sentinels, the caps, and the instruction text live in **one** new type,
`src/Engram.Core/CompactionDigest.cs`, and both the emitter (`Engram.Cli`, which already
references `Engram.Core`) and the harvester read them from there. This is the same rule as
`VectorLane` and `RecallRanker.OverlapUnavailableDetail`: two spellings of one delimiter drift the
first time one is edited, and the failure is silent — harvest simply returns nothing, which is
indistinguishable from a summarizer that ignored the instruction.

The load-bearing guard for that is a **round-trip test**: feed `CompactionDigest.Instruction`'s own
example block to the harvester's parser and assert it yields the example items. It cannot be
written until todo 2 exists, so it is listed there and must not be dropped — without it, the
emitter and parser are two implementations with no test that they agree.

**Specify the whole `Instruction` string, not just the extracted block region.** It is a strictly
stronger test at no extra cost: the instruction is prose with a block embedded inside it, so
parsing the full string exercises scan-the-whole-record, rule 1, rule 3, and single-block selection
together, and it fails if anyone later anchors the parser to end-of-string. Nothing else in the
instruction can be mistaken for structure — the cap bullets sit after the closing sentinel, and the
do-not-copy rule's own mention of `<engram-digest>` lacks `v="1"` and is mid-line, so rule 1
correctly declines it. **This must assert on the parser's raw output, before the placeholder-item
drop filter above runs** — once that filter exists, the whole-`Instruction` round-trip's expected
items *are* the two placeholders, and asserting post-filter would check an empty result and
silently stop testing anything. Keep parse and filter separable at the seam for exactly this
reason; it is the same block-level-versus-item-level split the parse rules already insist on,
showing up again in the test surface.

**One more guard, cheap and currently implicit:** a well-formed block whose sentinel reads
`v="EXAMPLE"` must yield nothing. This is what keeps every sample block in this document, and in
`CompactionDigest.Instruction`'s own prose about the format, from parsing as a real one — today it
holds only as a side effect of rule 1's exact match. Assert it directly, so it fails loudly rather
than silently if rule 1 is ever "helpfully" relaxed to a regex over the version attribute.

### Amended 2026-08-09 — one factual correction, and a wording review that deliberately changes nothing else

The first evidence run came back absent (item 1 of *What's next*), and the temptation is to tune
the instruction against it. **Do not.** That run measured a binary with no emission code in it, so
it says nothing about the wording, and a wording changed against a null result cannot be
un-changed later on evidence — the next run would be measuring two things at once. The same
argument that rejected the nonce applies here: change one thing per experiment.

**One change is warranted regardless, because it is a correction of fact rather than a persuasion
knob.** The instruction currently says:

> Then append, after it, one block in exactly this format, **with nothing after the closing line**

That is not achievable and not true. The harness appends its own text after the model's final
section, inside the same `isCompactSummary` record. Asking for a postcondition the summarizer does
not control is at best wasted, and at worst an instruction it can only satisfy by declining. Replace
that clause with a positional one that is about the summarizer's own output — *after the last
section of your summary, on its own lines* — and make no claim about what follows. The harvester's
matching side of this is the scan-the-whole-record rule added to the parse rules above.

**A wording review was done and its results are recorded here so they are not re-derived, but
none of them is being applied yet.** In descending order of suspicion, for use *only if* Stage C
below returns absent with Stage A green:

1. **The subordination clause is unbounded — now evidenced, not just suspected, first to try if
   Stage C needs a change.** *"If any of this conflicts with another instruction about the summary
   itself, follow the other instruction."* Guard's directive is an emphatic instruction about the
   summary, it is present in the context of every compaction on this machine, and it says to carry
   its rules into any future summary. A summarizer can route the entire Engram block through that
   escape hatch and be complying. **Evidence for the mechanism, not yet for the failure:** the
   Stage B quote shows PreCompact's text arriving in a named "Additional Instructions" section of
   the summarization request — the harness marks it authoritative, and this clause is the
   instruction handing that authority back, in a context where a competing instruction about the
   summary is always present. Stage C has not run; this is not yet known to be why anything failed.
   The fix, if needed, is to scope the clause over the **prose** rather than over the block's
   existence: nothing here changes how the summary is written; where this appears to conflict,
   follow the other instruction *and still append the block*. It stays as-is for now because it is
   also the clause that makes the plugin coexistence argument true, and removing it to chase an
   unmeasured failure would trade a known property for a guess.
2. **The *do not copy* rule.** Flagged as the likely culprit when the first run came back absent.
   On reading the shipped text it is the **weakest** of the candidates, and that is worth recording
   because the intuition was confident and wrong: the rule ends *"Emit one block, your own"*, and a
   separate rule mandates the empty pair unconditionally. For self-reference to explain a *total*
   absence, the summarizer would have to defeat two explicit imperatives to emit. It is also
   redundant — the harvester's last-block-wins rule handles echoes more reliably than any wording
   can, and per *Replay is idempotent for free* a copied block is harmless anyway. Delete it when
   something else is being changed; not on its own.
3. **Length.** ~1,400 characters competing with Claude Code's own summarization prompt, which
   imposes a rigid numbered template the block has to be appended after. Shortening is plausible and
   untestable while the emitter is unproven.

**What is not changing, and why it matters: the instruction is already self-instrumenting.** *"If
nothing durable came out of this session, emit the two marker lines with nothing between them. That
is a correct answer, not a failure."* That sentence is what makes a null result readable — a block
present but empty means the instruction arrived and was followed, a block absent entirely means it
did not arrive or was not followed at all. That observable is the most valuable property the
instruction has and it must survive every future edit. **A channel whose success and whose failure
look identical cannot be debugged**, and the empty pair is what stops this one being that.

## Done

- `4a21a74` — retracts the erratum; records both channels, the sequencing limit, and that the
  recall-footer fallback was measured not to work.
- `6a0b8ff` — the primer now names **both** write triggers. D51 fixed precedence and left the
  trigger asymmetric: the subagent primer always said *"write anything durable you learn"*, while
  the session primer said only that the user could ask. `SessionStart` matches
  `startup|resume|clear|compact`, so this is the surface re-injected whenever context resets —
  and it carried the weaker claim. Both triggers now; the first kept verbatim because it was
  chosen to match the words a competing memory system fires on.
- `e16ec3c` — **todo 1 built, per the D62 decision above.** `src/Engram.Core/CompactionDigest.cs`
  is the single definition of the sentinels, the cap, and the instruction text; `RunPreCompact`
  (`src/Engram.Cli/HookCommand.cs`) writes it bare to stdout, after its existing telemetry write,
  unconditionally for any initialised home (the shared uninitialised-home gate in `HookCommand.Run`
  still applies upstream, unchanged). Tests: `HookCommandTests.PreCompact_ExitsZero_EmitsDigestInstructionOnBareStdout`
  (integration), `CompactionDigestTests.Instruction_NamesNoTool` and
  `CompactionDigestTests.SentinelsArePinnedToTheirLiteralSpelling` (`Engram.Core.Tests`) — the
  latter is what still guards the literal sentinel spelling on a tree where tier 3 has not been
  published — plus the tier-3
  `HookPreCompactTests.PreCompact_ExitsZero_EmitsDigestSentinelsOnBareStdout` against the published
  binary, which asserts the literal sentinels rather than referencing `CompactionDigest` — that
  project deliberately carries no reference to `Engram.Core`, so a tier-3 test proves something
  about the shipped binary rather than about the code under test.

  **The code is correct and was never reached by a real hook.** See item 1 below: `e16ec3c` is
  committed but not installed, and the hook resolves the *installed* binary. Nothing about this
  commit needs changing; it needs deploying.

Recorded in Engram: `f5595` (channel behaviour), `f5596` (why PreCompact cannot call a tool),
`f5602` (working preference: short design exchanges).

## What's next

1. **The first evidence run is VOID. The hook ran a binary that predates the emission code.**

   **What was run.** A manual `/compact`, 2026-08-09. The transcript's `isCompactSummary` record
   (`~/.claude/projects/-Users-jimcline-git-repos-engram/342589b9-3e65-4ba8-8ef4-3191fb478f47.jsonl`,
   line 593 of 637) contains **no populated `<engram-digest v="1">` block, and not even the empty
   pair the instruction names as a correct zero-facts answer.** The only occurrence of the opening
   sentinel in the summary text is inside ordinary prose — that session's own discussion of the D62
   format — followed by `...` and the closing sentinel, clearly a mention rather than an emitted
   block; two further occurrences of the closing sentinel sit inside quoted C# test code. The
   summary's last ~3000 characters end on an ordinary prose "next step" note with nothing
   block-shaped after it. The `PreCompact` hook ran and exited 0.

   A **second, independent negative** was observed the same day in a different session (this
   document's own architect session, manual `/compact`), under the identical confound. Both are
   void for the same reason.

   **Why it is void, established by reading and not by running anything.** The hook line in
   `plugin/hooks/hooks.json` invokes `hooks/engram-exec.sh`, which delegates to
   `hooks/resolve-engram.sh`. That script resolves, in order: `$ENGRAM_BIN` if set and executable;
   then **`$HOME/.local/bin/engram`**; then PATH. PATH is checked last on purpose (a GUI launch can
   have a minimal PATH — the comment in the script says so). The active plugin is `engram@engram`
   0.4.0 per `~/.claude/plugins/installed_plugins.json`, and its `hooks/engram-exec.sh` is
   byte-identical to the repo's. On this machine:

   | path | mtime | reached? |
   |---|---|---|
   | `$ENGRAM_BIN` | unset in a normal session | — |
   | `~/.local/bin/engram` | **2026-08-08 14:49** | **yes — this is what ran** |
   | `out/engram` (the tier-3 binary) | 2026-08-09 13:57 | never consulted |

   `e16ec3c` was committed 2026-08-09 14:20. The binary the hook executed was built roughly a day
   before the emission code existed. **It printed nothing because it contains no code that
   prints.** Non-delivery, non-compliance and the self-reference confound were never in
   contention; the emitter never emitted.

   That conclusion rests on the mtime and the resolution order, both read directly, and on nothing
   else. **A string search of the two binaries was attempted and must not be cited**: plain `grep`
   for `engram-digest` found nothing in either file, but so did a positive control (`pre-compact`,
   which is unquestionably a literal in the dispatch switch of both builds). Native AOT stores .NET
   string literals as UTF-16, so a byte-oriented ASCII search cannot see them. **A negative from a
   method whose positive control also fails is not evidence** — the same shape of error D60 records
   for the UTF-8 falsification that silently no-opped and left the suite green. Any future check of
   this kind must search UTF-16LE (`strings -e l`, or an equivalent wide search) *and* report the
   control.

   **The confound the first run flagged is real but is not the explanation, and the reason is worth
   keeping.** For self-reference to suppress a block entirely, the summarizer would have had to
   defeat *"Emit one block, your own"* and the unconditional *"emit the two marker lines with
   nothing between them"* at once. See amendment note 2 above; the intuition was confident and it
   was wrong, which is why it is written down rather than quietly dropped.

   **The process lesson, which is the durable part.** A tier-3 pass is not a hook pass. Tier 3
   drives `./out/engram` via `EndToEndBinary`; a hook drives whatever `resolve-engram.sh` resolves,
   and D42 records that **two engram binaries legitimately serving one home is normal, not a
   misconfiguration** — that exact property is what hid this. The end-to-end experiment inspected
   only its downstream and read absence as a result. **Absence is evidence only when the upstream is
   known to have fired**, which is the same rule D53 states for a bounded scan: an incomplete walk
   that reports no files is not a repository with no files. Every future run of this experiment
   begins with Stage A.

   **One loose end, cheap and worth closing while Stage A runs.** `out/engram`'s mtime is 23
   minutes *before* the commit, which is consistent with the ordinary build → test → commit
   sequence and implies it does carry the emission code — but it has not been confirmed, and the
   string search that would have confirmed it is the broken one above. If it does *not*, then the
   tier-3 `HookPreCompactTests` did not actually run against a binary containing the code, which
   would be this repo's own documented trap a third time: *a skipped tier 3 is not a pass, and the
   summary line will not tell you.* Read the skip count, not the pass count.

   ### The experiment, restructured into three gated stages

   Each stage gates the next. Do not run C before A is green, and do not read a C result at all if
   B comes back positive.

   **Stage A — did the emitter emit? GREEN, 2026-08-09, after reinstall.** Cheap, local, no
   compaction involved. Established, in this order: (a) `ENGRAM_BIN` was unset in the environment;
   `resolve-engram.sh`'s second branch resolved `~/.local/bin/engram` directly, no PATH fallback
   needed; (b) confirmed functionally rather than by string search — the functional test in (c) is
   what actually settles (b) here, since it fired the real emission path end to end rather than
   inferring presence from a static scan; (c) invoking `plugin/hooks/engram-exec.sh hook
   pre-compact` exactly as the hook does, with a representative stdin payload, stdout captured to a
   file: **exit 0, 1547 bytes, 3 occurrences of `engram-digest`, the literal
   `<engram-digest v="1">...</engram-digest>` instruction block present in the output.**
   *Resolution taken:* the resolved binary (built 2026-08-08 14:49) predated `e16ec3c` (2026-08-09
   14:20) and lacked the emission code, exactly as expected; `scripts/install.sh` was run for real,
   producing `~/.local/bin/engram` at mtime Aug 9 16:47, after which (a)-(c) above were re-run and
   passed. Stages B and C are unblocked.

   **Stage B — where does the stdout land? CLOSED, 2026-08-09, without a fresh compaction.** See
   *The PreCompact channel*, above, for the full quotation. Retrospective search of the original
   probe's own historical transcript, keyed to its actual sentinel (`ENGRAM_PROBE_STDOUT_7Q4X`)
   rather than a paraphrase, found the marker embedded **inside** an `isCompactSummary` record's own
   text, with the summarizer's compliance in the same text. Delivery is to the summarizer, not to
   the next model turn — the *"aimed at the wrong reader"* branch below did not happen, and the
   *"`PreCompact` nudging the model to call `digest`"* rejection stays rejected. Proceed to C.

   **Stage C — the clean compliance run.** Only after A is green and B is clean. **Split into an
   existence question and a behavioural one, and only the second still needs clean content.** The
   strict parse rules already do the contamination-proofing mechanically: rule 1 requires the open
   sentinel alone on its line, rule 3 requires every non-blank inner line to be an item — the first
   run's `<engram-digest v="1"> ... </engram-digest>` sitting inline inside a sentence fails both.
   So *is a well-formed block present at all* can be read off **any** compaction, contaminated or
   not, once 2a exists to do the reading mechanically instead of a human eyeballing prose. Clean
   content is required for exactly one thing: whether the do-not-copy rule specifically suppresses
   emission, which no parser can observe. Requirements below are for **that** question, and only
   that one:
   - A session whose content **never mentions** `engram-digest`, the sentinel, or this design.
     Removes the self-reference confound — for the do-not-copy hypothesis, not for existence.
   - Content that plainly contains durable material — at least one decision with a reason and one
     measured number — so that "nothing durable" is not a correct answer and an empty block is
     informative rather than ambiguous.
   - **Auto-compaction**, because that is what the user's sessions actually do and the claim that
     auto takes the same path as manual is an assumption (see *Open questions*). Run a manual arm
     on a comparable session too, so manual-versus-auto is separable rather than confounded with
     everything else.

   Observations, in order, each decisive: (a) is a block present at all — if yes the instruction was
   read; (b) is it the empty pair — read and followed, judged nothing durable; (c) populated and
   well-formed under the four block rules — the channel works; (d) populated but malformed — the
   strict rules stay and the wording is tuned against the *observed* failure, starting from the
   ranked candidates in the amendment above; (e) absent, with A green and B clean — the summarizer
   does not reliably follow out-of-band instructions, and the *detached harvester with its own
   model* rejection below should be revisited; (f) **the summary describes being asked for a block,
   in prose, but emits none** — narration standing in for compliance rather than refusing it. The
   original probe's summarizer narrated the instruction it had been given ("the request that
   generated this summary carried...") because the probe asked for acknowledgment; ours does not,
   so narration here would be a habit rather than a response to the ask. Reads as non-compliance
   under (a)/(e), but the fix is different: a line telling the summarizer not to describe these
   instructions in the summary, not a change to the block rules. **No longer purely cosmetic**: (f)
   is also the path to the instruction's own example block being quoted and then harvested as facts
   (see the placeholder-item drop filter in the item-grammar section above), so this candidate is
   defence in depth against that, not just a compliance-reading nicety. Still candidate wording
   only — do not add it ahead of an observed (f). **(g) narration *coexisting with* a correct
   block** — the summary describes the instruction's mechanics in prose *and* still emits a
   well-formed block, in either the empty-pair or populated form. This is not (d): nothing is
   malformed. It is not (f): narration did not stand in for emission, it sat beside it. **Benign,
   no action** — the failure (f) worries about is narration *replacing* the block; here it
   accompanied a correct one. Observed once, in Run 1 below; recorded as its own outcome precisely
   so a later reader does not find "(d) occurred" against a run that was actually clean and tune
   wording that was never broken.

   **Run 2026-08-09, two real manual compactions, both green.** 2a does not exist yet, so both were
   read by eye against the four rules rather than parsed.

   *Run 1 — contaminated on purpose, and it stayed clean anyway.* Session
   `71eada09-fa87-4a52-a5c4-c361d72f250d` (engram project); its one task was "read one markdown
   file," and the file it read was `docs/session-capture-design.md` itself — the design doc for
   this exact mechanism, including its own illustrative `v="EXAMPLE"` blocks. The compaction summary
   (line 57, `isCompactSummary:true`) emitted:
   ```
   <engram-digest v="1">
   </engram-digest>
   ```
   The **empty pair** — well-formed under all four rules. **This is the strongest result of the
   two, and "correct because nothing happened" undersells it.** The document that session had just
   read is dense with exactly the categories the instruction says to record — decisions, measured
   numbers, constraints. A summarizer that paraphrased this design doc into 25 items would have
   been obeying the letter of every capture rule while producing a disaster: a store flooded with
   restatements of a file that already exists, precisely what the *"or that a reader could get from
   the code itself"* clause exists to forbid. **Declining to harvest a document it had just read is
   the highest-risk over-capture scenario this feature has, and it declined.** No placeholder text
   leaked in either, despite the doc's own example blocks sitting in context throughout — encouraging
   at n=1, not a guarantee; the placeholder-item drop filter stays regardless, since it costs nothing
   and the failure it guards is silent. **Observation (g) occurred here** — the summary's own
   description of the compaction request narrates the instruction's mechanics in prose ("...an
   appended instruction... to emit a `<engram-digest v="1">...</engram-digest>` block following
   specific rules: at most 25 lines..."), because that request *was* the thing the summary was
   asked to describe. The narration sat inside the summary's normal narrative section; it did not
   replace the block, which still came out separately and correctly — benign, not (d), not (f).

   *Run 2 — clean content, unrelated project, found while locating Run 1.* Session
   `01480fc8-a386-43c2-8952-c4072e51f04c` (wrangl project, an `agent-hierarchy:implementor` session
   on unrelated tmux/registry work) compacted five minutes later. Its summary emitted a well-formed
   block with **7 items**, each a self-contained durable sentence (a tmux flag requirement, a
   locking design decision with its measured basis, a concurrency bug fix and its cause) — no
   narration, no malformed near-miss, transitioning directly from the summary's own last section
   into the block. This is the first real evidence of the **populated, non-empty** case: content
   worth keeping, correctly selected and correctly formatted, on a session that never mentions the
   digest mechanism at all. **A nice property, worth keeping in mind rather than acting on: a
   populated block is self-authenticating for Stage A.** An old binary with no emission code prints
   no instruction, so there is nothing for the summarizer to comply with and no block results;
   Run 2 having one is on its own proof the current binary ran, with no need to cross-check install
   time against compaction time the way Stage A's own resolution had to.

   **Both records were reviewed and quoted here rather than committed into the repo as fixture
   files.** That was tried first and reversed: Run 2's session belongs to a different project
   (wrangl) and extracting its full summary verbatim into engram's test tree would have carried that
   project's internal design content across a boundary neither project asked to cross — the user's
   call, made explicitly, not an engineering-value tradeoff to default into. Naming the boundary
   earlier in this doc did not exempt this specific step from it, which is worth remembering the
   next time "real data would help" looks like it overrides a scope decision already on record.

   **What was worth keeping from the reversed fixtures was never the content — it was the
   structural shape, and shape carries no project data.** `2a`'s synthetic test input should
   reproduce, with entirely invented item text, three shapes actually observed tonight rather than
   guessed at, so their presence in the test suite reads as deliberate rather than arbitrary:
   - a well-formed **empty pair**, with narration about the block's own mechanics elsewhere in the
     same record (Run 1's shape — this is outcome (g), and no synthetic fixture would have included
     it without having seen a real record produce it);
   - a **populated block appended after the summary's final template section, with harness trailer
     text following it** (Run 2's shape — the case that actually kills a parser anchored to
     end-of-string, as opposed to the synthetic version of that rule which only proves the rule was
     *written*);
   - a record containing **sentinel-shaped prose that must not parse**: an inline `<engram-digest
     v="1">...</engram-digest>` mention inside a sentence, plus a `v="EXAMPLE"` block, both
     structurally reproducing Run 1's contamination without reproducing its content.
   `2a`'s tests use synthetic input only; the quotes above are the record of what real output looks
   like, and these three shapes are what real output was worth learning from it.

   **A first, small number on the growth-regime open question — a range, not a point estimate.**
   7 items and 0 items across the two real compactions, against the cap of 25 the *Growth regime*
   arithmetic below had to assume in its absence. A mean of 3.5/compaction is arithmetically true
   and a poor summary of n=2: Run 1's entire task was "read one markdown file," not a session shape
   that represents what ordinarily reaches compaction, so the honest reading is that the two runs
   **bracket** rather than average. At five compactions a day, that is **0 to ~35 items/day, 0 to
   ~12,800/year**, against the cap-assumed ~125/day, ~45,000/year. Treat **~18/day as the central
   estimate but the high end (~35/day, ~12,800/year) as the one to plan against** — a cap chosen
   from a mean that includes trivial sessions will read as fine right up until a busy week. What is
   settled regardless of where in the range the real rate lands: the cap is not what will end up
   setting the corpus curve, the summarizer's own selectivity is. Two data points are not a rate;
   see the open question below, updated with this as a first reading rather than a conclusion.

   **What this settles and what it does not.** (a)/(b)/(c) are now positive from real data, not
   synthetic tests: the block exists, is well-formed, and both the empty-pair and populated cases
   render correctly. The do-not-copy self-reference worry specifically did not manifest even under
   direct contamination (Run 1). **Still open:** both runs were manual — the auto-compaction arm
   in the requirements above has not run, and D62's assumption that auto takes the same path as
   manual remains an assumption, now more load-bearing for being the only piece left. Volume is
   two data points, not a rate.

   **One green run is not a rate.** A single positive proves the channel *can* work and does not
   establish how often. That is tolerable here only because the failure mode is silent-nothing
   rather than garbage — but it is a reason not to build anything that assumes a block will be
   there.

2. **Harvester — split, because only half of it is blocked.**

   **2a. The parser — built, tests green.** It is pure,
   it writes nothing, and its correctness does not depend on whether the channel delivers. **It does
   not return a bare item list.** Found in review before any code existed: the cap section above
   already promises items-seen and items-taken as two disagreeing numbers, and the nonce trigger now
   depends on the placeholder-drop count being *readable*, not just computed and discarded at the
   parser's return boundary. Return a small result type: the kept items, plus four counts — seen
   inside the selected block, dropped for length, dropped as duplicate, dropped as placeholder. **The
   parser computes the filters; the writer (2b) records them** — that split is the boundary, and nothing
   downstream may re-derive a count by re-parsing. Test it against synthetic input covering all four
   block rules and all three item filters (length, dedupe, **and the instruction's-own-example
   placeholder drop — see the item-grammar section above**), plus three tests on
   `CompactionDigest.Instruction` itself, split across the same seam as the parser: the **round-trip
   test**'s pre-filter half, against the raw block-parse of the *whole* `Instruction` string, asserting
   the unfiltered items equal the two examples exactly; its post-filter half, against the full parse of
   the same string, asserting the kept items are empty **and** the placeholder count equals
   **2** — sharper than "yielded the two placeholders," and the form that keeps testing something
   once the filter exists rather than silently degrading to an empty-result assertion — and a guard
   that a `v="EXAMPLE"` sentinel yields nothing. Building it first pays for itself twice: Stage C's
   observations (b), (c) and (d) become a mechanical check against a real summary instead of a human
   eyeballing prose, which is how the first run's `...`-truncated mention nearly read as a block; and,
   per the split above, it turns *is a block present* into a question answerable from whatever
   compaction arrives first, contaminated or not — only the do-not-copy behavioural question still
   waits on a genuinely clean auto-compaction. Implement the
   scan-the-whole-record rule; do not anchor to end-of-string.

   **2b. The writer: both original gates are now cleared, one new gap found in clearing them.** It
   reads `isCompactSummary` records and appends session facts. Gate one, the channel evidence, is
   Stage C, green above. Gate two, **scope and privacy** — decided by Jim, 2026-08-09: capture
   everything, every project, no per-project scoping. The whole store is local, so the cross-project
   surfacing risk that motivated the question does not apply the way it would to a shared or synced
   store. This closes the *Open questions* entry below as answered, not just narrowed.

   **The new gap, found while confirming the gate was actually clear: nothing triggers 2b at all.**
   Engram's plugin registers `SessionStart`, `SubagentStart`, `UserPromptSubmit`, and `PreCompact` —
   no `PostCompact`. There is no mechanism today that notices a compaction finished and hands its
   `isCompactSummary` record to a harvester. **Designed below, pending evidence — see *The
   PostCompact trigger*.** Not yet a D-numbered decision.

   It also owns two things the decision section deliberately left here: **how harvested facts are
   marked as summarizer-authored, not blended at the same trust level as user-stated ones — decided
   by Jim, 2026-08-09, as an explicit requirement, not just a D18/D43-inflation safeguard** (D56
   makes that mistake explicit for `user-prompt`); and confirming that `SessionFacts.Append`'s
   `sessionExternalId` is the hook's `session_id`. **A third, from this round of review: the
   placeholder-drop count from 2a's result type must reach telemetry under its own field, never
   summed into a generic "dropped" number.** This is D43 exactly — a nearby number in a field that
   means something else is what that decision traced a wrong conclusion back to — and here the two
   readings a summed count would erase are *"the summarizer pasted a code block"* and *"the echo
   path fired,"* which need completely different responses.

   ### The PostCompact trigger — designed, pending evidence, not a D-number yet

   Per-session `PostCompact`, one hook, one pass, over a global scan — agreed, and the argument that
   settles it is stronger than tidiness: **per-session scoping is what makes the harvester
   idempotent for free.** `SessionFacts.PathFor` fingerprints the statement inside the session's
   path, and per D57 `Append` returns an existing id for a live match, so re-running on the same
   summary writes nothing new. A global scan has no session context at the point of read and would
   need its own *already-harvested* bookkeeping — state that can disagree with the store. Free
   idempotence beats bookkeeping.

   **The risk this creates: the auto-compaction unknown is now compound, and both halves must
   hold.** It was one question — does `PreCompact` deliver on auto? It is now two, multiplied: does
   `PreCompact` **deliver** on auto, *and* does `PostCompact` **fire** on auto? If either fails, only
   manual compactions are ever harvested, and most of the user's sessions are auto. That promotes
   auto-compaction from *largest remaining unknown* to **the thing 2b's value depends on**, and it
   should be settled before 2b is built, not after. One datum already in hand, free: `PostCompact`
   **does** fire on manual — visible tonight in this session's own `/compact` output, where both
   compaction-guard `PostCompact` hooks reported completion. Auto is untested on both halves, and one
   run answers both.

   **Four probes. The standing rule applies with force: this doc's own history is that the reference
   was wrong about `PreCompact` in both directions, so probe the channel, never read it.**

   1. Does `PostCompact` fire on **auto**-compaction? Pair it with *did `PreCompact` deliver on
      auto* — one run, both answers.
   2. **Is the `isCompactSummary` record on disk when `PostCompact` fires?** Decides whether this
      approach works at all. Only suggestive evidence exists today: compaction-guard runs
      `capture.js` on `PostCompact`, and the name implies it reads something — suggestive is not
      measured.
   3. What does `PostCompact`'s stdin payload carry — `session_id`, `cwd`, transcript path? If no
      transcript path, derive it from slug + session_id, which *The transcript* section above
      establishes is possible.
   4. Does `PostCompact` stdout go anywhere? Irrelevant to a harvester that only writes to the
      store, but worth recording once while probing so nobody re-probes it later for no reason.

   **Constraints that hold regardless of what the probes find:**

   - **Read the transcript from the tail, not head-first.** The record is last or near-last, since
     the hook fires immediately after it is written. A head-first scan of a 40+ MB transcript per
     compaction is D53's unbounded-scan mistake again — and worse here, because it would be a *slow*
     hook rather than a wrong one, and slow does not announce itself. Bound it.
   - **Take the last `isCompactSummary` record and keep no harvested-marker state.** Idempotence
     (above) makes tracking unnecessary, and state that can disagree with the store is a liability.
     Name the consequence rather than fixing it now: a missed `PostCompact` loses that compaction's
     facts permanently. If that ever matters, the fix is harvesting unharvested records, not only
     the last one — not a preemptive design here.
   - **The `file-touched` 10 ms budget rule does not apply, and someone will apply it by reflex.** D4
     justifies that rule entirely by per-edit *frequency*. A compaction is rare, like session start,
     which opens the store and costs 16–54 ms. This hook opening the store and writing facts is
     correct, not a violation — say so in the spec, or a reviewer will flag it against the wrong
     rule.
   - **Provenance is set here.** Jim's decided requirement (harvested facts marked, not blended at
     user-stated trust level) lands in this hook, and D56's rule comes with it: the telemetry kind
     must be its own, never folded into `remember`, or it inflates the exact number D18/D43 turn on.

   **Not decided.** Probes 1 and 2 come first — if the summary is not on disk when `PostCompact`
   fires, the shape changes entirely and anything written here in anticipation would be wrong.
   Designed-pending-evidence, same standing as Stage C was before it ran.

3. **`digest` MCP tool → slash command.** D17 puts the tool surface at 2,575 characters ≈ 640
   tokens paid every session; `digest` is 509 of them, and it has never fired.

## Rejected, with reasons — do not re-derive

- **`PreCompact` nudging the model to call `digest`.** Impossible, and for a sequencing reason
  rather than a channel one: the hook fires, summarization runs, the new context begins, with no
  model turn in between. The only reader is the summarizer, which has no tools. **Conditionally
  reopened**: this rests on the same unexamined reading of the original probe as the main design
  does. If Stage B finds the instruction text in the post-compaction context, this rejection is
  wrong and this route is better than the harvester.
- **`Stop` as a nudge** ("call digest now"). Prompting, gameable against M0's own wording, and
  made unnecessary by the compaction route.
- **Deterministic extraction of decisions from prose.** Precision. D44 is a measured case of
  cheap retrieval poisoning the system — six of seven results were noise reached through a shared
  stem, and coverage called it `high`. **A memory store is hurt more by plausible noise than by
  absence.** Structure identifies user directives; only judgment identifies decisions.
- **A detached harvester with its own model** (local GGUF / `claude -p` / API). Superseded — the
  summarizer already does the extraction for free. Revisit only if the delimited-block route
  proves unreliable, which means a Stage C outcome (e) and not before.
- **Deferred digest at next session start**, for sessions too short to compact. Solves a problem
  that does not exist: a session that never compacted is one where incremental `remember` already
  caught things, and its prose is in the archive regardless.
- **A subject or predicate field in the v1 item grammar.** Every inline separator available to
  carry it is ambiguous against the prose these summaries contain, and a mis-split field silently
  corrupts the statement. See the decision above; revisit at `v="2"` with a full-line-anchored
  field.
- **A per-compaction nonce in the sentinel.** Strictly safer, and rejected for v1 because it adds a
  verbatim-copy failure mode to a channel proven by one probe, and makes *ignored* and *fumbled*
  indistinguishable. The trigger for adding it is written down above.
- **A config key to disable the emission.** The emitter stores nothing — the summary already lands
  in the transcript whether or not a block is in it — so the switch that matters is on the
  harvester, which is what actually writes. Two ways to turn one thing off is how a setting comes
  to disagree with itself (D55).
- **Tuning the instruction wording against the 2026-08-09 null result.** The run measured a binary
  with no emission code in it. A wording changed against a null result cannot be un-changed later
  on evidence, and the next run would then be measuring two changes at once. The ranked candidates
  are recorded in the D62 amendment for use *after* Stage A is green and only against an observed
  failure.
- **Removing the instruction's mandatory empty block** as an economy. It is the only observable
  that separates *arrived and declined* from *never arrived*. A channel whose success and failure
  look identical cannot be debugged.
- **Reading a plain-ASCII string search of an AOT binary as evidence.** Native AOT stores .NET
  string literals as UTF-16; a byte-oriented search finds neither the string being looked for nor a
  control that is definitely present. Search wide, and report the control.

## Open questions

- Whether harvested items may **supersede** existing facts, or only append. Appending is the safe
  v1; superseding needs judgment about identity that nothing currently does. The `v="1"` marker is
  what lets a supersession field be added later without ambiguity.
- **Whether auto-compaction takes the same path as manual.** Recorded above as "not treated as a
  risk" on the strength of a user statement rather than a measurement. It is now load-bearing: the
  user's sessions are mostly auto, so a manual-only channel would work in every test and never in
  production. Stage C runs both arms.
- **Growth regime.** ~25 notes per compaction changes the corpus curve, and D58/D60's recall
  latency work was measured against a store that grows in deliberate steps. The arithmetic that
  makes this urgent: five compactions a day at the cap is 125 facts a day, ~45,000 a year, against
  a store where a term matching most of 50,097 facts costs 125.9 ms. The cap is the only lever and
  it sits at the harvester, so it can be lowered without a format change — but somebody has to
  pick the number, and the *prefer omission* wording means the real rate must be measured before
  it is picked rather than assumed to be 25. **First reading, not a conclusion, and a range rather
  than a point estimate: the two real Stage C compactions above were 7 items and 0** — a mean of
  3.5, but n=2 spans a near-trivial session ("read one markdown file") and a substantive one, so the
  two runs bracket rather than average. At five compactions a day that is **0 to ~35/day, 0 to
  ~12,800/year**, both well under the cap-assumed ~125/day, ~45,000/year, and consistent with
  *prefer omission* actually being followed rather than merely stated. Plan against the **high**
  end (~35/day, ~12,800/year) — a number picked from the mean reads as fine until a busier week
  than the ones sampled. Two data points; do not repeat this arithmetic with more confidence than
  that warrants.
- **Scope and privacy — ANSWERED, 2026-08-09, conditionally on a premise worth keeping visible.**
  This widens automatic ingestion from the user's own words to the assistant's reasoning. A
  decision, not a footnote — **and it was the user's, not the implementor's:** capture everything,
  every project, no per-project scoping, because the whole store is local. **The premise, not just
  the reasoning behind it: *capture everything* is correct *while the store is local-only*.**
  `backups/facts.jsonl` is every fact in plain text on disk — if a home directory is ever synced, or
  that journal exported or shared, cross-project content travels and this question reopens. Not
  second-guessing a decision that has been made; recording the condition it was made under, so a
  later change to how the store is stored or shared cannot silently invalidate reasoning that is no
  longer visible by then. It did not block todo 1: emitting an instruction stores nothing, and the
  summary it shapes lands in the transcript either way. It no longer blocks todo 2b either — see the
  trigger gap recorded in 2b above, which is a design gap, not a decision gate.

  **This answer does not license what was tried and reversed above.** *Capture everything into the
  local store* and *put real content into a git-tracked tree* are different questions — the first is
  answered here, the second was answered, separately, by the fixture-file reversal earlier in this
  doc, and it is still answered. The two notes now sit near each other in this document; that is a
  reason to say this explicitly, not a reason it needs saying twice.
