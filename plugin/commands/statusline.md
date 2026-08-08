---
description: Add live Engram activity to the Claude Code status line — inspects whatever status line already exists and adds a segment to it, rather than replacing it. Backs up every file it edits.
allowed-tools: ["Bash", "Read", "Edit", "Write"]
---

Add an Engram segment to this user's status line. **Their status line is their code.** Whatever
is already in it — git branch, model name, token cost, directory — stays exactly as it is, and
your segment goes beside it. Adding one field to a script someone wrote is the whole job; if you
find yourself writing a status line from scratch when one already exists, you have misread this.

## First, find out what you are working with

One Bash call, then reason over it:

```bash
echo "=== existing status line configuration ==="
for f in "$HOME/.claude/settings.json" "$HOME/.claude/settings.local.json" ./.claude/settings.json; do
  [ -f "$f" ] && { echo "--- $f"; grep -A5 statusLine "$f" 2>/dev/null || echo "(no statusLine key)"; }
done
echo "=== which binary is installed, and does it emit the activity kinds? ==="
if BIN=$(command -v engram); then
  echo "$BIN"
  # A throwaway home, never the real one: this writes a fact and two hook records.
  h=$(mktemp -d)
  "$BIN" --home "$h" init >/dev/null 2>&1
  echo '{"session_id":"s","prompt":"I saw a film last Saturday"}' | "$BIN" --home "$h" hook user-prompt >/dev/null 2>&1
  echo '{"session_id":"s","tool_name":"Edit","tool_input":{"file_path":"/x/W.cs"}}' | "$BIN" --home "$h" hook file-touched >/dev/null 2>&1
  echo "emits: $(grep -o '"kind":"[a-z-]*"' "$h/telemetry.jsonl" 2>/dev/null | sort -u | tr '\n' ' ')"
  rm -rf "$h"
else
  echo "(engram not on PATH)"
fi
echo "=== the real log ==="
ls -lh "${ENGRAM_HOME:-$HOME/.engram}/telemetry.jsonl" 2>/dev/null || echo "(no log yet)"
tail -1 "${ENGRAM_HOME:-$HOME/.engram}/telemetry.jsonl" 2>/dev/null
```

**If that probe does not list `user-prompt` and `file-touched`, stop and say so.** The installed
binary predates those events and your segment would be permanently blank. Tell them to reinstall
(`./scripts/install.sh` from the repo, or however they installed it) and do nothing else. A
segment that renders nothing looks identical to a broken one, so shipping it teaches them to
distrust the feature.

If a `statusLine` command already exists, **read that script before touching it.**

## The data

`~/.engram/telemetry.jsonl` (or `$ENGRAM_HOME/telemetry.jsonl`) is one JSON object per line,
append-only, newest last. It is the same data a webhook subscriber receives, so a status line and
a dashboard never disagree.

Every record has `timestamp` (ISO-8601, **UTC**), `session_id`, `kind`. Then, by kind:

**`session_id` is two id spaces, not one.** Hook records — `file-touched`, `user-prompt`,
`session-start`, `subagent-start`, `pre-compact` — carry Claude Code's session id, the same one
arriving on stdin. Everything else carries the MCP transport's `Mcp-Session-Id`, or `server`, or
`cli`. The two never share a value and no record connects them (D43), so a tool call cannot be
attributed to the session that caused it.

| kind | means | extra fields |
|---|---|---|
| `file-touched` | a file was edited | `path` |
| `user-prompt` | Engram captured something the user said in passing | — |
| `remember` | the model chose to save a fact | `query` |
| `recall` / `browse` / `expand` | the model read memory | `query`, `fact_count`, `coverage` |
| `digest` / `revise` | the model wrote up or corrected memory | — |
| `session-start` / `subagent-start` | primer delivered | `long_term_fact_count`, `tokens_returned` |
| `session-open` | first MCP tool call of a transport session | — |
| `index` / `embedding` | work with a duration | `phase`: `started`, `finished`, `failed` |
| `server-start` / `server-stop` | the server came up or went down cleanly | — |
| `pre-compact` | context was compacted | — |

## What the segment must get right

These are not style preferences. Each one is a way this goes wrong that is invisible once it does.

- **Seek to the end; never read the file.** It grows without bound — hundreds of KB within days.
  `tail -c 8192 | grep '^{' | tail -1` costs the same on a 300 KB log as on a 3 MB one.
- **One log serves every session on the machine.** Unfiltered, this window shows a file edited in
  some other project — observed, and it reads as a bug in Engram rather than as the log being
  shared. But **filtering on `session_id` alone is the wrong fix**: by the two id spaces above it
  would drop every `recall`, `remember`, `browse` and `expand`, which is most of what is worth
  showing. Keep a record when it is **either** this session's **or** of a kind nobody can
  attribute anyway — MCP tool calls, and `index` / `embedding` / `server-*`, which are global.
  Only another session's hook records fall out, and those are the whole leak.
- **Claude Code cuts a long line; it does not wrap it.** One line of output is one row, so a line
  wider than the terminal loses its tail, and a resize takes segments with it. `COLUMNS` and
  `LINES` are exported to the script (Claude Code v2.1.153+) — `tput` cannot see the terminal
  because the output is captured, so read those instead. A script that packs its segments into
  rows itself keeps everything visible at any width. Leave one column unwritten: terminals
  disagree about whether writing the last cell wraps now or on the next character, and that
  difference is a blank row. If you are adding a segment to a line that already fills the width,
  you are the one who pushed something off the edge — say so, or fix it while you are there.
- **Clear stale activity, and know which clock you are on.** The newest event describes what
  happened, not what is happening, so an activity word has to be dropped once it ages out —
  otherwise the line freezes on "indexing" forever and stops meaning anything. Keep the durable
  numbers when you drop it. **A short threshold does nothing on its own**: by default the script
  re-runs only when a new assistant message arrives, so any age test is evaluated once per turn
  and a fresh event stays frozen on screen until the next one. `refreshInterval` in the
  `statusLine` block re-runs it on a timer (minimum 1 second) and is what makes a short threshold
  mean anything. It costs a render per second — measure yours and say what it is.
- **`index` and `embedding` are not instants, and must not be put on that timer.** They carry
  `phase`, so how long to keep showing them is a fact and not a guess (D55): show one while its
  `started` has no later `finished` or `failed`. Any freshness window short enough to be useful for
  instants blanks an embedding pass that is still running — one batch was measured at 28 seconds —
  and it blanks it in the direction that looks like nothing is wrong. Bound a dangling `started`
  anyway, generously: a killed process never writes its second half, exactly as `server-stop` is
  best-effort for the same reason.
- **Be silent when there is nothing to say.** No log, no readable log, no records: print nothing
  and exit 0. This runs on every status line update and must never be what breaks their prompt.
- **Drain stdin.** Claude Code pipes session JSON in. A script that never reads it can block.
- **Never open the database per render.** Do not call `engram status`, `engram recall`, or any
  other subcommand: process start alone is ~8 ms and the store open is another 1–1.5 ms, against a
  file read that costs microseconds. Read the log.
- **`long_term_fact_count` only rides primer events**, so a count taken from the log is "as of the
  last session start", not live. That is usually the right trade — say so if you show it, and do
  not reach for the database to make it exact.
- **`file-touched` is deliberately lossy under load.** The hook that writes it refuses to wait for
  the log, so a burst of edits drops a small fraction — measured 2% idle, 30% on a busy machine.
  Fine for "something is happening"; useless for counting edits or deriving a rate. Do not build a
  number out of it.

## Making the change

Back up anything you edit — `cp X X.bak-$(date +%Y%m%d-%H%M%S)` — and say where the backup is.

If they have **no** status line, write a small script (`~/.engram/statusline.sh`, `chmod +x`) and
add the key:

```json
{ "statusLine": { "type": "command", "command": "~/.engram/statusline.sh" } }
```

If they **already** have one, add your segment inside their script and leave their `settings.json`
alone. Match the code around it — their quoting, their helper functions, their separator, their
colour scheme. A segment that looks bolted on is a segment they will delete.

## Before you say it is done

Run their status line script the way Claude Code runs it, and show the output:

```bash
echo '{"session_id":"test","model":{"display_name":"Opus"}}' | <their script>
```

An empty activity slot is expected when nothing has happened recently — that is the decay working,
not a failure. To prove the branch that matters, point `ENGRAM_HOME` at a throwaway directory
holding one hand-written record with a current timestamp, run it again, and show that too.

Three checks that each catch a defect the happy path cannot:

- Plant a record under a **different** `session_id` and confirm it does not appear.
- Plant one just inside and just outside your freshness window, and an `index`/`embedding`
  `started` well outside it, and confirm the first two behave and the third still shows.
- Run at `COLUMNS=80` and confirm every row fits, then at `COLUMNS=200` for the single-line case.
- Delete the log entirely and confirm the script still exits 0 and prints their other segments.

Then break each guard you added and show it failing before restoring it — a filter that was never
tested against a foreign record is indistinguishable from no filter. Report the per-render cost if
it is above about 50 ms.
