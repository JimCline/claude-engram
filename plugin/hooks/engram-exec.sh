#!/bin/sh
# Runs the installed engram binary with whatever arguments follow.
#
# Every caller is a hook, and a hook that fails is worse than a hook that does nothing —
# so a missing binary exits 0 in silence here. The one place that says something about it
# is ensure-server.sh, which runs on SessionStart where a message can actually reach
# someone.
set -eu

here="$(dirname "$0")"
engram_bin="$("${here}/resolve-engram.sh" || true)"

if [ -z "${engram_bin}" ]; then
    exit 0
fi

exec "${engram_bin}" "$@"
