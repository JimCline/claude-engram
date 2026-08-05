#!/bin/sh
# Make sure the daemon that .mcp.json points at is actually listening.
#
# `engram start` is idempotent and returns in milliseconds when the server is already up,
# which is every session after the first one following a reboot — the daemon outlives the
# session that started it. Cold start was measured at 132ms, warm at 16ms.
#
# Three rules this script exists to enforce:
#
#   The success path writes NOTHING to stdout. On SessionStart, whatever a hook prints is
#   injected into the model's context as additionalContext, so "engram started (pid 1234,
#   port 7433)" would become a line of the agent's prompt every single session.
#
#   A missing binary is the one thing worth saying out loud. Silence there is
#   indistinguishable from memory simply not working, and the model can pass the fix on.
#
#   It always exits 0. A memory server that will not start makes a degraded session, not a
#   broken one, and failing here would surface as a startup error that buys nobody
#   anything.
set -eu

here="$(dirname "$0")"
engram_bin="$("${here}/resolve-engram.sh" || true)"

if [ -z "${engram_bin}" ]; then
    echo "Engram's plugin is loaded but no engram binary was found, so memory tools are unavailable this session. Tell the user to run scripts/install.sh --apply from the engram repository, or to set ENGRAM_BIN to the binary's path."
    exit 0
fi

"${engram_bin}" start >/dev/null 2>&1 || true
exit 0
