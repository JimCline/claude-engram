# Spec: verification for the `lookup-nudge` PreToolUse hook

## Status

The hook is implemented and building clean; tier 1 and tier 2 are green (Core 638/638,
Integration 1026 passed / 80 skipped). What is missing is verification of the hook *verb* itself
and any tier 3 coverage at all. This spec covers only that gap. **Do not redesign the hook, the
classifier, or the wiring** — if any of it looks wrong, stop and report the gap rather than
changing it.

## What already exists (do not re-implement)

| File | Role |
|---|---|
| `src/Engram.Core/SymbolQueryDetector.cs` | classifier: `LooksLikeSymbol`, `ExtractSearchPattern` |
| `src/Engram.Core/SessionNudgeState.cs` | once-per-session state, path-parameterized (shared with `memory-guard`) |
| `src/Engram.Core/EngramHome.cs` | `LookupNudgeStatePath` → `<home>/lookup-nudge.state` |
| `src/Engram.Cli/HookCommand.cs` | `lookup-nudge` verb + `RunLookupNudge` |
| `src/Engram.Core/Telemetry.cs` | `TelemetryEventKind.LookupNudge` |
| `src/Engram.Cli/HookOutputModels.cs` | `tool_name` on `HookStdinInput`; `pattern`, `command` on `HookToolInput` |
| `plugin/hooks/hooks.json` | PreToolUse entry, matcher `Grep\|Glob\|Bash` |
| `tests/Engram.Core.Tests/SymbolQueryDetectorTests.cs` | tier 1, 31 cases, falsified |

## Behaviour being verified

`RunLookupNudge` reads the PreToolUse payload and, in this order:

1. No `tool_input` → exit 0, silent.
2. Query = `tool_input.pattern` for `Grep`/`Glob`; `SymbolQueryDetector.ExtractSearchPattern(tool_input.command)` for `Bash`; null for any other tool.
3. `SymbolQueryDetector.LooksLikeSymbol(query)` false → exit 0, silent.
4. `[memory] precedence = off` → exit 0, silent.
5. No `session_id` → exit 0, silent.
6. Session already in `lookup-nudge.state` → exit 0, silent.
7. Append session to state; if the append fails → exit 0, silent.
8. Append a `lookup-nudge` telemetry record carrying the query.
9. Emit `hookSpecificOutput` with `permissionDecision: "deny"` and the reason from `LookupNudgeDenyReason`.

## Task 1 — Integration test (tier 2)

Create `tests/Engram.Integration.Tests/LookupNudgeHookTests.cs`.

**Use `tests/Engram.Integration.Tests/MemoryGuardHookTests.cs` as the structural template** — same
sandbox-home setup, same way of feeding a JSON payload to the hook verb and capturing stdout. Match
its conventions rather than inventing new ones.

Cases required:

1. **A symbol-shaped Grep denies.** `tool_name: "Grep"`, `tool_input.pattern: "ProcessFile"` → stdout parses as a PreToolUse output with `permissionDecision == "deny"`, and the reason mentions `engram_navigate`.
2. **A plain-word Grep stays silent.** `pattern: "latency"` → no stdout, and `lookup-nudge.state` does not exist.
3. **A shell grep denies.** `tool_name: "Bash"`, `tool_input.command: "grep -rn ProcessFile src/"` → deny. This is the case the hook exists for; it must be covered explicitly.
4. **A non-search Bash stays silent.** `command: "dotnet test"` → no stdout.
5. **Second call in the same session stays silent.** Two identical symbol-shaped payloads with the same `session_id` → first denies, second produces no stdout. (The once-per-session rule.)
6. **A different session still denies.** Same payload, different `session_id`, after case 5 → denies. (Proves the state file is keyed on session, not global.)
7. **`precedence = off` disarms it.** Write `[memory] precedence = off` into the sandbox config → symbol-shaped payload produces no stdout. Mirror however `MemoryGuardHookTests` sets precedence.
8. **Telemetry.** After a deny, `telemetry.jsonl` contains a record whose `kind` is `lookup-nudge`. Filter by kind — do **not** assert a total line count (CLAUDE.md: the session-start child writes into the same log, and total-count assertions have broken four end-to-end tests before).

**Prove the tests can fail.** After they pass, break `SymbolQueryDetector.LooksLikeSymbol` (e.g.
make it `return false;`) and confirm the deny cases go red; then restore and confirm green again.
Report both numbers. Do **not** use `git checkout`/`restore`/`stash` to revert — the working tree
has uncommitted work that those would destroy. Edit the file back by hand.

## Task 2 — Tier 3

1. Publish the AOT binary to `./out/engram`. Use whatever command this repo already uses — check `README.md`, `install.sh`, or any `publish` script before inventing one.
2. Run `dotnet test tests/Engram.EndToEnd.Tests/Engram.EndToEnd.Tests.csproj`.
3. **Report the skip count as well as the pass count.** Per CLAUDE.md, tier 3 evaporates into the skip column without a binary while the summary still reads `Passed!`. A run with a large skip count is not a pass — say so plainly if that happens.

No new end-to-end test is required. The goal is to confirm the AOT binary agrees with the JIT build
and that nothing in the new JSON model trips Native AOT.

## Constraints

- .NET 10, warnings are errors, Native AOT for `Engram.Cli`. No reflection-based serialization — any new JSON goes through a source-generated context.
- **`ENGRAM_HOME` must be set (or `--home` passed) before invoking the published binary by hand.** A verification command that omits it writes into the real `~/.engram`; this has already happened once in this repo.
- No test may touch the real instance. Use the sandbox home fixtures the test projects already provide.
- Do not commit, do not push, do not change branches. Report back and stop.

## Report back

- Files created/modified, with paths.
- Tier 2: pass/fail counts, plus the falsification result (which tests went red when the classifier was broken).
- Tier 3: pass **and skip** counts, and the publish command used.
- Anything in this spec that was silent, ambiguous, or wrong — stop and report rather than deciding.
