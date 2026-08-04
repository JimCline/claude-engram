# Engram — working rules

Read `docs/engram-implementation-plan.md` before any non-trivial change. It holds nine
decisions (D1–D9) that resolve questions the spec left open, and each one was reached by
argument or measurement, not preference. `docs/engram-schema.sql` is the authority for
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

**Facts are append-only.** Belief content — predicate, body, object, validity — is
immutable once written. Only `valid_to` and `superseded_by` are ever updated, and only
to close a fact. `path` is the sole exception: it is addressing metadata that follows
its entity on rename (D2), not belief content.

**Derived state is repairable; authored truth is not.** `compact` and `repair` may only
touch what can be regenerated — the FTS index, salience, denormalized paths, indexed
code facts. Neither may ever create, alter, or delete a fact body, predicate, validity
window, or supersession row (D8).

**Every connection sets its own pragmas.** `foreign_keys` and `busy_timeout` are
connection-scoped and default off/zero. Setting them in a schema file configures
nothing. Open through the one shared routine.

**Every write is `BEGIN IMMEDIATE`.** A deferred transaction that upgrades to a writer
raises `SQLITE_BUSY_SNAPSHOT`, which `busy_timeout` cannot wait out (D4).

**Hooks never open the database.** `file-touched` appends to a spool file and exits. Its
budget is 10 ms and it must hold unconditionally, not just when nothing else is writing.

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
