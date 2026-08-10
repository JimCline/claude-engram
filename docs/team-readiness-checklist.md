# Team readiness checklist

What's between the current state and handing Engram to teammates who install and depend on it,
as distinct from "is the feature that's currently being built MVP." Raised 2026-08-10 while
confirming D62 (session-capture) was done. Update items in place as they close; don't fork a new
file for the next round.

## Blocking

- [ ] **Fix the `UserPromptSubmit` mis-capture bug.** The hook sometimes stores text from a
  subagent's report or a cross-session peer message as if the user had typed it. The fix is
  already documented but not applied: a genuine user turn carries `promptSource`/`origin`/
  `permissionMode` and no `toolUseResult`; injected or tool-borne text lacks that signature. See
  `docs/session-capture-design.md`, "The transcript." Currently active — about 7 stray captures
  were manually retracted in one session on 2026-08-09/10 alone, with nothing but a human
  noticing to catch them.

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
