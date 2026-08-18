# 03 — Tool profiles

Status: design, revised. Parent: `docs/memory-expansion-spec.md` row 3.

## Goal

A profile selects which MCP tools a session's server connection *advertises*, trimming the
prompt-token cost of tool schemas. Lifecycle tools (`start`/`status`/`stop`, `index_repo`)
leave the default set. Token cost becomes a measured line item.

## Non-goals

- Not a permissions mechanism — `ClaudePermissions.GrantedTools` (client-side pre-approval,
  avoids a confirmation prompt) is untouched and orthogonal; see Design for why these are
  two different axes.
- No dynamic per-call profile switching — a profile takes effect on the next MCP
  connection, matching how every other config change already behaves.
- Not mirroring a comparable tool's own profile grouping (see Design) — the constraint's
  Engram-specific split is followed as given.

## Inspiration

A comparable memory tool lets a per-connection flag select a smaller tool set for routine
use versus a larger one that includes destructive/admin operations, cutting the token cost
of tool schemas sent to the model. Engram's version below picks a different split, sized to
its own much smaller tool inventory, and a different selection mechanism suited to how
Engram is packaged.

## Design

**Two axes, not one.** Today, `ClaudePermissions.GrantedTools` = `{engram_recall,
engram_remember, engram_status, engram_browse, engram_expand, engram_index_repo}` (6 of 10
tools) — this controls whether Claude Code prompts for permission before a call. It does
**not** reduce token cost: all 10 tool schemas (`Recall, Remember, Forget, Revise, Expand,
Browse, IndexRepo, Start, Status, Stop`) are sent to the model regardless of grant status.
A profile is a genuinely different, currently-unsolved axis: which tools the MCP server
*registers at all* for a given connection, which is what actually removes schema bytes from
the model's context. The two mechanisms are independent and both remain in force —
excluding a tool from a profile makes `GrantedTools` moot for that session (nothing to grant
if it isn't advertised); including it in a profile still respects whatever `GrantedTools`
says about prompting.

**Two profiles, not three.** The constraint names exactly one group that leaves the default
set: `{start, status, stop, index_repo}` — call this "lifecycle." The complement is
`{recall, remember, forget, revise, expand, browse}` — call this "default" (6 tools).
Because Engram has only 10 tools total, split cleanly along this one named boundary, a
profile that adds lifecycle tools back is identical in membership to "all tools." Final
profiles:
- **`default`** (6): `recall, remember, forget, revise, expand, browse`.
- **`full`** (10): default + `start, status, stop, index_repo`.

A third, finer-grained tier (splitting off e.g. `forget` as admin-only, mirroring an
agent-vs-admin style split seen in a comparable tool) was considered and rejected: that kind
of split makes sense on a much wider tool surface with its own destructive and aggregate
operations — Engram's 10-tool inventory doesn't have an equivalent destructive/aggregate
cluster large enough to justify a second cut. A comparable tool's own split carves a *small*
admin tier out of a *large* general-purpose set; Engram's already-small 6-tool default is
closer in spirit to that general-purpose tier than to something needing further division.

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
destructive verbs; a profile choice is freely reversible Engram-side preference, matching
how other config-set verbs already behave). **Verify** before implementation: confirm no
existing config-set verb actually *is* gated behind `--apply` — this spec assumes not, based
on `RepairCommand`'s convention being specific to state-mutating verbs, but was not directly
checked against a config-set verb.

**Schema delta**: none. Profile is a config value read at connection time, not stored in
the database. `doctor`/`engram_status` can report the active profile by reading config
directly (D37: reads, never repairs).

## Invariants preserved

- **D51**: any profile that includes `engram_remember` ships its full, unmodified
  description — profiles trim which *tools* appear, never truncate an included tool's
  description. `engram_remember`'s durability trigger stays unconditional regardless of
  profile.
- **D33**: profile stored via `ConfigEditor`'s marker convention.
- **D37**: `doctor` reports the active profile; never changes it, and an unusual profile
  choice is `Warn`, never `Broken`.

## Tests by tier (D9)

- **Tier 1**: profile → tool-set mapping as a pure function over the two defined profiles.
  Falsify: remove `index_repo` from the lifecycle-tools list without updating the mapping,
  confirm a test asserting `full` contains all 10 tools starts failing.
- **Tier 2**: an MCP connection under `[mcp] tool_profile = default` registers exactly 6
  tools; under `full`, exactly 10. Falsify: hardcode full-set registration regardless of
  config, confirm a test connecting under `default` and asserting 6 tools starts failing.
  Golden-file byte-diff test: `engram_remember`'s description is byte-identical whether
  connected under `default` or `full`. Falsify: truncate it under one profile path in test,
  confirm the byte-diff test catches it.
- **Tier 3**: end-to-end MCP connection against the published binary under each profile,
  asserting the tool list Claude Code would see.

## Measurements

- Exact token/byte count of the `default` (6-tool) vs `full` (10-tool) schema set, using
  `docs/mcp-tool-descriptions.golden.txt` as the base. The golden file's own current tool
  coverage should be confirmed as part of this measurement (see NEEDS-EVIDENCE #2) — an
  earlier mechanical pass over it reported an inconsistent count (7 names listed against a
  stated total of 8), which this spec did not resolve before finalizing.

## Open questions / NEEDS-EVIDENCE

1. **[measurement]** Byte/token delta between `default` and `full` tool sets — the
   "measured line item" this feature exists to produce. Build both lists, sum description
   bytes (and ideally run through the actual tokenizer Claude Code's context uses), report
   the delta.
2. **[verify, non-empirical]** Reconcile the golden file's tool-name list against its stated
   count (7 named vs "8 tools" reported) before using it as the measurement baseline.
3. **[verify, non-empirical]** Confirm no existing `engram <thing> set`-style config verb is
   gated behind `--apply`, to validate the "profile set acts immediately" decision above.
4. **Product-scope question, not evidence**: whether a third, finer-grained profile is ever
   wanted (e.g. `index_repo` without `start`/`stop`, or an admin-style carve-out as `forget`
   grows teeth) is left to the Orchestrator — not invented here for lack of a driving use
   case; a comparable tool's own admin-tier split only appears at a much larger tool count,
   suggesting Engram isn't there yet.
