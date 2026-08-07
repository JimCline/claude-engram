#!/usr/bin/env bash
# fetch-tree-sitter.sh — compile the tree-sitter core and grammars into Engram's lib/ directory.
#
# Engram loads these into its own process, so they are verified like a dependency: each
# source tarball is pinned by version and sha256 in this file, checked before anything is
# compiled, and a mismatch aborts rather than warns. The digests were computed from
# downloaded archives, so they check the bytes rather than restating a hope. GitHub's
# generated source archives carry no stability contract, which the pin turns into a
# feature: if the bytes ever change, this stops loudly instead of compiling something new.
#
# Compiled here because upstream ships grammars as C source, not binaries (D47): one
# `cc -shared` per library, about three seconds for all four, measured. The versions pin
# an ABI range the binding proved — one core accepted grammars answering ABI 14
# (typescript, tsx) and 15 (javascript) in the same process, so nothing here compares
# versions; ts_parser_set_language is the authority. An upgrade is a registry decision,
# not a refresh: ts_query_new validates the registry's queries against each grammar's
# node types, so a grammar bump can invalidate a query and must re-run the conformance
# suite. Adding a grammar is one registry row plus one pin here.
#
# Usage: scripts/fetch-tree-sitter.sh [--home <dir>] [--force] [--print-path]

set -euo pipefail

CORE_VERSION="0.26.11"
TYPESCRIPT_VERSION="0.23.2"
JAVASCRIPT_VERSION="0.25.0"

CORE_SHA256="1bab01ed21464f3272665b9c60e39ee79f68da1333e80b23f2c9356569d06971"
TYPESCRIPT_SHA256="2c4ce711ae8d1218a3b2f899189298159d672870b5b34dff5d937bed2f3e8983"
JAVASCRIPT_SHA256="9712fc283d3dc01d996d20b6392143445d05867a7aad76fdd723824468428b86"

# Must produce the names TreeSitter.LibraryFile expects, or Locate will call a finished
# install absent.
library_name() {
    case "$(uname -s)" in
        Darwin)               echo "lib$1.dylib" ;;
        Linux)                echo "lib$1.so" ;;
        MINGW*|MSYS*|CYGWIN*) echo "$1.dll" ;;
        *) return 1 ;;
    esac
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | cut -d' ' -f1
    else
        echo "error: no sha256sum or shasum on PATH; cannot verify the download" >&2
        exit 1
    fi
}

fetch_verified() { # url, destination, expected sha256
    curl -fsSL "$1" -o "$2"
    local actual
    actual="$(sha256_of "$2")"
    if [[ "$actual" != "$3" ]]; then
        echo "error: checksum mismatch for $1" >&2
        echo "       expected $3" >&2
        echo "       actual   $actual" >&2
        echo "       Refusing to compile. Engram loads these into its own process." >&2
        exit 1
    fi
    echo "  checksum ok"
}

home_dir="${ENGRAM_HOME:-$HOME/.engram}"
force=0
print_path=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --home)       home_dir="$2"; shift 2 ;;
        --force)      force=1; shift ;;
        --print-path) print_path=1; shift ;;
        -h|--help)
            sed -n '/^# fetch-tree-sitter/,/^# Usage/p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "error: unknown argument: $1" >&2; exit 2 ;;
    esac
done

core_lib="$(library_name tree-sitter)" || {
    echo "error: unsupported platform $(uname -s)/$(uname -m)." >&2
    exit 1
}
grammar_libs=()
for grammar in typescript tsx javascript; do
    grammar_libs+=("$(library_name "tree-sitter-$grammar")")
done

lib_dir="$home_dir/lib"

if [[ $print_path -eq 1 ]]; then
    echo "$lib_dir/$core_lib"
    exit 0
fi

if [[ $force -eq 0 ]]; then
    complete=1
    for lib in "$core_lib" "${grammar_libs[@]}"; do
        [[ -f "$lib_dir/$lib" ]] || complete=0
    done
    if [[ $complete -eq 1 ]]; then
        echo "tree-sitter v$CORE_VERSION already installed in $lib_dir"
        echo "(pass --force to re-fetch and recompile)"
        exit 0
    fi
fi

# Fail before any network when the compile could never happen.
command -v cc >/dev/null 2>&1 || {
    echo "error: no C compiler (cc) on PATH." >&2
    echo "       macOS: xcode-select --install    Debian/Ubuntu: apt install build-essential" >&2
    exit 1
}

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/out"

echo "Fetching tree-sitter v$CORE_VERSION..."
fetch_verified \
    "https://github.com/tree-sitter/tree-sitter/archive/refs/tags/v${CORE_VERSION}.tar.gz" \
    "$tmp/core.tar.gz" "$CORE_SHA256"
echo "Fetching tree-sitter-typescript v$TYPESCRIPT_VERSION..."
fetch_verified \
    "https://github.com/tree-sitter/tree-sitter-typescript/archive/refs/tags/v${TYPESCRIPT_VERSION}.tar.gz" \
    "$tmp/typescript.tar.gz" "$TYPESCRIPT_SHA256"
echo "Fetching tree-sitter-javascript v$JAVASCRIPT_VERSION..."
fetch_verified \
    "https://github.com/tree-sitter/tree-sitter-javascript/archive/refs/tags/v${JAVASCRIPT_VERSION}.tar.gz" \
    "$tmp/javascript.tar.gz" "$JAVASCRIPT_SHA256"

tar -xzf "$tmp/core.tar.gz" -C "$tmp"
tar -xzf "$tmp/typescript.tar.gz" -C "$tmp"
tar -xzf "$tmp/javascript.tar.gz" -C "$tmp"

core_src="$tmp/tree-sitter-$CORE_VERSION"
echo "Compiling $core_lib..."
cc -shared -fPIC -O2 \
    -I "$core_src/lib/include" -I "$core_src/lib/src" \
    "$core_src/lib/src/lib.c" \
    -o "$tmp/out/$core_lib"

ts_src="$tmp/tree-sitter-typescript-$TYPESCRIPT_VERSION"
for grammar in typescript tsx; do
    lib="$(library_name "tree-sitter-$grammar")"
    echo "Compiling $lib..."
    cc -shared -fPIC -O2 \
        -I "$ts_src/$grammar/src" \
        "$ts_src/$grammar/src/parser.c" "$ts_src/$grammar/src/scanner.c" \
        -o "$tmp/out/$lib"
done

js_src="$tmp/tree-sitter-javascript-$JAVASCRIPT_VERSION"
js_lib="$(library_name tree-sitter-javascript)"
echo "Compiling $js_lib..."
cc -shared -fPIC -O2 \
    -I "$js_src/src" \
    "$js_src/src/parser.c" "$js_src/src/scanner.c" \
    -o "$tmp/out/$js_lib"

# Staged into place only after every compile succeeded, so a failed run cannot leave a
# half-written library — or a core without its grammars — for the next process to load.
mkdir -p "$lib_dir"
for lib in "$core_lib" "${grammar_libs[@]}"; do
    cp "$tmp/out/$lib" "$lib_dir/$lib.partial"
    mv -f "$lib_dir/$lib.partial" "$lib_dir/$lib"
done

echo "Installed $core_lib and ${#grammar_libs[@]} grammars into $lib_dir"
