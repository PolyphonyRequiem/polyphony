---
name: polyphony-dogfood-recovery
description: >-
  Activate when a polyphony dogfood apex run is stuck or completed and the
  operator wants to wipe the prior-run state (branches, PRs, worktrees,
  manifest, watermark) so the apex can be re-launched cleanly. Since the
  reset workflow shipped, this is a thin runbook around
  `Invoke-PolyphonySdlc.ps1 -Intent reset` and the residual gotchas the
  automation cannot handle.

  Trigger phrases include:
  - 'clean up the apex run'
  - 'abort and reset apex'
  - 'restart from clean state'
  - 'reset work item state for re-run'
  - 'polyphony dogfood is stuck'
  - 'untangle partial workflow state'
  - 'the apex run died mid-way and I want to retry'
  - 'redo the apex'

  Do NOT activate for:
  - Pushing a work item forward toward completion. This skill is RESET only.
  - Individual work-item bugs unrelated to a run state — use regular
    `polyphony` verbs and `twig` operations directly.
  - Workflow-author-level fixes to prevent the bug recurring — defer to
    `polyphony-workflow-author` and `polyphony-cli-developer`.
user-invokable: false
---

# Polyphony Dogfood Recovery

Runbook for wiping a wedged / completed polyphony apex's prior-run state so
the apex can be re-launched. The bulk of what used to be a 5-axis manual
ceremony now lives in the `reset-apex@polyphony` workflow (PR run-reset
3/3); this skill covers (1) the one-liner, (2) the residual gotchas the
automation cannot handle, and (3) the post-reset smoke test.

For background on the reset model (run-started-at watermarks, branch-name
identity, per-leg ordering), see `docs/decisions/run-reset.md`.

Companion skills:

- **`polyphony-runtime`** — canonical invocation patterns; the re-launch
  step at the bottom of this skill defers to it.
- **`polyphony-workflow-author`** — when recovery surfaces a workflow-level
  bug worth fixing (e.g. the re-dispatch loop in incident AB#62286666).
- **`polyphony-cli-developer`** — when the bug lives in a `polyphony` CLI
  verb (including the `reset` verb family).
- **`polyphony-bootstrap`** — only for first-time onboarding of a fresh
  repo; irrelevant once `.polyphony-config/` exists.

---

## 1 · The one-liner

From any worktree of the polyphony bare repo (operator's normal cwd is
fine):

```powershell
# Dry-run preview (no mutations). Shows what would be torn down.
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <N> -Intent reset

# Same, then a confirmation gate, then execute.
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <N> -Intent reset -Execute

# Unattended (skips the confirmation gate). Use only when operator
# consent has been obtained out-of-band.
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <N> -Intent reset -Execute -AutoConfirm
```

The launcher dispatches `reset-apex@polyphony`, which calls
`polyphony reset apex --apex <N>` in dry-run, surfaces a confirmation
gate showing the per-leg counts (PRs to abandon, worktrees to remove,
branches to delete, manifest action, watermark target), then re-invokes
`polyphony reset apex --apex <N> --execute` on confirmation. The chain
halts on first failed leg; `polyphony reset apex` is idempotent on retry.

Additional flags:

- `-SkipState` — forwarded as `polyphony reset apex --skip-state`. Runs
  the cleanup chain but does NOT advance the per-apex run-started-at
  watermark. Use for hygiene sweeps that should not flip the
  satisfaction floor.
- `-Comment "<text>"` — override the closing comment posted on each
  abandoned PR. Empty (default) uses the built-in reset comment.

---

## 2 · Halt the run first (if mid-flight)

`-Intent reset` operates on the apex's branches/PRs/worktrees/manifest
regardless of whether a conductor process is currently running against
them. But running it against an in-flight apex will collide with the
conductor's open file handles + in-progress git operations. Stop the
conductor first.

```powershell
# Discover the pinned port (printed at launcher startup as
# 'Pinned conductor web port: <N>'; also in the new-window banner).
Invoke-RestMethod -Uri "http://127.0.0.1:<PORT>/api/kill" -Method POST

# Fallback: find and kill the python process directly.
Get-Process python | Where-Object { $_.CommandLine -match 'conductor' } |
    ForEach-Object { Stop-Process -Id $_.Id }
```

Then proceed with the one-liner in §1.

---

## 3 · Residual gotchas (the automation cannot handle these)

The reset workflow handles the canonical 5-axis state surface. These
edge cases still need manual attention:

### 3.1 · Uncommitted operator edits in apex worktrees

`polyphony reset worktrees` refuses to remove a worktree with uncommitted
changes (defense-in-depth — you'd lose work). If the preview reports
`failed_worktrees` with reason `dirty`, stash or commit those edits
first, then re-run.

```powershell
git -C "<runs_root>/apex-<N>/feature-<N>" status
git -C "<runs_root>/apex-<N>/feature-<N>" stash -u   # or commit + push to a backup branch
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <N> -Intent reset -Execute
```

### 3.2 · `twig` rewrote `.twig/config` mid-run

A known twig friction (filed separately): `twig` rewrites `.twig/config`
on every conductor invocation. The assert-clean preflight refuses to
dispatch with a dirty `.twig/config`. Reset doesn't run assert-clean
(no apex-worktree dependency), but the eventual re-launch with
`-Intent new` will. Restore before re-launching:

```powershell
git -C <main_worktree> checkout -- .twig/config
```

### 3.3 · Manually-curated work-item state

If you've hand-edited tags, fields, or state on the apex via twig
between the original run and the reset, `polyphony reset state` advances
the watermark but does NOT roll back those manual edits. Verify the apex
+ descendant state matches the desired pre-run baseline before
re-launch:

```powershell
twig show <ApexId> --output json | ConvertFrom-Json | Select state, tags
twig children <ApexId> --output json | ConvertFrom-Json | ForEach-Object { ... }
```

### 3.4 · Completed PRs are not closeable

Reset cannot un-merge or un-close completed PRs (ADO and GitHub both
disallow this by API). The watermark makes them invisible to the
observers — that is the design — but they remain in the platform's PR
history. If you need the platform-side record cleared, do it manually
via the UI before reset; reset will not retry.

---

## 4 · Post-reset smoke test

Before re-launching the apex, verify the reset took:

```powershell
# 1. Validate config + apex state.
polyphony validate-config
polyphony validate --work-item <ApexId>

# 2. Confirm the apex is in a re-dispatchable state.
polyphony state next-ready --work-item <ApexId>

# 3. Confirm the prior-run branches are gone.
git --git-dir <common_dir> branch -a --list "plan/<root>*" "impl/<root>-*" "mg/<root>-*"
git --git-dir <common_dir> worktree list

# 4. Re-launch.
./scripts/Invoke-PolyphonySdlc.ps1 -ApexId <ApexId> -Intent new
```

If any of the above surfaces residual prior-run state (branches,
worktrees, manifest entries, satisfaction-positive observations on a
fresh feature branch), file it as a reset-workflow bug — the workflow
is supposed to handle the full set. Re-running with `-Intent reset`
once more is safe (the verbs are idempotent), but if a second pass
doesn't clear it, the issue is in the verb chain, not in the
operator's environment.
