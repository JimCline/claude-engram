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
| `recall` / `browse` / `expand` | the model read memory | `query`, `fact_count`, `coverage`, and the three counts it splits into — `long_term_fact_count`, `session_fact_count`, `prior_session_fact_count` |
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
  because the output is captured, so read those instead. Pack segments into rows yourself — see
  "Packing segments into rows" below — rather than emitting one line and hoping it fits. If you
  are adding a segment to a line that already fills the width, you are the one who pushed
  something off the edge — say so, or fix it while you are there.
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
- **`long_term_fact_count` means two different things, so filter by kind before you read it.**
  Every record carries the whole field set with nulls in the slots that do not apply, and this one
  is populated on seven kinds. On `session-start` and `subagent-start` it is the size of the whole
  corpus; on `recall`, `browse`, `expand` and `remember` it is how many of *that call's* returned
  facts came from long-term memory. Measured on a real log: 5053 on the primer, 7–22 on the
  recalls beside it. So `grep -o '"long_term_fact_count":[0-9]*' | tail -1` renders `engram:11`
  moments after a recall against a store holding 5053 — a plausible number, in the right place,
  silently wrong. Grep `'"kind":"(session-start|subagent-start)"'` first. This is the same mistake
  D43 traced a wrong adoption conclusion back to, and it looks correct until someone checks it
  against the store.
- **A primer count is "as of the last session start", not live.** That is the right trade — say so
  if you show it, and do not reach for the database to make it exact.
- **`file-touched` is deliberately lossy under load.** The hook that writes it refuses to wait for
  the log, so a burst of edits drops a small fraction — measured 2% idle, 30% on a busy machine.
  Fine for "something is happening"; useless for counting edits or deriving a rate. Do not build a
  number out of it.

## Packing segments into rows

Collect segments into an array before printing anything — you cannot know whether one fits until
you know the width of everything already on its row. This is a working recipe, not a sketch;
build the same shape rather than inventing your own layout logic.

**Measure width after stripping color, never before.** A colored segment carries ANSI escapes
(`\033[36m...\033[0m`) that cost zero screen columns but count as characters to `${#seg}`. Skip
this and every width is wrong in the direction that wastes space — segments wrap a row early:

```bash
strip_ansi() {   # sets REPLY rather than printing; a command substitution forks once per
                  # segment, and this runs for every segment on every render
    local s=$1
    REPLY=""
    while [[ $s == *$'\033'* ]]; do
        REPLY+="${s%%$'\033'*}"
        s=${s#*$'\033'}
        s=${s#*m}
    done
    REPLY+=$s
}
```

**Leave one column unwritten.** Terminals disagree about whether writing the last cell wraps
immediately or on the next character written after it. Budget one column narrower than `COLUMNS`
reports, or that disagreement becomes an extra blank row on some terminals and not others:

```bash
sep="$(printf " ${DIM}|${RESET} ")"
sep_width=3
avail=$(( ${COLUMNS:-0} - 1 ))
```

**Fall back to a single line when there is nothing to lay out against.** No `COLUMNS` (an older
Claude Code, or the script run by hand outside Claude Code) or a width too narrow to pack against
meaningfully — emit the one line this always was rather than guessing at a size:

```bash
if [ "$avail" -lt 20 ]; then
    [ -n "$row" ] && row="${row}${sep}"
    row="${row}${seg}"
    continue
fi
```

**Pack greedily, and never split a segment across rows.** Walk the segment list once, adding each
to the current row if it fits alongside the separator, starting a new row otherwise:

```bash
rows=()
row=""; row_width=0
for seg in "${segments[@]}"; do
    strip_ansi "$seg"; seg_width=${#REPLY}

    if [ -z "$row" ]; then
        row="$seg"; row_width=$seg_width
    elif [ $(( row_width + sep_width + seg_width )) -le "$avail" ]; then
        row="${row}${sep}${seg}"; row_width=$(( row_width + sep_width + seg_width ))
    else
        rows+=("$row")
        row="$seg"; row_width=$seg_width
    fi
done
[ -n "$row" ] && rows+=("$row")

for row in "${rows[@]}"; do
    printf "%b\n" "$row"
done
```

A segment wider than `avail` on its own still gets a row to itself rather than being cut — this
packs rows, it does not truncate a segment. That gap is real and this recipe does not close it;
say so rather than inventing a truncation rule nobody asked for.

## Making the change

Back up anything you edit — `cp X X.bak-$(date +%Y%m%d-%H%M%S)` — and say where the backup is.

If they have **no** status line, write a small script (`~/.engram/statusline.sh`, `chmod +x`) and
add the key:

```json
{ "statusLine": { "type": "command", "command": "~/.engram/statusline.sh", "refreshInterval": 1 } }
```

If they **already** have one, add your segment inside their script and leave their `settings.json`
alone — with one exception. Your segment clears a stale event after a few seconds (see "What the
segment must get right" above), and that clearing is invisible without `refreshInterval`: without
it the script only re-runs when a new assistant message arrives, so the event your script means to
age out sits frozen on screen until the next turn no matter what the script computes. Check their
`statusLine` block for `refreshInterval`; if it is absent or larger than your freshness window
(`engram_fresh_seconds` above), set it to `1` and say you did — this is the one line in
`settings.json` you touch even when everything else stays as it was. Match the code around your
segment — their quoting, their helper functions, their separator, their colour scheme. A segment
that looks bolted on is a segment they will delete.

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
- Grep the live `settings.json` for `refreshInterval` and confirm it is present and `<=` your
  freshness window. The script passing the checks above in isolation proves the *logic* is right;
  it proves nothing about whether anything ever re-runs it on a timer, and that is the one failure
  this whole checklist can pass while the feature is silently frozen in real use.

Then break each guard you added and show it failing before restoring it — a filter that was never
tested against a foreign record is indistinguishable from no filter. Report the per-render cost if
it is above about 50 ms.
