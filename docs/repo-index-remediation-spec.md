# Spec — closing the repo index-freshness gap (four mechanisms)

Status: FINAL, amended once after NE-1 returned. Reviewed against the tree at `main` / `f3a8516`.
Ready to implement.
Scope: design only. Nothing here has been implemented or executed. Every empirical question is a
NEEDS-EVIDENCE item in §10 and must be routed to the Implementor rather than guessed at.

Provenance: this is the finalized form of a draft produced by an earlier design pass. Every
`file:line` citation below was re-verified against `main` at `f3a8516`; §12 lists the citations that
had drifted and the assumptions that are now resolved. Three open questions the draft left for the
user are **settled here**, with rationale — see §9. One of them is settled *against* the draft's own
framing, because the code disagreed with it.

**Amendment, after NE-1 ran.** §10's NE-1 now carries its measured result and gains a follow-up
(NE-1b), §11 carries the sequencing ruling it produced — a third outcome the original table did not
anticipate — §7 moves one guard forward from commit E to commit A, §8 drops its conditional
re-ordering, and §12.4 **withdraws** the Ultra-Advisor escalation with its reasoning. Each amended
section says what changed and why at the point of change.

---

## 0. TL;DR

`engram repo list` shows `last full scan: never` for enrolled repos and nothing reliably fixes it.
Four mechanisms close the gap, and they share **one selection policy** and **one indexing call**:

| # | Mechanism | Surface | Cadence |
|---|---|---|---|
| 1 | `doctor` warns per neglected enrolled repo | `Diagnostics.CheckEnrolledRepos` (new check) | on demand, read-only |
| 2 | `engram repo index --all [--apply]` | `RepoCommand` | user-invoked, all due repos |
| 3 | `engram index --freshen --apply` in the session-start child | `IndexCommand` + `MaintenanceLauncher` | ≤1 repo per session start |
| 4 | `IndexFreshnessService` in the MCP server | `ServeCommand` DI | ≤1 repo per tick, default **off** |

Items 3 and 4 are the *same* bounded policy at two cadences. That is deliberate and is the main
structural decision in this spec.

**The finding that reshaped the design.** `IndexCommand.DrainOtherEnrolledRoots`
(`src/Engram.Cli/IndexCommand.cs:213`) constructs every secondary root's options as:

```csharp
new IndexOptions(secondaryRoot, apply, Drain: true, Full: false, AllowFullScanDue: false, Queue: queue),
```

So the session-start `--drain-all` pass **never full-scans any repo except the invoked one**, by
design — its own docstring says a stale-cadence rescan of every enrolled repo per session start is
"unbounded in the number of repos enrolled". The consequence: a repo whose enrollment spawn died,
and in whose directory no session is ever started, is never full-scanned by anything. The problem
statement framed item 3 as "self-heal a dead spawn"; it is actually "close a structural hole", and
the fix must supply the **bound** that `AllowFullScanDue: false` was standing in for, not merely
flip that flag.

**NE-1 has run.** Its measured result is in §10 and the sequencing ruling it produced is in §11.
Short version: the concurrent-index race is real, the store's one-live-fact-per-subject-and-predicate
invariant absorbs it correctly, and `IndexLock` therefore stays at commit E — but the guard that
proves the absorption moves forward to commit A.

---

## 1. Ground truth (verified by reading at `main` / `f3a8516`)

Everything in this section was read. Anything unverified is in §12.

### 1.1 Schema (`docs/engram-schema.sql:343-351`, schema version 7)

```sql
CREATE TABLE repo_enrollment (
  identity          TEXT PRIMARY KEY,  -- CodeIndexer.ResolveIdentity(root); same key as repo_registry.identity
  state             TEXT NOT NULL CHECK (state IN ('enrolled','declined','deferred')),
  source            TEXT NOT NULL CHECK (source IN ('user','backfill')),
  last_root         TEXT,              -- last seen checkout root: a lookup cache, never the key
  decided_at        INTEGER NOT NULL,  -- unix seconds
  last_full_scan_at INTEGER            -- unix seconds; NULL = never scanned = due
);
CREATE INDEX ix_repo_enrollment_root ON repo_enrollment(last_root);
```

`decided_at` and `source` both exist and both are load-bearing below. **No schema change is required
by this spec.**

### 1.2 Existing API surface

`src/Engram.Core/RepoEnrollment.cs`:
- `RepoEnrollmentRow(...)` at `:12`
- `ByRoot` `:46` — cache-only; `IsEnrolled` `:79-101` — two-step, falls back to a `git` subprocess
- `ListAll` `:137`, `IsFullScanDue` `:180`, `StampFullScan` `:195`
- `DeferralCooldown = TimeSpan.FromDays(7)` at `:36` (see §9, OQ-3 — the collision with
  `NeglectedAfter` is real and must stay a collision)

`src/Engram.Core/CodeIndexer.cs`:
- `IndexOptions(string Root, bool Apply, bool Drain, bool Full, string? SidecarPath = null, ScanBudget? Budget = null, bool AllowFullScanDue = true, SpoolQueue? Queue = null)`
- `IndexReport(... int FactsWritten, int FactsClosed, ..., IReadOnlyList<string> Notes)`
- `IndexReport Index(SqliteConnection, EngramHome, ConfigFile, IndexingSettings, IndexOptions, DateTimeOffset now)`

`src/Engram.Core/IndexingSettings.cs`: `AutoIndexOnSessionStart` field `:23`,
`DefaultAutoIndexOnSessionStart = true` `:33`, `FullScanIntervalMinutes = 60` `:57`, the config read
at `:174`.

`src/Engram.Core/Diagnostics.cs`: `enum DiagnosisState { Ok, Warn, Broken }` `:8`,
`record Diagnosis(string Name, DiagnosisState State, string Detail, string? Fix = null)` `:24`,
`CheckRepo` `:924`. (The draft said "twelve checks"; there are more — register the new check
alongside the existing ones and do not rely on a count.)

`src/Engram.Core/MaintenanceLauncher.cs`: jobs discriminator at `:84`; `indexInvocation` at `:97-99`
picks `" index --drain --apply "` for `EnrollmentIndex` and `" index --drain-all --apply --auto "`
for `SessionStart`; `Redirect = "exec </dev/null >/dev/null 2>&1; "` at `:131`, prepended at `:110`.
`:115` carries the sentence that pins the config key's meaning: *"`auto_index_on_session_start`
answers 'may Engram index on its own', not 'must Engram …'"*.

`src/Engram.Cli/HookCommand.cs:428-431` spawns the launcher **unconditionally** whenever
`Environment.ProcessPath` is non-empty, inside a swallow-all try/catch. **The shell already forks on
every session start regardless of any config**, so adding a job to `SessionStart` costs the hook
nothing beyond what D28 already priced (1.6–3.4 ms for the one fork). The `--auto` gate lives at
`IndexCommand.cs:73` and `HookCommand.cs:298/320` — inside the child, not on the fork.

`src/Engram.Cli/IndexCommand.cs`: flags `--apply`, `--drain`, `--drain-all`, `--full`, `--auto`;
`Note(home, phase, repo)` at `:237-247` emitting `TelemetryEventKind.Index`; call sites at `:107`,
`:134`, `:162`, `:207`, `:218`.

`src/Engram.Cli/RepoCommand.cs` — **read in full**, because it is the file OQ-1 turns on:
- `Run`'s missing/flag-shaped subcommand error `:22-23`, **exit 2**; dispatch switch `:29-37`
- `Enroll` `:40`, `Decline` `:68`, `Later` `:85`, `Reset` `:103-133`, `List` `:207`
- `Unknown` `:314-318`, **exit 2**
- `CliSessionId = "cli"` `:147`; `ApplyDecision` `:160`
- `TrySpawnFirstIndex` called **unconditionally** from the enroll case at `:177`; the spawn itself at
  `:270`
- the enroll announcement `:57` and the spawn-failure warning `:61-62`
- the `repo list` rendering: identity bare at `:240`, state/source `:241`, root `:242`, and
  `last full scan: {scan}` at `:243`, where `scan` is `MomentText.Local(last)` or the literal
  `"never"` (`:237`) — the exact symptom this spec exists to remove
- `Reset` is the in-file dry-run-by-default precedent: `--apply` read at `:105`, the would-do line at
  `:117`, and *"Dry run only — nothing was changed. Re-run with --apply to reset."* at `:119`
- `ResolveCheckoutRoot` `:254-258`, `TryResolveCheckout` `:281-294`, `FileStateCount` `:296-304`,
  `StateText` `:306-312`

`src/Engram.Cli/ServeCommand.cs`: `AddFilter` `:78`, `:83`; `AddHostedService` `:102`, `:108`;
`ApplicationStopping.Register` `:152`.

`src/Engram.Core/PathCanonicalizer.cs:13` — `Canonical(string)`, public, in `Engram.Core`.
`src/Engram.Core/ProcessStartToken.cs` — `ForSelf()` `:44`, `ForPid(int)` `:47`, both `string?`.
`src/Engram.Core/EngramHome.cs` — `EmbeddingProgressPath` declared `:57`, composed `:98`. No lock
directory exists yet. `src/Engram.Core/DefaultConfig.cs:56-57` — the `[indexing]` block.

`grep -rn 'IndexLock' src/ tests/` returns nothing: the type in §6.4 is genuinely new.

### 1.3 Three invariants in the existing code that this spec must not disturb

**Truncated scans never stamp.** `CodeIndexer.cs:128-152`: `stampFullScan` is set to `options.Apply`
only inside the `else` of `if (scan.Truncated)`, and read after the write pass commits. Every
mechanism below inherits this **by calling `CodeIndexer.Index`** rather than scanning itself. No
mechanism in this spec may call `RepoScanner.Scan` directly.

**`full` is forced when `Drain` is false.** `CodeIndexer.cs:110`:
```csharp
var full = options.Full || versionForcedFull || !options.Drain || fullScanDue;
```
So `IndexOptions(root, apply: true, Drain: false, Full: false)` already means *full scan, no queue
interaction*. Items 2, 3 and 4 all use exactly that shape. They therefore never touch `SpoolQueue`,
never call `Consume` or `DiscardExcept`, and cannot perturb D67's losslessness argument for the
drain path.

**A NULL scan stamp is what makes the first index full, and no caller may pass `--full` to
shortcut it.** D67 states this and explains the cost of breaking it: passing `--full` at the
enrollment spawn "would produce the same scan today and permanently disarm that falsification".
`RepoIndexRun.Freshen` (§2.2) therefore passes `Full: false` and relies on `Drain: false`.

---

## 2. Shared foundation (lands first, in commit A)

### 2.1 `RepoFreshness` — one selection policy, four callers

New file: `src/Engram.Core/RepoFreshness.cs`.

```csharp
/// Why a repo is being offered for a full scan. The distinction is not cosmetic: an
/// unfulfilled user enrollment is the retry of a command already given and already
/// announced, and is not gated by auto_index_on_session_start; everything else is
/// ambient upkeep and is (D67, and §5.3 here).
public enum FreshnessReason
{
    /// last_full_scan_at IS NULL and source = 'user': the user typed `engram repo enroll`
    /// (or called the MCP tool), Engram printed "The first index is running in the
    /// background", and that index demonstrably never completed.
    UnfulfilledEnrollment,

    /// last_full_scan_at IS NULL and source = 'backfill': the v6->v7 migration inferred this
    /// enrollment. Nobody asked for it and nobody was told anything, so it is ambient.
    NeverScanned,

    /// last_full_scan_at is set but older than the interval.
    Stale,
}

public sealed record FreshnessCandidate(RepoEnrollmentRow Row, string Root, FreshnessReason Reason);

public static class RepoFreshness
{
    /// How long doctor waits after a decision before calling a NULL scan stamp neglect
    /// rather than work still in flight. Its basis is NE-3, not taste: it must exceed the
    /// wall time of one full applied index of the largest enrolled repo by an order of
    /// magnitude. Ships at one hour ONLY if NE-3 measures that run at or under six minutes.
    /// See §3.2 and §9 OQ-3.
    public static readonly TimeSpan EnrollmentGrace = TimeSpan.FromHours(1);

    /// How long doctor waits before calling a stamped repo neglected. Deliberately far longer
    /// than IndexingSettings.FullScanIntervalMinutes: "due" drives work, "neglected" drives a
    /// warning, and warning at 61 minutes is how people learn to stop reading doctor (D37).
    /// Seven days is chosen so that neglect implies a BROKEN MECHANISM rather than a lull —
    /// item 3 heals one repo per session start, so a week is dozens to hundreds of chances.
    ///
    /// This is numerically equal to RepoEnrollment.DeferralCooldown (RepoEnrollment.cs:36) and
    /// MUST NOT be replaced by it or by a shared constant. That one is a consent interval —
    /// how long before re-asking a human who said "not now" — and moves with how irritating
    /// re-prompting is. This one is a diagnostic threshold and moves with the heal cadence.
    /// No test can hold them apart while they are equal, which is why the comment is the guard.
    public static readonly TimeSpan NeglectedAfter = TimeSpan.FromDays(7);

    /// Every enrolled repo whose checkout is present on disk and whose full scan is due,
    /// most-neglected first: NULL stamps before stamped ones, oldest decided_at within the
    /// NULLs, oldest last_full_scan_at within the rest, identity as a total-order tiebreak.
    /// Ordering is what makes a bounded caller converge instead of starving one repo forever.
    /// Per row this does a Directory.Exists and a PathCanonicalizer.Canonical, which walks every
    /// component of the path front to back calling Directory.ResolveLinkTarget on each, recursing
    /// into a resolved target's own prefix to a depth of 8. Strictly read-only, and per-row against
    /// a registry of enrolled repos rather than a walk of a tree, so it is nowhere near D53's
    /// enumeration hazard — but it is not a single stat.
    public static IReadOnlyList<FreshnessCandidate> Due(
        SqliteConnection connection, int intervalMinutes, DateTimeOffset now,
        IReadOnlySet<string> exclude);

    /// Bounded selection for the session-start child and the background service.
    /// `includeAmbient` false restricts the result to FreshnessReason.UnfulfilledEnrollment.
    /// Returns at most one candidate. Never returns a root in `exclude` (canonicalized
    /// through PathCanonicalizer.Canonical).
    public static FreshnessCandidate? NextDue(
        SqliteConnection connection, int intervalMinutes, DateTimeOffset now,
        bool includeAmbient, IReadOnlySet<string> exclude);

    /// Rows doctor should warn about. Not the same predicate as Due(): see §3.2.
    public static IReadOnlyList<FreshnessCandidate> Neglected(
        SqliteConnection connection, DateTimeOffset now);
}
```

Selection filter, shared by all three producers, mirroring the filter already at
`IndexCommand.cs:196-202` so the two agree:

```
row.State == RepoEnrollmentState.Enrolled
  && row.LastRoot is { } root
  && Directory.Exists(root)
  && !exclude.Contains(PathCanonicalizer.Canonical(root))
```

An enrolled repo whose checkout is absent is **deliberately not a candidate** — the same rule D67
already applies in `DrainOtherEnrolledRoots`, for the same reason: a missing checkout is not a
freshness problem.

`Due` uses `RepoEnrollment.IsFullScanDue(row, intervalMinutes, now)` — it does not re-derive the
predicate. `Neglected` uses its own predicate (§3.2), and that divergence is intentional and must be
commented at both definitions.

> **Change from the draft:** `Due` gains the `exclude` parameter that `NextDue` already had. The
> draft specified `exclude` on the API and then never wired it, which left a real double-scan loop
> in item 3 — see §5.4.

### 2.2 `RepoIndexRun` — one indexing call, four callers

New file: `src/Engram.Core/RepoIndexRun.cs`. This is the *only* place any of items 2, 3, 4 turn a
`FreshnessCandidate` into an index.

```csharp
public static class RepoIndexRun
{
    /// A full scan of one enrolled repo, with no spool-queue interaction at all.
    /// Drain: false is what forces the full scan (CodeIndexer.cs:110), so this needs no
    /// Full: true — and must not pass one, for the reason D67 gives at the enrollment spawn:
    /// an explicit --full permanently disarms the falsification that proves a NULL
    /// last_full_scan_at is what makes the first scan full.
    ///
    /// Not draining is deliberate and is what keeps this off D67's losslessness argument:
    /// DiscardExcept is the drain path's bound, and a caller that consumes without being part
    /// of that three-step pass could discard an entry no root scanned for.
    public static IndexReport Freshen(
        SqliteConnection connection, EngramHome home, ConfigFile config,
        IndexingSettings settings, string root, bool apply, ScanBudget? budget,
        DateTimeOffset now)
        => CodeIndexer.Index(
            connection, home, config, settings,
            new IndexOptions(root, apply, Drain: false, Full: false, Budget: budget),
            now);
}
```

`AllowFullScanDue` is left at its `true` default and is irrelevant here, because `Drain: false`
already forces `full`. Say that in the comment: a reader who has seen `AllowFullScanDue` at
`IndexCommand.cs:213` will ask why it is absent.

### 2.3 `IndexTelemetry` — one emitter

`IndexCommand.Note` (`src/Engram.Cli/IndexCommand.cs:237-247`) is private to the CLI, and item 4 runs
in the server. Move it, unchanged in behaviour, to `src/Engram.Core/IndexTelemetry.cs`:

```csharp
public static void Note(EngramHome home, string sessionId, string phase, string repo);
```

- Same `File.Exists(home.ConfigPath)` guard.
- CLI callers pass `"cli"`, as today — the same convention the enrollment verb group already spells
  out at `RepoCommand.cs:145-147`. The server passes `"server"` — **a third honest value, on the
  same reasoning the existing code gives for `"cli"`** (D43: the id spaces are disjoint and do not
  combine). Hence a parameter rather than a hardcoded constant.
- **No counts** on the record (D46, D43).
  `ActivityEventsTests.AnIndexEvent_CarriesNoBorrowedCounts` (`:78`) must stay green untouched.
- `IndexCommand` calls the moved helper. Its five existing call sites (`:107`, `:134`, `:162`,
  `:207`, `:218`) keep emitting exactly what they emit today.

**No new telemetry kind.** An index run started by the background service is an `index` run; D55/D56
say a kind answers *how memory is used*, and a second kind would split that population. Every
mechanism in this spec emits `started` / `finished` / `failed` with `Repo` = identity.

> **Deliberately deferred:** distinguishing commanded from background runs in telemetry. It would be
> a new **field**, never a new kind. Nobody has asked the question yet, and D43's lesson is that an
> unrequested field in a record is where a wrong conclusion comes from. Revisit when someone
> actually needs to answer it.

---

## 3. Item 1 — `doctor` surfaces the gap without fixing it

### 3.1 Placement: a new check, not a change to `CheckRepo`

`Diagnostics.CheckRepo` (`:924`) answers *is the repo I am standing in indexed*. Item 1 asks *are any
enrolled repos neglected*, which is a different question with a different scope, and `CheckRepo`
already calls `RepoScanner.Scan` — a path D53 spent a whole decision bounding. Adding a second
concern there means every future edit to either concern risks the other.

**New:** `internal static IReadOnlyList<Diagnosis> CheckEnrolledRepos(EngramHome home, SqliteConnection? connection, DateTimeOffset now)`
in `src/Engram.Core/Diagnostics.cs`, registered alongside the existing checks. It takes `home`
because of the lock refinement in §3.2; if commit E has not landed, `home` is unused and that is
fine.

Returns a list because there is one row per neglected repo. If none are neglected it returns a
**single** `Ok` row (`"N enrolled repo(s), all scanned within the last 7 days"`) — not an empty list,
because a check that vanishes when healthy is a check nobody notices is gone. If `connection is
null`, one `Off` row.

### 3.2 Predicate, and why it is not `IsFullScanDue`

```
neglected(row, now) :=
    row.State == Enrolled
    && row.LastRoot is present on disk
    && no live IndexLock is held for row.Identity          // only once commit E has landed
    && (
        (row.LastFullScanAt is null
            && now - row.DecidedAt > RepoFreshness.EnrollmentGrace)   // never scanned
        || (row.LastFullScanAt is { } t
            && now - t > RepoFreshness.NeglectedAfter)                // long stale
       )
```

Four decisions embedded here, each with its reason:

1. **Not `IsFullScanDue`.** That predicate uses `FullScanIntervalMinutes = 60`. Warning about every
   repo not scanned in the last hour would mean `doctor` is amber essentially always, and D37 is
   explicit that a diagnostic reporting an ordinary state as a fault is one people stop reading.
   *Due* drives work; *neglected* drives a warning. Two predicates, both named, both commented at the
   other's definition so the divergence cannot read as a bug.
2. **The `EnrollmentGrace` on NULL.** `engram repo enroll` spawns the first index detached and
   returns immediately (`RepoCommand.cs:177`). Running `doctor` two seconds later would otherwise
   warn about work that is in flight. `decided_at` already exists, so the grace needs no schema
   change. Its value has a stated basis and a measurement behind it — see §9, OQ-3 and §10, NE-3.
3. **The lock check replaces the grace as evidence, once it exists.** A live `IndexLock` for that
   identity (§6.4) is *proof* that work is in flight; the grace is only a proxy for it. Once commit E
   has landed, a held lock suppresses the row outright and the grace covers only the window between
   the fork and the claim. **`doctor` reads the lock and never reaps it** — reaping deletes a file,
   `doctor` may not write, and the existing end-to-end guard that snapshots every file in the home by
   size and mtime around a `doctor` run would catch it. A lock whose holder is dead therefore still
   suppresses the row until something else reaps it; that is the safe direction (one missed warning,
   never a write from `doctor`).
4. **Absent checkouts are excluded.** `doctor` should not tell someone to index a directory that is
   not there; that is a different, existing concern.

### 3.3 Output

```
indexing/enrollment  WARN  engram — never scanned (enrolled 3 days ago)
                           fix: engram repo index --all --apply
indexing/enrollment  WARN  wrangl — last full scan 22 days ago
                           fix: engram repo index --all --apply
```

Row order is `ListAll`'s `ORDER BY identity`, which is what makes §7 commit C's exact-row assertions
well-defined; a test asserting exact rows against an unstated ordering passes by luck.

- **`Warn`, never `Broken`.** A neglected repo is a state, not corruption. `Broken` sets exit 1 (D37)
  and this must not. Same call D53 already made for a truncated scan.
- The `Fix` string names `engram repo index --all --apply`, which is why **item 2 lands before item
  1** (§8). `doctor` must never name a command that does not exist.
- Identity is rendered the way `engram repo list` renders it — bare, exactly as written at
  `RepoCommand.cs:240`, with the scan stamp formatted through `MomentText.Local` as at `:237`/`:243`
  — so the two surfaces address repos identically and a user can match a doctor row to a list entry
  by eye.
- **Discoverability for the off-by-default background key.** When the check is already warning *and*
  `auto_index_in_background` is absent from the config, append one sentence to the last row naming
  the key as an option. Only when already warning: an unset key is not a fault, and D37 forbids
  reporting a user's own configuration as one. This is the whole mitigation for §9 OQ-2's "a
  default-off feature nobody discovers".

### 3.4 Read-only, enforced

- Opens through `EngramDatabase.Open` only — never `OpenInitialized`, which migrates and by D31
  snapshots, making *your store is a schema behind* unsayable (D37). `RepoCommand.List` (`:215`) is
  the existing precedent for the read-only open on this table.
- **Calls no scanner.** Unlike `CheckRepo`, this check does zero filesystem enumeration. Per row this
  does a `Directory.Exists` and a `PathCanonicalizer.Canonical`, which walks every component of the
  path front to back calling `Directory.ResolveLinkTarget` on each, recursing into a resolved target's
  own prefix to a depth of 8, plus reads at most one lock file. Strictly read-only, and per-row against
  a registry of enrolled repos rather than a walk of a tree, so it is nowhere near D53's enumeration
  hazard — but it is not a single stat. The 7.8 GB home-directory failure mode is unreachable.
- Calls nothing in `CodeIndexer` or `RepoIndexRun`, and never reaps a lock (§3.2, decision 3).
- The existing end-to-end guard that snapshots every file in the home around a `doctor` run must be
  **extended** to a home containing enrolled-and-neglected repos and a stale lock file, not merely
  left passing on a home where the new check returns `Ok` (§7).

---

## 4. Item 2 — `engram repo index --all [--apply]`

### 4.1 Surface

```
engram repo index --all            # dry run: list what it would scan
engram repo index --all --apply    # do it
engram repo index                  # error: usage, exit 2
```

`--all` is **required**. A bare `engram repo index` inside a checkout would read as "index this
repo", which `engram index --apply` already means; two spellings of one action is how someone indexes
forty repos meaning one. Requiring the selector also leaves room for a future
`engram repo index <path>` without changing what the bare form means.

Wiring: one new arm in the dispatch switch at `RepoCommand.cs:29-37`:

```csharp
"index" => IndexAll(home, rest, stdout, stderr),
```

plus `private static int IndexAll(EngramHome home, string[] args, TextWriter stdout, TextWriter stderr)`
placed after `List` (`:207`).

**Exit codes follow the convention already in the file, which is a real split and not an accident:**
2 means *you typed the command wrong* (`Run`'s missing-subcommand error at `:22-23`, `Unknown` at
`:314-318`), 1 means *the command was right and the work failed* (`:44`, `:72`, `:89`, `:110`,
`:226`). So a missing `--all` exits **2**; a repo that failed to index exits **1**.

Also update the two usage strings that enumerate the subcommands — `:22` and `:316` — to include
`index`. A verb that exists and is not listed is a verb nobody finds.

### 4.2 Dry-run default

`--apply` is required to act. Three independent reasons, in descending strength:

1. **It rewrites what is already there.** D67 records a measured dry run on this very repository:
   *42 facts to write, **695 to close**, 3 files deleted.* Closing 695 beliefs is squarely inside
   D49's "removes or rewrites something already there". A verb that can close 695 facts with no flag
   is exactly the shape D49 exists to prevent.
2. **Symmetry with its siblings, one of which is in this very file.** `engram index` is dry-run by
   default with `--apply`, and so is `engram repo reset` (`RepoCommand.cs:105`, `:115-121`). Two
   indexing entry points with opposite defaults is a trap independent of which default is right —
   and `repo reset` fixes the wording too: match its closing line,
   *"Dry run only — nothing was changed. Re-run with --apply to index."* (`:119` is the model).
3. **It multiplies.** The single-repo verb risks one repo; this one risks every enrolled repo at
   once. If either deserves the brake, this one does more.

The no-flag guard is the load-bearing half, per D49's own note: a tier-3 test asserting that
`engram repo index --all` with no `--apply` writes nothing.

### 4.3 Behaviour

1. Open `EngramDatabase.Open` for a dry run, `OpenInitialized` for `--apply` — matching
   `IndexCommand.cs:116-118`. A dry run against a missing store errors rather than creating one (same
   message and reasoning as `IndexCommand.cs:100-105`). `RepoCommand.List:209-213` is the gentler
   precedent — it prints "nothing enrolled yet" and exits 0 — and is deliberately **not** followed
   here, because this verb was asked to do work and silence would read as work done.
2. `var candidates = RepoFreshness.Due(connection, IndexingSettings.FullScanIntervalMinutes, now, exclude: []);`
3. For each candidate, in the returned (most-neglected-first) order:
   - `IndexTelemetry.Note(home, "cli", "started", identity)`
   - `RepoIndexRun.Freshen(connection, home, config, settings, root, apply, budget: null, now)` —
     `budget: null` means `CodeIndexer` applies `ScanBudget.Default`, the same bound the single-repo
     command gets (§10, NE-5).
   - `IndexTelemetry.Note(home, "cli", "finished", identity)`
   - print the report through `RepoCommand`'s own renderer
4. On an exception from one repo: emit `"failed"` for that identity, print the error, **continue to
   the next repo**, and exit **1** at the end. One unreadable checkout must not abandon the other
   nine — the same shape as `backup replay`'s "what it cannot write, it skips and counts".
5. On a lock skip (§6.4): count it, print the note naming the holder, and exit **1** at the end.
   This is a **commanded** surface, so a skip is never silent.
6. Print a trailing summary: serviced / skipped-absent / skipped-locked / failed, and the count of
   truncated scans.

### 4.4 D53 compliance

Inherited, not re-implemented. Every scan goes through `CodeIndexer.Index`, so a truncated scan
already (a) derives no deletions and (b) does not stamp `last_full_scan_at` (`CodeIndexer.cs:140-152`).
The one addition item 2 owes is **saying so**: a repo whose scan truncated is reported as
`partial — not marked scanned` in the summary, so a user who runs the command twice and sees "never"
again knows why. Silence there would read as the command not working.

### 4.5 Ordering divergence from item 1, stated on purpose

`repo index --all` services everything **due** (>60 min), while `doctor` warns about everything
**neglected** (>7 days). So the command does strictly more than doctor complained about. That is
correct — catching up is catching up — and the summary line says how many repos were serviced, so
the difference is visible rather than surprising.

---

## 5. Item 3 — bounded self-heal on the session-start child

### 5.1 What the gap actually is

Not just "the enrollment spawn died". Because `DrainOtherEnrolledRoots` passes
`AllowFullScanDue: false` (`IndexCommand.cs:213`), **a repo is only ever full-scanned by a session
started inside it** (or by a hand-typed `engram index`). Enroll a repo, never open a session there
again, and it stays at `never` forever even with `auto_index_on_session_start = true` and a healthy
spawn. Item 3 must supply the bound that flag was standing in for.

### 5.2 Mechanism: a new narrow flag

**New flag `--freshen` on `engram index`**, parsed at `IndexCommand.cs:20-52` beside the existing
five. It is mutually exclusive with `--drain` / `--drain-all` / `--full` / a positional target
(error, exit 1) — it selects its own root and takes no other work. It accepts `--skip <root>`
(repeatable), which feeds `NextDue`'s `exclude` set; see §5.4.

`engram index --freshen --apply`:

1. Requires `--apply`; without it, dry-run prints the candidate it *would* scan and exits 0.
2. Silently exits 0 if the store file is absent or `home.ConfigPath` is absent — the same
   silent-refusal discipline the `--auto` gate uses, since this also runs inside a detached child
   where a refusal is not an error anyone can see.
3. Opens `EngramDatabase.OpenInitialized`.
4. ```csharp
   var candidate = RepoFreshness.NextDue(
       connection, IndexingSettings.FullScanIntervalMinutes, now,
       includeAmbient: settings.AutoIndexOnSessionStart,
       exclude: skipped);
   ```
5. If null, exit 0 silently.
6. Otherwise `Note started` → `RepoIndexRun.Freshen(...)` → `Note finished` (or `failed`). A lock
   skip (§6.4) is silent here: this is an **ambient** surface, and the next session start retries.

**At most one repo per session start.** That is the bound, and it is the whole design. It converges —
N sessions heal N neglected repos, most-neglected first — while costing O(1) scans per session
regardless of how many repos are enrolled. This directly answers the objection the existing docstring
raises ("unbounded in the number of repos enrolled") without giving up on healing.

**Rejected: spelling this `index --freshen --apply --auto` to reuse the existing gate.** This is the
obvious consistency refactor and it is wrong. `--auto` at `IndexCommand.cs:73` is a *compound* gate:
D67 records that its conjuncts include the cwd being a verified checkout. `--freshen` selects its
root from the database precisely so it can run when the session started somewhere else — which is
*the scenario item 3 exists for* — so inheriting the cwd conjunct would make it a no-op in exactly
that case. `--freshen` therefore reads `settings.AutoIndexOnSessionStart` itself and carries no
`--auto`. D67 also forbids re-adding such conjuncts "for safety"; this is that rule applied to a new
call site.

Consequence: `--freshen` is the **ambient** selector, always gated. The **commanded** catch-up
surface is `engram repo index --all --apply` (item 2), which is ungated. Two surfaces, one gate
between them, no second knob.

### 5.3 The `--auto` interaction — RESOLVED (see §9, OQ-1)

D67: *"`--auto` gates ambient work and may not gate commanded work."* `auto_index_on_session_start`
answers *may Engram index on its own* — the sentence is in the code at `MaintenanceLauncher.cs:115`.

`includeAmbient: settings.AutoIndexOnSessionStart` implements exactly that, and the split falls on
`FreshnessReason`:

| `last_full_scan_at` | `source` | Reason | Runs with the setting **off**? |
|---|---|---|---|
| NULL | `user` | `UnfulfilledEnrollment` | **Yes** |
| NULL | `backfill` | `NeverScanned` | No |
| stale | either | `Stale` | No |

**Why `UnfulfilledEnrollment` bypasses the setting.** This is not a new exemption. Engram *already*
performs a full index with the setting off when the user types `engram repo enroll`:
`RepoCommand.ApplyDecision` calls `TrySpawnFirstIndex` unconditionally at `:177` — there is no
`AutoIndexOnSessionStart` reference anywhere in that file — and `MaintenanceJobs.EnrollmentIndex`
carries neither `--auto` nor `--full` (`MaintenanceLauncher.cs:97-99`), which D67 records as the
deliberate fix to the exact inverse defect ("with the setting off, `engram repo enroll` announced a
background index and performed none"). The existing behaviour is guarded by
`MaintenanceLauncherTests.EnrollmentIndex_ContainsTheIndexJob_WithNoAutoAndNoFull` (`:95`).

So the bypass is **the retry of an existing commanded action**, not a new one. And Engram has already
made the promise out loud: `RepoCommand.cs:57` prints *"The first index is running in the background;
'engram repo list' will show its progress"*, and `EngramMcpTools.cs:422-425` prints the same from the
MCP surface — both unconditionally. `engram repo list` (`:243`) then says `last full scan: never`.
Declining the bypass leaves Engram having printed a false statement, with the user's own `repo list`
standing as the evidence against it.

Note what the *failure* branch already says, because it bounds what this fixes: when the spawn
throws, `:61-62` tells the user to run `engram index --apply --full {root}` by hand. A **detected**
spawn failure therefore already has a remedy the user was told about. The gap this closes is the
**undetected** one — the fork succeeded, the child died, and nothing was printed at all.

**Why `source = 'user'` is the necessary restriction.** The v6→v7 migration backfilled every
`repo_registry` row with a non-NULL `disk_path` to `state='enrolled', source='backfill',
last_full_scan_at=NULL`. D67 is explicit that the backfill "emits nothing" and that `source`
"distinguishes the inference from a real answer, so 'why are forty repos being scanned' stays
answerable." Nobody was promised anything for those rows. Without the `source` filter, the bypass
would full-scan every previously-registered repo on a machine where the user explicitly turned
indexing off — which is the original D67 defect inverted. (`repo list` surfaces `source` on its state
line at `:241`, so a user can see which of their repos this applies to.)

**Two properties that bound the bypass, and both must be stated in the code:**

- **It fires at most once per repo, ever.** The moment a run stamps `last_full_scan_at`, the row
  stops being `UnfulfilledEnrollment` and becomes `Stale`, which *is* gated. So this is a
  one-shot retry of a specific announced action, not a standing exemption from the setting.
- **With the setting off, `source = 'user'` provably means a human typed it.** D67: with the setting
  off the model never offers enrollment, so the only route to the verb is a person.

`DefaultAutoIndexOnSessionStart = true` (`IndexingSettings.cs:33`), so `false` is always an explicit
opt-out — which is the strongest argument *against* the bypass and is why it is narrowed to a
one-shot retry of an announced promise rather than left as a general exemption.

### 5.4 Launcher wiring

`MaintenanceLauncher.BuildScript`, `MaintenanceJobs.SessionStart` only, gains one job:

```
index --freshen --apply --skip <indexRoot>
```

placed **after** the existing `index --drain-all --apply --auto`. Two things are load-bearing:

- **Ordering.** The `--auto` job full-scans the invoked root when due and stamps it, so by the time
  `--freshen` runs the invoked root is normally no longer a candidate. There is no concurrency to
  reason about — the launcher builds `{ a; b; c; }`, one shell, sequential.
- **`--skip <indexRoot>` is not redundant with that ordering, and this is a correction to the
  draft.** The stamp only happens when the setting is on *and* the scan completed (`CodeIndexer.cs:140-152`).
  With the setting off, or after a **truncated** scan, the invoked root is still a candidate and
  `--freshen` would scan it a second time in the same session start — repeating the most expensive
  scan on exactly the largest repos, forever, with neither run ever stamping. The launcher already
  substitutes `indexRoot` into the drain-all invocation, so it has the value to pass.

`MaintenanceJobs.EnrollmentIndex` is **not** changed. The enroll-time spawn stays
`index --drain --apply <root>` with neither `--auto` nor `--full`, for the reason D67 gives and
`MaintenanceLauncherTests:95` / `:111` guard.

### 5.5 Hook budget

Zero new synchronous work in the hook. `HookCommand.cs:428-431` already spawns the launcher
unconditionally; this adds a job inside the already-detached shell. `Redirect` (`:131`) already
`exec`s the shell's own descriptors to `/dev/null` *before* any job — the placement
`MaintenanceLauncherTests:27` guards — so the new job cannot hold the primer pipe open. That guard's
continued passing with the new job present is itself part of the test plan (§7).

Note the tension worth naming: `MaintenanceLauncher.cs:121` describes `SessionStart` as "the ambient
session-start fork", and `--freshen` puts one non-ambient case (§5.3) inside it. That is consistent —
the *fork* was never gated, only the `--auto` job inside it was, and the launcher already takes a jobs
discriminator — but the docstring should be amended so the next reader is not misled.

Latency is nonetheless a **NEEDS-EVIDENCE** item (§10, NE-2): this repo has a documented history of
hook regressions visible only at tier 3, and a documented trap where timing through a pipe measures
the detached child rather than the hook.

---

## 6. Item 4 — the deferred D67 background freshness service

### 6.1 Process model: inside the MCP server, not its own process

`IndexFreshnessService : BackgroundService`, new file `src/Engram.Cli/IndexFreshnessService.cs`,
registered in `src/Engram.Cli/ServeCommand.cs` beside `EmbeddingBacklogService` (`:102`) and
`WebhookService` (`:108`), with a matching `AddFilter` beside `:78`/`:83` — without it,
`SetMinimumLevel(LogLevel.Warning)` drops every line the loop writes, which is the defect D54 records
for the backlog.

A separate daemon is rejected: it needs a second pid file, a second liveness story, and a second
instance of the identity problem D42 exists to solve — for a loop that needs the same home, the same
config and the same DB the server already has.

The work loop itself lives in `Engram.Core` (`IndexFreshness`, mirroring how `EmbeddingBacklog` sits
in Core while `EmbeddingBacklogService` in Cli is the thin hosted wrapper), so it is testable at tier
2 without hosting.

### 6.2 Discovery: poll `repo_enrollment`, not the spool queue

The queue only ever sees edits the `PostToolUse` hook observed — which is the exact defect D67 was
written to fix (a `git pull`, a rebase or a branch switch never enters it). Freshness is a *time*
question and `last_full_scan_at` is the time record. So: poll, and ask
`RepoFreshness.NextDue(..., includeAmbient: true, exclude: [])` — the **same** policy function items
2 and 3 use. The poll interval is not a second freshness policy; the policy decides work, the
interval only decides how often it is asked.

Proposed interval: 5 minutes (`IndexFreshness.PollInterval`), pending NE-4. The rule for choosing it:
**nothing is waiting on this loop.** A newly enrolled repo already gets its own spawn, and a repo a
session touches already gets item 3; the service exists only for the tail nothing else reaches. So
pick the *largest* interval that keeps tail latency tolerable, not the smallest one that feels
responsive.

### 6.3 Bounding: one repo per tick

Same bound and same reasoning as §5.2. A tick that services every due repo is an unbounded pass
wearing a bounded costume; one-per-tick makes worst-case work per unit time a constant, and the
most-neglected-first ordering makes it converge. Per-repo, `ScanBudget.Default` applies via
`CodeIndexer`, unchanged.

### 6.4 Coordination — the per-identity index lock

Today two processes can already full-scan one repo at once (two panes opening at the same second,
plus a hand-typed `engram index --apply`). A continuous ticker raises that from rare to routine, so
item 4 needs the lock the current design has been living without.

**NE-1 has now measured what today's unprotected race actually does.** Summary for a reader of this
section, with the full result in §10 and the ruling in §11: the race is real and produces a redundant
write-then-immediate-supersede cycle; the store's one-live-fact-per-subject-and-predicate invariant
(`ux_fact_live`) resolves it to the correct content, so the live fact set is right in every observed
outcome. `IndexLock` therefore remains a commit-E prerequisite for item 4 rather than a preemptive
bug fix, and what it removes is the redundant closed row.

**`IndexLock`**, new file `src/Engram.Core/IndexLock.cs`. A per-identity lock file, not a schema
change:

- Path: `Path.Combine(home.Root, "locks", SHA256(identity).ToHexLower() + ".lock")` — via a new
  `EngramHome.IndexLockDir`, since `EngramHome` is the one home resolver and nothing else may compose
  home paths.
- Claim: `new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)`. `CreateNew` is
  `O_EXCL`, so the claim is atomic across processes without a transaction.
- Content: the owner's `ProcessStartToken.ForSelf()` (`ProcessStartToken.cs:44` — pid plus the
  kernel's start token, the one thing a recycled pid cannot forge), plus identity and an ISO start
  time.
- Release: delete on dispose, in a `finally`.
- Stale reaping: if `CreateNew` fails, read the file. If it is unparseable, or its token does not
  match `ProcessStartToken.ForPid(pid)` (`:47`), delete it and retry the claim **exactly once**. A
  crashed holder therefore self-releases with nobody reaping, and a live holder is never stolen from.
  **No timeout-based reaping and no tolerance window** — D42 is explicit that softening a
  process-identity comparison with a window is how a healthy server gets declared dead. `doctor` is
  the one reader that may *not* reap (§3.2).

**Placement: inside `CodeIndexer.Index`**, immediately after `identity` is resolved and **before the
`last_root` repair at `CodeIndexer.cs:88-92`**, which is a write; released in a `finally` at the end
of `Index`. Putting it at the call sites means the next caller forgets; putting it here is the
one-implementation answer and covers `engram index`, `--drain-all`'s secondary loop, and items 2, 3
and 4 at once.

**That placement is also what makes the lock a fix rather than a mask, and §12.4's withdrawal turns
on it.** Claiming before `:88` and releasing at the end of `Index` puts the *entire* read-modify-write
cycle — the `file_state` snapshot, the scan, and the write pass — inside one critical section per repo
identity. NE-1's observed interleaving is a stale snapshot (both processes read `file_state` before
either wrote), and a lock spanning only the write would leave that window open and hide the symptom
without closing the race. This one closes the window. **Confirm the structural premise before relying
on that argument** — confirmed; §12.2, item 8.

**Claimed only when `options.Apply` is true.** A dry run writes nothing, so it must neither block nor
be blocked. The cost is that a preview taken during a concurrent apply may be slightly stale; the
alternative is a preview that refuses, which is worse.

**Contention never waits; only the reporting differs.** No timeout, no queueing — a wait is a number,
and D4's `busy_timeout` lesson is what waiting costs on a budgeted path. The blocked caller gets an
`IndexReport` with zero counts and the note
`"skipped: another process is indexing this repo (pid N, since <time>)"`. Then:

| Caller | Kind | On contention |
|---|---|---|
| `engram index --apply` | commanded | prints the note, exits non-zero |
| `engram repo index --all --apply` | commanded | counts it, prints the note, exits 1 at the end (§4.3 step 5) |
| `index --freshen --apply` | ambient | silent; next session start retries |
| `--drain-all` secondary loop | ambient | silent; next session start retries |
| `IndexFreshnessService` tick | ambient | silent; next tick picks another candidate |

The rule behind the table: **a command someone typed never silently no-ops.** The draft's symmetric
"skip and say so" would have made a hand-typed `engram index --apply` exit 0 having done nothing,
which reads as success.

### 6.5 Failing safe when the server is down

Item 4 is **purely additive**. Items 1–3 need no server. D67's acceptance property — the end-to-end
guard that enrolls, edits a file out of band, starts a session and asserts the change is indexed
**with no server at any point** — must pass **unmodified**, and this spec forbids changing it. A
second guard is added asserting the same scenario *with* the server running reaches the same end
state, so the service cannot quietly become the only path.

### 6.6 Config: a new key, default off

`[indexing] auto_index_in_background`, read in `IndexingSettings.Read` (`IndexingSettings.cs:174`,
beside `auto_index_on_session_start`), added to `DefaultConfig.cs`'s `[indexing]` block (`:56-57`)
with prose explaining the pair.

- **Not folded into `auto_index_on_session_start`.** That key's meaning was pinned by a whole D67
  paragraph and by `MaintenanceLauncher.cs:115`, and it names *session start* in the key itself.
  Widening it to cover a continuous loop changes what an existing `true` consents to — the same shape
  D33 forbids for retired keys, applied to a live one. Someone who enabled indexing at session start
  did not thereby ask for a permanent background walker.
- **Name.** `auto_index_in_background` reads as a sibling of `auto_index_on_session_start`; seeing
  both together tells a reader immediately that they are two cadences of one thing.
- **Default `false`** — settled, see §9 OQ-2.
- Existing configs will not contain the key, so they resolve to `false` via
  `config.Bool(...) ?? Default`. That is the correct outcome and needs no `ConfigEditor` work; it does
  mean the key is invisible to existing users, which §3.3's last bullet is the mitigation for.
- `IndexingSettings.Retired` (D67's `max_sync_index_ms`) is untouched. Nothing here retires or
  repurposes an existing key.

### 6.7 Not dry-run-first, and why that is consistent with D49

D49's rule is about *commands a user types*, where the dry run is the preview before consent. Consent
here was given twice already — enrolling the repo, and turning `auto_index_in_background` on — and
there is no surface on which a background service could present a preview or receive an answer for
it. A service that only ever dry-runs is not a service, it is a log.

What replaces the preview is **auditability after the fact**: `started`/`finished`/`failed` telemetry
per run (D55), plus the status note below.

### 6.8 `indexing.json` — the `embedding.json` pattern, third instance

New `IndexProgress` in `src/Engram.Core/IndexProgress.cs`, `EngramHome.IndexProgressPath =
Path.Combine(root, "indexing.json")`, mirroring `EmbeddingProgressPath` (`EngramHome.cs:57`, `:98`)
and `metal.json` before it (D42).

Following D54's rules, which were paid for in a live run:

- **The database owns counts; the note owns liveness.** The note carries: last tick time, current repo
  identity or null, started-at, `ProcessStartToken`, and an outcome string. It does **not** duplicate
  "how many repos are due" — the store answers that correctly whether or not a server is up, and a
  second answer goes stale exactly when someone is reading it.
- **A service that declines records why.** `auto_index_in_background = false` writes an explicit
  `Unavailable` note naming the setting, rather than writing nothing — D54's measured lesson that
  "the reason a number is not moving is the answer".
- **A standing statement is not a heartbeat.** `Unavailable` is excluded from any `LooksLive`
  computation, so a precise reason never ages into "stalled or stopped".
- Cleared on `ApplicationStopping` beside the pid file, with the same ownership test
  (`ServeCommand.cs:152` is the model). A service that declined never enters its loop and so never
  reaches the loop's own cleanup — which is the exact case D54 records.

### 6.9 Interaction with the other three mechanisms

| Concurrent pair | What happens |
|---|---|
| service tick vs. `repo index --all --apply` | `IndexLock` — whoever claims first proceeds; the commanded side prints the skip and exits 1, the tick moves on silently |
| service tick vs. session-start `--freshen` | same lock; both ambient, both silent |
| service tick vs. `--drain-all` secondary loop | same lock; the secondary loop does not full-scan anyway, so at most one incremental drain is skipped and the next session start redoes it |
| service tick vs. itself | in-process; the loop is sequential and awaits its own tick |
| service tick that finds nothing due | no work, no queue interaction. D67 anticipated exactly this: it is the case its "pathless entries are skipped unconditionally" rule in `DiscardExcept` was written for — on a pass where no root escalates, an unskipped watermark would be deleted with nothing having scanned for it. Because `RepoIndexRun.Freshen` never drains, item 4 never calls `DiscardExcept` at all, so the hazard is avoided by construction rather than by care. |

---

## 7. Test plan, by tier

Repo rule throughout: **a guard that cannot fail is worthless — prove each one fails by breaking what
it guards, then restore.** Falsify against a **committed** tree and assert the patch landed
(`git diff --quiet`), per D60: a harness that restores arms with `git checkout --` reverts an
uncommitted change under test, and a pattern spelling `·` as a bare `.` silently no-ops.

### Commit A — `RepoFreshness` / `RepoIndexRun` / `IndexTelemetry`

- **Tier 1** (`Engram.Core.Tests/RepoFreshnessTests.cs`): the ordering contract. NULL stamps before
  stamped; oldest `decided_at` within NULLs; oldest `last_full_scan_at` within the rest; identity
  breaks a total tie. Assert the reason grid across the **2×2** of `last_full_scan_at` × `source`
  that `ClassifyDueReason` can actually be reached with. The third column — a freshly-stamped row —
  is vacuous rather than omitted: `IsFullScanDue` never lets one reach `ClassifyDueReason`. Pin
  *that* instead, with one assertion that a row stamped inside the interval is not returned as due,
  so the property making the column vacuous fails visibly if it ever stops holding.
- **Tier 2** (`Engram.Integration.Tests/RepoFreshnessTests.cs`): `Due` / `NextDue` / `Neglected`
  against a real store with real `repo_enrollment` rows; declined and deferred rows excluded; a row
  whose `last_root` does not exist on disk excluded; a root in `exclude` excluded.
- **Tier 2**: `IndexTelemetry` still emits what `ActivityEventsTests:48` and `:78` assert. Those two
  tests must pass **unmodified**.
- **Tier 3** (`Engram.EndToEnd.Tests/ConcurrentIndexConvergenceTests.cs`) — **moved forward from
  commit E by the NE-1 amendment.** Two published-binary `engram index --apply --full` runs against
  one repo, started together.

  It lands here rather than at E because it pins **the property that makes NE-1's finding
  low-severity** (§11). The ruling that `IndexLock` may wait for E rests entirely on supersession
  absorbing the race correctly; if that ever stops holding, the severity changes and E's position must
  be revisited. Pinning the reason a bug is tolerable is worth more than pinning the bug.

  **Convergence guard.** After the concurrent indexing run, assert in every home that (i)
  `ux_fact_live` exists in `sqlite_master` with its `WHERE valid_to IS NULL` predicate intact, and (ii)
  `SELECT subject_id, predicate FROM fact WHERE valid_to IS NULL GROUP BY subject_id, predicate HAVING
  count(*) > 1` returns no rows. These pin the property that makes the indexing race tolerable — the
  index absorbs it into a closed row and leaves the live set correct — against removal and against
  bypass respectively, which are distinct failure modes.

  Do **not** assert live-set equality between the concurrent and serial arms. It cannot fail:
  measured, it stays green with `ux_fact_live` dropped and `FactStore.Remember` patched to skip
  closing, because a deterministic patch to shared code lands on both arms and two identically-broken
  stores are still equal to each other. Only an asymmetric perturbation can move a cross-arm equality,
  and staleness at the change-detection layer is not one — it causes duplication, never omission,
  which `ux_fact_live` then absorbs. Cross-arm comparison belongs at commit E, on closed counts, where
  NE-1 recorded the concurrent arm at 81 closed facts against the serial arm's 80 — a comparison that,
  unlike the live sets, can genuinely separate the two sides. It separates them only when the race
  actually fires: NE-1's second home pair came back 80 against 80. So removing the lock is a
  falsification whose *green* result proves nothing, and commit E must state how many trials a
  negative arm needs before it counts as one — a question for whoever writes commit E, deliberately
  not settled here.

  Measured on the harness: both arms scan the same on-disk repo and issue exactly two `Remember`
  calls per subject and predicate, so they differ only in interleaving and never in work performed —
  there is nothing for a deterministic patch to separate.

  Falsify by dropping `ux_fact_live` and patching `FactStore.Remember` to skip closing the prior live
  fact. Both assertions must go red on themselves, naming the missing index and the duplicate live
  rows — not on an exception. Start from NE-1's preserved harness (`/tmp/engram-ne1-run.sh`) rather
  than rebuilding the scenario — it already handles the part that is easy to get wrong, namely waiting
  out each home's auto-spawned enrollment index so it cannot become an uncontrolled third writer.

### Commit B — `engram repo index --all`

- **Tier 2** (`Engram.Integration.Tests/RepoCommandTests.cs`, extending the existing file):
  - selection: three enrolled repos, stamps NULL / 2 h old / 5 min old → exactly the first two are
    serviced, in that order.
  - a repo whose `last_root` is deleted from disk → the row is never selected:
    `RepoFreshness.IsSelectable` excludes it before `Due()` returns, so the candidate list comes
    back without it and the run exits 0 having serviced the rest. Assert *that*, not the
    `skipped-absent` counter, which this setup cannot reach.
    **`IndexAll`'s own `Directory.Exists` check is not thereby redundant and must not be removed as
    dead code.** `Due()` snapshots the candidate list once, and the loop then runs a full scan per
    repo — so for the Kth candidate the gap between being judged selectable and having its root
    used is the sum of the scan times of every candidate ahead of it, bounded only by how long
    those take (§10, NE-3). `IsSelectable` guards selection; `Directory.Exists` guards use; a batch
    loop is precisely the shape that pulls the two apart. `skipped-absent` (§4.3 step 6) therefore
    reads zero on every ordinary run without being dead — it counts the repos that vanish *during*
    one.
    Once commit E2 (§13) lands, this same vanish-during-run window produces a **truncated scan** rather
    than a wholesale deletion, so the two guards are layered rather than alternative: `Directory.Exists`
    keeps the repo out of the loop when it can, and a truthful `Truncated` makes the outcome harmless
    when it cannot. Neither makes the other removable.
  - one repo throwing → `failed` telemetry for it, the others still serviced, exit 1.
    **Ships uncovered, deliberately.** `RepoIndexRun.Freshen` cannot be made to throw from outside:
    every filesystem boundary beneath it degrades rather than raising — `GitFileLister.List`
    returns `null` for a directory that is not a checkout or has vanished, `RepoScanner.Walk`
    catches `IOException`/`UnauthorizedAccessException` per directory and continues, and
    `PathCanonicalizer.Canonical` returns rather than throwing at its depth bound. A corrupted
    `.git`, a `chmod 000` subtree and a NUL byte in a path were each tried, and each degraded well
    before reaching the call. This is not a testability problem to engineer around: the `catch` in
    `RepoCommand.IndexAll` is a backstop for *unanticipated* failures, and a backstop for the
    unanticipated cannot be exercised by anything anticipated.
    **Do not open a production seam to reach it.** An injection point existing only so five lines
    of control flow can be asserted buys a property that is already fully visible on inspection,
    and pays permanent indirection on the batch path for it.
  - **truncated scan**: with a `ScanBudget` forced tiny, assert `last_full_scan_at` is **still NULL**
    afterwards and no deletions were applied. *This is the D53 guard and it is the most important
    test in the commit.* Falsify by removing the `if (scan.Truncated)` branch in `CodeIndexer`.
  - **exit codes**: a missing `--all` exits **2**. **The exit-1 arm has no reachable trigger at
    this commit** — the throw is unreachable (above) and lock skips do not exist until commit E,
    which is where it should be asserted, on a skip that can actually be provoked. Do not
    manufacture the 1 here: a test that reaches it by stubbing the only thing that produces it is
    asserting the stub. The file's existing split between the two (`:23`, `:44`, `:226`, `:317`)
    is easy to break by accident, and a test that asserts only "non-zero" cannot catch it.
- **Tier 3** (`Engram.EndToEnd.Tests`): `engram repo index --all` with **no** `--apply` writes
  nothing — snapshot every file in the home by size and mtime around the run. Per D49 this is the
  load-bearing half. Also: bare `engram repo index` exits **2** with a usage line that lists `index`
  among the subcommands.
  Set `ENGRAM_HOME` or pass `--home` on every published-binary invocation.

### Commit C — `doctor`

- **Tier 2**: seeded enrollment states → exact `Diagnosis` rows. Must include the grace boundary: a
  NULL-stamped row decided 5 minutes ago is `Ok`, the same row decided 2 hours ago is `Warn`. And a
  repo at 90 minutes since its last scan — *due* but not *neglected* — is `Ok`. That last one is what
  stops someone "simplifying" the two predicates into one.
- **Tier 2**: the `Fix` string is byte-identical to the command item 2 actually accepts. A test that
  asserts a hand-written string proves nothing; assert it **parses**, by feeding it to the arg parser.
- **Tier 2** (only if E has landed): a live lock for an otherwise-neglected identity suppresses the
  row; a lock naming a dead pid does not; and **the lock file still exists after the `doctor` run**.
  That last assertion is the one that catches a reaping `doctor`.
- **Tier 3**: `doctor` on a home with a neglected repo exits **0**, and the existing file-snapshot
  read-only guard is **extended** to that home rather than left on a home where the new check returns
  `Ok` — otherwise the guard never exercises the new code.

### Commit D — `--freshen` and the launcher job

- **Tier 1** (`MaintenanceLauncherTests`): `SessionStart` contains `index --freshen --apply --skip`,
  and it appears **after** `index --drain-all --apply --auto`. Falsify by swapping the order.
  `EnrollmentIndex` still contains neither (`:95`, `:111` must pass unmodified). `:27`
  (`TheShellsOwnDescriptorsAreReplaced_BeforeItRunsAnything`) must still pass — the new job must not
  precede `Redirect`.
- **Tier 2** — **the truth table, exhaustively**, because this is where the contested decision lives:

  | `auto_index_on_session_start` | stamp | `source` | scanned? |
  |---|---|---|---|
  | true | NULL | user | yes |
  | true | NULL | backfill | yes |
  | true | stale | either | yes |
  | **false** | **NULL** | **user** | **yes** |
  | false | NULL | backfill | **no** |
  | false | stale | either | **no** |

  **Falsification discipline:** deleting the `UnfulfilledEnrollment` bypass must redden row four and
  **must not** redden rows five or six. If deleting the bypass reddens rows that should be `no`, the
  policy has been copied into the caller rather than called. Conversely, deleting the `source =
  'user'` filter must redden row five alone.
- **Tier 2** — the one-shot property, which is what bounds the bypass (§5.3): with the setting
  **off**, run `--freshen` twice against one `UnfulfilledEnrollment` repo and assert the second run
  selects nothing. Falsify by making the run not stamp.
- **Tier 2** — `--skip`: with the setting off and the invoked root an `UnfulfilledEnrollment`, assert
  `--skip <invokedRoot>` selects a *different* repo. Falsify by dropping the flag from the launcher
  and asserting the same root is chosen twice.
- **Tier 2**: at most one repo per invocation. Seed **three** due repos and assert exactly one is
  scanned. Sizing matters: with one due repo, "one per run" and "all per run" are indistinguishable
  and the test passes with the bound deleted — the same trap D55 documented at 12 facts / `MaxBatch` 4.
- **Tier 3** (`HookSessionStartTests`): enroll a repo, blow away its `last_full_scan_at` to simulate
  the dead spawn, start a session **in a different directory**, and assert the repo gets indexed. This
  is the guard for the feature's *absence*, and it fails today for the structural reason in §5.1.
- **Tier 3, latency**: session-start p50 before/after, **timed through a file, never a pipe**. See
  NE-2 — a pipe measures the detached child, and this repo has a withdrawn measurement from exactly
  that mistake.

### Commit E — `IndexLock`

- **Tier 2**: two claims on one identity — the second gets the skip note, not an exception, and the
  first's report is unaffected. A lock file whose `ProcessStartToken` names a dead process is reaped
  and the claim succeeds. A lock file naming a **live** process is never stolen. A dry run neither
  claims nor is blocked.
- **Tier 2**: the commanded/ambient reporting split (§6.4's table) — a blocked commanded caller exits
  non-zero, a blocked ambient caller exits 0 and prints nothing. Falsify by collapsing them to one
  behaviour; both arms must redden.
- **Tier 3** — **extend** commit A's `ConcurrentIndexConvergenceTests` rather than writing a second
  one, with the assertion the lock exists *for*: the concurrent run's **closed**-fact count now equals
  the serial run's, and no fact is written and superseded within one run by a sibling process.

  **This assertion's falsification has already been performed** — it is NE-1's measured result (81
  concurrent against 80 serial on today's code, §10), so the assertion is known to redden without the
  lock and is expected to go green with it. Nothing needs to be staged. If it does *not* redden when
  run against a pre-E tree, the race did not reproduce in that environment and the test is not yet
  meaningful — say so rather than accepting the green.
- **Regression sweep**: every existing index test must pass unmodified. NE-6 asks whether any of them
  index one repo concurrently.

### Commit E2 — RepoScanner truncation on an unreadable directory

- **Tier 2** (`Engram.Integration.Tests`, beside the existing scanner tests):
  - **deleted root, git path**: an enrolled repo whose root is removed → `GitFileLister.List` returns
    null, `Walk` runs, the scan comes back `Truncated`, **no deletions are applied**, and
    `last_full_scan_at` is still NULL. This is the case that set the commit's priority and it needs no
    permissions to stage. Falsify by reverting the catch's write to `stop`.
  - **summary text, not just the flag**: assert the rendered summary **names** the unreadable
    directory and its count. Falsify by deleting the new arm from `Summary()`'s stop switch — if that
    switch has a `_` default, this is the only assertion in the commit that reddens (§13.3).
  - **stop precedence**: a walk that skips an unreadable directory *and then* exhausts the time budget
    reports the time budget as its stop while still naming the skipped directory in the summary
    (ruling C). Falsify by making the new branch's assignment unconditional.
  - **layer 2**: an existing, empty, readable root with facts already indexed → deletions skipped, the
    note names the condition, and `last_full_scan_at` is **not** stamped (ruling D). Fully portable —
    no permissions needed, which is the point of staging the mountpoint case this way. Falsify by
    removing the `else if`.
  - **layer 2 negative**: empty root, nothing previously indexed → no note, ordinary empty result. This
    is the `states is non-empty` clause and it is the half that stops the guard firing on every new repo.
  - **mid-walk unreadable subdirectory** (`chmod 000`): the only test here needing permissions. **Skip
    explicitly on Windows and when running as root** — root ignores permission bits, so the test does
    not fail, it silently stops exercising anything, which is the tier-3 skip trap in a different
    costume. Assert inside the test that the enumeration actually failed, or skip.
- **Tier 3**: none required. Every behaviour here is reachable at tier 2 and the published-binary
  surface is unchanged.

### Commit F — `IndexFreshnessService`

- **Tier 2**: the loop's selection and its one-repo-per-tick bound, with ≥2 due repos (same sizing
  argument as above). Use a **real barrier** — wait for the startup log line, not `StartAsync`, which
  promises only that `ExecuteAsync` reached the scheduler; D55 records this failing under load and
  passing in isolation, looking exactly like a broken feature.
- **Tier 2**: `auto_index_in_background = false` → the service writes an `Unavailable` `indexing.json`
  naming the setting and does no work. `Unavailable` is excluded from `LooksLive`.
- **Tier 3**: **D67's existing no-server end-to-end guard passes unmodified.** Then a new sibling: the
  same scenario with the server up reaches the same end state — no double-index, no extra facts.
- **Tier 3**: `indexing.json` is absent after a clean `engram stop`.
- **No test may assert a total line count in `telemetry.jsonl`.** The service now writes into the same
  shared log; D55 and D56 both record tests broken by exactly this. Filter by kind.

**Read the skip count, not just the pass count.** Tier 3 evaporates into the skip column without a
published binary while the summary still reads `Passed!`.

---

## 8. Sequencing

> **Amendment.** This section previously carried a conditional re-ordering driven by NE-1. NE-1 has
> run and the ruling (§11) leaves `IndexLock` at E, so the order below is final and the conditional is
> removed.

Seven commits. The order is chosen so that (a) no commit names a command that does not yet exist,
(b) the two commits touching already-shipped hot paths land alone and are bisectable, and (c) the
riskiest change is not entangled with the newest feature.

| # | Commit | Why here |
|---|---|---|
| **A** | `RepoFreshness` + `RepoIndexRun` + `IndexTelemetry` move, **plus the concurrency-convergence guard** | Pure addition plus one behaviour-preserving move. Nothing consumes it yet, so it is trivially revertible. The guard rides here because it pins a property of *today's* code (§7, §11), not of anything this spec adds. |
| **B** | `engram repo index --all [--apply]` | First user-visible remedy. Synchronous, user-invoked, no hook and no background path. |
| **C** | `doctor` warns | **After B**, so the `Fix` string names a command that exists. Read-only, zero behaviour change. |
| **D** | `index --freshen` + `--skip` + launcher job | Touches the session-start path. Lands **alone** so a bisect on a hook-latency regression finds it unambiguously — this repo's history says that class of regression is only visible at tier 3. |
| **E** | `IndexLock` | Touches the hot path of *everything already shipped*. Lands alone, and **before** F, so if it breaks something the bisect is not confused by a new background service. Its own tier-3 assertion extends A's guard. |
| **E2** | RepoScanner truncation on an unreadable directory (§13) | Must precede F: F is what runs `--all` unattended on a timer, which is what turns this from a hazard needing a coincidence into one sampled continuously. It does **not** need to block commit B — B only widens a window that already exists — though landing it first is cheap if B has not merged. |
| **F** | `IndexFreshnessService` + `auto_index_in_background` + `indexing.json` | Purely additive behind a default-off flag. Last, because it is the only item that can be shipped disabled and enabled later. |

B and C could be one commit; keeping them apart means a `doctor` regression is never bisected into an
indexing change. D and F are deliberately far apart despite being the same policy — they are the two
paths this repo has historically regressed, and validating them independently is the point.

One consequence of E staying at E: C's lock refinement (§3.2, decision 3) is a follow-up rather than
available immediately, so `EnrollmentGrace` ships as a time-based proxy and narrows later. That is
accepted, and NE-3 is what makes the proxy defensible in the meantime.

---

## 9. Rulings on the three open questions

These were left open by the draft and are settled here, as design authority. Each is an argument, not
a preference. Any of them remains overridable by Jim on product grounds; none of them is blocking.

### OQ-1 — RULED: the `UnfulfilledEnrollment` bypass is correct, and the draft's framing of it was wrong

The draft called it "the only place in Engram where a config-off state does work" and flagged it as
too surprising to settle alone. **That premise is false, and the code says so.**

`RepoCommand.ApplyDecision` calls `TrySpawnFirstIndex` unconditionally (`:177`) — `RepoCommand.cs` was
read end to end and contains no reference to `AutoIndexOnSessionStart` at all — and
`MaintenanceJobs.EnrollmentIndex` deliberately carries neither `--auto` nor `--full`
(`MaintenanceLauncher.cs:97-99`), guarded by `MaintenanceLauncherTests:95`. Engram already performs a
full index with `auto_index_on_session_start = false`, whenever a human types `engram repo enroll`.
D67 records this as the deliberate fix to the inverse defect and states the rule: *"`--auto` gates
ambient work and may not gate commanded work."*

So the bypass is not a new exemption — it is the **retry of an existing one**, and the surprising
state is the current one, where Engram prints *"The first index is running in the background;
'engram repo list' will show its progress"* (`RepoCommand.cs:57`, and identically from the MCP tool at
`EngramMcpTools.cs:422-425`) and then `repo list` reports `never` (`:237`, `:243`), forever, with
nothing anywhere that fixes it.

Three restrictions keep it narrow, and all three must be in the code:

1. `source = 'user'` — backfilled rows were promised nothing (D67: the backfill "emits nothing").
2. `last_full_scan_at IS NULL` — only the *unfulfilled* promise, never a stale repo.
3. **One-shot per repo, ever** — the first successful stamp reclassifies the row as `Stale`, which is
   gated. This is the property that makes it a retry rather than an exemption, and §7 commit D gives
   it its own test.

Counter-argument, recorded because it is the real one: `DefaultAutoIndexOnSessionStart = true`
(`IndexingSettings.cs:33`), so `false` is always an explicit opt-out, and a user who typed it may read
it as "no filesystem walking on its own, full stop." That reading is already violated by the shipped
enroll path, so the choice is between honoring it consistently (which would mean *removing* today's
enroll-time spawn and contradicting D67) or completing what was announced. The second is right.

*If Jim disagrees:* the fallback is clean and costs one line — `includeAmbient:
settings.AutoIndexOnSessionStart` becomes the whole gate, `UnfulfilledEnrollment` loses its bypass,
and the unfulfilled enrollment surfaces through `doctor` (item 1) with `engram repo index --all
--apply` (item 2) as the remedy. Rows four and five of §7's truth table both become `no`. Nothing else
in the spec changes.

### OQ-2 — RULED: `auto_index_in_background` defaults to `false`

Confirming the draft's choice, with the basis stated:

1. **D67's acceptance property.** The feature must work "with the server never started", and the
   no-server end-to-end guard exists explicitly "to stop it from quietly becoming the only path." A
   default-on service makes the server path the de facto path on most installs, which is how a guard
   stays green while the property it protects stops being true in practice.
2. **It is redundant for the population that has sessions.** With items 1–3 shipped, anyone who starts
   sessions is already healed one repo at a time. Item 4 exists for the tail — repos where no session
   ever starts. Defaulting it on runs a continuous walker for everyone to serve a minority, and the
   duplicate work is only invisible because of the lock.
3. **D49's temperament.** Continuous work on a user's machine, started by a config line they never
   wrote.

The cost of `false` is discoverability, and §3.3's last bullet is the mitigation: when `doctor` is
*already* warning about neglected repos and the key is absent from the config, it names the key as an
option. Only when already warning — an unset key is not a fault, and D37 forbids reporting a user's
configuration as one.

### OQ-3 — RULED: `NeglectedAfter = 7 days` stands on an argument; `EnrollmentGrace = 1 hour` needs a measurement, and gets one

The two thresholds are **not the same kind of number**, and that is the ruling.

**`NeglectedAfter = 7 days` — accept, with a basis it did not have.** It is not arbitrary once you
say what it has to be true of. It must be (i) far above `FullScanIntervalMinutes = 60` so `doctor` is
not amber constantly (D37), and (ii) long enough that neglect implies a **broken mechanism** rather
than a lull. With item 3 healing one repo per session start, a week is dozens to hundreds of chances;
a repo still unscanned after that means either nothing heals it or no session ever starts — which is
precisely what the warning should mean. That is an argument, and it survives retuning: if the heal
cadence changes, the threshold moves with it for a stated reason.

**`EnrollmentGrace = 1 hour` — reject as written; keep the number only if a measurement supports it.**
This one is standing in for a fact the system can actually know: *is that index still running?* Its
value must exceed the wall time of one full applied index of the largest enrolled repo, with margin.
So:

- **NE-3 is retargeted** to report the *total wall time of one full `--apply` index run*, not just the
  scan (§10). The scan is bounded by `ScanBudget.Default`; the classification and write passes after
  it are not, and those are what the grace actually has to cover.
- Ship `1 hour` **only if** NE-3 measures that run at ≤ 6 minutes — an order of magnitude of margin.
  Otherwise raise it to ten times the measured worst case, rounded up.
- **Once `IndexLock` exists, the grace stops being the primary evidence.** A held lock proves work is
  in flight and suppresses the row directly (§3.2, decision 3); the grace then covers only the window
  between fork and claim, which is process start — milliseconds. Since E stays at E (§11), that
  refinement is a follow-up to C rather than part of it. `doctor` reads the lock and never reaps it.

**Do not share the constant with `RepoEnrollment.DeferralCooldown`.** Confirmed: both are 7 days
(`RepoEnrollment.cs:36`, surfaced to the user at `RepoCommand.cs:98-99`). They answer different
questions and would move for unrelated reasons — `DeferralCooldown` is a *consent* interval (how long
before re-asking a human who said "not now"), which moves with how irritating re-prompting is;
`NeglectedAfter` is a *diagnostic* threshold, which moves with the heal cadence. Folding them would
let a change to prompt politeness silently retune a diagnostic.

Worth saying plainly: **no test can hold these two apart while the numbers are equal**, and the
numbers must not be fudged to make one writable. The guard is therefore the comment, at both
definitions, each naming the other — which is why §2.1's comment on `NeglectedAfter` is written the
way it is, and why `RepoEnrollment.cs:36` should gain the mirror sentence.

### OQ-4 — carried forward, unresolved and non-blocking

Should `engram repo index --all` accept a per-repo argument? `--all` is required and
`engram repo index <path>` is unimplemented, because `engram index --apply <path>` already covers it.
If Jim would rather `engram repo` be a complete surface on its own, that is a small addition to
commit B — the file already has `TryResolveCheckout` (`:281-294`) for exactly this shape of argument
— and changes nothing else. **This is a product-shape question and is genuinely his**, not a
correctness one.

---

## 10. NEEDS-EVIDENCE — route to the Implementor, do not let me guess

Each item names what to run and what each outcome decides. Every published-binary invocation sets
`ENGRAM_HOME` or passes `--home`; a verification command that omits it litters the real `~/.engram`,
which has already happened once.

### NE-1 — What does a concurrent double-index of one repo actually do? — **DONE**

*Original question:* two arms — **1a** full vs. full (two `engram index --apply --full` started
together), **1b** full vs. `--drain` (the session-start shape) — each diffed against a serial baseline
on the live fact set, the closed fact set, `file_state`, and the stamp.

**Result, measured on the published binary against four disposable `ENGRAM_HOME`s, 80 mutated + 20 new
files, all 8 invocations exiting 0. Each home's auto-spawned enrollment index was waited out first so
it could not become an uncontrolled third writer.**

- **Arm 1a: live fact sets byte-identical** between concurrent and serial (320/320, empty structural
  diff). No content live in the serial run ends up closed-with-nothing-live in the concurrent run.
- **Arm 1a: closed counts differ — 81 concurrent, 80 serial.** Traced to one file
  (`dir0/file100.txt`): both processes independently detected the same mutation and each wrote an
  `about` fact for it. `id301` was written **and** closed within the same second,
  `superseded_by = 345`; `id345`, with a body identical to `301`, ended live. Net: one extra,
  functionally redundant closed fact-version that the serial run does not produce. 1 of 80 changed
  files, in one run.
- **Arm 1b: no divergence at aggregate level** — closed 80 = 80, live 320 = 320, empty structural
  diff. No per-file drill-down was performed.
- Stamp correct in all four homes. **The `file_state` comparison is disowned by its own author** and
  proves nothing either way: it did not exclude the per-row last-updated timestamp, which differs
  between independently-timed processes regardless of correctness.

Raw rows, script and all four homes preserved at `/tmp/engram-ne1-27796` and
`/tmp/engram-ne1-run.sh`. **Do not delete until commit A's guard (§7) is written** — it should start
from that harness rather than rebuilding the scenario.

*What it decided:* see §11. Ruling: `IndexLock` stays at commit E; the convergence guard moves to
commit A; the Ultra-Advisor escalation is withdrawn (§12.4).

### NE-1b — Can this race ever produce the severe case? *(new, from NE-1's result; runs in parallel with A–D, blocks nothing)*

The one question NE-1 left genuinely open, sharpened. **Not** "is the redundant row deterministic per
file" — that is a curiosity and does not change any decision. The question that changes a decision is:

> Across N repeats of arm 1a **and** arm 1b, does any run produce a **live-set** structural diff — a
> body live in the serial baseline with no equivalent live in the concurrent run?

Method: repeat NE-1's existing harness. Report **only** (a) the count of runs with a live-set diff and
the diff itself for any that had one, (b) the distribution of closed-count deltas, and (c) for arm 1b,
one per-file id-level drill-down on any run whose closed counts differ. Do **not** re-report matching
runs in detail. Ten repeats per arm is a reasonable N; say so if the race proves too rare to sample.

Also fix the `file_state` check while the harness is open: compare repo path, file path and content
hash only, **excluding** the last-updated column. As built it neither confirms nor refutes anything.

*Decides:* nothing about A–D, and nothing about E's position unless it comes back positive. A single
live-set divergence means the supersession invariant did **not** absorb the race, the severity
assessment in §11 is wrong, `IndexLock` becomes commit 0, and §12.4's escalation is reinstated with
its original question. Absent that, this is a stronger negative and the ruling stands unchanged.

### NE-2 — Session-start hook latency, before and after commit D

Time `session-start` on the published binary **through a file, never a pipe** — the repo's own records
withdraw a measurement made through a pipe because the detached child held it open. Report p50 at the
instance's live fact count and note the corpus size.
*Decides:* whether the extra job is free (expected: yes, since the shell already forks and the job is
detached) or whether it needs to be conditional. Any regression above noise blocks commit D.
Calibrate by running the same binary against itself first; alternate arm order.

### NE-3 — Wall time of one full applied index of the largest enrolled repo

Not just the scan; the whole `CodeIndexer.Index` call with `Apply: true`, under `ScanBudget.Default`.
Report the total, the scan portion, and whether the scan truncated.
*Decides:* two things. (a) whether "one repo per session start" is an acceptable detached-child cost,
or whether the session-start variant needs a smaller budget than the user-invoked one; and (b) the
value of `RepoFreshness.EnrollmentGrace` — ship 1 hour iff this measures ≤ 6 minutes, else ten times
the measured worst case (§9, OQ-3). NE-1's harness already builds a repo of the right shape and its
logs carry the wall times of eight applied runs — check whether that answers this before staging
anything new.

### NE-4 — Cost of an `IndexFreshnessService` tick that finds nothing due, at 50k facts

*Decides:* the poll interval. Five minutes is a guess. Per §6.2 the rule is to pick the largest
tolerable interval, not the smallest responsive one — so if a no-op tick is not near-free, raise the
interval rather than optimizing the tick.

### NE-5 — Is `ScanBudget.Default` the right per-repo bound for `engram repo index --all`?

A user typing an explicit catch-up command may legitimately want a larger budget than an ambient pass
gets. Measure a real multi-repo `--all --apply` and report how many repos truncate.
*Decides:* whether commit B needs its own budget (and therefore whether `RepoIndexRun.Freshen`'s
`budget` parameter earns its keep, or should be dropped).

### NE-6 — Does any existing test index one repo from two places concurrently?

Grep the suites; if ambiguous, run the full suite with `IndexLock` in place and report failures.
*Decides:* the blast radius of commit E.

### NE-7 — Has the scanner deletion hazard already fired? — ANSWERED for the transient case

**Result**: no evidence of it. On the real instance, 121 of 9,676 code-fact threads exceed version
count 2 (max 13), and **all are markdown or JSON documents with no source file above 2**.

**What settles it**: the defect is *directory-scoped* — an unreadable or vanished directory takes
down every indexed file beneath it — so it cannot selectively churn documentation while leaving
source files under the same tree untouched. The high-count threads (`STATUS.md`,
`engram-progress.md`, spec variants, `plugin.json`) are living documents revised over hours and days.

**A discriminator that was proposed and must not be reused as stated.** Inter-revision gap was
checked against *zero*, on the reasoning that a scanner double-firing would produce near-zero-second
gaps. It would not. `ProcessDeletion` closes in one scan and `ProcessFile` rewrites only on a later
one, so churned threads show revisions separated by roughly `FullScanIntervalMinutes`. The gap test
is sound but the comparison is against that interval, not against zero — and the observed 15–60
minute increments are ambiguous rather than exculpatory unless the configured interval is known to
sit well outside that band. Argument 1 above is what carries the conclusion; this paragraph exists so
the inverted version is not rediscovered.

**Not established: the persistent case.** A version-count query can only see a thread that was closed
*and then rewritten*. Persistent unreadability leaves facts closed and never rewritten — an absence,
which this query is structurally unable to detect.

### NE-8 — Open, and not a gate on E2

Whether any facts were closed and never rewritten: indexed paths in the store that no longer resolve
on disk, which a dry-run index already computes. Decides only whether E2 carries a repair question
alongside prevention. Both layers are preventive regardless, so this does not block the commit.

---

## 11. NE-1's result, and the sequencing ruling it produced

> **Amendment.** This section previously specified NE-1 as a gate to be run before any code was
> written, with a two-row outcome table. NE-1 has run. The observed result matched **neither** row, so
> the table is rewritten with the third outcome as its own row and the ruling follows.

### 11.1 The table was underspecified, and saying so is part of the ruling

The original table had a binary axis — *does the concurrent run destroy a belief the serial run
keeps?* — with row 1 as "yes, a live defect, `IndexLock` becomes commit 0" and row 2 as "no, only
`file_state`/stamp churn, E stays at E". Reality produced a third thing: **a divergence in the fact
table itself that the store's own invariant renders harmless.**

Read literally, row 1 fires — `id301` is a fact live in the serial run and closed in the concurrent
one. Read for intent, it does not: a byte-identical replacement (`id345`) is live, so no *belief* was
lost, only a *row identity*. The row-versus-belief distinction is the one the table failed to make,
and it is the distinction that decides the severity.

| Outcome | Meaning | Sequencing |
|---|---|---|
| A belief live in the serial run has **no equivalent live** in the concurrent run | Real defect; the supersession invariant did not absorb the race | `IndexLock` becomes **commit 0**, alone, ahead of A |
| **Observed:** live sets identical; one **redundant closed row**, two processes writing the same content and one immediately superseding the other | Real race, correctly absorbed. Costs a spurious fact-version, not a belief | **E stays at E.** The convergence guard moves forward to **commit A** (§7) |
| Only `file_state` / stamp churn | Cosmetic | E stays at E |
| No divergence at all | Lock is prophylaxis for item 4 | E stays at E, still a hard prerequisite for F |

### 11.2 Ruling: `IndexLock` stays at commit E. The guard moves to commit A.

**Why not commit 0.** "Commit 0" means *users are being harmed now and this must ship before the
feature work*. Three things say they are not:

1. **The content is correct in every observed outcome.** Both arms converge on identical live fact
   sets. `ux_fact_live` — one live fact per subject and predicate — is doing exactly the job it exists
   for, and it resolved the race without help.
2. **What is left is derived state (D8), and nothing needs recovering.** A redundant closed row is not
   a corrupted store, and re-indexing does not even remove it — there is no recovery action a user
   would take, because nothing they can read is wrong.
3. **The exposure is unchanged by shipping A–D.** Items 1–3 add no new concurrent full-scan of one
   repo: item 2 is a serial loop in one process, item 3 is one repo per session start inside a
   sequential shell, item 1 writes nothing. **Only item 4 raises the collision rate**, and item 4 is
   commit F, which E already gates. So the ordering that ships the lock before the thing that needs it
   is preserved without moving it.

**Why not "note it and move on" either.** The redundant row is not purely cosmetic, and two
consequences are worth stating so nobody later reads this ruling as *harmless*:

- **It degrades D57's revision marker.** `FactStore.VersionCounts` groups on `e.path` and
  `f.predicate` and counts the *whole* thread including closed rows, and recall renders `· v2` when the
  thread exceeds one version. A spurious supersession therefore makes a code fact advertise a revision
  history whose earlier entry says exactly what the current one says — precisely the "marking
  everything is as useless as marking nothing" failure D57 was written to avoid. Small, real, and in
  the direction of eroding a signal the store deliberately built.
- **A second-order effect on `backup replay`, since confirmed.** Replay's identity tuple is subject +
  predicate + body + `valid_from`, and `id301` and `id345` have identical bodies and were written in
  the same second. This was recorded here as a hypothesis, conditional on their `valid_from` values
  also being equal. They are: `docs/backup-replay-supersession-spec.md` records the duplicate
  `(subject_id, predicate, body, valid_from)` group measured among the concurrent arm's 401 rows and
  absent from the serial arm's 400, with the match returning whichever row the B-tree handed back. So
  the redundant closed row this section is about *is* the row that makes replay's match
  non-deterministic — one phenomenon seen from two sides. It is still not a reason to move E, and the
  reason has changed: D68 makes replay correct in the presence of such a pair rather than depending on
  the pair never being created. Nor does E retire D68 — the lock prevents new pairs and removes none
  of the ones already written, so a store that raced before the lock lands still needs the ordered
  match, and nothing can clean them up — they are `fact` rows, so by D8 `repair` may never delete one.

**Why the guard moves forward to A.** This is the substantive half of the ruling. The entire case for
leaving `IndexLock` at E rests on one property: *supersession absorbs the race, so the content is
always right*. That property belongs to today's code, not to anything this spec adds — and it is
currently unguarded. If it silently stops holding, the severity assessment above becomes wrong and
nobody finds out.

So commit A ships a tier-3 test that asserts that property **directly**, in two parts (§7): that
`ux_fact_live` exists in `sqlite_master` with its partial predicate intact, and that no subject-and-
predicate pair holds more than one live fact. Both, because *removed* and *bypassed* are distinct
failure modes and either can occur while the other holds.

What commit A deliberately does **not** ship is a comparison of the two arms' live sets, which is what
this ruling first called for. That test cannot fail: both arms run the same code against the same
repository, so any patch that breaks the invariant lands on both of them, and two identically-broken
stores compare equal. It was written, falsified against a store with `ux_fact_live` dropped, and
stayed green — it survived removal of the very constraint it existed to pin, which is the definition
of not guarding it. Cross-arm equality moves to commit E, where the lock is what makes it true and
where NE-1's own run recorded the arms differing by one closed row, so the comparison can genuinely
separate them.

**Pinning the reason a bug is tolerable is worth more than pinning the bug**, because the reason is
what the sequencing decision depends on.

### 11.3 On acting from a single observation

Asked directly: one reproduction, 1 of 80 files, one run — enough to rule on?

**Yes, for sequencing, because frequency is not the variable this ruling turns on.** The decision is
bounded by the *outcome class* (correct content live, one redundant closed row), not by how often it
lands. At 1/80 and at 80/80 the ruling is identical, because in both cases the store converges and
nothing a user reads is wrong. Repeat trials would refine a number that changes nothing.

**What is genuinely open is a different question**, written as NE-1b: *can this race ever produce the
severe case* — a belief live serially with no equivalent live concurrently? That is the only result
that moves E to commit 0 and reinstates the escalation. It runs **in parallel with A–D and blocks
nothing**, because A–D do not raise the collision rate (§11.2, point 3). If it comes back positive
before F, E moves and F waits; if after E has landed, the lock is already there and the finding is
retrospective.

Deliberately **not** asked for: the "deterministic-per-file vs. timing luck" investigation. It is
interesting and it decides nothing, and NE items that decide nothing are how a measurement queue stops
being read.

---

## 12. Verification record, and what remains unverified

### 12.1 Citations corrected against `main` / `f3a8516`

| Claim | Draft said | Actual |
|---|---|---|
| Secondary-root `IndexOptions` | `IndexCommand.cs:214` | `:213` |
| `full` disjunction | `CodeIndexer.cs:113` | `:110` |
| Truncated-scan block | `CodeIndexer.cs:129-152` | `:128-152` |
| `IndexCommand.Note` | `:241-246` | `:237-247` (the `Telemetry.Append` call inside it is `:241-246`) |
| Server shutdown hook | `ServeCommand.cs:172` | `:152` |
| Diagnostics check count | "twelve checks" | more than twelve; do not rely on a count |

Verified as stated: the schema block (`engram-schema.sql:343-351`), `FullScanIntervalMinutes = 60`
(`IndexingSettings.cs:57`), the config read (`:174`), `DeferralCooldown` = 7 days
(`RepoEnrollment.cs:36`), the launcher's job strings (`MaintenanceLauncher.cs:97-99`) and `Redirect`
(`:131`), the unconditional spawn (`HookCommand.cs:428-431`), `Diagnosis`/`DiagnosisState`
(`Diagnostics.cs:8`, `:24`), `CheckRepo` (`:924`), `AddHostedService` (`ServeCommand.cs:102`, `:108`),
`AddFilter` (`:78`, `:83`), `EmbeddingProgressPath` (`EngramHome.cs:57`, `:98`), the `[indexing]`
block (`DefaultConfig.cs:56-57`), and `MaintenanceLauncherTests` `:27` / `:95` / `:111`.

`RepoCommand.cs` was subsequently read **in full** rather than sampled, and that corrected two claims
an earlier pass of *this document* had itself introduced. Recorded here because a verification record
that hides its own misses is worth nothing:

| Claim | An earlier pass of this spec said | Actual |
|---|---|---|
| Dispatch switch | `:28-36` | `:29-37` — **the original draft was right; the "correction" was the error** |
| `Unknown`'s exit code | unverified, assumed 1 | **2**, at `:314-318`; `Run`'s subcommand error at `:22-23` is also 2 |
| `Enroll` | `:39` | `:40` |
| `repo list` identity rendering | `:307-310` (that is `StateText`, `:306-312`) | bare at `:240`; state `:241`, root `:242`, scan `:243` |

The lesson, worth keeping: correcting a `file:line` span without reading around it can introduce a
worse error than the one being fixed — a switch's span includes its `return … switch` opener and its
closing `};`, which an arm-only range silently drops.

### 12.2 Draft assumptions now resolved

1. **`PathCanonicalizer` — RESOLVED.** `src/Engram.Core/PathCanonicalizer.cs:13`,
   `public static string Canonical(string path)`, in `Engram.Core`. Usable as specified.
2. **`ProcessStartToken` — RESOLVED.** `src/Engram.Core/ProcessStartToken.cs`, `ForSelf()` `:44` and
   `ForPid(int)` `:47`, both returning `string?`. Exactly the shape §6.4 needs.
3. **`IndexLock` does not already exist — RESOLVED.** `grep -rn 'IndexLock' src/ tests/` is empty.
4. **The enroll spawn is ungated — RESOLVED**, and it changed the OQ-1 ruling. See §9.
5. **`RepoCommand`'s exit-code convention — RESOLVED.** 2 for a usage error (`:23`, `:317`), 1 for a
   real failure (`:44`, `:72`, `:89`, `:110`, `:226`), 0 otherwise. §4.1, §4.3 and §7's commit-B tests
   now follow it rather than asserting "non-zero".
6. **A dry-run precedent exists inside `RepoCommand` — RESOLVED.** `Reset` (`:103-133`) reads
   `--apply` at `:105` and otherwise prints *"Dry run only — nothing was changed. Re-run with --apply
   to reset."* at `:119`. §4.2 matches its shape and its wording.
7. **What an unprotected concurrent double-index does — RESOLVED by NE-1.** §10 and §11.
8. **The lock's coverage of the read-modify-write cycle — RESOLVED.** `LoadStates` `:111`,
   `RepoScanner.Scan` `:132` (inside the `full` branch), and the write pass `:213-222` all occur
   inside `CodeIndexer.Index`, after the `last_root` repair, and nothing in the cycle runs in a
   static initializer, constructor, or field initializer. Discharges §12.3 item 5.

### 12.3 Still unverified — confirm during implementation

1. `EmbeddingBacklogService`'s registration pattern transfers to a second hosted service with no
   ordering constraint. The registrations were read; no ordering dependency was searched for.
2. Adding a key to `DefaultConfig.cs`'s `[indexing]` block is subject to `ConfigEditor`'s
   "never overwrite a value it did not create" rule (D33) only on *edit*, not on first write. Confirm
   before commit F.
3. Whether `ScanBudget` and `ScanStop`/`Truncated` are shaped exactly as §1.2 describes — the members
   were taken from the draft and not re-read; `CodeIndexer.cs:132-143` confirms `Budget`,
   `scan.Truncated` and `scan.Summary()` are real.
4. `IndexCommand`'s flag-parsing block at `:20-52` and its store-open branch at `:100-118` — taken
   from the draft's citations and spot-checked only around `:73`, `:107`, `:196-218`.

### 12.4 Confidence, and the escalation — **withdrawn, with reasoning**

Medium-high overall. The parts I am confident in: the structural finding (§0), the shared-policy
design (§2), items 1–3, all three rulings in §9, and the sequencing ruling in §11.

> **Amendment.** The previous revision said: *"One area warrants Ultra-Advisor escalation if NE-1
> comes back positive"*, the question being whether a per-identity lock inside `CodeIndexer.Index` is
> sufficient, **or whether the defect lives in the `LoadStates`→`Scan`→write ordering, where a lock
> would mask a stale-snapshot bug rather than fix it.** NE-1 has come back positive, and I am
> **withdrawing** the escalation rather than invoking it.

The reasoning, because withdrawing a trigger I set has to be justified rather than quietly dropped:

**NE-1's observation confirms the stale-snapshot mechanism and simultaneously shows the lock covers
it.** Both processes independently detected the same mutation and each wrote a fact for it — which is
exactly a snapshot-loaded-before-the-other-committed interleaving. That was the hypothesis behind the
escalation. But the question the escalation asked was not *is the mechanism a stale snapshot*; it was
*would a lock mask it rather than fix it*, and that turns entirely on **what the lock spans**. §6.4
places the claim before the `last_root` write at `CodeIndexer.cs:88-92` and releases at the end of
`Index`, so the snapshot load, the scan and the write pass are all inside one critical section per
repo identity. A lock spanning the whole read-modify-write cycle eliminates the window; only a lock
covering the write alone would leave a stale read outside it and hide the symptom. The distinction the
escalation existed to surface is therefore already resolved by the placement, and asking it again
would spend the deepest reasoning tier on something the spec had specified correctly.

**Two conditions reinstate it, and both are cheap to detect:**

1. ~~**§12.3 item 5 comes back the wrong way** — any part of the snapshot/scan/write cycle happens
   before `:88` or outside `Index`.~~ **Discharged** — §12.2 item 8 confirms the whole cycle
   (`LoadStates` `:111`, `RepoScanner.Scan` `:132`, write pass `:213-222`) runs inside
   `CodeIndexer.Index` after the `last_root` repair, so the placement holds and this condition cannot
   fire.
2. **NE-1b returns a live-set divergence.** That would mean the supersession invariant did *not*
   absorb the race, so the defect is not shaped the way this ruling assumes, and the severity
   assessment (§11.2) and the escalation should be reopened together.

**What the lock does not cover, stated so it is not mistaken for coverage:** writers that are not
`CodeIndexer.Index` — the `user-prompt` hook, the MCP server's own writes, the embedding backlog.
Those write different subjects and do not participate in the code-fact supersession chain, so they are
not in this race. If a future writer ever produces `about` facts for indexed files from outside
`CodeIndexer.Index`, this analysis expires and the lock's placement must be revisited.

**Per-commit verification, B through F.** Each commit is verified against *its own* tree, not the
working tree it happened to be split out of: stash any later commit's in-progress diff, run tier 1 and
tier 2 against the isolated tree, then restore. Tier 3 stays on the aggregate, because it drives the
published binary and is the tier least sensitive to which working-tree diffs are present. Commit A was
verified one notch weaker than this — built in isolation, tested in aggregate — which was adequate
there because a compile dependency would have failed the build and no commit-A test reaches
`repo index --all`. It stops being adequate once commits acquire behavioural coupling to earlier ones,
which is the whole reason this is written down rather than re-decided per commit: *builds in isolation,
tested in aggregate* is what lets a commit go out green and bisect red later.

---

## 13. Item 5 — RepoScanner reports a scan complete that it could not perform (commit E2)

### 13.1 The defect

`RepoScanner.Walk` sets `stop = ScanStop.Complete` before its loop (`:210`) and revises it only in the
time and ceiling branches. Its `catch (Exception e) when (e is IOException or
UnauthorizedAccessException) { continue; }` (`:224-231`) writes to neither `skipped` nor `stop`. A
directory that fails to enumerate is therefore treated as containing nothing while the scan still
reports `Truncated == false`, which licenses `CodeIndexer` (`:140-151`) to compute
`deletions = states.Keys.Where(rel => !onDisk.Contains(rel))` and to stamp `last_full_scan_at`.

Every code fact beneath an unreadable directory is closed because the scanner could not see it. This
is precisely the outcome D53's guard exists to prevent. The guard is correct and correctly placed; it
is simply never armed, because the only thing that arms it is a truthful `Truncated`.

### 13.2 Exposure

Not confined to the non-git path. `GitFileLister.List` returns **null**, not an exception, when the
root is absent (`:295-298`), and `RepoScanner.Scan` is `candidates = listed ?? Walk(...)` (`:131-137`).
A git repo whose root has been deleted, moved, or unmounted therefore falls *through* to `Walk`;
`Walk` pushes `root` onto `pending` before the loop and the first iteration enumerates it inside the
same try/catch as every other directory, so `DirectoryNotFoundException` — an `IOException` — is
caught rather than thrown. The scan returns an empty candidate set marked `Complete` and the repo's
entire fact set is closed. A moved checkout and an unmounted volume both land here.

Severity is a function of how long the condition lasts, not a constant. Transient unreadability
self-heals on the next full scan: `ProcessDeletion` drops the `file_state` rows as well as closing
the facts, so the files read as new and `ProcessFile` writes fresh ones. Persistent unreadability
never heals through ordinary operation. Everything closed is D8-regenerable, so this is silent
wrongful closure and history pollution rather than unrecoverable loss — but the transient case is not
free either: each cycle closes a whole generation of facts and writes another, against `fact`,
`fact_fts` and `fact_token`, for no semantic change.

**Why this blocks commit F specifically.** Today the defect needs someone to run an index while
something is unreadable. Commit F runs `--all` unattended on a timer across every enrolled repo,
which converts a hazard requiring a coincidence into one that is sampled continuously.

No evidence this has fired on the real instance (§10, NE-7), though only the transient case has been
checked. Both layers are preventive either way.

### 13.3 Layer 1 — `Walk` records what it could not read

This is a bug fix against an existing decision, not a new design: D53 already rules what a partial
scan may do, and that ruling is already implemented one layer up in `CodeIndexer`.

- The catch records the failure into `skipped` under a new `SkipReason` — a count plus the first
  failing path. `ScanResult.Summary()` already renders the `skipped` dictionary, so the count reaches
  every existing caller with no change at those call sites.
- The catch sets `stop` to a new `ScanStop` value **only if `stop` is currently `Complete`** (ruling C).
- `Summary()`'s stop switch gains an arm for the new value.
- `Truncated` (`:59-68`) is `Stop != ScanStop.Complete` and needs no change; D53's guard in
  `CodeIndexer` then does the rest with no edit.
- `doctor` already branches on `Truncated` (`Diagnostics.cs:965`), so the warning surface exists.
  `Warn`, never `Broken` — an unreadable directory is a state, not corruption, and D37's exit code
  must stay intact.

**The trap, and it is silent.** If `Summary()`'s stop switch carries a `_` default, a new enum value
renders as *nothing* and the fix ships with its only observable missing. The test must therefore
assert the summary **text** names the unreadable directory, not merely that `Truncated == true`, and
must be proven to fail with the new arm removed.

### 13.4 Layer 1's accepted cost, and the alternative rejected

A repo containing one permanently unreadable directory will never again stamp `last_full_scan_at` and
will never again apply deletions. That is correct under D53 **once it is observable**, and silent it
would trade a data-loss bug for a deletions-quietly-stopped-forever bug, which is harder to find and
stays wrong longer. The observability in §13.3 is not decoration; it is the condition on which this
cost is acceptable.

**Prefix-scoped suppression — suppressing deletions only beneath the unreadable subtree and applying
them elsewhere — is rejected for now.** It is more code on the destructive path, defending a state
nobody has yet observed. Reassess only if the doctor warning is seen firing persistently in practice.

**A narrower fix is also rejected: returning a failed scan from `Scan` when `listed is null` and the
root is absent.** It covers only the deleted-root case and not the ordinary mid-walk one, and it would
be the fourth pre-check of the same shape — `RepoFreshness.IsSelectable` and `RepoCommand.IndexAll:308`
already check root existence and both are TOCTOU. The scan's own report is the thing that lies; the fix
belongs there.

### 13.5 Layer 2 — a zero-judgment guard on wholesale deletion

In `CodeIndexer`, as a new arm on the existing branch:

    stop == ScanStop.Complete && onDisk is empty && states is non-empty
      -> skip deletions, add a note naming the condition, do not stamp last_full_scan_at

**No threshold, and that is the whole point** — a threshold is the judgment cost and nothing derives
one. Warn and skip; never a silent clamp.

**Why this is not redundant with layer 1.** It names a case layer 1 structurally cannot catch: an
unmounted volume whose mountpoint directory *remains* is an existing, empty, cleanly enumerable root.
`GitFileLister` declines, `Walk` completes raising nothing, `Truncated` stays false. Under commit F's
timer each mount flap becomes close-everything-then-rewrite-everything. The asymmetry decides it — the
misfire cost is a legitimately emptied repo keeping stale facts plus a visible warning, which is small
and self-explaining; the miss cost is total silent closure per flap.

**Two shapes that will trip this deliberately, and are not bugs.** A repo whose files were genuinely
all removed, and a repo where a filter or ignore-glob change now excludes everything it previously
indexed. Both warn and skip. That is the accepted misfire; do not "fix" a test that exhibits it.

The `states is non-empty` clause is what keeps a brand-new repo with nothing indexed yet from warning.

### 13.6 What must not change

- D53's guard in `CodeIndexer` (`:140-151`) is correct. Do not rewrite it; layer 1 exists to arm it.
- `Truncated`'s definition (`RepoScanner.cs:59-68`).
- The time and ceiling branches' existing unconditional assignment to `stop`. They `break`; only the
  new branch is conditional (ruling C).
- `doctor` stays `Warn` for every state introduced here. Nothing here may set exit 1.
- `RepoCommand.IndexAll:308`'s existence check stays — see the note at §7's commit B block. It guards
  *use* where `IsSelectable` guards *selection*, and this commit does not close that window.

### 13.7 Assumptions the Architect could not verify

Stated so the Implementor checks rather than trusts. None of the following was read by me directly;
all reached this spec through a source-verified review relayed second-hand, and any of them may be
named differently in the tree:

- That `ScanResult.Summary()` renders the `skipped` dictionary, and the exact form of that rendering.
- That `Summary()`'s stop handling is a switch, and whether it carries a `_` default (§13.3's trap
  turns on this).
- The existing member names of `ScanStop` and `SkipReason`. **Match the existing convention rather
  than any name suggested here.**
- That `Diagnostics.cs:965` is the `Truncated` branch.
- That `ProcessDeletion` drops `file_state` rows as well as closing facts (§13.2's transient-recovery
  claim rests on this).

If any is wrong, the design in §13.3 and §13.5 still holds — only the implementation shape moves.
Report the gap rather than adapting the design.
