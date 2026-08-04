# Engram

A single-binary, fully local temporal memory and code knowledge graph for LLM coding
agents. Unifies three kinds of memory — code structure, project knowledge, and user
preferences — into one append-only store where facts are never mutated or deleted, only
superseded, with the reason for every revision recorded.

The success metric is **tokens the host LLM did not have to load**. Memory is a
substitute for context, not a supplement to it.

- C# / .NET 10, published as a Native AOT single-file binary
- SQLite only (WAL), one database at `~/.engram/engram.db`
- CLI for humans, MCP over stdio for agents
- No services, no containers, no runtime install

**Status:** M0 in progress. The CLI, the home resolver, and the Claude Code installer
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

## Testing against an isolated instance

Engram derives every path from one resolver: `--home <path>`, then `ENGRAM_HOME`, then
`~/.engram`. Nothing else in the codebase may compute a path from your home directory,
and a lint test fails the build if anything tries.

So a throwaway instance that cannot touch your real memory is one variable:

```
export ENGRAM_HOME=$(mktemp -d)
./out/engram init
./out/engram install claude-code --settings-path /tmp/settings.json --mcp-config /tmp/mcp.json
```

The install verbs take explicit targets for the same reason — a sandbox install must
never be able to write to your real `~/.claude`. Add `--dry-run` to print the resulting
JSON without writing anything.

