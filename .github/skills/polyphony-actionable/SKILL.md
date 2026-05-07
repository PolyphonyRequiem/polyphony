---
name: polyphony-actionable
description: >-
  Activate when authoring, modifying, or invoking the actionable.yaml
  conductor workflow — the workflow that satisfies the actionable facet
  of a single work item by either driving polyphony to produce evidence
  or routing to a human-only satisfaction gate. Covers the executor
  router, the polyphony leg (evidence branch + agent + evidence PR +
  floor check + reviewer + merge), the human leg (satisfaction gate
  only), and which Phase 6 wiring is intentionally deferred to PRs
  #5/#8. Companion to the ADR at
  `docs/decisions/actionable-executor-split.md` and the glossary
  "Workflows" section.
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
        compose_addendum (script — agent compose-addendum verb)
              │                                  │
        actionable_agent (LLM, opus-4.6)         │
              │                                  │
        open_evidence_pr                         │
              │                                  │
        evidence_floor_check (script)            │
         ├─pass──────> evidence_reviewer         │
         ├─violation─> floor_failed_gate         │
         │             ├─abort──> workflow_abandoned
         │             ├─retry──> actionable_agent
         │             └─manual_complete──> workflow_completed
         └─gh_failed─> workflow_error_gate       │
                                                 │
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

After PR #5 + PR #7 shipped, only one `TODO(p6-prN)` marker remains —
`lint-actionable.ps1` enforces both that it stays present until its
follow-up PR lands, and that the already-shipped markers stay
absent (their reappearance signals a botched revert):

| Marker          | Lands in | What it gates                                        |
|-----------------|----------|------------------------------------------------------|
| `TODO(p6-pr8)`  | PR #8    | Full evidence-judgment rubric for `evidence_reviewer` (currently a stub). |

When you remove a marker as part of its follow-up PR, also remove the
corresponding entry from `lint-actionable.ps1` — or the lint will
flag the now-stale check. The lint also enforces the inverse: shipped
TODO markers (e.g. `TODO(p6-pr5)`, `TODO(p6-pr7)`) MUST be absent from
the YAML; their reappearance signals a botched revert.

## Evidence floor check (Phase 6 PR #7)

`evidence_floor_check` is a **strict mechanical** pre-reviewer bar
between `open_evidence_pr` and `evidence_reviewer`. It exists to
catch "the agent crashed before producing anything" misfires before
the LLM reviewer is asked to judge an empty PR. Two violations,
listed in declaration order:

| Violation     | Trigger                                                         |
|---------------|-----------------------------------------------------------------|
| `no_commits`  | `commit_count < min_commits` (default 1) on the head branch.    |
| `empty_body`  | PR body is empty or whitespace-only after `.Trim()`.            |

The verb is **routing-style** — always exits 0; outcomes are
conveyed via the JSON envelope:

- **Pass:** `success=true, passes_floor=true, violations=[]` → route
  to `evidence_reviewer`.
- **Violation:** `success=true, passes_floor=false, violations=[...]`
  → route to `floor_failed_gate` (human gate offering
  `abort` / `retry` / `manual_complete`).
- **Transport failure:** `success=false, error_code=...`
  (`pr_not_found` or `gh_failed`) → route to `workflow_error_gate`.

Per Phase 6 design sketch pick #6, the floor is mechanical only. **Do
not extend `check-evidence-floor` with content-quality checks** —
that judgment belongs to the LLM reviewer alone.

PR #5 (this PR) wired `compose_addendum` + the prompt-text injection
that replaced the `TODO(p6-pr5)` placeholder. The lint now ASSERTS
the marker is gone (a "stale-deferred-wiring-todo" check fails the
build if it reappears) and that `actionable_agent`'s prompt template
references `compose_addendum.output.*`.

## How the addendum gets composed

The `compose_addendum` step shells out to
`polyphony agent compose-addendum {{ workflow.input.work_item_id }}`
(see `src/Polyphony/Commands/AgentCommands.ComposeAddendum.cs`). The
verb:

1. Looks up the work item in the local twig cache.
2. Reads the type's facet set from `process-config.yaml`.
3. Loads `policy.yaml` and resolves the `guidance` rule for the item's
   type (per PR #6's policy resolver).
4. Extracts the per-item guidance via `polyphony.Guidance.GuidanceExtractor`.
5. Calls `FacetProfileComposer.Compose(facets, profiles, perItemGuidance)`.
6. Emits a routing-style JSON envelope: `{ work_item_id, facets,
   skills, mcps, guidance, guidance_present, error?, error_code? }`.

The verb ALWAYS exits 0; failures surface via `error_code` (one of
`invalid_argument`, `work_item_not_found`, `type_unknown`,
`invalid_facet_profile_config`, `guidance_misconfigured`,
`cache_error`). The workflow's `compose_addendum` step routes on
`output.error` to `workflow_error_gate`.

`actionable_agent` consumes the envelope via Jinja2 prompt-text
injection — `{% if compose_addendum is defined %}…{% endif %}` blocks
for skills, MCPs, and guidance. Conductor's agent-step schema does
NOT expose structured `skills:` / `mcps:` / `prompt_addendum:`
fields, so prompt-text injection is the supported mechanism for the
addendum to reach the agent. The static `tools:` list on
`actionable_agent` controls which conductor MCP servers are
connected; the dynamic skills/MCPs the composer surfaces are
advisory context the agent reads from its prompt.

## Verbs the workflow shells out to

- `polyphony branch ensure-evidence-branch <workItemId> [--apex-id N] [--from-ref ref] [--remote origin]`
  — emits `{ branch, base_branch, action, apex_id, item_id, orphan, from_ref, error? }`.
- `polyphony agent compose-addendum <workItem> [--policy path]`
  — emits `{ work_item_id, facets, skills, mcps, guidance, guidance_present, error?, error_code? }`.
  Routing-style: always exits 0; errors surface via `error_code`. Composes
  the facet-profile-derived skills + MCPs the actionable_agent prompt
  injects, plus the per-item guidance extracted via the resolved policy.
- `polyphony pr open-evidence-pr <workItem> [--apex-id N] [--head X] [--base-branch Y] [--title T] [--body B]`
  — emits `{ pr_number, pr_url, title, head_branch, base_branch, work_item_id, apex_id, created, error? }`.
  GH-only today; ADO sibling deferred per Phase 6 wave-0 pattern.
- `polyphony pr check-evidence-floor <prNumber> [--repo owner/repo] [--min-commits N]`
  — emits `{ success, pr_number, commit_count, body_length, passes_floor, violations[], error_code?, error_message? }`.
  Always exits 0 (routing-style envelope); GH-only.
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

## Tested behaviors (Phase 6 PR #8)

`.conductor/registry/tests/e2e-actionable.Tests.ps1` is the
end-to-end suite for the workflow. It complements (does not
duplicate) `lint-actionable.ps1` (structural presence) and
`tests/Polyphony.Tests/Infrastructure/RouteActionableExecutorScriptTests.cs`
(router-script JSON envelope). The suite parses `actionable.yaml`
into an in-memory graph (via `powershell-yaml`'s `ConvertFrom-Yaml`)
and asserts the following end-to-end behaviors:

**Polyphony executor leg:**
- `executor=polyphony` routes from `executor_router` to `ensure_evidence_branch`.
- The full happy path is reachable: `ensure_evidence_branch →
  compose_addendum → actionable_agent → open_evidence_pr →
  evidence_floor_check → evidence_reviewer (approve) →
  merge_evidence_pr → workflow_completed`.
- `evidence_floor_check`'s `passes_floor == false` route targets
  `floor_failed_gate` (NOT the LLM reviewer — design pick #6).
- `floor_failed_gate` exposes `abort` / `retry` / `manual_complete`
  with routes `workflow_abandoned` / `actionable_agent` /
  `workflow_completed`.
- `evidence_reviewer` routes `approve` → `merge_evidence_pr`,
  `request_changes` → `revise_loop_gate`, `block` →
  `workflow_error_gate`, with a catch-all to `workflow_error_gate`
  (M4 — missing/malformed decisions fail safely).
- `revise_loop_gate` exposes `retry` (→ `actionable_agent`) and
  `abandon` (→ `workflow_abandoned`).
- `compose_addendum` errors route to `workflow_error_gate` BEFORE
  the agent runs (the agent never sees a partial envelope).
- `merge_evidence_pr` only reaches `workflow_completed` when
  `merged == true`.
- `workflow_error_gate`'s `retry` re-enters at `executor_router`
  (operator can flip executor mid-recovery).

**Polyphony agent prompt threading:**
- The agent prompt template references `workflow.input.work_item_id`,
  `workflow.input.apex_id`, `ensure_evidence_branch.output.branch`,
  and `ensure_evidence_branch.output.base_branch`.
- The agent prompt template consumes every `compose_addendum` output
  field (`facets`, `skills`, `mcps`, `guidance`, `guidance_present`)
  — without this, PR #5's facet-profile composition would ship but
  never reach the agent.
- The revise-loop re-entry block is guarded by `revise_loop_gate is
  defined` and surfaces `evidence_reviewer.output.comment`.
- Both the agent and the reviewer are pinned to opus-class models.

**Human executor leg:**
- `executor=human` routes from `executor_router` to
  `human_satisfaction_gate`.
- `human_satisfaction_gate` exposes `satisfied` / `not_yet` /
  `abandoned` with routes `workflow_completed` /
  `human_satisfaction_gate` (self-loop) / `workflow_abandoned`.
- The human leg never transitively reaches a polyphony-only node
  (ensure_evidence_branch, compose_addendum, actionable_agent,
  open_evidence_pr, evidence_floor_check, floor_failed_gate,
  evidence_reviewer, revise_loop_gate, merge_evidence_pr) — leg
  disjointness is asserted by reachability analysis.

**Cross-leg coverage:**
- The router declares an unconditional catch-all route (last entry)
  targeting `workflow_error_gate`.
- Both terminals route to `$end`.
- The workflow output map guards every leg-specific reference with
  `is defined` (M3 — StrictUndefined safety when only one leg has
  produced an envelope).

**Router script (live execution under pwsh):**
- `executor=polyphony` / `executor=human` emit the documented
  envelope and exit 0.
- Default executor (`-WorkItemId N` with no `-Executor`) is
  `polyphony` — matches `workflow.input.executor.default`.
- Unknown executor values populate `error` but the script still
  exits 0 (so the workflow's catch-all picks them up rather than
  halting the conductor run).
