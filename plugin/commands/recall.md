---
description: Query Engram's memory directly and show what it holds. Shows stored facts — it does not answer the question from source.
argument-hint: "<what you want to know>"
allowed-tools: ["mcp__plugin_engram_engram__engram_recall"]
---

Call `engram_recall` with this query:

> $ARGUMENTS

Then show the result **verbatim**, fact handles (`[f012]`) included. The handles are how
a fact gets referred to later; stripping them costs the user something.

The point of this command is to see what memory actually contains, so:

- Do **not** read files, grep the repo, or reason from your own knowledge to fill gaps.
  If memory holds nothing on the subject, the honest and useful answer is "nothing
  stored on this" plus the coverage figure recall reported.
- Do **not** rewrite, merge, or re-rank the facts. Recall already ranked them.
- After the results you may add one line — only if there is something to say that the
  output does not already show, such as a low coverage estimate meaning the answer is
  probably not in memory yet.

If `$ARGUMENTS` is empty, ask what to look for rather than guessing from the conversation.

If the `engram_recall` tool is not available at all, the memory server is not running:
say so and point at `/engram:start`.
