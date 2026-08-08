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
- **Decay stale activity.** Past a minute or two, the newest event describes history, not what is
  happening. Drop the activity word and keep only durable numbers, or the status line freezes
  showing "indexing" forever and stops meaning anything.
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
holding one hand-written record with a current timestamp, run it again, and show that too. Report
the per-render cost if it is above about 50 ms.
