# 03 — Tool profiles

Status: **approved for implementation.** Parent: `docs/memory-expansion-spec.md` row 3.

Revision 2 (2026-08-20): tool inventory corrected 10 → 11 (`engram_judge` shipped in `770519d`
after this spec was written); `engram_judge` placed in `default` (D-1); the third-tier rejection
re-argued on stronger grounds (D-2); NEEDS-EVIDENCE 1–3 closed with measured numbers (D-3); a
reference-integrity constraint the profile mechanism creates recorded (D-4).

Revision 3 (2026-08-20): `index_repo` moved to `default` (D-5), making lifecycle exactly
`{start, status, stop}` and `default` 8 tools; ship decision recorded (D-6). D-5's trigger fact
was verified against source and **differs from the one it was proposed on** — see D-5, which
records what was checked and why the conclusion survives anyway. The measured delta changed as a
consequence and is restated in D-3.

## Goal

A profile selects which MCP tools a session's server connection *advertises*, trimming the
prompt-token cost of tool schemas. Lifecycle tools (`start`/`status`/`stop`) leave the default
set. Token cost becomes a measured line item.

**This goal is D17's, not a new one.** `McpToolSurfaceBudgetTests` opens: "tool definitions are
serialized into every session whether or not memory is ever used, so the surface is a budget
rather than a free channel." D17 established the budget and a ceiling test to hold it. This
feature is the first mechanism that actually *spends less* of it, rather than bounding growth.
The spec should be read as implementing D17, and the two are not redundant — see D-3 on why
bounding the definition and bounding the delivery are different quantities.

## Non-goals

- Not a permissions mechanism — `ClaudePermissions.GrantedTools` (client-side pre-approval,
  avoids a confirmation prompt) is untouched and orthogonal; see Design for why these are
  two different axes.
- No dynamic per-call profile switching — a profile takes effect on the next MCP
  connection, matching how every other config change already behaves.
- Not mirroring a comparable tool's own profile grouping (see Design) — the split is sized to
  Engram's own inventory.

## Inspiration

A comparable memory tool lets a per-connection flag select a smaller tool set for routine
use versus a larger one that includes destructive/admin operations, cutting the token cost
of tool schemas sent to the model. Engram's version below picks a different split, sized to
its own much smaller tool inventory, and a different selection mechanism suited to how
Engram is packaged.

## Design

**Two axes, not one.** Today, `ClaudePermissions.GrantedTools` = `{engram_recall,
engram_remember, engram_status, engram_browse, engram_expand, engram_index_repo}` (6 of 11
tools) — this controls whether Claude Code prompts for permission before a call. It does
**not** reduce token cost: all 11 tool schemas (`Recall, Remember, Forget, Revise, Expand,
Browse, IndexRepo, Judge` in `EngramMcpTools`, plus `Start, Status, Stop` in
`EngramServerTools`) are sent to the model regardless of grant status.
A profile is a genuinely different, currently-unsolved axis: which tools the MCP server
*registers at all* for a given connection, which is what actually removes schema bytes from
the model's context. The two mechanisms are independent and both remain in force —
excluding a tool from a profile makes `GrantedTools` moot for that session (nothing to grant
if it isn't advertised); including it in a profile still respects whatever `GrantedTools`
says about prompting.

Because the two axes are independent, `default` is **not** the granted set, and the overlap is
worth stating plainly since it is what the profile actually feels like in use. Under `default`
(8 tools) five are granted (`recall, remember, browse, expand, index_repo`), three are advertised
but not granted and so still prompt (`forget, revise, judge`), and one grant goes dead because its
tool is not advertised (`status`). None of that is a defect — it is the two axes being genuinely
orthogonal — but "I added a tool to `default` and it still prompts" is a predictable and correct
outcome, not a bug to chase.

**Two profiles, not three.** One group leaves the default set: `{start, status, stop}` — call
this "lifecycle." The complement is everything else — call this "default." Final profiles:
- **`default`** (8): `recall, remember, forget, revise, expand, browse, judge, index_repo`.
- **`full`** (11): default + `start, status, stop`.

Lifecycle is now exactly the set of tools defined in `EngramServerTools`, and `default` exactly
the set defined in `EngramMcpTools`. That alignment arrived as a consequence of D-5 rather than as
its motive, and it is load-bearing for nothing — but it means the profile split, the golden file's
scope, and `McpToolSurfaceBudgetTests`'s reflection scope now describe one boundary instead of
three, which removes a standing source of confusion. See "Reference artifacts and their scope".

**The boundary is the exclusion list, and that is the definition — the enumeration above is
derived from it.** The original draft stated `default` as a closed list of six names, which is
what made `engram_judge` look like it needed adjudication when it shipped. It did not: the rule
that generated the list is "everything that is not lifecycle," and a rule stated as its own
output cannot answer a question about anything new. Written this way, tool 12 needs no
architect — it lands in `default` unless it is lifecycle, and only a *lifecycle* addition is a
decision. See D-1.

Defining the default set by exclusion fails open — a new tool joins `default` without anyone
deciding it should. That is deliberate, and it is safe only because of the Non-goals above: the
profile axis is token cost, not permission. A tool that lands in `default` by default is
advertised, not granted; `GrantedTools` is untouched and still prompts for anything it does not
name. So the failure mode of this rule is a token-cost regression, which is precisely what D17's
ceiling test and D-3's measurement exist to catch — and not a capability leak, which nothing here
could catch.

**Selection mechanism — config key, not a launch flag.** Two mechanisms were considered:
- *Launch flag via `.mcp.json` args* (considered, not chosen): this shape works cleanly for
  a manually-configured MCP client set up by hand from a README. Engram ships as a
  Claude-Code plugin whose `.mcp.json` is generated and owned by the plugin packaging, not
  something a user routinely hand-edits per install — making a profile choice a launch-flag
  would mean either baking it in at build time (not a runtime preference at all) or building
  a second marker-preserving editor for `.mcp.json` alongside `ConfigEditor`'s existing one
  for `config.toml` — two implementations of "edit a file without clobbering what's not
  ours" for one preference.
- *Config key* (chosen): `[mcp] tool_profile = default|full`, read by the server at
  connection time. Zero new mechanism — it is the exact existing `ConfigEditor`/D33
  convention every other Engram setting already uses ("one implementation"). Profile
  changes are rare, deliberate acts, not a per-call need, so a file matches the actual
  cadence of change, and it requires no second file-editing implementation.

`engram profile show` / `engram profile set <default|full>` write through `ConfigEditor`
(marker `# written by engram`, D33). This is a preference, not a removal/rewrite of
authored data, so it is **not** gated behind `--apply` (D49's dry-run rule targets
destructive verbs; a profile choice is freely reversible Engram-side preference). This is now
**confirmed against precedent rather than assumed** — see NEEDS-EVIDENCE #3, closed.

**Schema delta**: none. Profile is a config value read at connection time, not stored in
the database. `doctor`/`engram_status` can report the active profile by reading config
directly (D37: reads, never repairs).

## Decisions

The Design section above records the original decisions in prose; it predates this spec
carrying numbered records. D-1 onward are numbered because later specs cite them.

### D-1 — `engram_judge` ships in `default`, and the profile is defined by exclusion

`engram_judge` joins `default`.

The decisive argument is not a preference about where judge "feels" like it belongs. It is that
the opposite choice breaks an invariant this spec already commits to, in the profile that is the
default:

1. **`engram_remember`'s shipped description names `engram_judge`.** `EngramMcpTools.cs:79-89`
   ends: *"When candidates are enabled, also returns up to 3 similar live facts already stored,
   for a follow-up engram_judge call."* `remember` is in `default` under every reading of the
   boundary. **D51 — this spec's own first invariant — requires that description to ship full and
   unmodified in any profile containing it**, and the code comment directly above it
   (`EngramMcpTools.cs:71-78`) states the reason it cannot be varied at all: a `[Description]` is
   a compile-time constant. So the profile mechanism is *forbidden* from trimming the sentence
   away. Excluding judge from `default` would ship, in the default profile, an unmodifiable
   instruction to make a follow-up call to a tool that is not advertised. Note the sentence ships
   unconditionally even where the candidates setting is off — only the behaviour it describes is
   gated, never the text.
2. **Judge is not lifecycle**, so the exclusion rule places it in `default` without needing a new
   decision at all. Argument 1 is what makes this the *right* rule rather than merely the
   applicable one: the rule and the invariant agree.
3. **Judge's own trigger tools are both in `default`.** Its description reads: *"Call it after
   `engram_recall` or `engram_expand` surfaces two facts that might disagree… and shows up under
   `engram_expand … history` for either one."* Both named tools are in `default`. A profile
   containing the tool that creates the situation and the tool that displays the outcome, but not
   the tool that resolves it, is incoherent independently of argument 1.

Rejected, and the reason recorded so it is not re-tried: **trimming the `engram_judge` sentence
from `remember`'s description under `default`** would resolve argument 1 and must not be done. It
inverts D51, which exists precisely because that description loses races when weakened, and it
would make a compile-time constant vary per profile, which the language does not permit without a
second description — i.e. two implementations of one description, drifting from the first tuning.

Correcting the record on the original draft's framing: judge did not "arrive after a clean 6+4
split and break it." The split was never 6+4 — it was *complement-of-lifecycle*, which has always
been the correct reading, and which absorbs judge with no special case.

### D-2 — The third tier stays rejected, on stronger grounds than the original

The original rejected a third, finer-grained tier (carving `forget` off as admin-only) because
Engram's inventory lacked "an equivalent destructive/aggregate cluster large enough to justify a
second cut." That is an argument from current size, and an argument from size expires — it invites
re-litigation at every tool added, which is exactly what `engram_judge` triggered.

The stronger argument is that the cluster is empty **by invariant, not by inventory**. Engram's
facts are append-only; belief content is immutable once written. Checked against each tool's own
shipped description:

- `engram_forget` retracts — the store closes rather than deletes.
- `engram_revise`: *"The old fact is closed, never erased."*
- `engram_judge`: *"neither is changed or closed by it."*

So no MCP tool destroys authored truth, and there is nothing for an admin tier to carve out.
Adding judge did not weaken this rejection; being non-destructive, judge is a third witness for it.

**A tripwire, so this is a bound rather than a feeling** (D60: a bound nobody can test is not a
bound). Reopen the third-tier question if, and only if, an MCP tool is ever added that can destroy
or rewrite authored belief content. On the current invariants that tool cannot exist, so the
expected answer is "never" — but stated this way the condition is checkable, and someone proposing
such a tool will trip it rather than having to remember this paragraph.

### D-3 — The measured delta, and why it is now exact rather than a lower bound

Measured (NEEDS-EVIDENCE #1, closed), on the pre-D-5 split where lifecycle was four tools:
`default`(6, pre-judge) = 2,472 bytes; `full`(10, pre-judge) = 3,500 bytes; **delta = 1,028
bytes**. `engram_judge` = 364 bytes. These counted tool-level `[Description]` text only.

**The delta does not move when a tool joins `default`; it moves when a tool leaves `lifecycle`.**
A tool present in both profiles cancels, so **delta is the sum of the excluded tools alone**. D-1
therefore changed nothing (judge was in neither, then in both). **D-5 changed it materially**:
moving `index_repo` out of lifecycle removes its bytes from the delta permanently.

`engram_index_repo`'s description (`EngramMcpTools.cs:455-457`) is *"Record the user's answer on
indexing this checkout: enroll, decline (stop asking), or later (ask in a week). Call as soon as
they answer — decline is as valid an answer as enroll."* — **≈180 characters / ≈182 bytes** by hand
count from source. So the post-D-5 delta is approximately **1,028 − 182 ≈ 846 bytes ≈ ~235
estimated tokens**, down from ~286.

**Treat ~846 as provisional.** It is a hand count, and the authoritative figure must come from the
one summation named below. It is stated here only so D-6's ship decision can be checked against the
right order of magnitude rather than against a number D-5 invalidated.

**The lower-bound caveat from revision 2 is now void, and it inverted rather than shrinking.**
Revision 2 warned that 1,028 undercounted because parameter descriptions were excluded and
`index_repo` carried model-facing parameters (`"Git checkout path."`, `"enroll, decline, or
later."` — 44 characters). `start`/`status`/`stop` take only DI-injected parameters
(`EngramHome`, `ServerIdentity`, `IHostApplicationLifetime`) and carry **no** parameter
descriptions at all. So with `index_repo` moved to `default`, the only tools left in the delta
contribute zero parameter bytes: **the description-only delta and the parameter-inclusive delta
are now the same number.** The figure is exact, not a floor.

**Re-measure using the budget test's own summation, not a second one.** `DescriptionLength` over
`GetCustomAttribute<DescriptionAttribute>()` is already the one implementation of "what a tool
costs" in this tree; a measurement written freshly for this spec would be a second one, and the two
disagree the first time either is tuned. This is the same rule the repo applies to the tokenizer and
to the vector lane. Note this requires widening `ToolMethods()` beyond `EngramMcpTools` — see
Hazard 3, which is a decision, not a mechanical edit.

**The byte-versus-character caveat is not hypothetical here.** `TokenEstimator.Estimate` is
`ceil(chars / 3.6)` and takes *characters*; the measurements above are *bytes*. `index_repo`'s
description contains an em-dash — as do `remember`'s and `revise`'s — which is one character and
three bytes in UTF-8. So every byte figure in this spec slightly exceeds its character count, and
every token estimate derived from a byte figure is correspondingly a slight overestimate. This is
the same class of error D60 recorded when a pattern spelling `·` as a bare `.` matched one byte
against two. Whoever runs the authoritative measurement should report characters, since that is what
the estimator consumes.

Token estimates, all slight overestimates per the paragraph above: post-D-5 delta ~235;
`default`(8) ~1,060; `full`(11) ~1,295; the budget test's measured 8-tool actual (descriptions plus
parameters, 4,789 characters) ~1,331.

### D-4 — A profile removes tool schemas; it does not remove references to them

This is the constraint the profile mechanism creates, and it generalizes the problems found while
placing `engram_judge` and `engram_index_repo`. **Both known instances are now resolved by placing
the referent, and the two are worth distinguishing because they are different failure modes that a
single rule would blur.**

Excluding a tool withdraws its schema from the connection. It does **not** edit any of the places
that *name* or *depend on* that tool: other tools' `[Description]` text, the primer, `GrantedTools`,
or the golden file. Two instances existed:

- **Hard dangling name — `engram_remember` → `engram_judge`** (`EngramMcpTools.cs:89`). Shipped
  text literally contains the string `engram_judge`. Resolved by D-1, and resolvable *only* that
  way, because D51 forbids editing the referring text.
- **Unfollowable instruction — `PrimerBuilder.EnrollmentLine` → `engram_index_repo`.** The shipped
  primer names **no tool** (D15 forbids tool names in primer guidance); it says *"Ask the user
  whether to enroll it, and record their answer."* The dependency is on the *capability*, not on a
  name. Resolved by D-5. See D-5 for why the distinction matters.

**Requirement on implementation:** for every tool a profile excludes, check both classes. The first
is a grep — search shipped descriptions for the excluded tool's name. The second is not, and cannot
be automated the same way: it requires asking whether any shipped guidance instructs the model to do
something only that tool can do. A tier-2 test covers the first class as a general property — for
each tool *not* in a profile, no shipped description in that profile contains its name — so a future
tool cannot reintroduce it silently. The second class is a review question, recorded here because it
is invisible to the test.

Note the correct falsification for that test is to exclude judge from `default` and confirm it
reddens; asserting only the known pairs would pass with the general property broken.

### D-5 — `engram_index_repo` moves to `default`; lifecycle is exactly `{start, status, stop}`

`index_repo` joins `default` (8 tools). Lifecycle becomes exactly the three `EngramServerTools`
tools.

**The trigger fact this was proposed on does not hold, and the corrected version is recorded here
because a decision record that teaches a false premise will be misapplied.** The proposal described
`PrimerBuilder.cs:17-18` as "an undroppable, unconditional primer line" referencing `index_repo`'s
description, and argued it was the same shape as `remember`→`judge`. Verified against source, it is
neither undroppable nor unconditional, and the shape differs:

- **Conditional.** `EnrollmentLine` is emitted only under `if (offerEnrollment)`
  (`PrimerBuilder.cs:63-66`); the comment at `:17-21` states the caller resolves that via
  `RepoEnrollment.ShouldOfferEnrollment` and the builder "takes that answer as a value rather than
  resolving it itself."
- **Droppable.** It goes through `TryAppendLine`, which silently returns when the line would
  exceed `MaxTokens = 300` (`PrimerBuilder.cs:107-120`). The genuinely undroppable primer lines are
  `SubagentInstruction` (added directly to the list at `:91`) and the standing directives
  (`lines.Add` at `:141`/`:144`). This is not one of them.
- **No tool name ships.** The reference to `engram_index_repo` is in a *code comment*, which ships
  to nobody. D15 forbids tool names in primer guidance, so the shipped line could not name it.

**Why the decision survives the correction.** The remaining argument is not D-1's — D51 is not in
play, because nothing forbids editing `EnrollmentLine` — it is that the instruction becomes
*unfollowable*:

1. The primer conditionally tells the model to "record their answer — a no as well as a yes."
   Under a `default` profile excluding `index_repo`, no advertised tool can record it. The
   instruction fires exactly when Engram has decided to ask, and cannot be complied with. An
   instruction the model is given and cannot execute is a defect regardless of whether a tool name
   appears in it.
2. `index_repo` is not lifecycle in the `start`/`stop` sense. Its description: *"Record the user's
   answer on indexing this checkout: enroll, decline (stop asking), or later (ask in a week)."* It
   is a consent-recording tool answering a question **Engram itself raises** — routine per-session
   flow, not administration.
3. It was the only `EngramMcpTools` tool assigned to lifecycle; the boundary cut that file at
   exactly one tool, and that tool was the odd one out.

**What is not the reason.** The resulting alignment of three artifact boundaries onto one
(profile split = golden file scope = budget-test reflection scope = `EngramServerTools`) is
confirmation, not motive. Tidiness is not an argument, and recording it as one would license the
next tidiness-driven move.

**What this costs, stated because it is the honest counterweight:** the measured saving drops by
`index_repo`'s bytes, from ~286 to ~235 estimated tokens (D-3). D-6 was decided against the
superseded figure; see D-6's note.

**Not blocking either way, and the reason is worth keeping:** enrolment was never stranded.
`engram repo enroll|decline|later|reset` all exist as CLI verbs (`RepoCommand.cs:31-34`) and share
the same `RepoEnrollment` helpers as the MCP tool by explicit design — `RepoCommand.cs:10` notes the
tool "shares the same helpers rather than a second copy." What excluding `index_repo` cost was the
model's ability to answer on the user's behalf, not the capability itself.

### D-6 — Ship

Approved for implementation. The rationale, recorded as given: ~235 estimated tokens (post-D-5;
see the note below) saved on every default-profile session, for a config key read at connection
time plus a CLI verb — zero schema migration, reversible, no auth/migration/concurrency/public-
interface exposure. The parent spec's ordering rationale prices this correctly: cost is paid on
every session and the fix is small.

**Note on the figure this was decided against.** The decision cited ~286 tokens, which was the
pre-D-5 delta. D-5 was taken in the same breath and reduces it to ~235 (D-3). The direction of the
decision is unaffected — a cheap, reversible mechanism returning roughly one standing-directive
budget per routine session — but the number moved after the call was made, and it is recorded here
so nobody later reconciles a shipped measurement against 286 and reports a regression that is
actually this decision.

## Reference artifacts and their scope

Revision 1 carried a stale note about the golden file listing "7 names against a stated total of 8."
**That note is withdrawn: there is no stated total in the file to disagree with.** The mechanical
pass that produced it reported a conflict between two numbers, one of which did not exist — worth a
line because that failure mode is cheap to repeat and reads exactly like a real finding.

What is actually true is more useful. `docs/mcp-tool-descriptions.golden.txt` lists exactly 8 tool
names: `browse, expand, forget, index_repo, judge, recall, remember, revise`. That is **precisely the
set of tools defined in `EngramMcpTools.cs`** — set equality, not an approximate overlap. The golden
file is not missing `start`/`status`/`stop` by oversight; it has never covered `EngramServerTools`
at all. The same boundary appears a third time: `McpToolSurfaceBudgetTests.ToolMethods()` reflects
over `typeof(EngramMcpTools)` only, with `ExpectedToolCount = 8`, and its own comment names the gap —
"EngramServerTools's three tools (start/status/stop) are argued as cost in D17 but are not reflected
over by `ToolMethods()` and are not counted in this figure — a separate, unmeasured gap."

**After D-5 these three scopes coincide with the profile split**, which makes the situation clearer
but does not resolve it:

- The golden file now describes exactly the `default` profile. That is a useful property and should
  be stated wherever the file is regenerated, so it is not lost the next time a tool is added.
- Whatever the golden file does not cover has its descriptions **unguarded against drift**, which is
  that file's only job. `start`/`status`/`stop` are unguarded today, and remain so unless widened.
- The tier-2 byte-diff test in this spec baselines against the golden file. Deciding its scope must
  precede writing that test, not follow it.

This is flagged for the Implementor as a decision to surface, not one to make silently in passing.

## Invariants preserved

- **D51**: any profile that includes `engram_remember` ships its full, unmodified
  description — profiles trim which *tools* appear, never truncate an included tool's
  description. `engram_remember`'s durability trigger stays unconditional regardless of
  profile. **This is now load-bearing for D-1, not merely preserved**: it is what forces
  `engram_judge` into `default`.
- **D15**: no tool names in primer guidance. Verified intact — `PrimerBuilder.EnrollmentLine`
  names no tool, which is why D-5 rests on capability rather than on a dangling name.
- **D33**: profile stored via `ConfigEditor`'s marker convention.
- **D37**: `doctor` reports the active profile; never changes it, and an unusual profile
  choice is `Warn`, never `Broken`.
- **D17**: the tool surface is a budget. `McpToolSurfaceBudgetTests` bounds the *definition*;
  this feature bounds the *delivery*. Neither substitutes for the other — see Hazards 1–2.
- **Append-only facts**: unchanged, and now cited as the reason the third tier is empty (D-2).

## Hazards for the Implementor

1. **The budget test cannot see this feature working.** `ToolMethods()` reflects over
   `EngramMcpTools` only, so it is blind to `start`/`status`/`stop` — which after D-5 are *exactly*
   the tools a profile removes. It will pass identically whether profiles work, are broken, or are
   never wired up. Do not read it as evidence of anything about profiles.
2. **The budget test measures a different quantity than this spec.** It bounds the *defined*
   surface (profile-independent, since it reflects over types); profiles bound the *delivered*
   surface per connection. Nothing currently measures delivered cost — that is what D-3 adds.
3. **If you widen `ToolMethods()` to 11, `MaxDefinitionChars` must be re-baselined in the same
   commit with a stated reason.** The test's own comment requires it: "Raising this number is a
   deliberate edit that needs a reason in the commit message, not a knob to turn." `ExpectedToolCount`
   would go 8 → 11. If you choose *not* to widen it, say why in the commit — leaving it silently at 8
   is the outcome that reads as an oversight later. Note D-3's authoritative re-measurement needs
   this widening, so the two decisions are coupled.
4. **Do not resolve D-4's first class by editing the referring description.** Trimming the
   `engram_judge` sentence out of `engram_remember` inverts D51. See D-1's rejected alternative.
5. **The delta is ~846 B / ~235 tokens, not 1,028 B / ~286.** Revisions 1–2 of this spec and the
   original measurement predate D-5. If your measured figure is near 1,028, `index_repo` is still
   being counted as lifecycle — check the mapping before adjusting the number.
6. **Stamp the telemetry profile at connection time, from the same read that selected the profile.**
   Not by re-reading config when the record is written: the config file can change between the two,
   and a stamp that disagrees with the live connection is worse than no stamp.
7. **Do not restore a tool to lifecycle to make a count come out.** The rule is the definition
   (D-1); the enumeration is derived. Fitting the number is how the boundary rots.
8. **The tier-1 falsification must name a tool that is actually in lifecycle.** Revision 2's test
   said "remove `index_repo` from the lifecycle-tools list" — after D-5 that removes nothing and the
   guard would pass with the mapping broken. Use `stop`. This is the silently-no-op falsification
   class the repo has already paid for once.

## Telemetry requirement

Record the active profile on the `session-open` telemetry record (`ServeCommand.cs:217`,
`TelemetryEventKind.SessionOpen`), as a **new** nullable field — e.g. `tool_profile` — never by
overloading an existing one (D43).

The general reason holds: a profile makes some tools unadvertised, so their telemetry kinds become
unemittable, and a reader of `telemetry.jsonl` cannot then distinguish "the model never reached for
this tool" from "this session never offered it." D56 records the general form: "a kind that is
declared but never emitted is a feature that reads as switched off."

**D-5 substantially weakened the present-tense case for this, and the honest version is worth
stating rather than leaving revision 2's justification standing.** Before D-5, `index_repo` was
excluded under `default` and was the one adoption-relevant casualty. After D-5 the only tools whose
kinds become profile-conditional are `start`/`status`/`stop` — the three least adoption-relevant
tools in the surface. `recall` and `remember`, which are what D18/D43 actually read, are in **both**
profiles, so the headline adoption number was never at risk and is now further from it.

It stays a requirement rather than becoming a follow-up, on two grounds and not on the strength of
the present ambiguity: the cost is one nullable field, and D46's precedent is that an unrecorded
field is not recoverable later ("nothing retroactive is recoverable"). What it principally buys is
insurance against a *future* profile change — if the lifecycle set ever grows to include something
adoption-relevant, the stamp is already there and the historical data is already interpretable.
That is a weaker justification than revision 2 claimed, and it is still worth one field.

## Tests by tier (D9)

- **Tier 1**: profile → tool-set mapping as a pure function over the two defined profiles.
  Falsify: remove `stop` from the lifecycle-tools list without updating the mapping, confirm a test
  asserting `full` contains all 11 tools starts failing. (Do **not** use `index_repo` — see Hazard 8.)
- **Tier 1**: the mapping is derived from the exclusion list, not a literal (D-1). Falsify: add a
  ninth tool to `EngramMcpTools` in test and confirm it appears in `default` with no mapping edit.
  A test that enumerates eight literal names passes with the rule replaced by a hardcoded list, which
  is the defect D-1 exists to prevent.
- **Tier 2**: an MCP connection under `[mcp] tool_profile = default` registers exactly 8
  tools; under `full`, exactly 11. Falsify: hardcode full-set registration regardless of
  config, confirm a test connecting under `default` and asserting 8 tools starts failing.
- **Tier 2**: golden-file byte-diff — `engram_remember`'s description is byte-identical whether
  connected under `default` or `full`. Falsify: truncate it under one profile path in test,
  confirm the byte-diff test catches it. (Resolve the golden file's scope first — see "Reference
  artifacts and their scope" — since it decides what this baselines against.)
- **Tier 2**: reference integrity, D-4 first class. For each tool *not* in a profile, no description
  shipped in that profile contains that tool's name. Falsify by excluding `engram_judge` from
  `default` and confirming it reddens — **not** by asserting the currently-known pairs, which passes
  with the general property broken.
- **Tier 2**: `session-open` carries the active profile, and carries it from the connection's own
  profile read rather than a fresh config read (Hazard 6). Falsify: change config between connection
  and record write, confirm the recorded value follows the connection.
- **Tier 3**: end-to-end MCP connection against the published binary under each profile,
  asserting the tool list Claude Code would see. Per the repo's tier-3 rule, confirm this does not
  land in the skip column — a skipped tier 3 is not a pass.

## Measurements

- **Answered** (NEEDS-EVIDENCE #1): pre-D-5 `default`(6, pre-judge) 2,472 B; `full`(10, pre-judge)
  3,500 B; delta 1,028 B; `engram_judge` 364 B; `engram_index_repo` ≈182 B.
- **Current figure**: post-D-5 delta ≈846 B ≈ ~235 estimated tokens, now **exact** on the
  parameter-inclusive basis rather than a lower bound (D-3).
- **Outstanding, non-blocking**: confirm ≈846 via `McpToolSurfaceBudgetTests`'s existing
  `DescriptionLength` summation, reporting *characters* rather than bytes (D-3). This refines the
  number the feature reports; it does not gate the design. Coupled to Hazard 3.

## Deferred follow-ups

Recorded as deferred, not rejected — each with its reason, so a later reader can tell a decision
from a silence.

1. **Widening the golden file / budget test to cover `EngramServerTools`.** Deferred because it is a
   pre-existing D17 gap that this feature reveals rather than creates, and resolving it changes a
   baseline the tier-2 test depends on — it wants its own decision, not a drive-by. Note D-3's
   authoritative re-measurement needs the budget-test half of this.
2. **`engram_judge` in `ClaudePermissions.GrantedTools`.** Deferred because permissions are an
   explicitly orthogonal axis (Non-goals) and belong to spec 02's territory; judge will prompt under
   `default` until someone decides otherwise, which is correct-by-default for a tool that writes.
3. **A per-connection profile override.** Deferred because no driving use case exists; the config key
   matches the actual cadence of change (Design).

## Open questions / NEEDS-EVIDENCE

**All closed. Nothing outstanding before implementation starts.**

1. ~~**[measurement]** Byte/token delta between `default` and `full`.~~ **Answered**, then revised by
   D-5: ≈846 B, ~235 estimated tokens, exact rather than a floor. See D-3.
2. ~~**[verify]** Reconcile the golden file's tool-name list against its stated count.~~
   **Answered, and the question was malformed**: the file states no total. It lists 8 names, exactly
   equal to `EngramMcpTools`'s 8 tools. See "Reference artifacts and their scope".
3. ~~**[verify]** Confirm no existing config-set verb is gated behind `--apply`.~~ **Confirmed**:
   `ModelCommand` (`model install --use-it`) and `InitCommand` both write `config.toml` via
   `ConfigWriter.Apply` immediately. `PermissionsCommand` is the only `--apply`-gated verb and it
   modifies Claude Code's settings file, not `config.toml` — a different thing, not a counterexample.
   `profile set` acting immediately is validated by precedent.
4. ~~**`index_repo`'s placement.**~~ **Decided — D-5.** It moves to `default`.
5. ~~**Whether the measured saving justifies the mechanism.**~~ **Decided — D-6.** Ship, with the
   figure-moved note.
6. ~~**Whether a third profile is ever wanted.**~~ **Superseded by D-2**, which replaces the original
   size-based rejection with an invariant-based one and states a tripwire for reopening.

## Confidence

High on D-1 — it rests on a cited invariant (D51) plus a verbatim line of shipped code, not on
taste, and the alternative is forbidden rather than merely worse. High on D-2 and D-4.

**Medium-high on D-5**, and the qualification is deliberate: the conclusion is sound but the
argument had to be rebuilt after its proposed premise failed verification, and the surviving
argument ("an unfollowable instruction is a defect") is weaker than the one it replaced ("D51
forbids fixing the referrer"). It is still, in my judgement, sufficient — a conditional instruction
the model cannot comply with is a real defect, and arguments 2 and 3 stand independently. Someone
who disagrees should say so now rather than after implementation; the cost of reversing is one
mapping entry and the delta figure.

Medium on D-3's absolute numbers, which carry a hand count and a stated re-measurement.

No escalation to the Ultra-Advisor is recommended. Nothing here touches auth, data migration,
concurrency, or a public interface; the mechanism is a reversible config key.
