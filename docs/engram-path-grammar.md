# The path grammar for indexed code

`grammar_version = 2`

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
- A **symbol** is the scope chain of declared names, outermost first, joined with
  `/`, each name as written: `FactStore.cs#FactStore/Remember`,
  `Widget.cs#Widget/Inner`. The chain holds type-like containers only — namespaces
  are not segments, because the file path already locates the file and a namespace
  spans files rather than nesting identity. The universal tier still writes top-level
  names only (`FactStore.cs#FactStore`), the honest resolution of a line-level read;
  a top-level v2 address is spelled exactly like its v1 address, which is why the
  v1→v2 bump re-addressed nothing that existed — every v1 extractor was anchored to
  top-level declarations (D48).
- **Overloads** (new in v2): when several declarations in one file share a scope
  chain and a name, each appends its parameter list as written — parentheses
  included, interior whitespace runs collapsed to a single space:
  `Http.cs#Http/Get(string key)` beside `Http.cs#Http/Get(string key, int count)`.
  The suffix appears **only on collision**, so a unique name keeps its stable bare
  form and the arrival of a first overload is the only event that re-addresses a
  sibling. Declarations a syntactic view still cannot separate — same scope, name,
  and written parameter list — share one address, first declaration wins: the same
  rule partial classes already had. Which symbols a tier emits at these addresses is
  extraction policy, decided in D48; the grammar only defines the address a symbol
  gets.
- A grammar bump is detected, never inferred: the version lands in `schema_meta`
  through `code_index_version`, and a mismatch forces a full re-read. Where a bump
  retires an address (v2: a bare name splitting into suffixed overload siblings), the
  facts at it close through the ordinary vanished-symbol path; renames continue to
  move paths whole through `MoveSubtree`, aliasing the old path in `entity_alias`
  (D2).

## What a path is not

The path is addressing, not belief and not lifecycle. Identity is `entity.id`; a
rename moves the path and keeps the entity (D2). Trust is `learned_via`; whether a
fact may be destroyed and recomputed is `regenerable` (D23); how long it lives is
`scope` — all carried on the fact, none derivable from the path (D27 removed the last
pretense of that). A consumer that infers any of those from the prefix is wrong today
or will be after the next grammar version.
