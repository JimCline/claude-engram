---
description: Stop the running Engram memory server. Required before replacing the binary it was launched from.
allowed-tools: ["Bash"]
---

Run exactly this, once:

```bash
"${CLAUDE_PLUGIN_ROOT}/scripts/engram-cli.sh" stop
```

Show the output verbatim and stop. Do not restart the server afterwards unless the user
asks.

The non-obvious reason this command exists: **stop the server before installing a new
binary over the old one.** The daemon proves ownership by the path of the executable it
was launched from, so a server still running from a path that no longer exists cannot be
recognised, replaced, or signalled by a freshly installed binary. It keeps running, the
new one refuses to bind the port, and memory silently stops updating. Stopping first
costs a second and makes that state unreachable.

Once the server is stopped, the `engram_*` MCP tools stop working for the rest of the
session — say so if the user seems to expect otherwise. `/engram:start` brings it back.
