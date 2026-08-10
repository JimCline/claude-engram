---
description: Flush this session's durable learnings to Engram now, before compaction or the session ends.
allowed-tools: ["mcp__plugin_engram_engram__engram_remember"]
---

Review this session and call `engram_remember` once for each thing worth knowing in a
*future* session.

**What's worth remembering** — the test for each one: would a fresh agent, six weeks from
now, save real work by being told this before it starts reading files? Keep:

- decisions and the reason behind them, especially where the obvious choice was rejected
- constraints and invariants that are easy to violate by accident
- gotchas measured rather than assumed — API behaviour that surprised you, a fix that did
  not work and why
- dead ends already ruled out, so nobody pays for them twice

Leave out anything a fresh agent can read off the code or git history in a few seconds,
anything true only inside this conversation ("we are about to run the tests"), and
restatements of the task you were given.

Each fact must stand alone. "That approach doesn't work" is worthless out of context;
"a deferred SQLite transaction that upgrades to a writer raises SQLITE_BUSY_SNAPSHOT,
which busy_timeout cannot wait out" is the same fact made portable.

Then report **what each call actually returned**, not what you hoped it did. Each
successful call answers with a fact id (`[f123]`); if a response doesn't carry one, that
fact was not saved and you should not tell the user it was. Sending a statement that is
already stored is free — it returns the existing id rather than duplicating it — so err
toward including one you are unsure about rather than leaving it out.
