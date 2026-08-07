# Engram

A single-binary, fully local temporal memory and code knowledge graph for LLM coding
agents. Unifies three kinds of memory — code structure, project knowledge, and user
preferences — into one append-only store where facts are never mutated or deleted, only
superseded, with the reason for every revision recorded.

The success metric is **tokens the host LLM did not have to load**. Memory is a
substitute for context, not a supplement to it.

- C# / .NET 10, published as a Native AOT binary plus the SQLite library it loads at
  runtime — the installer places both, and refuses to finish if the installed copy
  cannot open a database
- SQLite only (WAL), one database at `~/.engram/engram.db`
- CLI for humans, MCP over local HTTP for agents — `engram start` / `stop` / `status`
- No containers, no runtime install, and nothing that leaves the machine: the server
  binds `127.0.0.1` only and rejects any request carrying an `Origin` header

**Status:** M0 complete and M1 in progress. The CLI, the supervised HTTP daemon, the hooks,
the installer and the Claude Code plugin all exist and are verified against a live Claude
Code. M0 deliberately shipped with no database — it was an adoption probe measuring whether
the agent calls the memory tools at all, before anything was built on the assumption that
it would. The evidence that this was the right order is in D12: a sibling tool on the same
machine holds 67,936 unstructured notes and zero structured facts, because the structured
half asks more of the model than the model volunteers. The store now exists, and every
memory tool writes to it — `engram_digest` was the last that did not.

## Documents

| | |
|---|---|
| [`docs/engram-spec.md`](docs/engram-spec.md) | Design specification, Rev D |
| [`docs/engram-implementation-plan.md`](docs/engram-implementation-plan.md) | Locked decisions, spec errata, milestones, testing strategy |
| [`docs/engram-schema.sql`](docs/engram-schema.sql) | Canonical M1 database schema |
| [`docs/engram-path-grammar.md`](docs/engram-path-grammar.md) | How code entities are addressed — versioned, v1 |
| [`docs/engram-design.html`](docs/engram-design.html) | Visual design sheet |

Start with the implementation plan — it records twenty-eight decisions (D1–D28) resolving
questions the spec left open, the places where the spec contradicts itself, and for each
one the argument or measurement that settled it.

## Building

Engram is built from this repository on the machine that runs it (D28). There are no
release binaries, and none of what follows assumes one.

Requires the .NET 10 SDK. Native AOT also needs a platform toolchain: Xcode command line
tools on macOS, `clang` and `zlib1g-dev` on Linux, the MSVC C++ build tools on Windows.

```
dotnet build -c Release
dotnet test  -c Release
dotnet publish src/Engram.Cli -c Release -r osx-arm64 -o out   # or linux-x64, win-x64
```

### Build risks worth knowing about

Because the binary is produced on your machine, a few properties of *your* machine end up
baked into it. None of these are checked at build time on purpose — a build that fails on a
machine with nothing to lose is worse than the thing it is guarding against — so `engram
doctor` reports them at runtime instead, where the hardware is known.

- **Apple Silicon, embedding speed.** llama.cpp compiles its Metal shaders at runtime, and
  the shader language version follows the SDK recorded in the executable, not the OS running
  it. Build with an old Xcode command line tools and the M5 tensor path stays off — embedding
  runs at roughly half speed with no error anywhere. Keep the command line tools current. If
  `doctor` reports the tensor path off on hardware that supports it, update them and rebuild.
- **CUDA on Linux and Windows.** GPU acceleration needs the driver present at build and run
  time. Without it Engram falls back to CPU, which is correct but much slower, and again
  silent — `doctor` names the backend actually in use.
- **Cross-building.** Publishing for a RID other than your own machine's is untested and
  unsupported here. If you do it, the two points above are yours to manage.

If you would rather distribute a built binary — signed, notarised, or otherwise — that
works, but it is outside what this repository tests or reasons about.

## Installing

`scripts/install.sh` builds the binary, installs it, puts it on your `PATH`, and
initialises the Engram home. It **prints what it would do and changes nothing** unless
you pass `--apply`:

```
scripts/install.sh                # dry run — read this first
scripts/install.sh --apply        # everything: binary, plugin, grammars, vector search
```

Every optional component installs by default: the Claude Code plugin, the tree-sitter
grammars (TypeScript/JavaScript indexing), the sqlite-vec vector-search extension, and
the Claude Code tool permissions. An interactive `--apply` asks one question up front —
press Enter to take all of that, or answer `a` to be asked a `[Y/n]` at each step. A
piped run takes the defaults without asking, except the permission grant, which edits
Claude Code's settings file and is never granted by a run nobody is watching.

Embeddings are the one step that stays interactive in both modes, because provider and
model are real tradeoffs — disk, download size, memory, languages — and the picker
explains them (with arrow-key menus at a real terminal). Pin the answers with the
`--embedding-*` flags for unattended runs, or `--no-embeddings` to skip the step; with
neither flags nor a terminal it defers and the summary says how to finish
(`engram init --with-embeddings`).

You do not need the .NET SDK: when no SDK 10 is found, the installer downloads one
privately into `<repo>/.dotnet` (a few hundred MB, no `PATH` changes, nothing outside
that directory) and builds with it. What you do need is a C compiler — the Xcode
command line tools on macOS, `clang` and the zlib headers on Linux — and the installer
checks for that first and prints the one command that fixes it.

On Windows, `scripts/install.ps1` and `scripts/uninstall.ps1` are the same installers
with the same defaults (dry run first, `-Apply` to act); building there needs the
"Desktop development with C++" workload of Visual Studio Build Tools. Under WSL, skip
the PowerShell pair entirely and use `scripts/install.sh` — WSL is the Linux path.

| | |
|---|---|
| `--apply` | Actually do it. Without this it is a dry run. |
| `--prefix DIR` | Install directory. Default `$HOME/.local/bin`. |
| `--binary PATH` | Install an already-built binary instead of building one. |
| `--sdk-dir DIR` | Where a bootstrapped SDK lives. Default `<repo>/.dotnet`. |
| `--dotnet-install PATH` | Use a local copy of Microsoft's `dotnet-install.sh` instead of downloading it. |
| `--no-path` | Do not put the binary on `PATH` by any means. |
| `--no-plugin` | Skip the two `claude plugin` commands below. When the step does run and `claude` is missing or fails, the install still finishes and prints them for you to run. |
| `--no-tree-sitter` | Skip compiling the tree-sitter grammars into `~/.engram/lib`. |
| `--no-sqlite-vec` | Skip fetching the sqlite-vec vector-search extension. |
| `--no-embeddings` | Skip the embedding step entirely; recall stays lexical. |
| `--embedding-provider P` | Answer the provider question without a terminal: `none`, `local`, `ollama`, `openai-compat`, `openai`. |
| `--embedding-model M` | Which local model (see `engram model list`), or what an endpoint calls its model. |
| `--embedding-endpoint URL` | Base URL of a server answering `POST /v1/embeddings`. |
| `--embedding-dim N` | Vector width, when the endpoint cannot be asked. |
| `--embedding-api-key-env VAR` | Environment variable holding the endpoint's API key. |
| `--grant-permissions` | Allow the memory tools without prompting, instead of asking. |
| `--no-grant-permissions` | Never grant them, and do not ask. |

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

The uninstaller also takes back the MCP tool permissions the installer granted — and only
those, which is why it runs before the binary and the home are removed: the record of what
was ours lives in the home, and reading it is the only thing separating an entry Engram added
from one you wrote.

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

### Letting the agent reach memory without a prompt

By default Claude Code asks before every MCP tool call. For a memory system that is not just
friction — it is a measurement problem. M0 exists to find out whether the model reaches for
memory on its own, and a dialog in front of each `engram_recall` turns that number into a
measurement of the dialog: the model calls it less, and the user approves on reflex.

So the installer offers to add six entries to `permissions.allow` in Claude Code's user
settings, and `engram permissions` does the same job on its own:

```
engram permissions                      # dry run — prints exactly what it would add
engram permissions --apply
engram permissions --remove --apply     # take back only what Engram added
```

| Granted | Withheld |
| --- | --- |
| `engram_recall`, `engram_remember`, `engram_digest`, `engram_status`, `engram_browse`, `engram_expand` | `engram_forget`, `engram_revise`, `engram_start`, `engram_stop` |

`engram_browse` lists what memory holds under a path, and `engram_expand` shows the story
behind one fact — its supersession history, its evidence, where it was learned. Both are
reads, so they join the grant. `engram_revise` replaces a belief with a corrected one and
records why; it is withheld for the same reason `engram_forget` is.

A server-wide wildcard would be one line instead of six and is supported, but it would pull
the withheld entries back in the moment any of them ships. `engram_forget` and
`engram_revise` close a fact and there is no un-retract; `start` and `stop` move the daemon
out from under the session talking to it. Those are worth an interruption.

The grant is opt-in, and it asks only a terminal — a piped or CI run declines, because silence
is not consent to edit somebody's settings file. Before writing it backs the file up, merges
into whatever is already there rather than replacing it, and **refuses outright to rewrite a
settings file it cannot parse strictly**, since comments and trailing commas would parse under
relaxed options and then disappear on the way back out.

Removal is exact rather than symmetric-by-name: the grant records which entries it added, under
the Engram home, and the uninstaller takes back only those. An entry you wrote yourself survives
an uninstall, and `engram permissions --remove` says so when it leaves one alone.

### Remembering what the user says

Every other path into memory waits for the model to volunteer a tool call, and the M0
telemetry says it does not: `remember` fired 0 times in 1 session, `digest` 0 times. A
fact the user states in passing is lost unless something that is not the model writes it
down. `UserPromptSubmit` is the only place every message passes through.

The catchable shape is grammatical, not lexical. *"I went to see a Spiderman movie last
Saturday"* contains no memory keyword at all — matching on "remember" or "always" would
never see it. What marks it is that it is a **first-person declarative**, which is also
what separates it from the two things a prompt is otherwise made of, a question and an
instruction. `UserStatementClassifier` keeps first-person statements and standing
instructions and drops the rest.

Classification runs **per sentence**, which is a privacy property as much as a precision
one: in *"I moved to Seattle in March. Now fix the failing test."* only the first clause
is stored, and the rest of the message never reaches disk.

Two things then happen, and the redundancy is deliberate:

- The raw sentence is written to the store immediately, as an ordinary fact. This does not
  depend on the model doing anything.
- The model is asked to restate anything that would not stand alone later — a relative
  date, an unresolved "it" — via `engram_remember` with `supersedes` set, which **closes**
  the raw capture rather than duplicating it.

Captured facts rank in recall's long-term tier, not beside session notes: what someone
says about themselves outlives the conversation it was said in. Saying the same thing
twice captures it once — the statement is its own address, so a repeat is recognised
rather than stored again.

**It can be undone.** `engram_forget` / `/engram:forget` retract by id. The fact is closed,
not erased, because belief content here is append-only: it stops being recalled, and the
row survives to record that something was retracted. Nothing puts it back — not a corpus
revision, not a reinstall. Capture without a delete key was not something worth shipping.

The classifier biases toward silence. A missed fact costs one repetition; a wrongly
captured one is a sentence the user did not choose to have written down.

### What the agent notes during a session

`engram_remember` writes working memory — a decision, a constraint, a partial result, a
dead end already ruled out — the state the model would otherwise carry in context and
lose to compaction or to a subagent's incomplete report. Recall ranks the current
session's notes above everything else, and later sessions still see them, annotated with
which sitting they came from.

These are ordinary facts in the same store as everything else, which is what makes them
retractable: `engram_forget` takes the id of a session note exactly as it takes the id of
a fact Engram shipped with. A subagent that passes its own name in `agent` has its notes
attributed to it, so "which worker learned this" survives the worker.

Recording the same statement twice in one session records it once — a note is addressed by
its own text — while two different sessions reaching the same conclusion keep both, because
they are two observations rather than one repeated.

### Remembering the shape of the code

`engram index [path]` turns a git checkout into code facts: a one-line impression of what
each file is for, each top-level declaration, each file's imports, and a section entry per
markdown heading. Entities are addressed by a versioned path grammar
([`docs/engram-path-grammar.md`](docs/engram-path-grammar.md)) —
`/projects/<project>/code/<repo>/<path>#<fragment>` — so a recall about "the retry logic"
can land on a symbol, not just a filename. Like everything that changes the store, it is
dry-run by default; `--apply` writes.

Indexing is incremental, and change detection never consults mtime: clean tracked files
compare by git's own blob sha, dirty and untracked files by content hash — the second half
matters because `git ls-files -s` reports the *staged* blob, and an unstaged edit is the
state a file is in when a hook has just seen it change. Renames keep their entity ids and
leave an alias behind, so a fact attached to a file survives the file moving. The indexer
writes only what it can regenerate: a fact an agent recorded about the code outranks
anything tier-0 analysis produces, and is never superseded by it.

The `file-touched` hook queues each edit; `engram index --drain` consumes the queue —
only the entries this repo can act on, after the re-read has committed, so another repo's
edits stay queued and a crash loses nothing. Session start runs
`index --drain --apply --auto` in the same detached maintenance child as backups and queue
compaction. `--auto` declines silently unless `[indexing] auto_index_on_session_start =
true` is set, a store already exists, and the directory actually is a git checkout — a
shell that happens to start in `$HOME` must not index `$HOME`.

The default analyzers are tier 0 of D24: managed code in-core, regex-shaped, no external
dependencies. C# has tier 2: `engram-roslyn`, a separate Roslyn process (D1 keeps Roslyn
out of the core binary) that the indexer batch-feeds over stdin/stdout. `install.sh`
builds and installs it automatically into `roslyn/` under the install prefix (with
`--binary`, pass `--roslyn-dir` to ship a prebuilt one); the indexer finds it there,
beside the executable, or wherever `ENGRAM_ROSLYN_SIDECAR` points, and `engram doctor`'s
`code analysis` row says which tier this machine actually has. It
replaces symbol and import facts with syntax-tree-accurate ones — nested types stop
masquerading as top-level — while keeping tier 0's file impression, and it formats the
imports fact byte-identically to tier 0, so swapping tiers rewrites nothing that did not
change. Anything that stops the sidecar (absent, no runtime, hung) silently leaves that
run at tier 0. Tree-sitter grammars (tier 1) for the other languages remain future work,
slotting into the same registry, because the grammar version and analyzer version are
recorded in the store and a bump forces re-analysis. `engram doctor` reports the index
per registered repo: how many files, into which project path, how stale.

### Slash commands

`plugin/commands/` adds eight, all namespaced `/engram:`:

| Command | What it does |
| --- | --- |
| `/engram:recall <query>` | Query memory directly and show what it holds, verbatim, fact handles included. |
| `/engram:remember <fact>` | Store a fact in the user's own words. |
| `/engram:digest` | Flush the session's durable learnings before compaction or exit. |
| `/engram:status` | Server pid, port, version, uptime, and whether the home is initialised. |
| `/engram:start` | Start the server. |
| `/engram:stop` | Stop it. |
| `/engram:forget <id>` | Retract anything memory holds — a captured fact, a session note, a shipped one. |
| `/engram:doctor` | Read-only diagnosis: resolved binary, port holder, home contents, telemetry, log tail. |

The split in how they reach engram is deliberate. `recall`, `remember`, and `digest` go
through the MCP tools, because that is where the ranking, the token budget, and the
session identity live. The four lifecycle and diagnostic commands shell out to the binary
instead — when the server is down its MCP tools disappear along with it, so an
MCP-backed `status` could only ever answer "running", and an MCP-backed `start` could
never cold-start the thing it exists to start.

They shell out through `scripts/engram-cli.sh` rather than `hooks/engram-exec.sh`. The
two differ in exactly one way, and it is the reason both exist: a hook that fails is
worse than a hook that does nothing, so `engram-exec.sh` swallows a missing binary in
silence, while somebody who typed `/engram:status` is waiting for an answer and has to
be told. Resolution itself is not duplicated — both go through `resolve-engram.sh`.

Because commands are prompts, nothing compiles them and nothing validates them at load
time; a command naming a moved script or a renamed MCP tool would fail for the first
user who typed it. `PluginCommandTests` closes that gap in CI: every
`${CLAUDE_PLUGIN_ROOT}` path must exist, every shell script must be executable, and
every MCP tool a command names is checked against `tools/list` on a real running server.

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

