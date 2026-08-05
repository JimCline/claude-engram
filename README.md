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

**Status:** M0 in progress. The CLI, the home resolver, and the Claude Code plugin
exist; there is no database yet by design — M0 is an adoption probe that measures
whether the agent calls the memory tools at all before anything is built on the
assumption that it will.

## Documents

| | |
|---|---|
| [`docs/engram-spec.md`](docs/engram-spec.md) | Design specification, Rev D |
| [`docs/engram-implementation-plan.md`](docs/engram-implementation-plan.md) | Locked decisions, spec errata, milestones, testing strategy |
| [`docs/engram-schema.sql`](docs/engram-schema.sql) | Canonical M1 database schema |
| [`docs/engram-design.html`](docs/engram-design.html) | Visual design sheet |

Start with the implementation plan — it records nine decisions resolving questions the
spec left open, and the places where the spec contradicts itself.

## Building

Requires the .NET 10 SDK. On macOS, Native AOT also needs Xcode command line tools.

```
dotnet build -c Release
dotnet test  -c Release
dotnet publish src/Engram.Cli -c Release -r osx-arm64 -o out
```

## Installing as a Claude Code plugin

Engram ships as a plugin. The repository itself is the marketplace; the binary is a
build artifact, not something committed to source control, so it has to be built once
before the plugin has anything to run:

```
scripts/build-plugin.sh
claude plugin marketplace add /Users/jimcline/git/repos/engram
claude plugin install engram@engram
```

Then, inside a running Claude Code session, `/reload-plugins` picks up the hooks and
the MCP server registration — a full restart is not required.

`plugin/.claude-plugin/plugin.json` carries a `version`. Claude Code's plugin cache is
version-pinned at `~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/` and is
wholesale-replaced on a version bump, so after rebuilding the binary with
`scripts/build-plugin.sh`, bump that `version` (and the matching entry in
`.claude-plugin/marketplace.json`) or Claude Code keeps serving the previously cached
copy.

### How the plugin reaches the server

`plugin/.mcp.json` is an `http` entry pointing at `http://127.0.0.1:7433/`. Nothing in
that file can start a server, so the `SessionStart` hook runs `hooks/ensure-server.sh`
first, which calls `engram start` — idempotent, silent, and always exit 0. The daemon
outlives the session that started it, so only the first session after a reboot pays for
a cold start.

**Two things here are built from the documentation and not yet confirmed against a
running Claude Code**, and both fail closed rather than dangerously:

- Whether Claude Code connects to the MCP entry *before* `SessionStart` hooks finish. If
  it does, the very first session after a reboot may find no server and lose its memory
  tools for that session; every later session finds the daemon already up.
- Whether Claude Code's MCP client sends an `Origin` header. It should not — `Origin` is
  a browser concept and Node's `fetch` does not add one — but the server rejects any
  request that carries one, so if it does, every call returns 403.

Verify both in a single session without installing anything permanently:

```
claude --plugin-dir /Users/jimcline/git/repos/engram/plugin
```

### Where memory lives

Engram's data — the SQLite store, telemetry, everything under the Engram home — lives
at `~/.engram`, resolved by the same `--home` / `ENGRAM_HOME` / `~/.engram` precedence
described below. It is deliberately **not** inside the plugin's own data directory
(`${CLAUDE_PLUGIN_DATA}`), because `claude plugin uninstall` deletes
`${CLAUDE_PLUGIN_DATA}` by default. Uninstalling the plugin removes the hooks, the MCP
server registration, and the binary; it does not touch `~/.engram`, and reinstalling
the plugin later picks the same memory back up.

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

