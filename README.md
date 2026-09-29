# claude-engram

Engram has moved to **[agentic-tool-labs/agent-engram](https://github.com/agentic-tool-labs/agent-engram)**. This repository is archived and no longer updated.

## Switching an existing install

The new repository starts from a fresh history, so `git pull` in an old clone fails with "refusing to merge unrelated histories". Point your clone at the new address, reset it, and rerun the installer:

```sh
cd /path/to/your/clone
git remote set-url origin https://github.com/agentic-tool-labs/agent-engram.git
git fetch origin
git reset --hard origin/main
scripts/install.sh
```

- `git reset --hard` discards uncommitted changes and local commits in the clone, so save anything you want to keep first.
- `scripts/install.sh` rebuilds Engram, replaces the installed binary and restarts the server.
- The Claude Code plugin points at this folder, so keeping the folder keeps the plugin working.
- Your memory store in `~/.engram` is not touched.

To use a fresh clone instead, remove the old plugin marketplace first, since it points at the old folder:

```sh
claude plugin marketplace remove engram
git clone https://github.com/agentic-tool-labs/agent-engram.git
cd agent-engram
scripts/install.sh
```
