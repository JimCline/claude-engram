# The path grammar for indexed code

`grammar_version = 1`

This document is the authority for how code subjects are addressed. It is versioned
because paths are promises: recall's cheapest operation is a prefix range scan over
them, and every fact the indexer writes is filed under one. A grammar change therefore
re-addresses a subtree, so it must be visible, deliberate, and detectable — the indexer
records the version it wrote with (in `schema_meta`, alongside the analyzer version),
and a mismatch on a later run forces a full re-index rather than leaving two grammars
interleaved in one store.

## The shape

```
/projects/<project>/code/<repo>                      the repo         kind=repo
/projects/<project>/code/<repo>/<rel/path>           a file           kind=file
/projects/<project>/code/<repo>/<rel/path>#<frag>    a section or     kind=doc-section
                                                     symbol in it     kind=symbol
```

A codebase is addressed inside its project, not beside it (D27): a project may hold
several codebases and a codebase belongs to exactly one project, and nesting makes
"this project's decisions and its code" one indexed range scan instead of a union of
two prefixes plus a mapping. `<repo>` stays even when a project has a single codebase —
eliding it would re-address every fact the day a second repo joins, and `path` is
mutable only to follow an entity on rename (D2).

## Segments

- **`<project>`** defaults to the repo's own directory name, so a solo codebase lands
  at `/projects/engram/code/engram/…` with no configuration. `[indexing]
  project = "name"` re-binds it declaratively when a project genuinely spans several
  repos. The default is consulted only at first registration; after that
  `repo_registry.repo_path` is the durable address, because guessing twice is how a
  subtree gets re-addressed by accident.
- **`<repo>`** is the basename of the repo's identity — the normalized git remote URL
  minus `.git`, or the root directory name where there is no remote. If the name is
  already registered to a *different* identity, a numeric suffix (`-2`, `-3`, …) is
  assigned once and kept: lookup is by identity, so the suffix never migrates.
- Both are slugged the same way: lowercased, every run outside `[a-z0-9]` collapsed
  to a single `-`, leading/trailing `-` trimmed.
- **`<rel/path>`** is the repo-relative file path verbatim — case preserved, `/`
  separators. It is not slugged: it must round-trip to the file on disk, and git
  already guarantees it is a usable identifier.

## Fragments

- A **doc section** is the heading text slugged as above; nested headings join with
  `/`: `README.md#install/prerequisites`. The slug is address, not display — the
  heading as authored lives on the entity's display name (the same split the primer
  already relies on for topics).
- A **symbol** is the declared name verbatim, nested scopes joined with `/`:
  `FactStore.cs#FactStore/Remember` would be the deep-tier form; the universal tier
  writes top-level names only (`FactStore.cs#FactStore`).
- Version 1 deliberately has **no overload disambiguation and no signature grammar**.
  The universal tier writes one symbol entity per distinct name, which is the honest
  resolution of what a line-level analyzer can actually see. When the deep tier can
  resolve overloads, that is grammar v2, and existing entities are **re-keyed by
  adopt/merge, not duplicated** (D2): the deep analyzer resolves a symbol to the
  existing entity by name and span, keeps its `entity.id`, corrects the path, and
  files the old path as an alias in `entity.meta`.

## What a path is not

The path is addressing, not belief and not lifecycle. Identity is `entity.id`; a
rename moves the path and keeps the entity (D2). Trust is `learned_via`; whether a
fact may be destroyed and recomputed is `regenerable` (D23); how long it lives is
`scope` — all carried on the fact, none derivable from the path (D27 removed the last
pretense of that). A consumer that infers any of those from the prefix is wrong today
or will be after the next grammar version.
