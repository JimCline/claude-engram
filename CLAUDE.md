# Engram — working rules

Read `docs/engram-implementation-plan.md` before any non-trivial change. It holds
forty-six decisions (D1–D46) that resolve questions the spec left open, and each one was
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
window, or supersession row (D8).

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

**`file-touched` never opens the database.** It writes one spool file per invocation —
its own, never a shared one — and exits. Its budget is 10 ms and it must hold
unconditionally, not just when nothing else is writing. Measured on the published binary:
p50 7.82 ms, of which **+0.02 ms is the hook and the rest is process start**. Opening the
database costs **1.0–1.5 ms**, measured by A/B-ing `probe` against homes with and without an
`engram.db` — it skips the store when the file is absent, so the difference is the open. The
2.1–2.4 ms that `session-start` and `user-prompt` add over the same floor is that open *plus
each hook's own work*; charging all of it to the open, as this file previously did, overstates
it. So the rule does not rest on the arithmetic — an opening `file-touched` would still fit at
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
rewrites — and that, not a lock, is why a compaction, a `Drain`, and a `file-touched` can run at
once. Surviving names still lead with `DateTime.Ticks`, so `Drain`'s sort stays chronological; a
compactor that rewrote entries into one file would pass every other test. Unreadable is not
unparseable: bytes that could not be obtained are left alone, because deleting on a transient
`FileShare.None` collision destroys a good edit. Session start's detached child runs it with
`--if-large`, in the same fork as `backup take --if-due` — `MaintenanceLauncher` owns both, and a
bound that depends on someone typing the command is not a bound (D41).

**Anything destructive is dry-run first.** `repair`, `compact`, `forget`, `backup prune`,
`backup restore`, `backup replay`, `queue compact`, and the installer print what they would do and
require an explicit flag to act.

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
default alone makes the second run refuse the first run's edit (D33).

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
detached: +2.0 ms mean, and the snapshot is skipped entirely unless the fingerprint of authored
truth actually moved, so an idle day costs nothing.

**A snapshot restores; the journal survives.** `backups/facts.jsonl` is every fact in plain text,
rewritten whole and atomically alongside each snapshot. A `.db` snapshot only restores into the
schema version that wrote it — the journal is addressed by path and predicate, so it replays into
any later one (D32). `backup replay` is additive and idempotent, matching on subject, predicate,
body and `valid_from`: it never rewrites or closes a fact the target store already had, because a
recovery tool that can retire live beliefs is worse than the loss it was called to fix.

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

## Commits

Explain why the change is right, not what the diff shows. Note anything measured, and
anything that was tried and rejected.
