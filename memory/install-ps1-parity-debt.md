---
name: install-ps1-parity-debt
description: Everything install.ps1 still owes relative to install.sh, and why it waits on a Windows machine
metadata:
  type: project
---

`install.ps1` froze at the shape `install.sh` had before the install-everything directive
(2026-08-06). The sh installer moved twice since — commits `587ed0f` and `3852e4f` — and
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
5. **Embedding step** (sh section 8b, commit `3852e4f`) — stays interactive in both
   modes because provider/model are real tradeoffs; `-Embedding*` flags answer
   unattended runs, `-NoEmbeddings` skips; no flags and no terminal defers and the
   summary says `engram init --with-embeddings`. Drives the binary's own picker so the
   model catalog stays single-sourced — the installer owns *when* the question is
   asked, never *what* the choices are.
6. **TUI styling** — step headers, banner, bold prompts, gated on a real terminal and
   honoring `NO_COLOR`. (The binary's picker brings its own TUI; this item is only the
   installer's shell output.)
7. **Uninstall symmetry** — whatever new steps land, `uninstall.ps1` keeps its dry-run
   default and removes only what the installer created. `uninstall.sh` has since moved
   further (2026-08-07): it inventories what is actually installed, an interactive
   `--apply` confirms each item before removing anything, and **backups survive a purge
   by default** (`--remove-backups` or an explicit yes deletes them) — the ps1 port
   needs all three, and the keep-backups default is the load-bearing one, because
   `backups/facts.jsonl` is the only thing that can restore a deleted store.

8. **The dry-run inversion (D49, 2026-08-07).** `install.sh` now installs by default;
   `--dry-run` is the brake and `--apply` is parsed and ignored for compatibility.
   `install.ps1` deliberately did **not** follow and still requires `-Apply`. The reason
   is specific to this item rather than general caution: the change makes a script act
   without being asked, parse-gating cannot catch an inverted conditional, and the way a
   half-applied inversion fails is a "dry run" that installs. The port is ~12 `if ($Apply)`
   sites plus the param and the two trailer messages — mechanical, but it must be *run*
   once on Windows before it ships, both halves: no flag installs, `-DryRun` changes
   nothing. Bring the two guards over with it.

Two constraints on the port, both from measured history:

- The **tri-state falsification** is part of each optional step's definition, not
  optional polish: plant the invocation outside its guard, watch exactly the
  step-fails e2e test go red, restore. It has caught the `set -e`-equivalent bug four
  times in sh (plugin, tree-sitter, sqlite-vec, embeddings).
- The **17 installer-family Windows e2e failures** (measured on `2cdfb2e`'s CI run) are
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
