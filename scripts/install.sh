#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/install.sh [options]
  --apply              Actually perform the installation (default is a dry run)
  --prefix DIR         Install directory (default: $HOME/.local/bin)
  --binary PATH        Install this prebuilt binary instead of building one
  --no-path            Do not modify any shell startup file
  --with-plugin        Also register the Claude Code marketplace and install the plugin
  --grant-permissions  Allow Claude Code to call Engram's memory tools without prompting
  --no-grant-permissions
                       Never grant them, and do not ask
  -h, --help           Show usage
EOF
}

apply=false
prefix="$HOME/.local/bin"
binary_override=""
no_path=false
with_plugin=false
# ask | yes | no. "ask" only ever asks a terminal; a non-interactive run declines, because
# silence from a pipe is not consent to edit somebody's settings file.
grant_permissions=ask

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
        --binary)
            if [ $# -lt 2 ]; then
                echo "error: --binary requires a value" >&2
                exit 1
            fi
            binary_override="$2"
            shift 2
            ;;
        --no-path)
            no_path=true
            shift
            ;;
        --with-plugin)
            with_plugin=true
            shift
            ;;
        --grant-permissions)
            grant_permissions=yes
            shift
            ;;
        --no-grant-permissions)
            grant_permissions=no
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

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd "$script_dir/.." && pwd)
target="$prefix/engram"

say() {
    echo "$@"
}

would() {
    echo "would: $*"
}

cleanup_dirs=()
cleanup() {
    local d
    for d in "${cleanup_dirs[@]+"${cleanup_dirs[@]}"}"; do
        [ -n "$d" ] && [ -d "$d" ] && rm -rf "$d"
    done
}
trap cleanup EXIT

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
                aarch64|arm64) echo "linux-arm64" ;;
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

# Writes (or replaces) the delimited PATH block in rc_file. If the markers
# already exist, only the lines between them are replaced, so re-running the
# installer never appends a second block.
install_path_block() {
    local rc_file="$1" block="$2" tmp start_line rel_end_line end_line link_dir hops

    # Follow a symlinked rc file to the file it actually names. Dotfile managers
    # (stow, chezmoi, yadm) symlink ~/.zshrc into a repository, and mv'ing over
    # the link would replace it with a regular file — silently detaching the
    # user's config from the repo that manages it. Writing through the link, as
    # a plain redirect would, is the behaviour to preserve; the atomic replace
    # just has to happen at the other end of it. readlink is used without -f
    # because BSD readlink has not always had it.
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
    if [ -f "$rc_file" ]; then
        cp -p "$rc_file" "$tmp"
    else
        : > "$tmp"
    fi

    if [ -f "$rc_file" ] && grep -qxF '# >>> engram >>>' "$rc_file"; then
        start_line=$(grep -nxF '# >>> engram >>>' "$rc_file" | head -1 | cut -d: -f1)
        rel_end_line=$(tail -n "+$start_line" "$rc_file" | grep -nxF '# <<< engram <<<' | head -1 | cut -d: -f1)
        end_line=$((start_line + rel_end_line - 1))

        head -n "$((start_line - 1))" "$rc_file" > "$tmp"
        printf '%s\n' "$block" >> "$tmp"
        tail -n "+$((end_line + 1))" "$rc_file" >> "$tmp"
    elif [ -f "$rc_file" ]; then
        cat "$rc_file" > "$tmp"
        printf '\n' >> "$tmp"
        printf '%s\n' "$block" >> "$tmp"
    else
        printf '%s\n' "$block" >> "$tmp"
    fi

    mv "$tmp" "$rc_file"
}

# Deliberately excluded from PATH-symlink candidates: Homebrew manages the
# symlinks in its own prefix, and an unbrewed one there makes `brew doctor`
# complain and can be silently clobbered by Homebrew's own link/unlink.
is_homebrew_prefix_dir() {
    local dir="$1" brew_prefix
    case "$dir" in
        /opt/homebrew|/opt/homebrew/*) return 0 ;;
        /usr/local/Homebrew|/usr/local/Homebrew/*) return 0 ;;
    esac
    if command -v brew >/dev/null 2>&1; then
        brew_prefix=$(brew --prefix 2>/dev/null || true)
        if [ -n "$brew_prefix" ]; then
            case "$dir" in
                "$brew_prefix"|"$brew_prefix"/*) return 0 ;;
            esac
        fi
    fi
    return 1
}

# --- 1. Preflight ---

if [ -z "$binary_override" ]; then
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "error: dotnet is not on PATH (needed to build; pass --binary to skip building)" >&2
        exit 1
    fi
    rid=$(detect_rid)
    say "Detected runtime identifier: $rid"
else
    if [ ! -f "$binary_override" ]; then
        echo "error: --binary path not found: $binary_override" >&2
        exit 1
    fi
fi

# --- 2. Stop a running daemon before replacing the binary ---

if [ -x "$target" ]; then
    if $apply; then
        say "Stopping existing daemon at $target ..."
        "$target" stop || true
    else
        would "stop the existing daemon at $target (ignoring failure)"
    fi
fi

# --- 3. Build, unless --binary was given ---

if [ -n "$binary_override" ]; then
    binary_path="$binary_override"
    say "Using prebuilt binary: $binary_path"
elif $apply; then
    staging_dir=$(mktemp -d)
    cleanup_dirs+=("$staging_dir")
    say "Building engram for $rid into $staging_dir ..."
    dotnet publish "$repo_root/src/Engram.Cli" \
        -c Release \
        -r "$rid" \
        -o "$staging_dir"

    binary_path="$staging_dir/engram"
    if [ ! -x "$binary_path" ]; then
        echo "error: expected published binary not found at $binary_path" >&2
        exit 1
    fi

    size_before=$(du -sh "$staging_dir" | cut -f1)
    say "Removing debug symbols from $staging_dir ..."
    find "$staging_dir" -maxdepth 1 -type f -name '*.pdb' -delete
    find "$staging_dir" -maxdepth 1 -type d -name '*.dSYM' -exec rm -rf {} +
    size_after=$(du -sh "$staging_dir" | cut -f1)
    say "Staging size before symbol cleanup: $size_before"
    say "Staging size after symbol cleanup:  $size_after"
else
    would "dotnet publish $repo_root/src/Engram.Cli -c Release -r $rid -o <temp staging dir>"
    would "remove .pdb files and .dSYM directories from the staging dir"
    binary_path="<built binary>"
fi

# --- 4. Verify the built binary runs before installing it ---

if $apply; then
    verify_home=$(mktemp -d)
    cleanup_dirs+=("$verify_home")
    say "Verifying the binary runs (ENGRAM_HOME=$verify_home) ..."
    if ! ENGRAM_HOME="$verify_home" "$binary_path" home >/dev/null; then
        echo "error: '$binary_path home' did not exit 0 — refusing to install an unverified binary" >&2
        exit 1
    fi
else
    would "verify the binary runs (engram home) against a throwaway ENGRAM_HOME"
fi

# --- 5. Install ---

if $apply; then
    mkdir -p "$prefix"
    # cp rewrites $target in place; on macOS this succeeds even if a daemon
    # is still running from that path, so its pages would change underneath
    # it. Install to a sibling file and mv over the destination instead — mv
    # swaps the directory entry, leaving any running process on its old inode.
    tmp_target="$target.new-$$"
    cp "$binary_path" "$tmp_target"
    chmod 755 "$tmp_target"
    mv "$tmp_target" "$target"
    say "Installed $target"
else
    would "mkdir -p $prefix"
    would "install binary to $target (mode 755, via atomic replace)"
fi

# --- 6. PATH ---

path_changed=false
path_backup=""
path_rc_file=""
path_advice=""
symlink_path=""
symlink_blocked=""

if $no_path; then
    say "Skipping PATH setup (--no-path)."
else
    case ":$PATH:" in
        *":$prefix:"*)
            say "$prefix is already on \$PATH; not touching any startup file."
            ;;
        *)
            # Prefer symlinking from a directory already on PATH over editing
            # a shell startup file: editing .zshrc is the most invasive thing
            # this installer does, so it is the last resort, not the first.
            for candidate_dir in "$HOME/bin" "/usr/local/bin"; do
                case ":$PATH:" in
                    *":$candidate_dir:"*) ;;
                    *) continue ;;
                esac
                [ -d "$candidate_dir" ] || continue
                [ -w "$candidate_dir" ] || continue
                if is_homebrew_prefix_dir "$candidate_dir"; then
                    continue
                fi

                candidate_link="$candidate_dir/engram"
                if [ -e "$candidate_link" ] || [ -L "$candidate_link" ]; then
                    if [ -L "$candidate_link" ] && [ "$(readlink "$candidate_link")" = "$target" ]; then
                        symlink_path="$candidate_link"
                        say "$candidate_link already links to $target; nothing to do."
                        break
                    else
                        symlink_blocked="$symlink_blocked$candidate_link "
                        say "Not using $candidate_link for \$PATH: something else already exists there; leaving it untouched."
                        continue
                    fi
                fi

                if $apply; then
                    ln -s "$target" "$candidate_link"
                    say "Symlinked $candidate_link -> $target"
                else
                    would "symlink $candidate_link -> $target"
                fi
                symlink_path="$candidate_link"
                break
            done

            if [ -z "$symlink_path" ]; then
                if [[ "$prefix" == "$HOME"/* ]]; then
                    prefix_repr="\$HOME${prefix#"$HOME"}"
                else
                    prefix_repr="$prefix"
                fi

                shell_name=$(basename "${SHELL:-}")
                rc_file=""
                case "$shell_name" in
                    zsh)
                        rc_file="$HOME/.zshrc"
                        ;;
                    bash)
                        if [ -f "$HOME/.bashrc" ]; then
                            rc_file="$HOME/.bashrc"
                        else
                            rc_file="$HOME/.bash_profile"
                        fi
                        ;;
                esac

                if [ -z "$rc_file" ]; then
                    if [ "$shell_name" = "fish" ]; then
                        path_advice="set -gx PATH $prefix_repr \$PATH"
                    else
                        path_advice="export PATH=\"$prefix_repr:\$PATH\""
                    fi
                    say "Shell '$shell_name' is not zsh or bash; not editing any startup file."
                    say "Add this line to your shell's startup file yourself:"
                    say "  $path_advice"
                else
                    # Written to a temp file rather than captured directly via
                    # $(cat <<'EOF' ...) — macOS's stock /bin/bash (3.2) mishandles
                    # a heredoc containing case-statement syntax when it's nested
                    # inside a command substitution inside a case statement.
                    block_template_file=$(mktemp)
                    cat <<'BLOCKEOF' > "$block_template_file"
# >>> engram >>>
# Added by engram's installer. Remove with scripts/uninstall.sh.
case ":$PATH:" in
  *":@@PREFIX@@:"*) ;;
  *) export PATH="@@PREFIX@@:$PATH" ;;
esac
# <<< engram <<<
BLOCKEOF
                    block_template=$(cat "$block_template_file")
                    rm -f "$block_template_file"
                    block_content="${block_template//@@PREFIX@@/$prefix_repr}"
                    path_rc_file="$rc_file"

                    if $apply; then
                        if [ -f "$rc_file" ]; then
                            timestamp=$(date -u +%Y%m%dT%H%M%SZ)
                            path_backup="${rc_file}.engram-backup-${timestamp}"
                            cp -p "$rc_file" "$path_backup"
                            say "Backed up $rc_file to $path_backup"
                        fi

                        install_path_block "$rc_file" "$block_content"
                        say "Updated $rc_file to add $prefix to \$PATH"
                        path_changed=true
                    else
                        would "back up $rc_file (if it exists) to ${rc_file}.engram-backup-<UTC timestamp>"
                        if [ -f "$rc_file" ] && grep -qxF '# >>> engram >>>' "$rc_file"; then
                            would "replace the existing engram PATH block in $rc_file"
                        else
                            would "append an engram PATH block to $rc_file"
                        fi
                    fi
                fi
            fi
            ;;
    esac
fi

# --- 7. Initialise the home ---

if $apply; then
    say "Initialising the Engram home ..."
    "$target" init
else
    would "run $target init to initialise the Engram home (idempotent, will not overwrite an existing config)"
fi

# --- 8. --with-plugin ---

plugin_installed=false
if $with_plugin; then
    if $apply; then
        if command -v claude >/dev/null 2>&1; then
            say "Registering the Claude Code marketplace and installing the plugin ..."
            claude plugin marketplace add "$repo_root"
            claude plugin install engram@engram
            plugin_installed=true
        else
            say "claude is not on PATH; run these commands yourself to install the plugin:"
            say "  claude plugin marketplace add $repo_root"
            say "  claude plugin install engram@engram"
        fi
    else
        would "claude plugin marketplace add $repo_root"
        would "claude plugin install engram@engram"
    fi
fi

# --- 9. MCP tool permissions ---

# Without this, Claude Code asks before every engram_recall. That is not just friction: M0
# measures whether the model reaches for memory at all, and a dialog in front of each call
# makes the number a measurement of the dialog. Still opt-in — it edits a file we do not own.

grant_result=skipped
if [ "$grant_permissions" != no ]; then
    if ! $apply; then
        would "offer to add Engram's memory tools to permissions.allow in Claude Code's user settings"
        if [ -x "$target" ]; then
            "$target" permissions || true
        fi
    elif [ "$grant_permissions" = yes ]; then
        "$target" permissions --apply && grant_result=granted
    elif [ -t 0 ] && [ -r /dev/tty ]; then
        echo
        "$target" permissions || true
        printf 'Grant these now? [y/N] '
        reply=""
        read -r reply < /dev/tty || true
        case "$reply" in
            [yY]*)
                "$target" permissions --apply && grant_result=granted
                ;;
            *)
                grant_result=declined
                say "Left Claude Code's settings alone. Grant later with: engram permissions --apply"
                ;;
        esac
    else
        grant_result=declined
        say "Not a terminal, so not asking about tool permissions. Grant with: engram permissions --apply"
    fi
fi

# --- 10. Summary ---

echo
if $apply; then
    echo "Summary:"
    echo "  Installed engram to: $target"
    if $no_path; then
        echo "  PATH: not modified (--no-path)"
    elif [ -n "$symlink_path" ]; then
        echo "  PATH: symlinked $symlink_path -> $target (no shell startup file touched)"
    elif $path_changed; then
        if [ -n "$path_backup" ]; then
            echo "  PATH: added $prefix to $path_rc_file (backup: $path_backup)"
        else
            echo "  PATH: added $prefix to $path_rc_file (newly created, no backup needed)"
        fi
        if [ -n "$symlink_blocked" ]; then
            echo "  PATH: symlink candidates skipped (already occupied by something else): $symlink_blocked"
        fi
    elif [ -n "$path_advice" ]; then
        echo "  PATH: not modified automatically (unsupported shell); add this line yourself:"
        echo "    $path_advice"
    else
        echo "  PATH: $prefix was already on \$PATH; nothing changed"
    fi
    if $with_plugin; then
        if $plugin_installed; then
            echo "  Claude Code plugin: registered and installed"
        else
            echo "  Claude Code plugin: NOT installed (claude was not on PATH); run the commands printed above"
        fi
    fi
    case "$grant_result" in
        granted)
            echo "  MCP tool permissions: granted (recall, remember, digest, status)"
            ;;
        declined)
            echo "  MCP tool permissions: not granted; run 'engram permissions --apply' to change that"
            ;;
        skipped)
            echo "  MCP tool permissions: not touched (--no-grant-permissions)"
            ;;
    esac
    echo
    echo "Next steps:"
    if $path_changed; then
        echo "  Open a new shell, or run: source $path_rc_file"
    fi
    if $with_plugin && $plugin_installed; then
        echo "  In a running Claude Code session, run: /reload-plugins"
    fi
    if [ "$grant_result" = granted ]; then
        echo "  Restart Claude Code so it re-reads its settings and picks up the grant"
    fi
else
    echo "Dry run only — nothing was changed. Re-run with --apply to perform this installation."
fi
