---
description: Diagnose an Engram install — runs engram doctor, then adds the three things a binary cannot see about itself: which binary got resolved, what is holding the port, and the tail of the log. Read-only.
allowed-tools: ["Bash"]
---

Gather everything in **one** Bash call, then reason over it. Do not run these piecemeal.

```bash
CLI="${CLAUDE_PLUGIN_ROOT}/scripts/engram-cli.sh"
echo "=== binary the plugin resolves ==="
"${CLAUDE_PLUGIN_ROOT}/hooks/resolve-engram.sh" || echo "(none — not installed)"
echo "ENGRAM_BIN=${ENGRAM_BIN:-<unset>}"
command -v engram >/dev/null 2>&1 && echo "on PATH: $(command -v engram)" || echo "on PATH: no"
echo "=== doctor ==="
"$CLI" doctor 2>&1
echo "doctor exit: $?"
echo "=== home ==="
"$CLI" home 2>&1
ROOT="$("$CLI" home 2>/dev/null | sed -n 's/^Root=//p')"
PORT="$("$CLI" status 2>/dev/null | sed -n 's/^port: //p')"
echo "=== port ${PORT:-7433} ==="
lsof -nP -iTCP:"${PORT:-7433}" -sTCP:LISTEN 2>/dev/null || echo "(nothing listening)"
echo "=== log tail ==="
[ -n "$ROOT" ] && tail -20 "$ROOT/engram.log" 2>/dev/null || echo "(no log)"
```

`engram doctor` carries the diagnosis. It checks the home and config, the store and its
schema version, the server, Claude Code's permissions, the embedding provider, the vector
index, backups, the edit queue, and what indexing would read here — each with a state and,
where there is one, the command that fixes it. It is read-only and exits non-zero only when
something is genuinely broken, so `doctor exit: 1` is the signal, not the presence of `off`
or `warn` rows.

Everything else in that block exists because it is what `doctor` structurally cannot see.
A binary cannot tell you it is the wrong binary, cannot see a foreign process holding its
port, and does not read its own log back. The home path comes from `engram home` rather
than a hardcoded `~/.engram` for the same reason: `ENGRAM_HOME` can move it, and a doctor
that inspects the wrong directory reports confident nonsense.

## Reading the result

Give the user a short verdict first — healthy, or the specific fault — then the evidence.
Do not paste all of the raw output back; quote only the lines that carry the finding.
`doctor` already names its own fixes; do not restate them, and do not invent different ones.

Faults worth naming explicitly, because none of them announce themselves and none appear
in `doctor` output:

- **The resolved binary is stale.** If the `doctor` section printed usage text instead of a
  report, the resolved binary predates the command and is older than the plugin expects.
  Everything else in the block is then describing an install that is not the one in the
  repository. Say so first; it explains faults that otherwise look unrelated. The fix is a
  reinstall, not `init`.
- **Resolved binary is not the one the user expects.** Resolution order is `$ENGRAM_BIN`,
  then `~/.local/bin/engram`, then PATH. A stale `ENGRAM_BIN` silently wins over a fresh
  install.
- **A process is listening but the server check says not running.** The daemon proves
  ownership by executable path and recorded start time, so a server launched from a path
  that has since been replaced or deleted can no longer be recognised or stopped by the
  installed binary. Symptom: the port is held, `doctor` reports the server down. Fix:
  identify the pid from the `lsof` block and have the *user* decide whether to kill it —
  never kill it yourself.
- **Log tail shows repeated bind failures.** Port contention, not a memory problem.

Two things `doctor` reports that read like faults and are not. An `off` row is a supported
configuration — `provider = "none"` is a choice, and a server that is not running is one
the hooks and the CLI do not need. And an unindexed repository is not a broken database:
code indexing is deferred, so recall answering from the seeded corpus plus whatever this
instance has been told is correct. Do not suggest `repair` for either.

## Boundaries

This command is read-only. It does not start, stop, initialise, repair, kill, or delete
anything — including when the fault is obvious and the fix is one command away. Name the
fix and let the user run it. A diagnostic that mutates state destroys the evidence someone
came here to look at.
