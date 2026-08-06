# Engram — working rules

Read `docs/engram-implementation-plan.md` before any non-trivial change. It holds
forty-two decisions (D1–D42) that resolve questions the spec left open, and each one was
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
second reason, independent of AOT-hostility, that D1 keeps `sqlite-vec` and llama.cpp
side-loaded rather than linked.

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

**Nothing starts a model process from `EmbedderFactory`.** `provider = "local"` runs llama.cpp's
server as a child, so the factory's local case *attaches* to a `LocalRuntime` and never launches
one. The reason is that creating an embedder is unowned everywhere it happens: `RetrievalExplainer`
calls the factory purely to ask whether a vector lane exists and drops the result, and no caller
disposes what it gets. A factory that launched would turn a readiness check into a model load and
leak a server per recall. Launching belongs to whoever can also stop it — the MCP server holds one
as a container singleton, `explain` builds and disposes its own. Measured, because the guard reads
like boilerplate until it does not: with `Dispose` broken, one test run left seven servers alive on
the machine. llama-server does not exit when its parent does. Engram locates that binary and never
downloads it, and a `server_path` that is set but missing is an error rather than a reason to fall
back to `PATH` (D35).

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
looking for the weights and for `llama-server`, never by resolving an embedder, because resolving
one launches llama.cpp (D35). Both are guarded: an end-to-end test snapshots every file in the home
by size and mtime around a run and asserts nothing moved, and an integration test installs a
stand-in `llama-server` that touches a marker when executed and asserts the marker never appears.
Only `Broken` sets exit 1 — `Off` is a supported configuration, not a fault, and a doctor that
reported red for a choice the user made is one people stop reading. Every check runs inside a
wrapper that turns a throwing check into one broken row, because the state most likely to make a
check throw is the state someone is running doctor in (D37).

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

**Ask `ServerIsAlive`, never `Kind is Running`.** `Wedged` and `VersionMismatch` are both live
processes holding whatever they loaded at startup, so any caller deciding whether it may act alone —
`embed --rebuild`, by D38 — has to count them. Enumerating states at the call site is how one caller
ends up racing a server it decided was absent. A version gap is also not a hang: it gets its own
state and doctor warns rather than reporting `Broken`, because nothing is wrong with a server that
answers correctly from the build before this one (D37, D42).

## Build constraints

- .NET 10, `net10.0`. Warnings are errors.
- `Engram.Cli` publishes Native AOT. No reflection-based serialization — use
  `JsonNode` for dynamic JSON and a source-generated `JsonSerializerContext` for typed
  JSON. Watch for overload traps: `JsonArray.Add(x)` binds to the AOT-hostile generic
  overload, so cast through `IList<JsonNode?>`.
- No ORM. Hand-written SQL, so query plans stay visible.
- Roslyn and llama.cpp never link into the core binary — sidecar and side-loaded
  respectively (D1).

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
