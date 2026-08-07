# Engram — progress snapshot

**As of 2026-08-07.** Working tree clean, 46 decisions (D1–D46). M3's tier 0 shipped overnight
on an explicit user override of D6's gate.

This is a handoff, not an authority. `CLAUDE.md` holds the invariants,
`docs/engram-implementation-plan.md` holds the decisions and their reasoning, and
`docs/engram-schema.sql` is the authority for database shape. Where this file and those
disagree, they win and this file is stale.

---

## Read these first, in this order

1. `CLAUDE.md` — the invariants that are easy to break by accident. All of them were paid
   for by a real defect.
2. `docs/engram-implementation-plan.md` — D1–D46. Skim the headings; read in full any
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

The remaining skips are `sqlite-vec` tests, which need the extension side-loaded into the
home under test.

---

## What landed recently

### `21708a4`…`2f4c38c` — M3 tier 0: the code index, gate to shipped in four commits

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

### `93fe169`…`f545d2b` — the M1 stragglers closed in one sitting

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

### `6d3ba89` — D45: llama.cpp is linked, not launched

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

### `e322643` — D46: the primer records what it delivered

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

## Verified vs. not

| claim | status |
|---|---|
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
| Windows anything | **not measured** — no machine, and this is why the fix leaves Windows on its existing code path |
| D6's gate on M3 | **unread** — see below |
| D18's gate on M4 | **unmet**, and the adoption fraction is not computable (D43) |

---

## Open work, ranked

### 1. M3's deep tiers — the sidecar and tree-sitter are what remain

Tier 0 shipped (above) on an explicit user instruction, which is worth recording precisely:
D6's gate never *fired* — the telemetry that was supposed to open it (code-structure recall
misses) still reads zero recalls of that shape, ever. The gate was overridden, not met, and
the same telemetry now measures whether the index earns its keep: code facts exist, so a
code-structure recall can finally hit or miss something.

What remains, in the order the plan argues for:

- **tree-sitter (tier 1, D24)** — real grammars for the languages regex is faking.
- **Overload grammar v2 (D27)** — nested types, overloads, member fragments. A grammar
  bump re-keys, which is adopt/merge's job, and `code_index_version` already forces the
  re-read.

### 2. The adoption question itself — 28 writes against 7 reads

This is spec §1.2's stated cause of death ("every predecessor died because the LLM never
called the memory tool") appearing in Engram's own telemetry. Note the confound before
concluding anything: the primer reaches every session without a tool call, so recall
undercounts delivery by construction — which is exactly what D46 was written to fix. Give
it some accumulation before drawing a line through it.

### 3. Score mass in `coverage` (D44, deliberately left open)

`coverage` currently keys off lane agreement only. The spec names score mass as a second
input. D44's reasoning for leaving it: one unmeasured knob is a rule, two are a preference.

### 4. Windows CI fixes are in, awaiting the run that can verify them

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

### 5. Smaller

- D27's open sub-question.
- `docs/engram-spec.md:62` "not a Sage replacement" — flagged, unresolved.

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
  It ate a test-file rewrite this session.
- **The subagent cap is per-session (200).** It was exhausted, so the Ultra Advisor and
  `task-gopher` were both unreachable for the whole session. Raise
  `CLAUDE_CODE_MAX_SUBAGENTS_PER_SESSION` if you want them.
- **`dotnet test` filters run in a fresh process**, so env vars from a previous shell command
  are gone. A test that "passes in the suite and fails alone" is usually this, not a flake —
  though this session it was a genuine race that only the unset-env ordering exposed.
