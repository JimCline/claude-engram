#!/bin/sh
# Make sure the daemon that .mcp.json points at is actually listening.
#
# `engram start` is idempotent and returns in milliseconds when the server is already
# up, which is every session after the first one following a reboot — the daemon
# outlives the session that started it.
#
# Two rules this script exists to enforce:
#
#   Nothing reaches stdout. On SessionStart, whatever a hook prints is injected into
#   the model's context as additionalContext, so "engram started (pid 1234, port 7433)"
#   would become a line of the agent's prompt every single session.
#
#   It always exits 0. A memory server that failed to start makes for a session with no
#   memory, which is a degraded session, not a broken one. Failing here would surface as
#   a hook error on startup and buy the user nothing.
"${CLAUDE_PLUGIN_ROOT}/bin/engram" start >/dev/null 2>&1
exit 0
