# 06 — Export to notes

Status: **scratched, 2026-08-20 — Jim's call, not built.** Parent: `docs/memory-expansion-spec.md`
row 6. Left in place as design history; not on the implementation path.

## Goal

`engram export --obsidian <dir>`: one Markdown note per entity path, facts as dated
bullets, supersession rendered, derived and regenerable, never read back. Overwrites only
files it wrote itself (marker), backs up/skips otherwise. Dry-run first.

## Non-goals

- No import-from-Obsidian path, ever — export is write-only from Engram's perspective.
- No new top-level CLI verb — this extends the existing `export` verb with a new output
  format, not a sibling command (a comparable tool instead ships a dedicated separate export
  command for this format; see Design for why Engram diverges).
- No second date formatter, no second path-to-string mapping.

## Inspiration

A comparable memory tool can export its store as JSON, and separately export to an
Obsidian-style note vault, one file per entry, as a beta, export-only feature. Engram's
version below folds the note-vault output into its existing export verb as a flag rather
than a separate command, and addresses notes by Engram's own existing path grammar rather
than inventing a new addressing scheme.

## Design

**One implementation, one new flag, not a third verb.** `export` and `import` are already
top-level CLI verbs in Engram (confirmed from the current verb list: `..., queue, repair,
compact, export, import`). This spec adds `--obsidian <dir>` to the existing `export` verb
rather than a third sibling verb — it shares the existing "walk every entity and its fact
history" iteration, differing only in the writer: a JSON serializer (existing) vs. a
Markdown-note writer (new). A comparable tool's own separate export command likely reflects
its own multi-entity-kind export bundle (several distinct record types) needing more
per-format surface than Engram's single-entity-kind (`fact`) export does; a flag on one verb
is the smaller, sufficient version of the same idea for Engram's simpler domain.

**One note per entity path, mirrored as a directory tree.** Filenames mirror the path
grammar's own segments as nested directories under `<dir>` (e.g.
`<dir>/projects/engram/code/engram/src/Engram.Core/FactStore.cs.md`), not flattened with
dashes and not a project/type/slug-style layout. Nesting by path is chosen because Engram's
path *is* the entity's real address (it already matches `docs/engram-path-grammar.md`'s tree
1:1, no new mapping to invent or keep in sync) — a project/type/slug-style scheme, by
contrast, exists specifically for a store whose entries have no path of their own and need
one invented for export. Adopting that scheme here would mean building a second, weaker
addressing scheme beside the one Engram already has.

**Facts as dated bullets**, using `MomentText` — the *same* formatter spec 05 reuses, not a
second one:
```
- **2026-08-17 14:32:05** favorite_color: green
```
**Supersession rendered as strikethrough + anchor link**, using native Markdown/Obsidian
syntax:
```
- ~~**2026-08-14 09:10:00** favorite_color: orange~~ → superseded by [[#2026-08-17-favorite_color]]
```
Supersession is always same-subject-and-predicate (`ux_fact_live`'s own definition), so the
link target is always an anchor within the same note — never a cross-note link for this
specific case. A separate, genuinely cross-note feature falls out of existing data at zero
new schema: any fact with a non-null `object_id` gets an Obsidian wikilink to that entity's
own note (e.g. `relates_to: [[projects/engram/code/widget-service]]`), giving Obsidian's
backlink graph real structure without inventing a new relation concept.

**Derived, regenerable, never read back.** Explicit design statement: nothing in Engram
ever parses these `.md` files. Export is strictly one-way — matching how a comparable tool
frames its own note-vault export as export-only, with no corresponding import path — and the
parent spec's explicit non-adoption of anything read-back-related.

**Overwrite discipline — D33's marker rule extended from a config line to a whole file.**
Every generated note's first line is an HTML comment marker, invisible in Obsidian's
rendered view: `<!-- written by engram -->`. On a later export run, a target file that
exists *without* this marker (a user's own note, or a pre-marker-convention file) is never
overwritten — it is skipped and counted, exactly the same discipline spec 01 uses for sync
conflicts. `--dry-run` (the default; see below) previews exactly which files would be
skipped for this reason *before* any write, which matters here specifically: a naive
`--obsidian` export pointed at a real vault directory could otherwise silently clobber a
user's own notes. Some comparable tools sidestep this by convention, namespacing their own
output under a dedicated root directory; Engram's marker check makes the same guarantee
explicit and enforced rather than relying on convention.

**Dry-run first (D49).** `engram export --obsidian <dir>` reports what would be
written/skipped by default; `--apply` is required to actually write, matching
`RepairCommand`'s exact convention (default-false `apply` flag, explicit switch required).

**Schema delta**: none. Pure read of `fact`/`entity` plus file writes; no new tables.

**CLI/MCP surface**: `engram export --obsidian <dir> [--apply]` only — no new tool, no new
verb.

**Telemetry**: new kind `TelemetryEventKind.Export = "export"`, phases
started/finished/failed (D55 shape, matching `Index`/`Embedding`). No counts inside the
event (D55's explicit rule); counts (files written, files skipped for missing marker) are
reported by the CLI itself, not telemetry.

## Invariants preserved

- **D8**: export never reads its own output back in; output is not authored truth.
- **D33 (spirit, extended)**: marker-gated overwrite, extended from line-granularity to
  file-granularity — stated explicitly as an extension, not a literal reuse of
  `ConfigEditor`.
- **D49**: dry-run default, `--apply` required to write.
- **"One implementation per behaviour"**: extends the existing `export` verb and reuses
  `MomentText` from spec 05 and Engram's own path grammar, rather than adding a new verb, a
  new formatter, or a second addressing scheme borrowed from a differently-shaped data model
  seen elsewhere.

## Tests by tier (D9)

- **Tier 1**: marker-detection logic (marker present → overwrite allowed; absent → skip).
  Falsify: remove the marker check, confirm a test asserting "a user-authored file at the
  target path is preserved" starts failing (the file gets clobbered). Path-to-filename
  mapping (nesting, sanitization). Falsify: flatten instead of nesting, confirm a
  directory-structure test starts failing.
- **Tier 2**: full export against a seeded `SandboxHome`; assert the resulting file tree
  matches the entity tree and supersession links resolve to real in-file anchors.
  Idempotency/stability: re-run export after adding one new fact, assert only the affected
  note's bytes change and every other note is byte-identical to the prior run — proving
  regeneration is stable rather than rewriting everything differently each time. Falsify:
  inject nondeterministic fact ordering into the writer, confirm the byte-stability test
  starts failing.
- **Tier 3**: end-to-end `export --obsidian` against the published binary and a real temp
  directory; a snapshot-based test (mirroring `doctor`'s own "snapshot every file by size
  and mtime" pattern) confirming `--dry-run` (no `--apply`) touches nothing on disk.

## Measurements

None. No hook path, no MCP tool schema change — `export --obsidian` is an on-demand CLI
verb with no token-cost or hook-budget footprint.

## Open questions / NEEDS-EVIDENCE

1. **[verify, non-empirical]** Confirm the existing `engram export`'s current flag surface
   and default (JSON) output shape precisely, so `--obsidian` is added consistently rather
   than guessed from the CLI verb list alone.
