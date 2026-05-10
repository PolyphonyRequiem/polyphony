---
name: polyphony-sdlc
description: >-
  Type-agnostic SDLC vocabulary and sub-workflow library documentation.
  Covers the polyphony engine vocabulary (facets, requirements, dispositions,
  EdgeGraph, waves), the routing-style verb envelope contract, and the
  sub-workflow building blocks (recursive planning, parallel PG execution,
  feature PR remediation, platform abstraction). Reference when invoking,
  debugging, or composing the polyphony SDLC pipeline.
user-invokable: false
---

# Polyphony SDLC Engine + Sub-Workflow Library

Type-agnostic SDLC vocabulary and a library of composable sub-workflows
powered by the polyphony engine and the `conductor` orchestrator. Accepts
**any work item type at any hierarchy level** — facet vocabulary
(plannable, implementable, actionable, decomposable) and per-item
requirement state drive every routing decision; no work item type names
appear in any YAML routing condition.

> **Apex driver status (Phase 7, shipped).** `apex-driver.yaml` is the
> canonical SDLC entry point: a tree-walking orchestrator that builds a
> worklist for an apex tree, dispatches each wave's items in parallel
> through `apex-wave-dispatch.yaml` → `apex-item-dispatch.yaml`,
> integrates each wave by merging per-item branches into the apex
> feature branch, and re-evaluates the worklist until the apex root
> reports `satisfied`. Per-item lifecycle dispatch (plan-level /
> actionable / implement-merge-group / feature-pr) is now wired end-to-end as of
> the Phase 7 follow-up. See "Invocation" below for the canonical
> command line and the "Apex driver invocation" section further down
> for the full input contract and re-entry semantics.

## Invocation

The canonical SDLC entry point is **`apex-driver@polyphony`**. The only
required input is `apex_id` — the apex (run-root) work-item id:

```powershell
conductor run apex-driver@polyphony --input apex_id=<ID> --web
```

Full invocation with all inputs explicit and the standard `-m` metadata
block (see *Workflow Metadata* below):

```powershell
Start-Process -WindowStyle Hidden -FilePath conductor -ArgumentList @(
  "run", "apex-driver@polyphony",
  "--input", "apex_id=<ID>",
  "--input", "intent=new",            # new | resume | replan
  "--input", "platform=ado",
  "--input", "organization=<org>",
  "--input", "project=<project>",
  "--input", "repository=<repo>",
  "-m", "tracker=ado",
  "-m", "project_url=https://dev.azure.com/<org>/<project>",
  "-m", "git_repo=<absolute repo path>",
  "-m", "workitem_id=<ID>",
  "-m", "worktree_name=<repo>-<ID>",
  "-m", "cwd=<absolute worktree path>",
  "--web"
)
```

Sub-workflows (`plan-level`, `actionable`, `implement-merge-group`, `feature-pr`,
…) can be invoked directly to replay or override a single leg, but the
apex-driver re-derives the right leg per item from observable state, so
direct sub-workflow invocations should be rare. See the "Apex driver
invocation" section further down for re-entry semantics, the renegotiation
flow, and the dispatched script set, and the ADR
[`docs/decisions/apex-driver.md`](../../../docs/decisions/apex-driver.md)
for full rationale and per-outcome examples.

## Workflow Metadata

When a sub-workflow in this library is invoked, metadata is passed
dynamically via `--metadata` / `-m` flags. **`tracker=ado` is required**
so downstream tooling (dashboard, observation filer, post-mortem
skills) knows which provider's APIs and URL conventions to use.

| Field | Example | Description |
|-------|---------|-------------|
| `tracker` | `ado` | Work item tracking provider (currently the only supported value) |
| `project_url` | `https://dev.azure.com/dangreen-msft/Twig` | ADO organization + project URL — used to render work-item links in the dashboard and to construct ADO REST URLs in close-out reports |
| `git_repo` | `C:\Users\dangreen\projects\polyphony` | Originating git repo path (the source-of-truth checkout, NOT the worktree) |
| `workitem_id` | `2930` | Target work item ID (numeric, mirror of `--input work_item_id`) |
| `worktree_name` | `polyphony-2930` | Git worktree directory name (`<repo>-<id>` convention) |
| `cwd` | `C:\Users\dangreen\projects\polyphony-2930` | Worktree working directory — where conductor is launched from |

> **All values must be resolved** — templates with `{braces}` are skipped by the dashboard.

> **ADO provider note:** Polyphony is currently ADO-only on the work-item side
> (`twig` is the ADO CLI). GitHub is used only for code/PR hosting via the
> `github-pr.yaml` sub-workflow. The `tracker=ado` metadata reflects the
> work-item provider and is independent of the PR platform routing inside
> `implement-merge-group.yaml` (`pr_platform_router`).

## Per-Requirement State (replaces phase strings)

The engine no longer surfaces a coarse "phase" string. Instead, each
work item carries a derived **`RequirementSet`** — a list of
`(kind, disposition)` requirements plus the within-item edges that
gate them. Drivers and workflows route on per-requirement readiness:

```
polyphony state next-ready --work-item N
  → status: needed | ready | fulfilling | satisfied | empty
  → requirements: [ { kind, disposition }, … ]
  → next:        [ { kind, disposition }, … ]   # the ready set
```

`status` collapses the per-requirement view into a single routing
hint, with `"empty"` reserved for pure-container items (the only
requirement is `item_satisfied`, still `Needed` pending cross-item
rollup — see "Engine Vocabulary" below). For the cross-item picture
spanning multiple items, callers build an `EdgeGraph` and consume
`ToWaves()` for topological dispatch.

## Recursion Depth Budget (C3)

The conductor engine enforces a maximum depth of 10. Sub-workflows in
this library compose to depths well under that ceiling — the deepest
chain today bottoms out around depth 5 (driver → `plan-level.yaml`
self-recursion → `github-pr.yaml`):

```
Depth 0: <driver / direct invocation>
Depth 1: plan-level.yaml                      (recursive planning core)
Depth 2-N: plan-level.yaml self-recursion     (nested plannable levels)
Depth N+1: github-pr.yaml / ado-pr.yaml       (PR lifecycle — leaf)
            OR implement-merge-group.yaml               (PG lifecycle)
```

The `depth_guard` script in `plan-level.yaml` validates `depth < max_depth`
(default `max_depth=6`). If the limit is reached, a `depth_exceeded_gate`
human gate fires so the user can approve going deeper or abort.

## Sub-Workflow Library

The sub-workflows below are the composable building blocks the
(future) apex driver will dispatch into; several can also be invoked
directly with their own `--input` shape.

### `plan-level.yaml` — Recursive Planning Core

**Responsibility:** Plans a single hierarchy level. Loads type-specific context from
`.conductor/work-item-types/`, runs the architect agent with parallel review pipeline,
and seeds children. Self-recurses for nested plannable levels via `for_each`.

| Agent | Type | Description |
|-------|------|-------------|
| `depth_guard` | script | Validate recursion depth against max_depth |
| `depth_exceeded_gate` | human_gate | Approve deeper recursion or abort |
| `type_loader` | script | Load type definition + template from work-item-types |
| `type_loader_error_gate` | human_gate | Handle type loading failures |
| `route_check` | script | Validate Polyphony route for this work item |
| `architect` | agent (Opus 1M) | Design implementation plan with PR groupings |
| `open_questions_policy` | script | Resolve open_questions policy domain for routing |
| `open_questions_counter` | script | Track answer loop iteration count |
| `open_questions_gate` | human_gate | Surface blocking open questions to user |
| `open_questions_answer_counter` | script | Increment loop counter on answer route |
| `review_group` | parallel | Runs `technical_reviewer` and `readability_reviewer` concurrently |
| `technical_reviewer` | agent (Opus 1M) | Technical accuracy review (must score ≥ 90) |
| `readability_reviewer` | agent (Sonnet) | Clarity and structure review (must score ≥ 90) |
| `review_router` | script | Check scores; loop to architect or proceed |
| `plan_approval` | human_gate | Approve / revise / reject the plan |
| `seeder` | agent (Sonnet) | Seed child work items via twig CLI |
| `child_router` | script | Discover plannable children for recursion |
| `plan_children_group` | for_each (workflow) | Self-recurse into plan-level.yaml per child |
| `plan_children_summary_gate` | human_gate | Surface partial failures in child planning |

---

### `implement-merge-group.yaml` — Single PG Lifecycle

**Responsibility:** Implements all tasks in a single Processing Group, creates and merges
a PG-level PR, and closes completed work items in ADO after merge.

| Agent | Type | Description |
|-------|------|-------------|
| `pg_router` | script | Determine current PG action via `pg-router.ps1` |
| `branch_manager` | script | Create/checkout PG branch |
| `primary_router` | script | Route to next implementable child via `polyphony branch next-impl` |
| `coder` | agent (Opus 1M) | Implement a single task with incremental commits |
| `primary_reviewer` | agent (Opus) | Per-task quality gate with re-review awareness |
| `primary_reviewer` | agent (Sonnet) | Per-child quality gate |
| `primary_completer` | script | Mark child as done, loop to `primary_router` |
| `dependency_check` | script | Check ADO predecessor links via `dependency-check.ps1` |
| `dependency_gate` | human_gate | Surface blocked dependencies to user |
| `scope_reviewer` | agent (Opus) | Per-issue acceptance criteria and integration check |
| `scope_reviewer` | agent (Opus 1M) | Per-scope acceptance criteria and integration check |
| `user_acceptance` | human_gate | Conditional per-issue user acceptance |
| `pr_submit` | agent (Sonnet) | Validate build + tests, create PR via `gh pr create` |
| `pr_platform_router` | script | Route to `github-pr.yaml` or `ado-pr.yaml` |
| `pr_lifecycle_github` | workflow | Delegates to `github-pr.yaml` |
| `pr_lifecycle_ado` | workflow | Delegates to `ado-pr.yaml` |
| `scope_closer` | script | Transition completed items to terminal state via `scope-closer.ps1` |

---

### `github-pr.yaml` — GitHub PR Lifecycle

**Responsibility:** Platform-specific PR lifecycle for GitHub. Reviews the PR with an
Opus 1M reviewer, fixes issues via a Sonnet fixer in a loop (max 10 iterations per P7),
and merges when approved.

| Agent | Type | Description |
|-------|------|-------------|
| `pr_reviewer` | agent (Opus 1M) | Thorough code review with line-level comments |
| `review_counter` | script | Track fix iteration count (max 10) |
| `pr_fixer` | agent (Sonnet) | Address review feedback with targeted fixes |
| `pr_fix_exhausted_gate` | human_gate | Escalate when fix loop cap reached |
| `review_counter_reset` | script | Reset counter after human intervention |
| `pr_merger` | agent (Sonnet) | Merge approved PR and clean up branch |

---

### `ado-pr.yaml` — ADO PR Lifecycle

**Responsibility:** Azure DevOps PR review/poll/merge cycle. Mirrors
`github-pr.yaml`'s contract via `polyphony pr poll-status-ado` and
`pr merge-feature-ado`, with human-gate fallbacks for the pending
state and a stuck-review timeout (PR #148) that escalates after
60 fruitless polls.

| Agent | Type | Description |
|-------|------|-------------|
| `ado_pr_validator` | script | Validate ADO PR inputs (organization/project/repository/PR number) |
| `ado_pr_status_check` | script | `polyphony pr poll-status-ado` — read reviewer vote + state |
| `ado_pr_pending_gate` | human_gate | Re-poll / manual-verify-merge / trigger remediation / abort |
| `ado_pr_changes_requested_gate` | human_gate | Re-poll / trigger remediation / abort |
| `ado_pr_pending_poll_counter` | script | Per-PR pending-poll counter; routes to `ado_stuck_review_gate` at the cap |
| `ado_stuck_review_gate` | human_gate | `continue_waiting` / `override_approved` / `abort` |
| `ado_pr_merger` | script | `polyphony pr merge-feature-ado` — complete the PR |

---

### `feature-pr.yaml` — Feature PR and Remediation

**Responsibility:** Creates a feature PR (all PGs merged → feature branch → target
branch), reviews it, and runs remediation cycles when the reviewer requests changes.
Capped at **3 remediation cycles**; after that, a human gate fires for escalation.

| Agent | Type | Description |
|-------|------|-------------|
| `feature_pr_creator` | script | (GitHub) Create feature PR via `polyphony pr create-feature-pr` |
| `feature_pr_creator_ado` | script | (ADO) Create feature PR via `polyphony pr create-feature-ado` |
| `feature_pr_creator_failed_gate_ado` | human_gate | Operator gate when ADO PR creation fails |
| `pr_platform_router` | script | Route to `github-pr.yaml` or `ado-pr.yaml` based on `platform` input |
| `pr_lifecycle_github` | workflow | Delegates to `github-pr.yaml` for review/fix/merge |
| `pr_lifecycle_ado` | workflow | Delegates to `ado-pr.yaml` for review/fix/merge (full lifecycle as of v1.2.0) |
| `remediation_counter` | script | Track remediation cycle count (max 3) |
| `remediation_cap_gate` | human_gate | Escalate when 3 remediation cycles exceeded |
| `remediation_abort` | script | Handle abort from cap gate |
| `remediation_planner` | agent (Opus 1M) | Plan fixes for reviewer feedback as an addendum (platform-aware: `gh` on GitHub, `pr poll-status-ado` on ADO) |
| `remediation_seeder` | agent (Sonnet) | Seed new remediation PG from the addendum plan |
| `remediation_implementer` | workflow | Delegates to `implement-mg.yaml` for the remediation MG |
| `feature_pr_updater` | agent (Sonnet) | Generate the re-review comment after a remediation cycle (platform-aware: posts directly via `gh` on GitHub; emits `comment_body` on ADO) |
| `feature_pr_updater_poster_ado` | script | (ADO) Post the updater's `comment_body` via `polyphony pr post-comment-ado` |

> **Platform abstraction parity:** Like `implement-mg.yaml`, the feature PR lifecycle
> goes through `pr_platform_router` → `pr_lifecycle_github` / `pr_lifecycle_ado`. The
> review/fix/merge logic lives in `github-pr.yaml` and `ado-pr.yaml` — not duplicated
> in `feature-pr.yaml`. As of v1.2.0 both legs run the same remediation chain
> (planner → seeder → implementer → updater → router); the ADO leg additionally
> uses `feature_pr_updater_poster_ado` to post the re-review comment via
> `polyphony pr post-comment-ado` (the **dual-poster pattern**, mirroring
> `plan-level.yaml`'s `plan_reviewer` + `plan_reviewer_poster_ado`).
> See `docs/decisions/ado-feature-pr-parity.md`.

**Remediation cycle flow:**

```
feature_pr_creator → pr_platform_router → pr_lifecycle_github (or _ado)
                                                    │
                                                    ├── merged=true ──→ $end
                                                    │
                                                    └── merged=false ──→ remediation_counter
                                                                              │
                                                                              ├── under limit ──→ remediation_planner
                                                                              │                       → remediation_seeder
                                                                              │                       → remediation_implementer
                                                                              │                       → feature_pr_updater
                                                                              │                       → pr_platform_router (loop)
                                                                              │
                                                                              └── cap reached ──→ remediation_cap_gate
                                                                                                       ├── retry → remediation_planner
                                                                                                       └── abort → $end
```

---

### `close-out.yaml` — Close-Out and Observation Filing

**Responsibility:** Post-implementation close-out. Generates structured observations
from the completed work item hierarchy and files them as new ADO work items using
filing-eligible types from `process-config.yaml`.

| Agent | Type | Description |
|-------|------|-------------|
| `close_out` | agent (Opus 1M) | Post-mortem analysis: what went well, what can improve |
| `closeout_filer` | agent (Sonnet) | File observations as tagged ADO work items |

## PR Group (PG) Lifecycle

PGs are the unit of parallel work in the implementation phase. Each PG:

1. **Branch creation** — `branch_manager` creates a PG branch (e.g., `feature/1234-pg-1`)
   targeting the feature branch
2. **Implementation loop** — `primary_router` discovers the next implementable child; `coder` implements
   it; `primary_reviewer` gates quality; loop until all children in the PG are complete
3. **Scope review** — `scope_reviewer` checks
   acceptance criteria
4. **PR creation** — `pr_submit` validates build/tests and creates a PR
5. **PR lifecycle** — routed to `github-pr.yaml` or `ado-pr.yaml` for review/fix/merge
6. **Scope close** — `scope_closer` transitions completed items to Done in ADO

Multiple PGs execute in parallel via `for_each` with `max_concurrent: 3` and
`failure_mode: fail_fast`.

## Feature PR and Remediation Cycle

After all PGs merge into the feature branch, `feature-pr.yaml` creates a **feature PR**
targeting the main branch. The feature PR review is more holistic than PG-level reviews —
it checks cross-PG integration, overall architecture, and acceptance criteria.

If the reviewer requests changes:

1. `remediation_counter` increments the cycle count
2. `remediation_planner` (Opus 1M) creates an addendum plan addressing the feedback
3. `remediation_seeder` creates a new remediation PG with tasks from the addendum
4. `remediation_implementer` runs the full `implement-merge-group.yaml` lifecycle for the new PG
5. The feature PR is re-reviewed

This cycle repeats up to **3 times**. If the cap is exceeded, a `remediation_cap_gate`
human gate fires for escalation — the user can choose to continue (override the cap) or
abort the workflow.

## Platform Abstraction (GitHub vs ADO)

The workflow abstracts PR operations behind a platform-specific sub-workflow interface:

| Platform | Sub-Workflow | Status | Review Model |
|----------|-------------|--------|-------------|
| `github` | `github-pr.yaml` | Full implementation | Opus 1M reviewer + Sonnet fixer (max 10 loops) |
| `ado` | `ado-pr.yaml` | Stub (human gate) | Manual PR management via human gate |

Both sub-workflows share the same **interface contract**:

- **Inputs:** `pr_number`, `branch_name`, `target_branch`, `review_policy`
- **Outputs:** `merged` (boolean), `pr_url` (string)

The `platform` input (default: `github`) on `implement-merge-group.yaml` and `feature-pr.yaml`
controls which sub-workflow is selected. Platform selection is driven by
`process-config.yaml` — not hardcoded in workflow routing.

## Stuck-Review Safety (Pending-Review Timeout)

Two PR-lifecycle workflows host re-entrant pending-review polling loops:

- **`plan-level.yaml`** — polls plan-PR review status (github / ADO legs);
  on `state == 'pending'` it waits at `pending_review_gate` and re-polls.
- **`ado-pr.yaml`** — polls ADO PR status; on `pending` it waits at
  `ado_pr_pending_gate` and re-polls.

Without a cap, a silent reviewer can leave the workflow looping indefinitely
with no escalation surface beyond "abort". The MVP guards both loops with a
**poll-cap counter** that escalates to a **stuck-review gate** when the cap
is reached.

| Concept | Where it lives |
|---|---|
| Poll-cap value | Hard-coded **60** in both YAMLs. Greppable via `cap = 60`. |
| Counter file | `$TMPDIR\conductor-plan-pending-poll-{work_item_id}` (plan-level), `$TMPDIR\conductor-ado-pr-pending-poll-{pr_number}` (ado-pr). Same idiom as existing `revise_counter` / `review_counter`. |
| Counter agent | `pending_poll_counter` (plan-level), `ado_pending_poll_counter` (ado-pr). |
| Escalation gate | `stuck_review_gate` (plan-level), `ado_stuck_review_gate` (ado-pr). Three options: `continue_waiting` (reset counter and resume regular polling), `override_approved` (treat as approved and route to merger), `abort` (`$end`). |
| Reset on continue | `stuck_review_reset` zeros the counter so the operator gets a fresh budget after choosing `continue_waiting`. |
| ADR | [`docs/decisions/stuck-review-timeout.md`](../../../docs/decisions/stuck-review-timeout.md) — explains why the cap is hard-coded and sketches the future policy-resolved schema. |

**Out-of-scope by design** for this safety mechanism:

- `github-pr.yaml` — uses an LLM reviewer that returns approved /
  changes_requested directly; no `pending` state, no loop to cap.
- `feature-pr.yaml` — delegates to `pr_lifecycle_*` sub-workflows.
- `actionable.yaml` — uses an LLM evidence reviewer; no polling.

## Versioning

The polyphony CLI binary and the workflow registry **ship bundled** —
one git tag (`v1.2.3`) drives both:

- CLI binary version `1.2.3` (MinVer reads the tag).
- Every workflow YAML's `workflow.version: "1.2.3"`.
- Each `index.yaml` workflow entry's `versions: [...]` list ends with
  `"1.2.3"` (the list is append-only across releases).

Each workflow YAML also declares a **min CLI version** it requires:

```yaml
workflow:
  name: my-workflow
  version: "1.0.0"
  metadata:
    min_polyphony_version: "1.0.0"
```

Wherever a workflow declares `min_polyphony_version`, its preflight
agent (`polyphony state preflight` for full validation, `polyphony
state preflight-lite` for the lightweight 3-check variant) compares
the running CLI's `AssemblyInformationalVersion` against the declared
minimum and **fails preflight on mismatch**. The owning workflow's
`preflight_gate` routes a failed preflight to retry/abort — there is
no "Proceed Anyway" option for version mismatches.

Comparison ignores `+build-metadata` (per SemVer); pre-release identifiers
(`-alpha.5`) are NOT stripped and compare per SemVer rules.

> **Do not upgrade polyphony mid-run.** v1 has no resume-time re-check;
> if you upgrade between conductor sessions, restart the workflow rather
> than `--resume`.

> **Pinning.** `conductor registry add` does NOT accept `--ref` / `--tag`
> for github sources, so you can't pin the registry version via
> conductor. The min-version preflight check is the runtime guarantee
> instead.

For full rationale and the three-layer truth model see
[`docs/decisions/versioning-strategy.md`](../../../docs/decisions/versioning-strategy.md).

## How to Invoke

### Prerequisites

1. Install the conductor CLI
2. Register the polyphony workflow registry:
   ```
   conductor registry add polyphony PolyphonyRequiem/polyphony
   ```
   (Note: positional `NAME SOURCE`; conductor does **not** accept
   `--ref` / `--tag` for github sources. The registry resolves to HEAD
   of the default branch. Per-workflow version pinning is enforced at
   runtime by the min-version preflight check, not by the registry
   mechanism.)
3. Ensure `twig`, `gh`, `polyphony`, and `dotnet` are in PATH.
   `polyphony --version` reports the real MinVer SemVer (e.g. `1.0.0`
   on a tagged commit, or `1.0.1-alpha.5+<sha>` on an untagged commit
   downstream of a tag).
4. The target repo has `.conductor/process-config.yaml` with type
   definitions, templates, and `polyphony validate-config` passes.

### Apex driver invocation

The canonical SDLC entry point is `apex-driver.yaml`. Invoke as
`conductor run apex-driver@polyphony --input apex_id=<ID> --web` (see
the top-of-file *Invocation* section for the command line and `-m`
metadata block). Inputs:

| Input | Required | Default | Description |
|-------|----------|---------|-------------|
| `apex_id` | yes | — | ADO work-item id of the apex (run-root) feature. Treated as the tree root for `polyphony worklist build`. |
| `intent` | no | `new` | One of `new` / `resume` / `replan`. Drives preflight behavior; the dispatch loop is observable-state-driven and identical across all three. |
| `platform` | no | `ado` | Work-item source platform. Threaded through to lifecycle sub-workflows verbatim; the apex-driver itself is platform-agnostic. |
| `organization` | no | `""` | ADO organization name. Required by feature-pr / plan-level on the ADO leg (`platform=ado`); ignored on `github`. |
| `project` | no | `""` | ADO project name (see `organization`). |
| `repository` | no | `""` | ADO repository identifier (GUID or name) (see `organization`). |

Companion deterministic scripts (in `.conductor/registry/scripts/`):

- `lifecycle-router.ps1` — wraps `polyphony state next-ready` to
  classify each item into one of `plan-level | actionable |
  implement-merge-group | feature-pr | fast-path | monitoring | blocked |
  error`. Same envelope shape as `route-actionable-executor.ps1`.
- `worktree-manager.ps1` — spawns/tears down per-item git worktrees
  at `<repo-parent>/<repo-name>-item-<work_item_id>` on branch
  `sdlc/apex/<work_item_id>` (forked from the apex feature branch).
  Idempotent.
- `wave-integrator.ps1` — merges per-item branches into the apex
  feature branch in topological order from `polyphony edges check`.
  `--no-ff` by default. Conflicts are captured per-branch and
  surfaced to a wave-level human gate; the wave continues.

Re-entry semantics: the dispatch loop variable is the worklist
itself, recomputed every iteration via `polyphony worklist build`,
so resuming after a human gate or interrupted run requires no
persisted step pointer — the EdgeGraph re-classifies what's still
pending and the next wave is whatever's ready *now*.

Renegotiation: bubble-up signals (`renegotiation_pending: true` from
inner sub-workflows) consult `policy.renegotiation.auto_decide`
(`prompt` / `auto_restart` / `ignore`) — see `.conductor/policy.yaml`
and ADR `docs/decisions/apex-driver.md` for full rationale.

### Tested behaviors (Phase 7 e2e)

`.conductor/registry/tests/e2e-apex-driver.Tests.ps1` is the
end-to-end suite for the apex-driver tree-walker. It complements
(does not duplicate) `lint-apex-driver.ps1` (structural presence)
and pins the GRAPH the three workflows declare plus the
script-to-YAML contract on `lifecycle_workflow`. The suite parses
all three YAMLs into in-memory graphs (via `powershell-yaml`'s
`ConvertFrom-Yaml`) and asserts the following end-to-end behaviors,
organized per YAML:

**apex-driver.yaml — outer loop:**
- `preflight_apex_state` short-circuits `satisfied` / `empty` to
  `terminal_apex_satisfied`, routes `error` to
  `preflight_failure_gate`, and has an M4 catch-all to the same
  failure gate.
- `preflight_failure_gate` exposes `retry` (→ `preflight_apex_state`)
  and `abort` (→ `terminal_preflight_failed`).
- `build_worklist` routes success to `check_conflicts` and failure
  (with M4 catch-all) to `worklist_failure_gate`.
- `check_conflicts` routes `has_conflicts == true` to
  `conflict_resolution_gate` and `false` (with M4 catch-all) to
  `wave_dispatch_loop`.
- `conflict_resolution_gate` exposes `retry` (→ `build_worklist`)
  and `abort` (→ `terminal_apex_abandoned`).
- `wave_dispatch_loop` is a `for_each` over `build_worklist.output.waves`
  (M8: bare dotted source) that invokes `./apex-wave-dispatch.yaml`
  with `max_concurrent: 1` (waves are sequential by definition) and
  routes to `wave_loop_summary`.
- `wave_loop_summary` routes `all_succeeded == true` to
  `renegotiation_summary` and (with M4 catch-all) failure to
  `wave_failed_gate`.
- `wave_failed_gate` exposes `retry` (→ `build_worklist`), `abort`
  (→ `terminal_apex_abandoned`), and `renegotiate` (also →
  `terminal_apex_abandoned` per MVP stub).
- `renegotiation_summary` routes `any_pending == true` to
  `renegotiation_gate` and (with M4 catch-all) the no-renegotiation
  path to `apex_completion_gate`.
- `renegotiation_gate` exposes `renegotiate` (→ `build_worklist`),
  `override` (→ `apex_completion_gate`), and `abort` (→
  `terminal_apex_abandoned`).
- `apex_completion_gate` exposes `confirm` (→ `close_mark_satisfied`)
  and `abandon` (→ `terminal_apex_abandoned`).
- All three terminals (`terminal_apex_satisfied`,
  `terminal_apex_abandoned`, `terminal_preflight_failed`) route to
  `$end`.
- The full happy-path waypoint chain — `preflight_sync` →
  `preflight_apex_state` → `preflight_ensure_branch` →
  `build_worklist` → `check_conflicts` → `wave_dispatch_loop` →
  `wave_loop_summary` → `renegotiation_summary` →
  `apex_completion_gate` → `close_mark_satisfied` →
  `terminal_apex_satisfied` — is reachable from the entry point.

**apex-wave-dispatch.yaml — wave fan-out:**
- `dispatch_items` is a `for_each` over `workflow.input.wave_items`
  (M8: bare dotted source) with `max_concurrent: 3` and
  `failure_mode: continue_on_error`, that invokes
  `./apex-item-dispatch.yaml` per item.
- `dispatch_items.input_mapping` threads `apex_id`, per-item
  `work_item_id` (from `item.item_id`), `platform`, `organization`,
  `project`, and `repository`.
- `dispatch_items` routes to `aggregate_renegotiation`.
- `aggregate_renegotiation` reads `dispatch_items.outputs` (per M8)
  to scan for `renegotiation_pending` and routes to `integrate_wave`.
- `integrate_wave` invokes `wave-integrator.ps1` with `-ApexId` and
  `-WaveIndex` and routes to `$end`.

**apex-item-dispatch.yaml — branch-on-router (heart of PR #149):**
- `classify_lifecycle` invokes `lifecycle-router.ps1` with
  `-WorkItemId` and `-ApexId`.
- `classify_lifecycle` short-circuits `fast-path` / `monitoring` /
  `blocked` / `error` verdicts to their dedicated terminal nodes
  BEFORE spawning a worktree. The success route targets
  `spawn_worktree`; the M4 catch-all is the last entry and falls
  through to `terminal_classify_error`.
- `spawn_worktree` invokes `worktree-manager.ps1` with
  `-Operation spawn` and base branch `feature/{apex_id}` (canonical
  branch-model name; pre-PR-176 the apex pipeline used a `feature/apex-`
  sub-prefix that bypassed `BranchNameBuilder`).
- `spawn_worktree` branch-on-routers each of the four dispatchable
  verdicts (`plan-level` → `plan_level_dispatch`, `actionable` →
  `actionable_dispatch`, `implement-merge-group` → `implement_merge_group_dispatch`,
  `feature-pr` → `feature_pr_dispatch`) with the spawn-success guard
  in the `when:` clause. The M4 catch-all is the last entry and falls
  through to `terminal_spawn_error`.
- All four lifecycle dispatch nodes are `type: workflow` and invoke
  the parent-relative path `./<lifecycle>.yaml`.
- All four lifecycle dispatch nodes converge on `teardown_worktree`
  (which invokes `worktree-manager.ps1` with `-Operation teardown`
  and routes to `terminal_dispatched`).
- All seven terminals (`terminal_dispatched`, `terminal_fast_path`,
  `terminal_monitoring`, `terminal_blocked`, `terminal_classify_error`,
  `terminal_spawn_error`, plus the apex-driver terminals via the
  outer chain) route to `$end`.
- All four lifecycle dispatch nodes are reachable from
  `classify_lifecycle` in the assembled graph (no orphans).

**Renegotiation bubble-up across all three layers:**
- `apex-item-dispatch.output` declares `renegotiation_pending`,
  `renegotiation_request`, `validate_scope_verdict`, and
  `scope_violation_files`, all guarded by
  `plan_level_dispatch is defined` (M3).
- `apex-wave-dispatch.output` aggregates per-item bubble-ups into
  `renegotiation_pending` (bool) + `renegotiation_items` (array),
  guarded by `aggregate_renegotiation is defined`.
- `apex-driver.output` surfaces `renegotiation_pending` to the
  caller, guarded by `renegotiation_summary is defined`.
- All three output maps pipe booleans through `| string | lower`
  (M7) so a real bool — not capital `True`/`False` — bubbles up.

**Input/output contracts across the 3-YAML chain:**
- `apex-driver` declares `apex_id` (required, number), `intent`,
  `platform` (default `ado`), `organization`, `project`, `repository`.
- `apex-driver` → `apex-wave-dispatch` input_mapping threads
  `apex_id`, per-wave `wave_index` + `wave_items` (`tojson`'d), and
  the ADO context block.
- `apex-wave-dispatch` declares the inputs apex-driver passes;
  `apex-item-dispatch` declares the inputs apex-wave-dispatch passes.
- `apex-item-dispatch` → `plan-level` threads `work_item_id` +
  `intent: resume` + ADO context. → `actionable` threads
  `work_item_id` + `apex_id` + `executor: polyphony`. →
  `implement-merge-group` derives `pg_number` / `branch_name` /
  `feature_branch` from the apex+item ids. → `feature-pr` targets
  `main` on the apex feature branch.

**lifecycle-router script ↔ YAML contract drift:**
- The router script emits exactly the canonical set of
  `lifecycle_workflow` values: `plan-level`, `actionable`,
  `implement-merge-group`, `feature-pr`, `fast-path`, `monitoring`, `blocked`,
  `error`. No undocumented values, none missing.
- Every value the router emits is handled by an
  `apex-item-dispatch.yaml` `when:` clause — short-circuit verdicts
  are branched in `classify_lifecycle`, dispatchable verdicts are
  branched in `spawn_worktree`. No silent dropping into a catch-all,
  no dead branches in the YAML.
- `lifecycle-router.ps1` always exits 0 (routing-style envelope) —
  asserted by live invocation against a missing `polyphony`
  executable: returns `success=false` + `error_code=polyphony_unavailable`
  + `lifecycle_workflow=error` + the work_item_id echo.

**Script envelope contracts (worktree-manager + wave-integrator):**
- `worktree-manager.ps1` teardown of a non-existent worktree is
  idempotent (`success=true`); envelope carries `success`,
  `operation`, `work_item_id`, `worktree_path`, `branch`,
  `error_code`. Always exits 0.
- `wave-integrator.ps1` returns a routing-style envelope
  (`success=false` + `error_code=polyphony_unavailable`) when
  `polyphony` is unavailable. Envelope carries `success`,
  `wave_index`, `apex_id`, `feature_branch` (defaults to
  `feature/{apex_id}`), `merge_strategy` (defaults to `no-ff`),
  `branches_integrated`, `skipped`, `conflicts`. Always exits 0.

### Direct Sub-Workflow Invocation

Individual sub-workflows in the library can still be invoked directly
when a driver isn't required — for example, `plan-level.yaml` to drive
only the planning phase, or `feature-pr.yaml` to run the feature-PR
review and remediation loop standalone. Each sub-workflow declares its
own `--input` shape; consult the YAML for the exact contract.

In every case, the standard worktree + metadata pattern still applies:

```powershell
# Standard worktree setup before any conductor run
git worktree add -b sdlc/<ID> ../polyphony-<ID> main
cd ../polyphony-<ID>
dotnet restore
Copy-Item -Recurse ../polyphony/.twig .twig
twig set <ID>
twig sync
```

The required `-m` metadata block (see "Workflow Metadata" above) is the
same regardless of which sub-workflow is invoked: `tracker`,
`project_url`, `git_repo`, `workitem_id`, `worktree_name`, `cwd`.

> **Always launch detached** — wrap with `Start-Process -WindowStyle Hidden` so
> conductor survives if the parent session drops. Always use `--web` (not `--web-bg`).

## Policy Configuration

Policy is defined in `.conductor/policy.yaml` and resolved at runtime via
`polyphony policy resolve --domain <domain> --scope <scope>`. Resolution uses
most-specific-wins scoping: `root` → `type:<Name>` → `defaults`.

### Policy Domains

| Domain | Consumed By | Keys | Description |
|--------|-------------|------|-------------|
| `approvals` | `plan-level.yaml` (review_router / plan_approval) | `mode`, `max_revision_cycles`, `quality_threshold` | Controls whether the plan approval gate fires and under what conditions |
| `pr` | `github-pr.yaml` / `ado-pr.yaml` | `mode`, `max_fix_loops`, `max_remediation_cycles` | Controls PR merge gating and fix loop caps |
| `concurrency` | the apex driver's merge-group dispatch and the recursive `plan-level.yaml` for_each blocks | `max_concurrent_children` | Limits parallel sub-workflow and merge-group execution |
| `open_questions` | `plan-level.yaml` (open_questions_policy → routing) | `mode`, `min_severity`, `max_question_loops` | Controls whether architect open questions gate for user input |

### `open_questions` Domain

Resolved after the architect agent completes. Drives routing between the
architect and the `open_questions_gate` human gate.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `mode` | `auto` \| `warning` \| `manual` | `warning` | `auto` = never gate; `warning` = gate on severity ≥ min_severity; `manual` = gate on any question |
| `min_severity` | `critical` \| `major` \| `moderate` \| `low` | `moderate` | Minimum severity that triggers the gate (only applies in `warning` mode) |
| `max_question_loops` | number | `3` | Maximum answer → revision cycles before auto-proceeding to review |

**Gate modes explained:**

- **`auto`** — Questions are emitted for plan documentation but never stop the
  workflow. Useful for routine task types where the architect's defaults suffice.
- **`warning`** — Gates only when questions at or above `min_severity` exist.
  The default mode; balances user involvement with workflow throughput.
- **`manual`** — Any question (even `low` severity) stops for user input.
  Use for root items or high-stakes planning where every ambiguity matters.

## Engine Vocabulary (Phase 6 + 7)

The sections below cover the requirement / edge / facet vocabulary the
engine produces and consumes. Workflow YAMLs and helper scripts route on
the JSON envelopes these primitives surface — see the
**polyphony-workflow-author** skill for the routing patterns.

### EdgeGraph and cross-item edges

`Polyphony.Sdlc.EdgeGraph` is the merged dependency graph spanning every
item in a run-root's worklist. `EdgeGraph.Build(items)` accepts a flat
list of `EdgeGraphInput(itemId, parentItemId, requirementSet)` and
returns an immutable graph carrying:

- `ItemRequirements` — the per-item `RequirementSet` (within-item edges
  live here).
- `Edges` — flat list of `CrossItemEdge(prereqItem, prereqKind,
  dependentItem, dependentKind, threshold, source)`.
- `Conflicts` — `EdgeConflict` records (`Cycle`, `UnknownItem`,
  reserved `ThresholdMismatch`); empty on a clean build.

The **definitional** bucket is the only one wired today
(`CrossItemEdgeDeriver.DeriveDefinitional`). It emits exactly two rules:

1. **Children-unblock.** A parent's `children_seeded → child.<entry-req>`
   for every entry requirement of every child. Pure containers (no
   `children_seeded`) skip this — children start unblocked.
2. **Terminal-rollup.** Every `child.item_satisfied → parent.item_satisfied`.
   Gates parent completion, not start.

Cycles are detected at `(itemId, requirementKind)` granularity, not bare
item id. Definitional edges flow parent→child for unblock and
child→parent for rollup, but target distinct kinds, so they never cycle
on their own — cycles only arise from policy or planner-declared edges
(later PRs).

`ToWaves()` returns a topological grouping suitable for parallel
dispatch. **Carve-out:** only edges targeting an item's *entry*
requirements (within-item requirements with no incoming within-item
edges, excluding `item_satisfied`) gate dispatch. Terminal-rollup edges
gate completion, not start. `ToWaves()` throws when `Conflicts` is
non-empty — the conflict gate is the only legitimate consumer of a
conflicted graph.

`polyphony edges check <work-item>` (Phase 7 PR #3) walks the subtree,
builds the graph, and emits a routing-style envelope:

```json
{
  "work_item_id": 1234,
  "items_walked": 7,
  "edges_total": 12,
  "has_conflicts": false,
  "conflicts": []
}
```

Always exits 0; workflows route on `has_conflicts` + `conflicts[]`.
`--render text` additionally writes a Markdown report to stderr.

### `ItemSatisfied` synthetic terminal

Every item carries a synthetic `item_satisfied` requirement — leaves
AND pure containers — emitted by `RequirementSetDeriver`. It is the
uniform terminal that the cross-item terminal-rollup rule targets.

**Reducer semantics** (`RequirementSetReducer.Apply`): the reducer
never *observes* `item_satisfied` — it is derived purely from
prerequisite edges:

- **Pure container** (no incoming within-item edges) → stays `Needed`
  pending cross-item rollup from children.
- **Decomposable + plannable + implementable item** (has incoming
  within-item edges) → when all prerequisites meet their thresholds,
  promotes **straight to `Satisfied`** (skipping `Ready` — there is
  nothing to dispatch, the item is wholly done).

`polyphony state next-ready`'s `ClassifyStatus` recognizes the pure
container case (single `item_satisfied` requirement still `Needed`)
and surfaces it as `status: "empty"` so workflows can treat it
identically to zero-own-work types.

### Execution mode

Per-type knob in `process-config.yaml`:

```yaml
types:
  Apex:
    facets: [plannable, implementable]
    execution_mode: plan_then_implement   # default: parallel
```

Allowed values: `parallel` (default) and `plan_then_implement`. Validated
at config-load time by `ConfigValidator` rule **V-19**.

`RequirementInputResolver.Resolve` returns
`ResolvedRequirementInputs.ExecutionMode` together with
`ExecutionModeProvenance` — `Default` when the config says nothing,
`Explicit` when the type sets it.

`ExecutionModeInjector.Inject(set, mode)` is the composition layer that
turns the value into edges:

- `Parallel` — no-op, returns the same `RequirementSet` instance.
- `PlanThenImplement` — when the set carries BOTH `plan_promoted` AND
  `implementation_merged`, appends a single
  `plan_promoted → implementation_merged` edge at threshold `Satisfied`.
  Idempotent: re-applying never duplicates the edge. If either kind is
  absent, the mode is irrelevant and no edge is added.

The injected edge carries `Source = Definitional` — the knob is a
hard-wired transformation of a typed config value to a fixed edge
shape, not a per-instance policy. The deriver itself stays
mode-agnostic; callers compose `Inject(Derive(...),
resolved.ExecutionMode)` when they want the mode applied.

### Facet profiles and agent addendum

`process-config.yaml > facets:` binds each canonical facet name to the
skills + MCPs the driver layers onto an agent invocation when an item
carries that facet:

```yaml
facets:
  actionable:
    skills: [actionable-evidence, security-review]
    mcps:   [shell, web-fetch]
  implementable:
    skills: [polyphony-coding-style]
    mcps:   [shell]
```

`FacetProfileComposer.Compose(facets, profiles, perItemGuidance)`
returns an `AgentAddendum(Skills, Mcps, GuidanceContext)`:

- **Union semantics with permissible identical collisions.** Two
  facets binding the same skill name dedupe silently — that is the
  whole point of union.
- **Different-value collisions are not detectable** at compose time
  (skills + MCPs are bound by name, not by value). The
  `FacetProfileValidator` (rule **V-20**) rejects intra-facet
  duplicates at config-load — those are almost certainly typos.
  Cross-facet duplicates are fine.
- **Output is deterministic:** Skills and MCPs sorted ordinal
  ascending. Snapshot tests and reviewers depend on this.
- Unknown facet names are silently omitted (validator catches typos).

Per-item `guidance` is **append-only prompt context** — never composed,
never merged, never order-sensitive. It rides on
`AgentAddendum.GuidanceContext` separately from skills/mcps.

### Per-item guidance

`polyphony guidance extract <work-item>` reads the resolved guidance
policy and returns the extracted text:

```json
{
  "work_item_id": 1234,
  "source": "description_block",
  "guidance": "Use the Foo library, not Bar.",
  "guidance_present": true
}
```

Two sources, configured in `.conductor/policy.yaml`:

```yaml
guidance:
  source: description_block        # default — works on every platform
  # or:
  # source: ado_field
  # ado_field_name: Custom.PolyphonyGuidance

  # Optional per-type overrides (most-specific-wins).
  by_type:
    Leaf:
      source: ado_field
      ado_field_name: Custom.LeafGuidance
```

- **`description_block`** (default) — the driver extracts a fenced HTML
  comment block from the work item description:

  ```html
  <!-- polyphony:guidance -->
  Use the Foo library, not Bar.
  Reviewer: pay extra attention to error messages.
  <!-- /polyphony:guidance -->
  ```

  Description text outside the block is **NOT** injected — it sits
  outside the prompt-injection trust boundary. Works on every platform
  with a description field (GitHub Issues, ADO, etc.).

- **`ado_field`** (opt-in) — the driver reads from a dedicated ADO
  custom field whose reference name is set via `ado_field_name`. Use
  in workspaces that have stood up a dedicated guidance field.

When the effective source is `ado_field` but `ado_field_name` is
empty, `PolicyLoader` fails at load time.

### Evidence branches and PRs

The actionable workflow surface uses evidence branches/PRs to record
work that is not implementation (interviews, evaluations, decisions).
Both verbs are routing-style and idempotent on resume.

**`polyphony branch ensure-evidence-branch <work-item>`** ensures the
evidence branch exists locally and on remote:

- Default name: `evidence/<apex>-<item>` (Rev 4 grammar, via
  `BranchNameBuilder.Evidence`).
- Collapses to orphan form `evidence/<item>` when `--apex-id` equals
  `workItemId` (or is omitted — defaults to the work item itself).
- Default base: `feature/<apex>`; override with `--from-ref`.

**`polyphony pr open-evidence-pr <work-item>`** opens (or reuses) the
GitHub PR promoting the evidence branch into its parent feature
trunk:

- Normal case: head = `evidence/<apex>-<item>`, base = `feature/<apex>`.
- Orphan case: head = `evidence/<item>`, base = `main`.
- Reuses an existing open PR for the same head/base pair instead of
  creating a duplicate (mirrors `pr create-feature-pr`).

### New CLI verbs (Phase 6 + 7)

Quick index of the new verb surface added by PRs #125-#133. All emit a
routing-style JSON envelope on stdout and exit 0; workflows gate on
envelope fields, never on the exit code (cf. **polyphony-workflow-author**).

| Verb | Purpose | Source |
|------|---------|--------|
| `polyphony edges check <id>` | Build the EdgeGraph for a subtree; surface conflicts as routable JSON | `Commands/EdgesCommands.Check.cs` |
| `polyphony branch ensure-evidence-branch <id>` | Idempotently create the evidence branch (orphan or apex-scoped) | `Commands/BranchCommands.EnsureEvidenceBranch.cs` |
| `polyphony pr open-evidence-pr <id>` | Open or reuse the evidence PR against `feature/<apex>` (or `main` for orphan) | `Commands/PrCommands.OpenEvidencePr.cs` |
| `polyphony guidance extract <id>` | Read the per-item guidance per the resolved `guidance:` policy | `Commands/GuidanceCommands.cs` |