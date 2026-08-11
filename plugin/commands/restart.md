---
description: Restart the Engram memory server — stop whatever is running, then start fresh. The one-command path after installing a new binary over the old one.
allowed-tools: ["Bash"]
---

Run exactly this, once:

```bash
"${CLAUDE_PLUGIN_ROOT}/scripts/engram-cli.sh" restart
```

Show the output verbatim, then stop.

`restart` stops the recorded server if one is alive — healthy, wedged, or
version-mismatched alike — waits for it to actually exit, then starts one. If nothing
was running it just starts one, the same as `/engram:start`.

This is the binary-swap workflow in one step: stop → reinstall → start becomes
reinstall → restart.

Unlike `/engram:stop`, the `engram_*` MCP tools keep working after a restart — the
transport is HTTP and reconnects on the next call, so there is no next-session caveat
here. (Session-scoped server state, if any, starts fresh with the new process.)

There is no MCP tool for this: reaching an MCP tool proves the server is up, and the
reply dies with the process that would serve it, so restart has to be reached from
outside — the same reasoning `/engram:start` documents for cold start.
