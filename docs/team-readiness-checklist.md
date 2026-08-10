# Team readiness checklist

What's between the current state and handing Engram to teammates who install and depend on it,
as distinct from "is the feature that's currently being built MVP." Raised 2026-08-10 while
confirming D62 (session-capture) was done. Update items in place as they close; don't fork a new
file for the next round.

## Blocking

- [x] **Fix the `UserPromptSubmit` mis-capture bug.** Fixed 2026-08-10. Ground-truthed against
  this session's own transcript rather than assumed: every one of 16 real mis-captures that
  night carried `promptSource: "system"` on the transcript record Claude Code wrote for that
  submission (`origin.kind` was `"peer"` for cross-session messages, `"task-notification"` for
  background task notifications); both of the 2 genuine typed prompts carried `"typed"`. That
  field isn't on the hook's own stdin payload — only `transcript_path` is, confirmed against
  Claude Code's official hook docs — so the fix is a bounded, tail-only read of the transcript's
  last line in `HookCommand.IsGenuinelyTyped`, checked after classification (the common case is
  nothing to capture, and that path shouldn't pay for a file read). Any failure (missing path,
  unreadable file, unparseable line) fails closed — skip capture, never guess. 10 new/updated
  tests in `HookUserPromptTests.cs`, all passing against the published binary. Rebuilt and
  reinstalled; takes effect on the next `UserPromptSubmit` in any already-running session, since
  this changes behavior inside an existing hook rather than registering a new one.

## Should resolve before wider rollout

- [ ] **Validate the M4/D18 recall-quality gate.** `docs/session-capture-design.md` states it in
  its own words as "unmet" — not broken, just not yet measured to the bar the project set for
  itself.
- [ ] **Establish PR-based review for `main`.** The repo has branch protection requiring PRs;
  three commits landed via a direct bypass on 2026-08-09/10 with no second reviewer. Fine for one
  person iterating fast at night, not once teammates are depending on what's in that history.

## Lower priority

- [ ] **Validate `install.ps1` on a real Windows machine.** Unlike `install.sh`, which acts by
  default (D49), `install.ps1` still requires manual `-Apply` because nobody has run it on
  Windows once — shipping an acts-by-default script nobody has executed is the change that
  should not go in blind. Drop the flag requirement once someone has.
- [ ] **Todo 3 — retire the `digest` MCP tool now that D62 replaces it.** It costs roughly 509 of
  the tool surface's ~2,575 characters every session and has fired 0 times since telemetry
  started. Fold it into a slash command per `docs/session-capture-design.md`.
