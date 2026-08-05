---
description: Report whether the Engram memory server is running — pid, port, version, uptime, and whether the home is initialised.
allowed-tools: ["Bash"]
---

Run exactly this, once:

```bash
"${CLAUDE_PLUGIN_ROOT}/scripts/engram-cli.sh" status
```

Show the output verbatim. It is already written for a person — do not reformat it into a
table, a bullet list, or a summary.

This goes through the CLI rather than the `engram_status` MCP tool on purpose. When the
server is down its MCP tools disappear along with it, so the tool can only ever answer
"yes, running" — the one case nobody needs to ask about. The binary answers either way.

Then add **at most one line**, and only if one of these applies:

- Server not running → `/engram:start` will start it.
- Home not initialised → `/engram:doctor` shows what is missing.
- The command reported that engram is not installed → repeat the install instructions it
  printed; do not invent your own.

Otherwise say nothing further. Do not start the server, repair anything, or read any
other file. This command reports and stops.
