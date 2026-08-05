#!/bin/sh
# Prints the path to the installed engram binary, or nothing if there isn't one.
# Always exits 0 — "not installed" is an answer, not a failure.
#
# The plugin deliberately ships no binary of its own. Two reasons, both structural:
#
#   A gitignored build artifact cannot travel through a remote marketplace (D13). A
#   marketplace clone gets the scripts and the manifests, and nothing to run.
#
#   A bundled copy lives under the version-pinned plugin cache, so its path changes on
#   every version bump — .../0.2.0/bin/engram becomes .../0.3.0/bin/engram. The daemon
#   proves ownership by executable path before it will signal anything, so after an
#   upgrade the new binary cannot recognise the running server as its own: it refuses to
#   replace it, fails to bind the port, and exits. The old daemon runs forever and the new
#   one never starts, silently. One binary in one stable place avoids that entirely.
#
# PATH is checked last, not first: a hook inherits whatever environment launched Claude
# Code, and a GUI launch can have a minimal PATH that never included ~/.local/bin.
set -eu

if [ -n "${ENGRAM_BIN:-}" ] && [ -x "${ENGRAM_BIN}" ]; then
    printf '%s\n' "${ENGRAM_BIN}"
    exit 0
fi

if [ -x "${HOME}/.local/bin/engram" ]; then
    printf '%s\n' "${HOME}/.local/bin/engram"
    exit 0
fi

from_path="$(command -v engram 2>/dev/null || true)"
if [ -n "${from_path}" ] && [ -x "${from_path}" ]; then
    printf '%s\n' "${from_path}"
fi

exit 0
