namespace Engram.Core;

public sealed record CannedFact(
    string Id,
    string Subject,
    string Predicate,
    string Body,
    string Scope,
    int AgeDays,
    string? Evidence = null);

public static class CannedFacts
{
    public static readonly IReadOnlyList<CannedFact> All =
    [
        new("f001", "execution-model", "decided", "Sonnet subagents implement each spec; Jim reviews every diff himself before merging.", "user", 0, "implementation-plan §5"),
        new("f002", "test-verification", "requires", "Temporal correctness, D4 concurrency, D7 isolation, and repair's authored-fact guarantee need real verification, not agent reports.", "user", 0, "implementation-plan §5"),
        new("f003", "comment-style", "prefers", "No comments narrating diffs; comments only for public contracts or non-obvious why.", "user", 0),
        new("f004", "ambiguity-policy", "requires", "If a spec is ambiguous or a feature isn't listed, stop and report — never guess or invent.", "user", 0),
        new("f005", "git-discipline", "prefers", "Agents never run git commit or push; changes stay uncommitted for review.", "user", 0),
        new("f006", "build-quality", "requires", ".NET 10 project; warnings are treated as errors across every build.", "user", 0, "Directory.Build.props"),
        new("f007", "aot-safety", "requires", "Engram.Cli publishes Native AOT; every dependency and API used must stay trim-safe.", "user", 0, "implementation-plan D1"),
        new("f008", "delegation-style", "prefers", "Cheap retrieval and tool-heavy legwork goes to a Haiku runner; judgment stays with Sonnet or Opus.", "user", 0),
        new("f009", "D1-packaging", "decided", "engram core is one AOT binary; Roslyn ships as a separate sidecar spawned only for indexing.", "project", 0, "implementation-plan D1"),
        new("f010", "D1-packaging", "decided", "The core owns every database write; the Roslyn sidecar never opens engram.db.", "project", 0, "implementation-plan D1"),
        new("f011", "D2-identity", "decided", "entity.id is identity; path is mutable addressing, renamed via one MoveSubtree operation.", "project", 0, "implementation-plan D2"),
        new("f012", "D3-fts5", "decided", "fact_fts is external-content FTS5, trigger-maintained, live facts only; history search uses LIKE.", "project", 0, "implementation-plan D3"),
        new("f013", "D4-concurrency", "decided", "Every write transaction is BEGIN IMMEDIATE; a deferred transaction upgrading mid-write is the classic WAL footgun.", "project", 0, "implementation-plan D4"),
        new("f014", "D4-concurrency", "decided", "The file-touched hook never opens SQLite; it only appends a line to a queue spool file.", "project", 0, "implementation-plan D4"),
        new("f015", "D5-contradiction", "decided", "No automated contradiction detection in v1; the agent reading both facts side by side is the detector.", "project", 0, "implementation-plan D5"),
        new("f016", "D6-sequencing", "decided", "M0 inserts an adoption probe before the code graph, since past memory tools failed when the LLM never called the tool.", "project", 0, "implementation-plan D6"),
        new("f017", "D7-isolation", "decided", "Every path derives from one resolved home root: --home flag, then ENGRAM_HOME, then the user's home.", "project", 0, "implementation-plan D7"), // engram-lint:allow(canned fact text names the real env var for recall relevance, not a computed path)
        new("f018", "D7-isolation", "decided", "A lint test scans sources for hardcoded home-path literals outside the one resolver and installer default.", "project", 0, "implementation-plan D7"),
        new("f019", "D8-repair", "decided", "engram repair only rebuilds derived state — FTS, salience, fact.path — never authored fact content.", "project", 0, "implementation-plan D8"),
        new("f020", "D9-testing", "decided", "Five test tiers; integration is the primary tier because temporal and concurrency bugs need real SQLite files.", "project", 0, "implementation-plan D9"),
        new("f021", "D9-testing", "decided", "Tier 3 end-to-end tests drive the published AOT binary as a subprocess, not the JIT build.", "project", 0, "implementation-plan D9"),
        new("f022", "M0-milestone", "scoped", "M0.0 ships the home resolver, a stub MCP server, canned facts, and the primer/digest hooks — no database yet.", "project", 0, "implementation-plan §3"),
        new("f023", "M0-milestone", "tests", "M0's exit criterion is two weeks of real use where the agent calls recall before exploring, unprompted.", "project", 0, "implementation-plan §3"),
        new("f024", "riskiest-assumption", "states", "A 300-token primer should make Claude Code call engram_recall before exploring files, session after session.", "project", 0, "implementation-plan §2"),
        new("f025", "schema-fact-table", "defines", "fact rows are append-only: valid_to and superseded_by are the only columns ever updated.", "code", 0, "spec §4.1"),
        new("f026", "mcp-tool-surface", "exposes", "M0.0 ships exactly three MCP tools: engram_recall, engram_remember, engram_digest.", "code", 0, "spec §9"),
        new("f027", "hook-events", "wires", "Claude Code hooks call engram hook session-start, pre-compact, and file-touched.", "code", 0, "spec §10.1"),
        new("f028", "recall-contract", "formats", "Recall output is a header line, one line per fact with an [fXXX] handle, and a coverage estimate.", "code", 0, "spec §6.2"),
        new("f029", "token-estimator", "computes", "Tokens are estimated as ceil(characters / 3.6) until a real tokenizer replaces the placeholder.", "code", 0, "implementation-plan §4"),
        new("f030", "aot-packaging", "measured", "Native AOT publish is zero-warning; the MCP SDK round-trips initialize, tools/list, and tools/call from the published binary.", "code", 0, "implementation-plan §1.5 Spike B"),
    ];
}
