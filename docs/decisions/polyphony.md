---
doc_type: decision
status: accepted
synopsis: Root driver dispatches lifecycle work via tree-walking with per-item worktree isolation; replaces the deleted single-shot polyphony-full.yaml.
supersedes: [polyphony-full.yaml]
---

# Root Driver — Tree-Walking Dispatch with Per-Item Worktree Isolation

> **Status:** Accepted. Phase 7 — polyphony MVP.
> **Driver:** Phase 7 needs an SDLC orchestrator that walks a root
> tree level by level, dispatches lifecycle work per item with
> isolation, and re-enters cleanly after human gates or interruptions.
> The deleted `polyphony-full.yaml` was a single-shot pipeline; the
> polyphony is a *driver* — it loops over EdgeGraph waves until the
> root reports `satisfied` (or the loop is abandoned at a gate).
> **Supersedes:** the deleted `polyphony-full.yaml`.

## Context

The root tree is a typed forest of work items rooted at an "root" — a
root work item whose satisfaction defines completion of an SDLC unit.
Each non-root item carries one or more facets (plannable, actionable,
implementable). Facets become satisfied at different times and through
different machinery, and items have ordering edges between them
(`children_seeded → ...`, `action_satisfied → implementation_merged`,
etc.) that the EdgeGraph computes into **waves** — sets of items that
can be dispatched in parallel within a batch, with strict serialization
between waves.

The dispatch contract is:

1. Build a worklist for the root (`polyphony worklist build`).
2. For each batch (in topological order):
   - For each item in the batch (in parallel up to a cap):
     - Classify what *kind* of dispatch the item needs *right now*
       based on its observable state (`polyphony state next-ready`).
     - Spawn an isolated worktree.
     - Run the appropriate lifecycle workflow (plan-level / actionable
       / implement-merge-group / feature-pr).
     - Tear down the worktree.
   - Integrate the batch (merge per-item branches into the root feature
     branch in topological order).
3. Re-evaluate the worklist (waves can change as items satisfy or
   renegotiation fires) and loop.
4. When the root reports `satisfied` and the EdgeGraph reports
   no remaining work, mark the root satisfied and exit.

Several open design questions had to be settled to ship this:

- **Q1.** Does the dispatch loop live in *one* workflow or fan out to
  sub-workflows?
- **Q2.** How does per-item lifecycle classification happen — inside
  the workflow YAML, or in a script step?
- **Q3.** How is each item isolated from siblings dispatched in
  parallel within the same batch?
- **Q4.** How does the driver re-enter after a human gate, a
  renegotiation, or an interrupted run?

## Decision

### Q1 — Three-file split, not one

Conductor's `for_each` agent invokes exactly **one** thing per
iteration. Per-batch handling needs to do *two* things — fan out to N
item dispatches (for_each), then run the batch integrator (script).
That requires more than one step per batch, so batch handling must live
in a sub-workflow.

The same logic recurses one level lower: per-item handling needs to
classify, spawn a worktree, run a lifecycle workflow, tear down the
worktree. That's also multiple steps, also a sub-workflow.

We ship **three workflows**:

- `polyphony.yaml` — outer dispatch loop (`build_worklist` →
  `batch_dispatch_loop` → `root_completion_gate` → loop or close).
- `root-batch-dispatch.yaml` — per-batch fan-out (`dispatch_items`
  for_each → `integrate_batch` script).
- `root-item-dispatch.yaml` — per-item pipeline (`classify_lifecycle`
  → `spawn_worktree` → `lifecycle` → `teardown_worktree`).

### Q2 — Lifecycle classification is a script

The classification rule set ("if the item's next-ready signal is
`plan_authored`, route to plan-level; if it's `action_satisfied`,
route to actionable; …") has to consult `polyphony state next-ready`
output — a JSON envelope with a `status`, `kind`, `signal`, and a
flag for whether the item is the root. That logic is too much
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

Each item dispatched in parallel within the same batch gets its own
worktree at `<repo-parent>/<repo-name>-item-<work_item_id>` on a
branch named `sdlc/root/<work_item_id>`. Branches fork from the root
feature branch (`feature/<root_id>`), not from `main`, so the
per-item branch already contains the integrated work of prior waves.

`worktree-manager.ps1` handles spawn (`git worktree add -b …`) and
teardown (`git worktree remove --force`) idempotently: spawning is a
no-op when the worktree exists; teardown is a no-op when it doesn't.
This is what makes re-entry safe.

After each batch's items complete, `batch-integrator.ps1` merges the
per-item branches into `feature/<root_id>` in **topological
order** (read from `polyphony edges check`). Default merge strategy
is `--no-ff` — every per-item merge produces an explicit merge
commit, so the root feature branch keeps an auditable record of
which work item contributed which change. Conflicts are captured
(files, branches involved), the merge is aborted (`git merge
--abort`), and the batch continues; conflicts are reported up to a
human gate (`batch_conflict_gate`).

### Q4 — Observable-state re-entry

The driver's loop variable is **the worklist itself** — recomputed
on every iteration via `polyphony worklist build`. The driver does
not carry a "step counter" or "last completed item" pointer. After a
gate or restart, the driver re-builds the worklist, the EdgeGraph
re-classifies what's still pending, and the next batch is whatever's
ready *now*. Items already satisfied drop out of the worklist on
their own.

This is what the **renegotiation** policy hooks into: when a child
plan-level invocation reports `renegotiation_pending: true` (a
"bubble-up" signal), polyphony consults `policy.renegotiation
.auto_decide`. With `prompt`, it routes to the renegotiation human
gate. With `auto_restart`, it restarts the dispatch loop without a
gate. With `ignore`, it continues. In all three cases the next-loop
worklist build picks up the (possibly mutated) tree state — no
explicit "rewind" mechanism needed.

## Invocation

`polyphony@polyphony` is the canonical SDLC entry point. The CLI does not
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

The only required input is the root work-item id; `platform`
defaults to `ado` and `intent` defaults to `new`:

```powershell
conductor run polyphony@polyphony --input root_id=<ID> --web
```

### Full invocation (all inputs explicit)

```powershell
conductor run polyphony@polyphony `
  --input root_id=<ID> `
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
`output:` map carries `root_id`, `satisfied`, `abandoned`, `preflight_failed`,
and `renegotiation_pending`.

**Satisfied** — root reports `satisfied` and the EdgeGraph reports no
remaining work. Re-running with `--input intent=resume` is a no-op (the
worklist is empty); the run is closed-out via `close-out.yaml`.

```powershell
conductor run polyphony@polyphony --input root_id=2930 --web
# → root_id=2930, satisfied=true, abandoned=false, renegotiation_pending=false
```

**Abandoned** — operator chose `abort` at one of the root-level human gates
(preflight failure, conflict resolution, renegotiation, batch conflict).
The root feature branch and per-item branches are left in place for forensic
inspection. Re-enter with `--input intent=resume` after triaging.

```powershell
conductor run polyphony@polyphony --input root_id=2930 --input intent=resume --web
# → root_id=2930, satisfied=false, abandoned=true
```

**Renegotiation pending** — a child plan-level invocation reported
`renegotiation_pending: true`. The driver consults
`policy.renegotiation.auto_decide` (`prompt` / `auto_restart` / `ignore`) in
`.polyphony-config/policy.yaml` and surfaces `renegotiation_gate`. The workflow's
output carries `renegotiation_pending=true`; the operator either accepts
the renegotiation (loop continues with the mutated tree) or aborts.

```powershell
conductor run polyphony@polyphony --input root_id=2930 --input intent=resume --web
# → root_id=2930, satisfied=false, abandoned=false, renegotiation_pending=true
```

### Per-leg invocations (replay / override)

`polyphony` re-derives the right leg per item per batch from observable
state, so most users should not invoke a leg directly. Reach for a per-leg
invocation only when you want to *replay* or *override* a single leg of an
in-flight root:

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

- One per-item branch per work item per root run.
- `--no-ff` by default (auditability over linear history).
- Topological order from `polyphony edges check`; falls back to batch
  input order when no explicit topological order is available
  (the worklist already returns waves in topological order).
- Conflicts abort the *single* merge, not the *batch*. The batch
  continues; conflicts roll up to a batch-level human gate.
- Branches that don't exist locally are recorded in a `skipped[]`
  list (reason: `branch_not_found`) — happens when a per-item
  dispatch routed to `fast-path` or `monitoring` and never created
  a branch.

## Deferred items

The MVP wires the **dispatch skeleton** end-to-end: build worklist,
loop over waves, classify each item, spawn worktree, tear down
worktree, integrate batch, gate on conflicts, close on satisfaction.
The actual *lifecycle dispatch* — invoking `plan-level.yaml`,
`actionable.yaml`, `implement-merge-group.yaml`, `feature-pr.yaml` from inside
`root-item-dispatch.yaml` — was deferred to a follow-up PR, behind
a `lifecycle_dispatch_placeholder` step. **The follow-up has now
landed; see "Lifecycle dispatch wiring (Phase 7 follow-up)" below.**

## Lifecycle dispatch wiring (Phase 7 follow-up)

The deferred lifecycle dispatch is now wired. The placeholder
in `root-item-dispatch.yaml` is replaced with four typed `workflow:`
nodes — `plan_level`, `actionable`,
`implement_merge_group`, `feature_pr` — each fanned to from
`spawn_worktree` via a `when:` clause matching the lifecycle router's
`route` field.

**Pattern: branch-on-router-into-sub-workflow.** Conductor does not
support templated `workflow:` paths. The canonical workaround,
already proven by `feature-pr.yaml`'s platform router → `pr_lifecycle_github` /
`pr_lifecycle_ado`, is to enumerate one `type: workflow` node per
route value, each with explicit `input_mapping`, all converging on a
common downstream node. root-item-dispatch follows this pattern
exactly: four lifecycle nodes (and three terminal nodes —
`fast_path`, `monitoring`, `blocked`) all
route to `teardown_worktree`.

**Multi-facet sequencing is implicit, not explicit.** A previous
design sketch (and the original task description) imagined a
`facet_sequence: [plan-level, actionable, ...]` array carrying the
ordered facets to dispatch within one item-dispatch invocation.
We instead rely on the existing `polyphony state next-ready`
contract: each `root-item-dispatch` invocation handles ONE facet,
and the next worklist rebuild picks up the next facet (if any) in
its own batch. This keeps the root-item-dispatch surface area small
and avoids duplicating dispatch logic that already lives in the
worklist builder. Trade-off: an item with three facets takes three
trips through the dispatch loop instead of one — acceptable, since
each trip is cheap and the worktree spawn/teardown already happens
per facet anyway.

**Renegotiation bubble-up is now wired end-to-end.**

| Layer | Source | Carrier |
|---|---|---|
| Plan-level | `plan-level.yaml` output `renegotiation_pending` | (declared by plan-level; consumed optimistically with `is defined`) |
| Per item | `root-item-dispatch.yaml` output map | `plan_level.output.renegotiation_pending` |
| Per batch | `root-batch-dispatch.yaml` `aggregate_renegotiation` script step | scans `dispatch_items.outputs` and emits `renegotiation_pending` + `renegotiation_items[]` |
| Per root | `polyphony.yaml` `renegotiation_summary` script step | scans `batch_dispatch_loop.outputs` and emits `renegotiation_pending` + summary |
| Gate | `polyphony.yaml` `renegotiation_gate` (human_gate) | fires when `renegotiation_pending=true`; consults `policy.renegotiation.auto_decide` |

`policy.renegotiation.auto_decide` is honored only for the default
`prompt` mode. `auto_restart` and `ignore` are documented MVP stubs
that currently fall through to `prompt`; full handling (loop
restart vs continue) is deferred to a future PR.

**implement-merge-group input mapping is the MVP shape.** `implement-merge-group.yaml`
expects `pg_number`, `work_item_ids`, `branch_name`,
`feature_branch`. root-item-dispatch synthesizes these from
`work_item_id` + `root_id` (one item per merge group, branch name derived
from root+item IDs). Richer mappings — multi-item merge groups,
planner-declared branch names — require polyphony to surface merge-group
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
- `polyphony worklist build` — batch generator consumed by
  `polyphony.yaml`.
- `polyphony edges check` — topological order generator consumed by
  `batch-integrator.ps1`.
- `policy.renegotiation` — renegotiation policy block (introduced
  alongside this driver in `.polyphony-config/policy.yaml`).
- ADR `scope-renegotiation.md` — broader renegotiation contract this
  driver hooks into.
- ADR `actionable-executor-split.md` — the prior application of the
  "deterministic classifier script + trivial YAML route" pattern.
