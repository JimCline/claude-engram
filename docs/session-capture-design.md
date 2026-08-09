# Session capture — design in progress

Working document for the change that replaces "the model remembers to call `engram_digest`"
with something that does not depend on the model remembering. Read
`docs/engram-implementation-plan.md` first; this only covers what is not settled there.

**Status: designed, partly built, three items outstanding.** Resume at *What's next*.

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

## Done

- `4a21a74` — retracts the erratum; records both channels, the sequencing limit, and that the
  recall-footer fallback was measured not to work.
- `6a0b8ff` — the primer now names **both** write triggers. D51 fixed precedence and left the
  trigger asymmetric: the subagent primer always said *"write anything durable you learn"*, while
  the session primer said only that the user could ask. `SessionStart` matches
  `startup|resume|clear|compact`, so this is the surface re-injected whenever context resets —
  and it carried the weaker claim. Both triggers now; the first kept verbatim because it was
  chosen to match the words a competing memory system fires on.

Recorded in Engram: `f5595` (channel behaviour), `f5596` (why PreCompact cannot call a tool),
`f5602` (working preference: short design exchanges).

## What's next

1. **`RunPreCompact` emits the digest instruction** on bare stdout. Currently emits nothing.
2. **Harvester** — read delimited blocks from `isCompactSummary` records into session facts.
   Parse strictly; malformed input must yield nothing, never partial garbage.
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

## Open questions

- Delimiter format, and whether the **compaction-guard plugin** — which already injects its own
  directive into the same summarizer — conflicts.
- Whether harvested items may **supersede** existing facts, or only append. Appending is the safe
  v1; superseding needs judgment about identity that nothing currently does.
- **Growth regime.** ~25 notes per compaction changes the corpus curve, and D58/D60's recall
  latency work was measured against a store that grows in deliberate steps.
- **Scope and privacy.** This widens automatic ingestion from the user's own words to the
  assistant's reasoning. A decision, not a footnote.
