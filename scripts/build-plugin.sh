#!/usr/bin/env bash
set -euo pipefail

# The engram binary is built here rather than committed to the repository
# because it is a native, platform-specific artifact (Native AOT, one RID per
# build) — committing it would mean committing a different binary per
# platform and re-committing on every change, which is what a build step is
# for. plugin/bin/ is gitignored; this script is the only thing that
# populates it.
#
# Claude Code's plugin cache is version-pinned at
# ~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/ and is
# wholesale-replaced on a version bump. After rebuilding this binary, bump
# "version" in plugin/.claude-plugin/plugin.json (and in
# .claude-plugin/marketplace.json) or Claude Code will keep serving the
# previously cached copy.

usage() {
    cat <<'EOF'
usage: scripts/build-plugin.sh [--rid <id>] [--help]

Builds the Native AOT engram CLI into plugin/bin/ for use as a Claude Code
plugin.

  --rid <id>   Override runtime identifier detection (e.g. osx-arm64,
               osx-x64, linux-x64, linux-arm64).
  --help       Show this message and exit.
EOF
}

rid_override=""

while [ $# -gt 0 ]; do
    case "$1" in
        --rid)
            if [ $# -lt 2 ]; then
                echo "error: --rid requires a value" >&2
                exit 1
            fi
            rid_override="$2"
            shift 2
            ;;
        --help)
            usage
            exit 0
            ;;
        *)
            echo "error: unrecognized argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

detect_rid() {
    local os arch
    os=$(uname -s)
    arch=$(uname -m)

    case "$os" in
        Darwin)
            case "$arch" in
                arm64) echo "osx-arm64" ;;
                x86_64) echo "osx-x64" ;;
                *)
                    echo "error: unsupported Darwin architecture: $arch" >&2
                    exit 1
                    ;;
            esac
            ;;
        Linux)
            case "$arch" in
                x86_64) echo "linux-x64" ;;
                aarch64) echo "linux-arm64" ;;
                *)
                    echo "error: unsupported Linux architecture: $arch" >&2
                    exit 1
                    ;;
            esac
            ;;
        *)
            echo "error: unsupported OS: $os" >&2
            exit 1
            ;;
    esac
}

if [ -n "$rid_override" ]; then
    rid="$rid_override"
else
    rid=$(detect_rid)
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: dotnet is not on PATH" >&2
    exit 1
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd "$script_dir/.." && pwd)
publish_dir="$repo_root/plugin/bin"

echo "Building engram for $rid into $publish_dir ..."
rm -rf "$publish_dir"
dotnet publish "$repo_root/src/Engram.Cli" \
    -c Release \
    -r "$rid" \
    -o "$publish_dir"

binary_path="$publish_dir/engram"
if [ ! -x "$binary_path" ]; then
    echo "error: expected published binary not found at $binary_path" >&2
    exit 1
fi

verify_home=$(mktemp -d)
trap 'rm -rf "$verify_home"' EXIT

echo "Verifying the published binary starts (ENGRAM_HOME=$verify_home) ..."
if ! ENGRAM_HOME="$verify_home" "$binary_path" home >/dev/null; then
    echo "error: '$binary_path home' did not exit 0 — refusing to ship an unverified binary" >&2
    exit 1
fi

publish_size_before=$(du -sh "$publish_dir" | cut -f1)
echo "Removing debug symbols from $publish_dir ..."
find "$publish_dir" -maxdepth 1 -type f -name '*.pdb' -delete
find "$publish_dir" -maxdepth 1 -type d -name '*.dSYM' -exec rm -rf {} +
publish_size_after=$(du -sh "$publish_dir" | cut -f1)

binary_size=$(du -h "$binary_path" | cut -f1)

echo
echo "Built and verified: $binary_path ($binary_size)"
echo "plugin/bin size before symbol cleanup: $publish_size_before"
echo "plugin/bin size after symbol cleanup:  $publish_size_after"
echo
echo "Next steps to install the plugin:"
echo "  claude plugin marketplace add $repo_root"
echo "  claude plugin install engram@engram"
echo "  # then, in a running session:"
echo "  /reload-plugins"
