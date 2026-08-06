#!/usr/bin/env bash
# fetch-vec0.sh — put the sqlite-vec loadable extension in Engram's lib/ directory.
#
# Engram loads this into its own process, so it is fetched like a dependency and verified
# like one: the archive digest is pinned in this file, checked before anything is written,
# and a mismatch aborts rather than warns. The digests came from the release metadata and
# were confirmed against a downloaded archive, so they check the bytes rather than restating
# a hope.
#
# The version is pinned too, and not merely for reproducibility: the extension version and
# the vec0 table shape Engram creates are coupled, so an upgrade is a schema decision.
#
# Usage: scripts/fetch-vec0.sh [--home <dir>] [--force] [--print-path]

set -euo pipefail

VERSION="0.1.9"

# platform → archive suffix, library filename, sha256 of the archive.
digest_for() {
    case "$1" in
        macos-aarch64)  echo "8282126333399ddfe98bbbcc7a1936e7252625aac49df056a98be602e46bfd29" ;;
        macos-x86_64)   echo "53ad76e400786515e2edcaed2f01271dda846316390b761fadbd2dcf56aa4713" ;;
        linux-x86_64)   echo "b959baa1d8dc88861b1edb337b8587178cdcb12d60b4998f9d10b6a82052d5d7" ;;
        linux-aarch64)  echo "ea03d39541e478fab5974253c461e1cb5d77742f69e40cf96e3fad5bc309a37c" ;;
        windows-x86_64) echo "51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983" ;;
        *) return 1 ;;
    esac
}

library_for() {
    case "$1" in
        macos-*)   echo "vec0.dylib" ;;
        linux-*)   echo "vec0.so" ;;
        windows-*) echo "vec0.dll" ;;
        *) return 1 ;;
    esac
}

detect_platform() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Darwin) case "$arch" in
                    arm64)  echo "macos-aarch64" ;;
                    x86_64) echo "macos-x86_64" ;;
                    *) return 1 ;;
                esac ;;
        Linux)  case "$arch" in
                    x86_64)        echo "linux-x86_64" ;;
                    aarch64|arm64) echo "linux-aarch64" ;;
                    *) return 1 ;;
                esac ;;
        # Git Bash and MSYS report these; there is no arm64 Windows build published.
        MINGW*|MSYS*|CYGWIN*) echo "windows-x86_64" ;;
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

home_dir="${ENGRAM_HOME:-$HOME/.engram}"
force=0
print_path=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --home)       home_dir="$2"; shift 2 ;;
        --force)      force=1; shift ;;
        --print-path) print_path=1; shift ;;
        -h|--help)
            sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "error: unknown argument: $1" >&2; exit 2 ;;
    esac
done

platform="$(detect_platform)" || {
    echo "error: unsupported platform $(uname -s)/$(uname -m)." >&2
    echo "       sqlite-vec publishes macOS (arm64/x64), Linux (x64/arm64) and Windows (x64)." >&2
    exit 1
}

library="$(library_for "$platform")"
expected="$(digest_for "$platform")"
lib_dir="$home_dir/lib"
dest="$lib_dir/$library"

if [[ $print_path -eq 1 ]]; then
    echo "$dest"
    exit 0
fi

if [[ -f "$dest" && $force -eq 0 ]]; then
    echo "sqlite-vec v$VERSION already installed at $dest"
    echo "(pass --force to re-fetch)"
    exit 0
fi

archive="sqlite-vec-${VERSION}-loadable-${platform}.tar.gz"
url="https://github.com/asg017/sqlite-vec/releases/download/v${VERSION}/${archive}"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Fetching sqlite-vec v$VERSION for $platform..."
curl -fsSL "$url" -o "$tmp/$archive"

actual="$(sha256_of "$tmp/$archive")"
if [[ "$actual" != "$expected" ]]; then
    echo "error: checksum mismatch for $archive" >&2
    echo "       expected $expected" >&2
    echo "       actual   $actual" >&2
    echo "       Refusing to install. Engram loads this into its own process." >&2
    exit 1
fi
echo "  checksum ok"

tar -xzf "$tmp/$archive" -C "$tmp"

found="$(find "$tmp" -name "$library" -type f | head -1)"
if [[ -z "$found" ]]; then
    echo "error: $library not found inside $archive" >&2
    exit 1
fi

# Staged into place only after the archive verified, so a failed run cannot leave a
# half-written library that the next process would happily load.
mkdir -p "$lib_dir"
cp "$found" "$dest.partial"
mv -f "$dest.partial" "$dest"

echo "Installed $dest"
