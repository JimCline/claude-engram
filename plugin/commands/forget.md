---
description: Retract something Engram remembers — wrong, private, or no longer true. It stops appearing in recall immediately.
argument-hint: "<fact id, e.g. f42 — or describe what to forget>"
allowed-tools: ["mcp__plugin_engram_engram__engram_forget", "mcp__plugin_engram_engram__engram_recall"]
---

Retract this from memory:

> $ARGUMENTS

**If the argument is an id** (something like `f42`, with or without brackets), call
`engram_forget` with it directly. Do not look it up first and do not ask for confirmation
— naming an id is the confirmation.

**If the argument is a description** rather than an id, call `engram_recall` with it,
show the matching facts with their ids, and ask which to retract. Retract only ids the
user names back. Never guess at a match and retract it, and never retract more than was
asked for: the whole point of this command is that the user is in control of what is
kept, and a command that over-deletes is as bad as one that cannot delete at all.

**If `$ARGUMENTS` is empty**, ask what they want forgotten.

Any stored fact can be retracted, including the ones Engram ships with — it is the user's
memory. Session notes (ids like `s001`, or `s001@p1` from an earlier session) cannot be
retracted this way; if the user asks for one of those, say so plainly rather than
reporting a success that did not happen.

Report what the tool returned. The original is closed rather than erased — facts here are
append-only, so the record shows that something was retracted without keeping the content
in recall. Say that if the user asks whether it is really gone: it will not be recalled
again, and nothing puts it back, including updating or reinstalling Engram.
