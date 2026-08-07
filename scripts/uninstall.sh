#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/uninstall.sh [options]
  --apply              Actually perform the uninstall (default is a dry run)
  --prefix DIR         Install directory (default: $HOME/.local/bin)
  --purge              ALSO delete the Engram home (~/.engram) and everything in it
  -h, --help           Show usage
EOF
}

apply=false
prefix="$HOME/.local/bin"
purge=false

while [ $# -gt 0 ]; do
    case "$1" in
        --apply)
            apply=true
            shift
            ;;
        --prefix)
            if [ $# -lt 2 ]; then
                echo "error: --prefix requires a value" >&2
                exit 1
            fi
            prefix="$2"
            shift 2
            ;;
        --purge)
            purge=true
            shift
            ;;
        -h|--help)
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

target="$prefix/engram"

say() {
    echo "$@"
}

would() {
    echo "would: $*"
}

# Removes the delimited PATH block (and the single blank line the installer
# put before it, if present) from rc_file, leaving every other line
# byte-identical to what the installer found there.
strip_path_block() {
    local rc_file="$1" tmp start_line rel_end_line end_line cut_from prev_line link_dir hops

    # Follow a symlinked rc file to the file it actually names — see the matching
    # comment in install.sh. Replacing a dotfile manager's symlink with a regular
    # file detaches the user's config from the repository that manages it, and
    # nothing would report that it had happened.
    hops=0
    while [ -L "$rc_file" ] && [ "$hops" -lt 16 ]; do
        link_dir=$(dirname "$rc_file")
        rc_file=$(readlink "$rc_file")
        case "$rc_file" in
            /*) ;;
            *) rc_file="$link_dir/$rc_file" ;;
        esac
        hops=$((hops + 1))
    done

    # tmp lives next to rc_file (not under mktemp's /tmp) so the final mv is
    # a same-filesystem rename, and is seeded via cp -p so truncating and
    # rewriting it in place with >/>> preserves rc_file's original mode.
    tmp="$rc_file.engram-tmp-$$"
    cp -p "$rc_file" "$tmp"

    start_line=$(grep -nxF '# >>> engram >>>' "$rc_file" | head -1 | cut -d: -f1)
    rel_end_line=$(tail -n "+$start_line" "$rc_file" | grep -nxF '# <<< engram <<<' | head -1 | cut -d: -f1)
    end_line=$((start_line + rel_end_line - 1))

    cut_from="$start_line"
    if [ "$start_line" -gt 1 ]; then
        prev_line=$(sed -n "$((start_line - 1))p" "$rc_file")
        if [ -z "$prev_line" ]; then
            cut_from=$((start_line - 1))
        fi
    fi

    # A block that starts at line 1 means install.sh created this file; BSD head rejects
    # -n 0, so an empty prefix is written as truncation, not as head.
    if [ "$cut_from" -gt 1 ]; then
        head -n "$((cut_from - 1))" "$rc_file" > "$tmp"
    else
        : > "$tmp"
    fi
    tail -n "+$((end_line + 1))" "$rc_file" >> "$tmp"

    mv "$tmp" "$rc_file"
}

# --- Resolve the Engram home path up front. It informs both the --purge
#     step and the summary, and must happen before the binary is removed
#     below, since resolving it (correctly, honoring ENGRAM_HOME) prefers
#     asking the binary itself via its own `home` command. ---

engram_home=""
if [ -x "$target" ]; then
    home_output=$("$target" home 2>/dev/null || true)
    engram_home=$(printf '%s\n' "$home_output" | sed -n 's/^Root=//p')
fi
if [ -z "$engram_home" ]; then
    engram_home="${ENGRAM_HOME:-$HOME/.engram}"
fi

# --- Stop the daemon if the binary is present ---

if [ -x "$target" ]; then
    if $apply; then
        say "Stopping engram daemon at $target ..."
        "$target" stop || true
    else
        would "stop the engram daemon at $target (ignoring failure)"
    fi
fi

# --- Remove the Claude Code plugin and marketplace, if claude is present ---

if command -v claude >/dev/null 2>&1; then
    if $apply; then
        say "Removing the Claude Code plugin and marketplace (tolerating absence) ..."
        claude plugin uninstall engram -y || true
        claude plugin marketplace remove engram || true
    else
        would "claude plugin uninstall engram -y (tolerating absence)"
        would "claude plugin marketplace remove engram (tolerating absence)"
    fi
else
    say "claude is not on PATH; skipping plugin/marketplace removal."
fi

# --- Take back the MCP tool permissions we granted ---

# This has to happen while the binary and the home are both still here: the record of which
# permissions.allow entries were ours lives in the home, and reading it is the only thing
# separating an entry this installer added from one the user wrote themselves.

permissions_result=none
if [ -x "$target" ]; then
    if $apply; then
        revoke_output=$("$target" permissions --remove --apply 2>&1 || true)
        printf '%s\n' "$revoke_output"
        case "$revoke_output" in
            *"Removed "*) permissions_result=removed ;;
        esac
    else
        would "take back only the permissions.allow entries Engram added to Claude Code's settings"
        "$target" permissions --remove 2>&1 || true
    fi
fi

# --- Remove the installed binary ---

if [ -f "$target" ]; then
    if $apply; then
        rm -f "$target"
        say "Removed $target"
    else
        would "remove $target"
    fi
else
    say "$target does not exist; nothing to remove."
fi

# engram's AOT build loads SQLite by dlopen from beside the executable, so install.sh
# puts a copy of the library in the prefix. Leaving it behind would leave a stray
# native library in somebody's bin directory forever. Named exactly, never globbed:
# removing whatever else happens to be in a bin directory is not this script's business.
case "$(uname -s)" in
    Darwin) sidecar_name="libe_sqlite3.dylib" ;;
    *) sidecar_name="libe_sqlite3.so" ;;
esac
sidecar_target="$prefix/$sidecar_name"

if [ -f "$sidecar_target" ]; then
    if $apply; then
        rm -f "$sidecar_target"
        say "Removed $sidecar_target"
    else
        would "remove $sidecar_target"
    fi
fi

# --- Remove the llama.cpp natives the install recorded ---

# The named-exactly rule above, kept under a directory whose contents vary by platform:
# install.sh writes runtimes/.engram-manifest listing every file it copied, and this
# removes precisely that list. A file somebody else put under runtimes/ is not in the
# manifest and survives, along with any directory that holding it keeps non-empty.
natives_root="$prefix/runtimes"
natives_manifest="$natives_root/.engram-manifest"

if [ -f "$natives_manifest" ]; then
    if $apply; then
        while IFS= read -r rel; do
            case "$rel" in
                ""|*..*|/*) continue ;;
            esac
            rm -f "$natives_root/$rel"
        done < "$natives_manifest"
        rm -f "$natives_manifest"
        [ -d "$natives_root" ] && find "$natives_root" -type d -empty -delete 2>/dev/null || true
        say "Removed the llama.cpp natives listed in the install manifest from $natives_root"
    else
        would "remove the $(wc -l < "$natives_manifest" | tr -d ' ') llama.cpp native files listed in $natives_manifest, then prune emptied directories"
    fi
elif [ -d "$natives_root" ]; then
    say "Not touching $natives_root: no engram manifest there, so nothing in it is provably ours."
fi

# --- Remove the tier-2 analyzer the install recorded ---

roslyn_root="$prefix/roslyn"
roslyn_manifest="$roslyn_root/.engram-manifest"

if [ -f "$roslyn_manifest" ]; then
    if $apply; then
        while IFS= read -r rel; do
            case "$rel" in
                ""|*..*|/*) continue ;;
            esac
            rm -f "$roslyn_root/$rel"
        done < "$roslyn_manifest"
        rm -f "$roslyn_manifest"
        [ -d "$roslyn_root" ] && find "$roslyn_root" -type d -empty -delete 2>/dev/null || true
        say "Removed engram-roslyn as listed in the install manifest from $roslyn_root"
    else
        would "remove the $(wc -l < "$roslyn_manifest" | tr -d ' ') engram-roslyn files listed in $roslyn_manifest, then prune emptied directories"
    fi
elif [ -d "$roslyn_root" ]; then
    say "Not touching $roslyn_root: no engram manifest there, so nothing in it is provably ours."
fi

# --- Remove the PATH symlink, if we created one ---

symlink_removed=false
symlink_removed_path=""
symlink_declined=""

for candidate_dir in "$HOME/bin" "/usr/local/bin"; do
    candidate_link="$candidate_dir/engram"
    if [ -L "$candidate_link" ]; then
        if [ "$(readlink "$candidate_link")" = "$target" ]; then
            if $apply; then
                rm -f "$candidate_link"
                say "Removed symlink $candidate_link -> $target"
            else
                would "remove symlink $candidate_link -> $target"
            fi
            symlink_removed=true
            symlink_removed_path="$candidate_link"
        else
            symlink_declined="$symlink_declined$candidate_link "
            say "Not removing $candidate_link: it is a symlink but does not point at $target."
        fi
    elif [ -e "$candidate_link" ]; then
        symlink_declined="$symlink_declined$candidate_link "
        say "Not removing $candidate_link: it is not a symlink engram created."
    fi
done

# --- Remove the marked PATH block from whichever startup file contains it ---

path_changed=false
path_backup=""
path_files_touched=()

for candidate in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile"; do
    if [ -f "$candidate" ] && grep -qxF '# >>> engram >>>' "$candidate"; then
        if $apply; then
            timestamp=$(date -u +%Y%m%dT%H%M%SZ)
            backup_file="${candidate}.engram-backup-${timestamp}"
            cp -p "$candidate" "$backup_file"
            say "Backed up $candidate to $backup_file"

            strip_path_block "$candidate"
            say "Removed the engram PATH block from $candidate"
            path_changed=true
            path_backup="$backup_file"
            path_files_touched+=("$candidate")
        else
            would "back up $candidate to ${candidate}.engram-backup-<UTC timestamp>"
            would "remove the engram PATH block from $candidate"
            path_files_touched+=("$candidate")
        fi
    fi
done

# --- --purge ---

if $purge; then
    if [ -z "$engram_home" ] || [ "$engram_home" = "/" ] || [ "$engram_home" = "$HOME" ]; then
        echo "error: refusing to purge suspicious Engram home path: '$engram_home'" >&2
        exit 1
    fi

    if [ -d "$engram_home" ]; then
        file_count=$(find "$engram_home" -type f | wc -l | tr -d ' ')
        total_size=$(du -sh "$engram_home" | cut -f1)

        if $apply; then
            say "Purging Engram home: $engram_home ($file_count files, $total_size)"
            rm -rf "$engram_home"
            say "Deleted $engram_home"
        else
            would "delete the Engram home at $engram_home ($file_count files, $total_size)"
        fi
    else
        say "--purge given, but no Engram home found at $engram_home; nothing to delete."
    fi
fi

# --- Summary ---

echo
if $apply; then
    echo "Summary:"
    if [ -f "$target" ]; then
        echo "  engram binary: still present at $target (removal may have failed)"
    else
        echo "  engram binary: removed (or was already absent) from $target"
    fi
    if $symlink_removed; then
        echo "  PATH: removed symlink $symlink_removed_path -> $target"
    fi
    if $path_changed; then
        echo "  PATH: removed the engram block from: ${path_files_touched[*]}"
        echo "  Backup: $path_backup"
    fi
    if ! $symlink_removed && ! $path_changed; then
        echo "  PATH: no engram symlink or PATH block found; nothing changed"
    fi
    if [ -n "$symlink_declined" ]; then
        echo "  PATH: left untouched (not ours): $symlink_declined"
    fi
    case "$permissions_result" in
        removed)
            echo "  MCP tool permissions: removed the entries Engram added"
            ;;
        *)
            echo "  MCP tool permissions: none of ours found; Claude Code's settings left alone"
            ;;
    esac
    if $purge; then
        echo "  Engram home: purged ($engram_home)"
    else
        echo "  Engram home: left untouched. Nothing under $engram_home was deleted; pass --purge to remove it."
    fi
else
    echo "Dry run only — nothing was changed. Re-run with --apply to perform this uninstall."
fi
