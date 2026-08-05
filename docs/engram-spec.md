# ENGRAM — Temporal Memory & Code Knowledge Graph for Coding Agents

**Design Specification · Rev D · August 2026 · Status: Ready for implementation handoff**

---

## 1. Overview

Engram is a single-binary, fully local memory engine for LLM coding agents. It unifies three kinds of memory that today live in separate, poorly-adopted tools:

1. **Code knowledge graph** — structure and facts about repositories, auto-indexed, rebuildable.
2. **Project memory** — decisions, discovered facts, gotchas, and conventions learned while working in a codebase.
3. **User memory** — machine-wide preferences, tone, style, and cross-project patterns that persist for the lifetime of the user's work.

All memory is **temporal**: facts are append-only, never mutated or deleted. New facts *supersede* old ones, and every supersession records the reason and evidence for the change. Current-state queries return only live facts; the full history of how any belief evolved is one hop away.

### 1.1 Prime directive: context reduction

The system's single success metric is **tokens the host LLM did not have to load**. Memory is a *substitute* for context, not a supplement to it. Every design decision follows from this:

- Retrieval returns distilled fact statements (target ≤ 60 tokens each), never raw source, packed under an explicit token budget.
- Every returned item carries a handle (fact ID / entity ID) so the agent can pull depth *only when needed* via a cheap follow-up call.
- When memory has no answer, the response says so explicitly and names the gap — the agent falls back to natural discovery (reading files, running tools), then **writes what it found back**, so the next session gets a memory hit instead of a re-discovery.
- Session-start injection is a tiny primer (≤ 300 tokens), not a memory dump.

This closes the human-like loop: *recall → use → amend → re-store*, with discovery as the fallback learning path.

### 1.2 Why existing tools failed (design responses)

| Observed failure | Engram response |
|---|---|
| LLM never calls the memory tool | Hooks inject a primer telling the agent memory exists and is cheap; tool names and descriptions are optimized for tool-selection; `recall` is the *first* tool the agent should reach for, and its output proves its value immediately |
| Memory files go stale | No files. Temporal supersession keeps a single live truth per subject; contradiction detection flags conflicts at write time |
| Retrieval floods context | Hard token budgets, distilled statements, handle-based expansion |
| Heavy infra (Postgres, Redis, Docker) rots | One AOT binary + SQLite files. Zero services, zero containers |
| Memory is per-tool, per-project silo | One database, one memory tree: every fact lives on a rooted hierarchical path from broad to specific (`/people/jim/preferences/…`, `/code/acme-api/src/…`), all queryable together |

---

## 2. Goals, Requirements, Non-Goals

### 2.1 Goals

1. **Context reduction as the prime directive** (§1.1).
2. **Temporal truth** — append-only facts, supersession with recorded reasoning, current-vs-history queries.
3. **Unified memory scopes** — code / project / user in one system, one query surface.
4. **Associative organization** — facts link to entities (symbols, files, concepts, decisions, people) and to adjacent facts; retrieval walks out from seeds like human recall.
5. **Claude Code native** — hooks auto-index repos, capture learnings at lifecycle boundaries, inject the primer; MCP does the rest; subagents share memory through scoped handles.

### 2.2 Hard requirements

- **Language/runtime:** C# / .NET 9+, published as Native AOT per platform (win-x64, linux-x64, osx-arm64, osx-x64). No runtime install required. Not single-file: SQLite is loaded at runtime from a library beside the executable, since a static e_sqlite3 is published only for browser-wasm — see D1 in the implementation plan.
- **Storage:** SQLite only (WAL mode). **One database** — `~/.engram/engram.db` — holding the entire memory tree; repos, projects, and user memory are path branches, not separate files. FTS5 for lexical search (built into SQLite). `sqlite-vec` extension loaded *only* when embeddings are enabled.
- **Zero services:** no daemons required for core operation. An optional file-watcher mode exists but is opt-in.
- **Pluggable embeddings:** in-process LLamaSharp (GGUF) *or* any OpenAI-compatible local endpoint (LM Studio, Ollama). Lazy-loaded, idle-unloaded. The system is fully functional with embeddings disabled (lexical + graph lanes).
- **Interfaces:** CLI (`engram …`) for humans; MCP server over stdio (`engram mcp`) for agents.
- **Performance:** recall < 50 ms lexical warm, < 150 ms with vectors warm; incremental git-aware indexing.
- **Portability:** the entire memory state is the `~/.engram` directory — copy it to move machines.

### 2.3 Non-goals

- Not a general vector database and not a Sage replacement. Engram stores *structure, facts, and gist-level impressions* (§5.4) — never verbatim document content. The file itself remains the source of truth; full-text semantic document retrieval stays out of scope.
- No cloud sync, no multi-user, no network exposure beyond localhost stdio/optional local HTTP.
- No LLM required at runtime for core operation. Document impressions default to a zero-dependency extractive method; a generative local LLM is only ever an opt-in refinement (§5.4). Distillation and digest quality for agent-learned facts remain the *host agent's* job, guided by tool descriptions.

---

## 3. System Architecture

### 3.1 Components

```
┌─────────────────────────────────────────────────────────────────┐
│  Claude Code (host)                                             │
│   ├─ Hooks ──────────────► engram hook <event>   (short-lived) │
│   └─ MCP (stdio) ────────► engram mcp            (per-session) │
├─────────────────────────────────────────────────────────────────┤
│  ENGRAM (single AOT binary)                                     │
│   ├─ CLI layer          engram init|index|search|show|…         │
│   ├─ MCP server         stdio JSON-RPC, tools §9                │
│   ├─ Memory engine      temporal store, supersession, salience  │
│   ├─ Retrieval engine   FTS5 + vec + graph walk → RRF → pack    │
│   ├─ Code indexer       Roslyn (C#) + generic indexer (other)   │
│   └─ Embedding provider IEmbedder: None|LLamaSharp|OpenAI-compat│
├─────────────────────────────────────────────────────────────────┤
│  Storage (~/.engram)                                            │
│   ├─ engram.db          whole memory tree: user+proj+code       │
│   ├─ models/            optional GGUF embedding models          │
│   └─ engram.log         structured rolling log                  │
└─────────────────────────────────────────────────────────────────┘
```

There is exactly one process model: the `engram` binary runs in one of three short-lived-or-session-scoped modes — CLI command, hook invocation (must exit fast), or MCP server (lives as long as the Claude Code session). Concurrent access across modes is safe via SQLite WAL + busy-timeout; there is no lock server.

### 3.2 Storage layout & identity

- `~/.engram/engram.db` — **one database, one memory tree**. User, project, and code memory are branches of a single rooted hierarchy (§4.2), not separate files. Deleting a repo from disk deletes nothing here: its branch persists until explicitly forgotten or compacted.
- The **repo registry** (a table) maps a stable repo identity — normalized git remote URL if present, else normalized root path — to its memory path (e.g. `/code/acme-api`) and last-seen disk locations, so a repo that moves or is re-cloned reattaches to its existing memory.
- One database makes cross-cutting queries trivial (no fan-out): "what did we decide about auth *anywhere*" is one query with no filter; "only this repo" is the same query with `path_prefix = /code/acme-api`.
- Memory never lives inside repos: checkouts stay clean, memory stays private. `engram export --path /code/acme-api` produces a portable subtree bundle if project memory should ever be shared deliberately.

### 3.3 Configuration

`~/.engram/config.toml` (created by `engram init` with commented defaults):

```toml
[embedding]
provider = "none"            # none | llamasharp | openai-compat
# -- llamasharp --
model_path = "~/.engram/models/qwen3-embedding-0.6b-q8_0.gguf"
threads = 4
idle_unload_minutes = 5
# -- openai-compat --
endpoint = "http://localhost:1234/v1"
model = "text-embedding-qwen3-embedding-0.6b"
# -- shared --
dim = 1024
max_batch = 16

[retrieval]
default_budget_tokens = 500
seed_k = 32
graph_hops = 2
recency_half_life_days = 45

[indexing]
auto_index_on_session_start = true
max_sync_index_ms = 1500      # beyond this, indexing continues async
ignore = ["**/bin/**", "**/obj/**", "**/node_modules/**", "**/.git/**"]

[impressions]
mode = "extractive"           # extractive (default, zero-dep) | llm
# llm mode refines extractive impressions via an OpenAI-compatible local endpoint
endpoint = "http://localhost:1234/v1"
model = "qwen3-4b-instruct"
max_tokens_per_impression = 60
batch = 8                      # throttled, idle-priority, never blocks indexing

[taxonomy]
# top-level roots of the memory tree; extendable without migration
roots = ["/machine", "/code", "/projects", "/people", "/concepts"]
default_user = "jim"          # preference-kind facts route to /people/<default_user>/…

[primer]
max_tokens = 300
```

---

## 4. Data Model — Temporal Knowledge Graph

One database, one schema. A fact's position in the memory tree is data (`path`), not a schema fork.

### 4.1 Schema (DDL)

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;

-- Entities: the nouns memory is about
CREATE TABLE entity (
  id             INTEGER PRIMARY KEY,
  path           TEXT NOT NULL UNIQUE, -- rooted memory path, broad → specific:
                                       --   /people/jim/preferences
                                       --   /code/acme-api/src/Auth/AuthService.cs#ValidateToken
  kind           TEXT NOT NULL,     -- machine|repo|project|module|file|symbol|concept|
                                    -- decision|convention|preference|person|tool
  name           TEXT NOT NULL,     -- display name (last path segment, denormalized)
  created_at     INTEGER NOT NULL,  -- unix seconds
  meta           TEXT               -- JSON: language, signature, aliases, disk locations
);
CREATE INDEX ix_entity_path ON entity(path); -- subtree = range scan: path >= ? AND path < ?||'\uffff'

-- Facts: append-only temporal statements. NEVER updated except the two
-- closure columns (valid_to, superseded_by), NEVER deleted.
CREATE TABLE fact (
  id            INTEGER PRIMARY KEY,
  subject_id    INTEGER NOT NULL REFERENCES entity(id),
  predicate     TEXT NOT NULL,      -- normalized verb phrase: "uses", "decided",
                                    -- "is located at", "prefers", "returns"
  body          TEXT NOT NULL,      -- distilled statement, target <= 60 tokens
  object_id     INTEGER REFERENCES entity(id),   -- optional: fact points at entity
  path          TEXT NOT NULL,      -- denormalized subject path (prefix-searchable)
  scope         TEXT NOT NULL,      -- user | project | code (derived from root; kept for ergonomics)
  learned_via   TEXT NOT NULL,      -- stated | observed | derived | indexed
  confidence    REAL NOT NULL DEFAULT 0.8,
  evidence      TEXT,               -- "src/Auth.cs:120", "commit a1b2c3", tool ref
  session_id    INTEGER REFERENCES session(id),
  valid_from    INTEGER NOT NULL,
  valid_to      INTEGER,            -- NULL = currently believed
  superseded_by INTEGER REFERENCES fact(id),     -- NULL unless superseded
  created_at    INTEGER NOT NULL
);
CREATE INDEX ix_fact_live    ON fact(subject_id, predicate) WHERE valid_to IS NULL;
CREATE INDEX ix_fact_session ON fact(session_id);
CREATE INDEX ix_fact_path    ON fact(path);

-- The reasoning path of belief revision: why did new replace old?
CREATE TABLE supersession (
  old_fact_id INTEGER NOT NULL REFERENCES fact(id),
  new_fact_id INTEGER NOT NULL REFERENCES fact(id),
  reason      TEXT NOT NULL,        -- "refactored in commit …", "user corrected",
                                    -- "test disproved assumption"
  evidence    TEXT,
  session_id  INTEGER,
  created_at  INTEGER NOT NULL,
  PRIMARY KEY (old_fact_id, new_fact_id)
);

-- Associative structure between entities
CREATE TABLE edge (
  from_id    INTEGER NOT NULL REFERENCES entity(id),
  to_id      INTEGER NOT NULL REFERENCES entity(id),
  relation   TEXT NOT NULL,  -- defines|declares|calls|implements|imports|references|
                             -- part_of|relates_to|learned_with|contradicts|derived_from
  weight     REAL NOT NULL DEFAULT 1.0,
  source     TEXT NOT NULL,  -- indexer | agent
  created_at INTEGER NOT NULL,
  PRIMARY KEY (from_id, to_id, relation)
);
CREATE INDEX ix_edge_to ON edge(to_id);

-- Retrieval-strength bookkeeping ("use it or it fades in rank, never in storage")
CREATE TABLE salience (
  fact_id       INTEGER PRIMARY KEY REFERENCES fact(id),
  access_count  INTEGER NOT NULL DEFAULT 0,
  last_accessed INTEGER,
  confirmations INTEGER NOT NULL DEFAULT 0,   -- times re-asserted/validated
  score         REAL NOT NULL DEFAULT 0.5     -- recomputed lazily on read
);

-- Provenance anchor: everything learned traces to a session
CREATE TABLE session (
  id         INTEGER PRIMARY KEY,
  host       TEXT NOT NULL,          -- claude-code | cli | other
  repo_path  TEXT,                   -- e.g. /code/acme-api
  started_at INTEGER NOT NULL,
  ended_at   INTEGER,
  digest     TEXT                    -- end-of-session summary written via engram_digest
);

-- Lexical lane (always on)
CREATE VIRTUAL TABLE fact_fts USING fts5(
  body, predicate, subject_name UNINDEXED,
  content='', tokenize='porter unicode61'
);

-- Vector lane (created only when embeddings enabled; dim from config)
-- CREATE VIRTUAL TABLE fact_vec USING vec0(fact_id INTEGER PRIMARY KEY,
--                                          embedding float[1024]);

-- Incremental code-index bookkeeping
CREATE TABLE file_state (
  repo_path  TEXT NOT NULL,         -- memory path of the repo, e.g. /code/acme-api
  path       TEXT NOT NULL,         -- repo-relative file path
  blob_sha   TEXT NOT NULL,         -- git blob hash (or content hash if untracked)
  lang       TEXT,
  indexed_at INTEGER NOT NULL,
  PRIMARY KEY (repo_path, path)
);

CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT); -- schema_version, dim, …
```

### 4.2 Hierarchical memory paths (the categorization spine)

Every entity sits at exactly one rooted path in a single memory tree, running broad → specific. The path is simultaneously the **storage key**, a **retrieval lane**, and the **structured way an agent recalls by category** — narrowing the way human recall narrows from "people" to "Jim" to "how Jim likes explanations".

```
/machine                             host, OS, filesystem, installed tools
/code/<repo>/<dirs>/<file>#<symbol>  mirrors real repo structure down to symbols
/projects/<name>/…                   decisions, conventions, status (may span repos)
/people/<name>/…                     /people/jim/preferences/formatting · /people/jim/style
/concepts/<topic>/…                  cross-cutting knowledge, e.g. /concepts/auth/jwt
```

Rules:

- **Broad → specific, always.** A parent path is a real place, not just a folder: facts attach at any depth (`/people/jim` holds identity facts; `/people/jim/preferences/testing` holds one preference).
- **Hierarchy organizes; the graph associates.** Edges (`relates_to`, `learned_with`, `contradicts`) cut across branches freely — categories give structured recall, associations give spreading activation. Both, like human memory.
- **Auto-routing at write time.** `remember` accepts an explicit `path`; when omitted, Engram routes by kind + context: preference-kind → `/people/<user>/preferences/…`; facts about symbols/files in a repo session → `/code/<repo>/…`; decisions → `/projects/<current>/decisions/…`. Routing rules live in config, not code.
- **Prefix operations are cheap.** A subtree query is an indexed range scan. `engram_browse(path)` returns children with fact counts and top-salience facts — a table of contents for memory, never a dump.
- **Deleting a repo on disk deletes nothing in memory.** The `/code/<repo>` branch persists; the registry marks it detached (noted in the primer); `engram compact --path /code/<repo>` prunes only derived, rebuildable rows if the user chooses.
- **Roots are config** (`[taxonomy]`), so new top-level categories appear without a migration.

### 4.3 Temporal semantics

**Write path (`remember`):**

1. Resolve/create the subject entity (fuzzy match on kind + name/qualified name; aliases in `meta`).
2. Check for a **live fact collision**: same `subject_id` + same normalized `predicate`. If found, this is a *revision*, not a new fact:
   - Insert the new fact (`valid_from = now`, `valid_to = NULL`).
   - Close the old fact: `valid_to = now`, `superseded_by = new.id`.
   - Insert a `supersession` row with the caller-supplied `reason` (required on collision — the MCP tool schema enforces it).
3. If the new fact *contradicts* a live fact on a different predicate about the same subject (detected lexically via negation/antonym heuristics, or vector similarity > threshold with opposing polarity when embeddings are on), it is stored **and** a `contradicts` edge is written; `recall` surfaces both with a ⚠ marker so the agent resolves it explicitly with `revise`.
4. Auto-link: `learned_with` edges to other facts written in the same session within a short window; `relates_to` edges to entities mentioned in the body (entity-name spotting against the entity table).

**Read path:** `WHERE valid_to IS NULL` everywhere by default. History is explicit: `expand(fact_id, view: history)` walks the `superseded_by` chain backwards and returns each hop with its supersession reason — the full "how we came to believe this" path.

**Forgetting:** `forget(fact_id, reason)` closes a fact with `superseded_by = NULL` and records the reason in `supersession` with `new_fact_id = 0` (sentinel). Nothing is ever hard-deleted; `engram compact` only prunes derived/rebuildable code-scope rows and vacuums.

### 4.4 Salience (retrieval strength, not truth)

```
score = w_r · recency(last_accessed, half_life)
      + w_u · log(1 + access_count)
      + w_c · log(1 + confirmations)
      + w_v · confidence                 (w_r=0.35 w_u=0.25 w_c=0.25 w_v=0.15)
```

Salience ranks; it never expires facts. A fact untouched for a year still exists and still wins if it is the only match. Recomputed lazily when a fact is scored during recall (cheap, no background job).

---

## 5. Code Knowledge Graph & Auto-Indexing

### 5.1 What the indexer produces

Code indexing is **derived memory**: entities (`file`, `module`, `symbol`), structural edges (`defines`, `calls`, `implements`, `imports`, `references`), and terse facts with `learned_via = 'indexed'` (e.g. *"AuthService.ValidateToken — public method, returns Result<Claims>, called from 7 sites"*). Derived rows are rebuildable and are the only rows `compact` may prune.

Agent-written facts about code (`learned_via = observed|derived|stated`) attach to the *same* entities — this is the join point between "what the code is" and "what we learned about it." When a symbol disappears on reindex, its entity is kept, its `indexed` facts are closed with reason `"removed in <commit>"`, and any agent facts about it are flagged `stale-subject` in recall output so beliefs about deleted code visibly age out instead of lingering.

### 5.2 Analyzer tiers — universal first, depth where available

The indexer is language-agnostic by construction: the **universal tier handles every file in a repo, including documents**. Language-aware tiers never gate anything — they only *deepen* the graph where a richer analyzer exists. Roslyn is not a foundation dependency; it is the first deep-tier plugin behind `IAnalyzer`, chosen because it is the official C# compiler API, NuGet-only, and in-process (no language server, no fragile native libs). Other ecosystems ignore it entirely and lose nothing structural.

| Tier | Handles | Mechanism |
|---|---|---|
| **Universal** (always on) | every file, any language | File/module entities mirroring real structure into `/code/<repo>/…`; top-level symbol extraction via per-language regex packs (functions, classes, exports); import-graph edges. Zero external deps |
| **Document** (always on) | md, txt, adoc, rst, org | Headings become child paths (`…/README.md#Installation`); links become `references` edges; code fences link to the code entities they mention; every doc and section gets a gist-level impression fact (§5.4) |
| **Deep: Roslyn** | C#, VB | `Microsoft.CodeAnalysis` in-process: full semantic graph — signatures, references, call sites. AOT-compatible workspace loading |
| **Deep: plugins** *(later)* | tree-sitter grammars, LSP-backed | Optional dynamically-loaded `IAnalyzer` implementations; never required |

Graceful degradation is the contract: a Python or Godot repo gets files, symbols, imports, and documentation in the graph on day one; adding a deep analyzer later only enriches existing entities at the same paths — it never re-keys them.

### 5.4 Document impressions (gist, not contents)

Documents contribute more than structure: every doc and every heading section gets an **impression** — a terse, gist-level fact (≤ 60 tokens, `learned_via = indexed`) at its hierarchical path. Engram stores what the doc *is about*; the file remains the source of truth for what it *says*.

```
[f610] /code/acme-api/README.md           "covers install, configuration, plugin authoring; assumes .NET 9"
[f611] /code/acme-api/README.md#Install   "dotnet tool install; requires git on PATH"
[f612] /code/acme-api/docs/adr-007.md     "ADR: chose SQLite over Postgres for portability"
```

Generation is tiered like the analyzers:

- **Extractive (default, always on, zero deps):** lead-sentence + keyword-salience extraction (TextRank-style) per doc and per section. Deterministic, instant, good enough to route recall toward the right file.
- **LLM refinement (opt-in):** an OpenAI-compatible local endpoint rewrites extractive impressions into true summaries — batched, throttled, idle-priority, never blocking indexing or recall. Conservative by config (`[impressions]`).
- **Agent gists outrank both:** when the host agent actually reads a doc and `remember`s its own gist (`learned_via = derived`), that fact ranks above indexed impressions in recall and persists across reindexes (flagged `stale-subject` if the doc later changes materially).

Temporality applies to documents like everything else: when a file's blob hash changes, its indexed impression facts are closed with reason `"document changed (<sha>)"` and fresh ones written — the history of what a document used to be about is preserved in the supersession chain.

All analyzers implement:

```csharp
public interface IAnalyzer {
    bool Handles(string path);
    AnalysisResult Analyze(SourceFile file, AnalysisContext ctx);
    // entities, edges, indexed-facts
}
```

### 5.3 Incremental pipeline

```
trigger ─► enumerate (git ls-files + status) ─► diff blob_sha vs file_state
        ─► changed set ─► analyze (parallel, bounded) ─► upsert entities/edges
        ─► close indexed-facts for removed symbols ─► write file_state
        ─► (if embeddings on) queue new/changed fact bodies for batch embed
```

- **Triggers:** `SessionStart` hook (bounded to `max_sync_index_ms` synchronous, remainder continues in a detached child process); `PostToolUse` hook on Edit/Write (queues just the touched files — reindexed on next recall touching that file, or immediately if the queue is small); manual `engram index`.
- First index of a large repo runs fully detached; `engram status` and the MCP `engram_status` tool report progress so the agent knows freshness.

---

## 6. Retrieval Engine — Context-Frugal Recall

### 6.1 Pipeline

```
query ─┬─ Lane L: FTS5 BM25 over live facts (top seed_k)
       ├─ Lane V: vec0 KNN over live facts (top seed_k, if enabled)
       ├─ Lane E: entity-name match (exact/prefix/alias)
       └─ Lane P: path constraint — optional path_prefix narrows every lane;
                  categorical recall can skip search entirely via browse
             │
             ▼
   Graph expansion: from seed subjects, walk ≤ graph_hops over edges
   (weight × relation prior), collect adjacent live facts
             │
             ▼
   Fusion: Reciprocal Rank Fusion across lanes  ×  salience  ×  scope prior
   (repo context boosts project/code scope; global queries boost user scope)
             │
             ▼
   Pack: dedupe (subject+predicate), truncate to budget_tokens,
   emit distilled lines with handles
             │
             ▼
   Side effects: bump salience access counts; log recall to session
```

### 6.2 Output contract (what the agent sees)

```
RECALL "token validation" · repo:acme-api · 7 facts · 214/500 tokens · coverage: high
[f412] AuthService.ValidateToken decided → use Result<Claims>, never throw
       (project · 12d · ev: PR #84)
[f388] token refresh handled in RefreshWorker, NOT in AuthService (project · 30d)
[f501] ⚠ conflicts with [f388]: "refresh moved into AuthService"
       (project · 2d · unresolved — use engram_revise to settle)
[e77]  AuthService — class, 14 members, src/Auth/AuthService.cs
…
gaps: no facts about rate limiting on token endpoints
→ expand(id) for history/related/evidence · remember() what you discover
```

Rules: every line ≤ 1 sentence; handles on everything; explicit `coverage` estimate (high/partial/none) computed from lane agreement and score mass; a `gaps` line when coverage < high, naming what to discover — this is the instruction that trains the *discover → remember* fallback loop. `coverage: none` returns in < 5 lines. The tool never returns raw source; `expand(entity, view: source)` returns a bounded snippet only on explicit request. Categorical recall needs no query at all: `engram_browse(path)` walks the tree broad → specific, returning children, fact counts, and top facts — a table of contents, memory-palace style.

### 6.3 Session primer (hook-injected, ≤ 300 tokens)

Assembled at SessionStart: 3–5 top user-scope preferences (by salience), 3–5 top project facts, index freshness, and one standing instruction: *"Engram memory is available and cheap. Call engram_recall before exploring files; remember() new durable facts as you learn them; revise() when you find something outdated."* This primer is the adoption mechanism — the reason the LLM actually uses the tools.


---

## 7. Embedding Providers (Pluggable, Conservative)

```csharp
public interface IEmbedder : IAsyncDisposable {
    int Dim { get; }
    ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
```

| Provider | Mechanism | Resource posture |
|---|---|---|
| `NullEmbedder` (default) | — | Zero. Lexical + graph lanes only; system fully functional |
| `LLamaSharpEmbedder` | GGUF in-process (LLamaSharp, Metal/CUDA/CPU). Recommended: qwen3-embedding-0.6b q8_0 (~640 MB) | Lazy-loaded on first embed call; unloaded after `idle_unload_minutes`; `threads` capped from config; batch ≤ `max_batch` |
| `OpenAiCompatEmbedder` | HTTP to LM Studio / Ollama `/v1/embeddings` | Zero in-process cost; degrades gracefully (falls back to lexical + queues texts for later embedding) if the endpoint is down |

Embedding is **write-time and batched** (index queue + remembered facts), never blocking a recall. Query embedding is the only inline call, skipped when the provider is cold and `coverage` from lexical alone is already high — a warm-up embed is queued instead. Vector dim is pinned in `schema_meta`; changing models requires `engram embed --rebuild`.

---

## 8. Subagent Memory Sharing

Claude Code subagents inherit the MCP server, so tools are already reachable; sharing is about **provenance and scope**, not transport.

- `engram_share(paths?, note?) → token` — the parent mints a short-lived share token binding: session id, allowed path prefixes (default: whole tree), and an optional note ("investigating auth bug").
- The parent passes the token in the subagent's prompt; the subagent calls `engram_join(token)`.
- Joined subagents read the same memory (within scope) and their writes carry the parent session's provenance plus a `via_subagent` marker — so a digest at session end sees everything the whole agent tree learned, and history shows *which* worker learned each fact.
- No token → tools still work but default to read-mostly within the current repo's `/code` branch, keeping accidental unscoped subagent writes out of user memory. "Willing sharing" is therefore a one-line act by the parent.

---

## 9. MCP Server & Tool Surface

`engram mcp` speaks MCP over stdio. Tool count is deliberately small (10) and every description is written for tool-selection ("check memory **before** reading files…"). All outputs follow §6.2's compact contract.

| Tool | Args (required*) | Returns |
|---|---|---|
| `engram_recall` | `query*`, `path_prefix`, `scope`, `budget_tokens`, `k` | Packed facts + handles + coverage + gaps |
| `engram_remember` | `statement*`, `subject`, `subject_kind`, `path`, `scope`, `learned_via`, `evidence`, `supersedes_fact_id`, `reason` | New fact id; supersession/contradiction notices |
| `engram_browse` | `path*`, `depth` | Children + fact counts + top-salience facts — memory table of contents |
| `engram_revise` | `fact_id*`, `statement*`, `reason*`, `evidence` | New fact id (explicit belief revision) |
| `engram_forget` | `fact_id*`, `reason*` | Confirmation (tombstone, never delete) |
| `engram_expand` | `id*`, `view*: history\|related\|evidence\|source` | Bounded detail for one handle |
| `engram_code` | `query*`, `view: definition\|references\|callers\|summary` | Graph answer with entity handles |
| `engram_digest` | `learnings*[]`, `session_summary` | Batch write + session close prep |
| `engram_share` / `engram_join` | see §8 | Token / join ack |
| `engram_status` | — | Index freshness, counts, embedder state, DB sizes |

Design notes: `remember` on a live-fact collision *without* `reason` returns a soft error naming the colliding fact and asking for a reason — supersession reasoning is structurally enforced, not hoped for. `digest` accepts up to 25 learnings in one call so end-of-session capture costs one tool call.

---

## 10. Claude Code Integration

### 10.1 Hooks (installed by `engram install claude-code`)

| Hook event | Command | Behavior |
|---|---|---|
| `SessionStart` | `engram hook session-start` | Open session row; incremental index (≤ `max_sync_index_ms` sync, rest detached); emit primer (§6.3) as `additionalContext` |
| `PostToolUse` (Edit\|Write\|MultiEdit\|NotebookEdit) | `engram hook file-touched` | Queue touched files for reindex; O(ms), never blocks |
| `PreCompact` | `engram hook pre-compact` | Inject one instruction: flush durable learnings via `engram_digest` before context is compacted |
| `SessionEnd` / `Stop` | `engram hook session-end` | Close session row; if no digest was written, log it (visible in `engram doctor`) |
| `UserPromptSubmit` *(opt-in, off by default)* | `engram hook prompt-recall` | ≤ 150-token relevant recall injected as context; disabled by default to honor the no-flooding principle |

The installer writes hook entries into `~/.claude/settings.json` (or `--project` → `.claude/settings.json`) and registers the MCP server in the appropriate `.mcp.json`. `engram install claude-code --dry-run` prints the JSON without writing. Uninstall is symmetric.

### 10.2 Session flow

```
SessionStart ─ hook ─► index Δ + primer(≤300 tok) ─► agent works
   │  recall() before exploring ── hit ─► use handles, expand() as needed
   │                             └ miss ─► discover (read/run) ─► remember()
   │  edits ─► PostToolUse hook queues reindex
PreCompact/SessionEnd ─► engram_digest(learnings[]) ─► session closed, memory grew
```

---

## 11. CLI Reference

```
engram init                      create ~/.engram, config, global.db
engram install claude-code       write hooks + MCP registration [--project|--global|--dry-run]
engram index [path] [--full]     index repo (incremental by default)
engram watch [path]              optional foreground file-watcher indexing
engram search <query> [--path]   human-facing recall (same engine as MCP)
engram browse [path] [--depth]   walk the memory tree (table of contents)
engram show <id>                 fact/entity detail
engram history <fact-id>         supersession chain with reasons
engram remember|revise|forget    manual memory ops (same semantics as MCP)
engram status                    freshness, counts, sizes, embedder state
engram doctor                    integrity checks, orphan scan, config lint, hook health
engram compact [--path]          prune derived (rebuildable) rows, VACUUM
engram export|import [--path]    portable subtree bundle (JSONL + manifest)
engram embed --rebuild           re-embed after model/dim change
engram report                    static HTML dashboard into ~/.engram/report/
engram mcp                       run MCP server on stdio
engram hook <event>              hook entrypoints (fast-exit)
```

---

## 12. Non-Functionals, Monitoring, Trade-offs

**Performance targets:** recall p50 < 50 ms (lexical), < 150 ms (with warm vectors); `remember` < 20 ms; hook `file-touched` < 10 ms; incremental index of a 2k-file repo with 20 changed files < 1 s; cold binary start < 100 ms (AOT). **Footprint:** binary ≈ 15–40 MB; engram.db grows with repo count (tens of MB to low GB, compactable); RAM near-zero except during LLamaSharp residency (bounded, idle-unloaded).

**Monitoring:** `engram status` (one screen), `engram doctor` (actionable checks), structured log at `~/.engram/engram.log` (rolling, 10 MB × 3), `engram report` (static HTML: memory growth, supersession activity, recall hit/coverage rates, top entities). Recall coverage rate over time is *the* health metric — it measures whether memory is actually substituting for context.

**Key trade-offs (accepted):**

| Decision | Cost | Why accepted |
|---|---|---|
| SQLite over graph DB | Multi-hop walks are app-side recursive queries | Zero-dependency, portable, WAL concurrency is enough for 1 user + N agents |
| Facts distilled at write time by the host agent | Quality depends on agent discipline | Keeps Engram LLM-free; tool schemas + primer enforce shape; `doctor` flags bloated facts |
| Single database for all memory | One file grows large; corruption has a bigger blast radius | Memory survives repo deletion; zero fragmentation; cross-cutting queries are one indexed scan; mitigated by WAL, `export` backups, and plain file copy |
| No background daemon | First recall after big external changes may hit a stale index | Freshness reported honestly in `status` + primer; watcher exists for those who want it |
| Regex-tier generic indexer | Shallower graph for non-C# langs | No fragile native deps; tree-sitter stays optional plugin |

**Open questions for implementation:** entity resolution fuzziness thresholds (start conservative: exact + alias + case-insensitive); antonym/negation heuristic scope for contradiction detection (start: same subject+predicate with differing objects only); whether `UserPromptSubmit` recall earns default-on after real-world coverage data.

---

## 13. Milestones

| | Deliverable | Exit criterion |
|---|---|---|
| **M1 — Core store** | Schema, temporal engine, CLI (init/search/browse/show/history/remember/revise/forget/status), MCP (recall/browse/remember/revise/expand/forget/status), lexical-only retrieval | Round-trip: remember → recall → revise → history shows reasoned chain |
| **M2 — Claude Code** | Hook suite + installer, session lifecycle, primer, digest, share/join | Fresh machine: `engram init && engram install claude-code` → agent uses memory unprompted in a real session |
| **M3 — Code graph** | Universal + document analyzers with extractive impressions, incremental pipeline, `engram_code`, stale-subject flagging; Roslyn deep tier for C# | Any-language repo (code + docs) indexes on day one; edit a C# file → next recall reflects it; deleted symbol closes its facts with reason |
| **M4 — Embeddings** | IEmbedder providers, vec lane, RRF fusion, batch/backfill, `embed --rebuild` | Recall quality ↑ on paraphrase queries; zero resource cost while idle |
| **M5 — Polish** | Salience tuning, compact, export/import, report HTML, doctor completeness | Coverage-rate trend visible in report; doctor green on fresh + aged installs |

---

*End of specification — Rev D (document impressions revision). Companion artifact: `engram-design.html` (visual design sheet).*
