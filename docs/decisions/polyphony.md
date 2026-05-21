# Apex Driver — Tree-Walking Dispatch with Per-Item Worktree Isolation

> **Status:** Accepted. Phase 7 — apex-driver MVP.
> **Driver:** Phase 7 needs an SDLC orchestrator that walks an apex
> tree level by level, dispatches lifecycle work per item with
> isolation, and re-enters cleanly after human gates or interruptions.
> The deleted `polyphony-full.yaml` was a single-shot pipeline; the
> apex-driver is a *driver* — it loops over EdgeGraph waves until the
> apex root reports `satisfied` (or the loop is abandoned at a gate).
> **Supersedes:** the deleted `polyphony-full.yaml`.

## Context

The apex tree is a typed forest of work items rooted at an "apex" — a
root work item whose satisfaction defines completion of an SDLC unit.
Each non-root item carries one or more facets (plannable, actionable,
implementable). Facets become satisfied at different times and through
different machinery, and items have ordering edges between them
(`children_seeded → ...`, `action_satisfied → implementation_merged`,
etc.) that the EdgeGraph computes into **waves** — sets of items that
can be dispatched in parallel within a wave, with strict serialization
between waves.

The dispatch contract is:

1. Build a worklist for the apex (`polyphony worklist build`).
2. For each wave (in topological order):
   - For each item in the wave (in parallel up to a cap):
     - Classify what *kind* of dispatch the item needs *right now*
       based on its observable state (`polyphony state next-ready`).
     - Spawn an isolated worktree.
     - Run the appropriate lifecycle workflow (plan-level / actionable
       / implement-merge-group / feature-pr).
     - Tear down the worktree.
   - Integrate the wave (merge per-item branches into the apex feature
     branch in topological order).
3. Re-evaluate the worklist (waves can change as items satisfy or
   renegotiation fires) and loop.
4. When the apex root reports `satisfied` and the EdgeGraph reports
   no remaining work, mark the apex satisfied and exit.

Several open design questions had to be settled to ship this:

- **Q1.** Does the dispatch loop live in *one* workflow or fan out to
  sub-workflows?
- **Q2.** How does per-item lifecycle classification happen — inside
  the workflow YAML, or in a script step?
- **Q3.** How is each item isolated from siblings dispatched in
  parallel within the same wave?
- **Q4.** How does the driver re-enter after a human gate, a
  renegotiation, or an interrupted run?

## Decision

### Q1 — Three-file split, not one

Conductor's `for_each` agent invokes exactly **one** thing per
iteration. Per-wave handling needs to do *two* things — fan out to N
item dispatches (for_each), then run the wave integrator (script).
That requires more than one step per wave, so wave handling must live
in a sub-workflow.

The same logic recurses one level lower: per-item handling needs to
classify, spawn a worktree, run a lifecycle workflow, tear down the
worktree. That's also multiple steps, also a sub-workflow.

We ship **three workflows**:

- `apex-driver.yaml` — outer dispatch loop (`build_worklist` →
  `wave_dispatch_loop` → `apex_completion_gate` → loop or close).
- `apex-wave-dispatch.yaml` — per-wave fan-out (`dispatch_items`
  for_each → `integrate_wave` script).
- `apex-item-dispatch.yaml` — per-item pipeline (`classify_lifecycle`
  → `spawn_worktree` → `lifecycle_dispatch` → `teardown_worktree`).

### Q2 — Lifecycle classification is a script

The classification rule set ("if the item's next-ready signal is
`plan_authored`, route to plan-level; if it's `action_satisfied`,
route to actionable; …") has to consult `polyphony state next-ready`
output — a JSON envelope with a `status`, `kind`, `signal`, and a
flag for whether the item is the apex root. That logic is too much
for a Jinja expression, would explode the YAML route block, and
would be untestable.

We ship `lifecycle-router.ps1` — a deterministic classifier that
wraps the `polyphony state next-ready` call, applies the priority
rules (plan > action > impl with the action-before-impl rule per the
implicit `action_satisfied → implementation_merged` edge), and emits
a routing envelope (`route: plan-level | actionable | implement-merge-group |
feature-pr | fast-path | monitoring | blocked | error`). The workflow
just reads `classify_lifecycle.output.route` and dispatches.

This pattern — *classification as deterministic script, dispatch as
trivial YAML route* — is the same one used by
`route-actionable-executor.ps1` for the actionable facet's
polyphony-vs-human split. We get unit tests, observability via the
JSON envelope, and a one-line YAML route block.

### Q3 — Per-item git worktrees keyed by work-item id

Each item dispatched in parallel within the same wave gets its own
worktree at `<repo-parent>/<repo-name>-item-<work_item_id>` on a
branch named `sdlc/apex/<work_item_id>`. Branches fork from the apex
feature branch (`feature/<apex_id>`), not from `main`, so the
per-item branch already contains the integrated work of prior waves.

`worktree-manager.ps1` handles spawn (`git worktree add -b …`) and
teardown (`git worktree remove --force`) idempotently: spawning is a
no-op when the worktree exists; teardown is a no-op when it doesn't.
This is what makes re-entry safe.

After each wave's items complete, `wave-integrator.ps1` merges the
per-item branches into `feature/<apex_id>` in **topological
order** (read from `polyphony edges check`). Default merge strategy
is `--no-ff` — every per-item merge produces an explicit merge
commit, so the apex feature branch keeps an auditable record of
which work item contributed which change. Conflicts are captured
(files, branches involved), the merge is aborted (`git merge
--abort`), and the wave continues; conflicts are reported up to a
human gate (`wave_conflict_gate`).

### Q4 — Observable-state re-entry

The driver's loop variable is **the worklist itself** — recomputed
on every iteration via `polyphony worklist build`. The driver does
not carry a "step counter" or "last completed item" pointer. After a
gate or restart, the driver re-builds the worklist, the EdgeGraph
re-classifies what's still pending, and the next wave is whatever's
ready *now*. Items already satisfied drop out of the worklist on
their own.

This is what the **renegotiation** policy hooks into: when a child
plan-level invocation reports `renegotiation_pending: true` (a
"bubble-up" signal), apex-driver consults `policy.renegotiation
.auto_decide`. With `prompt`, it routes to the renegotiation human
gate. With `auto_restart`, it restarts the dispatch loop without a
gate. With `ignore`, it continues. In all three cases the next-loop
worklist build picks up the (possibly mutated) tree state — no
explicit "rewind" mechanism needed.

## Invocation

`apex-driver@polyphony` is the canonical SDLC entry point. The CLI does not
itself drive a pass; this workflow does. Prerequisites (verify before invoking):

- `polyphony health` exits 0 (CLI present, env wired, twig cache reachable).
- `twig` is on PATH and authenticated against the target ADO org/project
  (`twig workspace` returns a workspace).
- `gh` is on PATH and authenticated for any GitHub-hosted PR work
  (`gh auth status` clean), and/or `az` for the ADO PR leg.
- `conductor` is on PATH and the polyphony registry is registered:
  `conductor registry add polyphony PolyphonyRequiem/polyphony` (or a local
  path).
- The target repo has `.polyphony-config/process-config.yaml` and
  `polyphony validate-config --config .polyphony-config` exits 0.

### Minimum invocation

The only required input is the apex (run-root) work-item id; `platform`
defaults to `ado` and `intent` defaults to `new`:

```powershell
conductor run apex-driver@polyphony --input apex_id=<ID> --web
```

### Full invocation (all inputs explicit)

```powershell
conductor run apex-driver@polyphony `
  --input apex_id=<ID> `
  --input intent=new `
  --input platform=ado `
  --input organization=<org> `
  --input project=<project> `
  --input repository=<repo> `
  -m tracker=ado `
  -m project_url=https://dev.azure.com/<org>/<project> `
  -m git_repo=<absolute repo path> `
  -m workitem_id=<ID> `
  -m worktree_name=<repo>-<ID> `
  -m cwd=<absolute worktree path> `
  --web
```

Per the polyphony-sdlc skill's *Workflow Metadata* section, the `-m`
metadata block is what the dashboard, observation filer, and close-out
skills consume. `tracker=ado` is currently the only supported value.

### Outcomes

The driver terminates in one of three observable states; the workflow's
`output:` map carries `apex_id`, `satisfied`, `abandoned`, `preflight_failed`,
and `renegotiation_pending`.

**Satisfied** — apex root reports `satisfied` and the EdgeGraph reports no
remaining work. Re-running with `--input intent=resume` is a no-op (the
worklist is empty); the run is closed-out via `close-out.yaml`.

```powershell
conductor run apex-driver@polyphony --input apex_id=2930 --web
# → apex_id=2930, satisfied=true, abandoned=false, renegotiation_pending=false
```

**Abandoned** — operator chose `abort` at one of the apex-level human gates
(preflight failure, conflict resolution, renegotiation, wave conflict).
The apex feature branch and per-item branches are left in place for forensic
inspection. Re-enter with `--input intent=resume` after triaging.

```powershell
conductor run apex-driver@polyphony --input apex_id=2930 --input intent=resume --web
# → apex_id=2930, satisfied=false, abandoned=true
```

**Renegotiation pending** — a child plan-level invocation reported
`renegotiation_pending: true`. The driver consults
`policy.renegotiation.auto_decide` (`prompt` / `auto_restart` / `ignore`) in
`.polyphony-config/policy.yaml` and surfaces `renegotiation_gate`. The workflow's
output carries `renegotiation_pending=true`; the operator either accepts
the renegotiation (loop continues with the mutated tree) or aborts.

```powershell
conductor run apex-driver@polyphony --input apex_id=2930 --input intent=resume --web
# → apex_id=2930, satisfied=false, abandoned=false, renegotiation_pending=true
```

### Per-leg invocations (replay / override)

`apex-driver` re-derives the right leg per item per wave from observable
state, so most users should not invoke a leg directly. Reach for a per-leg
invocation only when you want to *replay* or *override* a single leg of an
in-flight apex:

```powershell
conductor run plan-level@polyphony           --input work_item_id=<ID> --web
conductor run actionable@polyphony           --input work_item_id=<ID> --web
conductor run implement-merge-group@polyphony         --input work_item_id=<ID> --web
conductor run feature-pr@polyphony           --input work_item_id=<ID> --web
```

Each sub-workflow declares its own `--input` shape; consult the YAML in
`.conductor/registry/workflows/` for the contract. The standard `-m`
metadata block applies regardless of which leg is invoked.



In priority order:

| `polyphony state next-ready` | Route                |
|------------------------------|----------------------|
| status=satisfied or empty    | `fast-path`          |
| status=monitoring            | `monitoring`         |
| status=blocked               | `blocked`            |
| status=error                 | `error`              |
| dispatchable + plan_*        | `plan-level`         |
| dispatchable + action_*      | `actionable`         |
| dispatchable + impl_* + root | `feature-pr`         |
| dispatchable + impl_* + ¬root| `implement-merge-group`       |

Plan > action > impl handles multi-facet items (action evidence
must land before implementation per the implicit edge
`action_satisfied → implementation_merged`).

## Integration discipline

- One per-item branch per work item per apex run.
- `--no-ff` by default (auditability over linear history).
- Topological order from `polyphony edges check`; falls back to wave
  input order when no explicit topological order is available
  (the worklist already returns waves in topological order).
- Conflicts abort the *single* merge, not the *wave*. The wave
  continues; conflicts roll up to a wave-level human gate.
- Branches that don't exist locally are recorded in a `skipped[]`
  list (reason: `branch_not_found`) — happens when a per-item
  dispatch routed to `fast-path` or `monitoring` and never created
  a branch.

## Deferred items

The MVP wires the **dispatch skeleton** end-to-end: build worklist,
loop over waves, classify each item, spawn worktree, tear down
worktree, integrate wave, gate on conflicts, close on satisfaction.
The actual *lifecycle dispatch* — invoking `plan-level.yaml`,
`actionable.yaml`, `implement-merge-group.yaml`, `feature-pr.yaml` from inside
`apex-item-dispatch.yaml` — was deferred to a follow-up PR, behind
a `lifecycle_dispatch_placeholder` step. **The follow-up has now
landed; see "Lifecycle dispatch wiring (Phase 7 follow-up)" below.**

## Lifecycle dispatch wiring (Phase 7 follow-up)

The deferred lifecycle dispatch is now wired. The placeholder
in `apex-item-dispatch.yaml` is replaced with four typed `workflow:`
nodes — `plan_level_dispatch`, `actionable_dispatch`,
`implement_merge_group_dispatch`, `feature_pr_dispatch` — each fanned to from
`spawn_worktree` via a `when:` clause matching the lifecycle router's
`route` field.

**Pattern: branch-on-router-into-sub-workflow.** Conductor does not
support templated `workflow:` paths. The canonical workaround,
already proven by `feature-pr.yaml`'s platform router → `pr_lifecycle_github` /
`pr_lifecycle_ado`, is to enumerate one `type: workflow` node per
route value, each with explicit `input_mapping`, all converging on a
common downstream node. apex-item-dispatch follows this pattern
exactly: four lifecycle nodes (and three terminal nodes —
`terminal_fast_path`, `terminal_monitoring`, `terminal_blocked`) all
route to `teardown_worktree`.

**Multi-facet sequencing is implicit, not explicit.** A previous
design sketch (and the original task description) imagined a
`facet_sequence: [plan-level, actionable, ...]` array carrying the
ordered facets to dispatch within one item-dispatch invocation.
We instead rely on the existing `polyphony state next-ready`
contract: each `apex-item-dispatch` invocation handles ONE facet,
and the next worklist rebuild picks up the next facet (if any) in
its own wave. This keeps the apex-item-dispatch surface area small
and avoids duplicating dispatch logic that already lives in the
worklist builder. Trade-off: an item with three facets takes three
trips through the dispatch loop instead of one — acceptable, since
each trip is cheap and the worktree spawn/teardown already happens
per facet anyway.

**Renegotiation bubble-up is now wired end-to-end.**

| Layer | Source | Carrier |
|---|---|---|
| Plan-level | `plan-level.yaml` output `renegotiation_pending` | (declared by plan-level; consumed optimistically with `is defined`) |
| Per item | `apex-item-dispatch.yaml` output map | `plan_level_dispatch.output.renegotiation_pending` |
| Per wave | `apex-wave-dispatch.yaml` `aggregate_renegotiation` script step | scans `dispatch_items.outputs` and emits `renegotiation_pending` + `renegotiation_items[]` |
| Per apex | `apex-driver.yaml` `renegotiation_summary` script step | scans `wave_dispatch_loop.outputs` and emits `renegotiation_pending` + summary |
| Gate | `apex-driver.yaml` `renegotiation_gate` (human_gate) | fires when `renegotiation_pending=true`; consults `policy.renegotiation.auto_decide` |

`policy.renegotiation.auto_decide` is honored only for the default
`prompt` mode. `auto_restart` and `ignore` are documented MVP stubs
that currently fall through to `prompt`; full handling (loop
restart vs continue) is deferred to a future PR.

**implement-merge-group input mapping is the MVP shape.** `implement-merge-group.yaml`
expects `pg_number`, `work_item_ids`, `branch_name`,
`feature_branch`. apex-item-dispatch synthesizes these from
`work_item_id` + `apex_id` (one item per merge group, branch name derived
from apex+item IDs). Richer mappings — multi-item merge groups,
planner-declared branch names — require apex-driver to surface merge-group
grouping and are deferred.

**Still deferred after this PR:**
- End-to-end test of the full dispatch loop with all four lifecycle
  workflows live (smoke test only — no fixture suite).
- `auto_restart` / `ignore` renegotiation policy modes (currently
  treated as `prompt`).
- Planner-declared executor for actionable items (currently relies
  on `route-actionable-executor.ps1` heuristics inside
  `actionable.yaml`).
- Richer implement-merge-group input mapping (multi-item PGs,
  planner-declared branch names).

## Forward references

- `polyphony state next-ready` — observable-state classifier consumed
  by `lifecycle-router.ps1`.
- `polyphony worklist build` — wave generator consumed by
  `apex-driver.yaml`.
- `polyphony edges check` — topological order generator consumed by
  `wave-integrator.ps1`.
- `policy.renegotiation` — renegotiation policy block (introduced
  alongside this driver in `.polyphony-config/policy.yaml`).
- ADR `scope-renegotiation.md` — broader renegotiation contract this
  driver hooks into.
- ADR `actionable-executor-split.md` — the prior application of the
  "deterministic classifier script + trivial YAML route" pattern.
