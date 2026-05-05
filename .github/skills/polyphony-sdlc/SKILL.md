---
name: polyphony-sdlc
description: >-
  Type-agnostic SDLC workflow documentation. Covers the polyphony-full@polyphony
  conductor workflow suite — 9 YAML files with Polyphony-driven phase detection,
  recursive planning, parallel PG execution, feature PR remediation, and platform
  abstraction. Reference when invoking, debugging, or extending the v2 pipeline.
user-invokable: false
---

# Polyphony SDLC v2 Workflow Suite

Type-agnostic SDLC pipeline powered by the Polyphony routing engine and the
`conductor` orchestrator. Accepts **any work item type at any hierarchy level**,
detects lifecycle phase via `polyphony route`, and drives it through planning,
implementation, PR review, feature PR remediation, and close-out — all via
multi-agent orchestration with no hardcoded type names.

> **Replaces** the original `twig-sdlc-full@twig` workflow. The polyphony suite
> is registered as `polyphony-full@polyphony` and lives in the
> `polyphony-conductor-workflows` repo (separate from `twig-conductor-workflows`).

## Workflow Inputs

The root workflow (`polyphony-full.yaml`, registered as `polyphony-full@polyphony`)
accepts three inputs:

| Input | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `work_item_id` | `number` | **yes** | — | ADO work item ID (any type at any hierarchy level) |
| `intent` | `string` | no | `resume` | `new` — fresh start · `redo` — delete and redo · `resume` — pick up where left off |
| `user_plan_path` | `string` | no | `""` | Path to a user-authored plan document; the architect refines rather than discards it |

## Workflow Metadata

All metadata is passed dynamically via `--metadata` / `-m` flags at invocation time.
The workflow YAMLs contain no metadata — the invoking agent resolves all values for
the dashboard and event log. **`tracker=ado` is required** so downstream tooling
(dashboard, observation filer, post-mortem skills) knows which provider's APIs and
URL conventions to use.

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
> `implement-pg.yaml` (`pr_platform_router`).

## Phase Detection and Routing

The root workflow uses `polyphony route` (via `detect-state.ps1`) for type-agnostic
phase detection. No work item type names appear in any YAML routing condition.

```
preflight_check → state_detector (detect-state.ps1)
                      │
                      ├── needs_planning / needs_seeding → planning sub-workflow
                      ├── ready_for_implementation / in_progress → implementation sub-workflow
                      ├── ready_for_completion → close-out sub-workflow
                      └── done / removed → $end
```

The `state_detector` reads ADO state via `polyphony route` and `polyphony validate`,
producing a `phase` value that drives all downstream routing. On `intent=new`, the
plan is generated from scratch; on `resume`, existing state is discovered and the
workflow picks up where it left off (P3: re-entry by state discovery).

## Recursion Depth Budget (C3)

The conductor engine enforces a maximum depth of 10. The polyphony workflow budget uses 9
levels, leaving 1 level of headroom:

```
Depth 0: polyphony-full.yaml                  (root: preflight → detect → route)
Depth 1: polyphony-planning.yaml              (planning orchestration)
         OR polyphony-implement.yaml          (implementation orchestration)
Depth 2: plan-level.yaml                      (recursive planning core)
Depth 3: plan-level.yaml self-recursion       (child level planning)
Depth 4-7: plan-level.yaml self-recursion     (up to 6 nested plannable levels)
Depth 8: github-pr.yaml / ado-pr.yaml         (PR lifecycle — leaf)
         OR implement-pg.yaml                  (PG lifecycle)
```

The `depth_guard` script in `plan-level.yaml` validates `depth < max_depth` (default
max_depth=6). If the limit is reached, a `depth_exceeded_gate` human gate fires so the
user can approve going deeper or abort.

## The 9 YAML Workflow Files

### 1. `polyphony-full.yaml` — Root Workflow

**Registry name:** `polyphony-full@polyphony`
**Responsibility:** Top-level entry point. Accepts any work item type at any hierarchy
level, runs full preflight validation, detects lifecycle phase via Polyphony, and routes
to the appropriate sub-workflow.

| Agent | Type | Description |
|-------|------|-------------|
| `preflight_check` | script | Full preflight validation (12 checks) via `preflight-check.ps1` |
| `preflight_gate` | human_gate | Retry / proceed / abort on preflight failure |
| `state_detector` | script | Detect lifecycle phase via `detect-state.ps1` |
| `planning` | workflow | Delegates to `polyphony-planning.yaml` |
| `implementation` | workflow | Delegates to `polyphony-implement.yaml` |
| `close_out` | workflow | Delegates to `close-out.yaml` |

---

### 2. `polyphony-planning.yaml` — Planning Orchestration

**Responsibility:** Planning entry point. Runs lightweight preflight, delegates to
`plan-level.yaml` for recursive planning, re-checks state, and seeds the work tree.

| Agent | Type | Description |
|-------|------|-------------|
| `preflight_lite` | script | Quick 3-check validation via `preflight-lite.ps1` |
| `preflight_lite_gate` | human_gate | Retry / abort on lite preflight failure |
| `plan_level` | workflow | Delegates to `plan-level.yaml` (depth=0, max_depth=6) |
| `seed_check` | script | Re-check phase after planning via `detect-state.ps1` |
| `work_tree_seeder` | agent (Sonnet) | Seed child work items from the approved plan |

---

### 3. `plan-level.yaml` — Recursive Planning Core

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

### 4. `polyphony-implement.yaml` — Implementation Orchestration

**Responsibility:** Implementation entry point. Loads the work tree hierarchy, discovers
PG structure, and dispatches pending PGs in parallel via `for_each` to `implement-pg.yaml`.

| Agent | Type | Description |
|-------|------|-------------|
| `preflight_lite` | script | Quick 3-check validation |
| `preflight_lite_gate` | human_gate | Retry / abort on failure |
| `load_work_tree` | script | Load work item hierarchy and PG structure via `load-work-tree.ps1` |
| `pg_dispatcher` | script | Prepare pending PGs array for `for_each` dispatch |
| `pg_execution_group` | for_each (workflow) | Parallel PG execution — spawns one `implement-pg.yaml` per pending PG (max 3 concurrent) |
| `pg_summary_gate` | human_gate | Surface partial PG failures; retry or abort |

---

### 5. `implement-pg.yaml` — Single PG Lifecycle

**Responsibility:** Implements all tasks in a single Processing Group, creates and merges
a PG-level PR, and closes completed work items in ADO after merge.

| Agent | Type | Description |
|-------|------|-------------|
| `pg_router` | script | Determine current PG action via `pg-router.ps1` |
| `branch_manager` | script | Create/checkout PG branch |
| `primary_router` | script | Route to next implementable child via `polyphony branch next-task` |
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
| `scope_closer` | script | Transition completed items to Done via `scope-closer.ps1` |

---

### 6. `github-pr.yaml` — GitHub PR Lifecycle

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

### 7. `ado-pr.yaml` — ADO PR Lifecycle (Stub)

**Responsibility:** Placeholder for Azure DevOps PR operations. Returns a structured
error indicating ADO PR support is not yet implemented, with a human gate for manual
PR management. Shares the same interface contract as `github-pr.yaml`.

| Agent | Type | Description |
|-------|------|-------------|
| `ado_pr_error` | script | Emit structured error: ADO PR not implemented |
| `ado_pr_manual_gate` | human_gate | Manual PR management or abort |

---

### 8. `feature-pr.yaml` — Feature PR and Remediation

**Responsibility:** Creates a feature PR (all PGs merged → feature branch → target
branch), reviews it, and runs remediation cycles when the reviewer requests changes.
Capped at **3 remediation cycles**; after that, a human gate fires for escalation.

| Agent | Type | Description |
|-------|------|-------------|
| `feature_pr_creator` | script | Create feature PR via `polyphony pr create-feature-pr` (reuses an existing open PR for the same head/base pair if one exists) |
| `pr_platform_router` | script | Route to `github-pr.yaml` or `ado-pr.yaml` based on `platform` input |
| `pr_lifecycle_github` | workflow | Delegates to `github-pr.yaml` for review/fix/merge |
| `pr_lifecycle_ado` | workflow | Delegates to `ado-pr.yaml` (stub — human gate) |
| `remediation_counter` | script | Track remediation cycle count (max 3) |
| `remediation_cap_gate` | human_gate | Escalate when 3 remediation cycles exceeded |
| `remediation_abort` | script | Handle abort from cap gate |
| `remediation_planner` | agent (Opus 1M) | Plan fixes for reviewer feedback as an addendum |
| `remediation_seeder` | agent (Sonnet) | Seed new remediation PG from the addendum plan |
| `remediation_implementer` | workflow | Delegates to `implement-pg.yaml` for the remediation PG |
| `feature_pr_updater` | agent (Sonnet) | Re-request review on the feature PR after a remediation cycle merges |

> **Platform abstraction parity:** Like `implement-pg.yaml`, the feature PR lifecycle
> goes through `pr_platform_router` → `pr_lifecycle_github` / `pr_lifecycle_ado`. The
> review/fix/merge logic lives in `github-pr.yaml` (or the ADO stub) — not duplicated
> in `feature-pr.yaml`.

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

### 9. `close-out.yaml` — Close-Out and Observation Filing

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
2. **Task loop** — `primary_router` discovers the next implementable child; `coder` implements
   it; `primary_reviewer` gates quality; loop until all children in the PG are complete
3. **Issue review** — `scope_reviewer` checks
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
4. `remediation_implementer` runs the full `implement-pg.yaml` lifecycle for the new PG
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

The `platform` input (default: `github`) on `implement-pg.yaml` and `feature-pr.yaml`
controls which sub-workflow is selected. Platform selection is driven by
`process-config.yaml` — not hardcoded in workflow routing.

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
  name: polyphony-full
  version: "1.0.0"
  metadata:
    min_polyphony_version: "1.0.0"
```

On every run, `polyphony state preflight` (root) and `polyphony state
preflight-lite` (planning + implement sub-workflows) check the running
CLI's `AssemblyInformationalVersion` against
`metadata.min_polyphony_version` and **fail preflight on mismatch**.
The existing `preflight_gate` routes a failed preflight to retry/abort
— there is no "Proceed Anyway" option for version mismatches.

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

### New Epic with a User Plan

```powershell
# Set up a worktree for the Epic
git worktree add -b sdlc/<ID> ../polyphony-<ID> main
cd ../polyphony-<ID>
dotnet restore
Copy-Item -Recurse ../polyphony/.twig .twig
twig set <ID>
twig sync

# Launch the workflow with a user-authored plan
conductor run polyphony-full@polyphony `
  --input work_item_id=<ID> `
  --input intent=new `
  --input user_plan_path=path/to/plan.md `
  -m tracker=ado `
  -m project_url=https://dev.azure.com/dangreen-msft/Twig `
  -m git_repo=C:\Users\dangreen\projects\polyphony `
  -m workitem_id=<ID> `
  -m worktree_name=polyphony-<ID> `
  -m cwd=C:\Users\dangreen\projects\polyphony-<ID> `
  --web
```

### Resume an Existing Work Item

```powershell
# Resume from wherever the work item left off (default intent)
conductor run polyphony-full@polyphony `
  --input work_item_id=<ID> `
  -m tracker=ado `
  -m project_url=https://dev.azure.com/dangreen-msft/Twig `
  -m git_repo=C:\Users\dangreen\projects\polyphony `
  -m workitem_id=<ID> `
  -m worktree_name=polyphony-<ID> `
  -m cwd=C:\Users\dangreen\projects\polyphony-<ID> `
  --web
```

### Redo from Scratch

```powershell
# Delete existing children/branches and start over
conductor run polyphony-full@polyphony `
  --input work_item_id=<ID> `
  --input intent=redo `
  -m tracker=ado `
  -m project_url=https://dev.azure.com/dangreen-msft/Twig `
  -m git_repo=C:\Users\dangreen\projects\polyphony `
  -m workitem_id=<ID> `
  -m worktree_name=polyphony-<ID> `
  -m cwd=C:\Users\dangreen\projects\polyphony-<ID> `
  --web
```

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
| `concurrency` | `polyphony-implement.yaml` / `polyphony-planning.yaml` | `max_concurrent_children`, `max_concurrent_pgs` | Limits parallel workflow and PG execution |
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