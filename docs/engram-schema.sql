-- ENGRAM — canonical schema, version 1
--
-- This file is the authority for M1's database shape. It implements the spec's
-- section 4.1 with the amendments recorded as D2, D3, and D9 in
-- docs/engram-implementation-plan.md. Where it departs from the spec, the reason
-- is stated inline.
--
-- Invariants the schema itself enforces, rather than trusting code to maintain:
--   * a fact is closed at most once            (supersession PK on old_fact_id)
--   * at most one live fact per subject+predicate  (unique partial index)
--   * the lexical index holds live facts only  (triggers, not application code)
--   * no fact can reference a subject that does not exist  (foreign keys ON)
--   * provenance is one of exactly three tiers, and regenerability is a
--     separate boolean, so the two can never be conflated  (CHECK constraints)

-- Persistent. journal_mode is written into the database header and survives, so
-- setting it here is enough.
PRAGMA journal_mode = WAL;

-- CONNECTION-SCOPED, NOT PERSISTENT. The three pragmas below apply only to the
-- connection that runs them — including the one applying this file. They are
-- listed here for completeness, but setting them here does NOT configure the
-- database. Every connection Engram opens must set them itself, in one shared
-- open routine:
--
--     PRAGMA synchronous  = NORMAL;
--     PRAGMA foreign_keys = ON;
--     PRAGMA busy_timeout = 5000;
--
-- foreign_keys is the one with teeth: SQLite the engine defaults it OFF, so a
-- connection that forgets it loses every foreign-key guarantee in this file
-- silently, with no error at any point. Verified against the sqlite3 CLI: an
-- UPDATE writing superseded_by = 2 while fact 2 did not exist was accepted.
--
-- Measured caveat, so nobody mistakes what is holding the line: the provider we
-- actually use, Microsoft.Data.Sqlite, already sends `PRAGMA foreign_keys = 1`
-- when it opens. A raw connection through it reads back fk=1, busy=0, sync=2,
-- journal=delete. So of the three pragmas, only busy_timeout and synchronous are
-- doing work our code has to do; setting foreign_keys ourselves is insurance
-- against a connection string that says `Foreign Keys=False`, or a swap to a
-- provider without the courtesy. Keep it — the cost is one statement and the
-- failure it prevents is silent — but do not expect deleting it to break a test.
--
-- Three integration tests guard this (D9): one asserting `PRAGMA foreign_keys`
-- reads back 1 on a freshly opened connection, one asserting a dangling
-- reference is rejected through the real open path, and one on busy_timeout,
-- which is the one that genuinely fails when the open routine stops setting it.
--
-- busy_timeout pairs with BEGIN IMMEDIATE on every write (D4). A deferred
-- transaction that later upgrades to a writer raises SQLITE_BUSY_SNAPSHOT, which
-- busy_timeout cannot wait out — the WAL footgun the discipline exists to avoid.
PRAGMA synchronous  = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;


-- ---------------------------------------------------------------------------
-- Entities: the nouns memory is about.
-- ---------------------------------------------------------------------------
-- D2: `id` is identity. `path` is addressing metadata — unique and indexed
-- because subtree recall is a prefix range scan, but mutable, because renaming a
-- directory must not orphan a subtree. Renames go through one operation
-- (MoveSubtree) that rewrites entity.path and fact.path in a single transaction
-- and files the old path in entity_alias.

CREATE TABLE entity (
  id         INTEGER PRIMARY KEY,
  path       TEXT    NOT NULL UNIQUE,  -- rooted, broad → specific:
                                       --   /people/jim/preferences
                                       --   /projects/acme/code/acme-api/src/Auth.cs#ValidateToken
  kind       TEXT    NOT NULL,         -- machine|repo|project|module|file|symbol|
                                       -- section|concept|decision|convention|preference|person|
                                       -- tool|topic|statement|session|agent|note
  name       TEXT    NOT NULL,         -- last path segment, denormalized for display
  created_at INTEGER NOT NULL,
  meta       TEXT                      -- JSON: language, signature, disk locations
);

-- A subtree is a range scan, and the UNIQUE constraint above already provides the
-- index that serves it. The upper bound is the prefix with its final character
-- incremented — for a prefix ending in '/' (0x2F) that is the same string ending
-- in '0' (0x30):
--
--     WHERE path = '/knowledge/hooks'
--        OR (path >= '/knowledge/hooks/' AND path < '/knowledge/hooks0')
--
-- NOT a `|| X'FFFD'` sentinel, which an earlier draft of this comment specified
-- and which is wrong: U+FFFD is not the largest encodable character, so any path
-- containing an astral character (U+10000 and up, which encode higher in UTF-8)
-- sorts past that bound and disappears from its own subtree. Nor `LIKE 'prefix%'`,
-- which is case-insensitive for ASCII by default and so cannot use the index range
-- optimization at all. An integration test writes an emoji-bearing path and fails
-- under the sentinel version.
CREATE INDEX ix_entity_kind ON entity(kind);


-- Aliases: prior paths after a rename, and alternate names for entity resolution.
--
-- Departs from the spec, which parks aliases in entity.meta JSON. Resolution
-- looks aliases up on every reindex, and a JSON scan cannot be indexed — this is
-- a query, so it gets a table.
CREATE TABLE entity_alias (
  entity_id  INTEGER NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
  alias      TEXT    NOT NULL,
  kind       TEXT    NOT NULL,   -- 'path' (superseded by a rename) | 'name'
  created_at INTEGER NOT NULL,
  PRIMARY KEY (alias, kind, entity_id)
);
CREATE INDEX ix_entity_alias_entity ON entity_alias(entity_id);


-- ---------------------------------------------------------------------------
-- Facts: append-only temporal statements.
-- ---------------------------------------------------------------------------
-- Belief content — predicate, body, object, validity — is immutable once written.
-- Only valid_to and superseded_by are ever updated, and only to close a fact.
-- `path` is the one exception and is not belief content: it is denormalized
-- addressing metadata that follows its subject entity (D2).

CREATE TABLE fact (
  id            INTEGER PRIMARY KEY,
  subject_id    INTEGER NOT NULL REFERENCES entity(id),
  predicate     TEXT    NOT NULL,   -- normalized verb phrase: uses, decided, prefers
  body          TEXT    NOT NULL,   -- distilled statement, target <= 60 tokens
  object_id     INTEGER REFERENCES entity(id),
  path          TEXT    NOT NULL,   -- denormalized subject path, prefix-searchable
  scope         TEXT    NOT NULL,   -- user | project | code | session

  -- Provenance (D19). How well grounded the belief is, ordinal and ranked
  -- stated > observed > inferred. Read by a model, which must be able to tell
  -- the user's own words from an agent's conclusion.
  learned_via   TEXT    NOT NULL CHECK (learned_via IN ('stated','observed','inferred')),

  -- Regenerability (D23). A SEPARATE axis from provenance, and the reason this
  -- is its own column rather than a fourth learned_via value: a code fact from
  -- an AST and an agent fact from command output are both 'observed', but only
  -- the first can be recomputed. `repair` and `compact` key off THIS column and
  -- must never consult learned_via — reading "rebuild the derived facts" as
  -- "drop the observed ones and re-index" would destroy agent observations that
  -- no longer have a source to recompute from.
  --
  -- Deleting the source file does not clear the flag. The fact is flagged stale,
  -- never rewritten, because belief content is immutable.
  regenerable   INTEGER NOT NULL DEFAULT 0 CHECK (regenerable IN (0,1)),

  evidence      TEXT,               -- "src/Auth.cs:120", "commit a1b2c3"
  details       TEXT,               -- depth beyond the statement; never indexed by any lane (v1); see the two-field design.
  session_id    INTEGER REFERENCES session(id),
  valid_from    INTEGER NOT NULL,
  valid_to      INTEGER,            -- NULL = currently believed
  superseded_by INTEGER REFERENCES fact(id),
  created_at    INTEGER NOT NULL,

  -- How deeply this fact was extracted (code-navigation Phase 4 spec §3): 0 = tier-0 regex,
  -- 1 = tree-sitter, 2 = Roslyn, NULL = not a code fact or written before this column existed.
  -- Stamped at write time, never derived or backfilled — a producer states its own tier or the
  -- column stays NULL; nothing may infer it from LanguageRegistry after the fact (§7.1).
  analyzer_tier INTEGER
);

-- The collision check on the write path (spec 4.3 step 2) is a lookup against
-- this index, and UNIQUE makes "at most one live fact per subject+predicate" a
-- constraint the database enforces rather than a rule the code remembers.
-- Two disjoint partial indexes, not one combined index: SQL treats NULLs as
-- distinct, so adding object_id to a single index would constrain nothing for
-- ordinary facts. Together: one live belief per subject+predicate for
-- objectless facts, one live edge per subject+predicate+object for edges.
CREATE UNIQUE INDEX ux_fact_live ON fact(subject_id, predicate)
  WHERE valid_to IS NULL AND object_id IS NULL;

CREATE UNIQUE INDEX ux_fact_edge_live ON fact(subject_id, predicate, object_id)
  WHERE valid_to IS NULL AND object_id IS NOT NULL;

-- Same columns as ux_fact_live, deliberately, and NOT redundant with it: that one is
-- partial on `valid_to IS NULL`, and the query this serves counts a thread's whole
-- history, closed rows included (D57). A partial index cannot answer a query that
-- reaches outside its predicate, so SQLite fell back to a full scan of fact once per
-- returned row. Measured on the 50,097-fact store: that correlated subquery was 93-99%
-- of the entire ranking statement at every corpus size and every match count, and this
-- index removes it — 1,545 ms to 105 ms for a term matching 45,132 facts, 31.8 ms to
-- 1.1 ms at 5,308. It changes which rows are FOUND, never which are counted, which is
-- what makes it preferable to substituting the denormalized fact.path column: that
-- would trade a rename staleness window (D8) for the same speedup.
CREATE INDEX ix_fact_thread  ON fact(subject_id, predicate);

CREATE INDEX ix_fact_path    ON fact(path);
CREATE INDEX ix_fact_session ON fact(session_id);
CREATE INDEX ix_fact_scope   ON fact(scope) WHERE valid_to IS NULL;

-- `repair` and `compact` sweep exactly this set (D8, D23). Partial, because the
-- regenerable rows are the minority and the query is always "which may I discard".
CREATE INDEX ix_fact_regenerable ON fact(regenerable) WHERE regenerable = 1;


-- ---------------------------------------------------------------------------
-- Supersession: why a belief changed.
-- ---------------------------------------------------------------------------
-- Two departures from the spec, both to make invalid states unrepresentable:
--
--   1. PRIMARY KEY is old_fact_id alone, not the pair. A fact is closed exactly
--      once, so the pair permitted a state that cannot occur.
--   2. new_fact_id is NULLABLE, where the spec used a 0 sentinel for `forget`.
--      A 0 sentinel cannot satisfy the foreign key it was declared with. NULL
--      means "closed and not replaced" — forgotten.

CREATE TABLE supersession (
  old_fact_id INTEGER PRIMARY KEY REFERENCES fact(id),
  new_fact_id INTEGER REFERENCES fact(id),   -- NULL = forgotten, not replaced
  reason      TEXT    NOT NULL,              -- required; the MCP schema enforces it
  evidence    TEXT,
  session_id  INTEGER REFERENCES session(id),
  created_at  INTEGER NOT NULL
);
CREATE INDEX ix_supersession_new ON supersession(new_fact_id);


-- ---------------------------------------------------------------------------
-- Edges: associative structure. Hierarchy organizes; the graph associates.
-- Superseded by D70: object-bearing facts (fact.object_id, ux_fact_edge_live)
-- carry code-navigation edges now. Left in place, unused; BackupStore.cs:53
-- still counts it.
-- ---------------------------------------------------------------------------
CREATE TABLE edge (
  from_id    INTEGER NOT NULL REFERENCES entity(id),
  to_id      INTEGER NOT NULL REFERENCES entity(id),
  relation   TEXT    NOT NULL,  -- defines|declares|calls|implements|imports|
                                -- references|part_of|relates_to|learned_with|
                                -- contradicts|derived_from
  weight     REAL    NOT NULL DEFAULT 1.0,
  source     TEXT    NOT NULL,  -- indexer | agent
  created_at INTEGER NOT NULL,
  PRIMARY KEY (from_id, to_id, relation)
);
CREATE INDEX ix_edge_to ON edge(to_id);


-- ---------------------------------------------------------------------------
-- Salience: retrieval strength, never truth. Ranks; does not expire.
-- ---------------------------------------------------------------------------
CREATE TABLE salience (
  fact_id       INTEGER PRIMARY KEY REFERENCES fact(id) ON DELETE CASCADE,
  access_count  INTEGER NOT NULL DEFAULT 0,
  last_accessed INTEGER,
  confirmations INTEGER NOT NULL DEFAULT 0,
  score         REAL    NOT NULL DEFAULT 0.5   -- recomputed lazily on read
);


-- ---------------------------------------------------------------------------
-- Sessions: the provenance anchor. Everything learned traces to one.
-- ---------------------------------------------------------------------------
CREATE TABLE session (
  id         INTEGER PRIMARY KEY,
  external_id TEXT UNIQUE,          -- the host's session identifier, if any
  host       TEXT    NOT NULL,      -- claude-code | cli | other
  repo_path  TEXT,                  -- memory path, e.g. /projects/acme/code/acme-api
  started_at INTEGER NOT NULL,
  ended_at   INTEGER,
  digest     TEXT
);


-- ---------------------------------------------------------------------------
-- Lexical lane (D3): external-content FTS5 over LIVE facts only.
-- ---------------------------------------------------------------------------
-- External content, not contentless: the 'delete' command needs the previously
-- indexed column values, and external content has them by construction. Facts
-- are never hard-deleted outside `compact`, so the content table cannot dangle.
--
-- Live-only is deliberate. Superseded facts occupying seed_k slots before the
-- live filter would pollute ranking. History search is rare and served by a scan
-- over closed facts.
--
-- subject_name is absent by necessity — external content can only index columns
-- that exist on the content table. `path` carries the subject anyway: it is the
-- denormalized subject path (D2), it lives on this table, and unicode61 splits
-- it on / and - into the same words the name is made of.
--
-- Indexing it is not cosmetic. Measured on the seeded corpus, 30 of 45 live
-- facts (67%) have at least one subject word that appears nowhere in their body,
-- across 39 such words. Without `path` here those words are reachable only by
-- the literal-token lane, so any morphological variant of them — most often a
-- plural — matches nothing at all.
--
-- Columns are weighted equally by bm25, which is the honest default rather than
-- a measured one. `path` is short, so a hit in it is not cheap to earn; the root
-- and topic segments repeat across every fact and are therefore nearly free of
-- IDF, which is what keeps "/knowledge" from matching the whole store.

CREATE VIRTUAL TABLE fact_fts USING fts5(
  body,
  predicate,
  path,
  content='fact',
  content_rowid='id',
  tokenize='porter unicode61'
);

-- Code-navigation edges (CodePredicates.EdgeBearing) never enter the lexical
-- lanes: tens of thousands of near-identical edge bodies would inflate D44's
-- coverage as corroboration-shaped noise, and nobody recalls an edge body in
-- words. The predicate list must stay identical across all four triggers and
-- EngramDatabase.RebuildFactFts's interpolated copy.
CREATE TRIGGER fact_fts_insert AFTER INSERT ON fact
  WHEN new.predicate NOT IN ('calls', 'imports') BEGIN
  INSERT INTO fact_fts(rowid, body, predicate, path)
    VALUES (new.id, new.body, new.predicate, new.path);
END;

CREATE TRIGGER fact_fts_close AFTER UPDATE OF valid_to ON fact
  WHEN old.valid_to IS NULL AND new.valid_to IS NOT NULL
    AND old.predicate NOT IN ('calls', 'imports') BEGIN
  INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path)
    VALUES ('delete', old.id, old.body, old.predicate, old.path);
END;

-- Live rows only: fact_fts_close already un-indexed a closed fact, and FTS5
-- refuses a second 'delete' for an entry the index no longer holds. Measured:
-- without the WHEN clause, DELETE of a closed fact fails at the statement with
-- "database disk image is malformed (11)" — which made every closed fact
-- undeletable and would have broken `compact` on its first prune.
CREATE TRIGGER fact_fts_delete AFTER DELETE ON fact
  WHEN old.valid_to IS NULL AND old.predicate NOT IN ('calls', 'imports') BEGIN
  INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path)
    VALUES ('delete', old.id, old.body, old.predicate, old.path);
END;

-- `path` is the one piece of belief content that is allowed to change: it
-- follows its entity on rename (D2). Every other indexed column is immutable, so
-- this is the only trigger of its kind, and without it a rename would leave the
-- fact indexed under its old address with nothing to say so.
CREATE TRIGGER fact_fts_repath AFTER UPDATE OF path ON fact
  WHEN new.valid_to IS NULL AND old.path <> new.path
    AND new.predicate NOT IN ('calls', 'imports') BEGIN
  INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path)
    VALUES ('delete', old.id, old.body, old.predicate, old.path);
  INSERT INTO fact_fts(rowid, body, predicate, path)
    VALUES (new.id, new.body, new.predicate, new.path);
END;


-- ---------------------------------------------------------------------------
-- Code index bookkeeping. These rows are derived and rebuildable, which is
-- exactly the set `compact` may prune and `repair` may regenerate (D8).
-- ---------------------------------------------------------------------------
CREATE TABLE file_state (
  repo_path  TEXT    NOT NULL,   -- memory path of the repo, e.g. /projects/acme/code/acme-api
  path       TEXT    NOT NULL,   -- repo-relative file path
  blob_sha   TEXT    NOT NULL,   -- git blob hash, or content hash if untracked
  lang       TEXT,
  indexed_at INTEGER NOT NULL,
  PRIMARY KEY (repo_path, path)
);

CREATE TABLE repo_registry (
  repo_path                   TEXT PRIMARY KEY,  -- memory path, e.g. /projects/acme/code/acme-api
  identity                    TEXT NOT NULL,     -- normalized git remote URL, else normalized root path
  disk_path                   TEXT,              -- last seen location; NULL once detached
  detached_at                 INTEGER,           -- non-NULL when the checkout is gone from disk
  created_at                  INTEGER NOT NULL,
  last_scan_suppressed_reason TEXT CHECK (last_scan_suppressed_reason IN ('truncated','empty-scan'))
                                                  -- NULL = last run's deletions were not suppressed;
                                                  -- set by CodeIndexer wherever it skips stampFullScan,
                                                  -- cleared wherever a full scan applies deletions (§14).
                                                  -- Lives here, not on repo_enrollment: an indexed but
                                                  -- unenrolled repo still has a repo_registry row.
);
CREATE UNIQUE INDEX ux_repo_identity ON repo_registry(identity);


-- Authored truth: the user's answer to "should Engram index this repo". Deliberately NOT in
-- repo_registry, which StoreCompactor deletes rows from under a path prefix — a decision stored
-- there would be un-declined by `engram compact` and the user re-prompted (D8).
CREATE TABLE repo_enrollment (
  identity          TEXT PRIMARY KEY,  -- CodeIndexer.ResolveIdentity(root); same key as repo_registry.identity
  state             TEXT NOT NULL CHECK (state IN ('enrolled','declined','deferred')),
  source            TEXT NOT NULL CHECK (source IN ('user','backfill')),
  last_root         TEXT,              -- last seen checkout root: a lookup cache, never the key
  decided_at        INTEGER NOT NULL,  -- unix seconds
  last_full_scan_at INTEGER            -- unix seconds; NULL = never scanned = due
);
CREATE INDEX ix_repo_enrollment_root ON repo_enrollment(last_root);


-- ---------------------------------------------------------------------------
-- Literal-token overlap lane: fact_token(token, fact_id) over LIVE facts only.
-- ---------------------------------------------------------------------------
-- Maintained from C# (FactTokenIndex), not by trigger: the tokenizer it must share with the
-- ranker (RecallEngine) is a C# implementation, and a trigger cannot call it. Expressing
-- tokenization a second time in SQL would be a second implementation that drifts from the first.
--
-- WITHOUT ROWID with a composite primary key: (token, fact_id) is a non-integer composite key
-- and rows are far under 1/20th of a page, which sqlite.org/withoutrowid.html names as the case
-- this saves real storage — one B-tree instead of a table plus a separate index. The leftmost
-- prefix serves `WHERE token = ?`, the only read shape the ranker uses.

CREATE TABLE fact_token (
  token   TEXT    NOT NULL,
  fact_id INTEGER NOT NULL REFERENCES fact(id) ON DELETE CASCADE,
  PRIMARY KEY (token, fact_id)
) WITHOUT ROWID;

-- Deletion and re-indexing address a fact by id, and the primary key cannot serve
-- `WHERE fact_id = ?` with token leading.
CREATE INDEX ix_fact_token_fact ON fact_token(fact_id);


-- ---------------------------------------------------------------------------
-- Cross-machine sync (docs/memory-expansion/01-sync-spec.md) — side tables only,
-- nothing added to `fact`. Both are derived in the weak sense (D8): rebuildable
-- by re-running `sync import` over the full chunk history, never authored truth.
-- ---------------------------------------------------------------------------

CREATE TABLE sync_chunk_state (
  machine_id TEXT NOT NULL,
  seq        INTEGER NOT NULL,
  applied_at INTEGER NOT NULL,
  fact_count INTEGER NOT NULL,
  close_count INTEGER NOT NULL,
  PRIMARY KEY (machine_id, seq)
);

CREATE TABLE sync_deferred_close (
  subject_path TEXT NOT NULL,
  predicate    TEXT NOT NULL,
  body         TEXT NOT NULL,
  valid_from   INTEGER NOT NULL,
  valid_to     INTEGER NOT NULL,
  superseded_by_body TEXT,
  superseded_by_valid_from INTEGER,
  status TEXT NOT NULL DEFAULT 'deferred' CHECK (status IN ('deferred','stalled')),
  retry_count INTEGER NOT NULL DEFAULT 0,
  first_seen_at INTEGER NOT NULL,
  source_chunk TEXT NOT NULL,
  PRIMARY KEY (subject_path, predicate, body, valid_from)
);

-- ---------------------------------------------------------------------------
-- Conflict verdicts (docs/memory-expansion/02-conflict-verdicts-spec.md) —
-- a verdict is an annotation kept apart from `fact`, never a fact mutation
-- (D8). Rows are immutable: a re-judgment is a new row, not an update.
-- ---------------------------------------------------------------------------

CREATE TABLE fact_relation (
  id INTEGER PRIMARY KEY,
  fact_id    INTEGER NOT NULL REFERENCES fact(id),
  related_id INTEGER NOT NULL REFERENCES fact(id),
  relation   TEXT NOT NULL CHECK (relation IN
             ('supersedes','conflicts_with','scoped','not_conflict')),
  reason     TEXT,
  judged_at  INTEGER NOT NULL
);
CREATE INDEX ix_fact_relation_fact    ON fact_relation(fact_id);
CREATE INDEX ix_fact_relation_related ON fact_relation(related_id);

-- ---------------------------------------------------------------------------
-- Scoped export (docs/memory-expansion/01-sync-spec.md) — an explicit
-- always-sync opt-in. Not derived from `fact` or the chunk history, so it is
-- not covered by D8's "derived state is repairable"; losing a row is a real
-- loss, not a cache eviction.
-- ---------------------------------------------------------------------------

CREATE TABLE fact_sync_request (
  fact_id      INTEGER NOT NULL PRIMARY KEY REFERENCES fact(id),
  requested_at INTEGER NOT NULL
);

-- ---------------------------------------------------------------------------
-- Review-due marker (docs/memory-expansion/04-lifecycle-spec.md) — an
-- explicit, caller-supplied reminder date. Side-table, not derived (D8's
-- "derived from fact" sense does not apply: nothing in a fact's body encodes
-- a chosen reminder date).
-- ---------------------------------------------------------------------------

CREATE TABLE fact_review (
  fact_id      INTEGER PRIMARY KEY REFERENCES fact(id),
  review_after INTEGER NOT NULL,
  set_at       INTEGER NOT NULL
);


CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT);
INSERT INTO schema_meta(key, value) VALUES ('schema_version', '14');

-- Built by a fresh CREATE, and pre-stamped ready: an empty table matches whatever
-- FactTokenIndex.Rebuild would produce over zero facts, so a new store needs no rebuild pass.
-- The version must track FactTokenIndex.CurrentVersion by hand — this file cannot reference a
-- C# constant, the same duplication schema_version above already accepts.
INSERT INTO schema_meta(key, value) VALUES ('fact_token_version', '1');


-- ---------------------------------------------------------------------------
-- fact_vec — the sqlite-vec index over fact bodies — is NOT here, and cannot
-- be. Its DDL embeds the vector width, which is a property of whichever
-- embedder is configured, and applying it at all needs sqlite-vec loaded on
-- the connection; a static statement expresses neither. `VectorIndex` owns it
-- and pins the space it holds in `schema_meta` (embedding_model,
-- embedding_dimensions, embedding_input). Derived state throughout: it
-- rebuilds from `fact` plus an embedder, which is what lets `compact` and
-- `repair` touch it (D8) and makes dropping it a recovery, not data loss.
--
--   CREATE VIRTUAL TABLE fact_vec USING vec0(
--     fact_id   INTEGER PRIMARY KEY,
--     is_live   INTEGER,   -- mirrors fact.valid_to IS NULL; filtered INSIDE
--                          -- the MATCH, because vec0 applies k before any
--                          -- join and a post-filter silently returns short
--     embedding float[N] distance_metric=cosine
--   );
-- ---------------------------------------------------------------------------
