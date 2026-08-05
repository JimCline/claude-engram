# Engram — working rules

Read `docs/engram-implementation-plan.md` before any non-trivial change. It holds
twenty-six decisions (D1–D26) that resolve questions the spec left open, and each one was
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

**Every write is `BEGIN IMMEDIATE`.** A deferred transaction that upgrades to a writer
raises `SQLITE_BUSY_SNAPSHOT`, which `busy_timeout` cannot wait out (D4).

**`file-touched` never opens the database.** It appends to a spool file and exits. Its
budget is 10 ms and it must hold unconditionally, not just when nothing else is writing.
This rule is about that hook, not about hooks: D4 justifies it entirely by per-edit
frequency and write contention. The primer hooks — `session-start`, `subagent-start` —
do take a short read and close it, because a primer that reports memory from a hardcoded
list disagrees with recall the moment a fact is forgotten. `user-prompt` writes, once per
message the user sends: it is the only place a fact stated in passing can be caught, and a
capture the model has to opt into is a capture that does not happen. Each of those was
measured against the version it replaced — a hook that opens the database is a decision
with a number behind it, never a default.

**Anything destructive is dry-run first.** `repair`, `compact`, `forget`, and the
installer print what they would do and require an explicit flag to act. Anything editing
a user's file backs it up first and refuses to overwrite a value it did not create.

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
