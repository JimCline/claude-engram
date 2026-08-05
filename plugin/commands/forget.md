---
description: Retract something Engram captured about you — wrong, private, or no longer true. It stops appearing in recall immediately.
argument-hint: "<fact id, e.g. u1a2b3c4d — or describe what to forget>"
allowed-tools: ["mcp__plugin_engram_engram__engram_forget", "mcp__plugin_engram_engram__engram_recall"]
---

Retract this from memory:

> $ARGUMENTS

**If the argument is an id** (something like `u1a2b3c4d`, with or without brackets), call
`engram_forget` with it directly. Do not look it up first and do not ask for confirmation
— naming an id is the confirmation.

**If the argument is a description** rather than an id, call `engram_recall` with it,
show the matching facts with their ids, and ask which to retract. Retract only ids the
user names back. Never guess at a match and retract it, and never retract more than was
asked for: the whole point of this command is that the user is in control of what is
kept, and a command that over-deletes is as bad as one that cannot delete at all.

**If `$ARGUMENTS` is empty**, ask what they want forgotten.

Only facts Engram captured from the user's own statements — ids beginning with `u` — can
be retracted. Seeded facts (`f…`) and session notes (`s…`) cannot; if the user asks for
one of those, say so plainly rather than reporting a success that did not happen.

Report what the tool returned. The original is closed rather than erased — facts here are
append-only, so the record shows that something was retracted without keeping the content
in recall. Say that if the user asks whether it is really gone: it will not be recalled
again, and the statement stays on disk in `~/.engram/user-facts` until they delete that
directory themselves.
