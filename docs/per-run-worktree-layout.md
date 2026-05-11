# Per-run worktree layout

> **Status:** in flight (AB#3085 epic). The runtime tooling that produces and consumes this layout ships as a stack of small PRs; this document is the authoritative description of the target end-state.

## Why

The legacy SDLC orchestration ran every apex inside the operator's main clone (a normal non-bare `.git` directory). That model produced two recurring production bugs that no amount of defensive code could eliminate:

1. **Launcher hijack.** `Invoke-PolyphonySdlc.ps1` defaulted `WorktreeRoot = (Get-Location).Path`. When the operator launched from `~/projects/polyphony`, the apex driver yanked the operator's main worktree off `main` onto an `impl/{apex}-{item}` branch mid-conversation. Recurred at least twice (checkpoints 157 and 165 of the prior session state).

2. **`worktree_dirty` cross-contamination.** Sibling apex runs and ad-hoc operator state on the shared main worktree raced each other. A wave-dispatched item would arrive at `git status --porcelain` and find unrelated changes left behind by a peer.

Both classes are **structural**: the only worktree available to dispatch into was the one the operator was using. Refusing to dispatch there would leave the SDLC with no worktree at all.

The bare-repo + per-run worktree model eliminates both classes by construction. Each apex run gets its own worktree tree under `~/projects/polyphony-runs/apex-{N}/`. The operator's main worktree (`~/projects/polyphony`) is **never** a dispatch target.

## Target on-disk layout

```
~/projects/polyphony.git/                    bare repo (objects + refs only)
~/projects/polyphony/                        operator's main worktree, ALWAYS on main
~/projects/polyphony-runs/
  apex-3085/                                 one root per apex run
    feature-3085/                              apex feature trunk worktree
    plan-3085-XXXX/                            nested per plan branch
    impl-3085-XXXX/                            nested per impl branch
    mg-3085_pg-XXXX/                           nested per merge-group branch
    evidence-3085-XXXX/                        nested per evidence branch
```

Properties:

- All worktrees share `~/projects/polyphony.git/objects` and `refs` — cheap on disk.
- Per-apex root holds a manifest of its child worktrees so `polyphony worktree gc` can recurse.
- Branch invariants from the **polyphony-branch-model** skill are unchanged. The model changes *where* worktrees live, not how branches relate.

## Detection

`polyphony state preflight` runs an advisory check named **`bare_repo`** at the start of every SDLC apex run. The check resolves the git common-dir and probes `git --git-dir={commonDir} rev-parse --is-bare-repository`. When that returns `false`, the check fails with a remediation pointer to this document.

The check is **advisory** until the migration tooling ships — required-now would gate every SDLC apex run before operators have any way to migrate. Once `scripts/Migrate-ToBareRepo.ps1` (PR 2 of the AB#3085 stack) lands, the check flips to required on both `polyphony state preflight` and `polyphony state preflight-lite`.

## Probe semantics

Two non-obvious details, both empirically verified:

1. **Probe via `--git-dir`, not cwd discovery.** Running `git rev-parse --is-bare-repository` from the cwd of a *linked* worktree of a bare repo returns `false`, because git resolves the worktree-specific gitdir under `{commonDir}/worktrees/{name}/` — that gitdir is itself non-bare. The shared common-dir IS bare, but you only see that with the explicit `--git-dir={commonDir}` form.

2. **`safe.bareRepository=explicit`.** Many secured workstations (including Daniel's) set this globally. Bare repos must then be addressed via `--git-dir=<path>` or `GIT_DIR=<path>` env — plain `git -C <bare> rev-parse` fails with `fatal: cannot use bare repository '...' (safe.bareRepository is 'explicit')`. Every SDLC verb and helper that touches the bare must construct git invocations with the explicit form. The `bare_repo` preflight check uses `--git-dir` for exactly this reason.

## Migration

**While `Migrate-ToBareRepo.ps1` (PR 2) is in flight,** the migration is manual:

```pwsh
# 1. From any directory OUTSIDE ~/projects/polyphony:
cd ~/projects
git clone --bare https://github.com/PolyphonyRequiem/polyphony.git polyphony.git

# 2. Add a clean main worktree at the operator path:
git --git-dir=$HOME/projects/polyphony.git worktree add $HOME/projects/polyphony.new main

# 3. Verify the new clone works (and that any global gitconfig including
#    safe.bareRepository=explicit accepts the explicit --git-dir form):
git --git-dir=$HOME/projects/polyphony.git rev-parse --is-bare-repository   # → true

# 4. Move your existing operator clone aside (do NOT delete; carries any
#    uncommitted local state, hooks, IDE config, etc.):
mv ~/projects/polyphony ~/projects/polyphony.legacy
mv ~/projects/polyphony.new ~/projects/polyphony

# 5. Create the runs root (empty until the launcher dispatches an apex):
mkdir ~/projects/polyphony-runs
```

After this:

- `~/projects/polyphony.git/` is the source of truth (bare).
- `~/projects/polyphony/` is your day-to-day operator worktree, always on `main`. The SDLC orchestrator will refuse to dispatch into this path.
- `~/projects/polyphony-runs/` is empty; `polyphony worktree init-apex --apex N` (PR 1b) will populate it on the next apex dispatch.
- `~/projects/polyphony.legacy/` is the old layout; once you've confirmed nothing important lived there, delete it.

**Once `Migrate-ToBareRepo.ps1` ships,** that script will encapsulate the procedure with `--dry-run` and `--commit` phases.

## Verification

After migration, `polyphony state preflight --work-item N` should show:

```
advisory_checks:
  - bare_repo: PASSED
    Bare repo at /Users/{you}/projects/polyphony.git — bare-repo + per-run worktree layout (AB#3085).
```

If `bare_repo` still fails, confirm:

- The cwd you ran preflight from is inside a worktree backed by the bare common-dir (i.e. the cwd's `git rev-parse --path-format=absolute --git-common-dir` resolves to `~/projects/polyphony.git/`).
- The bare common-dir's `config` file contains `bare = true` under `[core]`.

## References

- **Epic:** [AB#3085](https://dev.azure.com/dangreen-msft/Polyphony/_workitems/edit/3085) — bare-repo + per-run worktree model
- **This sub-issue:** [AB#3093](https://dev.azure.com/dangreen-msft/Polyphony/_workitems/edit/3093) — preflight bare-repo check + this doc
- **Branch model:** `docs/decisions/branch-model.md` — invariants the new layout preserves
- **State location:** `docs/decisions/branch-model.md` § State location (Rev 4.2) — common-dir resolution rules consumed by the preflight probe
