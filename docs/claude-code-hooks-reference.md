# Claude Code hook behaviour Engram depends on

Platform facts, with provenance. Every one below was either read out of working code or
recovered from commit history in Jim's own plugin repositories (`claudetools`,
`github-agent-plugins`), where they were established by live probing rather than by
reading documentation. Several are things the documentation does not state.

The common property: **they fail silently.** A hook can be wired, syntactically valid,
exiting 0, and delivering nothing at all — with no error, no log line, and no signal in
the exit code. That is why this file exists.

---

## What fires, and where

| Event | Main session | Subagent |
|---|---|---|
| `SessionStart`, `SessionEnd`, `UserPromptSubmit`, `Stop` | fires | **never fires** |
| `PreToolUse`, `PostToolUse`, `PermissionRequest`, `PermissionDenied` | fires | fires, inside the subagent's own loop, with `agent_id` / `agent_type` present |
| `SubagentStart` | — | fires at every spawn |

`SubagentStart` reaches **workflow-spawned agents** — ones never created through an
`Agent`/`Task` tool call. A `PreToolUse` rewrite of the `Agent` tool's input structurally
cannot: a measured 47-agent workflow run produced zero relay events. If a directive must
reach every spawn path, `SubagentStart` is the only proven channel.

## The `SubagentStart` envelope

Bare stdout is **silently discarded** on this event. `SessionStart` accepts bare stdout,
so the habit formed there actively misleads here. This is the single most expensive fact
in this file.

```javascript
process.stdout.write(JSON.stringify({
  hookSpecificOutput: {
    hookEventName: "SubagentStart",
    additionalContext: DIRECTIVE,
  },
}));
```

All three keys are load-bearing and `hookEventName` must match the event. Exit code is
an implicit 0 on the success path. A ~7.5 KB `additionalContext` was delivered
untruncated in a live probe; no truncation threshold was found.

`additionalContext` from multiple hooks on one event aggregates safely — Claude receives
all of them. Conflicting `permissionDecision` values from multiple hooks on one event are
undocumented, which is why only one plugin should ever deny a given tool.

## Identifying the caller

- **`agent_id` present ⇒ the caller is a subagent.** This is the discriminator.
- **`agent_type` alone is not**, because it is also set on a top-level `--agent` session.
  Keying a guard on `agent_type` wrongly suppresses the main orchestrator.
- The key name differs by event: **`agent_type`** on `SubagentStart`, but
  **`subagent_type`** inside `PreToolUse`'s `tool_input`. Reading the wrong one matches
  nothing, silently.
- Plugin agent types are namespaced `plugin:agent`, so match anchored — `(^|:)name$` —
  not by bare name.
- Hook payloads carry **no model or capability-tier field**. `effort` is thinking level,
  not tier.

## `PreToolUse` exit codes

Relevant to the gate escalation in D12.

| Code | Meaning |
|---|---|
| `0` | allow |
| `2` | **deny** — blocks the call and feeds the stderr message back to the model |
| `1` | error, and **not** a block — silently treated as allow |

That `1` is not a block is why the trap below is so damaging.

## Traps that fail silently

**Temporal dead zone disabling an entire guard.** A `const` declared at the bottom of the
file beside its helpers, referenced from an early-returning branch, throws `ReferenceError`
at runtime — function declarations hoist, `const` does not. Because exit 1 is not a block,
*every* rule in the guard goes inert at once. `node --check` passes clean; the fault is
runtime-only. This happened three times across two releases.

**Version-pinned plugin cache.** Editing a plugin's `hooks/` files in a directory-marketplace
working tree has no effect. The loaded copy lives under
`~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/` and refreshes only when
`plugin.json`'s version changes. `hooks.json` is not re-read mid-session either. The relay
doc records that this trap "produced a false negative indistinguishable from *the mechanism
doesn't work*." Any hook probe needs a fresh session at a bumped version.

**No agent roster at spawn.** A subagent cannot see the available-agent list until the
result of its own first tool call. A directive naming an unresolvable agent type fails
silently on first delegation, and looks exactly like the subagent ignoring the directive.

**`SessionEnd` has a very short enforced timeout** (~1.5s observed). Real work must be
backgrounded with stdin captured first, or it is killed mid-flight. By contrast `PreCompact`
is budgeted generously (90s in one config) and is the one event where heavier synchronous
work is affordable — which makes it the natural place to flush session memory before
compaction, even though it cannot inject context (see the PreCompact subsection in the
implementation plan).

## Testing implications

Adopted for Engram's own hook suite (D9).

**Assert the output shape, not just the exit code.** `rc == 0` is ambiguous between "actively
decided allow" and "no decision, fell through." A test checking only the exit code can pass
while proving nothing — one such test was found "passing unarmed."

**Harness pattern**: a shell script piping a JSON payload to the hook on stdin, capturing
stdout/stderr and the exit code, asserting on both, with `HOME` redirected to a sandbox so
tests never touch the real configuration. Predicates read as `is_allow`, `is_deny`,
`is_inject`, each checking the actual emitted JSON.

**A harness is necessary but not sufficient.** It cannot prove the *host* honours
`additionalContext` or `updatedInput` — only a live probe dispatching a real subagent that
reports back what it received can do that.

## On method

The relay document these facts come from tags every claim as *live-probed* or
*doc-sourced*, because an earlier revision ruled out `SubagentStart` entirely on the basis
of documentation silence — recorded afterwards as "an inference from silence written in the
voice of a documented fact."

That distinction is worth keeping in our own documents. Absence of a statement in
documentation is not evidence of absence in behaviour, and the cost of confusing the two
here was a working channel being ruled out for a whole release.
