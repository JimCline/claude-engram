---
description: Store a fact in Engram's memory explicitly, in the user's own words. Recall ranks it above long-term memory for the rest of the session.
argument-hint: "<the fact to remember>"
allowed-tools: ["mcp__plugin_engram_engram__engram_remember"]
---

Call `engram_remember` to store this:

> $ARGUMENTS

**Pass the user's words through as `statement`, essentially unchanged.** They typed this
command instead of letting you decide what was worth keeping, which means the wording is
the instruction. Tighten a run-on sentence or expand a pronoun that would be meaningless
later ("it", "that file") — nothing more. Never substitute your own reading of what they
meant; a memory store that quietly rewrites what it was told is worse than one that
forgets.

Fill the optional fields only when the conversation already answers them:

- `subject` — what the fact is about, when the statement alone would not make that clear.
- `evidence` — the file path, command output, or PR this came from, if it is in front of
  you. Do not go looking for it.

Then confirm in one line what was stored. If `$ARGUMENTS` is empty, ask what to remember.

If the `engram_remember` tool is not available, the memory server is not running: say so,
point at `/engram:start`, and do not attempt to write the fact anywhere else.
