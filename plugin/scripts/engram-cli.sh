#!/bin/sh
# Runs the installed engram binary on behalf of a slash command.
#
# The only difference from hooks/engram-exec.sh is the failure mode, and it is the whole
# reason this file exists rather than reusing that one. engram-exec.sh is called by
# hooks, where a missing binary must vanish in silence — a hook that fails is worse than
# a hook that does nothing. Here somebody typed /engram:status and is waiting for an
# answer, so "not installed" has to be said out loud, with the fix attached.
#
# Resolution itself is not duplicated: both paths go through hooks/resolve-engram.sh, so
# there stays exactly one answer to "which binary is this plugin talking to".
set -eu

here="$(dirname "$0")"
engram_bin="$("${here}/../hooks/resolve-engram.sh" || true)"

if [ -z "${engram_bin}" ]; then
    cat <<'EOF'
engram is not installed: no $ENGRAM_BIN, no ~/.local/bin/engram, and nothing named
engram on PATH. The plugin ships hooks and commands but deliberately no binary.

Install it from a clone of the engram repo:
    scripts/install.sh              # dry run first — prints what it would do
    scripts/install.sh --apply
EOF
    exit 127
fi

exec "${engram_bin}" "$@"
