# Engram vs. Graphify

Analysis of [`Graphify-Labs/graphify`](https://github.com/Graphify-Labs/graphify), 2026-08-05.
All repository figures below were read from the GitHub and PyPI APIs on that date, not from
the README's own claims.

## What Graphify is

> "Turn any codebase, with its docs, SQL schemas, configs, and PDFs, into a queryable
> knowledge graph. A `/graphify` skill for Claude Code, Cursor, Codex, and Gemini CLI:
> local deterministic AST parsing, every edge explained, no vector store."

Python 3.10+, installed via `uv tool install graphifyy`. It parses a repository with
tree-sitter across 40+ languages, resolves the results into a node/edge graph persisted as
`graph.json` (512 MB default cap), runs Leiden community detection over it, and serves the
result to an agent over MCP — `query_graph`, `get_node`, `get_neighbors`, `shortest_path`,
`list_prs`, `get_pr_impact` — alongside a CLI (`extract`, `query`, `path`, `explain`) and a
`/graphify` skill. Code is parsed locally; documents, PDFs, and images are sent to a
configured LLM for semantic extraction. Edges are tagged `EXTRACTED` (read directly from an
AST) or `INFERRED` (resolved by heuristic or model) and carry a confidence score.

## Maturity, read honestly

| | |
|---|---|
| Created | 2026-04-03 — four months old |
| Version | v0.9.33 (pre-1.0), 201 PyPI releases, default branch `v8` |
| Commits | 1,344 |
| Stars / forks | 102,663 / 9,964 |
| Watchers | 348 |
| Open issues | 803 |
| Contributors | 100+, but the top author has 946 of 1,344 commits |
| License | Apache-2.0 (dual MIT) |

Two ratios are worth noticing before treating the star count as evidence of anything.
Watchers-to-stars here is roughly **1:295**; a repository with genuine sustained usage
typically sits nearer 1:20–30. And one contributor wrote 70% of the commits in four months.
This is the signature of a launch that went viral — the README's Y Combinator affiliation
and hosted `app.graphify.com` fit that reading — rather than of a tool with years of
production hardening behind it. That is not a criticism of the code, which may be excellent;
it is a caution against reading "102k stars" as "solved problem, don't compete."

## The two projects are not competitors

The overlap is narrower than the surface similarity suggests. Both build a graph, both speak
MCP, both attach to Claude Code. They point at different objects.

| | Graphify | Engram |
|---|---|---|
| **Subject** | Code and documents, as they exist *now* | Beliefs about the user, project, and past sessions, as they evolved *over time* |
| **Time model** | None. A snapshot, rebuilt on commit via git hook | The core of the design — `valid_from`/`valid_to`, supersession chains, append-only facts |
| **Truth revision** | Re-extract and overwrite | A fact is never edited; it is closed and superseded, and the history stays queryable |
| **Ingestion** | Explicit `graphify extract` run, plus a post-commit hook | Passive capture during ordinary sessions; the user never runs an indexer |
| **Storage** | `graph.json`, loaded in memory, 512 MB cap | SQLite, WAL, one file, concurrent processes |
| **Retrieval** | Deterministic graph traversal | FTS5 today, FTS5 + vector fused by RRF under D18 |
| **Runtime** | Python 3.10+, `uv`/`pipx` | Single Native AOT binary, no runtime |
| **Privacy** | Code stays local; docs/PDFs/images go to a configured LLM | Everything local |
| **Scope** | One repository | Cross-session, cross-repository, user-scoped |

The sharpest difference is the second row. Graphify answers *what does this codebase look
like*. Engram answers *what did we decide, when, and what has since replaced it*. A snapshot
graph cannot answer the second question, and no amount of AST parsing gets you there,
because the information was never in the AST — it was in a conversation.

## Where this actually bears on our plan

### 1. It strengthens the gate on M3, and may retire it

M3 (code graph) is the one place the projects genuinely collide. Graphify already does what
M3 proposes — tree-sitter across 40+ languages, incremental re-extraction, PR impact — and
does it as a free, MIT/Apache tool that already speaks MCP to Claude Code.

D6 already says *prove adoption before building the code graph*, and M3's gate says it
shrinks or moves behind M4 if telemetry doesn't show code-structure questions dominating.

An earlier draft of this document proposed a third outcome — *point users at Graphify
instead of building* — on the grounds that consuming an MCP server is not really a
dependency. **That was wrong.** An MCP server is a dependency with a protocol boundary in
front of it: recommending it hands users a Python runtime, an LLM egress path, and a pre-1.0
release cadence, while recording none of it anywhere in this repository. Abstraction changes
who notices the coupling, not whether it exists.

Resolved in **D20**: M3 is built in-house when its gate opens, and no Engram capability may
work only when Graphify is installed. The cost — duplicating freely available work — is
accepted deliberately, because a memory system that quietly needs a Python service to answer
code questions is not the single-binary local tool D1 promises.

### 2. It is a real test of D18, which survives

Graphify's headline claim is *"no embeddings, no vector store: a real graph you traverse"* —
the precise opposite of the decision made in D18 hours earlier. Worth taking seriously
rather than dismissing.

They are right for their problem and it does not transfer. Graphify retrieves over
**structure**: "what calls this function" has a deterministic answer that an edge literally
encodes, so an embedding would only add fuzziness to a question that already has a crisp
one. Engram retrieves over **unstructured natural-language belief**, where there is no edge
to traverse and the dominant failure is vocabulary mismatch — *"what's my kid into"* against
*"son is Liam"*. Their argument is an argument against vectors *as a substitute for available
structure*, not against vectors *as a lexical-recall backstop*. D18 already scopes vectors to
the second role and keeps FTS5 authoritative for exact identifiers.

No change to D18.

### 3. One idea worth stealing: typed edge provenance

Graphify tags every edge `EXTRACTED` or `INFERRED` and attaches a confidence score, so a
consumer can tell a fact read directly out of source from one a model guessed at.

Engram has the raw material for this — captures carry `evidence`, and user statements are
distinguishable from agent-written facts — but does not expose a **provenance tier** on the
fact itself. It should. The recall output is read by a model that currently cannot tell "Jim
said this in his own words" from "an agent concluded this from a diff," and those warrant
different trust. This also bears directly on D5: contradiction detection was cut partly
because two conflicting facts give no principled basis for choosing between them, and a
provenance tier is exactly such a basis — a user statement should win over an inference
without needing to resolve the semantics.

Recommended: a small decision covering a provenance/confidence tier on facts, surfaced in
recall output, and revisit D5 with it in hand alongside the vector evidence.

### 4. "Every edge explained" is the second idea worth stealing

Graphify ships `graphify explain` and makes edge-level justification a headline principle.
Engram has no equivalent, and D18 is about to make that gap materially worse: RRF fuses two
independently ranked lists, so nothing will be able to account for why one fact outranked
another. Since D12 makes *recall coverage* the health metric, missed recalls are the unit of
debugging — and an unexplainable miss can only be re-rolled, not fixed.

Taken up as **D21**, including the sequencing constraint that the explainer ships with or
before the vector lane rather than after it.

### 5. A human-readable view of what is stored

Graphify emits `graph.html` and `GRAPH_REPORT.md` alongside the machine-readable graph. The
equivalent gap in Engram is sharper than it is for a code tool, because the contents are
personal: a user cannot currently see everything Engram knows about them. `doctor` reports
health, not content, and `recall` answers a query rather than enumerating.

Taken up as **D22** — `engram report`, Markdown rather than HTML, including superseded facts
and with no truncation, on the grounds that retraction without enumeration is not a real
control.

### 6. Two smaller notes

- **`BENCHMARKS.md` at the repo root.** Graphify publishes measurements as a first-class
  artifact. Engram's culture already demands numbers over estimates and §1.5 records spike
  results; promoting them to a maintained top-level document is nearly free and makes the
  measured-not-estimated claim legible to anyone arriving cold.
- **Multi-assistant reach.** Graphify targets Claude Code, Cursor, Codex, and Gemini CLI
  from one package. Engram is Claude Code only, which is correct for now — D13 and D14 are
  already deep in Claude Code's plugin and hook specifics — but the ceiling is worth
  recording as a known strategic limit rather than discovering it later.

## Summary

Graphify is not a competitor to Engram; it is a competitor to Engram's *M3*. Its existence is
evidence that M3 should shrink rather than grow, and strong evidence that Engram's claim to
distinctiveness rests on the temporal belief model — the part no snapshot graph can
replicate. Its no-vector stance tests D18 and does not overturn it.

Three design ideas are imported: **typed provenance** (D19), **explainable retrieval** (D21),
and **a readable report of stored content** (D22). One earlier conclusion is reversed —
Engram builds its own code graph rather than recommending Graphify, because an MCP server is
a dependency with a protocol boundary in front of it (D20).
