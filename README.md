# Engram

A single-binary, fully local temporal memory and code knowledge graph for LLM coding
agents. Unifies three kinds of memory — code structure, project knowledge, and user
preferences — into one append-only store where facts are never mutated or deleted, only
superseded, with the reason for every revision recorded.

The success metric is **tokens the host LLM did not have to load**. Memory is a
substitute for context, not a supplement to it.

- C# / .NET 10, published as a Native AOT single-file binary
- SQLite only (WAL), one database at `~/.engram/engram.db`
- CLI for humans, MCP over local HTTP for agents — `engram start` / `stop` / `status`
- No containers, no runtime install, and nothing that leaves the machine: the server
  binds `127.0.0.1` only and rejects any request carrying an `Origin` header

**Status:** M0 in progress. The CLI, the supervised HTTP daemon, the hooks, the installer
and the Claude Code plugin all exist and are verified against a live Claude Code. There is
no database yet **by design** — M0 is an adoption probe that measures whether the agent
calls the memory tools at all, before anything is built on the assumption that it will.
The evidence that this is the right order is in D12: a sibling tool on the same machine
holds 67,936 unstructured notes and zero structured facts, because the structured half
asks more of the model than the model volunteers.

## Documents

| | |
|---|---|
| [`docs/engram-spec.md`](docs/engram-spec.md) | Design specification, Rev D |
| [`docs/engram-implementation-plan.md`](docs/engram-implementation-plan.md) | Locked decisions, spec errata, milestones, testing strategy |
| [`docs/engram-schema.sql`](docs/engram-schema.sql) | Canonical M1 database schema |
| [`docs/engram-design.html`](docs/engram-design.html) | Visual design sheet |

Start with the implementation plan — it records fourteen decisions (D1–D14) resolving
questions the spec left open, the places where the spec contradicts itself, and for each
one the argument or measurement that settled it.

## Building

Requires the .NET 10 SDK. On macOS, Native AOT also needs Xcode command line tools.

```
dotnet build -c Release
dotnet test  -c Release
dotnet publish src/Engram.Cli -c Release -r osx-arm64 -o out
```

## Installing

`scripts/install.sh` builds the binary, installs it, puts it on your `PATH`, and
initialises the Engram home. It **prints what it would do and changes nothing** unless
you pass `--apply`:

```
scripts/install.sh                # dry run — read this first
scripts/install.sh --apply
scripts/install.sh --apply --with-plugin   # also register the Claude Code plugin
```

| | |
|---|---|
| `--apply` | Actually do it. Without this it is a dry run. |
| `--prefix DIR` | Install directory. Default `$HOME/.local/bin`. |
| `--binary PATH` | Install an already-built binary instead of building one. |
| `--no-path` | Do not put the binary on `PATH` by any means. |
| `--with-plugin` | Also run the two `claude plugin` commands below. |

Getting onto `PATH` is tried in order of how invasive it is. If the install directory is
already on `PATH`, nothing happens at all. Otherwise a **symlink** goes into a directory
that is already on `PATH` — one file, removed by one `rm`, and it will never overwrite
something it did not create. Only if that fails does it edit a shell startup file, and
then it backs the file up first and writes a delimited block the uninstaller removes
exactly.

Homebrew's `bin` is deliberately excluded from the symlink candidates: unbrewed symlinks
there make `brew doctor` complain, and Homebrew may clobber them.

Uninstalling is symmetric, and equally a dry run by default:

```
scripts/uninstall.sh              # dry run
scripts/uninstall.sh --apply
scripts/uninstall.sh --apply --purge   # ALSO deletes ~/.engram and all your memory
```

**`--purge` is the only thing that will ever touch `~/.engram`.** Without it the memory
store is left completely alone, and the summary says so explicitly.

### The Claude Code plugin

The repository itself is the marketplace:

```
claude plugin marketplace add /Users/jimcline/git/repos/engram
claude plugin install engram@engram
```

Then `/reload-plugins` in a running session picks up the hooks and the MCP registration;
a full restart is not required.

**The plugin ships no binary.** Its hooks resolve one — `$ENGRAM_BIN`, then
`$HOME/.local/bin/engram`, then `PATH` — which is why the installer above is a
prerequisite rather than a convenience. A plugin installed with no binary present says
so at `SessionStart` instead of failing silently. This also means the plugin cache now
holds only manifests and a few small scripts, so it can travel through a remote
marketplace, and that the binary's path no longer changes when the plugin version does.

`plugin/.claude-plugin/plugin.json` carries a `version`, and Claude Code's cache is
version-pinned at `~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/`, replaced
wholesale on a bump. Editing `hooks/` or the manifests in a working-tree marketplace has
no effect until you bump that version (and the matching entry in
`.claude-plugin/marketplace.json`). Rebuilding the binary no longer needs a bump.

### How the plugin reaches the server

`plugin/.mcp.json` is an `http` entry pointing at `http://127.0.0.1:7433/`. Nothing in
that file can start a server, so `SessionStart` runs `hooks/ensure-server.sh` ahead of
the primer, which calls `engram start` — idempotent, silent, and always exit 0. The
daemon outlives the session that started it, so only the first session after a reboot
pays for a cold start. Measured: **132 ms cold, 16 ms warm.**

Both of the things this design was unsure about have now been **confirmed against a
running Claude Code 2.1.222**:

- **The hook wins the race.** The daemon bound at `07.641`, `engram start`'s health check
  returned at `07.682`, and the client's `initialize` arrived at `08.396` — roughly 715 ms
  of margin. Observed with room rather than proven ordered, and only the first session
  after a reboot depends on it.
- **No `Origin` header is sent.** `initialize`, `notifications/initialized`, the SSE
  `GET`, and `tools/list` all succeeded; the rebinding guard never fired.

To try the plugin for one session without registering anything:

```
claude --plugin-dir /Users/jimcline/git/repos/engram/plugin
```

### Where memory lives

Engram's data — the SQLite store, telemetry, everything under the Engram home — lives
at `~/.engram`, resolved by the same `--home` / `ENGRAM_HOME` / `~/.engram` precedence
described below. It is deliberately **not** inside the plugin's own data directory
(`${CLAUDE_PLUGIN_DATA}`), because `claude plugin uninstall` deletes
`${CLAUDE_PLUGIN_DATA}` by default. Uninstalling the plugin removes the hooks and the
MCP registration; it does not touch `~/.engram` or the installed binary, and reinstalling
later picks the same memory back up.

Nothing in the hooks does anything at all until `engram init` has run — every hook exits
0 silently when the home has no `config.toml`. That is deliberate, and it is why a
plugin loaded without an initialised home produces no telemetry and no primer.

## Testing against an isolated instance

Engram derives every path from one resolver: `--home <path>`, then `ENGRAM_HOME`, then
`~/.engram`. Nothing else in the codebase may compute a path from your home directory,
and a lint test fails the build if anything tries.

So a throwaway instance that cannot touch your real memory is one variable:

```
export ENGRAM_HOME=$(mktemp -d)
./out/engram init
./out/engram start --port 7434
./out/engram status
./out/engram stop
```

Pick a port other than the default when a real instance is already running, since the
pid file lives in the home and the two instances would otherwise fight over one port.

`engram start` refuses to touch a port held by anything it cannot prove is itself, and
proves identity by executable path *and* recorded start time before it signals a
process — so a recycled pid is forgotten, never killed.

