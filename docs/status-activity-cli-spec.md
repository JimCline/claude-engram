# Spec: `engram status --json` and `engram activity`

Two read-only CLI additions. Neither is destructive, so neither takes a dry-run gate.
`activity` must never open the database.

Author: Architect. Status: ready to implement.

---

## 0. What already exists (do not rebuild it)

Everything below was read from the tree at spec time. Reuse it; do not write a second
version of any of it.

| Thing | Where | Why it matters here |
|---|---|---|
| Verb dispatch | `src/Engram.Cli/CliApp.cs:37` (the `switch` on `remaining[0]`) | one new arm for `activity` |
| Usage text | `src/Engram.Cli/CliApp.cs:78` `PrintUsage` | two lines to touch |
| `status` handler | `src/Engram.Cli/StatusCommand.cs` | gains `--json` |
| `StatusResult` | `src/Engram.Core/ServerLifecycle.cs:59` — `(ServerStatusKind Kind, PidFileRecord? Recorded, HealthResponsePayload? Health, string? LaunchedFrom)`, plus `ServerIsAlive` | the source of every status field |
| JSON precedent | `src/Engram.Cli/DoctorCommand.cs:22` — `DoctorJsonContext`, `[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]` | copy this shape exactly |
| Flag-parse precedent | `src/Engram.Cli/ProbeCommand.cs:14-42` | copy this loop shape exactly |
| Telemetry path | `Telemetry.ResolvePath(EngramHome home)` → `Path.Combine(home.Root, "telemetry.jsonl")` | the only way to locate the file |
| Telemetry read + filter | `src/Engram.Core/TelemetryProbeReader.cs:45` — `TelemetryProbeReader.Read(EngramHome home, DateTimeOffset? since)` returning `TelemetryProbeReadResult(bool FileExists, IReadOnlyList<TelemetryRecord> Records, int SkippedLines)` | **this is `activity`'s whole read path.** It already handles missing file, blank lines, malformed lines (counted, not fatal), and `since` filtering |
| Line parse | `TelemetryLineParser.TryParse` — rejects a record missing `Timestamp`/`SessionId`/`Kind` or with an unparseable timestamp | malformed-line policy is already decided |
| `TelemetryRecord` | `src/Engram.Core/Telemetry.cs:133` | `Timestamp`, `SessionId`, `Kind`, plus optional fields |

**Correction to the dispatching brief.** The brief says records carry a "unix-second
timestamp". They do not. `TelemetryRecord.Timestamp` is a **string in ISO 8601**, parsed
with `DateTimeOffset.TryParse(..., DateTimeStyles.RoundtripKind)`. Do not write
integer-seconds comparison code. Filtering is already done inside
`TelemetryProbeReader.Read`; pass it a `DateTimeOffset` cutoff and read nothing yourself.

---

## 1. `engram status --json`

### 1.1 CLI surface

```
status [--json]                    report whether the MCP server is running
```

- `--json` is the only flag. Any other argument → `CliApp.PrintUsage(stderr)` and return 1
  (this is what the current `rest.Length != 0` guard does; preserve the behaviour, just
  through a parse loop instead).
- Replace `StatusCommand.Run`'s length check with a loop in the shape of
  `ProbeCommand.Run` (`src/Engram.Cli/ProbeCommand.cs:14`): `switch (rest[i])`, `case
  "--json": json = true; break;`, `default: CliApp.PrintUsage(stderr); return 1;`.

### 1.2 Exit codes — unchanged, and this is load-bearing

`status` today returns **0 only for `ServerStatusKind.Running`** and **1 for every other
kind**, including `VersionMismatch` (which prints a full report and still exits 1).
`--json` changes the *rendering* and nothing else. A JSON run must return the identical
code the human run would for the same state. A guard test asserts this pairwise across
kinds (§1.5).

### 1.3 Payload

New file `src/Engram.Cli/StatusJson.cs` (or at the bottom of `StatusCommand.cs` — either
is fine, keep it to one place):

```csharp
internal sealed record StatusJson(
    string Home,
    bool Initialised,
    string Server,
    int? Pid = null,
    int? Port = null,
    string? Version = null,
    DateTimeOffset? StartTimeUtc = null,
    long? UptimeSeconds = null,
    string? StartedFrom = null,
    string? ThisBinary = null);

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusJson))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;
```

Serialize with
`JsonSerializer.Serialize(payload, StatusJsonContext.Default.StatusJson)` and
`stdout.WriteLine` it — same call shape as `ProbeCommand.cs:64`. No naming policy: doctor
does not set one, so property names stay PascalCase and the two commands agree.
`WhenWritingNull` means the not-running shapes simply omit the fields they have no value
for; do not emit explicit nulls or empty strings.

Field derivation, per kind:

| Field | Source |
|---|---|
| `Home` | `home.Root` |
| `Initialised` | `File.Exists(home.ConfigPath)` — same expression the human path uses |
| `Server` | `status.Kind.ToString()` — the enum name verbatim: `Running`, `VersionMismatch`, `Stale`, `Wedged`, `Reused`, `NotRunning` |
| `Pid` | `Running`/`VersionMismatch`: `status.Health!.Pid`. `Wedged`: `status.Recorded!.Pid` (the human line prints exactly that). Otherwise omitted. |
| `Port`, `Version` | `status.Health` when non-null, else omitted. On `VersionMismatch`, `Version` is the **running server's** version, matching the human line. |
| `StartTimeUtc` | `status.Health!.StartTimeUtc` when health is present |
| `UptimeSeconds` | `(long)(DateTimeOffset.UtcNow - health.StartTimeUtc).TotalSeconds`, clamped at 0 — reuse the negative-clamp rule from `FormatUptime`. Emit only where the human path prints uptime, i.e. `Running`. |
| `StartedFrom` | `status.LaunchedFrom` whenever it is non-empty |
| `ThisBinary` | `ExecutablePath.Current` — emit **only** when `StartedFrom` is present and differs |

**Deliberate asymmetry, and why.** The human formatter suppresses `started from:` when it
equals the running binary (`StatusCommand.cs:74`, with a comment explaining that printing
it unconditionally buries the one case that explains a surprising answer). JSON has no
noise problem and a consumer wants the value, so `StartedFrom` is emitted whenever known.
`ThisBinary` keeps the *comparison* available without making the consumer call
`ExecutablePath` itself, and is emitted only in the case the human formatter cares about,
so `ThisBinary` present still means "these disagree".

Do not add a `Detail`/`Message` prose field. The kind name plus the fields is the machine
contract; prose belongs to the human renderer and a second copy of it will drift.

Under `--json`, print **nothing** to stdout but the one document — in particular do not
also emit the `restart it to pick up this build` advice line. The `VersionMismatch` kind
plus the two versions already say it, and a consumer parsing stdout as JSON breaks on a
trailing sentence.

### 1.4 Structure of the change

Split `StatusCommand.Run` into:

1. gather — the existing `home` / `executablePath` / `initialized` / `lifecycle.Status(...)`
   block, unchanged;
2. `private static int ExitCodeFor(ServerStatusKind kind)` — the one place the 0-vs-1 rule
   lives, so both renderers cannot disagree;
3. the existing human `switch` (now returning `ExitCodeFor(...)` rather than literals);
4. `private static StatusJson BuildJson(...)`.

One lifecycle probe per invocation either way — `lifecycle.Status` does a health check over
HTTP, so calling it twice would double that cost and could observe two different states.

### 1.5 Verification

- Tier 2, `tests/Engram.Integration.Tests/` (place beside the existing status tests):
  for each `ServerStatusKind` reachable with a faked `ServerLifecycle` collaborator,
  assert `Run` with `--json` and without return the **same** exit code, and that the
  `--json` stdout parses as JSON with `Server` equal to the enum name.
- Tier 2: `Running` case — assert `Pid`/`Port`/`Version`/`UptimeSeconds` present;
  `NotRunning` case — assert those keys are **absent**, not null (this is what
  `WhenWritingNull` buys and the half worth guarding).
- Tier 2: `Wedged` — assert `Pid` comes from `Recorded`, not from `Health`.
- Tier 3, `tests/Engram.EndToEnd.Tests/`: run the published binary as
  `status --json` against a sandbox home with no server, assert exit 1 and that stdout
  parses cleanly. Remember `Assert.SkipUnless(EndToEndBinary.Path is not null, …)` and
  pass `--home`/`ENGRAM_HOME` — never let it touch the real instance.
- `status --json --bogus` → usage on stderr, exit 1.

Prove each new guard can fail before committing (repo rule): break the exit-code mapping
and confirm the pairwise test reddens.

---

## 2. `engram activity`

Answers one question: **when did Engram last do anything, and how much has it done
lately.**

### 2.1 CLI surface

```
activity [--since <window>] [--json]   when Engram last did anything
```

- `--since <window>` — a live window ending now. Grammar: `<n>` or `<n><unit>` where unit
  is `s`, `m`, `h`, or `d`. A bare number is **seconds**, so the brief's `--since 10`
  means ten seconds. `<n>` must be a positive integer.
  - Bad value → `error: invalid --since value '<v>', expected e.g. '10s', '5m', '2h', '1d'`
    on stderr, exit 1. Missing value → `error: --since requires a value, e.g. --since 10s`,
    exit 1. (Both mirror `ProbeCommand.cs:25` and `:31` in wording style.)
- `--json` — machine shape, §2.4.
- Unknown argument → `CliApp.PrintUsage(stderr)`, exit 1.

Put the parser in `src/Engram.Core/TimeWindow.cs`:

```csharp
public static class TimeWindow
{
    public static bool TryParse(string value, out TimeSpan window);
}
```

Parse with `NumberStyles.None` and `CultureInfo.InvariantCulture`, as
`ProbeCommand.TryParseSinceDays` does.

**Do not retrofit `probe --since` onto this.** `probe` accepts `7d` only, its parser is
private and its behaviour is covered by existing tests; widening it is a separate change
with its own blast radius and is out of scope here. `TimeWindow` is written for `activity`
and is a superset of probe's grammar should anyone later want to converge them.

### 2.2 Behaviour

Locate the file with `Telemetry.ResolvePath(home)` — never build the path by hand
(`EngramHome` is the one home resolver; `NoHardcodedPathsTests` enforces it).

Read with **`TelemetryProbeReader.Read(home, since)`** and nothing else:

- no `--since` → pass `since: null`; the window is all of recorded history.
- `--since w` → pass `DateTimeOffset.UtcNow - w`.

From `TelemetryProbeReadResult`:

- `FileExists == false` → "nothing recorded yet" shape, **exit 0**.
- `Records.Count == 0` → "no activity in window" shape, exit 0.
- otherwise: the **last** record in `Records` is the most recent one (the file is appended
  chronologically and `Read` preserves order — do not sort), and the per-kind counts are a
  group-by over `Records`.
- `SkippedLines > 0` → report the count; never fail on it. Malformed lines are already
  tolerated by the reader and that policy is not `activity`'s to change.

**`activity` never opens the database.** Do not copy `ProbeCommand.ReadFactDensity`, which
does. Telemetry is the whole source.

### 2.3 Human output

No `--since` — one line:

```
last: recall 4m 12s ago (2026-08-23T14:02:11Z)
```

With `--since 10s` — two lines, the window line second:

```
last: recall 4s ago (2026-08-23T14:06:19Z)
window: 3 event(s) in the last 10s — recall 2, file-touched 1
```

Kind counts are descending by count, then ordinal by kind name for a stable tie-break
(an unstable order makes a diff-based test flap). Cap the kind list at the top 5 and
append `, +N more` beyond that — a busy window otherwise wraps, and D52's rule about a
line the terminal wraps applies to anything a status line may render.

Idle window, `--since` given:

```
last: recall 4m 12s ago (2026-08-23T14:02:11Z)
window: no activity in the last 10s
```

Note this needs *two* reads of different windows to render — the "last" line needs
unbounded history while "window" needs the cutoff. Do **one** unbounded
`TelemetryProbeReader.Read(home, since: null)` and apply the cutoff in memory when
counting. One pass over the file per invocation; see the ceiling in §2.7.

No telemetry at all (file missing, or present and holding no valid record):

```
no activity recorded yet
```

Malformed suffix line, when `SkippedLines > 0`, appended last:

```
2 malformed line(s) skipped.
```

Timestamps render as round-trip ISO 8601 (`"o"`-style, `DateTimeOffset` → UTC), matching
what the file stores. The age is rendered from `DateTimeOffset.UtcNow` minus the record
time, clamped at zero, using the `FormatUptime` shape from `StatusCommand.cs:83` but
extended with a sub-minute form (`4s`) so a ten-second window can say something other than
`0h 0m 4s`. Recommended: `Nd Nh Nm` above a day, `Nh Nm Ns` above an hour, `Nm Ns` above a
minute, `Ns` below. Put it in one private helper; do not reach into `StatusCommand`.

**Exit code: always 0** on a successful read, including "no activity" and "nothing
recorded yet". `activity` is a report, not a health check; an empty window is a true answer
about an idle instance, and D37's rule — a diagnostic that reports a supported state as a
fault is one people stop reading — applies. Only argument errors exit 1.

### 2.4 `--json`

Included, because the plausible caller is a status line or script and the two neighbouring
read-only commands (`doctor`, `probe`) both have it. Keep it to one small record.

```csharp
internal sealed record ActivityJson(
    string Home,
    string? LastKind,
    DateTimeOffset? LastAt,
    long? LastAgeSeconds,
    int? WindowSeconds,
    int WindowCount,
    IReadOnlyList<ActivityKindCount> Kinds,
    int SkippedLines);

internal sealed record ActivityKindCount(string Kind, int Count);

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivityJson))]
internal sealed partial class ActivityJsonContext : JsonSerializerContext;
```

- `WindowSeconds` omitted when `--since` was not given; `WindowCount` and `Kinds` then
  cover all of history.
- Nothing recorded: `LastKind`/`LastAt`/`LastAgeSeconds` omitted, `WindowCount` 0, `Kinds`
  empty. **`Kinds` is a non-nullable empty list, not omitted** — a consumer indexing it
  should not have to special-case absence.
- The `+N more` truncation is a *human* rendering concern only; `Kinds` in JSON is
  complete.

AOT: source-generated context only, no reflection serialization. `JsonArray.Add` is not
used here, so the overload trap in the build constraints does not apply.

### 2.5 Wiring

`src/Engram.Cli/CliApp.cs`:

- one arm in the switch, placed after `"doctor"` so related read-only diagnostics sit
  together:
  `"activity" => ActivityCommand.Run(homePath, rest, stdout, stderr),`
- one line in `PrintUsage`, after the `doctor` line, column-aligned with its neighbours
  (the block aligns descriptions; match it):
  ```
  writer.WriteLine("  activity [--since <window>]        when Engram last did anything");
  ```
- amend the existing `status` line to `  status [--json]` and keep its description.

New file: `src/Engram.Cli/ActivityCommand.cs`, `internal static class ActivityCommand`
with `public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter
stderr)` — the same signature every other command uses.

### 2.6 Edge cases, settled

| Case | Behaviour |
|---|---|
| `telemetry.jsonl` missing | `no activity recorded yet`, exit 0 |
| file exists, zero bytes | same message, exit 0 (`Records` empty, `SkippedLines` 0) |
| every line malformed | same message **plus** the `N malformed line(s) skipped.` line, exit 0 |
| home not initialised (no `config.toml`) | irrelevant to `activity` — it reads one file and reports on it. Do not gate on `Initialised`; a home mid-install with a telemetry file is a legitimate thing to ask about |
| home directory does not exist at all | `File.Exists` is false → `no activity recorded yet`. Do **not** create the directory; `activity` creates nothing |
| a record whose timestamp is in the future (clock skew) | age clamps to `0s`; it still counts in the window |
| final line half-written (a concurrent append) | `TelemetryLineParser.TryParse` rejects it, `SkippedLines` increments. Already handled; do not add retry or locking. Opening a reader on this file can starve `DurableAppend` (see CLAUDE.md) — `File.ReadLines` holds it briefly and `TelemetryProbeReader` is the existing precedent for exactly this read, so use it and add no second reader path |
| `--since 0` / negative / non-integer | argument error, exit 1 |
| both `--json` and `--since` | valid, combine |

### 2.7 Known ceiling — the one thing to watch

`TelemetryProbeReader.Read` reads the **whole** `telemetry.jsonl` every invocation. That is
correct and is the existing precedent (`probe` does it), and it is why this spec adds no
new tailing machinery: `TelemetryTail` is offset-based and belongs to a long-lived process
that can hold a cursor, which a CLI invocation cannot.

The exposure is a caller polling `activity --since 10s` on every status-line render against
a file that grows without bound. That is a real ceiling, not a hypothetical, and it is
recorded rather than pre-optimised. If it bites, the fix is a bounded backward read of the
file's last N bytes when `--since` is small — not a cursor, not a database.

Leave a `ponytail:` comment at the read site naming this ceiling and that upgrade path.

**NEEDS-EVIDENCE (route to the Implementor; do not let it block the build).** Measure and
record in the commit message:
1. `wc -c` and `wc -l` of `telemetry.jsonl` on this instance.
2. Wall time of `engram activity --since 10s` on the **published binary** (not `dotnet
   run`) against that file, minus the `probe` process-start floor.
   Remember to pass `--home`/`ENGRAM_HOME`.

If that lands under ~15 ms the ceiling is theoretical for now and nothing further is
needed. If it is materially above, report back before wiring `activity` into anything that
polls — the design does not change, but the polling caller's does.

### 2.8 Verification

- Tier 2, new `tests/Engram.Integration.Tests/ActivityCommandTests.cs` against a
  `SandboxHome`, writing `telemetry.jsonl` by hand:
  - records inside and outside a window → correct `WindowCount` and correct `last`;
  - the last line of the file is the reported `last` even when an earlier line has a later
    timestamp (proves "no sorting" is the intended read of an append-ordered file — if the
    team prefers max-by-timestamp instead, see NEEDS-DECISION 3);
  - a malformed line between two good ones → both good ones counted, `SkippedLines` 1;
  - missing file → the empty message, exit 0;
  - `--json` empty case → `Kinds` present and empty, `LastKind` absent.
- Tier 2: `TimeWindow.TryParse` — `10`→10s, `10s`, `5m`, `2h`, `1d`, and rejection of ``,
  `0`, `-1`, `10x`, `1.5m`, `d`.
- Tier 3, `tests/Engram.EndToEnd.Tests/`: run the published binary's `activity` against a
  sandbox home containing a seeded `telemetry.jsonl` and **no `engram.db`**; assert exit 0,
  assert the expected line, and assert **no `engram.db` was created** — this is the guard
  for "never opens the database", modelled on doctor's file-snapshot test.
  `Assert.SkipUnless(EndToEndBinary.Path is not null, …)` as every test in that tier does.
- **Do not write any test that counts total lines of `telemetry.jsonl`.** CLAUDE.md records
  this trap twice: the server and the detached session-start child both append to that file
  during a test run. Filter by `kind`.
- Prove every new guard fails before it passes.

---

## 3. NEEDS-DECISION

Each has a recommended default, already applied above. Change only if the Orchestrator or
user disagrees.

1. **`--since` grammar.** The brief said seconds (`--since 10`); `probe --since` already
   means days-with-suffix (`7d`). Two commands sharing a flag name with different units is
   a trap. *Recommended and specced:* accept `<n>[s|m|h|d]` with a bare number meaning
   seconds — satisfies the brief literally and is a superset of probe's grammar.
   *Alternative:* seconds-only, simplest, but then `activity --since 7d` is an error while
   `probe --since 7d` works.
2. **`activity` exit code when the window is empty.** *Recommended and specced:* always 0.
   *Alternative:* 1 for an empty window so `if engram activity --since 10s; then` works as
   a liveness test — rejected because it reports a supported state as a fault (D37) and a
   status line does not want a nonzero.
3. **"Last" = last line, or max timestamp?** *Recommended and specced:* last line, because
   the file is append-ordered and a sort is O(n log n) for a property the format already
   guarantees. *Alternative:* max-by-timestamp, defensive against out-of-order appends —
   which do not happen today.
4. **`activity --json` at all.** *Recommended and specced:* yes, one small record. The
   brief left it to me and both neighbouring read-only commands have it.
5. **Human output when `--since` is absent.** *Recommended and specced:* the `last:` line
   only, with no all-time count. An unbounded "1,847 events, all time" line is noise
   against the question the command names.

## 4. Out of scope — do not do these

- Changing `probe --since`'s grammar or its private parser.
- Any cursor, offset file, or state written by `activity`. It reads and prints.
- Any use of `TelemetryTail` from a CLI verb.
- Touching `Telemetry.Append`, `DurableAppend`, or the webhook.
- Adding an `activity --watch`. `embed --status --watch` exists and if a live view is
  wanted later it should follow that pattern deliberately, not be smuggled in here.

## 5. Confidence

Reasonable. Both commands are small, sit entirely on existing helpers, and neither writes
anything. The one place I would want a second look is §2.7's ceiling if `activity` is
about to be wired into a per-render status-line poll — that changes the caller's design,
not this one, but it is worth knowing the number first. No escalation to the Ultra-Advisor
recommended.
