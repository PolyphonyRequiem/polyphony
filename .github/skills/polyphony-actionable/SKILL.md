---
name: polyphony-actionable
description: >-
  Activate when authoring, modifying, or invoking the actionable.yaml
  conductor workflow — the workflow that satisfies the actionable facet
  of a single work item by either driving polyphony to produce evidence
  or routing to a human-only satisfaction gate. Covers the executor
  router, the polyphony leg (evidence branch + agent + evidence PR +
  reviewer + merge), the human leg (satisfaction gate only), and which
  Phase 6 wiring is intentionally deferred to PRs #5/#7/#8. Companion
  to the ADR at `docs/decisions/actionable-executor-split.md` and the
  glossary "Workflows" section.
user-invokable: false
---

# Polyphony Actionable Workflow Skill

The actionable workflow (`actionable.yaml`) satisfies the **actionable
facet** of one work item. It is one of three sibling
facet-specialized workflows alongside `plannable.yaml` and
`implementable.yaml`. Load this skill when touching `actionable.yaml`,
when adding inputs/outputs the workflow must thread, or when wiring
follow-up PRs that fill in the deferred slots flagged with
`TODO(p6-prN)` markers.

If you are authoring workflow YAMLs in general — not specifically the
actionable workflow — load **polyphony-workflow-author** instead.
If you are deciding whether to ship one workflow or two, read the ADR.

## Shape

```text
                          executor_router
                          (script — reads workflow.input.executor)
                                ├── polyphony ─┐
                                ├── human ─────┼─┐
                                └── error ─────┼─┼──> workflow_error_gate
                                               │ │
        ensure_evidence_branch ◄───────────────┘ │
              │                                  │
        actionable_agent (LLM, opus-4.6)         │
              │                                  │
        open_evidence_pr                         │
              │                                  │
        evidence_reviewer (LLM, opus-4.7)        │
         ├─approve──> merge_evidence_pr ─┐       │
         ├─request_changes─> revise_loop_gate    │
         │                     ├─retry─> actionable_agent
         │                     └─abandon─> workflow_abandoned
         └─block──> workflow_error_gate          │
                                                 │
                          human_satisfaction_gate ◄─┘
                                ├─satisfied─> workflow_completed
                                ├─not_yet───> human_satisfaction_gate
                                └─abandoned─> workflow_abandoned
```

## Executor values

`executor` is a **per-item** property on the actionable facet:

- **`polyphony`** — polyphony performs the action and produces
  evidence. The full polyphony leg fires.
- **`human`** — the action is outside polyphony's authority. Only the
  satisfaction gate fires; no evidence branch, agent, or PR.
- (Future) **`discussion`** etc. would slot in as a third route off
  the same `executor_router`. Add a route, not a workflow.

The router script
(`.conductor/registry/scripts/route-actionable-executor.ps1`) validates
the value against the allowed enum and surfaces unknown values via the
`error` field of its output envelope (per the routing-script
convention: always exit 0, route on `error`).

## Inputs the workflow expects

`work_item_id`, `apex_id`, `executor`, `platform`, `organization`,
`project`, `repository`, `from_ref`. All eight are pinned by
`lint-actionable.ps1`.

## Outputs it produces

`satisfied`, `executor`, `pr_url`, `pr_number`, `evidence_branch`.
Polyphony-leg-specific outputs (`pr_url`, `pr_number`,
`evidence_branch`) are guarded by `is defined` so the human leg
doesn't crash StrictUndefined evaluation.

## Phase 6 deferred wiring

Three `TODO(p6-prN)` markers are intentional placeholders that
`lint-actionable.ps1` enforces — removing them before the follow-up
PR lands is a CI failure:

| Marker          | Lands in | What it gates                                        |
|-----------------|----------|------------------------------------------------------|
| `TODO(p6-pr5)`  | PR #5    | Facet-profile composition + per-item guidance addendum on `actionable_agent`. |
| `TODO(p6-pr7)`  | PR #7    | Evidence floor check between agent and `open_evidence_pr`. |
| `TODO(p6-pr8)`  | PR #8    | Full evidence-judgment rubric for `evidence_reviewer` (currently a stub). |

When you remove a marker as part of its follow-up PR, also remove the
corresponding entry from `lint-actionable.ps1` — or the lint will
flag the now-stale check.

## Verbs the workflow shells out to

- `polyphony branch ensure-evidence-branch <workItemId> [--apex-id N] [--from-ref ref] [--remote origin]`
  — emits `{ branch, base_branch, action, apex_id, item_id, orphan, from_ref, error? }`.
- `polyphony pr open-evidence-pr <workItem> [--apex-id N] [--head X] [--base-branch Y] [--title T] [--body B]`
  — emits `{ pr_number, pr_url, title, head_branch, base_branch, work_item_id, apex_id, created, error? }`.
  GH-only today; ADO sibling deferred per Phase 6 wave-0 pattern.
- Inline `gh pr merge --squash --auto --delete-branch` in
  `merge_evidence_pr` (no `polyphony pr merge-evidence-pr` verb yet).

## Don'ts

- **Don't hardcode process-template type names** in the YAML — load
  the operator's allowed types from `process-config.yaml` at runtime.
  `lint-actionable.ps1` checks this (the lint and its sibling
  `tests/lint-type-agnostic.ps1` are the source of truth for the
  forbidden-literal set).
- **Don't reference cross-leg agent outputs without `is defined`** —
  StrictUndefined will fail the run. The human leg never sees
  `open_evidence_pr.output.*`.
- **Don't add an auto-cap on `revise_loop_gate`** — per P7, the human
  decides when to abandon a runaway loop. The same applies to
  `human_satisfaction_gate`'s `not_yet` self-loop.
- **Don't wire `evidence_reviewer.block` to auto-approve** — block
  always routes to the error gate so a human resolves it.

## Related

- ADR: `docs/decisions/actionable-executor-split.md`
- Glossary: `docs/glossary.md` — "Workflows" and "Work item — facet
  terms" sections.
- Workflow author conventions: **polyphony-workflow-author** skill.
- Branch naming: **polyphony-branch-model** skill.
