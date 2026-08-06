# Engram — progress snapshot

**As of 2026-08-06.** `main` @ `e322643`, working tree clean, 46 decisions (D1–D46).

This is a handoff, not an authority. `CLAUDE.md` holds the invariants,
`docs/engram-implementation-plan.md` holds the decisions and their reasoning, and
`docs/engram-schema.sql` is the authority for database shape. Where this file and those
disagree, they win and this file is stale.

---

## Read these first, in this order

1. `CLAUDE.md` — the invariants that are easy to break by accident. All of them were paid
   for by a real defect.
2. `docs/engram-implementation-plan.md` — D1–D46. Skim the headings; read in full any
   decision you are about to touch.
3. This file — for what is in flight and what the last session learned the hard way.

---

## Build, publish, test

```bash
# build (warnings are errors)
dotnet build Engram.sln -c Release

# publish the AOT binary — tier-3 tests drive this, not the JIT build
dotnet publish src/Engram.Cli/Engram.Cli.csproj -c Release -r osx-arm64 -o ./out

# full suite. ENGRAM_TEST_BINARY is what makes tier 3 run at all.
export ENGRAM_TEST_BINARY=$PWD/out/engram
dotnet test Engram.sln -c Release

# CUDA variant (builds anywhere; only the packaging is verified — see below)
dotnet publish src/Engram.Cli -c Release -r linux-x64 -p:EngramGpu=cuda12
```

**Last measured, both green:**

| | Core | Integration | EndToEnd | total |
|---|---|---|---|---|
| no weights (the CI shape) | 336 | 410 (63 skipped) | 104 | **850** |
| with weights | 336 | 416 (57 skipped) | 104 | **856** |

`ENGRAM_TEST_MODEL_HOME` un-skips 6 real-weights tests. Point it at an Engram home whose
`models/` holds a GGUF:

```bash
ENGRAM_HOME=/tmp/e-weights ./out/engram init
ENGRAM_HOME=/tmp/e-weights ./out/engram model install all-minilm-l6-v2 --use-it
ENGRAM_TEST_MODEL_HOME=/tmp/e-weights dotnet test Engram.sln -c Release
```

The remaining skips are `sqlite-vec` tests, which need the extension side-loaded into the
home under test.

---

## What landed in the last two commits

### `6d3ba89` — D45: llama.cpp is linked, not launched

`provider = "local"` loads a GGUF into the Engram process through LLamaSharp.
`LlamaServer.cs`, `LocalRuntime`'s child process and `[embedding] server_path` are gone.
This reversed what had shipped and restored what D1 and spike E originally planned: the
shipped code ran `llama-server` as a child and *located* it in three places without ever
fetching it, so rung 2 of the install ended at a binary the user had to supply.

Three traps worth knowing before touching this code:

- **`LlamaNative.Prepare` must not state library paths.** Stating them was written,
  shipped into a publish, then measured against its own absence — the published binary
  embeds 45 facts with *no* path configuration at all. IL3000 is a warning about a branch
  with a working fallback. Worse, `WithLibrary` replaces LLamaSharp's selecting policy: on
  linux-x64 the CPU package ships only `native/{noavx,avx,avx2,avx512}/` and CUDA adds
  `native/cuda12/`, so a hand-written resolver picks by sort order and `avx` sorts first.
  The Mac-correct version would have run the weakest CPU build on every CUDA machine.
- **`Prepare` holds its lock across the registration, not just the flag.** Releasing early
  lets a second thread skip the gate and load the library out from under the first.
  8 failures in 8 runs before, 0 in 8 after. Needs no weights and no GPU.
- **Pooling is the third silently-failing knob**, after `dim` (D34) and the embedding space
  (D18). The paraphrase test is *not* a pooling guard — flipping MiniLM to `Last` leaves it
  passing. The suite proves the setting reaches llama.cpp, nothing more.

### `e322643` — D46: the primer records what it delivered

`session-start` and `subagent-start` now write `long_term_fact_count` and
`tokens_returned`. `fact_count` **stays null** on a primer record and has its own test:
on a recall it means facts returned to the model, and a primer returns a count line plus
up to two example bodies. A nearby number in that field is how D43's phantom-outage bug
happened.

---

## Verified vs. not

| claim | status |
|---|---|
| in-process embedding works on the published AOT binary | measured — 45 vectors in 0.46 s, MiniLM on Metal |
| binary growth does not move the hook budget | measured — 21.84→22.25 MB, `file-touched` p50 9.44→9.51 ms |
| publish output trimmed to the target RID | measured — 210 MB → 121 MB, of which 5.7 MB is llama.cpp |
| CUDA *packaging* | measured — real `linux-x64 -p:EngramGpu=cuda12` build, output inspected |
| CUDA *execution* | **not measured** — no NVIDIA device here |
| pooling values per model | **argued from architecture, never benchmarked** |
| D6's gate on M3 | **unread** — see below |
| D18's gate on M4 | **unmet**, and the adoption fraction is not computable (D43) |

---

## Open work, ranked

### 1. M3 (code graph) is gated shut — do not start it without re-reading D6

Going to read the gate found there was almost nothing to read. On the live instance:

| | ever | Aug 5 | Aug 6 |
|---|---|---|---|
| `session-start` | 54 | 33 | 20 |
| `subagent-start` | 336 | 81 | 255 |
| `remember` | 28 | 23 | 5 |
| `recall` | **7** | 7 | **0** |

All 7 recalls were personal/project questions — plugin conventions, a movie, a weekend
outing, permissions, a son's favourite game. **Zero code-structure queries.** The server
was up and reachable throughout; Aug 6 had MCP tool calls, just no recalls.

The plan now says the gate is **unread, not failed**. Reading 7 events as evidence
*against* code-structure questions would repeat D44's mistake in the other direction.
D46 makes the gate answerable going forward; it recovers nothing retroactive.

### 2. The adoption question itself — 28 writes against 7 reads

This is spec §1.2's stated cause of death ("every predecessor died because the LLM never
called the memory tool") appearing in Engram's own telemetry. Note the confound before
concluding anything: the primer reaches every session without a tool call, so recall
undercounts delivery by construction — which is exactly what D46 was written to fix. Give
it some accumulation before drawing a line through it.

### 3. `SpoolReader.Drain` has no production caller

`file-touched` writes and nothing reads. The live queue is at ~219 entries. `SpoolCompactor`
(D41) bounds the growth, so this is not urgent — the missing consumer *is* the M3-shaped
hole, and `doctor` says so out loud in `Diagnostics.cs:632`.

### 4. Score mass in `coverage` (D44, deliberately left open)

`coverage` currently keys off lane agreement only. The spec names score mass as a second
input. D44's reasoning for leaving it: one unmeasured knob is a rule, two are a preference.

### 5. Smaller

- Re-measure spike E with a `has_tensor` assertion — cheap now that weights load in-process.
- D27's open sub-question.
- `docs/engram-spec.md:62` "not a Sage replacement" — flagged, unresolved.
- Nested-git-checkout edge case in the scanner.

---

## Gotchas that cost real time

- **Set `ENGRAM_HOME` before running `./out/engram` by hand.** The three test guards protect
  test code, not your shell. A verification command without it writes to the real `~/.engram`.
  This has already happened once.
- **Prove a guard can fail.** Two guards this session looked green while proving nothing —
  the paraphrase test survived a deliberate pooling break, and an early AOT publish passed
  only because nothing referenced the assembly yet. Break the thing, watch the test fail,
  restore.
- **`git checkout` destroys uncommitted work** and behaves differently on untracked files.
  It ate a test-file rewrite this session.
- **The subagent cap is per-session (200).** It was exhausted, so the Ultra Advisor and
  `task-gopher` were both unreachable for the whole session. Raise
  `CLAUDE_CODE_MAX_SUBAGENTS_PER_SESSION` if you want them.
- **`dotnet test` filters run in a fresh process**, so env vars from a previous shell command
  are gone. A test that "passes in the suite and fails alone" is usually this, not a flake —
  though this session it was a genuine race that only the unset-env ordering exposed.
