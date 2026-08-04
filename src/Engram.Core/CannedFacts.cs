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
        new("f001", "subagentstart-envelope", "requires", "SubagentStart delivers additionalContext only via the hookSpecificOutput envelope; bare stdout is silently discarded even though SessionStart accepts it.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f002", "subagentstart-scope", "warns", "SessionStart, SessionEnd, UserPromptSubmit, and Stop hooks never fire for a subagent; only PreToolUse, PostToolUse, PermissionRequest, PermissionDenied, and SubagentStart do.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f003", "agent-id-discriminator", "states", "agent_id present in a hook payload is the reliable subagent discriminator; agent_type alone is not, since it is also set on a top-level --agent session.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f004", "agent-type-key-name", "warns", "The subagent's type is keyed agent_type on SubagentStart but subagent_type inside PreToolUse's tool_input; reading the wrong key name matches nothing, silently.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f005", "pretooluse-exit-codes", "defines", "PreToolUse exit code 0 allows, 2 denies and feeds stderr back to the model, but 1 is an error and not a block — it is silently treated as allow.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f006", "temporal-dead-zone", "warns", "A const referenced from an early-returning branch but declared later in the file throws ReferenceError at runtime, not at node --check, silently disabling the whole guard since exit 1 is not a block.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f007", "plugin-cache-pinning", "warns", "Editing a plugin's hook files in a marketplace working tree has no effect; the loaded copy is cached per plugin version and only refreshes when the plugin's version number bumps.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f008", "sessionend-timeout", "requires", "SessionEnd enforces a very short timeout, about 1.5s observed; real work must be backgrounded with stdin captured first or it is killed mid-flight.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f009", "precompact-budget", "states", "PreCompact is budgeted generously, 90s in one observed config, so heavier synchronous work is affordable there, unlike SessionEnd.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f010", "precompact-no-injection", "warns", "PreCompact has no additionalContext injection channel; its decision:block refuses the compaction outright rather than annotating it, so it cannot inject context.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f011", "subagentstart-reach", "states", "To get a directive into every subagent, including workflow-spawned ones, use SubagentStart — a PreToolUse rewrite of the Agent tool's input cannot reach them; a measured 47-agent run confirmed zero relay events that way.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f012", "hook-context-aggregation", "warns", "additionalContext from multiple hooks on one event aggregates safely, but conflicting permissionDecision values across hooks on the same event are undocumented — let only one plugin deny a given tool.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f013", "no-agent-roster-at-spawn", "warns", "A subagent cannot see the available-agent roster until the result of its own first tool call; a directive naming an unresolvable agent type fails silently and looks identical to the subagent ignoring it.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f014", "plugin-agent-type-match", "requires", "Plugin agent types are namespaced as plugin:agent, so match them anchored, (^|:)name$, never by bare name, or a same-named agent from another plugin will match too.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f015", "hook-payload-no-tier", "states", "Hook payloads carry no model or capability-tier field; effort is a thinking-level setting, not a model tier, so tier-based routing needs another signal.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f016", "subagentstart-envelope-keys", "requires", "All three SubagentStart envelope keys are load-bearing and hookEventName must exactly match the firing event, or the additionalContext is not delivered.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f017", "additionalcontext-size", "states", "A ~7.5 KB additionalContext payload was delivered untruncated in a live SubagentStart probe; do not assume a truncation threshold exists without testing one.", "user", 0, "docs/claude-code-hooks-reference.md"),

        new("f018", "hook-toggle-mechanism", "prefers", "A hook's on/off toggle is handled by a script that checks enabled state at run time, not by editing settings.json to add or remove the hook entry.", "user", 0, "claudetools, github-agent-plugins"),
        new("f019", "toggle-state-shape", "prefers", "Toggle state is a JSON config with an explicit enabled boolean, not a marker file's mere existence — presence on disk and what status reports can drift apart.", "user", 0, "claudetools, github-agent-plugins"),
        new("f020", "plugin-state-location", "prefers", "Plugin state lives in the user's own Claude config directory, not inside the version-pinned plugin cache, so it survives the plugin's auto-updates.", "user", 0, "claudetools, github-agent-plugins"),
        new("f021", "concurrent-write-log-format", "requires", "Concurrent-write state files are append-only JSONL, never read-modify-write — read-modify-write under concurrency measurably dropped roughly 4 of 12 writes.", "user", 0, "claudetools, github-agent-plugins"),
        new("f022", "log-pruning-pattern", "prefers", "Log pruning writes a pid-suffixed temp file first, then atomically renames it over the original, rather than truncating or rewriting the log in place.", "user", 0, "claudetools, github-agent-plugins"),
        new("f023", "hook-enabled-check-order", "requires", "Hooks check whether they are enabled via a cheap unlocked read before taking any lock or doing real work, so a disabled hook costs almost nothing.", "user", 0, "claudetools, github-agent-plugins"),
        new("f024", "hook-failure-isolation", "requires", "Hook bodies are wrapped in try/catch and exit 0 on failure, so a broken hook never blocks the tool call or session it is attached to.", "user", 0, "claudetools, github-agent-plugins"),
        new("f025", "blocking-gate-fail-open", "requires", "A blocking gate hook fails open on its own internal error, rather than blocking the user's work because the gate itself broke.", "user", 0, "claudetools, github-agent-plugins"),
        new("f026", "toggle-scope-targeting", "prefers", "On/off toggles target the narrowest scope that already has a config file, so a repo-level opt-out never rewrites the user's global setting.", "user", 0, "claudetools, github-agent-plugins"),
        new("f027", "hook-shell-test-harness", "requires", "Every plugin ships a shell test harness that pipes a JSON payload to the hook on stdin and asserts on both the exit code and the emitted output shape.", "user", 0, "claudetools, github-agent-plugins"),
        new("f028", "hooks-json-descriptions", "prefers", "hooks.json descriptions are written as real prose documentation of what the hook does and why, not a one-line label nobody will expand on later.", "user", 0, "claudetools, github-agent-plugins"),
        new("f029", "delegation-enforcement-layer", "requires", "Delegation to a cheaper runner is enforced at the tool-permission layer, not by instructions alone, since instructions are the first thing a model drops under pressure.", "user", 0, "claudetools, github-agent-plugins"),

        new("f030", "preserve-behavior-vs-defect", "warns", "An implementor told to preserve behavior precisely will faithfully preserve a defect along with it — state explicitly which behaviors are the contract, not just keep it working the same.", "user", 0, "implementation-plan §5"),
        new("f031", "scope-to-defect-class", "requires", "Scoping a fix to the one file that showed the bug lets the same defect survive untouched in a sibling file — scope the fix to the defect class, not the symptom's location.", "user", 0, "implementation-plan §5"),
        new("f032", "untested-guard-is-decoration", "warns", "A lint or guard test that has never been observed to fail is decoration, not protection, until someone deliberately breaks the rule and watches it get caught.", "user", 0, "implementation-plan D12"),
        new("f033", "exit-code-only-test", "warns", "A test asserting only an exit code cannot distinguish an active decision from silently falling through — assert the emitted output's shape too.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f034", "instrumentation-independence", "requires", "Instrumentation must not depend on the same behavior it is measuring, or a failure in that behavior becomes invisible in exactly the data meant to reveal it.", "user", 0, "implementation-plan D12"),
        new("f035", "documentation-silence", "warns", "Absence of a statement in documentation is not evidence of absence in behaviour — treat undocumented as unknown, not as ruled out, and verify by probing.", "user", 0, "docs/claude-code-hooks-reference.md"),
        new("f036", "adoption-vs-demand", "states", "Adoption of a tool by a model is roughly inversely proportional to what it demands of the model — passive auto-capture gets used heavily, structured authoring gets skipped.", "user", 0, "implementation-plan D12"),
        new("f037", "invisible-crashed-process", "warns", "Low observed usage of a background or dependent process is not evidence of disinterest until a silent crash of that process has been ruled out first.", "user", 0, "implementation-plan D12"),

        new("f038", "filemode-append-race", "warns", "FileMode.Append in .NET is seek-then-write, not POSIX O_APPEND, so concurrent appends can resolve the same offset and end up losing writes — unlike Node's appendFileSync, which is O_APPEND and atomic.", "code", 0, "implementation-plan D4"),
        new("f039", "jsonarray-add-aot", "requires", "JsonArray.Add binds to the AOT-hostile generic overload by default; cast the array through IList<JsonNode?> first to call the non-generic Add and stay trim-safe.", "code", 0, "implementation-plan D1"),
        new("f040", "pragma-foreign-keys-scope", "warns", "PRAGMA foreign_keys is connection-scoped and defaults to off in SQLite, so foreign keys are not enforced unless every new connection sets it itself — declaring it once in a schema file configures nothing.", "code", 0, "sqlite.org pragma reference"),
        new("f041", "begin-immediate-required", "requires", "Every SQLite write transaction should open BEGIN IMMEDIATE — a deferred transaction upgrading to a writer raises SQLITE_BUSY_SNAPSHOT, which busy_timeout cannot wait out.", "code", 0, "implementation-plan D4"),
        new("f042", "fts5-delete-needs-old-values", "requires", "A SQLite FTS5 external-content table needs the previously indexed column values to evict a row — its delete command requires them, and content='table' has them by construction.", "code", 0, "implementation-plan D3"),
        new("f043", "atomic-rename-same-dir", "requires", "A temp file used for an atomic rename must be created in the same directory as its target — renaming across filesystems or mount points is not atomic.", "code", 0, "Engram.Core/AtomicFile.cs"),
        new("f044", "fts5-external-content-columns", "warns", "SQLite FTS5 external-content tables can only index columns that exist on the content table — a denormalized join column has to be joined at query time instead.", "code", 0, "implementation-plan D3"),
        new("f045", "orm-hostile-to-aot", "warns", "An ORM or EF Core is reflection-heavy and hostile to Native AOT trimming; hand-written SQL keeps the AOT surface small and the query plans visible.", "code", 0, "implementation-plan §4"),

        new("f046", "no-orm-in-this-repo", "states", "No ORM here: hand-written SQL keeps query plans visible.", "project", 0, "CLAUDE.md"),
        new("f047", "home-resolver-authority", "requires", "Only the one home resolver may derive a path from the user's home directory; a lint test fails the build if any other source file does.", "project", 0, "CLAUDE.md; implementation-plan D7"),
        new("f048", "test-fixture-real-home-guard", "requires", "Every test runs against a disposable home; the sandbox fixture refuses to construct against the real one.", "project", 0, "CLAUDE.md; implementation-plan D7"),
        new("f049", "hand-invoke-needs-explicit-home", "warns", "Hand-invoking the published binary without setting ENGRAM_HOME writes to the real home — the guards protect test code, not a shell.", "project", 0, "CLAUDE.md"), // engram-lint:allow(prose reference to the env var name, not a path read)
        new("f050", "integration-tier-is-primary", "states", "Integration is the primary test tier here because the real risks — temporal invariants and multi-process contention — are ones unit tests cannot reach.", "project", 0, "implementation-plan D9"),
        new("f051", "authoring-riskier-than-recall", "states", "Adoption of a memory tool appears inversely proportional to what it demands of the model, which is why structured fact authoring is a bigger adoption risk here than recall.", "project", 0, "implementation-plan D12"),
    ];
}
