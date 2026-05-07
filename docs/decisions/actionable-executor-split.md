# Actionable Workflow — Executor Split via In-Workflow Router

> **Status:** Accepted. Phase 6 — actionable facet workflow scaffold.
> **Driver:** Phase 6 ships the `actionable.yaml` workflow; the
> actionable facet carries an `executor` property whose two values
> (`polyphony`, `human`) demand structurally different downstream
> machinery. We need a stable place for that branching to live.
> **Supersedes:** none — actionable is a new workflow.

## Context

The actionable facet is recorded on every actionable item (see the
[glossary entry](../glossary.md#work-item--facet-terms)) along with an
`executor` property:

- **`polyphony`** — polyphony performs the action and produces an
  evidence artifact. Requires the full chain of evidence machinery:
  `branch ensure-evidence-branch`, an LLM agent, `pr open-evidence-pr`,
  an evidence reviewer, and a merge step.
- **`human`** — the action is outside polyphony's authority (e.g.,
  manual prod approval, an out-of-band infra change). Requires only
  that polyphony block until a human confirms the action has been
  performed; no evidence branch, no agent, no PR.

The two paths overlap in **almost nothing** at the agent / step level —
yet they share the same workflow inputs (`work_item_id`, `apex_id`,
`platform`, etc.), the same workflow outputs (`satisfied`, `executor`),
and the same surrounding driver semantics (one workflow invocation per
actionable requirement). They are two legs of the same conceptual unit:
"satisfy this actionable item."

## Decision

**Ship one workflow with an in-workflow `executor_router` script that
routes between two legs.** Do *not* split into two separate workflows
(`actionable-polyphony.yaml` + `actionable-human.yaml`).

Concretely:

- `actionable.yaml` declares a single `executor_router` script-step as
  its entry point. The script reads `workflow.input.executor` and
  emits an envelope `{ executor, work_item_id, error }`. The workflow
  then routes:
  - `executor == 'polyphony'` → polyphony leg
    (`ensure_evidence_branch` → `actionable_agent` →
    `open_evidence_pr` → `evidence_reviewer` →
    `revise_loop_gate` / `merge_evidence_pr`).
  - `executor == 'human'` → human leg (`human_satisfaction_gate`
    only).
  - `else` → `workflow_error_gate`.
- Both legs converge on the same terminals (`workflow_completed`,
  `workflow_abandoned`).
- The workflow declares one consolidated `output:` map; only the
  branch that runs populates each output, and StrictUndefined-safe
  `is defined` guards keep the other branch's references benign.

## Consequences

**Positive**

- One driver call site. Drivers and PR-orchestration logic don't have
  to know which leg will fire; they invoke `actionable.yaml` and the
  workflow handles dispatch.
- One set of inputs/outputs to keep stable. Adding a new input (e.g.,
  `branch_prefix_override`) is one change, not two.
- Adding a third executor (e.g., `discussion` for items satisfied by
  recording a meeting outcome) is a third route off the same router —
  no new workflow file, no new entry point, no driver-side fan-out.
- Routing logic is **declarative in the YAML** rather than buried in
  driver code, which keeps the design auditable in the same place as
  the rest of the workflow shape.

**Negative / accepted**

- The YAML is larger than either leg alone would be. Mitigated by the
  `executor_router` placing each leg under a clearly-labelled comment
  banner, and by `lint-actionable.ps1` enforcing the structural
  contract.
- The polyphony leg's output references (`open_evidence_pr.output.*`,
  `ensure_evidence_branch.output.*`) only make sense when that leg
  ran. Mitigated by `is defined` guards on every cross-leg reference
  (per `conductor-mechanics` M3; pinned by
  `lint-strict-undefined.Tests.ps1`).
- A run that started on the wrong leg can't be "switched" mid-flight —
  the executor decision happens once, at entry. Acceptable: changing
  the executor is a backlog edit, not a workflow event.

## Alternatives considered

1. **Two workflows** (`actionable-polyphony.yaml` + `actionable-human.yaml`).
   Rejected — moves the routing into the driver and forces every
   downstream consumer (reporting, sprint-summary) to know about both
   files. Doubles the maintenance surface for a decision that is
   genuinely *one* branch on *one* property.
2. **Inline the router as a Jinja `route.when` chain on the first real
   step** (no script). Rejected — `executor` validation (rejecting
   unknown values) needs somewhere to live; a one-line script is
   clearer than a three-armed `when:` cascade with a fallthrough hack.
   Also keeps the precedent for future routers (e.g., `platform_router`
   in `feature-pr.yaml`).

## References

- Glossary: [Actionable facet](../glossary.md#work-item--facet-terms),
  [Actionable workflow / Executor / Human satisfaction gate](../glossary.md#workflows).
- Phase 6 design sketch (private session state).
- Companion script: `.conductor/registry/scripts/route-actionable-executor.ps1`.
- Lint: `.conductor/registry/tests/lint-actionable.ps1`.
- Pattern precedent: `feature-pr.yaml` `pr_platform_router` (script-step routing on a single input field).
