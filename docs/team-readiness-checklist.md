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

- [x] **Validate the M4/D18 recall-quality gate.** Measured 2026-08-09. D18's actual bar: "no
  recorded query has yet missed a fact that existed at the time it was asked." Before this, the
  only evidence was 2 `coverage: none` events in the whole telemetry log (48 recalls total,
  0 `coverage: low`), and D44 had already shown both were cold start — the query fired before
  the answering fact was written, not a retrieval miss. That left the vocabulary-mismatch promise
  itself (D18's whole reason for a vector lane — matching a query to a fact stated in different
  words) never actually exercised.

  Ran 5 fresh paraphrase probes against the real store, each deliberately reusing none of the
  target fact's wording (the same shape as D18's own "kid's name" vs. "son is Liam" example),
  picked from real `/user/about-you` facts several days old — not synthetic, not cold-start.
  4 of 5 surfaced the exact target fact as a top-ranked hit at the default 300-token budget.
  The 5th ("stray extra process in the wrangl agent list" against a fact about a `tmuxCc`/`tmux-cc`
  config typo) returned nothing at default budget but surfaced the fact at rank 7 of 10 once budget
  was raised to 1500 — a ranking/budget interaction, not a `coverage: none` miss, so it doesn't
  bear on D18's stated criterion. 0 of 5 genuine misses; 0 `coverage: none`.

  Gate passes on every query recorded to date, including this session's. Caveat for anyone
  reading this later: 5 hand-picked probes over a corpus of a few thousand personal facts is real
  evidence, not an exhaustive benchmark — D18's bar is cumulative (any future genuine miss reopens
  it), not a one-time close. The rank/budget nuance on probe 5 is worth a follow-up if `coverage`
  keeps hiding relevant-but-low-ranked facts at default budget, but it's a quality note, not a
  blocker.
- [ ] **Establish PR-based review for `main`.** Reviewed 2026-08-09. Branch protection on `main`
  already requires 1 approving review, blocks force-push/deletion, and requires conversation
  resolution — but `enforce_admins` is `false`, which is the entire gap: as sole admin, Jim can
  and did bypass it with direct pushes (the whole commit history to date, zero PRs ever opened).
  Deliberately left as-is rather than flipping `enforce_admins` now: GitHub does not let a PR
  author approve their own PR, so `enforce_admins=true` with the review count still at 1 would
  lock Jim out of merging to `main` at all while solo. Revisit when a teammate actually joins —
  either flip `enforce_admins` with the approval count still at 1 (now satisfiable by the
  teammate), or as a lighter interim step, flip `enforce_admins` with the approval count dropped
  to 0 so every change gets a PR diff and paper trail without an unsatisfiable approval gate.

## Lower priority

- [ ] **Validate `install.ps1` on a real Windows machine.** Unlike `install.sh`, which acts by
  default (D49), `install.ps1` still requires manual `-Apply` because nobody has run it on
  Windows once — shipping an acts-by-default script nobody has executed is the change that
  should not go in blind. Drop the flag requirement once someone has. Deferred 2026-08-09;
  README now states supported platforms up front (macOS, Linux, WSL) and flags Windows
  outside WSL as unvalidated, so this isn't silently implied anymore.
- [ ] **Todo 3 — retire the `digest` MCP tool now that D62 replaces it.** It costs roughly 509 of
  the tool surface's ~2,575 characters every session and has fired 0 times since telemetry
  started. Fold it into a slash command per `docs/session-capture-design.md`.
