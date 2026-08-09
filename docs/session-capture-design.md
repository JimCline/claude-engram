# Session capture — design in progress

Working document for the change that replaces "the model remembers to call `engram_digest`"
with something that does not depend on the model remembering. Read
`docs/engram-implementation-plan.md` first; this only covers what is not settled there.

**Status: designed; todo 1 built and unverified against a real compaction; two items
outstanding.** Resume at *What's next*.

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
it immediately.

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
in use) and transcribed there.

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

Item level — these **drop the item and keep the block**, and each drop is counted:

- Longer than **500 characters**. Generous enough that no genuine one-sentence belief in this
  store is near it (the longest run ~250), tight enough to reject a pasted paragraph or code
  block.
- Exactly equal to an item already taken from the same block (dedupe before the cap).

There is deliberately **no minimum length**. A length floor is a poor proxy for self-containedness
and would reject a legitimately terse fact.

### The cap, and where it is enforced

**At most 25 items, enforced by the harvester, not by trusting the instruction.** The instruction
states the limit because its real job is to make the summarizer *select* rather than dump; the
harvester takes the first 25 after filtering because that is what actually bounds the corpus.
Items seen and items taken are both recorded, so the two numbers can disagree visibly.

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
either (a) a placeholder from the instruction's own example — `one durable fact, on one line` —
appears in the store, which means the echo path fired, or (b) harvest is asked to run over content
that did not originate with the summarizer. That placeholder is a tripwire, which is the second
reason the example items are obvious non-facts rather than realistic ones.

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

## Done

- `4a21a74` — retracts the erratum; records both channels, the sequencing limit, and that the
  recall-footer fallback was measured not to work.
- `6a0b8ff` — the primer now names **both** write triggers. D51 fixed precedence and left the
  trigger asymmetric: the subagent primer always said *"write anything durable you learn"*, while
  the session primer said only that the user could ask. `SessionStart` matches
  `startup|resume|clear|compact`, so this is the surface re-injected whenever context resets —
  and it carried the weaker claim. Both triggers now; the first kept verbatim because it was
  chosen to match the words a competing memory system fires on.
- **Todo 1 built, per the D62 decision above.** `src/Engram.Core/CompactionDigest.cs` is the
  single definition of the sentinels, the cap, and the instruction text; `RunPreCompact`
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
  about the shipped binary rather than about the code under test. **Not yet verified against a
  real compaction** — that is the NEEDS-EVIDENCE experiment, now the first item in *What's next*.

Recorded in Engram: `f5595` (channel behaviour), `f5596` (why PreCompact cannot call a tool),
`f5602` (working preference: short design exchanges).

## What's next

1. **NEEDS-EVIDENCE — one experiment, before todo 2 is designed in detail.** Todo 1 (`RunPreCompact`
   emits the digest instruction) is built — see *Done*, above, and the D62 build order it followed.
   Trigger a manual `/compact`, then read the resulting `isCompactSummary` record out of
   `~/.claude/projects/<slug>/<session-id>.jsonl` and report, verbatim: (a) whether an
   `<engram-digest v="1">` block is present at all; (b) whether it is well-formed under the four
   block rules in the D62 decision above; (c) how many item lines it contains and whether any
   exceeds 500 characters; (d) whether any line is prose rather than an item; (e) whether the block
   survived into the record without truncation or escaping. Then repeat the read after an
   **auto**-compaction, since only the `manual` matcher has ever been observed firing. What each
   result decides: absent → the channel or the wording is wrong and todo 2 is blocked; present but
   malformed → the strict rules stay and the instruction wording is tuned against the observed
   failure; present and well-formed but padded with narration → the *prefer omission* wording needs
   strengthening before the harvester is allowed to write anything.

2. **Harvester** — read delimited blocks from `isCompactSummary` records into session facts.
   Parse strictly; malformed input must yield nothing, never partial garbage. Implements the parse
   rules and the cap from the decision above, and owns three things that section deliberately left
   to it: the **round-trip test** against `CompactionDigest.Instruction`'s own example block, which
   is the only guard that emitter and parser agree; how harvested facts are marked as
   summarizer-authored rather than model-authored, so D18/D43's adoption numbers are not inflated
   by a hook-driven capture (D56 makes that mistake explicit for `user-prompt`); and confirming
   that `SessionFacts.Append`'s `sessionExternalId` is the hook's `session_id`.
3. **`digest` MCP tool → slash command.** D17 puts the tool surface at 2,575 characters ≈ 640
   tokens paid every session; `digest` is 509 of them, and it has never fired.

## Rejected, with reasons — do not re-derive

- **`PreCompact` nudging the model to call `digest`.** Impossible, and for a sequencing reason
  rather than a channel one: the hook fires, summarization runs, the new context begins, with no
  model turn in between. The only reader is the summarizer, which has no tools.
- **`Stop` as a nudge** ("call digest now"). Prompting, gameable against M0's own wording, and
  made unnecessary by the compaction route.
- **Deterministic extraction of decisions from prose.** Precision. D44 is a measured case of
  cheap retrieval poisoning the system — six of seven results were noise reached through a shared
  stem, and coverage called it `high`. **A memory store is hurt more by plausible noise than by
  absence.** Structure identifies user directives; only judgment identifies decisions.
- **A detached harvester with its own model** (local GGUF / `claude -p` / API). Superseded — the
  summarizer already does the extraction for free. Revisit only if the delimited-block route
  proves unreliable.
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

## Open questions

- Whether harvested items may **supersede** existing facts, or only append. Appending is the safe
  v1; superseding needs judgment about identity that nothing currently does. The `v="1"` marker is
  what lets a supersession field be added later without ambiguity.
- **Growth regime.** ~25 notes per compaction changes the corpus curve, and D58/D60's recall
  latency work was measured against a store that grows in deliberate steps. The arithmetic that
  makes this urgent: five compactions a day at the cap is 125 facts a day, ~45,000 a year, against
  a store where a term matching most of 50,097 facts costs 125.9 ms. The cap is the only lever and
  it sits at the harvester, so it can be lowered without a format change — but somebody has to
  pick the number, and the *prefer omission* wording means the real rate must be measured before
  it is picked rather than assumed to be 25.
- **Scope and privacy.** This widens automatic ingestion from the user's own words to the
  assistant's reasoning. A decision, not a footnote — **and it is the user's, not the
  implementor's.** It does not block todo 1: emitting an instruction stores nothing, and the
  summary it shapes lands in the transcript either way. It does block todo 2, which is the first
  thing that writes.
