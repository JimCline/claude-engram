---
description: Diagnose an Engram install — which binary the plugin resolved, server and port state, home contents, telemetry, and the tail of the log. Read-only.
allowed-tools: ["Bash"]
---

Gather everything in **one** Bash call, then reason over it. Do not run these piecemeal.

```bash
CLI="${CLAUDE_PLUGIN_ROOT}/scripts/engram-cli.sh"
echo "=== binary the plugin resolves ==="
"${CLAUDE_PLUGIN_ROOT}/hooks/resolve-engram.sh" || echo "(none — not installed)"
echo "ENGRAM_BIN=${ENGRAM_BIN:-<unset>}"
command -v engram >/dev/null 2>&1 && echo "on PATH: $(command -v engram)" || echo "on PATH: no"
echo "=== status ==="
"$CLI" status 2>&1
echo "=== home ==="
"$CLI" home 2>&1
ROOT="$("$CLI" home 2>/dev/null | sed -n 's/^Root=//p')"
PORT="$("$CLI" status 2>/dev/null | sed -n 's/^port: //p')"
echo "=== home contents (${ROOT:-unresolved}) ==="
[ -n "$ROOT" ] && ls -la "$ROOT" 2>&1 | head -20 || echo "(home did not resolve)"
echo "=== pid file ==="
[ -n "$ROOT" ] && cat "$ROOT/engram.pid" 2>/dev/null || echo "(no pid file)"
echo "=== port ${PORT:-7433} ==="
lsof -nP -iTCP:"${PORT:-7433}" -sTCP:LISTEN 2>/dev/null || echo "(nothing listening)"
echo "=== telemetry ==="
"$CLI" probe --since 7d 2>&1 | head -40
echo "=== log tail ==="
[ -n "$ROOT" ] && tail -20 "$ROOT/engram.log" 2>/dev/null || echo "(no log)"
```

The home path comes from `engram home` rather than a hardcoded `~/.engram` on purpose:
`ENGRAM_HOME` can move it, and a doctor that inspects the wrong directory reports
confident nonsense.

## Reading the result

Give the user a short verdict first — healthy, or the specific fault — then the evidence.
Do not paste all of the raw output back; quote only the lines that carry the finding.

Faults worth naming explicitly, because none of them announce themselves:

- **Resolved binary is not the one the user expects.** Resolution order is `$ENGRAM_BIN`,
  then `~/.local/bin/engram`, then PATH. A stale `ENGRAM_BIN` silently wins over a fresh
  install.
- **A process is listening but `status` does not claim it.** The daemon proves ownership
  by executable path and recorded start time, so a server launched from a path that has
  since been replaced or deleted can no longer be recognised or stopped by the installed
  binary. Symptom: the port is held, status says not running. Fix: identify the pid from
  the `lsof` block and have the *user* decide whether to kill it — never kill it yourself.
- **Pid file present, no process.** Harmless leftover; `/engram:start` repairs it.
- **Home resolves but `config.toml` is missing.** The home was never initialised, so hooks
  exit early and no primer reaches the model. `engram init` fixes it.
- **Telemetry is empty while the server is running.** Nothing is being recorded — usually
  the same uninitialised-home cause as above, one layer down.
- **No `engram.db` in the home listing.** `engram init` creates and seeds it, so an absent
  database means initialisation never finished — it is not optional and not deferred.
  Name `engram init` as the fix. What *is* deferred is code indexing: no repository has
  been indexed yet, so recall answers from the seeded corpus plus whatever this instance
  has been told since. An unindexed repository is not a broken database — do not report
  it as one, and do not suggest `repair` for it.
- **Log tail shows repeated bind failures.** Port contention, not a memory problem.

## Boundaries

This command is read-only. It does not start, stop, initialise, repair, kill, or delete
anything — including when the fault is obvious and the fix is one command away. Name the
fix and let the user run it. A diagnostic that mutates state destroys the evidence
someone came here to look at.
