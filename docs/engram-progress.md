# Engram — progress snapshot

**As of 2026-08-07.** Working tree clean, 50 decisions (D1–D50). M3's tier 0 shipped overnight
on an explicit user override of D6's gate; tier 1 (D47) and grammar v2 (D48) followed.

This is a handoff, not an authority. `CLAUDE.md` holds the invariants,
`docs/engram-implementation-plan.md` holds the decisions and their reasoning, and
`docs/engram-schema.sql` is the authority for database shape. Where this file and those
disagree, they win and this file is stale.

---

## Read these first, in this order

1. `CLAUDE.md` — the invariants that are easy to break by accident. All of them were paid
   for by a real defect.
2. `docs/engram-implementation-plan.md` — D1–D50. Skim the headings; read in full any
   decision you are about to touch.
3. This file — for what is in flight and what the last session learned the hard way.

---

## Build, publish, test

```bash
# build (warnings are errors)
dotnet build Engram.sln -c Release

# publish the AOT binary — tier-3 tests drive this, not the JIT build
dotnet publish src/Engram.Cli/Engram.Cli.csproj -c Release -r osx-arm64 -o ./out

# full suite. ENGRAM_TEST_BINARY is what makes tier 3 run at all.
export ENGRAM_TEST_BINARY=$PWD/out/engram
dotnet test Engram.sln -c Release

# CUDA variant (builds anywhere; only the packaging is verified — see below)
dotnet publish src/Engram.Cli -c Release -r linux-x64 -p:EngramGpu=cuda12
```

**Last measured, green:**

| | Core | Integration | EndToEnd | total |
|---|---|---|---|---|
| no weights (the CI shape) | 344 | 435 (64 skipped) | 105 | **884** |
| with weights | 344 | 441 (58 skipped) | 105 | **890** |

The no-weights row was measured this session. The with-weights row is that row plus the same six
real-weights tests as before — none of the eighteen tests added this session are weights-gated — and
was **not** re-run, so treat it as arithmetic rather than a measurement.

The with-weights row depends on *which* models are installed, not just that some are. A home
holding only MiniLM skips the two-model test as well, which is why that row can show one more
skip than a home holding Nomic too.

`ENGRAM_TEST_MODEL_HOME` un-skips 6 real-weights tests. Point it at an Engram home whose
`models/` holds a GGUF:

```bash
ENGRAM_HOME=/tmp/e-weights ./out/engram init
ENGRAM_HOME=/tmp/e-weights ./out/engram model install all-minilm-l6-v2 --use-it
ENGRAM_TEST_MODEL_HOME=/tmp/e-weights dotnet test Engram.sln -c Release
```

`ENGRAM_TEST_TREE_SITTER_DIR` un-skips the 4 gated tier-1 conformance tests. Point it at a
directory holding the compiled core and grammars — `fetch-tree-sitter.sh` produces exactly
that layout:

```bash
scripts/fetch-tree-sitter.sh --home /tmp/e-ts
ENGRAM_TEST_TREE_SITTER_DIR=/tmp/e-ts/lib dotnet test Engram.sln -c Release
```

The remaining skips are `sqlite-vec` tests, which need the extension side-loaded into the
home under test.

---

## What landed recently

### `1bdde52`…`fdc28bd` — M3 tier 0: the code index, gate to shipped in four commits

The D6 gate was overridden by explicit user instruction, not by telemetry — the gate itself
never fired (see "Open work" below for what that means now). What exists:

- **`docs/engram-path-grammar.md`** is the versioned addressing authority, grammar v1:
  `/projects/<project>/code/<repo>/<rel/path>#<fragment>`. `CodePaths` implements it;
  file paths keep their case and are never slugged, fragments are slugged headings or
  verbatim top-level symbol names. No overload grammar in v1 — that is v2's problem, and
  re-keying is what D2's adopt/merge exists for.
- **`LanguageRegistry`** is one static row per language (D24: AOT means no discovery), each
  row carrying its own conformance fixture. The suite iterates the registry, so adding a
  language is one row and zero harness edits — the same shape as the game-registry rule, and
  a source lint (`CodeAnalyzer_CarriesNoLanguageIdLiterals`) holds `CodeAnalyzer` free of
  per-language `if`s. Rows today: csharp, typescript, javascript, markdown, plus a `Text`
  catch-all. All tier 0 — regex declarations, lead-comment impressions, sorted `imports`.
- **`CodeIndexer`** is the incremental pipeline: `file_state` rows keyed by content identity,
  clean tracked files by git blob sha, dirty/untracked by SHA-256. Renames pair by unique
  sha and move the subtree (`FactStore.MoveSubtree` — substr matching, not LIKE, because
  filenames contain `%`), leaving a path alias so old addresses still resolve. One
  `BEGIN IMMEDIATE` transaction per file. `schema_meta['code_index_version']` records
  grammar×analyzer; a bump forces full re-analysis. Non-regenerable facts on code paths are
  never touched — agent testimony outranks tier 0 (D19).
- **`SpoolQueue`** finally consumes `file-touched`'s queue: peek without delete, consume
  only after the commit that made an entry redundant, other repos' entries stay queued
  (D41). A parsed entry with no path escalates to a full scan.
- **Wiring**: `engram index [path] --apply --drain --full --auto`, dry-run by default.
  `--auto` is what the session-start maintenance child runs, and the whole policy lives in
  the child like `--if-due`: config gate (`[indexing] auto_index_on_session_start`, default
  off), store must exist, target must be a git checkout. Every refusal is silent exit 0.
  `doctor` gets a per-repo row via the indexer's own identity resolution.

**Two bugs only the published binary could show** — 449 integration tests were green while
both lived, which is tier 3's whole argument:

- **Symlinks**: the hook spools the spelling the tool used (`/tmp/…`), git canonicalises the
  root (`/private/tmp/…`), and the drained entry was left behind as "another repo's edit" —
  a permanent queue leak on macOS. .NET has no realpath; `PathCanonicalizer` walks
  components and **recurses on link targets**, because the first version returned the
  target unwalked and its own prefix (`/var`) still contained links — it failed its own
  test before it passed it.
- **Staged blobs**: `git ls-files -s` reports the *staged* blob, so an unstaged edit — the
  state every file is in when `file-touched` has just fired — read as unchanged. `git
  status --porcelain -z` names dirty and untracked files, which fall through to content
  hash. Distilled into `UnstagedEdit_InARealCheckout_IsStillDetected`.

Measured end-to-end on the published binary: edit → `hook file-touched` → `index --drain
--apply` = 1 file considered, 1 analyzed, 1 fact written, queue 1 consumed 0 left; the
rerun writes 0. Falsified before trusting: the language-literal lint went red on a planted
`"csharp"`, the `--auto` config gate went red with its condition deleted.

**The live instance does not auto-index yet.** Session start still runs the previously
installed binary, and the config gate defaults off. Turning it on is a reinstall
(`install.sh` or plugin update) plus `auto_index_on_session_start = true` under
`[indexing]` — deliberately not done unbidden.

### `516e1a5`…`ee2fdc9` — the M1 stragglers closed in one sitting

Three things the plan promised in M1 and never got, built the same night as M3:

- **`engram repair` (D8)** — dry-run by default, snapshots unconditionally before
  `--apply`, rebuilds only what derives: FTS, `fact.path`, orphan salience rows, WAL.
  Detection taught two lessons now recorded in `CLAUDE.md`: external-content FTS answers
  non-MATCH queries from the content table (the obvious drift detector compares `fact`
  against itself — the first version could not see its own test's planted desync;
  `fts5vocab` reads the real index), and FTS5's `'rebuild'` would re-index closed facts,
  so repair calls `EngramDatabase.RebuildFactFts` — the one implementation of what belongs
  in the index. Salience scores stay untouched: nothing writes them yet, and repair must
  not become the first implementation of the formula.
- **`engram export` / `engram import`** — the portable bundle is a *filtered fact
  journal*, not a second format. Export streams `FactJournal.WriteTo` with MoveSubtree's
  `/`-or-`#` boundary; import is `backup replay`'s exact flow, extracted so the two
  ingestion paths cannot diverge. Closed facts travel with their windows and reasons.
  Export refuses to overwrite; stdout mode keeps the bundle clean of the summary.
- **`engram_browse` / `engram_expand` / `engram_revise`** — the last of spec §9. Browse
  folds one subtree query into a table of contents (rendering phantom intermediate
  segments an indexed store always has); expand shows history/related/evidence/source for
  one handle; revise is `FactStore.Remember` with a reason — the store's one-live-fact
  collision rule *is* belief revision, the tool only adds the guards. Both guard tests
  fired and were updated deliberately: surface budget 2600 → 3800 for a measured 3,663
  (the trio costs a lean 1,088), tool count 4 → 7. Browse and expand join the unprompted
  permissions grant as reads; revise stays withheld beside forget, because closing a
  belief should cost a confirmation prompt.

- **Roslyn sidecar (tier 2, D1)** — `engram-roslyn`, a separate Roslyn process the indexer
  batch-feeds JSON-lines over stdin/stdout; one process per run, kill-on-timeout, and any
  failure leaves the batch at tier 0, because the deep tier is an upgrade, never a
  requirement. Packaging measured before building: framework-dependent 16 MB against
  self-contained 99 MB, identical ~40 ms warm start — FD won because every install ran
  from an SDK, and losing the runtime later degrades silently. The merge contract is the
  part that had to be proven: tier 2 replaces symbol and import facts, keeps tier 0's
  file impression, and formats the imports body byte-identically, so a tier swap
  supersedes nothing. The guard went red when the separator was deliberately broken —
  but only the unit test did; the end-to-end parity test sat green until its fixture got
  a second import, because a one-element join reads identically under any separator. The
  driver keys off `Tier == 2` and the language-id lint now covers it. What tier 2
  actually fixes on day one: tier 0's 0–4-space indent window reads a nested type in a
  file-scoped namespace as top-level; Roslyn sees the nesting and writes nothing there.

- **Sidecar deployment** — `install.sh` publishes `engram-roslyn` framework-dependent and
  installs it into `roslyn/` under the prefix with the same manifest discipline as the
  llama natives: record every file, remove exactly that list, never claim a foreign one
  (held by a test that plants an unrecorded file and asserts uninstall leaves it). With
  `--binary`, `--roslyn-dir` ships a prebuilt sidecar the same way `--binary` ships the
  binary; without it, nothing installs and the summary says C# stays at tier 0. `doctor`
  grew a `code analysis` row — presence only, never a launch: absent is Ok ("tier 0
  only"), because Off is a configuration, and the one Broken state is an
  `ENGRAM_ROSLYN_SIDECAR` that points at nothing (D37). The Windows installers
  (install.ps1/uninstall.ps1) do not carry the sidecar yet — recorded here, not hidden.

- **`compact` and schema v3** — the fact-store pruner, dry-run by default. Without
  `--path` it prunes closed regenerable facts; with one it takes the whole regenerable
  subtree — the detached-repo case — plus the code entities nothing references any more
  and the `file_state`/`repo_registry` rows underneath. Clearing file state is the
  load-bearing half: pruned facts with surviving blob hashes would make the next index
  run see nothing changed and rewrite nothing, turning a temporary loss permanent, and
  the test for it was proven able to fail by omitting exactly that delete. A regenerable
  fact sharing a supersession edge with an authored fact is never pruned, in either
  direction — revising a code fact into a belief makes the pair authored history.
  Writing the first prune is what bumped the schema: the v2 delete trigger re-deleted an
  FTS entry `fact_fts_close` had already removed, and FTS5 fails the statement with
  "database disk image is malformed (11)" — measured in a scratch store before a line of
  compact existed, so every closed fact was undeletable. The fix is `WHEN old.valid_to
  IS NULL` on the trigger, shipped as migration v2→v3 with a downgrade fixture that
  deletes a closed fact through a migrated store, and the sqlite_master byte-identity
  guard now covers the second copy of that DDL in `RebuildFactFts`.

- **`SpoolReader.Drain` is gone** — the queue's one consumer is `SpoolQueue`, which peeks
  without deleting and consumes only after commit; a drain that deletes before its caller
  acts was an invitation to lose edits, kept alive only by its own tests. `Parse` and
  `SpooledEdit` stay, because both real consumers read entries through them. Rejected:
  demoting it to test support — the compactor tests only ever needed a name-ordered read.

- **The scanner stops at another repository's border** — measured: `git ls-files` emits an
  embedded checkout as one bare directory entry (an untracked clone as `inner/`, a
  committed gitlink as the plain path), and both used to be counted as *unreadable files*;
  the directory walk recursed straight through the border and indexed the inner repo's
  sources under the outer repo's identity. Both modes now count one `embeddedcheckout`
  skip per inner repo, and the walk also refuses to descend into any directory named
  `.git`, which hardens `use_git = false` over real checkouts. Both guards proven able to
  fail independently.

Still absent after tonight, in plan order: tree-sitter, the salience writer (M5, and
deliberately blind until there is a retrieval benchmark to tune against), D5 — which is
cut by decision rather than unbuilt (plan lines 236–252: no automated cross-predicate
contradiction detection until real transcripts show conflicts the agent missed) — and
sidecar parity for the PowerShell installers.

### D42 amended: a start time you compute is not a start time

Three Linux end-to-end failures in CI turned out to be one bug, and not the one it looked like.
`ServerLifecycle` proved a pid file still described our server by comparing the start time the server
reported about itself against the start time read back for that pid. On Linux those are different
numbers. `Process.StartTime` there is the kernel's `starttime` added to a **per-process estimate of
boot time**, so it partly describes whoever is asking — measured in a container, 24 of 24
cross-process reads disagreed, by up to 3636 ticks.

Exact equality therefore never held on Linux. Every `status` answered `Reused` about a healthy
server, and `stop` did D42's original damage — delete the pid file, say "not running", leave the
server running with nothing left to address it by — on *every* invocation rather than in the rare
case. The `embed --rebuild` failure that read as a sqlite-vec problem was this too: the server check
at `EmbedCommand.cs:125` runs first and silently answered "no server".

Identity is now `ProcessStartToken`: `/proc/<pid>/stat` field 22 plus the boot id on Linux, the exact
kernel start time on macOS and Windows. Worth knowing before touching it:

- **A tolerance was considered and rejected, and the fitted version looks fine.** The skew is not
  jitter — it is the difference between two boot-time estimates, each read off the realtime clock, so
  an NTP step or a VM resume moves it without bound. Any window is either smaller than a possible
  clock step (trading a deterministic failure for an intermittent one) or fitted to hoped-for clock
  behaviour. No window, ever.
- **Nothing backstops this comparison.** `Stop` never runs the health check at all, and `Start`
  terminates precisely *when* the health check failed to vouch — so `IsAnsweringForUs` proving
  `health.Pid == record.Pid` does no work on any kill path. The start token carries termination
  alone.
- **macOS and Windows keep their existing code path deliberately.** Their kernels store an absolute
  creation time. A fix that does not touch the platform nobody here can test cannot regress it.
- **Cut `/proc/<pid>/stat` at the last `)`, never the first.** `comm` is whatever the process called
  itself and may hold spaces and parens both. Cutting at the first one shifts every field left by two
  and still *parses* — it returns num_threads — so a process free to name itself would be free to
  nominate its own start time.
- **Records written before tokens compare `StartTimeUtc` exactly.** That is a legacy path, not a
  fallback; giving it a tolerance would put a number in the kill path permanently for a population
  that is empty.

Eight breaks were applied and each went red, including the two that matter most: sourcing the Linux
token from `Process.StartTime` again reproduces the original bug through the new tier-3 test
(`status called a live server dead`), and ignoring the token makes an altered pid file identify the
server anyway.

### D28's Metal check: the loader records, `doctor` reads

`has_tensor` was prescribed in D28 and never built, because the sentence as written was
unimplementable: it put the check "in `doctor`, where the hardware and the actual `has_tensor`
result are both known", and doctor cannot know that result without loading the weights D35 and
D37 forbid it from loading. So the check is split — `LocalRuntime` writes `metal.json` after a
load, `doctor` only reads it — and D28's sentence is amended rather than left as a trap for
whoever implemented it next.

**The mechanism is now measured, not just described.** Same M5 Pro, same MiniLM GGUF, same
`libggml-metal.dylib`, differing only in which Mach-O is the main executable:

| loader | SDK field | `has tensor` |
|---|---|---|
| `out/engram` (Apple's linker, this machine) | 26.5 | **true** |
| `dotnet` host (Microsoft, prebuilt) | 15.5 | **false** |

So the half-speed path D28 protects against is live — and it is what `dotnet run` and
`dotnet test` get, never what users get. Three things worth knowing before touching this:

- **The plan's spelling was wrong.** llama.cpp prints `has tensor`, with a space. Grepping for
  the documented `has_tensor` finds nothing and proves nothing.
- **`GPU name:` is not the GPU name.** It answers `MTL0`, a device index. The hardware appears
  only on `ggml_metal_init: picking default device:`. The M5 gate keyed to the obvious line
  could never match Apple silicon, so the warning could never fire — a guard that cannot fail.
- **First-wins, not a ring.** `ggml_metal_init` repeats on every context creation, so a ring
  would evict the `has tensor` line in a long-lived server. Measured: one load records 24 lines,
  a process that loaded repeatedly fills the 64 cap.

### `c0dac71` — D45: llama.cpp is linked, not launched

`provider = "local"` loads a GGUF into the Engram process through LLamaSharp.
`LlamaServer.cs`, `LocalRuntime`'s child process and `[embedding] server_path` are gone.
This reversed what had shipped and restored what D1 and spike E originally planned: the
shipped code ran `llama-server` as a child and *located* it in three places without ever
fetching it, so rung 2 of the install ended at a binary the user had to supply.

Three traps worth knowing before touching this code:

- **`LlamaNative.Prepare` must not state library paths.** Stating them was written,
  shipped into a publish, then measured against its own absence — the published binary
  embeds 45 facts with *no* path configuration at all. IL3000 is a warning about a branch
  with a working fallback. Worse, `WithLibrary` replaces LLamaSharp's selecting policy: on
  linux-x64 the CPU package ships only `native/{noavx,avx,avx2,avx512}/` and CUDA adds
  `native/cuda12/`, so a hand-written resolver picks by sort order and `avx` sorts first.
  The Mac-correct version would have run the weakest CPU build on every CUDA machine.
- **`Prepare` holds its lock across the registration, not just the flag.** Releasing early
  lets a second thread skip the gate and load the library out from under the first.
  8 failures in 8 runs before, 0 in 8 after. Needs no weights and no GPU.
- **Pooling is the third silently-failing knob**, after `dim` (D34) and the embedding space
  (D18). The paraphrase test is *not* a pooling guard — flipping MiniLM to `Last` leaves it
  passing. The suite proves the setting reaches llama.cpp, nothing more.

### `fc56beb` — D46: the primer records what it delivered

`session-start` and `subagent-start` now write `long_term_fact_count` and
`tokens_returned`. `fact_count` **stays null** on a primer record and has its own test:
on a recall it means facts returned to the model, and a primer returns a count line plus
up to two example bodies. A nearby number in that field is how D43's phantom-outage bug
happened.

### The installers went soup-to-nuts

Three gaps stood between `scripts/install.sh` and "someone can install this":

- **It required a .NET 10 SDK and never said which version.** An SDK-8 machine passed
  preflight ("dotnet is on PATH") and died inside publish. Resolution is now: PATH dotnet
  with a `^10.` SDK, else a previously bootstrapped `<repo>/.dotnet`, else download
  `dotnet-install.sh` and install one there — privately, `--no-path`, nothing outside that
  directory. The toolchain check runs *before* the SDK resolution, because the missing
  30-second fix (`xcode-select --install`, `apt-get install clang zlib1g-dev`) should be
  heard before a few hundred MB of download. Tests drive the decision through a stub
  `dotnet` (Ubuntu images ship `/usr/bin/dotnet`, so "no dotnet on PATH" is not a state a
  test can arrange by subtraction) and a stub `dotnet-install.sh` that records its argv
  and plants a fake 10.x dotnet whose publish fails — proving the chain without network.
- **The llama natives did not survive the install.** They publish to
  `runtimes/<rid>/native/` and the installer carried only the binary and `libe_sqlite3`,
  so an installed binary on `provider = "local"` died at model load. Measured both ways on
  a sandboxed real install: the installed binary embedded 46/46 facts through MiniLM from
  the prefix; move `runtimes/` aside and the same command fails with *"No library was
  loaded before calling native apis"* — the exact error every installed binary produced
  before the fix. Install records a manifest of every file it copies; uninstall removes
  exactly that list (a planted foreign file under `runtimes/` survives, and a test holds
  that).
- **There was no Windows installer.** `install.ps1`/`uninstall.ps1` are the same
  installers with the same invariants; PATH is the user environment value with the
  previous value backed up to a file, and the prefix defaults to
  `$LOCALAPPDATA\Programs\engram`. A `-Help` run under `pwsh` parses the whole file, so
  the parse gate runs on every OS in CI; the apply round-trip runs Windows-only, with
  `-NoPath` because the runner's user PATH is real state with no sandbox to redirect it
  into — the PATH edit ships proven only as a dry-run plan.

The whole path was then walked for real on this machine: sandboxed `--apply` (real
publish, 123 MB staging → 29 MB after symbol strip), `model install all-minilm-l6-v2`
through the installed binary, `embed --rebuild --apply`, then `uninstall.sh --apply
--purge` back to an empty bin. Falsified four ways before commit: the SDK grep broken →
bootstrap planned despite a 10.x SDK; `--no-path` dropped from the bootstrap → argv
assertion red; the natives copy disabled → round-trip red; a syntax error in
`install.ps1` → the parse gate red naming the line.

---

### `f63e06d`…`5e3ce80` — M3 tier 1: tree-sitter decided (D47) and landed in four commits

The supply question that consumed a whole session for llama.cpp was settled in one decision
here because the probe had already measured everything D47 needed: grammars arrive as pinned,
digest-checked C source and compile at install (`scripts/fetch-tree-sitter.sh`, ~3 s for the
core and three grammars), because upstream ships no binaries and the toolchain is already a
prerequisite of building Engram at all. The runtime never fetches. The installer compiles
the grammars by default — see the install-everything entry below; `--no-tree-sitter` opts out.

The shape of the thing: a hand-rolled binding (`TreeSitter.cs`, 19 function pointers through
`NativeLibrary.GetExport`, AOT-safe by construction), extraction queries carried as registry-row
data with the same `@name`/`@module` contract the regex patterns have, and the tier-0/1/2 merge
rule moved to one place (`DeepTier`) so the sidecar and tree-sitter cannot drift. Queries are
per-row by necessity: `ts_query_new` validates node types against the specific grammar, so a
stale registry query fails loudly at the exact offset rather than matching nothing — that
refusal is the guard against a registry nobody re-verified. ABI compatibility is a measured
*range*, not a version: one core accepted grammars answering 14 (typescript, tsx) and 15
(javascript) in the same process, so `ts_parser_set_language` stays the only authority. The
`#eq?` predicate machinery is implemented in the binding (the C library does not evaluate
predicates), restricted to literal equality, and is what keeps `fetch("url")` out of the
imports while `require("./x")` stays in. `AnalyzerVersion` bumped to 2, which is what re-reads
existing stores under the better extractor. The doctor gained a `tree-sitter` row that answers
from file existence alone — absence Ok ("tier 0 only"), a lying override Broken, and the state
only doctor can see (core installed, grammar missing) Warn, since at index time that gap is a
silent downgrade.

Three falsifications, all red then restored: a planted bad node type failed the conformance
walk with "refused at offset 265 (error 2)" in the assertion message; flipping the predicate
comparison lost `require()` imports in two tests at once; moving the installer's fetch out of
its `if` condition let `set -e` abort a finished install, which is the exact defect
`--with-plugin` once shipped. Final state: the release-tag pins were proven by running the
script into a scratch home and pointing the conformance suite at its own output — the probe's
grammars were repo HEADs, so that run is the evidence the tags parse identically.

### install.sh installs everything by default, and asks exactly one question

Jim's directive, and it is a contract, not a preference: **clone to running with one script,
no thinking required.** Every optional component — the Claude Code plugin, the tree-sitter
grammars, the sqlite-vec extension, the MCP tool permissions — installs by default. An
interactive `--apply` asks once, up front: take the defaults (Enter) or be asked a `[Y/n]`
at each step; `--no-plugin`, `--no-tree-sitter`, `--no-sqlite-vec` pin a step off without
being asked, and the `--with-*` spellings still pin one on. A piped run takes the defaults
unasked, with one deliberate exception carried over intact: the permission grant edits a
file Engram does not own, and silence from a pipe is still not consent — at a terminal,
choosing "everything" up front *is* that consent, so auto mode grants without re-asking.

The reasoning is D41's, applied to the installer: an opt-in flag nobody types is a tier
that does not exist in the field, and M4's adoption gate measures defaults, not flags.
sqlite-vec is the proof case — `fetch-vec0.sh` existed but nothing invoked it, so the
vector lane's extension only reached machines whose owner read the docs; it is now section
9c in the same tri-state shape as the plugin and tree-sitter steps, and that shape was
falsified again (invocation moved out of its `if`, the fetch-fails test went red, restored).
Every apply-mode e2e call site gained `--no-tree-sitter --no-sqlite-vec` so no test touches
the network; the per-step tests pin the *other* two steps off and stub curl for their own.

Debt made explicit rather than hidden: `install.ps1` now lags behind — it still has
`-WithPlugin` as opt-in and no tree-sitter, sqlite-vec, or mode question at all. That
belongs to the existing ps1-parity item and needs a Windows machine to land honestly.

### Embeddings joined the installer, and the picker grew a TUI

The second half of Jim's directive. Section 8b runs the embedding step through the
binary's own picker (`engram init --with-embeddings < /dev/tty`), so the model catalog,
the tradeoff prose, and the endpoint probe stay single-sourced — the installer owns
*when* the question is asked, never *what* the choices are. It is deliberately unlike
the other optional steps: it stays interactive even when the mode answer was
"everything", because provider and model are real costs the user should see. Six
`--embedding-*` flags answer unattended runs (`--embedding-provider none` is a complete
answer); `--no-embeddings` skips; no flags and no terminal defers, and the summary says
how to finish. Four states — configured | skipped | manual | failed — and the tri-state
`set -e` plant was falsified a fourth time: invocation moved out of its `if`, exactly
`WithABadModel_TheInstallStillFinishesAndSaysWhatBroke` went red, restored. That failure
test needs no network stub — a model id the catalog does not know is refused before
anything downloads.

The TUI is hand-rolled ANSI in `Tui.cs`, no package, because binary size is hook latency
(1.06 MB starts in 3.44 ms, 21.2 MB in 7.80 ms — adding a TUI dependency would tax
`file-touched` forever). One control flow: `EmbeddingSetup.Ask` routes presentation
through `Tui.Menu`/`Tui.Line`, and the plain path is **byte-identical to what the 26
existing `EmbeddingSetupTests` freeze** — rich mode needs a real console on both ends
plus a sane `TERM`, colors additionally honor `NO_COLOR`. The installer got the same
treatment in shell: step headers, a boxed banner, bold prompts, all gated on the same
terminal test. One pty test (`TuiPtyTests`, macOS-gated, `script -q /dev/null`) proves
the arrow-key menu actually renders and Enter lands `provider = "none"` in the config —
without it the rich path would ship forever unexecuted, since every other test drives
redirected streams and by design gets the plain prompts. What that one test does not
cover: arrow-key navigation itself (it pipes a bare Enter), and the rich path on Linux.
`install.ps1` lags further still — no embedding step, no styling; same parity item.

### Grammar v2 landed (D48): nested types, members, overloads

The last named item of M3's deep tiers. A symbol fragment is now the scope chain joined
with `/` (`Widget.cs#Widget/Inner`, `FactStore.cs#FactStore/Remember`); overloads append
their parameter list as written, whitespace-collapsed, **only on collision** — a unique
name keeps its stable bare form, and the one normalization lives in `DeepTier.Fragments`,
which composes every fragment for both deep tiers (neither tier ever spells an address).
`CodePaths.GrammarVersion` is 2, and the bump re-addresses nothing by construction: every
v1 extractor was top-level-anchored, and a top-level v2 address is spelled like its v1
address, so the forced re-read only adds member entities.

The tree-sitter binding deliberately gained no node navigation — nesting is the query
pattern's shape (`@scope` beside `@name`, `@params` for overloads), which keeps it inside
what `ts_query_new` validates. All the new node names (`interface_body`,
`function_signature`, `public_field_definition`, `abstract_method_signature`,
`method_signature`, JS `field_definition`) compiled against the real pinned grammars on
the first run. The sidecar walks nested types at any depth and emits surface members only
(explicit public/internal/protected, or interface membership); tier 1 filters `private`
on the declaration line and `#name` members never match structurally. Deliberately not
emitted: enum members, indexers, operators, local functions — D44 already measured what
near-noise does to lexical ranking. Both falsifications went red on exactly their guard:
unconditional suffix → the two collision-rule unit tests; `PrivateKeyword` added to the
surface list → the sidecar batch test on the planted private field. (A `if (false)` plant
does not survive warnings-as-errors — CS0162 fails the build before the test can go red;
plant a realistic wrong edit instead.)

### The uninstaller inventories, confirms, and keeps the backups

Jim's directive, three parts. The script now leads with what is *actually installed* —
binary, plugin, permissions, PATH entry, home, backups, each probed rather than assumed —
and an interactive `--apply` collects a confirmation per found item before removing
anything: `[Y/n]` for what an uninstaller exists to remove, `[y/N]` for the home, because
uninstalling a program does not imply wanting the memory it kept gone (`--purge` flips
that one question back to default-yes). And backups now survive a purge by default:
`backups/` holds the plain-text journal that can restore the store into a fresh install
(D32), so the old `--purge` deleted the recovery path along with the thing it recovers —
a safety inversion. Deleting them takes an explicit yes at the prompt or
`--remove-backups`. Piped runs keep the prior semantics exactly (standard items removed,
home only with `--purge`), which is why every pre-existing round-trip test passed
unchanged — a fresh install's `backups/` is empty, and an empty backups directory
protects nothing, so it does not trigger the keep. Falsified: the keep branch forced
off turned exactly the keeps-backups test red, restored. `uninstall.ps1` lags again;
recorded in the parity memory file, where the keep-backups default is flagged as the
load-bearing piece.

### `install.sh` acts by default (D49), and the destructive rule got its boundary back

Jim: "folks will just want to get running." The installer had been on the dry-run-first
list, and it did not belong there — every other verb on that list removes or rewrites
something already present, while the installer only adds and its one file edit is backed
up and marked (D33). Running it now installs; `--dry-run` is the brake. `--apply` is
parsed and ignored rather than removed, because ~20 e2e call sites and every README
written before this pass it, and turning a silent no-op into an unrecognized-argument
error buys nothing.

Eight test sites were relying on bare-means-dry-run — the two `InstallerSoupToNutsTests`
dry runs, two in `InstallerRoundTripTests`, and the bare `Install(home)` helper call in
each of the embedding, tree-sitter, sqlite-vec and plugin files. Those are precisely the
tests that assert *nothing happened*, so they were the ones that would have kept passing
while asserting the opposite of what they read. Two new guards pin the default down, and
the falsification was unusually loud: restoring `apply=false` turned 14 of the 17
round-trip tests red, including both new guards by their own messages — with the old
default and `--apply` inert, every `--apply` test does nothing, which is exactly how a
half-applied inversion would fail.

The piped-run consequence is intended and worth stating: a run with no terminal now
installs everything Engram owns unasked. The consent exception is unchanged and matters
more for it — the MCP permission grant edits a file Engram does not own, and a run nobody
is watching still never grants it. `install.ps1` deliberately did not follow: the whole
content of the change is that a script acts without being asked, parse-gating cannot see
an inverted conditional, and nobody has run that script on Windows even once. It keeps
`-Apply`, and the README now says so instead of claiming parity it does not have.

---

### The installer starts the server, and proves it (D50)

Jim, immediately after the last one: the install script should start engram at the end and
validate it is running. Nothing else starts the server — session start spawns maintenance,
not the daemon — so a fresh install left memory unreachable until somebody typed `engram
start`, and the person least likely to know that is the one installing for the first time.

The reinstall case is what makes it a defect rather than a nicety. Section 2 stops the
daemon serving the binary about to be replaced, which it has to: `cp` over a running
executable on macOS changes its pages underneath it. So an upgrade *actively ended with the
server down* — stopped one, started nothing — and the summary reported success. Same shape
as D49: the script did what it was told and not what it was for.

Starting is not the claim; running is. `start` health-checks before returning 0, but that is
the launching process vouching for itself, so the step asks again through `status` — a
separate process, pid file and start token and an HTTP health check, which by D42 is a
different question and the one every later consumer actually puts. `StatusCommand` exits 0
only for `Running`, which is what makes it usable as a predicate; this is also the one place
`Running` rather than `ServerIsAlive` is right, because `Wedged` and `VersionMismatch` both
mean the server did *not* come up healthy. No retry loop, deliberately: given start's
guarantee a disagreement is news, not a race, and a window here would trade a deterministic
failure for an intermittent one.

Three guards, and the load-bearing falsification is the third: replacing `start` with a
command that succeeds without launching anything leaves the step running and the summary
correctly saying `NOT running`, which is the only break that tests the *validation* rather
than the start. Two of the three breaks silently no-opped on the first attempt — the `perl`
patterns contained `$target` and `$with_start`, perl interpolated them to empty, nothing
matched, and both reported green. Checksumming the file across the edit is what caught it;
an unchanged file is now treated as a failed falsification rather than a passed test.

Every other installer test gained `--no-start`. Thirty-odd apply-mode call sites would each
have launched a real daemon on the default port, fighting each other and whatever server the
developer running the suite has up, then had their sandbox home deleted underneath them. The
three that do start one take a private port through `ENGRAM_PORT` (new:
`InstallerHarness.RunScriptWithEnvironment`, because `install.sh` has no `--port` to forward)
and stop it in a `finally`. Full installer suite after the change: 40 passed, 0 failed, and
the machine's `engram serve` count unchanged across the run.

---

## Verified vs. not

| claim | status |
|---|---|
| the installer starts the server and confirms it independently | measured — 3 guards pass; falsified three ways, including a `start` that succeeds without launching |
| no installer test leaks a daemon | measured — 40 passed, `pgrep 'engram serve'` count identical before and after the suite |
| the same start step on `install.ps1` | **not ported, not run** — and the Windows fd-redirection equivalent of `ProcessServerLauncher` is unproven |
| `install.sh` installs with no flag, and `--dry-run` still changes nothing | measured — both guards pass; falsified by restoring `apply=false`, 14 of 17 round-trip tests red |
| the same inversion on `install.ps1` | **not ported, not run** — needs a Windows machine; parse-gating cannot see an inverted conditional |
| in-process embedding works on the published AOT binary | measured — 45 vectors in 0.46 s, MiniLM on Metal |
| binary growth does not move the hook budget | measured — 21.84→22.25 MB, `file-touched` p50 9.44→9.51 ms |
| publish output trimmed to the target RID | measured — 210 MB → 121 MB, of which 5.7 MB is llama.cpp |
| CUDA *packaging* | measured — real `linux-x64 -p:EngramGpu=cuda12` build, output inspected |
| CUDA *execution* | **not measured** — no NVIDIA device here |
| pooling values per model | **argued from architecture, never benchmarked** |
| the SDK field decides the Metal tensor path | measured — controlled pair on one M5 Pro, `sdk 26.5` → on, `sdk 15.5` → off |
| the shipped binary gets the fast path | measured — `out/engram` records `has tensor = true` through Engram's own code |
| the metal Warn branch, end to end | measured — the published binary renders it from a record the JIT host wrote, provenance line and all |
| that Warn alone never fails the exit code | tested, not observed — `Warn` is not `Broken`; no home here has metal as its only non-ok row |
| the Warn on hardware that natively lost the path | **not measured** — no such machine available; the JIT host is the only way to produce `false` here |
| embeddings from an *installed* prefix | measured — 46/46 facts through MiniLM from a sandboxed real install, and the counterfactual (runtimes/ moved aside) fails with the pre-fix error |
| the Windows installers | **not measured** — parse-gated on every OS, apply round-trip is Windows CI's job |
| the SDK bootstrap against the real dotnet-install.sh | **not measured** — the argv contract is tested through a stub; the real download path is exercised by humans |
| the Linux server-identity fix | measured — full suite green on a linux-arm64 AOT build in a container, including the 3 tests that failed in CI |
| that same fix on linux-**x64** | **not measured here** — Docker on Apple silicon serves arm64 and its `ld` will not link x86_64. The mechanism is procfs, not the instruction set, so CI is the check |
| Windows tiers 0–2 | **measured, green** — 369 core + 532 integration + e2e-under-JIT on Windows CI, after the pooling fixes |
| Windows e2e against the AOT binary | **measured, failing in two named families** — 17 installer tests that want Windows skip guards, 17 server tests dead on the `/bin/sh` launchers (see open work 4) |
| D6's gate on M3 | **unread** — see below |
| D18's gate on M4 | **unmet**, and the adoption fraction is not computable (D43) |
| tier-1 extraction through the pinned release tags | measured — `fetch-tree-sitter.sh` into a scratch home, 8-test conformance suite against its own output |
| the tree-sitter ABI range | measured — one core accepted ABI 14 and 15 in one process; nothing compares versions |
| tier-1 guards can fail | proven — query refusal, predicate flip, and the installer `set -e` plant each went red before restore |
| tier-1 on Linux/Windows | **not measured** — the binding's naming covers `.so`/`.dll` and the script's case arms exist, but only macOS has executed them |
| the plain prompt path under the TUI refactor | frozen — the 26 pre-existing `EmbeddingSetupTests` pass unchanged against the reworked `Ask` |
| the rich TUI path renders and lands a choice | measured — one macOS pty test: menu paints ANSI, Enter writes `provider = "none"` |
| arrow-key navigation, digits, Esc | **not measured by any test** — the pty test pipes a single Enter; the key loop beyond that has only been driven by hand |
| the embedding tri-state guard can fail | proven — the `set -e` plant turned exactly the bad-model test red, restored |
| grammar v2 queries against the real pinned grammars | measured — conformance walk green on first contact; fixtures pin scope chains, overload suffixes, and the private filter |
| grammar v2 guards can fail | proven — unconditional-suffix and private-as-surface plants each turned exactly their tests red, restored |
| the v1→v2 bump re-addresses nothing | argued from anchoring (all v1 extractors were top-level-only), not measured against a populated v1 store |

---

## Open work, ranked

### 1. M3's deep tiers — complete; what remains is watching the telemetry

Tier 0 shipped on an explicit user instruction, which is worth recording precisely:
D6's gate never *fired* — the telemetry that was supposed to open it (code-structure recall
misses) still reads zero recalls of that shape, ever. The gate was overridden, not met, and
the same telemetry now measures whether the index earns its keep: code facts exist, so a
code-structure recall can finally hit or miss something. Tiers 1 and 2 have since both
landed (the sidecar earlier, tree-sitter D47), and grammar v2 (D48) closed the last named
item — nested types, members, overloads, one coordinated bump. Nothing in this line is
blocked on engineering now: what remains is whether code-structure recalls ever arrive,
and whether the deliberately-unemitted populations (enum members, indexers, operators)
turn out to be asked about, which is a telemetry question, not a backlog item.

### 2. The adoption question itself — 28 writes against 7 reads

This is spec §1.2's stated cause of death ("every predecessor died because the LLM never
called the memory tool") appearing in Engram's own telemetry. Note the confound before
concluding anything: the primer reaches every session without a tool call, so recall
undercounts delivery by construction — which is exactly what D46 was written to fix. Give
it some accumulation before drawing a line through it.

### 3. Score mass in `coverage` (D44, deliberately left open)

`coverage` currently keys off lane agreement only. The spec names score mass as a second
input. D44's reasoning for leaving it: one unmeasured knob is a rule, two are a preference.

### 4. Windows CI: tiers 0–2 green, e2e decoded into two named defects

The red run decomposed into four clusters, all diagnosed by reading rather than reproducing —
there is still no Windows machine here, so the next CI run is the verdict, not a formality:

- **346 of 358 were one missing line of cleanup.** Disposing a `SqliteConnection` pools the handle
  rather than closing it; Unix lets an open file be unlinked, Windows turns it into `IOException` on
  every `SandboxHome` delete. Fixed with `EngramDatabase.ReleasePooledConnections` — **targeted**
  `ClearPool`, never `ClearAllPools`, which disposes handles already handed out and was measured in
  this suite as an `ObjectDisposedException` inside an unrelated test's initializer. A guard in
  `VectorExtensionLoadTests` asserts both halves (released pool goes cold, other pools stay warm),
  using extension inheritance as the observable; both assertions were proven red independently —
  once against `ClearAllPools`, once against a connection string that no longer matches `Open`'s,
  which "succeeds", clears a key nothing was stored under, and releases nothing.
- **9 core failures were POSIX literals in expectations.** `EngramHomeTests` now normalises the
  *expected* path through the same `Path.GetFullPath` the resolver uses — stated independently of
  the input, so a resolver that picks the wrong source or fails to normalise still goes red.
- **`RepoScannerTests` cleanup** — git marks objects/packs read-only, and Windows refuses to delete
  read-only files. `TempRepo.Dispose` clears the attribute first.
- **`InstallerRoundTripTests`** claimed in a comment that it never runs on Windows; nothing enforced
  that. Now an explicit `Assert.SkipWhen(OperatingSystem.IsWindows(), …)` — install.sh is a POSIX
  installer.

The pooling fix is the one that **cannot be falsified on macOS or Linux** — both allow the
delete-while-open that Windows refuses — so Windows CI is its only instrument. Alongside these,
`DiagnosticsTests` stopped racing for a free port on macOS: the stub server binds port 0 and
prints what it got.

The first CI run cut Windows from 358 failures to 12, and the residue was the same disease in
two shapes the first fix missed: `SandboxHome.Dispose` released only the *main* database's pool,
while a snapshot written by `BackupStore` is its own file with its own pool the moment
`FingerprintOf` or a test opens it (Dispose now releases every `*.db` under the root); and the
creation-from-nothing tests deleted a fully initialized home at the *start* of the test, where
the sandbox's own pooled handle blocks the inline delete — those construct with
`initialize: false` now, so the deleted directory never held a database at all. The macOS flake
was the alphabetically-first test paying the runner's cold-start on the first python spawn:
`ReadPort`'s 30 s bound covered a warm start only, and its timeout branch was the one failure
path that attached no diagnosis — now 120 s, and it kills the process then reports stderr.

The verdict came in three acts. Every run after `dd81ebb` **hung** in the Windows e2e step —
2h55m and climbing where the runs before it failed in 3m24s, seven runs deep against a 6-hour
default job timeout that a private repo bills double. Nothing in `dd81ebb` can block; the pooling
fix let Windows get past its fail-fast failures into something downstream that had always been
waiting. `89692dd` added `timeout-minutes: 30` and `--blame-hang` so a wedged test aborts and
prints its name, and the instrumented run decoded everything:

- **Tiers 0–2 are fully green on Windows** — 369/369 core, 532 integration (25 environment
  skips), e2e-under-JIT all passing. The pooling fixes did exactly what they claimed.
- **The e2e step against the AOT binary crashed on `/bin/sh`.** `ServerLauncher.cs:33` and
  `MaintenanceLauncher.cs:60` both route their detached child through a shell Windows does not
  have; the AOT binary dies by fail-fast (`0xC0000409`) on the spawn. Every server-shaped family
  fails on it: ServerLifecycle, McpServer, SessionMemory, Probe, ServerFirstRun, Queue
  housekeeping, EmbedRebuild's refuse-while-running.
- **The hang was the harness, not the product.** Job cleanup listed two orphaned console hosts
  and no `engram.exe`: the crashed binary's conhost kept the redirected pipe open, and
  `EngramProcess` read both pipes to EOF *before* its 10-second `WaitForExit`, so the bound
  guarded the wrong event — EOF on a pipe is not exit of a process. `2cdfb2e` drains the pipes
  concurrently and gives the join its own 5-second bound; the guard test plants a stub that exits
  instantly leaving a backgrounded sleep holding stdout, and was proven red against the old body
  (blocks the sleep's full 30 s, then passes nothing).

Current Windows state, measured on `2cdfb2e`'s run: the e2e suite completes in **1 minute** —
34 failed / 69 passed / 17 skipped of 120, every test named, no hang possible. The 34 are two
populations: **17 installer-family** (POSIX `install.sh` driven through `/bin/bash` and
`File.SetUnixFileMode` — these want the same `Assert.SkipWhen(OperatingSystem.IsWindows(), …)`
the round-trip test already carries, and the population is exactly the one ps1-parity work will
revisit) and **17 server-family** (the `/bin/sh` launchers — the one production defect Windows
actually has). The launcher fix is a design, not a patch: Windows detachment means no shell,
`CreateNoWindow`, descriptors not inherited, and it must not disturb the D42 identity machinery;
it wants a session with a Windows machine or at least Windows CI iteration room. The seven hung
runs were left to expire at their caps — cancelling them was denied to this session's
permissions, and `gh run cancel` on anything still burning is the first thing worth doing by hand.

### 5. Smaller

- D27's open sub-question — how a repo learns its project — is implemented as designed
  (`[indexing] project` re-binds, the repo directory name is the default, read at
  `CodeIndexer` registration). What remains is only that no multi-codebase project has
  exercised the re-bind yet.

---

## Gotchas that cost real time

- **Set `ENGRAM_HOME` before running `./out/engram` by hand.** The three test guards protect
  test code, not your shell. A verification command without it writes to the real `~/.engram`.
  This has already happened once.
- **`init --with-embeddings` is a picker, and it no-ops when stdin is not a terminal.** It says so
  and prints the non-interactive forms (`init --provider local --model …`), but a script that
  checks only the exit code sees 0 and concludes it installed something. It did not.
- **Linux is reachable, and it is worth reaching.** Docker Desktop is installed; `open -a Docker`
  starts it. A container that mounts the repo **read-only** and copies the source in — excluding
  `out/`, `.git/`, `bin/`, `obj/` — builds, AOT-publishes and runs the whole suite in a few minutes.
  Mounting read-write instead leaves root-owned Linux artifacts in `bin/` and `obj/` where the macOS
  build expects its own. Two traps cost real time: `docker run` **without `-i` discards stdin**, so
  `bash -s <<EOF` runs an empty script and exits 0 having done nothing, which looks exactly like
  success; and the image is **arm64** on Apple silicon, so `-r linux-x64` dies at
  `ld.bfd: unrecognised emulation mode: elf_x86_64` — publish `linux-arm64`. Two integration tests
  fail in-container and pass in CI (`AtomicFileTests.Write_TempFileCreationFails…`,
  `SpoolCompactorTests.AnEntryItCannotOpen…`): both work by making a file unopenable, and the
  container runs as root, which bypasses that.
- **Windows is still unreachable**, and CUDA execution with it. Anything claiming to work there is
  an argument, not a measurement — which is why the D42 fix deliberately leaves Windows on the code
  path it already had.
- **Mac is still the only hardware for the Metal path.** The JIT host is the one genuine second
  configuration available: it really does lose the tensor path, which is what makes the Warn branch
  reachable at all.
- **Prove a guard can fail.** Two guards this session looked green while proving nothing —
  the paraphrase test survived a deliberate pooling break, and an early AOT publish passed
  only because nothing referenced the assembly yet. Break the thing, watch the test fail,
  restore.
- **`git checkout` destroys uncommitted work** and behaves differently on untracked files.
  It ate a test-file rewrite once, and then a second session used it to restore a
  falsification plant and wiped every uncommitted registry edit in the same file. A plant
  in a file carrying unfinished work is reverted by a second edit, never by `git checkout`.
- **The subagent cap is per-session (200).** It was exhausted, so the Ultra Advisor and
  `task-gopher` were both unreachable for the whole session. Raise
  `CLAUDE_CODE_MAX_SUBAGENTS_PER_SESSION` if you want them.
- **`dotnet test` filters run in a fresh process**, so env vars from a previous shell command
  are gone. A test that "passes in the suite and fails alone" is usually this, not a flake —
  though this session it was a genuine race that only the unset-env ordering exposed.
- **The repo owned no `nuget.config`**, so restore inherited whatever package sources the
  machine carried. Invisible with only nuget.org and fatal with a second feed: central
  package management plus two sources is NU1507, and warnings are errors, so a work machine
  with a GitHub Packages source failed `install.sh` before the first line compiled. `<clear />`
  at the repo root is the fix because it drops the inheritance rather than adding to it —
  suppressing NU1507 would leave restore dependent on machine configuration, which is the
  defect itself. Falsified by removing the file under a simulated two-source home: exit 1 with
  NU1507 on all six projects, exit 0 and zero with it.
