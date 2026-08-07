---
name: install-ps1-parity-debt
description: Everything install.ps1 still owes relative to install.sh, and why it waits on a Windows machine
metadata:
  type: project
---

`install.ps1` froze at the shape `install.sh` had before the install-everything directive
(2026-08-06). The sh installer moved twice since — commits `7423b12` and `3fa60c2` — and
none of it has a PowerShell counterpart. The TODOs, in the order they should probably land:

1. **Defaults flip.** Every optional component installs by default. One question up
   front at an interactive `-Apply`: take everything (Enter) or be asked per step; ask
   mode prompts `[Y/n]` defaulting yes. `-No*` flags pin a step off unasked; `-With*`
   spellings pin on. A piped/non-interactive run takes the defaults. `install.ps1` still
   has `-WithPlugin` as opt-in and no mode question at all.
2. **Permission-grant consent exception.** A run nobody is watching never grants —
   piped runs decline; at a terminal, choosing "everything" up front *is* consent, so
   auto mode grants without re-asking. This is the one deliberate exception to
   "piped takes the defaults" and must survive the port intact.
3. **sqlite-vec step** (sh section 9c) — tri-state (fetched | skipped | failed), runs
   inside an `if` so a failure cannot abort a finished install, summary line either way.
4. **tree-sitter step** (sh: `fetch-tree-sitter.sh`, pinned digest-checked source
   tarballs compiled into `~/.engram/lib`). The fetch script is bash; Windows needs its
   own compile path (the binding already expects `<name>.dll` naming). This is the
   hard one — a C compiler on Windows means the VS Build Tools workload the README
   already requires for the binary build.
5. **Embedding step** (sh section 8b, commit `3fa60c2`) — stays interactive in both
   modes because provider/model are real tradeoffs; `-Embedding*` flags answer
   unattended runs, `-NoEmbeddings` skips; no flags and no terminal defers and the
   summary says `engram init --with-embeddings`. Drives the binary's own picker so the
   model catalog stays single-sourced — the installer owns *when* the question is
   asked, never *what* the choices are.
6. **TUI styling** — step headers, banner, bold prompts, gated on a real terminal and
   honoring `NO_COLOR`. (The binary's picker brings its own TUI; this item is only the
   installer's shell output.)
7. **Uninstall symmetry** — whatever new steps land, `uninstall.ps1` keeps its dry-run
   default and removes only what the installer created.

Two constraints on the port, both from measured history:

- The **tri-state falsification** is part of each optional step's definition, not
  optional polish: plant the invocation outside its guard, watch exactly the
  step-fails e2e test go red, restore. It has caught the `set -e`-equivalent bug four
  times in sh (plugin, tree-sitter, sqlite-vec, embeddings).
- The **17 installer-family Windows e2e failures** (measured on `9aa4751`'s CI run) are
  exactly the population this parity work revisits — they drive POSIX `install.sh`
  through `/bin/bash` and `File.SetUnixFileMode`. They want
  `Assert.SkipWhen(OperatingSystem.IsWindows(), …)` like `InstallerRoundTripTests`
  already carries, and the ps1 port should bring its own Windows-shaped e2e tests in
  their place.

Why it waits: parse-gating runs on every OS, but nothing here can execute PowerShell
apply-mode honestly — this needs a Windows machine or Windows CI iteration room, and CI
is currently disabled. Related but distinct: the 17 **server-family** Windows failures
(`/bin/sh` in `ServerLauncher.cs`/`MaintenanceLauncher.cs`) are a production defect, not
installer parity, and are tracked in `docs/engram-progress.md` open work #4.
