# ADR: Per-run worktree model + same-root run lock (concurrency)

> **Status:** Accepted; rolling out across AB#3085 PR stack (PR 1a/1b/1c, PR 2,
> PR 3 launcher, PR 4 workflows).
> **Supersedes:** the implicit "main worktree + sibling per-item worktrees"
> convention referenced obliquely in `docs/onboarding-guide.md` and inherited
> from the original `Invoke-PolyphonySdlc.ps1` (PR #194).

## Context

The polyphony SDLC engine drives multi-item, multi-wave concurrency: a single
apex run fans out into per-item lifecycle dispatches (`plan-level`,
`actionable`, `implement-merge-group`, `feature-pr`) inside an
`apex-wave-dispatch` loop with `max_concurrent: 3`. Several layers of state
are mutated in parallel:

- **Filesystem-level:** branch checkouts (`git checkout`), worktree dirty
  state (uncommitted changes from coder agents), in-flight `git rebase` /
  `git merge`.
- **Logical-level:** the EdgeGraph (cross-item dependencies), the run
  manifest, parent-plan generations (each plan PR merge bumps a generation),
  PR-lifecycle state, remote refs (`feature/{r}`, `plan/{r}`, `mg/{r}_{p}`,
  `impl/{r}-{i}`, `evidence/{r}-{i}`).

Until AB#3085, both layers were "protected" by a single mechanism: a
**same-root run lock** keyed off the apex root id. Two driver invocations on
the same root would resume / refuse; different roots could run concurrently.

The same-root lock is necessary AND sufficient for the **logical** layer (one
controller for the branch-graph + manifest + PR state per root), but it is
**not sufficient** for the filesystem layer in the legacy "shared main
worktree" model:

- Per-item worktrees were spawned as siblings of the operator's main
  checkout (`<repo>-item-<id>`), but the **launcher itself** defaulted
  `WorktreeRoot = (Get-Location).Path`. Operators launching from
  `~/projects/polyphony` (the main worktree) had the apex driver yank
  `main` off and onto an `impl/` branch mid-conversation. This is the
  **launcher hijack bug**.
- A human editing the main worktree concurrently with a driver run on
  the same root would race the driver's `git checkout`. The same-root
  lock did not cover this — it only covered driver-vs-driver.
- Stale prunable worktrees from past runs accumulated under the operator's
  parent directory, with no GC.

## Decision

Adopt a **bare repo + per-run worktree tree** layout, separate from the
operator's main worktree:

```
~/projects/polyphony.git/                # bare (objects + refs)
~/projects/polyphony/                    # operator's main worktree, ALWAYS on `main`
~/projects/polyphony-runs/
  apex-{root}/                           # plain container directory (NOT a worktree)
    feature-{root}/                      # worktree on feature/{root}
    plan-{root}/                         # worktree on plan/{root}
    plan-{root}-{item}/                  # worktree on plan/{root}-{item}
    impl-{root}-{item}/                  # worktree on impl/{root}-{item}
    mg-{root}_{mg_path}/                 # worktree on mg/{root}_{mg_path}
    evidence-{root}-{item}/              # worktree on evidence/{root}-{item}
```

Properties:

1. The main worktree is the **operator's workspace** and is never targeted
   by the SDLC. Launcher refuses any `-WorktreeRoot` that resolves to or
   inside the main worktree (`Test-IsSameOrInside` with OrdinalIgnoreCase
   on Windows).
2. The apex run gets its own per-apex tree under `polyphony-runs/`. The
   apex container is a **plain directory**, not a worktree (avoiding the
   "child worktrees show up as untracked content of the parent worktree"
   conflict).
3. Per-item worktrees are **siblings** under the apex container. Naming
   derives mechanically from the canonical branch grammar
   (`{branch with / replaced by -}/`).
4. Path resolution lives in **one place**: `polyphony worktree
   {init-apex,create}` derives `runs_root` from `git --git-common-dir` and
   constructs `{runs_root}/apex-{root}/{slug}/`. PowerShell never mirrors
   the resolver.
5. The launcher reads `runs_root` and `main_worktree_path` from
   `polyphony worktree init-apex`'s output envelope (PR 3). Defense-in-depth:
   refuses if init-apex's derived path is or is inside main.

## Two locks, two layers

The per-run worktree model **does not replace** the same-root run lock — it
**complements** it. The two address different concurrency hazards:

| Layer       | Hazard                                               | Mitigation                                           |
|-------------|------------------------------------------------------|------------------------------------------------------|
| Filesystem  | Checkout / dirty-state contention                    | **Per-run worktree tree** (this ADR)                 |
| Filesystem  | Sibling per-item dirty state racing on shared HEAD   | **Per-item sibling worktrees** under apex container  |
| Logical     | Two drivers mutating same `feature/{r}` / manifest   | **Same-root run lock** (pre-existing)                |
| Logical     | Same root's parent-plan generations racing           | **Same-root run lock** + per-item driver gating      |

**Same-root run lock retained.** Two drivers on the same apex root would
still race the branch graph, manifest, parent-plan generation counters, and
remote refs (`feature/{r}` push contention) even with isolated worktrees.
The lock continues to enforce "same root → single controller; second attempt
resumes or refuses." Different roots may run concurrently, exactly as
before.

**Per-run worktrees alone are not enough.** A naive reading would be "give
each driver its own worktree and you can have N concurrent drivers." That is
**wrong** — the logical layer would still race. The model is "per-run
worktrees prevent the filesystem races; the same-root lock prevents the
logical races; together they enable safe concurrency."

## Driver-level pre-flight (PR 4)

Two filesystem hazards remain even with the per-run worktree tree:

1. **Operator side-edits in an apex worktree between driver dispatches.**
   Resume-after-pause picks up an apex worktree the operator has been
   poking at; uncommitted changes break `git checkout` /
   `git rebase` mid-flight.
2. **Wrong-branch on resume.** A previous resume left the apex worktree
   on a sibling branch; the next dispatch's `git rebase` runs against the
   wrong base.

Mitigation: the apex driver calls `polyphony worktree assert-clean
--path {apex_worktree} --expected-branch {feature/<root>}` **before each
dispatch**. The verb's routing-style envelope reports `dirty`,
`wrong_branch`, `git_operation_in_progress`, `path_missing`, or
`not_a_worktree`; the driver routes to a human gate to surface the
remediation.

## Deferred: per-apex-worktree run lock

The current same-root run lock prevents two drivers on the same apex root.
With per-item sibling worktrees, an in-theory-novel race exists: two
**threads inside the same driver process** could try to spawn the same
sibling worktree concurrently. Today this cannot happen because conductor's
`for_each` serializes sibling spawns within a wave; `polyphony worktree
create` is itself idempotent (existing worktree on the expected branch is
treated as success).

If conductor changes its `for_each` semantics in the future, an explicit
per-apex-worktree advisory lock (e.g. `flock` on a marker file in the apex
container) would be needed. **Deferred** until that hazard surfaces.

## What about cross-machine?

Out of scope. The bare repo lives in the operator's `~/projects/`; nothing
in this model is shared across machines. Cross-machine concurrency is a
separate epic.

## Migration

`scripts/Migrate-ToBareRepo.ps1` (PR 2, AB#3097) is a two-phase
fresh-clone tool — Phase 1 clones bare into a sibling, Phase 2 (after the
operator moves their existing clone aside) adds the main worktree and
copies `.twig/` over. No in-place `.git` surgery.

## Acceptance signals

- **Hijack** structurally impossible: launcher has no path that targets
  the main worktree (verified by Pester refusal tests + PR 3 init-apex
  PathBoundary check).
- **Cross-contamination** structurally impossible: each apex run lives in
  its own subtree; per-item worktrees are siblings, not shared (verified
  by `polyphony worktree create` matrix tests).
- **Sprawl bounded**: `polyphony worktree gc` (PR 7) prunes
  polyphony-runs/ worktrees whose branches are gone.
- **Fresh clone + bootstrap** produces the layout end-to-end (verified by
  `Migrate-ToBareRepo.Tests.ps1`).

## Related ADRs

- `docs/decisions/branch-model.md` — branch invariants are unchanged; only
  *where* worktrees live changes.
- `docs/decisions/apex-driver.md` — the run lock + dispatch model that
  this ADR's filesystem-layer model complements.
- `docs/per-run-worktree-layout.md` — operator-facing reference for the
  layout; this ADR is the design rationale.
