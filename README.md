# Engram

> [!WARNING]
> **Experimental**: Subject to rapid changes and evolution, use at your own discretion

A single-binary, fully local temporal memory and code knowledge graph for LLM coding
agents. Unifies three kinds of memory — code structure, project knowledge, and user
preferences — into one append-only store where facts are never mutated or deleted, only
superseded, with the reason for every revision recorded.

The success metric is **tokens the host LLM did not have to load**. Memory is a
substitute for context, not a supplement to it.

- C# / .NET 10, published as a Native AOT binary plus the native libraries it loads at
  runtime — SQLite, llama.cpp for local embeddings, and optionally sqlite-vec and the
  tree-sitter grammars. The installer places them, and refuses to finish if the installed
  copy cannot open a database
- SQLite only (WAL), one database at `~/.engram/engram.db`
- CLI for humans, MCP over local HTTP for agents — `engram start` / `stop` / `status`
- No containers and no runtime install. The server binds `127.0.0.1` only and rejects any
  request carrying an `Origin` header, and with the default local embedding model nothing
  leaves the machine at all — the one way to change that is choosing a remote embedding
  endpoint, which sends it the text being embedded

**Status:** everything described below is built and runs against a live Claude Code — the
store, the CLI, the supervised HTTP daemon, the hooks, the installer, the plugin, the code
index through all three analyzer tiers, and the embedding and vector-search lane. In the
implementation plan's milestone terms: M0 and M1 are done; M2's integration landed as the
installer plus the plugin rather than the `engram install claude-code` verb the plan
sketched, and its `share`/`join` and HTML report were never built; M3's code graph ships.
M4's machinery exists too, and its retrieval-quality gate (D18) now passes on the evidence
recorded so far — see below.

**What is not settled** is the question the whole design is bet on: whether the agent
reaches for memory on its own. M0 deliberately shipped with no database because that was
the thing to measure first, before anything was built on the assumption. The evidence that
this was the right order is D12 — a sibling tool on the same machine holds 67,936
unstructured notes and zero structured facts, because the structured half asks more of the
model than the model volunteers. Engram's own telemetry now shows 28 writes against 7
reads, which is spec §1.2's stated cause of death appearing in its own numbers. It is not
yet a verdict: the session primer delivers memory without any tool call, so recall
undercounts delivery by construction, and D46 is what makes the difference countable going
forward. D18's retrieval-quality bar — no recorded query missing a fact that existed when asked —
now passes on direct measurement (D63): five paraphrase probes against the live store, none
reusing the target fact's wording, all resolved with zero misses. That closes nothing for good,
since the bar is cumulative and answers retrieval quality only; whether the agent reaches for the
vector lane on its own, rather than being deliberately probed, is still the open bet stated above.

## Documents

| | |
|---|---|
| [`docs/engram-spec.md`](docs/engram-spec.md) | Design specification, Rev D |
| [`docs/engram-implementation-plan.md`](docs/engram-implementation-plan.md) | Locked decisions, spec errata, milestones, testing strategy |
| [`docs/engram-schema.sql`](docs/engram-schema.sql) | Canonical database schema — the authority for database shape, currently v3 |
| [`docs/engram-path-grammar.md`](docs/engram-path-grammar.md) | How code entities are addressed — versioned, v2 |
| [`docs/engram-design.html`](docs/engram-design.html) | Visual design sheet |
| [`docs/engram-progress.md`](docs/engram-progress.md) | What landed recently, what is verified vs. merely argued, and the open work |
| [`docs/memory-expansion-spec.md`](docs/memory-expansion-spec.md) | Overview and index of the memory-expansion feature set below |
| [`docs/memory-expansion/`](docs/memory-expansion/) | Per-feature specs: sync, conflict verdicts, tool profiles, lifecycle, browse/timeline, standing directives |

Licensed under [Apache-2.0](LICENSE).

Start with the implementation plan — it records fifty-eight decisions (D1–D58) resolving
questions the spec left open, the places where the spec contradicts itself, and for each
one the argument or measurement that settled it. `CLAUDE.md` holds the invariants that are
easy to break by accident, most of which were paid for by breaking them.

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

**Supported: macOS, Linux, and Windows via WSL (WSL takes the Linux path below).
Windows outside WSL is unvalidated** — `install.ps1` exists but nobody has run it on a
Windows machine yet to confirm it works end to end; see the Windows note further down and
`docs/team-readiness-checklist.md`.

`scripts/install.sh` builds the binary, installs it, puts it on your `PATH`, initialises
the Engram home, and starts the server. **Running it installs** — no flag required:

```
scripts/install.sh                # everything: binary, plugin, grammars, vector search
scripts/install.sh --dry-run      # print the whole plan and change nothing
```

This is the one place Engram is not dry-run-first. Everything that removes or rewrites
what is already there — `uninstall.sh`, `repair`, `compact`, `forget`, `backup prune` —
still prints its plan and waits for `--apply`. The installer only adds things, and
running an installer is already the request to install, so the brake is opt-in instead:
pass `--dry-run` to read the plan first. (`--apply` is still accepted and ignored, so
the invocation in your shell history keeps working.)

Every optional component installs by default: the Claude Code plugin, the tree-sitter
grammars (TypeScript/JavaScript indexing), the sqlite-vec vector-search extension, the
Claude Code tool permissions, and starting the server at the end. An interactive run
asks one question up front — press Enter to take all of that, or answer `a` to be asked
a `[Y/n]` at each step. A piped run takes the defaults without asking, except the
permission grant, which edits Claude Code's settings file and is never granted by a run
nobody is watching.

The last step starts the server and then confirms it, by asking `engram status` from a
separate process rather than trusting the one that just launched it — so the summary
line says `Server: running` only when something independent agrees. `--no-start` leaves
it stopped. This matters most on a *re*install: replacing the binary requires stopping
the daemon serving the old one, so before this step existed an upgrade ended with the
server down and the summary still reporting success.

An agent often arrives already carrying another memory system, described somewhere Engram
cannot see — Claude Code's own file-based memory is the usual one, and its instructions fire
on the literal words *"remember this."* Engram used to make no competing claim at all, so it
lost that race, correctly. It now says which store wins, through one line in the session
primer: `engram-first` (the default — Engram is primary for reads and writes, the other store
still exists), `engram-only`, or `off` to say nothing. The default arrives with the shipped
config, so a piped install gets it without answering anything; `--memory-precedence` pins it,
and asking per step offers the three choices. Change it later with
`engram init --memory-precedence <value>`, and `engram doctor` reports which one is in force.

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

On Windows, `scripts/install.ps1` and `scripts/uninstall.ps1` are the PowerShell
counterparts; building there needs the "Desktop development with C++" workload of
Visual Studio Build Tools. Both still take `-Apply` and still dry-run without it — the
default above has *not* been inverted there, because that inversion makes a script act
by default and no one has yet run these on a Windows machine to check. They are behind
the POSIX installer in other ways too; `memory/install-ps1-parity-debt.md` lists what is
owed. Under WSL, skip the PowerShell pair entirely and use `scripts/install.sh` — WSL is
the Linux path.

| | |
|---|---|
| `--dry-run` | Print the whole plan and change nothing. |
| `--apply` | Accepted and ignored; installing is the default. |
| `--prefix DIR` | Install directory. Default `$HOME/.local/bin`. |
| `--binary PATH` | Install an already-built binary instead of building one. |
| `--sdk-dir DIR` | Where a bootstrapped SDK lives. Default `<repo>/.dotnet`. |
| `--dotnet-install PATH` | Use a local copy of Microsoft's `dotnet-install.sh` instead of downloading it. |
| `--no-path` | Do not put the binary on `PATH` by any means. |
| `--no-plugin` | Skip the two `claude plugin` commands below. When the step does run and `claude` is missing or fails, the install still finishes and prints them for you to run. |
| `--no-tree-sitter` | Skip compiling the tree-sitter grammars into `~/.engram/lib`. |
| `--no-sqlite-vec` | Skip fetching the sqlite-vec vector-search extension. |
| `--no-start` | Leave the server stopped at the end. Start it later with `engram start`. |
| `--memory-precedence P` | What the primer says about preferring Engram over another memory system: `engram-first` (default), `engram-only`, `off`. |
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

Uninstalling removes things, so unlike the installer it *is* a dry run by default:

```
scripts/uninstall.sh              # dry run — shows what is installed and what would go
scripts/uninstall.sh --apply
scripts/uninstall.sh --apply --purge   # ALSO deletes ~/.engram — your memory store
```

The uninstaller first looks at what is actually installed, and an interactive `--apply`
confirms each item before removing anything — binary, plugin, permissions, PATH entry,
and (with its own question, defaulting to no unless `--purge`) the home. Backups survive
a purge by default: `~/.engram/backups` holds the plain-text journal that can restore
the memory into a fresh install (`engram backup replay`), so deleting it requires either
answering yes at the prompt or passing `--remove-backups`. A piped run takes the
defaults without asking.

**`--purge` is the only thing that will ever touch `~/.engram`.** Without it the memory
store is left completely alone, and the summary says so explicitly.

The uninstaller also takes back the MCP tool permissions the installer granted — and only
those, which is why it runs before the binary and the home are removed: the record of what
was ours lives in the home, and reading it is the only thing separating an entry Engram added
from one you wrote.

### The Claude Code plugin

The repository itself is the marketplace:

```
claude plugin marketplace add /path/to/claude-engram   # this repository's root
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
claude --plugin-dir /path/to/claude-engram/plugin
```

### Letting the agent reach memory without a prompt

By default Claude Code asks before every MCP tool call. For a memory system that is not just
friction — it is a measurement problem. The open question above is whether the model reaches
for memory on its own, and a dialog in front of each `engram_recall` turns that number into a
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

Every other path into memory waits for the model to volunteer a tool call, and the
telemetry says that is not something to rely on — `engram_recall` has fired 7 times across
the life of the instance that has been running this repo, every one of them opt-in. A fact
the user states in passing is lost unless something that is not the model writes it down.
`UserPromptSubmit` is the only place every message passes through.

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

Analysis is tiered (D24), and all three tiers now exist. **Tier 0** is the default and the
floor: managed code in-core, regex-shaped, no external dependencies, every language. **Tier
1** is tree-sitter, which the installer compiles into `~/.engram/lib` from pinned
digest-checked sources (D47); it covers TypeScript and JavaScript today, and adding a
language is a registry row rather than a code change. **Tier 2** is `engram-roslyn`, a
separate Roslyn process for C# (D1 keeps Roslyn out of the core binary) that the indexer
batch-feeds over stdin/stdout. `install.sh` builds and installs it automatically into
`roslyn/` under the install prefix (with `--binary`, pass `--roslyn-dir` to ship a prebuilt
one); the indexer finds it there, beside the executable, or wherever
`ENGRAM_ROSLYN_SIDECAR` points.

The deep tiers replace symbol and import facts with syntax-tree-accurate ones — nested
types stop masquerading as top-level — while keeping tier 0's file impression, and they
format the imports fact byte-identically to tier 0, so swapping tiers rewrites nothing that
did not change. Anything that stops a deep tier (absent, no runtime, hung) silently leaves
that run at tier 0, which is why `engram doctor`'s `code analysis` row reports the tier
this machine actually has rather than the one it could have. Both the grammar version and
the analyzer version are recorded per fact, so a bump forces re-analysis instead of leaving
a store half-addressed: grammar v2 (D48) is what lets a fragment name a nested type or one
overload of a method (`FactStore.cs#FactStore/Remember`) rather than only a top-level
declaration. `engram doctor` also reports the index per registered repo: how many files,
into which project path, how stale.

### Finding it again: lexical, semantic, or both

Recall's default lane is lexical (SQLite FTS5), which is exact and needs nothing installed.
The second lane is semantic: facts are embedded into vectors and searched by meaning, so
*"how do we handle retries"* can reach a fact that says nothing about retries. The two are
fused rather than chosen between, and either can be absent — **recall can never fail
because the vector lane failed.** Every way that lane can stop returns a reason and an empty
ranking, so a dead endpoint or a missing extension costs vector hits and nothing else.

Embeddings come from one of four providers, picked during install or with `engram init
--with-embeddings`: `none`, `local` (a GGUF model loaded in-process through llama.cpp —
fully offline, and the default), `ollama`, or `openai-compat` (which also answers to
`openai`, and covers anything serving `POST /v1/embeddings`). `engram model
list` names the local models with their size and tradeoffs. Two settings fail *silently*
when wrong and so are handled rather than typed: vector width is asked of the endpoint
(`engram embed --probe`) instead of guessed from the model name, because a mismatched width
stores vectors that rank like noise and error nowhere; and changing model or provider needs
`engram embed --rebuild`, which refuses to run while the server is up rather than racing the
embedder that server loaded at *its* startup.

Embedding happens in the background, inside the server, so `engram embed --status` says how
far it has got and whether it is still moving — and `--watch` redraws it live, with what is
being embedded right now. The counts come from the store, so they are right whether or not a
server is running; the rate, the eta and the in-flight list come from the server, because
nothing else can know them. If there is no loop, it says why: a model that was never
downloaded is reported there rather than only in the log.

```
engram embed --status                  # how far, how fast, and what is being embedded
engram embed --status --watch          # the same, redrawn until it finishes
```

`engram explain <query>` shows why recall ranks what it ranks — every lane's contribution,
what fused, and what got left outside the token budget. It runs the same `VectorLane` recall
runs rather than a copy of it, so what it describes is the ranker that actually executed.

Recall also reports `coverage`, which counts **lane agreement, not rows**: a fact found by
several lanes is corroborated, and three or more corroborated facts is `high`. Counting
candidates instead was a real bug — a weekend-plans query returned seven hits, six of them
engineering notes reached through a shared word stem, and reported `high`, which is the
value that tells the model memory has the question covered.

### Reacting to what Engram does

Engram records what it does — every recall, every capture, every session start — as one JSON object
per line in `~/.engram/telemetry.jsonl`. Point `[webhook] url` at something and the server POSTs each
record as it lands, so a script can react in real time:

```toml
[webhook]
url = "http://127.0.0.1:8787/engram"
kinds = ["remember", "recall", "session-start"]   # or ["*"] for everything
timeout_ms = 2000
```

The request body is that log line **verbatim**, one event per request, plus `X-Engram-Event` and
`X-Engram-Version` headers so a shell script can route on the kind without parsing JSON. A live feed
and a read of the file are therefore the same data in the same shape.

Two properties worth knowing before you build on it:

- **It starts at the end of the log and never resumes.** You get what happens while the server is
  running, and nothing else — otherwise a restart after a day of downtime would replay thousands of
  `file-touched` events at whatever is listening. For history, read `telemetry.jsonl` directly; it is
  durable, plain text and timestamped.
- **A failed delivery is dropped, not queued.** Delivery is never allowed to stall memory, and a
  subscriber that is down is muted with a doubling backoff rather than retried per event. This is
  safe precisely because the file still has everything.

Work that takes time reports both ends, so a subscriber never has to guess how long to keep saying
it is happening: `index` and `embedding` each carry a `phase` of `started`, `finished` or `failed`.
Neither carries counts — the index report says what was written, and how far along a backfill is
lives in `engram embed --status`, which is right whether or not anything is subscribed.

The everyday activity a status line actually wants is there too:

- **`file-touched`** — one per edit, carrying the `path` that changed. This is the highest-frequency
  event by far, and the one place Engram accepts loss: the hook that writes it holds a 10 ms budget
  and refuses to wait for the log, so under a heavy burst a small fraction of these are dropped
  rather than delaying an edit. The queue the indexer actually reads never loses anything.
- **`user-prompt`** — Engram captured something you said in passing. It fires only when a fact was
  written, so it is not a proxy for "the user typed", and it is deliberately *not* filed under
  `remember`: that kind means the model chose to save something, and the two must stay countable
  apart.
- **`server-start`** and **`server-stop`** — the server came up, or went down cleanly. Lifecycle
  only. `server-stop` is best effort, so treat its absence as no information: a killed process never
  sends one. Ask `engram status` whether the server is up.

`engram doctor` reports the configuration — a URL that will not parse is an error, and a `kinds`
entry Engram never emits is a warning that narrows delivery rather than switching it off.

### Keeping it healthy

`engram doctor` checks the whole instance and says what to type about anything wrong —
resolved binary, server, home contents, schema version, embedding provider, analyzer tier,
index freshness per repo. It is strictly read-only: it will not migrate, repair, or even
open a model file, because the most useful thing it can tell you is *your store is a schema
behind*, and a check that fixed things could not say it. Only genuinely broken things set
exit 1; a feature you turned off is a supported configuration, not a fault.

Backups are automatic and cheap. Session start snapshots the store if the interval has
passed *and* authored truth actually changed, so an idle day costs nothing. Each snapshot is
written with SQLite's `VACUUM INTO` rather than `cp` — a WAL database copied with `cp` was
measured here to produce not a stale file but an unusable one — and alongside it goes
`backups/facts.jsonl`, every fact in plain text. That journal is the durable half: a `.db`
snapshot only restores into the schema version that wrote it, while the journal is addressed
by path and predicate and replays into any later one. `engram backup replay` is additive and
idempotent, and will never rewrite or close a fact the target store already has. Both it and
`restore` are dry-run first, like everything else that rewrites the store.

```
engram doctor                          # what is wrong, and what to type about it
engram backup list                     # what snapshots exist
engram backup restore [name] --apply   # put one back, keeping the current store as a new snapshot
engram backup replay --apply           # read facts.jsonl into the store, adding what it lacks
```

### Slash commands

`plugin/commands/` adds ten, all namespaced `/engram:`:

| Command | What it does |
| --- | --- |
| `/engram:recall <query>` | Query memory directly and show what it holds, verbatim, fact handles included. |
| `/engram:remember <fact>` | Store a fact in the user's own words. |
| `/engram:digest` | Flush the session's durable learnings before compaction or exit. |
| `/engram:status` | Server pid, port, version, uptime, and whether the home is initialised. |
| `/engram:start` | Start the server. |
| `/engram:stop` | Stop it. |
| `/engram:restart` | Stop it, then start a fresh one. |
| `/engram:forget <id>` | Retract anything memory holds — a captured fact, a session note, a shipped one. |
| `/engram:doctor` | Read-only diagnosis: resolved binary, port holder, home contents, telemetry, log tail. |
| `/engram:statusline` | Add live Engram activity to your status line — adds a segment to whatever you already have rather than replacing it. |

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

## Memory expansion

Six features on top of the base store, in this order: cross-machine sync, conflict
verdicts, MCP tool profiles, lifecycle primitives (review-due, session pin), an
interactive browser and timeline, and standing directives. A seventh — an Obsidian
export — was specced and scratched; see `docs/memory-expansion/06-export-spec.md` for
why. None of the six below changes the schema in a way existing facts need to migrate
for; each is additive.

### Syncing across machines

Cross-machine sync replicates authored facts between machines you control, over
whatever transport you already trust — git, iCloud Drive, Google Drive, Dropbox.
Engram writes immutable JSONL chunk files and never shells out to git itself; you move
the chunks however you already move files, and folder-sync services need no code of
their own because `[sync] dir` already covers them. It is opt-in and off by default.

```
engram sync export [--apply] [--scope all|user|repo:<name>]
engram sync import [--apply]
engram sync status
engram sync compact [--apply] [--if-large]
```

```toml
[sync]
enabled = false         # gates only the ambient session-start invocation
dir = "/absolute/path"  # required, no ~
scope = "all"            # all | user | repo:<identity>
stale_after_days = 14
retain_days = 90         # must be >= stale_after_days
```

`enabled = false` only stops sync from running unattended at session start — an
explicit `sync export/import/compact --apply` runs regardless, the same precedent
`index --auto` set. Chunk writes are atomic (write-then-rename), and a reader waits for
a chunk to parse cleanly end-to-end before treating it as final. Retention is
time-windowed rather than acknowledgement-based: a closed fact older than `retain_days`
drops out of future chunks, so a machine that reconnects after that window has lost
that history — `sync status` warns once a peer has gone quiet past `stale_after_days`.
`sync compact` only ever deletes within its own machine's subdirectory, never a peer's,
which matters under folder-sync where a delete propagates. What this does not do: no
cloud tier, no three-way merge, no auto-resolution by timestamp — a conflicting close
record surfaces in `sync status`, it is never silently dropped or picked for you.
`engram_remember` takes an optional `sync: bool` parameter (triggered by "share engram"
plus content) to mark a fact for replication as it is written.

### Flagging conflicts and near-duplicates

With `[remember] candidates` enabled, `engram_remember` checks a new fact against what
is already stored — through the same FTS/token-overlap/vector lanes recall uses, gated
by D44's 2+-lane corroboration bar — and can return up to three related live facts. The
model records how the two relate with the new `engram_judge` tool: `supersedes`,
`conflicts_with`, `scoped` (same topic, different context), or `not_conflict`. A
verdict is an annotation, never a mutation — it closes and edits neither fact — and
shows up under `engram_expand … history` for both.

```toml
[remember]
candidates = false   # off until scan cost is measured at 50k+ facts
```

Candidates only surface on a fresh `engram_remember` call, not when `supersedes` is
already set — a caller that named its target isn't guessing. This is deliberately not
same-slot collision detection: D57's per-statement addressing already makes an exact
restatement dedupe silently, so this catches near-duplicates and disagreements, not
literal repeats. Verdicts are local annotations only; sync above does not replicate
them across machines.

### Choosing which tools the agent sees

`engram profile show` / `engram profile set default|full` picks how many MCP tools a
connection advertises, trading prompt-token schema cost for surface area — it changes
what is offered, not what is permitted. `default` is eight tools (`recall`, `remember`,
`forget`, `revise`, `expand`, `browse`, `judge`, `index_repo`); `full` adds the three
lifecycle tools (`start`, `status`, `stop`). This is orthogonal to the permissions grant
above: excluding a tool from a profile makes its grant moot for that connection, and
including it still respects whatever `engram permissions` decided. The split is a rule,
not a maintained list — `default` is everything except the lifecycle tools — so a new
tool lands in `default` automatically unless it is itself lifecycle. Takes effect on
the next connection; measured saving is roughly 235 tokens per session on `default`.

### Marking a fact for later, or pinning one for now

`review_after` is an optional parameter on `engram_remember` and `engram_revise` — a
relative duration (`3d`, `2w`, `12h`) or an ISO date. It does not change ranking; it
surfaces the fact in `engram review list` and in `doctor` once the date passes.
`engram review clear <id> [--apply]` is dry-run first, like everything that touches the
store.

`engram_pin(fact_id)` / `engram_unpin(fact_id)` is session-scoped: a pinned fact takes
top rank in `engram_recall` whenever the query would already match it — it is never
injected into results it would not otherwise reach. The pin dies with the session, so
an unreleased one costs nothing; there is nothing left to release once the session that
made it ends.

### Browsing memory interactively, and reconstructing a timeline

`engram browse` is now also an interactive terminal navigator over the same entity tree
`engram_browse` returns to the model — live fact counts per node, `Enter` to descend,
`t` to jump to timeline, `h` for a fact's own history, `b`/`q` to back out and quit.
`engram timeline <fact-id> [--before N] [--after N]` (default 5 each way) reads facts
in chronological order around a target fact, across every subject, for reconstructing
what was happening around it — distinct from `engram_expand … history`, which is one
entity's own revision chain. Both are read-only.

### Standing directives

`engram directive add "<text>"` records a standing rule the way `CLAUDE.md` does, but
authored from the CLI mid-session rather than by hand-editing a file, and carried by
sync to other machines. Directives are delivered verbatim and unconditionally in the
primer at every context reset and to every subagent — never retrieval-gated, so one
cannot silently drop out because nothing recalled it.

```
engram directive add "<text>"
engram directive list [--all]
engram directive remove <id> --apply
engram directive revise <id> "<text>" --apply
```

They store as ordinary facts at `/directives` (predicate `directs`) — no schema change
— but are excluded from recall and from the fact/topic counts, and carry a separate,
fixed 250-token budget outside the normal primer allowance so they cannot be crowded
out by anything else competing for primer space. `add` is the only way one gets
created; nothing is promoted into a directive automatically.

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

`engram start` refuses to touch a port held by anything it cannot prove is itself, so a
recycled pid is forgotten rather than killed. Identity is the pid plus the kernel's start
token for that pid, and deliberately **not** the executable path (D42): two engram binaries
legitimately serve one home — an installed one and a freshly built one is what every
session working on this repo looks like — so keying on the path answers a different
question, *was this launched from the same file I am?*, and answering it wrongly once left
a live server running with its pid file deleted. The path is reported when it differs from
the binary you asked, never enforced.

