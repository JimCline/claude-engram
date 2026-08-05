---
description: Start the Engram memory server. The recovery path when the server is down and its MCP tools have vanished with it.
allowed-tools: ["Bash"]
---

Run exactly this, once:

```bash
"${CLAUDE_PLUGIN_ROOT}/scripts/engram-cli.sh" start
```

Show the output verbatim, then stop. `start` is idempotent — if a server is already
running it says so and changes nothing, so there is never a reason to check status first.

This is a shell command rather than the `engram_start` MCP tool because the MCP tool
cannot do the one thing you need here. Reaching it at all proves the server is up, so it
can only repair a stale pid file; it can never cold-start anything. When the server is
genuinely down, every `engram_*` tool is gone and this command is the only way back.

One thing worth telling the user afterwards, if the server was not previously running:
the `engram_*` MCP tools attach when the session's MCP client connects, so they may not
appear until the next session. The hooks and the CLI work immediately either way.

If start reports the port is held by something it cannot claim, do not retry, and do not
kill anything. Send them to `/engram:doctor`, which shows what is actually on the port.
