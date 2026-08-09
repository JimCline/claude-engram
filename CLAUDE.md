# Engram — working rules

Read `docs/engram-implementation-plan.md` before any non-trivial change. It holds
fifty-eight decisions (D1–D58) that resolve questions the spec left open, and each one was
reached by argument or measurement, not preference. `docs/engram-schema.sql` is the authority for
database shape.

## Invariants that are easy to break by accident

**One home resolver.** `EngramHome` is the only code permitted to read `ENGRAM_HOME`,
`HOME`/`USERPROFILE`, or call `Environment.GetFolderPath`. Everything else takes paths
from it. `NoHardcodedPathsTests` fails the build otherwise. If a literal genuinely needs
an exception, add `// engram-lint:allow(<reason>)` on that line with a real reason — do
not move the constant into `EngramHome.cs` to dodge the check, which defeats the guard.

**No test touches the real instance.** Integration and end-to-end tests run against a
disposable home. `SandboxHome` refuses to construct against the real one, and an
assembly fixture redirects `ENGRAM_HOME` for the whole test run. Anything writing to
`~/.claude` in a test is a bug regardless of whether the test passes.

**Set `ENGRAM_HOME` before invoking the published binary by hand.** Those three guards
protect *test code*. They do not constrain a shell running `./out/engram ...`, which
resolves the default home and writes there — correctly, since that is its production
behavior. A verification command that omits `ENGRAM_HOME` will litter the real
`~/.engram`; this has already happened once. Pass `--home` or export `ENGRAM_HOME`
first, every time, including in ad-hoc checks.

**Facts are append-only.** Belief content — predicate, body, object, validity — is
immutable once written. Only `valid_to` and `superseded_by` are ever updated, and only
to close a fact. `path` is the sole exception: it is addressing metadata that follows
its entity on rename (D2), not belief content.

**Derived state is repairable; authored truth is not.** `compact` and `repair` may only
touch what can be regenerated — the FTS index, salience, denormalized paths, indexed
code facts. Neither may ever create, alter, or delete a fact body, predicate, validity
window, or supersession row (D8). Two FTS facts to know before touching `repair`'s
detector, both measured: on an external-content table every non-MATCH query — including
`SELECT rowid FROM fact_fts` — is answered from the *content* table, so the obvious
index-vs-fact set difference compares `fact` against itself and calls any desync healthy
(the first detector could not see its own test's planted break; `fts5vocab` is how you
read the real index). And FTS5's own `'rebuild'` command re-reads the whole content
table, closed beliefs included, while the index deliberately holds live facts only —
rebuild through `EngramDatabase.RebuildFactFts`, which is the one implementation of what
belongs in the index.

**`fact_token` is maintained from C# call sites, and `--tokens` reads the stamp only.** The overlap
index cannot be trigger-maintained the way `fact_fts` is, because a trigger cannot call `Tokenizer`
and a second tokenizer written in SQL agrees with the first until one of them is tuned — after which
a term spelled two ways scores zero and the lane returns less, which reads as an empty corpus rather
than a bug. So every write goes through `FactTokenIndex.Add`/`Remove` at the same chokepoints
`fact_fts` uses, and the guard is a from-scratch recomputation diffed against the incrementally
maintained table, not a unit test. Readiness is a stamped tokenizer version, never a probe: an index
one version behind is not corrupt, it disagrees, and by D8 it costs the overlap lane and nothing
else. `repair --apply --tokens` runs from the session-start child on **every** session, so it checks
that stamp and nothing else — `CountMissing` and `CountExtra` scan the whole token table and belong
to the full `repair` verb, beside the FTS detector. Measured, and each number decides something: a
rebuild is 297 ms at 5,097 live facts and **4,161 ms at 50,097** (701,358 token rows), which is why
that scan may not ride session start; and `CountMissing`'s `NOT IN` beats the `EXCEPT` that would
make it match `CountExtra` by **22 ms against 42 ms** at that size, because SQLite plans the first as
a bloom filter probed during the scan and the second as a temp b-tree. The zero-token exclusion in
`CountMissing` is load-bearing in one direction only — counting an all-stopword fact as a missed
`Add` leaves `TokenIndexNeedsRebuild` permanently true, so every repair rebuilds and none stops the
next — which is why the assertion that matters sits *after* the apply (D59).

**Every connection sets its own pragmas.** `foreign_keys`, `busy_timeout`, and
`synchronous` are connection-scoped. Setting them in a schema file configures the
connection that applied the file and nothing else. Open through the one shared routine,
`EngramDatabase.Open`. Measured, because the obvious version of this rule is half wrong:
a raw `Microsoft.Data.Sqlite` connection already reads back `foreign_keys=1` — the
provider sends it — but `busy_timeout=0` and `synchronous=2`. Deleting the `foreign_keys`
line breaks no test; deleting either of the others does. Keep all three, and do not write
a guard that claims to protect the first.

Loadable extensions are connection-scoped for the same reason, and **connection pooling
hides it**. Measured: load `sqlite-vec` on one connection, dispose it, and the next
`EngramDatabase.Open` in that process still answers `vec_version()` — the pool handed back
the same `sqlite3` handle with the module still registered. Call `ClearAllPools` first and
the extension is gone, which is what a *different* process gets. So a vector query that
passes in the MCP server can fail in a hook, and vice versa, purely on pool luck. Whatever
loads the extension in M4 must do it per connection and never infer from a successful query
that it is loaded.

**Every write is `BEGIN IMMEDIATE`.** A deferred transaction that upgrades to a writer
raises `SQLITE_BUSY_SNAPSHOT`, which `busy_timeout` cannot wait out (D4).

**`file-touched` never opens the database.** It writes one spool file per invocation — its own,
which is why the queue can never lose an entry to contention — and appends one telemetry record,
which is shared and therefore may. Its budget is 10 ms and it must hold
unconditionally, not just when nothing else is writing. Measured on the published binary:
p50 7.82 ms, of which **+0.02 ms is the hook and the rest is process start**. Opening the
database costs **1.0–1.5 ms**, measured by A/B-ing `probe` against homes with and without an
`engram.db` — it skips the store when the file is absent, so the difference is the open. The
2.1–2.4 ms that `session-start` and `user-prompt` add over the same floor is that open *plus
each hook's own work*; charging all of it to the open, as this file previously did, overstates
it. **`user-prompt` and `file-touched` still hold that; the primer hooks are corpus-proportional
and always were.** Measured on the published binary against a `probe` floor of ~10 ms:
`session-start` costs **16 ms at 5,308 live facts and 54 ms at 50,097**, `subagent-start` 14 and
51. The gap between the two is the maintenance spawn, **1.6–3.4 ms**, which is what D28 recorded
for it all along. What is corpus-proportional is the primer's own read, and beware the figures
this file used to carry here — 61 ms and 93 ms, and 11 ms and 76 ms for the read alone. **Those
were measured through a pipe that the detached maintenance child was holding open**, so they timed
that child's whole run and not the hook; see `MaintenanceLauncher`, where the defect and the
controlled measurement both live. Time a hook through a *file* to see what it costs, or fix the
descriptor leak first. What survives the correction: `PrimerSummary.Read` replaced
`FactCatalog.ReadLongTerm` on this path and `subagent-start` — the clean isolate, since it builds
the same primer and spawns nothing — went **69.5 → 43.4 ms over floor at 50,097** and 7.8 → 3.7 at
5,308, with the primers byte-identical on both binaries at both sizes. The ~40 ms that remains at
50,097 is the topic histogram, which transfers one row per distinct subject but still scans every
live fact; at the 5,000 this instance holds it is ~4 ms. Nothing here breaches
`file-touched`'s 10 ms rule, which is about that hook alone. So the rule does not rest on the
arithmetic — an opening `file-touched` would still fit at
p50. It rests on the word *unconditionally*. Under an indexer-shaped writer committing
back-to-back chunks this hook holds p50 9.29 ms and grows no tail, because a hook that never
opens the database cannot wait on a lock; one that opens can, and `busy_timeout` is 5000 ms
against a 10 ms budget. `FileTouchedBudgetTests` guards the margin, not the absolute number,
so it fails when the rule breaks rather than when the machine is busy.

The rule is *never opens the database*, not *does as little as possible*. `file-touched` reads its
stdin payload to record which file changed, because a queue of bare timestamps answers one bit no
matter how long it gets, and the indexer that drains it has to know what to re-read. Measured on
the published binary: piping the payload in costs 0.27 ms, and `user-prompt` parses the same
stdin, opens the store *and* writes a fact for 0.67 ms more than `file-touched` spent doing none of
it. A spool entry is a timestamp then an optional path — optional, so the entries written before
this existed still drain (D39).

**The hook's one shared write never waits, and the measurement that allowed it nearly said the
opposite.** `file-touched` appends a `file-touched` record so a live feed can see edits at all, and
it is the sole caller of `Telemetry.Append`'s retry-budget overload, passing `TimeSpan.Zero`. That
value is not "retry briefly": `DurableAppend` checks `elapsed < retryBudget` *before* its back-off
sleep, so zero is exactly one attempt and no sleep, while any small non-zero budget would be worse
than either extreme — one collision sleeps up to 20 ms against a 10 ms budget. Cost is **+0.11 ms
at the minimum, +0.08 ms at p50** on the published binary. The first attempt at that number said
**+0.78 ms** and very nearly moved this write into a polling service in the server that would have
existed for no reason: the A/B loop ran the same arm first every iteration, which charges arm A
whatever the first of a pair costs. Alternate the order, and calibrate by running the *same binary
against itself* — that reads ±0.07 ms, which is the only way to know the difference you are
measuring is larger than the harness. What a dropped collision costs is likewise measured, and it
is load-dependent, so no test may assert a delivery rate: 2.0% lost at twenty concurrent editors on
an idle machine, 1.6% at fifty, but **30% for the same test inside the full suite**. Zero torn
lines and zero lost spool entries throughout, which is the pair that actually matters — the queue
the indexer reads is per-invocation and cannot collide, and `FileShare.None` makes a collision cost
a whole record rather than tear one (D56).

This rule is about that hook, not about hooks: D4 justifies it entirely by per-edit
frequency and write contention. The primer hooks — `session-start`, `subagent-start` —
do take a short read and close it, because a primer that reports memory from a hardcoded
list disagrees with recall the moment a fact is forgotten. `user-prompt` writes, once per
message the user sends: it is the only place a fact stated in passing can be caught, and a
capture the model has to opt into is a capture that does not happen. Each of those was
measured against the version it replaced — a hook that opens the database is a decision
with a number behind it, never a default.

The budget's remaining headroom is 22% and it is all process start, so **binary size is a
latency decision**: 1.06 MB started in 3.44 ms, 21.2 MB starts in 7.80 ms. That is a
second reason, independent of AOT-hostility, that D1 keeps `sqlite-vec` side-loaded rather than
linked. It is *not* a reason against linking llama.cpp, and the difference is the point: the natives
publish beside the binary, so linking the binding grew it 21.84 MB → 22.25 MB and the hook went p50
9.44 ms → 9.51 ms, which is noise. Size costs latency when it is *in* the executable; a 5.7 MB
dylib nothing has loaded costs nothing (D45).

**The queue is folded, never pruned.** `file-touched` writes and never reads, so nothing deleted
spool files and the queue only grew — 1102 entries before `SpoolCompactor` existed. It removes only
entries a later one makes redundant, which loses nothing rather than losing a little: a consumer
re-reads the file's current content, so three touches of one path carry exactly what the newest one
does. Pruning by age would discard a path that is still dirty. Per path keep the **newest** (it means
last touched); for entries with no path keep the **oldest**, because a bare timestamp is only ever a
watermark and the earlier one is the safe reading. It **only deletes** — never renames, never
rewrites — and that, not a lock, is why a compaction, a consumer's read, and a `file-touched` can
run at once. Surviving names still lead with `DateTime.Ticks`, so a name-ordered read stays
chronological; a compactor that rewrote entries into one file would pass every other test. Unreadable is not
unparseable: bytes that could not be obtained are left alone, because deleting on a transient
`FileShare.None` collision destroys a good edit. Session start's detached child runs it with
`--if-large`, in the same fork as `backup take --if-due` — `MaintenanceLauncher` owns both, and a
bound that depends on someone typing the command is not a bound (D41).

**Anything destructive is dry-run first.** `repair`, `compact`, `forget`, `backup prune`,
`backup restore`, `backup replay`, `queue compact`, and `uninstall.sh` print what they would do and
require an explicit flag to act. `install.sh` is the deliberate exception and the boundary is the
word *destructive*: every verb on that list removes or rewrites something already there, while the
installer only adds, and running an installer is already the request to install. It acts by default
and `--dry-run` is the brake; `--apply` is still parsed and ignored so existing invocations keep
working. Two end-to-end guards hold the pair, and the no-flag one is the load-bearing half — the
plan is that a default needing a flag is not a default (D49). The exception is *this script*, not
installers as a category: `install.ps1` keeps `-Apply` until someone can run it on Windows once,
because shipping an acts-by-default script nobody has executed is the change that should not go in
blind.

**An optional installer step that fails does not discard a finished install.** `install.sh` runs
under `set -e`, so a non-zero command aborts it where it stands — and `--with-plugin` is near the
end, after the binary, the PATH entry and the home are all durable, and *before* the MCP permission
grant. A failing `claude plugin install` therefore used to skip a step that has nothing to do with
the plugin and swallow the summary that tells a person what happened. Optional steps run inside an
`if` condition, which is exempt from `set -e`, and report through a tri-state the summary reads;
the exit code stays 0 because the installation that was asked for did happen. This was a real
defect, found by writing the test that had never existed for that flag.
Anything editing a user's file backs it up first and refuses to overwrite a value it did not
create. For `config.toml` that means `ConfigEditor`, which changes one line and leaves the rest of
the file — the prose in there explains the choices, and a TOML round-trip would delete it. A value
Engram wrote carries `# written by engram` on its own line, because comparing against the shipped
default alone makes the second run refuse the first run's edit (D33). Leaving the rest of the file
also means **a key Engram retires stays in that file forever**, reading exactly like a live setting:
`model_path`, `threads` and `idle_unload_minutes` were real until the embedder moved inside the
server, and `model_path` still looks like it picks the weights when `EmbeddingModels` has picked
them since. So `EmbeddingSettings.Retired` names them and `doctor` warns. It is an explicit list,
never "anything absent from the shipped default": `ConfigFile` is lenient about unknown keys on
purpose — that is how a config survives a version bump and how someone leaves themselves a note —
and reporting those would call a user's own choice a fault, which D37 says is how people learn to
stop reading `doctor`. They ride `Ignored`, not `Problems`, and that split is load-bearing:
`Problems` clears `IsUsable`, so folding them in would switch off the vector lane of every config
old enough to have one.

**`dim` is measured, not typed.** It is the only embedding setting that fails silently when wrong —
a mismatched width stores vectors that rank like noise and error nowhere — and it is not derivable
from the model name, since an endpoint may serve a quantized variant under the same label. So
`engram embed --probe` asks the endpoint, the picker asks before it asks the user, and `--dim` is
optional. `HttpEmbedder.ProbeWidthAsync` is the one caller allowed past the width assertion on the
embedding path; that assertion is load-bearing and must stay where it is (D34).

**There is one vector lane, and recall and `explain` both call it.** `VectorLane` is that lane;
neither caller may grow a private copy, because D30 makes `explain` a promise that it describes the
ranker which actually runs, and two implementations diverge the first time one is tuned. The
explainer must also run it *before* the fusion, not after — a lane reported too late to affect the
result is the same defect. Recall can never fail because this lane failed: every stop returns a
reason and an empty ranking, so a dead provider costs vector hits and nothing else. The lane's own
`VectorExtension.Load` is not redundant with `EngramDatabase.Open` — Open loads and discards the
result on purpose, and the lane needs that state to tell "sqlite-vec is not installed" from "no
index in this store", which are different problems with different fixes (D36).

**Nothing loads a model from `EmbedderFactory`.** `provider = "local"` loads a GGUF into this
process through LLamaSharp, so the factory's local case *attaches* to a `LocalRuntime` and never
loads. The reason is that creating an embedder is unowned everywhere it happens:
`RetrievalExplainer` calls the factory purely to ask whether a vector lane exists and drops the
result, and no caller disposes what it gets. A factory that loaded would turn a readiness check into
several hundred resident megabytes, per recall. Loading belongs to whoever can also release it — the
MCP server holds one as a container singleton, `explain` builds and disposes its own. `LocalRuntime`
holds exactly one model at a time behind a lock, and the embedder it hands out owns nothing, so
dropping one is free and disposing the runtime is what frees the weights (D35, D45).

**llama.cpp is linked, not launched, and not fetched at runtime.** It arrives as
`LLamaSharp.Backend.Cpu` — which is the *Metal* backend on osx-arm64, misleading name and all —
resolved for the target RID at restore. `-p:EngramGpu=cuda12` swaps it; exactly one backend may be
referenced, since two would publish two `libllama` for one RID. `Directory.Build.targets` trims
foreign RIDs, because the package copies all seven platforms whenever it cannot see a
`RuntimeIdentifier` and the SDK does not pass one to a RID-agnostic project reference: 210 MB of
publish output before the fix, 121 MB after, of which 5.7 MB is llama.cpp.

**`LlamaNative.Prepare` routes the log and touches nothing else.** Do not add library-path
configuration to it. IL3000 says LLamaSharp resolves natives through `Assembly.Location`, which is
empty under Native AOT, and the obvious response — compute `runtimes/<rid>/native/` from
`AppContext.BaseDirectory` and pass it to `WithLibrary` — was written, shipped into a publish, and
then measured against its own absence: the published binary embeds 45 facts with **no** path
configuration at all. The warning is real; the failure it predicts is not. Worse, stating a path
replaces LLamaSharp's selecting policy, which is choosing between builds that are not
interchangeable — on linux-x64 the CPU package ships only `native/{noavx,avx,avx2,avx512}/` with
nothing at the top and CUDA adds `native/cuda12/`, so any search short enough to write by hand picks
by sort order and `avx` sorts first. The version that looked correct on a Mac would have silently
run the weakest CPU build on a CUDA machine. The log callback is process-wide and one-shot, so
`Prepare` must still run before the first load; it is captured rather than discarded because when a
GGUF will not load the managed exception says little and llama.cpp's log says exactly what is wrong
(D45).

**`Prepare` holds its lock across the registration, not just the flag.** LLamaSharp refuses
configuration once anything is loaded, and the flag is what every other caller waits on — release it
early and a second thread sees `prepared`, goes straight to `LoadFromFile`, and loads the library
out from under the first, which then throws. Two `LocalRuntime` instances have two locks of their
own and order nothing. Measured, and it is not a rare interleaving: with the lock released early the
integration tests fail **8 runs out of 8** and pass 8 of 8 with it held. A load that fails to parse
still loads the native library, so this needs no weights and no GPU — it is the shape of bug that
reaches CI first (D45).

**`NoWarn` in `Engram.Cli.csproj` covers IL2026/IL3050, so `NoReflectionJsonTests` exists.** Three
unreachable reflection-JSON warnings come from LLamaSharp's `ModelParams` converters, and `NoWarn`
cannot name an assembly — silencing theirs silences ours, and the AOT publish was what enforced "no
reflection-based serialization" for free. The test asserts every `JsonSerializer` call in `src/`
names a source-generated context. Do not widen that `NoWarn` further without replacing what it
removes (D45).

**Pooling is the third knob that fails silently, and the tests bound what they prove.** `dim` is one
(D34), the embedding space is another (D18), and `EmbeddingModel.Pooling` is the third: the wrong
value returns a correctly-shaped vector from the correct model that encodes something else.
Measured on MiniLM — cos(mean, last) = 0.76, cos(mean, cls) = 0.50. Do not read the paraphrase test
as a pooling guard: flipping MiniLM to `Last` and re-running it leaves it **passing**, because a
degraded embedding still sorts a paraphrase above an unrelated sentence. What the suite holds is
that the setting reaches llama.cpp. Each row's value is an argument from the model's architecture,
never measured against a retrieval benchmark (D45).

**The store gets a snapshot before anything rewrites it.** `VACUUM INTO`, never `cp` — a WAL
database copied with `cp` was measured here to yield not a stale file but an unusable one, with no
`fact` table at all because everything was still in the log. Migrations snapshot unconditionally,
because a migration is the only thing Engram's own code does that rewrites structure rather than
appending to it, and it runs unattended on open (D31). Session start spawns `backup take --if-due`
detached, and the snapshot is skipped entirely unless the fingerprint of authored truth actually
moved, so an idle day costs nothing.

**A detached child must not inherit the hook's stdout, and the redirection has to be on the shell
rather than on the group.** `MaintenanceLauncher` runs several jobs, so it cannot `exec` them the
way `ServerLauncher` execs its one; the adaptation wrote `{ … } >/dev/null 2>&1`, which replaces
the *group's* descriptors and leaves `/bin/sh` holding whatever it inherited for as long as the
slowest job runs. Every job's output really was discarded, so it read as correct. But a pipe
reaches EOF only when its last writer closes, and **Claude Code reads this hook's stdout to receive
the primer** — so every session start waited on `backup take`, `queue compact`, `repair --tokens`
and `index --drain`, which is the whole of what detaching exists to avoid. `exec` with no command,
before anything else, replaces the shell's own descriptors. Measured on the published binary as the
difference between timing the hook through a pipe and through a file: **+76.6 ms at 5,308 live
facts and +44.0 ms at 50,097**, against **+0.4 ms** for `subagent-start`, which forks nothing —
and −0.2 / −0.1 ms once fixed. `MaintenanceLauncherTests` asserts the redirection precedes the
first job; restore the group form and exactly one test reddens, while the test that merely checks
all three descriptors appear stays green, which is why the placement assertion is the load-bearing
half.

**That leak also invalidated a measurement, and the shape of the error is the lesson.** A
before/after pair had shown `session-start` going 148.9 → 92.5 ms at 50,097 when
`MaintenanceLauncher.Spawn` moved above the primer's read, with the saving growing with the corpus
— read at the time as `fork(2)` copying the parent's page tables. It was the pipe: spawning earlier
started the child earlier, so the timer stopped earlier, and the "saving" tracked the corpus
because the parent's read is what delayed the spawn. **A timer that stops at EOF measures every
process holding the pipe, not the one you launched.** So do not cite a hook latency measured
through a pipe against a hook that spawns, and prefer alternating a *file*-timed arm when one is
available. The spawn itself costs 1.6–3.4 ms, which is what D28 recorded for it before any of
this. The ordering was kept anyway — a fork is never dearer for happening while the parent is
small — but it is no longer justified by a number, and the telemetry-collision measurement it
forced (zero lost, zero torn across 160 session starts, because every caller but `file-touched`
passes a 500 ms retry budget) stands on its own and still applies.

**A snapshot restores; the journal survives.** `backups/facts.jsonl` is every fact in plain text,
rewritten whole and atomically alongside each snapshot. A `.db` snapshot only restores into the
schema version that wrote it — the journal is addressed by path and predicate, so it replays into
any later one (D32). `backup replay` is additive and idempotent, matching on subject, predicate,
body and `valid_from`: it never rewrites or closes a fact the target store already had, because a
recovery tool that can retire live beliefs is worse than the loss it was called to fix. **What it
therefore cannot write, it skips and counts — it does not abort.** `ux_fact_live` allows one live
fact per subject and predicate, so a journalled belief the target disagrees with cannot go in
without closing the target's, which the previous sentence forbids; the insert used to violate the
index and take the whole replay down, so a journal replayed into any store that had been through
`init` — which arrives seeded — recovered **nothing**. Conflicts are counted apart from
`AlreadyPresent`, because "already there" and "not recovered" are the two answers a recovery tool
exists to tell apart, and a conflicted fact gets no `idMap` entry so a supersession aimed at it
comes out unresolved rather than pointed at some other row. Only *live* facts collide — the index
is partial on `valid_to IS NULL` — so a closed one lands beside whatever is believed now. The
`claimed` set is **for the dry run only**, and the test that says so had to be rewritten to prove
it: an apply sees its own inserts through the transaction and resolves an in-journal duplicate
without help, so the first version passed with the set deleted.

**`doctor` reads; it may not repair.** It opens the store with `EngramDatabase.Open`, never
`OpenInitialized` — the latter migrates on open and D31 makes that migration snapshot first, which
would make the most useful thing it can say, *your store is a schema behind*, unsayable: asking
would perform the answer. The same rule at the other end — `provider = "local"` is checked by
looking for the weights on disk, never by resolving an embedder, because resolving one loads them
(D35, D45). Both are guarded: an end-to-end test snapshots every file in the home by size and mtime
around a run and asserts nothing moved, and an integration test chmods the GGUF to `UnixFileMode.None`
and asserts the row still reports Ok — a check that opened the file could not.
Only `Broken` sets exit 1 — `Off` is a supported configuration, not a fault, and a doctor that
reported red for a choice the user made is one people stop reading. Every check runs inside a
wrapper that turns a throwing check into one broken row, because the state most likely to make a
check throw is the state someone is running doctor in (D37).

**The Metal tensor path is recorded by whoever loaded, never inferred by whoever asks.** ggml-metal
compiles its shaders at runtime and takes their language version from the SDK stamped in the *main
executable*, so the capability belongs to the process that loaded llama.cpp rather than to the binary
running `doctor`. Measured as a controlled pair on one M5 Pro — same weights, same
`libggml-metal.dylib`, same machine: `out/engram` at `sdk 26.5` records `has tensor = true`, and the
`dotnet` host at `sdk 15.5` records `false`. The half-speed path is therefore live, and it is what
`dotnet run` and `dotnet test` get rather than anything a user gets, which is a second instance of
the rule tier 3 already encodes. So `LocalRuntime` writes `metal.json` after a load and doctor only
reads it: inferring from doctor's own SDK field would answer for the wrong process — two binaries
legitimately serve one home (D42) — and would copy ggml's gating policy into a second implementation
that drifts on upgrade (D36). Three details cost real time and none are guessable. llama.cpp prints
**`has tensor`, with a space**, so grepping the plan's `has_tensor` finds nothing and proves nothing.
**`GPU name:` is not the GPU name** — it answers `MTL0`, a device index; the hardware is only on
`ggml_metal_init: picking default device:`, and the M5 gate keyed to the obvious line could never
match Apple silicon, so the warning could never fire. And the capture is **first-64-wins rather than
a ring**, because `ggml_metal_init` repeats on every context creation and a ring would evict the
capability line in a long-lived server. The warning is gated on the recorded device name, not on the
capability alone: a GPU with no tensor cores reports the API disabled too, and reporting that as a
fault would red an M2 for hardware it never had (D28).

**A rebuild waits for the server to stop.** `EmbeddingBacklog` is the one owner of vector
production, and a running server holds the embedder it built at *its* startup — so
`embed --rebuild` refuses while it is up rather than racing it. Losing that race is not a slow
rebuild, it is a wrong one: the server's `EnsureCreated` re-pins the recreated table to the space
the user just moved away from. Which half of the rebuild runs is likewise not a preference —
`Clear` keeps the table, `Drop` removes it and the space it pinned, and the plan picks by reading
that pin, because a same-width model swap is invisible to everything else (`vec0` checks width,
not provenance). Rebuilding derived state needs no snapshot, unlike a migration (D31): by D8 it
recomputes from `fact` and can destroy nothing authored (D38).

**A running server is identified by its start time, never by where it was launched from.** pid plus
the kernel's start time for that pid is unique, and it is exactly what a recycled pid cannot forge.
Adding the executable path to that answers a different question — *was this launched from the same
file I am?* — and two engram binaries legitimately serve one home, which is what every session
working on this repo looks like. Measured here: the installed binary reported the server up while a
freshly built one called the same pid file dead, in the same second. `stop` was the real damage — it
deleted the pid file, said "not running", and left the server running with nothing left to address
it by. The path is reported, never enforced: `StatusResult.LaunchedFrom` carries it and `status` and
`doctor` print it only when it differs from the binary being asked. Nothing is terminated whose
start time does not match what was recorded — that guarantee never rested on the path (D42).

**"Start time" means the kernel's start token, and Linux is where that distinction turned out to
matter.** `Process.StartTime` there is `starttime` added to a *per-process estimate* of boot time, so
it describes the process asking as much as the one asked about: measured in a container, 24 of 24
cross-process reads disagreed, by up to 3636 ticks. Exact equality never held, so every Linux
`status` answered `Reused` about a healthy server and `stop` did the damage above on every
invocation rather than in the rare case — all three Linux end-to-end failures in CI were this one
bug. `ProcessStartToken` is now the only thing that produces identity: `/proc/<pid>/stat` field 22
plus the boot id on Linux, the exact kernel start time on macOS and Windows, which keep the code path
they already had — their kernels store an absolute creation time, and a fix that does not touch the
platform this repo cannot test cannot regress it. Self-view and by-pid view come from that one type,
because written separately they are two implementations of one comparison and the first divergence is
a server reporting itself dead. **No tolerance may be added, and no comparison may convert between
token and wall clock** — the conversion *is* `bootTime + starttime`, which is the defect. The skew is
not jitter but the difference of two clock readings, so a clock step moves it without bound and every
window is either too small (trading a deterministic failure for an intermittent one) or fitted to
hoped-for clock behaviour. Nothing softens a wrong answer here either: `Stop` never runs the health
check at all, and `Start` terminates precisely *when* the health check failed to vouch, so
`IsAnsweringForUs` does no work on any kill path. Records written before tokens existed still compare
`StartTimeUtc` exactly; that path is legacy, not a fallback, and giving it a window would put a
number in the kill path permanently for a population that is empty (D42).

**Ask `ServerIsAlive`, never `Kind is Running`.** `Wedged` and `VersionMismatch` are both live
processes holding whatever they loaded at startup, so any caller deciding whether it may act alone —
`embed --rebuild`, by D38 — has to count them. Enumerating states at the call site is how one caller
ends up racing a server it decided was absent. A version gap is also not a hang: it gets its own
state and doctor warns rather than reporting `Broken`, because nothing is wrong with a server that
answers correctly from the build before this one (D37, D42).

**The probe's two session counts do not subtract.** `session-start` carries Claude Code's session
id; `session-open` carries the transport's `Mcp-Session-Id`. Measured on a real instance: 23 of the
first, 9 of the second, no value in both. Disjoint id spaces, so the difference counts nothing —
subtracting them once produced "N session(s) ran without Engram's MCP server reachable; memory was
unavailable", printed for every session in which the model simply never asked. `McpSessionId` is
`AddTransient` and injected only into the four tool methods, so `session-open` is written on the
first *tool call*, never on connection: these counts move with use and never with uptime, and
nothing Engram records observes reachability at all. The one comparison that survives is zero MCP
sessions against a non-zero hook count, which needs no correspondence between the spaces. The
consequence to know before trusting an adoption number: a tool call cannot be attributed to the
Claude Code session that caused it, so "what fraction of sessions used memory" — what D18 gates M4
on — is not computable today, and the percentages are over MCP sessions, a population that by
construction called a tool (D43).

**`coverage` counts lane agreement, not rows.** The spec always said so — "computed from lane
agreement and score mass" — and the code counted candidates until it was measured: `weekend saturday
personal activity outing` returned seven, six of them engineering notes bm25 reached through a shared
stem, and the count called it `high`. `high` is the value that suppresses the `gaps:` line, so a
result that was 86% noise told the model memory had the question covered and the discover-then-remember
loop never fired. Corroboration separates cleanly on every query this instance has recorded — 8, 7, 8
for the ones that worked against 1, 1, 1, 1 for the ones that did not — so the `3+` boundary is kept
rather than fitted. `none` stays keyed to the total: it means the store said nothing and selects a
different response shape, and returning facts beneath it would be worse than the bug being fixed.
Score mass, the spec's other input, is still open on purpose — one unmeasured knob is a rule, two are
a preference. `Corroborated` is public because its `> 1` is the whole rule and `Pack` cannot reach it
from a unit test (`CannedFact` has no numeric id, so lexical ranks need a real store) (D44).

**A primer record says what it delivered, and `fact_count` stays null.** `session-start` and
`subagent-start` write `long_term_fact_count` and `tokens_returned`; they must not write
`fact_count`, which on a `recall` record means facts returned to the model and on a primer means
nothing — a primer returns a count line and up to two example bodies. A nearby number in that field
is how D43 happened. Before this, 54 session-start and 336 subagent-start records carried every
memory field null, leaving `recall` — 7 events, opt-in, on one day — as the only visible read path,
which understates delivery by construction because the primer reaches every session whether or not a
tool is called. Recording it does not make D6's or D18's gate *met*; it makes them answerable going
forward, and nothing retroactive is recoverable. Two end-to-end tests hold it; the null one is the
load-bearing half (D46).

**The M4 gate is unmet, and the number that looks like it isn't.** 28.6% of recalls returning
`coverage: none` reads as a paraphrase-miss rate. Both of them fired ~82 minutes *before* the fact
that answers them was written — cold start, not retrieval failure. No recorded query has yet missed
a fact that existed when it was asked, so D18 still gates M4 shut. Check `valid_from` against the
telemetry timestamp before reading any miss as a retrieval failure (D44).

**Where memory lives is claimed in two channels, and neither may take the other's job.** An agent
usually arrives carrying a second memory system described somewhere Engram cannot see, in
instructions that are longer and fire on the literal words *remember this*. Engram made no competing
claim at all until D51, and the only place it ever stated the write rule was the *subagent* primer —
which reads as extending a baseline that was never established. The fix is split by whether something
is a preference. `engram_remember`'s description opens on durability and names the trigger; that is
unconditional, ships to everyone, and must keep both properties, because a rule with no trigger loses
to one that has a trigger regardless of which is more correct. Whether another store is *subordinate*
is a preference — those files are the user's — so it is `[memory] precedence` and rides the primer.
Do not migrate either way: a `[Description]` is a compile-time constant and cannot vary per install,
and the primer is ordinary context that decays between compactions, so the trigger cannot live there
either. `SessionStart` matches `startup|resume|clear|compact`, so the line is re-injected wherever
context was reset; `BuildForSubagent` repeats it rather than assuming the parent's, since
`SessionStart` never fires for a subagent. Three details are load-bearing: the line goes **first**,
because `TryAppendLine` drops what overruns the budget and this is the only line whose absence changes
behaviour; an empty store therefore emits a primer unless precedence is `off`, since a store with
nothing in it is exactly the session where the other system wins uncontested; and the D15 guard
forbidding tool names in primer guidance carries **one** exemption, subtracted by exact string rather
than by pattern, so every other way guidance could drift back still fails. Nothing about the model's
actual preference is measured yet — the channels exist, adoption is a D18/D43 question (D51).

**A menu may not emit a row it cannot count.** `\x1b[{n}A` and `\x1b[2K` both count *physical* rows,
so a line the terminal wraps costs rows the redraw never moves back over. `Tui.Render` therefore
clips every line to the width, gives the detail block a fixed height, and returns the count it
actually wrote for the caller to feed back as `previousRows` — one logical line, one row, always.
That was the whole reported bug and all three of its symptoms: the model menu's entries ran ~290
characters against a redraw of one row per choice, so at 80 columns each took four rows, the menu
marched down the screen, one row in four got cleared, and the visible `❯` sat on a stale copy while
the real index moved on — "the options repeat, the text is not formatted well, and it selected one I
did not pick" is one assumption, not three bugs. Clip the **head first** and the description against
what is left: on a narrow terminal the label overflows on its own, which the first fix missed. Keep
the specs and the tradeoff prose in separate fields — clipping means concatenating them no longer
corrupts the screen, it silently ellipses the specs instead, which is why
`ModelMenu_SpecsFitBesideTheLabel_WithoutBeingEllipsed` exists as well as the width assertion. A test
that builds its own `TuiChoice` list proves nothing about the picker; draw `EmbeddingSetup.ModelChoices()`
itself, or the falsification passes with the defect restored. One column is left unwritten because
terminals disagree about whether the last cell wraps now or later, and that difference is a row this
cannot see (D52).

**A scan is bounded, and absence is only evidence when it finished.** `RepoScanner.Scan` takes a
`ScanBudget` and reports which bound stopped it. Both exist and they are not one rule twice: the
clock covers the whole scan, while the file ceiling covers only the walk — a tree of a million empty
directories runs forever under a ceiling, because the collected list never grows, and the ceiling is
deliberately kept off the git path since a monorepo listing 150,000 files is completely enumerated
and calling it partial would disable its deletions for good. Measured, and the numbers are why the
bound is not a preference: `engram doctor` from a home directory printed nothing, held 100% of a core
and **7.8 GB resident** at 106 seconds, and had to be killed. Outside a checkout `Scan` falls through
to `Walk`, which had no budget at all; the configured globs (`bin`, `obj`, `node_modules`, `.git`)
describe none of `~/Library`, a package cache or a downloads folder, so adding patterns was never the
fix — a plain `find` counted 1,318,043 files there in 20 seconds without finishing, against 289 via
`git ls-files` and 4,318 unpruned for a real repository. The **bound on its own would have been the
worse bug**: `CodeIndexer` derives deletions from every indexed file absent from the scan, so a
truncated one reads as a repository whose files were all removed, and a slow scan would have become a
destructive one. Nothing may treat a partial scan as complete — the indexer skips deletions and says
so, and `doctor` warns rather than answering a home directory with `engram index --apply`, which is an
instruction to index the thing that could not be walked (`Warn`, never `Broken`, so D37's exit code is
intact). Bounding only the enumeration is half a fix and publishing it is what showed that: the walk
stopped at its ceiling in two seconds and classification then spent six more reading the head of
100,000 candidates. One clock covers both halves, and its check sits **on the first candidate** rather
than in a separate pre-check, because two checks against one clock cannot be told apart by a test —
whichever fires first answers for both, and the other can be deleted with the suite still green.
2.0 s and 258 MB after, unchanged at 0.00 s inside a checkout where git answers (D53).

**`embed --status` takes its counts from the store and everything else from the note.** The database
is the authority on how many facts are embedded and how many wait, and it is right whether or not a
server is up; what no reader can derive is whether a loop is alive, how fast it is going, what it is
working on, or why it never started. Those exist only in the server process, so it writes
`embedding.json` and everything else reads — the `metal.json` shape (D42), for the same reason.
Counts are deliberately not duplicated into it: a second answer goes stale exactly when someone is
most likely to be reading it, and after `stop` the measured store still says 208 of 873 while the
file is correctly gone. Two rules the live run paid for and no test would have. **The reason a number
is not moving is the answer** — a server was up with 873 pending and status said `not running — start
the server with 'engram start'`, advice to do what had already been done, while the one process that
knew (`qwen3-embedding-0.6b is not downloaded yet`) had written it to a log nobody asking that
question opens; so a service that declines records why. And **a standing statement is not a
heartbeat**: `Unavailable` is excluded from `LooksLive` outright, or a precise reason ages into
`stalled or stopped` after forty-five seconds, which is worse than what it replaced. The note is
cleared on `ApplicationStopping` beside the pid file and with the same ownership test, because a
backlog that declined never enters `RunAsync` and so never reaches the loop's own cleanup. The
backlog was **never silent** — it had logged `Embedded N fact(s)` since it was built and
`SetMinimumLevel(LogLevel.Warning)` dropped every line, so the fix is one `AddFilter`, not a second
logging path beside the one already there. Publishing is **per committed batch**, since a pass is
eight batches and one was measured at 28 seconds. `--watch` redraws through `Tui.Frame`, which
inherits D52's row budget entire; the bar is a terminal decoration and a pipe gets key-and-value
lines, because that output is what a script and an agent parse (D54).

**Memory is timestamped to the second and must be read back at that resolution, in the reader's
zone — the render stops where the data does.** It stopped at the minute first, and the case that
showed that was wrong is the one the read path exists to serve: a superseded preference at 00:02:11
and its replacement at 00:02:20 rendered identically, so the chain showed *that* one belief closed
another without showing which came first. Any unit coarser than the stored one has to be re-argued
the next time two facts land inside it, which is the same bug the day format had.
`valid_from` and `created_at` are unix seconds, but the read path rendered `yyyy-MM-dd` in UTC —
two defects in one line, both silent. The model could report which *day* a memory was made and never
what time, so every fact from one working session was mutually unordered on screen, and the analysis
behind D44 — that two `coverage: none` recalls fired 82 minutes *before* the fact answering them was
written — was not performable from tool output at all; it needed the store, because the read path had
discarded exactly the resolution that decides it. The UTC half was worse because nothing shows it:
west of Greenwich every fact recorded after mid-afternoon rendered with *tomorrow's* date, against an
agent whose context states today's date locally. `MomentText` is the one renderer, it takes a
`TimeZoneInfo` so the boundary is testable rather than asserted, and six characters a fact is the
whole cost.

**A handle that leads somewhere has to say so, because the fact on the line cannot.** Recall returns
live beliefs, so one that replaced another and one held all along arrive as the same line; the earlier
version is reachable only through `engram_expand … history`, which needs the right handle to be given
it. Measured on this instance: `favorite color` returns two live facts, both saying green — one a
single version, one heading a thread whose previous entry says orange. Expanding the wrong one reports
`1 version`, which reads exactly like *never revised*, and nothing separated them except that the right
one's body happened to mention the old value. That is luck, and it fails silently in the direction of
"there is no history here". So `CannedFact.Versions` carries the thread length and the recall line
gains `· v2` when it exceeds one. `FactStore.VersionCounts` groups on `e.path` and `f.predicate` —
**not** `subject_id`, which is the indexed and otherwise more natural key — because this number's only
job is to advertise `History`, and `History` addresses a thread by path; counting by a different key
than the call being advertised is how a marker comes to promise two versions and the expand it invited
returns one. One query for the whole catalog rather than one per fact: recall packs a handful but ranks
every live belief, so a per-fact lookup would put a round trip behind each of them. Threads of one
version are omitted from the result, which keeps it small, and a fact whose thread is unknown reads as
one — a count nobody looked up must not be advertised as a revision. Marking everything is exactly as
useless as marking nothing, so the unrevised case is the half worth guarding. **Only the long-term
formatter carries the marker, and the asymmetry is addressing rather than an omission**: a session
note's path ends in a fingerprint *of its own statement*, so rewording one addresses a different path
and starts its own history instead of extending the old note's — there is nothing earlier for a marker
to point at. Do not "fix" that from the cheap reading, which is false: retract a note and restate it
verbatim and the path does collect two rows, because `Append` returns an existing id only for a *live*
match and `Forget` closes rather than deletes. That thread holds one sentence twice — matching the path
is what forced the text to be identical — so the marker would announce history saying nothing the line
already does. Both halves are pinned by tests in `SessionFactsTests`, because the property lives in the
addressing and is invisible at the formatter (D57).

**Recall pays for the match set, not for the corpus — and `explain` must pay for neither. The first
clause was the exact opposite until D60, and the rest of this paragraph is the reasoning that got
there, so read it as history with live rules embedded rather than as current measurements.** The
spec's p50-under-50 ms lexical target had never been measured until D58: against the object ranker
it was met at 5,097 live facts (16–21 ms once the published binary's ~8 ms process start is
subtracted, ~24–29 ms on the wall clock) and missed at 50,097 (127 ms, ~135 ms wall), and missed by
the **floor**, since a query matching nothing
cost what one matching everything did. Re-measured on the published binary after D59's index and
D60's cutover, floor subtracted: **2.5 ms at 50,097 for an ordinary query** (14.4 ms wall) against
125.9 ms for a term matching 45,132 of them. The floor is gone, the target is met at 50k, and what
is left is proportional to matches. So indexes are not the bottleneck and index tuning is not
the fix — warm SQL is ~3 ms of an ~18 ms pipeline, every plan is sane, and `ReadLive`'s full scan is
inherent because recall wants every live fact and `ORDER BY f.id` is then free. The trap that cost
the most time: FTS match count does not predict cost (`index` matches 45,119 rows and is fast,
`latency` 45,001 and looked 8x slower), and that 8x turned out to be **explain-only overhead**. D30
makes `explain` the measurable proxy for the ranker, which is exactly what makes this easy to get
wrong, so split `Pack` from `explain` before concluding anything from a number measured through the
command. That overhead was **not merely slow**: bound one parameter per candidate and
`engram explain latency` on the 50,097-fact store dies with `SQLite Error 1: 'too many SQL variables'`
at 45,001 candidates against a ceiling of 32,766 — measured as a controlled pair of binaries built
either side of the fix, so the 1,220 ms once recorded for a hot term is time to crash, not time to
answer. `RetrievalExplainer.ReadTiers` is bounded twice and the two are not one rule written twice:
it reads only as far as the caller renders, via a **required** `displayLimit` — defaulting it to
`int.MaxValue` reads as "no opinion" and is precisely the unbounded read — *and* it chunks at 500 ids
regardless, because `--limit` is a number a user types and a bound a caller can raise is not a bound.
Measured at 20,000 candidates: unbounded and unchunked 12.9x the no-match arm, unbounded but chunked
1.89x, both together 1.3x — so chunking, specified as the correctness half, carries most of the
latency too. **The tier-3 guard that held that pair has been deleted, and the reason matters more
than the test did.** `ExplainCandidateScalingTests` seeded 20,000 facts sharing a token and asserted
the hot arm within 3x of a no-match arm; D60 then capped the candidate set at `seed_k` per lane, so
the hot arm became 32 candidates and both arms collapsed onto the shared floor. The chunking could
have been deleted with that test still green. It did not rot silently — it carried an explicit
`of 20,000 candidates returned` assertion for exactly this, and that is the line that failed — but
once its premise is gone the honest move is to remove it rather than retune a ratio that can no
longer separate anything. What still holds the display bound is
`RetrievalExplainerTests.Explain_ReadsTheProvenanceTierOnlyAsFarAsTheCallerWillPrint`, deterministically
and without a clock. **The 500-id chunking is now unguarded**, and knowingly: past D60 it is
reachable only by setting `seed_k` above 32,766 in config, which nothing validates and nobody has
done, and the test that would prove it needs a 40,000-fact corpus to defend a configuration that
does not exist. Restore a guard before raising `seed_k`, or bound it. Recall's own change is a
move and not a redesign: `BuildCandidates` formats a line **after** the lane check rather than for
every live fact, carrying the source record — a reference copy, where a `Func<string>` per entry
would swap one allocation for another on the same O(corpus) path. Bounded materialization of the
candidate set is designed and **deferred**, and is now the only remaining item — retargeted, because
the floor it was priced against no longer exists: what it addresses is the one case still above the
spec target, 125.9 ms for a term matching 45,132 of 50,097 facts, which is D44's coverage counts
computed over the whole scored set. An ordinary query at that size is 2.5 ms and needs nothing.
**The floor work itself is done.** The design named next in this paragraph — an **inverted
literal-token index** (token → fact) over subject name plus body, merged into the same integer
overlap score, picked because it is equivalence-testable — is `fact_token` (D59), and the cutover
that reads it is D60; equivalence is how it was accepted, at 764 queries and 5.8M candidate
comparisons with zero divergences outside §2.5. The tripwire that would have gated it (15,000 live
facts, or a measured p50 above 40 ms, against then's 5,097) is therefore **void rather than unmet**,
and its reasoning stands for the next such bound: fact growth here is a step function, since one
`engram index --apply` can consume the whole 5,097-to-17,000 headroom in a single command, while the
fix carries schema-migration lead time — and a tripwire crossable faster than its fix can ship is
not a bound.
Precomputed token *sets* were rejected as still O(corpus) (loading and intersecting 50k sets per
recall), and restricting the overlap lane to what the indexed lanes already found was rejected
because it corrupts D44: a lane that only scores what FTS returned cannot independently corroborate
it, so "2+ lanes agree" decays toward tautology and coverage inflates in the direction that looks
like success. One number is **not** settled and must be re-measured on a Release build before that
work apportions effort: the 84 ms catalog read against tokenization's 59 ms came from a Debug/JIT
test host and cannot be reconciled with the published binary's 127 ms whole pipeline (127 − 59 − 19
leaves ~49 ms, with no room for an 84 ms read), so do not cite it as the larger half. An earlier
217 ms reading for it was one cold JIT sample and is withdrawn outright. What is settled:
`TokenEstimator.Estimate` is `Math.Ceiling(text.Length / 3.6)` — arithmetic on a length, never a
tokenization, which is why deferring it was rejected (D58).

**`ix_fact_thread` looks redundant with `ux_fact_live` and is not — deleting it costs 93% of every
recall.** Both index `fact(subject_id, predicate)`; `ux_fact_live` is partial on `valid_to IS NULL`,
and the query that needs the other one counts a thread's *whole* history, closed rows included,
which is what makes it a version count rather than a live-fact count (D57). A partial index cannot
answer outside its predicate, so without the second one SQLite full-scans `fact` once per returned
candidate. Measured with the subquery patched out: 1,545 ms → 105 ms at 50,097 live facts for a term
matching 45,132, and 31.8 ms → 1.0 ms at 5,308 — the cost is *candidates × corpus*, so it was never
a large-store problem, only invisible at 5k. Two process lessons came with it, both cheap and both
paid for. `SCAN f2` was found during the cutover, investigated, and correctly escalated rather than
decided — but ranked low, because **a plan is not a clock**: `EXPLAIN QUERY PLAN` could show the
scan and could not show that it was 99% of the statement, so pair a plan finding with a timing
before deciding it can wait. And a migration whose DDL is conditional needs a fixture genuinely
missing it: `WriteVersion1Store` rolls a *current*-schema store back, so `CREATE INDEX IF NOT
EXISTS` no-opped and a deliberately wrong migration left 18 of 18 green until the test dropped the
index first (D60).

**Recall says when a lane did not run, and that note is keyed to lane state, never to hit count.**
With one lane the corroboration term degenerates to `(rank IS NOT NULL) > 1` — false for every row —
so coverage cannot reach `high` and an overlap-only fact is absent entirely; the digest then reads
`coverage: none · gaps: no facts matched` about a store holding the answer, which is
indistinguishable from an empty store and ends D6's loop before it starts. So `AvailabilityNote`
rides the coverage header at *every* coverage value, and fires on `Unavailable` but never on `Off` —
D18 makes `Off` a supported configuration and D37 says a diagnostic reporting a choice as a fault is
one people stop reading. A query that legitimately matches nothing must keep saying so, with no
note. `RecallRanker.OverlapUnavailableDetail` is shared with `RetrievalExplainer`'s lane row so the
two surfaces cannot word the same state differently (D60).

**Falsify against a committed tree, and assert the patch landed.** A harness that restores arms with
`git checkout --` restores to HEAD, so an uncommitted change under test is reverted by its own
falsification and every arm goes red for the wrong reason. The sibling failure is quieter: a pattern
spelling `·` as a bare `.` matches one byte against two in UTF-8, so the break silently no-ops and
the suite stays green — a falsification reporting success while proving nothing. One `git diff
--quiet` check before trusting an arm catches both (D60).

**The webhook delivers the telemetry log; it is not a second event system.** Every kind Engram
records already lands in `telemetry.jsonl`, so `WebhookService` tails that file rather than being
notified at the point of emission — which is what makes a subscriber's live feed and a dashboard's
history the same data, parsed by the same `Telemetry.TryParse`. The body of each POST is the log
line **verbatim**, one event per request; an envelope was rejected because it adds a nesting level
every subscriber unwraps for no information and makes the live feed parse differently from the file,
which is the one property the feed exists to have. **Only the server delivers**: the producers are
hooks that must not do outbound HTTP, since `file-touched` holds a 10 ms budget and may not even
open the database (D4), and a POST costs far more than the open it is forbidden. Writing a line and
exiting keeps emission free.

**There is no cursor and no resume — the tail starts at end-of-file.** So the feed is what happens
while the server runs, which is a contract in one sentence and needs no staleness threshold; the
alternative replays a day of `file-touched` at a status-line script after any restart. Nothing is
lost, because the log is durable and timestamped: history is a read of the file, which is what a
dashboard wanting more than the tail should do anyway. **A failed delivery is dropped, never
queued**, for the same reason — delivery may not stall the tail, and the durable log recovers
anything a dashboard actually needs. Each subscriber gets **at most one failing attempt per poll**
and is then muted with a doubling backoff: a subscriber that accepts and hangs, rather than
refusing, costs the full timeout per record, and 64 records against 2 s is a two-minute poll during
which nothing else is delivered. Muting is **per URL**, so a closed dashboard cannot take a status
line down with it. The poll loop catches its own exceptions; letting one out of `ExecuteAsync` ends
the `BackgroundService` for the life of the server, so a single unreadable record would silently
stop every event after it.

Three things measured while building it, none guessable. **A reader can starve `DurableAppend` and
the loss is silent**: its writer opens `FileShare.None`, which an open reader refuses, and after the
500 ms budget it *returns* rather than throwing — so a telemetry record disappears with no error.
Relaxing the writer to `FileShare.Read` admits readers but was measured to let **two appenders both
succeed**, destroying exactly the cross-process lost-update protection that `None` is there for, so
the reader cannot be made harmless from the writer's side. In practice the tail holds the file for
microseconds twice a second against a 500 ms retry budget, so collisions retry rather than drop —
but do not add a second reader without revisiting this. **`TelemetryEventKind.All` must be checked
against the constants by reflection, not by walking itself**: the obvious test iterates `All` and
asserts each entry is accepted, which is a tautology — deleting a kind from the list means it is
simply never visited, and that version passed with the defect in place. **A `BackgroundService`
test needs a real barrier**: `StartAsync` promises only that `ExecuteAsync` was handed to the
scheduler, so events written straight after it can beat the tail to its starting mark; that failed
under load, passed in isolation, and looked exactly like a broken feature. The startup log line is
emitted after the mark is taken, so waiting for it is the guarantee (D55).

**Work with a duration reports both ends, and reports transitions rather than samples.** `index`
and `embedding` carry a `phase` — `started`, `finished`, `failed` — because without the second half
anything displaying activity has to guess how long to keep displaying it, and a guess about how long
a repository takes is not a design. The backlog emits on the idle/working edge, not per pass: a
backfill is hundreds of batches and putting each into `telemetry.jsonl` would change what that file
is, since it is what D18 and D43 read to answer how memory is used. Progress belongs in
`embedding.json`, which is maintained for that question already (D54). Neither event carries counts —
a nearby number in a field meaning something else is exactly what D43 traced a wrong conclusion back
to. Two consequences measured on the published binary, both caught only by tier 3: `session-start`
spawns the maintenance child, so an `index` run now writes into the same log during a session-start
test, and four end-to-end tests that counted *every* line of `telemetry.jsonl` broke. Those counts
were already a race — the child is detached — so they filter by kind now. Sizing matters in the
guard too: at 12 facts and `MaxBatch` 4 the backfill completes in one pass, so "once per transition"
and "once per pass" emit identically and the test passed with the guard deleted (D55).

**A kind that is declared but never emitted is a feature that reads as switched off.** `ServerStart`
and `FileTouched` were constants with zero emission sites, and `user-prompt` — the one path that
catches a fact stated in passing — had no kind at all, so the automatic capture that D51 calls
unconditional was invisible to every reader of the log. Three rules came out of filling them in.
The capture event is **its own kind, never `remember`**: D18 and D43 read `remember` to answer
whether *the model* reached for memory, and a hook-driven capture folded into it would inflate the
one number those gates turn on, in the direction that looks like success. It is recorded **after
the "was anything stored" guard**, so the event means a fact was written rather than that the user
typed — and the test for that must use a *restatement* the store already holds, because an ordinary
working prompt returns at the earlier "was anything worth capturing" guard and never reaches either
placement (measured: the obvious version passed with the guard moved). `server-start` is
**lifecycle, never a session count** — D14 retired an earlier one precisely because one-per-process
only meant "a session" under stdio, and `session-open` still owns that question; `server-stop` is
best effort twice over, since a killed process never reaches `ApplicationStopping` and on a clean
exit the webhook delivering it is shutting down beside it, so no reader may infer "still up" from
having seen no stop. A shared log also means **no test may assert a total line count**: the server
now writes its own records into it, which broke an MCP test that had been counting every line, the
same trap the session-start hook tests already documented (D56).

**A misfiled `kinds` entry narrows delivery; it may not switch it off.** Unknown kinds land in
`WebhookSettings.Unknown`, which `doctor` warns about, and never in `Problems` — `IsEnabled` is
cleared by `Problems`, so folding them in would stop delivering the kinds that *were* spelled
correctly. That is the same trap a retired key set for the vector lane (D33), and it is why a bad
*URL* is different: that one is `Broken`, because nothing degrades, delivery simply does not happen,
and someone is waiting at the other end of it. There is deliberately no `enabled` key — a
configured URL is the switch, since two ways to turn one thing off is how a setting disagrees with
itself (D55).

## Build constraints

- .NET 10, `net10.0`. Warnings are errors.
- `Engram.Cli` publishes Native AOT. No reflection-based serialization — use
  `JsonNode` for dynamic JSON and a source-generated `JsonSerializerContext` for typed
  JSON. Watch for overload traps: `JsonArray.Add(x)` binds to the AOT-hostile generic
  overload, so cast through `IList<JsonNode?>`.
- No ORM. Hand-written SQL, so query plans stay visible.
- Roslyn never links into the core binary — it runs as a sidecar (D1). llama.cpp does link, through
  LLamaSharp, but its natives publish to `runtimes/<rid>/native/` rather than into the executable
  (D45).

## Tests

Five tiers (D9). Tier 2, integration against real SQLite files, carries the bulk — this
system's risks are temporal invariants, multi-process contention, and an AOT binary
diverging from the JIT build, none of which unit tests can reach. Tier 3 drives the
published binary, because CI passing on the JIT build proves nothing about what ships.

A lint or guard test that cannot fail is worthless. When adding one, prove it fails by
breaking the thing it guards, then restore.

**A skipped tier 3 is not a pass, and the summary line will not tell you.** Every test in
`Engram.EndToEnd.Tests` opens with `Assert.SkipUnless(EndToEndBinary.Path is not null, …)`, so
without a binary the whole tier evaporates into the skip column while the summary still reads
`Passed!`. That is how this suite was reported green three times in one session with **128 of 161
tests skipped**, and how a red test survived several commits — by D9 the run whose result means
least is the one that looked cleanest. `EndToEndBinary` therefore falls back to `./out/engram`
when `ENGRAM_TEST_BINARY` is unset, so a published tree runs tier 3 on a plain `dotnet test` with
no ceremony, and `TierThreeCoverageTests` names the skip when there is nothing to drive.
**Failing on an unpublished tree was tried and reverted**: it made every inner-loop run red, and a
check people learn to route around is worth less than no check — D37's rule about `doctor`,
applied to a test. It does assert the path *exists*, which is a different job: measured, a variable
pointing at nothing does not skip, it fails 128 tests with `Win32Exception` from wherever each one
started a process, and this reduces that to one line naming the cause. Read the skip count, not
just the pass count.

## Commits

Explain why the change is right, not what the diff shows. Note anything measured, and
anything that was tried and rejected.
