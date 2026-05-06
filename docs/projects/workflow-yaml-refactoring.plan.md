---
work_item_id: 2583
title: "Phase 3: Workflow YAML Refactoring"
type: Epic
status: in-progress
plan_revision_count: 1
---

# Phase 3: Workflow YAML Refactoring — Implementation Plan

## Executive Summary

This plan designs and implements the `twig-sdlc-v2-full` conductor workflow YAML suite — a type-agnostic, Polyphony-driven replacement for the existing hardcoded `twig-sdlc-full` workflow. The v2 workflow accepts any work item type at any hierarchy level, uses Polyphony CLI for phase detection and routing, supports recursive planning through a single `plan-level.yaml` sub-workflow, drives PG lifecycle with full parallel support, and delegates PR operations to platform-specific sub-workflows. All files are created as new additions alongside the existing workflow — no existing files are modified. The result is a workflow system that works with any ADO process template (Basic, Agile, Scrum, CMMI) without type-name hardcoding, enabling cross-repo adoption per the Phase 6 roadmap.

## Background

### Current State

The existing `twig-sdlc-full@twig` workflow (registered from `PolyphonyRequiem/twig-conductor-workflows`) orchestrates an Epic → Issue → Task SDLC pipeline through ~9 YAML workflow files with type-specific routing branches. The Polyphony CLI (`polyphony route`, `polyphony validate`, `polyphony hierarchy`) and the generic scripts (`detect-state.ps1`, `pg-router.ps1`, `impl-router.ps1`, `scope-closer.ps1`, `load-work-tree.ps1`) from Phases 1-2 are now complete and type-agnostic. The foundation is ready for the workflow YAML layer to become type-agnostic.

**Completed prerequisite phases:**

| Phase | Status | What It Delivered |
|-------|--------|-------------------|
| Phase 0: Foundations | ✅ Done | `.conductor/` config, process-config.yaml, work-item-types, P12 principle |
| Phase 1: Polyphony Core Engine | ✅ Done | `route`, `validate`, `hierarchy` commands with full routing engine |
| Phase 2: Generic Workflow Scripts | ✅ Done | Type-agnostic `detect-state.ps1`, `pg-router.ps1`, `impl-router.ps1`, `scope-closer.ps1`, `load-work-tree.ps1` |

### Existing Workflow Architecture

The current `twig-sdlc-full` workflow has this structure:

| YAML File | Purpose | Type Coupling |
|-----------|---------|---------------|
| `twig-sdlc-full.yaml` | Root entry, preflight → intake → planning → implementation → close-out | Routes by work item type |
| `twig-sdlc-planning.yaml` | Planning orchestration (architect → review → approve) | Epic/Issue-specific branches |
| `twig-sdlc-implement.yaml` | Implementation orchestration (PG manager → task manager) | Issue/Task-specific paths |
| `plan-design.yaml` | Architect agent for Epic-level plans | Epic-only |
| `plan-child.yaml` | Plan child Issues under an Epic | Hardcoded parent-child |
| `plan-issue.yaml` | Plan a single Issue | Issue-only |
| `task-decomposition.yaml` | Decompose Issue into Tasks | Issue→Task only |
| `github-pr.yaml` (implicit) | PR lifecycle for GitHub | Platform-specific |

**Key scripts consumed by workflows (all now type-agnostic from Phase 2):**

| Script | Purpose | Polyphony Integration |
|--------|---------|----------------------|
| `detect-state.ps1` | Root state detection: phase, plan, seeds, intent | `polyphony route` + `polyphony validate` |
| `pg-router.ps1` | Route to next PG action | `polyphony hierarchy` + `Group-ByPG` |
| `impl-router.ps1` | Route to next implementable task within a PG | `polyphony hierarchy` + facet filter |
| `scope-closer.ps1` | Close items in a PG after PR merge | `polyphony validate` per item |
| `load-work-tree.ps1` | Load full hierarchy with PG completion status | `polyphony hierarchy` + PR status |

### Conductor YAML Facets

Conductor workflow YAMLs support the following agent types and constructs relevant to this design (confirmed from conductor source in `~/projects/conductor-fix/`):

- **`type: agent`** (default) — LLM agent with model, prompt, tools, and output routing
- **`type: script`** — Deterministic PowerShell/shell script execution (P8). Output is always `{stdout, stderr, exit_code}`.
- **`type: human_gate`** — User decision point with options list (P6). Options have `label`, `value`, `route`, and optional `prompt_for`.
- **`type: workflow`** — Invokes an external workflow YAML as a sub-workflow. Supports:
  - `workflow: ./path.yaml` — path resolved relative to parent YAML
  - `input_mapping:` — maps parent context to sub-workflow inputs via Jinja2 expressions
  - `max_depth: N` — per-agent recursion depth limit (1-10). **Self-referencing is supported** — a workflow can reference itself with `max_depth` to prevent runaway recursion.
  - Sub-workflow output is accessible via `{{ agent_name.output.field }}` in downstream agents
- **`parallel:`** — Static parallel groups: runs a fixed list of agents concurrently with `failure_mode` (fail_fast, continue_on_error, all_or_nothing)
- **`for_each:`** — Dynamic parallel groups: spawns N agent instances from a runtime array. Supports `source`, `as`, `max_concurrent`, and `key_by`. **This is the mechanism for parallel PG execution** — the PG dispatcher outputs an array, and `for_each` launches one sub-workflow per PG.
- **Routes** — `routes:` list with `to:` (agent name, group name, `$end`, or `self`) and optional `when:` (Jinja2 condition)

### Recursion Depth Budget (C3)

```
Depth 0: twig-sdlc-v2-full.yaml (root)
Depth 1: plan-level.yaml OR implement-pg.yaml (sub-workflow)
Depth 2: plan-level.yaml recursion (child planning)
Depth 3-7: plan-level.yaml recursion (up to 6 nested plannable levels)
Depth 8: github-pr.yaml / ado-pr.yaml (PR lifecycle, leaf)
```

Maximum conductor depth is 10; this budget uses 9, leaving 1 level of headroom.

### User Plan Support

The v2 workflow supports `user_plan_path` as a first-class input. When provided, the architect agent refines the user plan rather than starting from scratch. The user plan is committed alongside the refined plan in the planning PR as `docs/projects/<id>.user-plan.md`.

### Design Principles Governing This Work

| Principle | Implication for YAML Refactoring |
|-----------|----------------------------------|
| P1: Work Items Are Source of Truth | Routing decisions come from Polyphony (which reads ADO state), not from workflow-embedded logic |
| P3: Re-Entry by State Discovery | All sub-workflows must be resumable — `detect-state.ps1` and `pg-router.ps1` discover current state |
| P5: Type-Agnostic Structure | No type names in YAML routing conditions — Polyphony facets drive branching |
| P6: Human Gates for Genuine Decisions | Gates only at plan approval, open questions, user acceptance — not routine checkpoints |
| P8: Scripts Over Agents | Routing, state detection, PG management are scripts; agents do judgment work |
| P10: Explicit Invariants | Every node documents preconditions and postconditions |
| P12: Short-Lived Sessions | Recursive sub-workflows keep each agent's scope bounded |
| P13: Human-Readable Gates | Gate prompts are Jinja2 Markdown templates with clickable artifact links |

## Problem Statement

The existing `twig-sdlc-full` workflow hardcodes Epic → Issue → Task hierarchy assumptions across its YAML routing logic. Specifically:

1. **Type-specific workflow files** — Separate `plan-design.yaml`, `plan-child.yaml`, `plan-issue.yaml`, and `task-decomposition.yaml` each handle one type, creating N files × M types complexity.
2. **No recursive planning** — The workflow has separate paths for "plan Epic" vs "plan Issue" vs "decompose Issue into Tasks" instead of a single recursive "plan any plannable level" pattern.
3. **Sequential PG execution** — PGs execute one at a time; no parallel PG support.
4. **No platform abstraction** — PR operations are GitHub-specific without a clean interface for ADO PR support.
5. **No user plan input** — The workflow doesn't accept pre-authored plans as first-class input.
6. **No feature PR remediation** — When a human reviewer requests changes on a feature PR, there's no automated cycle to create an addendum, implement fixes, and re-request review.
7. **No dependency gates** — Blocked work items (ADO predecessor links) aren't detected or surfaced.

## Goals and Non-Goals

### Goals

1. **G1:** Create `twig-sdlc-v2-full.yaml` as a parallel deployment alongside the existing workflow — registered as `twig-sdlc-v2-full@twig`.
2. **G2:** Replace 4 planning workflow files (`plan-design`, `plan-child`, `plan-issue`, `task-decomposition`) with a single recursive `plan-level.yaml`.
3. **G3:** Support any work item type at any hierarchy level as the workflow entry point.
4. **G4:** Implement full parallel PG support in the implementation sub-workflow.
5. **G5:** Create platform-specific PR sub-workflows (`github-pr.yaml` with full implementation, `ado-pr.yaml` as interface stub).
6. **G6:** Support `user_plan_path` as a first-class workflow input.
7. **G7:** Implement feature PR creation and remediation cycle.
8. **G8:** Implement dependency gates for blocked work items.
9. **G9:** Zero modification to existing workflow files — pure additive deployment.

### Non-Goals

- **NG1:** Modifying the existing `twig-sdlc-full` workflow or its scripts.
- **NG2:** Full ADO PR implementation (stub only — deferred to Phase 6).
- **NG3:** Cross-repo onboarding (Phase 6 scope).
- **NG4:** DU preview adoption in Polyphony (Phase 5 scope).
- **NG5:** Changes to Polyphony CLI commands or scripts — those are complete from Phases 1-2.
- **NG6:** New Polyphony CLI commands — the v2 workflows consume the existing `route`, `validate`, and `hierarchy` commands.

## Requirements

### Functional Requirements

| ID | Requirement | Source |
|----|-------------|--------|
| FR1 | Root workflow accepts `work_item_id`, `intent`, and `user_plan_path` inputs | §3.1 |
| FR2 | Root workflow routes to planning or implementation based on `polyphony route` phase detection | §3.1 |
| FR3 | Planning sub-workflow handles any plannable level via a single `plan-level.yaml` | §3.2 |
| FR4 | Planning sub-workflow recursively invokes itself for nested plannable children | §3.2 |
| FR5 | Planning recursion is capped at 6 levels (C3 depth budget) | §3.2 |
| FR6 | Planning sub-workflow injects type definition + template from `.conductor/work-item-types/` into architect prompt | §3.2 |
| FR7 | Implementation sub-workflow supports full parallel PG execution | §3.3 |
| FR8 | PR lifecycle is delegated to platform-specific sub-workflows | §3.3 |
| FR9 | Feature PR is created after all PGs merge into the feature branch | §3.4 |
| FR10 | Remediation cycle: addendum → new PG → merge → re-review on feature PR | §3.4 |
| FR11 | Dependency gates fire for work items with unresolved ADO predecessor links | §3.4 |
| FR12 | All workflows are resumable via state discovery (P3) | P3 |

### Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR1 | No type-name literals in workflow YAMLs (P5) |
| NFR2 | Conductor depth ≤ 9 for maximum recursion path (C3) |
| NFR3 | Each agent session is bounded to avoid context compaction (P12) |
| NFR4 | Gate prompts use Jinja2 Markdown templates (P13) |
| NFR5 | Workflows validate via `conductor validate` without errors |

## Proposed Design

### Architecture Overview

The v2 workflow is a tree of YAML files, each representing a bounded orchestration concern:

```
twig-sdlc-v2-full.yaml                    (root: preflight → detect → route)
  ├── twig-sdlc-v2-planning.yaml           (planning orchestration)
  │     └── plan-level.yaml                (recursive: plan any plannable level)
  │           └── plan-level.yaml          (self-recursion for child levels)
  ├── twig-sdlc-v2-implement.yaml          (implementation orchestration)
  │     ├── implement-pg.yaml              (single PG lifecycle)
  │     │     └── github-pr.yaml           (PR lifecycle — GitHub)
  │     │     └── ado-pr.yaml              (PR lifecycle — ADO stub)
  │     └── implement-pg.yaml              (parallel PGs)
  ├── feature-pr.yaml                      (feature PR creation + remediation)
  │     └── github-pr.yaml / ado-pr.yaml
  └── close-out.yaml                       (close-out + filing)
```

### Key Components

#### 1. `twig-sdlc-v2-full.yaml` — Root Workflow

**Responsibility:** Top-level entry point. Accepts any work item type, detects phase, routes to planning or implementation.

**Inputs:**
```yaml
inputs:
  work_item_id:
    type: number
    required: true
    description: ADO work item ID (any type at any level)
  intent:
    type: string
    default: resume
    description: "new | redo | resume"
  user_plan_path:
    type: string
    default: ""
    description: "Path to user-authored plan document"
```

**Flow:**
```
preflight_check (script: preflight-check.ps1)
  → ready=false → preflight_gate (human gate) → retry/abort
  → ready=true →
state_detector (script: detect-state.ps1)
  → phase=needs_planning → sub_workflow: twig-sdlc-v2-planning
  → phase=needs_seeding → sub_workflow: twig-sdlc-v2-planning (seeds only)
  → phase=ready_for_implementation → sub_workflow: twig-sdlc-v2-implement
  → phase=in_progress → sub_workflow: twig-sdlc-v2-implement (resume)
  → phase=ready_for_completion → sub_workflow: close-out
  → phase=done → $end
  → phase=removed → $end
```

**Key difference from v1:** No type-specific routing. The `detect-state.ps1` script calls `polyphony route` which returns facet-based phase decisions. The root workflow routes purely on phase, not type.

#### 2. `twig-sdlc-v2-planning.yaml` — Planning Orchestration

**Responsibility:** Orchestrates the planning lifecycle for the root work item. Delegates recursive planning to `plan-level.yaml`.

**Flow:**
```
preflight_lite (script: preflight-lite.ps1)
  → ready=false → preflight_lite_gate → retry/abort
  → ready=true →
plan_level (sub_workflow: plan-level.yaml)
  inputs: work_item_id, intent, user_plan_path, depth=0, max_depth=6
  → planning complete →
seed_check (script: detect-state.ps1)
  → phase=needs_seeding → work_tree_seeder (agent)
  → phase=ready_for_implementation → $end (pass to implementation)
```

#### 3. `plan-level.yaml` — Recursive Planning (Core Innovation)

**Responsibility:** Plans a single level of the hierarchy. Self-invokes for child plannable levels.

**Self-Referencing Pattern (confirmed from conductor source):**
```yaml
agents:
  - name: plan_children
    type: workflow
    workflow: ./plan-level.yaml          # Self-reference
    max_depth: 6                         # Recursion cap (conductor enforces)
    input_mapping:
      work_item_id: "{{ child_router.output.child_id }}"
      depth: "{{ workflow.input.depth + 1 }}"
      max_depth: "{{ workflow.input.max_depth }}"
      intent: "{{ workflow.input.intent }}"
      user_plan_path: ""                 # User plan applies only to root level
```

**Inputs:**
```yaml
inputs:
  work_item_id:
    type: number
    required: true
  intent:
    type: string
    default: resume
  user_plan_path:
    type: string
    default: ""
  depth:
    type: number
    default: 0
  max_depth:
    type: number
    default: 6
```

**Flow:**
```
depth_guard (script: checks depth < max_depth)
  → depth >= max_depth → depth_exceeded_gate (human gate) → $end
  → depth < max_depth →

type_loader (script: reads .conductor/work-item-types/<type>.md and templates/<type>-template.md)
  →

route_check (script: polyphony route --work-item <id>)
  → phase=needs_planning →
      architect (agent: Opus 1M)
        prompt includes: type definition, template, user_plan (if provided)
      → open_questions_gate (conditional human gate)
      → parallel(technical_reviewer, readability_reviewer)
      → review_router
        → scores < 90 → architect (revise)
        → scores >= 90 → plan_approval (human gate)
          → approved → seeder (agent)
          → revise → architect (revise)
          → reject → $end
  → phase=needs_seeding →
      seeder (agent: seeds children from plan)
  → already planned → skip

child_router (script: polyphony hierarchy → find plannable children)
  → has plannable children →
      for_each: plan_children_group
        source: child_router.output.plannable_children
        as: child
        max_concurrent: 3
        failure_mode: continue_on_error
        agent:
          type: workflow
          workflow: ./plan-level.yaml
          max_depth: 6
          input_mapping:
            work_item_id: "{{ child.id }}"
            depth: "{{ workflow.input.depth + 1 }}"
            max_depth: "{{ workflow.input.max_depth }}"
  → no plannable children → $end
```

**This single YAML replaces:**
- `plan-design.yaml` (Epic-level planning)
- `plan-child.yaml` (child Issue planning)
- `plan-issue.yaml` (Issue-level planning)
- `task-decomposition.yaml` (Issue → Task decomposition)

**Type injection mechanism:** The `type_loader` script reads the work item's type from the hierarchy, loads `work-item-types/<type>.md` and `work-item-types/templates/<type>-template.md`, and injects them into the architect agent's prompt context. This is how the architect knows the semantic meaning and decomposition guidance for any type without hardcoding.

#### 4. `twig-sdlc-v2-implement.yaml` — Implementation Orchestration

**Responsibility:** Manages the PG lifecycle with full parallel support.

**Flow:**
```
preflight_lite (script)
  → ready=false → gate → retry/abort
  → ready=true →

load_work_tree (script: load-work-tree.ps1)
  → outputs: pr_groups, completed_pgs, pending_pgs, next_pg

pg_dispatcher (script: routes to PG execution)
  → all PGs complete → sub_workflow: feature-pr.yaml
  → pending PGs exist →
      for_each: pg_execution_group
        source: pg_dispatcher.output.pending_pgs
        as: pg
        max_concurrent: 3
        failure_mode: fail_fast
        agent:
          type: workflow
          workflow: ./implement-pg.yaml
          input_mapping:
            pg_number: "{{ pg.number }}"
            work_item_ids: "{{ pg.work_item_ids | json }}"
            branch_name: "{{ pg.branch_name }}"
            feature_branch: "{{ workflow.input.feature_branch }}"
      → all PGs done → sub_workflow: feature-pr.yaml
```

**Parallel PG execution:** The `pg_dispatcher` script emits the set of pending PGs. The workflow launches one `implement-pg.yaml` sub-workflow per pending PG in parallel. Each PG operates on its own branch independently. Merge conflicts are resolved by the implementing agent via rebase.

#### 5. `implement-pg.yaml` — Single PG Lifecycle

**Responsibility:** Implements all tasks in a single PG, creates and merges a PG PR.

**Flow:**
```
pg_router (script: pg-router.ps1)
  → action=create_branch → branch_manager (script: create/checkout PG branch)
  → action=submit_pr → pr_submit
  → action=all_complete → $end

task_loop:
  task_router (script: impl-router.ps1)
    → action=implement_task →
        coder (agent: Opus 1M) → reducer_code (agent) → task_reviewer (agent)
          → approved → task_completer (script: twig state Done)
            → more tasks → task_loop
          → changes_requested → coder (fix) → task_reviewer
    → action=all_tasks_done →
        dependency_check (script: check ADO predecessor links)
          → blocked → dependency_gate (human gate: wait/override/reassign)
          → not blocked →
            reducer_issue (agent) → issue_reviewer (agent)
              → approved → user_acceptance (conditional human gate)
                → accepted → next impl/issue
                → changes → task_loop
              → changes_requested → task_loop

pr_submit:
  reducer_pr (agent) → pr_creator (agent: gh pr create)
    → sub_workflow: github-pr.yaml (or ado-pr.yaml)
      → pr merged → scope_closer (script: scope-closer.ps1)
```

#### 6. `github-pr.yaml` — GitHub PR Lifecycle

**Responsibility:** Platform-specific PR review, fix, and merge cycle for GitHub.

**Flow:**
```
pr_reviewer (agent: Opus 1M, reviews via gh api)
  → approved → pr_merger (agent: gh pr merge)
  → changes_requested → pr_fixer (agent: Sonnet, addresses feedback)
    → pr_reviewer (re-review loop, max 10 iterations per P7)
```

**Interface contract:** Both `github-pr.yaml` and `ado-pr.yaml` accept the same inputs (`pr_number`, `branch_name`, `target_branch`, `review_policy`) and produce the same outputs (`merged: bool`, `pr_url: string`). This enables the implementation workflow to choose the platform sub-workflow based on `process-config.yaml`'s `platform` field.

#### 7. `ado-pr.yaml` — ADO PR Lifecycle (Stub)

**Responsibility:** Placeholder for Azure DevOps PR operations.

**Implementation:** Returns an error indicating ADO PR support is not yet implemented, with a human gate offering to proceed with manual PR management or abort.

#### 8. `feature-pr.yaml` — Feature PR + Remediation

**Responsibility:** Creates the feature PR (all PGs merged → feature branch → target branch) and handles remediation cycles.

**Flow:**
```
feature_pr_creator (agent: creates feature PR to target branch)
  → sub_workflow: github-pr.yaml (or ado-pr.yaml)
    → approved → $end (pass to close-out)
    → changes_requested →
        remediation_planner (agent: creates addendum plan)
          → remediation_seeder (agent: creates new work items)
          → sub_workflow: implement-pg.yaml (remediation PG)
            → PR merged → feature_pr_updater (agent: re-request review)
              → sub_workflow: github-pr.yaml (re-review)
```

**Remediation cycle:** When the human reviewer requests changes on the feature PR, the workflow creates a plan addendum (new doc referencing the original plan), seeds new work items for the remediation, executes them as a new PG branch targeting the feature branch, merges the remediation PG, and re-requests review on the feature PR. This cycle repeats until the feature PR is approved.

#### 9. `close-out.yaml` — Close-Out + Filing

**Responsibility:** Post-implementation close-out: transitions root to Done, generates observations, files them.

**Flow:**
```
close_out (agent: Opus 1M)
  → observations generated →
    closeout_filer (agent: Sonnet, creates ADO Issue for observations)
      → $end
```

### Data Flow

```
User Input (work_item_id, intent, user_plan_path)
  │
  ▼
detect-state.ps1 ──→ polyphony route ──→ { phase, action, workspace_hint }
  │
  ▼ (phase routes to sub-workflow)
  │
  ├── needs_planning ──→ plan-level.yaml
  │     │
  │     ├── polyphony hierarchy ──→ { type, facets, children }
  │     ├── .conductor/work-item-types/<type>.md ──→ architect prompt context
  │     ├── architect agent ──→ .plan.md artifact
  │     ├── seeder agent ──→ ADO work items created
  │     └── recursive: plan-level.yaml for each plannable child
  │
  ├── ready_for_implementation ──→ implement-pg.yaml (×N parallel)
  │     │
  │     ├── pg-router.ps1 ──→ { current_pg, branch_name, task_ids }
  │     ├── impl-router.ps1 ──→ { task_id, task_title, branch_name }
  │     ├── coder agent ──→ code changes committed
  │     ├── reviewer agents ──→ approval/changes
  │     ├── github-pr.yaml ──→ PR created, reviewed, merged
  │     └── scope-closer.ps1 ──→ ADO items transitioned to Done
  │
  └── all PGs complete ──→ feature-pr.yaml
        │
        ├── feature PR created (feature branch → target)
        ├── human review ──→ approved OR changes_requested
        └── changes_requested ──→ remediation cycle (loops)
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Single recursive `plan-level.yaml` | Self-invoking sub-workflow (`type: workflow`, `workflow: ./plan-level.yaml`, `max_depth: 6`) | Eliminates 4 type-specific YAMLs; handles any depth of plannable hierarchy with one definition. Conductor's `max_depth` field provides built-in recursion protection. |
| Parallel PG execution | Conductor `for_each` group | Maximizes throughput; PGs are independent by design (separate branches). `for_each` handles dynamic N (number of PGs determined at runtime by `pg_dispatcher` script). |
| Platform-specific PR sub-workflows | Separate `github-pr.yaml` / `ado-pr.yaml` | Clean interface contract; platform logic is isolated; ADO can be implemented later without touching implementation workflow |
| Feature PR as separate workflow | `feature-pr.yaml` with remediation loop | Separates PG-level PRs (auto-merge) from feature-level PRs (human review); remediation is a self-contained cycle |
| Depth guard in `plan-level.yaml` | Script checks `depth < max_depth` + conductor `max_depth` enforcement | Defense in depth: script provides human-readable gate; conductor provides hard limit |
| Type injection via file loading | Script reads `.conductor/work-item-types/<type>.md` | No type names in YAML; type knowledge is in config files, injected dynamically |
| Sub-workflow input passing | `input_mapping` with Jinja2 expressions | Enables parent context to flow into child sub-workflows with explicit field mapping; supports dynamic values from script outputs |
| v2 registry name | `twig-sdlc-v2-full@twig` | Parallel deployment alongside v1; config-presence gated adoption |
| Workflow location | `twig-conductor-workflows` repo | Follows existing pattern; workflows registered via conductor registry |
| Dependency gates | Script checks ADO predecessor links | P8: deterministic check; P6: human gate only for genuine decisions |

## Dependencies

### External Dependencies

- **Conductor CLI** — workflow execution engine (confirmed support: `type: workflow` sub-workflows with self-referencing + `max_depth`, `for_each` dynamic parallel groups, `parallel` static groups, `human_gate` with options, `script` agents, `input_mapping` for sub-workflow input passing, `!file` tag for external prompt includes)
- **Polyphony CLI** — `route`, `validate`, `hierarchy` commands (completed in Phases 1-2)
- **twig CLI** — work item cache, state transitions, sync
- **gh CLI** — GitHub PR operations (create, review, merge)
- **Git** — branch management, worktree support

### Internal Dependencies

- **Phase 1: Polyphony Core Engine** — ✅ Complete (provides `route`, `validate`, `hierarchy`)
- **Phase 2: Generic Workflow Scripts** — ✅ Complete (provides `detect-state.ps1`, `pg-router.ps1`, `impl-router.ps1`, `scope-closer.ps1`, `load-work-tree.ps1`)
- **`.conductor/` config** — ✅ Complete (provides `process-config.yaml`, `work-item-types/`, `templates/`)
- **`twig-conductor-workflows` repo** — Existing repo where v2 YAMLs will be added

### Sequencing Constraints

1. Issue 3.1 (root workflow) must be created first — it defines the entry point and input contract.
2. Issues 3.2 (planning) and 3.3 (implementation) can proceed in parallel after 3.1.
3. Issue 3.4 (feature PR + remediation) depends on both 3.3 (implementation) and 3.2 (planning, for remediation planning).
4. The polyphony-sdlc SKILL.md update depends on all YAMLs being complete.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Conductor recursion depth exceeds 10 | Low | High | Depth guard script in `plan-level.yaml`; `max_depth: 6` on sub-workflow agent; conductor global limit is 10 (C3) |
| Parallel PG merge conflicts | Medium | Medium | PG branches are independent; agents resolve via rebase; retry budget with human escalation |
| Feature PR remediation loop doesn't terminate | Low | Medium | Cap remediation cycles (max 3); human gate escalation after cap |
| ADO PR stub blocks users expecting ADO support | Low | Low | Clear error message with human gate offering manual PR management |
| `for_each` group with `type: workflow` agents | Low | Medium | Confirmed `for_each` supports inline agent definitions; if workflow-type agents aren't supported inside `for_each`, fall back to script-driven sequential invocation or a trampoline pattern |

## Open Questions

| # | Question | Severity | Status | Notes |
|---|----------|----------|--------|-------|
| OQ1 | Does conductor support self-referencing sub-workflows (plan-level.yaml invoking plan-level.yaml)? | ~~Moderate~~ | **Resolved** | **Yes.** Confirmed from conductor source (`~/projects/conductor-fix/`). Sub-workflows use `type: workflow` + `workflow: ./plan-level.yaml`. Self-referencing is supported with `max_depth: N` (1-10) to prevent runaway recursion. The engine tracks `_subworkflow_depth` and enforces both per-agent `max_depth` and global `MAX_SUBWORKFLOW_DEPTH` (10). |
| OQ2 | What is the exact conductor YAML syntax for parallel sub-workflow invocation with dynamic inputs? | ~~Moderate~~ | **Resolved** | **`for_each` groups.** The `for_each:` top-level construct spawns N agent instances from a runtime array (`source: agent.output.array_field`). Each instance can be any agent type including `type: workflow`. The `input_mapping` field passes Jinja2-rendered values from parent context to sub-workflow inputs. `max_concurrent` controls parallelism (default 10). |
| OQ3 | Should the `type_loader` script be a new script or integrated into `detect-state.ps1`? | Low | Open | Leaning toward a new lightweight script (`load-type-context.ps1`) for separation of concerns. The output is only needed by the architect agent in plan-level.yaml. |

## Files Affected

> **Note:** All v2 workflow YAMLs are created in the `twig-conductor-workflows` registry repo
> (`PolyphonyRequiem/twig-conductor-workflows`). Supporting scripts and SKILL documentation
> are created in this repo (`PolyphonyRequiem/polyphony`). No existing files are modified.

### New Files

| File Path | Purpose |
|-----------|---------|
| `workflows/twig-sdlc-v2-full.yaml` | Root workflow: preflight → detect → route to planning/implementation/close-out |
| `workflows/twig-sdlc-v2-planning.yaml` | Planning orchestration: preflight → plan-level → seed check |
| `workflows/plan-level.yaml` | Recursive planning: architect → review → approve → seed → recurse for children |
| `workflows/twig-sdlc-v2-implement.yaml` | Implementation orchestration: load work tree → parallel PG dispatch |
| `workflows/implement-pg.yaml` | Single PG lifecycle: task loop → PR creation → merge → scope close |
| `workflows/github-pr.yaml` | GitHub PR lifecycle: review → fix → merge |
| `workflows/ado-pr.yaml` | ADO PR lifecycle stub (returns error with human gate) |
| `workflows/feature-pr.yaml` | Feature PR creation + remediation cycle |
| `workflows/close-out.yaml` | Close-out: observations + filing |
| `scripts/load-type-context.ps1` | Loads type definition + template for architect prompt injection |
| `scripts/depth-guard.ps1` | Validates recursion depth is within budget |
| `scripts/dependency-check.ps1` | Checks ADO predecessor links for blocked items |
| `scripts/feature-pr-creator.ps1` | Creates feature PR from feature branch to target |
| `.github/skills/polyphony-sdlc/SKILL.md` | Updated SKILL documentation for v2 workflows (new file in polyphony repo) |

### Modified Files

| File Path | Changes |
|-----------|---------|
| *None* | No existing files are modified per Epic scope |

## ADO Work Item Structure

This Epic (#2583) is decomposed into 4 Issues, each with concrete Tasks.

### Issue 3.1: Root Workflow (`twig-sdlc-v2-full.yaml`)

**Goal:** Create the v2 root workflow that accepts any work item type and routes based on Polyphony phase detection, with `user_plan_path` as a first-class input.

**Prerequisites:** None (first Issue to implement).

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 3.1.1 | Create `twig-sdlc-v2-full.yaml` with inputs (work_item_id, intent, user_plan_path), preflight_check script node, and state_detector script node | `workflows/twig-sdlc-v2-full.yaml` | 3h |
| 3.1.2 | Implement phase-based routing in root YAML: route to planning, implementation, close-out, or end based on detect-state.ps1 output | `workflows/twig-sdlc-v2-full.yaml` | 2h |
| 3.1.3 | Add preflight_gate human gate with retry/proceed/abort options using Jinja2 template | `workflows/twig-sdlc-v2-full.yaml` | 1h |
| 3.1.4 | Create `close-out.yaml` sub-workflow with close_out agent and closeout_filer agent | `workflows/close-out.yaml` | 2h |
| 3.1.5 | Validate root and close-out YAMLs via `conductor validate` | N/A (validation) | 1h |

**Acceptance Criteria:**
- [ ] `twig-sdlc-v2-full.yaml` accepts `work_item_id`, `intent`, and `user_plan_path` inputs
- [ ] Routing is purely phase-based (no type-name literals in YAML)
- [ ] `conductor validate` passes for both workflow files
- [ ] Preflight gate renders correctly with human-readable template

### Issue 3.2: Recursive Planning Sub-Workflow

**Goal:** Create the recursive planning system that handles any plannable level with a single `plan-level.yaml`, replacing 4 type-specific planning workflows.

**Prerequisites:** Issue 3.1 (root workflow defines entry point).

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 3.2.1 | Create `twig-sdlc-v2-planning.yaml` orchestration workflow with preflight_lite, plan_level sub-workflow invocation, and seed_check | `workflows/twig-sdlc-v2-planning.yaml` | 2h |
| 3.2.2 | Create `plan-level.yaml` with depth_guard, type_loader, route_check, architect agent, review pipeline, and child_router | `workflows/plan-level.yaml` | 6h |
| 3.2.3 | Create `scripts/load-type-context.ps1` that reads `.conductor/work-item-types/<type>.md` and `templates/<type>-template.md` for a given work item ID | `scripts/load-type-context.ps1` | 2h |
| 3.2.4 | Create `scripts/depth-guard.ps1` that validates current recursion depth against max_depth | `scripts/depth-guard.ps1` | 1h |
| 3.2.5 | Implement self-recursion for child plannable levels: child_router script discovers plannable children via `polyphony hierarchy`, plan-level.yaml invokes itself per child with depth+1 | `workflows/plan-level.yaml` | 3h |
| 3.2.6 | Add architect agent prompt with type definition injection, user_plan_path support, and decomposition_guidance from process-config.yaml | `workflows/plan-level.yaml` | 2h |

**Acceptance Criteria:**
- [ ] Single `plan-level.yaml` handles Epic planning, Issue planning, and Task decomposition
- [ ] Recursion stops at depth 6 with human gate notification
- [ ] Architect agent receives type-specific definitions and templates in its prompt
- [ ] User plan is refined (not discarded) when `user_plan_path` is provided
- [ ] `conductor validate` passes for all planning workflow files
- [ ] No type-name literals in any YAML file

### Issue 3.3: Implementation Sub-Workflow with Parallel PGs

**Goal:** Create the implementation orchestration with full parallel PG support and platform-specific PR sub-workflows.

**Prerequisites:** Issue 3.1 (root workflow defines entry point).

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 3.3.1 | Create `twig-sdlc-v2-implement.yaml` with preflight_lite, load_work_tree script, and pg_dispatcher for parallel PG routing | `workflows/twig-sdlc-v2-implement.yaml` | 3h |
| 3.3.2 | Create `implement-pg.yaml` with task loop (task_router → coder → reviewer), issue review cycle, and PG PR creation | `workflows/implement-pg.yaml` | 6h |
| 3.3.3 | Create `github-pr.yaml` with PR reviewer, fixer loop (max 10 iterations), and merger agents | `workflows/github-pr.yaml` | 3h |
| 3.3.4 | Create `ado-pr.yaml` stub with error message and human gate for manual PR management | `workflows/ado-pr.yaml` | 1h |
| 3.3.5 | Create `scripts/dependency-check.ps1` that checks ADO predecessor links and outputs blocked/not_blocked with blocking item details | `scripts/dependency-check.ps1` | 2h |
| 3.3.6 | Add dependency gate human gate in `implement-pg.yaml` with wait/override/reassign options | `workflows/implement-pg.yaml` | 1h |

**Acceptance Criteria:**
- [ ] Parallel PG execution works via conductor parallel node
- [ ] `github-pr.yaml` handles full PR lifecycle (create, review, fix, merge)
- [ ] `ado-pr.yaml` returns clear error with actionable human gate
- [ ] Dependency gates fire for items with unresolved predecessor links
- [ ] Platform selection is driven by `process-config.yaml` platform field
- [ ] `conductor validate` passes for all implementation workflow files

### Issue 3.4: Feature PR and Remediation

**Goal:** Create the feature PR workflow with remediation cycle support and update the polyphony-sdlc SKILL documentation.

**Prerequisites:** Issues 3.2 (planning, for remediation addendum planning) and 3.3 (implementation, for remediation PG execution).

**Tasks:**

| Task ID | Description | Files | Effort |
|---------|-------------|-------|--------|
| 3.4.1 | Create `feature-pr.yaml` with feature PR creation (all PGs merged → feature branch → target) | `workflows/feature-pr.yaml` | 3h |
| 3.4.2 | Implement remediation cycle in `feature-pr.yaml`: changes_requested → remediation_planner → remediation_seeder → implement-pg → re-request review | `workflows/feature-pr.yaml` | 4h |
| 3.4.3 | Create `scripts/feature-pr-creator.ps1` that creates a feature PR using workspace_hint branch names | `scripts/feature-pr-creator.ps1` | 2h |
| 3.4.4 | Add remediation cycle cap (max 3) with human gate escalation after cap | `workflows/feature-pr.yaml` | 1h |
| 3.4.5 | Create/update `.github/skills/polyphony-sdlc/SKILL.md` with v2 workflow documentation, inputs, phases, and agent summary | `.github/skills/polyphony-sdlc/SKILL.md` | 3h |
| 3.4.6 | End-to-end validation: `conductor validate` on all v2 YAMLs, verify registration as `twig-sdlc-v2-full@twig` | N/A (validation) | 2h |

**Acceptance Criteria:**
- [ ] Feature PR is created after all PGs merge into the feature branch
- [ ] Remediation cycle creates addendum plan, new work items, remediation PG, and re-requests review
- [ ] Remediation capped at 3 cycles with human gate escalation
- [ ] SKILL.md documents all v2 workflows, inputs, agents, and phases
- [ ] All v2 YAMLs pass `conductor validate`
- [ ] v2 workflow registers as `twig-sdlc-v2-full@twig` alongside existing v1

## PR Groups

PR groups cluster Tasks for reviewable PRs. Sized for ≤2000 LoC and ≤50 files each.

### PG-1: Root & Close-Out Workflows
**Type:** Deep (few files, complex routing logic)
**Tasks:** 3.1.1, 3.1.2, 3.1.3, 3.1.4, 3.1.5
**Estimated LoC:** ~600 (2 YAML files with routing, gate templates, agent prompts)
**Successors:** PG-2, PG-3

### PG-2: Recursive Planning Suite
**Type:** Deep (complex recursive self-invocation, type injection)
**Tasks:** 3.2.1, 3.2.2, 3.2.3, 3.2.4, 3.2.5, 3.2.6
**Estimated LoC:** ~1200 (2 YAML files + 2 scripts with recursive planning logic, architect prompts, review pipeline)
**Predecessors:** PG-1
**Successors:** PG-4

### PG-3: Implementation & PR Workflows
**Type:** Deep (parallel PG dispatch, task lifecycle, PR sub-workflows)
**Tasks:** 3.3.1, 3.3.2, 3.3.3, 3.3.4, 3.3.5, 3.3.6
**Estimated LoC:** ~1400 (4 YAML files + 1 script with PG lifecycle, PR lifecycle, dependency checking)
**Predecessors:** PG-1
**Successors:** PG-4

### PG-4: Feature PR, Remediation & Documentation
**Type:** Deep (remediation cycle, SKILL documentation)
**Tasks:** 3.4.1, 3.4.2, 3.4.3, 3.4.4, 3.4.5, 3.4.6
**Estimated LoC:** ~1000 (1 YAML file + 1 script + SKILL.md update with feature PR logic, remediation cycle, full documentation)
**Predecessors:** PG-2, PG-3

## Execution Plan

### PR Group Summary

| Group | Name | Issues/Tasks | Dependencies | Type |
|-------|------|--------------|--------------|------|
| PG-1 | root-and-closeout | Issue 3.1 / Tasks 3.1.1–3.1.5 | None | Deep |
| PG-2 | recursive-planning | Issue 3.2 / Tasks 3.2.1–3.2.6 | PG-1 | Deep |
| PG-3 | implementation-and-pr | Issue 3.3 / Tasks 3.3.1–3.3.6 | PG-1 | Deep |
| PG-4 | feature-pr-remediation-docs | Issue 3.4 / Tasks 3.4.1–3.4.6 | PG-2, PG-3 | Deep |

### Execution Order

**Phase 1 — PG-1 (serial prerequisite):** Implement `twig-sdlc-v2-full.yaml` and `close-out.yaml`. This establishes the root entry point, input contract (`work_item_id`, `intent`, `user_plan_path`), preflight gate, phase-based routing stubs, and the close-out sub-workflow. `conductor validate` is scoped to these two files. All downstream PGs depend on the root being merged first.

**Phase 2 — PG-2 and PG-3 (parallel):** Once PG-1 is merged, PG-2 and PG-3 can proceed concurrently on separate branches. PG-2 delivers the recursive planning suite (`twig-sdlc-v2-planning.yaml`, `plan-level.yaml`, `load-type-context.ps1`, `depth-guard.ps1`). PG-3 delivers the implementation orchestration (`twig-sdlc-v2-implement.yaml`, `implement-pg.yaml`, `github-pr.yaml`, `ado-pr.yaml`, `dependency-check.ps1`). Each set is self-contained within its branch and does not modify files owned by the other.

**Phase 3 — PG-4 (serial finisher):** After both PG-2 and PG-3 are merged, PG-4 delivers `feature-pr.yaml`, `feature-pr-creator.ps1`, and the `SKILL.md` documentation update. This PG references `github-pr.yaml` and `implement-pg.yaml` from PG-3 and the planning context from PG-2. End-to-end `conductor validate` across all v2 YAMLs runs here.

### Dependency Topology

```
PG-1 (root + close-out)
  ├─► PG-2 (recursive planning)   ─┐
  └─► PG-3 (implementation + PR)  ─┴─► PG-4 (feature PR + remediation + docs)
```

### Validation Strategy

| PG | Validation Scope | Pass Criteria |
|----|-----------------|---------------|
| PG-1 | `conductor validate workflows/twig-sdlc-v2-full.yaml workflows/close-out.yaml` | No errors; phase routing conditions are syntactically valid; gate template renders |
| PG-2 | `conductor validate workflows/twig-sdlc-v2-planning.yaml workflows/plan-level.yaml`; script unit test (`depth-guard.ps1`, `load-type-context.ps1` with mock type files) | No errors; recursion self-reference resolves; no type-name literals |
| PG-3 | `conductor validate workflows/twig-sdlc-v2-implement.yaml workflows/implement-pg.yaml workflows/github-pr.yaml workflows/ado-pr.yaml`; `dependency-check.ps1` dry-run | No errors; `for_each` PG dispatch accepted; PR interface contract verified |
| PG-4 | `conductor validate` on all 9 v2 YAML files; SKILL.md review for completeness | All pass; remediation cycle cap enforced; `twig-sdlc-v2-full@twig` registration confirmed |

### Self-Containment Assessment

All four PR groups are self-contained. The work is purely additive (no existing files modified per G9), so merging any PG in isolation leaves the existing `twig-sdlc-full` workflow fully operational. Sub-workflow references in earlier PGs point to files delivered in later PGs, but those references are not resolved until the full v2 suite is invoked — the partially-deployed state is safe because the v2 entry point is not registered until the final registration step in task 3.4.6.

## References

- [Type-Agnostic SDLC Plan](type-agnostic-sdlc.plan.md) — Parent Epic implementation plan with full architecture
- [Polyphony Core Engine Plan](polyphony-core-engine.plan.md) — Phase 1 implementation (completed)
- [Conductor Design Principles](../../.github/skills/conductor-design/SKILL.md) — P1-P13 governing workflow design
- [Process Config](../../.conductor/process-config.yaml) — Type facets, transitions, review policies, branch strategy
- [Work Item Type Definitions](../../.conductor/work-item-types/) — Epic, Issue, Task semantic definitions
- [twig-conductor-workflows repo](https://github.com/PolyphonyRequiem/twig-conductor-workflows) — Target repo for v2 YAML files


