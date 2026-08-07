#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/uninstall.sh [options]
  --apply              Actually perform the uninstall (default is a dry run)
  --prefix DIR         Install directory (default: $HOME/.local/bin)
  --purge              ALSO delete the Engram home (~/.engram) — your memory store
  --remove-backups     With the home removal, delete ~/.engram/backups too.
                       Kept by default: backups hold the plain-text journal that
                       can restore this memory into a fresh install.
  -h, --help           Show usage

An interactive --apply first shows what is installed, then confirms each item
before removing anything. A piped run takes the defaults: binary, PATH entry,
plugin, and permissions are removed; the home only with --purge; backups are
kept unless --remove-backups says otherwise.
EOF
}

apply=false
prefix="$HOME/.local/bin"
purge=false
remove_backups_flag=false

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
        --remove-backups)
            remove_backups_flag=true
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

# Same gate as install.sh: styling only where a person is watching.
if [ -t 1 ] && [ -n "${TERM:-}" ] && [ "${TERM:-}" != dumb ] && [ -z "${NO_COLOR:-}" ]; then
    T_BOLD=$(printf '\033[1m')
    T_CYAN=$(printf '\033[36m')
    T_RESET=$(printf '\033[0m')
else
    T_BOLD=""
    T_CYAN=""
    T_RESET=""
fi

step() {
    echo
    echo "${T_CYAN}${T_BOLD}── $*${T_RESET}"
}

# One [Y/n] question, defaulting to yes — for items an uninstaller exists to remove.
ask() {
    printf '%s [Y/n] ' "$1"
    local reply=""
    read -r reply < /dev/tty || true
    case "$reply" in
        [nN]*) return 1 ;;
        *) return 0 ;;
    esac
}

# One [y/N] question, defaulting to no — for the removals that destroy something
# an uninstall does not imply wanting gone.
ask_no() {
    printf '%s [y/N] ' "$1"
    local reply=""
    read -r reply < /dev/tty || true
    case "$reply" in
        [yY]*) return 0 ;;
        *) return 1 ;;
    esac
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

# --- Resolve the Engram home path up front. It informs the inventory, the home
#     removal, and the summary, and must happen before the binary is removed,
#     since resolving it (correctly, honoring ENGRAM_HOME) prefers asking the
#     binary itself via its own `home` command. ---

engram_home=""
if [ -x "$target" ]; then
    home_output=$("$target" home 2>/dev/null || true)
    engram_home=$(printf '%s\n' "$home_output" | sed -n 's/^Root=//p')
fi
if [ -z "$engram_home" ]; then
    engram_home="${ENGRAM_HOME:-$HOME/.engram}"
fi

# --- 1. Inventory: look at what is actually installed before deciding anything ---

found_binary=false
[ -f "$target" ] && found_binary=true

found_path=false
for candidate_dir in "$HOME/bin" "/usr/local/bin"; do
    if [ -L "$candidate_dir/engram" ] && [ "$(readlink "$candidate_dir/engram")" = "$target" ]; then
        found_path=true
    fi
done
for candidate in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile"; do
    if [ -f "$candidate" ] && grep -qxF '# >>> engram >>>' "$candidate"; then
        found_path=true
    fi
done

found_claude=false
command -v claude >/dev/null 2>&1 && found_claude=true

found_home=false
[ -d "$engram_home" ] && found_home=true

# Backups count as present only when there is something in them: an empty directory
# protects nothing, and keeping it would make "kept your backups" an empty promise.
found_backups=false
if [ -d "$engram_home/backups" ] && [ -n "$(ls -A "$engram_home/backups" 2>/dev/null)" ]; then
    found_backups=true
fi

step "Installed"
$found_binary && say "  binary and its runtime files at $target"
$found_claude && say "  Claude Code plugin (claude is on PATH; removal tolerates absence)"
$found_binary && say "  MCP tool permissions (only the entries Engram itself added)"
$found_path && say "  PATH entry (symlink or shell startup block)"
if $found_home; then
    home_files=$(find "$engram_home" -type f | wc -l | tr -d ' ')
    home_size=$(du -sh "$engram_home" | cut -f1)
    say "  Engram home at $engram_home ($home_files files, $home_size) — your memory store"
fi
$found_backups && say "  backups at $engram_home/backups — the plain-text journal that can restore this memory"
if ! $found_binary && ! $found_path && ! $found_home && ! $found_claude; then
    say "  nothing — no binary at $target, no PATH entry, no home at $engram_home"
fi

# --- 2. Decisions, all collected before anything is removed ---

# Piped defaults: what an uninstaller exists to remove goes; the store stays unless
# --purge says otherwise; backups stay unless --remove-backups says otherwise. An
# interactive run confirms each found item instead — the home defaulting to no unless
# --purge already said yes, because uninstalling a program does not imply wanting the
# memory it kept gone.
remove_binary=$found_binary
remove_plugin=$found_claude
remove_permissions=$found_binary
remove_path=$found_path
remove_home=false
if $purge && $found_home; then
    remove_home=true
fi
remove_backups=$remove_backups_flag

if $apply && [ -t 0 ] && [ -r /dev/tty ]; then
    step "Confirm"
    if $found_binary; then
        ask "Remove the engram binary and its runtime files from $prefix?" && remove_binary=true || remove_binary=false
    fi
    if $found_claude; then
        ask "Remove the Claude Code plugin and marketplace registration?" && remove_plugin=true || remove_plugin=false
    fi
    if $found_binary; then
        ask "Take back the Claude Code permission entries Engram added?" && remove_permissions=true || remove_permissions=false
    fi
    if $found_path; then
        ask "Remove the PATH entry (symlink or shell startup block)?" && remove_path=true || remove_path=false
    fi
    if $found_home; then
        if $purge; then
            ask "Delete the Engram home at $engram_home — your memory store?" && remove_home=true || remove_home=false
        else
            ask_no "Also delete the Engram home at $engram_home — your memory store?" && remove_home=true || remove_home=false
        fi
    fi
    if $remove_home && $found_backups && ! $remove_backups_flag; then
        ask_no "Also remove the backups? They are what can restore this memory later" && remove_backups=true || remove_backups=false
    fi
fi

# --- 3. Act. Order matters twice: the daemon stops before its binary goes, and the
#     permission revoke runs while the binary and the home both still exist — the
#     record of which permissions.allow entries were ours lives in the home, and
#     reading it is the only thing separating an entry this installer added from
#     one the user wrote themselves. ---

if [ -x "$target" ] && { $remove_binary || $remove_home; }; then
    if $apply; then
        say "Stopping engram daemon at $target ..."
        "$target" stop || true
    else
        would "stop the engram daemon at $target (ignoring failure)"
    fi
fi

if $remove_plugin; then
    if $apply; then
        say "Removing the Claude Code plugin and marketplace (tolerating absence) ..."
        claude plugin uninstall engram -y || true
        claude plugin marketplace remove engram || true
    else
        would "claude plugin uninstall engram -y (tolerating absence)"
        would "claude plugin marketplace remove engram (tolerating absence)"
    fi
elif ! $found_claude; then
    say "claude is not on PATH; skipping plugin/marketplace removal."
fi

permissions_result=none
if $remove_permissions && [ -x "$target" ]; then
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

if $remove_binary; then
    if [ -f "$target" ]; then
        if $apply; then
            rm -f "$target"
            say "Removed $target"
        else
            would "remove $target"
        fi
    fi
else
    if ! $found_binary; then
        say "$target does not exist; nothing to remove."
    fi
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

if $remove_binary && [ -f "$sidecar_target" ]; then
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

if $remove_binary && [ -f "$natives_manifest" ]; then
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
elif [ -d "$natives_root" ] && [ ! -f "$natives_manifest" ]; then
    say "Not touching $natives_root: no engram manifest there, so nothing in it is provably ours."
fi

# --- Remove the tier-2 analyzer the install recorded ---

roslyn_root="$prefix/roslyn"
roslyn_manifest="$roslyn_root/.engram-manifest"

if $remove_binary && [ -f "$roslyn_manifest" ]; then
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
elif [ -d "$roslyn_root" ] && [ ! -f "$roslyn_manifest" ]; then
    say "Not touching $roslyn_root: no engram manifest there, so nothing in it is provably ours."
fi

# --- Remove the PATH symlink, if we created one ---

symlink_removed=false
symlink_removed_path=""
symlink_declined=""

if $remove_path; then
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
fi

# --- Remove the marked PATH block from whichever startup file contains it ---

path_changed=false
path_backup=""
path_files_touched=()

if $remove_path; then
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
fi

# --- The Engram home ---

backups_kept=false
if $remove_home; then
    if [ -z "$engram_home" ] || [ "$engram_home" = "/" ] || [ "$engram_home" = "$HOME" ]; then
        echo "error: refusing to delete suspicious Engram home path: '$engram_home'" >&2
        exit 1
    fi

    if [ -d "$engram_home" ]; then
        file_count=$(find "$engram_home" -type f | wc -l | tr -d ' ')
        total_size=$(du -sh "$engram_home" | cut -f1)

        if $apply; then
            if $found_backups && ! $remove_backups; then
                say "Deleting the Engram home at $engram_home ($file_count files, $total_size), keeping backups/ ..."
                for entry in "$engram_home"/* "$engram_home"/.[!.]*; do
                    [ -e "$entry" ] || [ -L "$entry" ] || continue
                    [ "$(basename "$entry")" = "backups" ] && continue
                    rm -rf "$entry"
                done
                backups_kept=true
                say "Deleted everything under $engram_home except backups/"
            else
                say "Deleting the Engram home at $engram_home ($file_count files, $total_size) ..."
                rm -rf "$engram_home"
                say "Deleted $engram_home"
            fi
        else
            if $found_backups && ! $remove_backups; then
                would "delete the Engram home at $engram_home ($file_count files, $total_size), keeping backups/"
            else
                would "delete the Engram home at $engram_home ($file_count files, $total_size)"
            fi
        fi
    else
        say "Home removal requested, but no Engram home found at $engram_home; nothing to delete."
    fi
fi

# --- Summary ---

echo
if $apply; then
    echo "${T_BOLD}Summary:${T_RESET}"
    if $remove_binary; then
        if [ -f "$target" ]; then
            echo "  engram binary: still present at $target (removal may have failed)"
        else
            echo "  engram binary: removed (or was already absent) from $target"
        fi
    elif $found_binary; then
        echo "  engram binary: kept at $target (you said no)"
    else
        echo "  engram binary: was not installed at $target"
    fi
    if $symlink_removed; then
        echo "  PATH: removed symlink $symlink_removed_path -> $target"
    fi
    if $path_changed; then
        echo "  PATH: removed the engram block from: ${path_files_touched[*]}"
        echo "  Backup: $path_backup"
    fi
    if $found_path && ! $remove_path; then
        echo "  PATH: entry kept (you said no)"
    elif ! $symlink_removed && ! $path_changed; then
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
            if $found_binary && ! $remove_permissions; then
                echo "  MCP tool permissions: kept (you said no)"
            else
                echo "  MCP tool permissions: none of ours found; Claude Code's settings left alone"
            fi
            ;;
    esac
    if $remove_home && [ ! -d "$engram_home" ]; then
        echo "  Engram home: deleted ($engram_home)"
    elif $backups_kept; then
        echo "  Engram home: deleted, except backups/ — restore later with: engram backup replay"
        echo "  Backups kept: $engram_home/backups"
    elif $remove_home; then
        echo "  Engram home: removal attempted; check $engram_home"
    else
        echo "  Engram home: left untouched. Nothing under $engram_home was deleted; pass --purge to remove it."
    fi
else
    echo "Dry run only — nothing was changed. Re-run with --apply to perform this uninstall."
    echo "An interactive --apply will confirm each item above before removing anything."
fi
