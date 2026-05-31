# Mahler — Conductor Gaps Report
**Date:** 2026-05-31T10:51:14-07:00  
**Author:** Mahler (Conductor Expert)  
**Requested by:** Daniel Green  
**Context:** Daniel's question: "on_error is supposed to be a huge cleanup right?" + top conductor gaps list  
**Sources:** Live inspection of polyphony workflows, conductor-dogfood source, and decisions.md Phase 2 scope (lines 923–1532)

---

## Part A: on_error Retrofit Assessment

### 1. Scope — What currently exists as error-handling sprawl

**Bottom line first:** across the 15 workflow YAML files in `.conductor/registry/workflows/`, after PR #535 removed 19 trivial gates, the codebase still carries **27 dedicated error-gate node definitions** spanning **8 workflow files**, with **42 routes** pointing into them, plus **87 `output.error` field-check conditions** in route `when:` clauses (the workaround pattern for the verb exit-0 problem).

#### Per-file breakdown (files with any error-handling sprawl)

| File | Lines | Error-gate defs | Routes to gates | `output.error` checks | TODO(AB#3257) |
|---|---:|---:|---:|---:|---:|
| `plan-level.yaml` | 3224 | **13** | **15** | 29 | 0 |
| `actionable.yaml` | 948 | 2 | 10 | 18 | 0 |
| `feature-pr.yaml` | 1278 | 3 | 4 | 5 | 0 |
| `implement-merge-group.yaml` | 2431 | 3 | 6 | 12 | 0 |
| `ado-pr.yaml` | 1625 | 2 | 3 | 7 | 1 |
| `github-pr.yaml` | 1481 | 1 | 1 | 2 | 1 |
| `polyphony.yaml` | 1620 | 2 | 2 | 12 | 0 |
| `restack-remedy.yaml` | 230 | 1 | 1 | 2 | 0 |
| **TOTAL** | | **27** | **42** | **87** | **2** |

**Notes on what's already done:** PR #535 removed 19 trivial human-gate error nodes from `ado-pr`, `github-pr`, `actionable`, `implement-merge-group`, `remedy-stale-descendant`. Those are now replaced with direct abort routes + TODO comments. The 27 gates in the table above are what STILL EXISTS post-#535.

#### Why plan-level.yaml is the worst offender

plan-level.yaml has **13 dedicated error-gate nodes occupying 581 lines** — **18% of the entire file**. These gates are:

| Gate name | Parent step | Lines | Retry available? |
|---|---|---:|---|
| `root_resolver_error_gate` | `root_resolver` | 23 | No (abort-only) |
| `type_loader_error_gate` | `type_loader` | 27 | No (abort-only) |
| `ancestor_chain_error_gate` | `ancestor_chain` | 46 | Yes |
| `state_detector_error_gate` | `state_detector` | 25 | Yes |
| `write_plan_error_gate` | `write_plan` | 35 | Yes |
| `ensure_plan_branch_error_gate` | `ensure_plan_branch` | 35 | Yes |
| `commit_and_push_error_gate` | `commit_and_push` | 39 | Yes |
| `open_plan_pr_error_gate` | `open_plan_pr` | 70 | Yes |
| `poll_error_gate` | `poll_status` | 53 | Yes (platform-router) |
| `merge_error_gate` | `merge_plan_pr` | 110 | Yes |
| `seeder_error_gate` | `seeder` | 49 | 3-way (retry/continue/abort) |
| `open_plan_pr_ado_error_gate` | `open_plan_pr_ado` | 39 | Yes |
| `merge_plan_pr_ado_error_gate` | `merge_plan_pr_ado` | 30 | Yes |

Every one of these follows the same mechanical pattern:
1. Script node's route: `when: "{{ nodename.output.error is defined and nodename.output.error }}"` → error gate
2. Error gate: `type: human_gate` with a Retry / Abort prompt
3. Gate options: `route: nodename` (retry) and `route: abort_run` (abort)

The `merge_error_gate` (110 lines) is the worst single node — it has an extended diagnostic prompt listing common merge failure reasons, but all routing paths lead to either retry or abort_run. No judgment required.

#### The try/catch duplication pattern

Every infrastructure script node in plan-level.yaml carries this duplicated routing structure:

```yaml
# CURRENT PATTERN (repeated 13 times in plan-level.yaml):
- name: some_step
  type: script
  routes:
    - to: some_step_error_gate
      when: "{{ some_step.output.error is defined and some_step.output.error }}"
    - to: next_step

- name: some_step_error_gate
  type: human_gate
  prompt: |
    ## ⚠️ Some Step Failed
    **Error:** {{ some_step.output.error ... }}
    Choose an action:
    - Retry
    - Abort
  options:
    - label: "🔁 Retry"
      route: some_step
    - label: "🛑 Abort"
      route: abort_run
```

This pattern is replicated nearly verbatim across `ado-pr.yaml`, `github-pr.yaml`, `feature-pr.yaml`, `implement-merge-group.yaml`, and `polyphony.yaml` as well. The only variation is the diagnostic context in the prompt.

---

### 2. Cleanup Math — Before/After Sketch

**Worked example: `write_plan_error_gate` in `plan-level.yaml`**

**BEFORE** (current code — 37 lines):
```yaml
  # In write_plan's routes block:
      - to: write_plan_error_gate
        when: "{{ write_plan.output.error is defined and write_plan.output.error }}"
      - to: commit_and_push

  # ── Write plan error gate ──────────────────────────────────────────────
  - name: write_plan_error_gate
    type: human_gate
    prompt: |
      ## ⚠️ Plan Write Failed

      Could not write the plan for work item
      **{{ workflow.input.work_item_id }}**.

      **Error:** {{ write_plan.output.error if write_plan.output.error is defined else "Unknown error" }}

      ---

      Choose an action:
      - **Retry** — re-run `polyphony plan write-plan` (idempotent)
      - **Abort** — halt the entire run (terminates the conductor process)
    options:
      - label: "🔁 Retry"
        value: retry
        route: write_plan
      - label: "🛑 Abort"
        value: abort
        route: abort_run
```

**AFTER Phase 1** (abort-only, no retry yet — 5 lines replacing the whole gate + its route):
```yaml
  # In write_plan's routes block:
      - to: commit_and_push           # success path (unchanged)
      - to: abort_run                 # Phase 1: catch-all abort on script error
        on_error: true
```
*(The `output.error` route check is replaced by the engine detecting non-zero exit or $CONDUCTOR_ERROR_OUT. The 35-line gate node is deleted entirely.)*

**AFTER Phase 1 + Phase 2** (retry restored — 7 lines):
```yaml
      - to: commit_and_push
      - on_error: true                # Phase 2: retry the node up to 3 times
        retry: { max: 3, backoff: exponential, initial_seconds: 5 }
      - to: abort_run                 # post-retry-exhaustion fallback
        on_error: true
```

**The arithmetic for plan-level.yaml:**

| Metric | Before | After Phase 1 | After Phase 1+2 |
|---|---:|---:|---:|
| Error-gate node lines | 581 | 0 | 0 |
| Route condition lines (per node) | 2 (with `when:`) | 2 (simpler) | 3 |
| **Net change** | **0** | **−567 lines** | **−541 lines** |
| **% of file** | **18%** | **−17.6%** | **−16.8%** |

**Cross-workflow total estimate:**
- 27 gate nodes averaging ~30 lines = ~810 lines of gate definitions eliminated
- ~42 `when: output.error` route conditions simplified = ~20 lines saved
- **Total savings: ~830 lines across 8 files** when Phase 1+2 is complete
- The 87 `output.error` checks in `when:` conditions are a separate category — most stay as success-path routing for polyphony verb calls (see §3 below)

---

### 3. Conductor-Side Cost — What's Needed to Make This Real

#### Status as of 2026-05-31

**Critical fact: PR #229 (error routing / `on_error:`) has NOT merged to `origin/main`.** The current upstream main is v0.1.18. The `on_error:` feature lives exclusively in the dogfood branch (`dogfood/on-error+notifications` at `e044d84`, base `efa520f` = v0.1.17).

Verified by inspecting `origin/main` commit log:
```
085b7a5 chore: release 0.1.18 (#233)   ← current upstream HEAD
23751a2 fix: external workflow friction (#232)
370209d feat(providers): structured runtime.provider config (#225)
8124e8e feat(engine): add type: terminate step (#219)
...
```
PR #229 (`feature/error-routing`) is not in this list.

**Blocker:** There is a known semantic conflict between PR #229's `context.py` changes and the v0.1.18 `is_dict_output` / `None`-seed initialization, documented as a PR comment on 2026-05-28 (see Mahler history). Until the upstream conductor team resolves the sentinel pattern question, the dogfood branch is pinned at pre-v0.1.18.

#### What Phase 1 (PR #229) actually provides

From direct inspection of `conductor-dogfood/src/conductor/config/schema.py` and `engine/errors.py`:

| Feature | Status | Notes |
|---|---|---|
| `on_error: true` (catch-all) on routes | ✅ Dogfood-only | `RouteDef.on_error: bool \| str \| list[str] \| None` |
| `on_error: "some.kind"` (exact match) | ✅ Dogfood-only | Dotted-lowercase KIND_PATTERN enforced |
| `on_error: ["k1", "k2"]` (OR match) | ✅ Dogfood-only | |
| `raises: [kind1, kind2]` on nodes | ✅ Dogfood-only | Optional contract enforcement |
| `$CONDUCTOR_ERROR_OUT` env var | ✅ Dogfood-only | Script writes envelope, exits 0 → node is errored |
| `internal.script_error` synthetic kind | ✅ Dogfood-only | Non-zero exit **with raises/on_error opt-in** |
| `internal.schema_violation` | ✅ Dogfood-only | Agent output fails declared `output:` schema |
| `internal.undeclared_kind` | ✅ Dogfood-only | Node raised kind not in `raises:` list |
| `{{ node.error.kind }}`, `{{ node.error.message }}`, `{{ node.error.details.foo }}` | ✅ Dogfood-only | Error context in downstream templates |
| `when:` conditions on error routes | ✅ Dogfood-only | Jinja/simpleeval in error-bucket routes |
| 61 feature tests passing | ✅ Dogfood | `tests/test_engine/test_error_routing.py` |

**Key behavior confirmed from `engine/workflow.py` lines 1207–1217:**
```python
# Phase 1 invariant: error envelopes do NOT propagate across
# sub-workflow boundaries. The child's halt is surfaced to the
# parent as a generic ExecutionError so the parent's success
# path treats it like any other sub-workflow failure.
# Phase 2 will introduce envelope propagation with parent frames.
```
This is a hard Phase 1 constraint. Sub-workflow errors are opaque.

#### What Phase 1 does NOT provide (gaps for Phase 2)

| Gap | Impact on polyphony | Status |
|---|---|---|
| `retry:` route action | 14 of 19 AB#3257 gates blocked | Not designed |
| Post-retry-exhaustion routing | Needed alongside `retry:` | Not designed |
| Sub-workflow error propagation | Three-tier stack is blind to inner errors | "Phase 2" in code comment |
| Workflow-level default `on_error:` | Would auto-protect nodes without explicit routes | Not in Phase 1 |
| `provider.exhausted` routable kind | Nice-to-have for LLM retries | Not in Phase 1 |

#### Critical cross-cutting gap: polyphony CLI verb exit-0 behavior

This is the sneakiest blocker. All polyphony CLI verbs exit 0 and write `{"error": "...", "success": false}` to stdout JSON on semantic failure. The current workflows use success-path routing: `when: "{{ node.output.error is defined and node.output.error }}"`.

For `on_error:` to fire on polyphony verb failures, one of:
- **(A)** Verbs also write to `$CONDUCTOR_ERROR_OUT` on failure (C# change, Mozart/Liszt scope)
- **(B)** Wrapper PowerShell scripts detect `output.error` and re-emit to `$CONDUCTOR_ERROR_OUT` (Liszt scope)
- **(C)** Keep success-path routing for polyphony verbs; use `on_error:` only for infrastructure scripts (git, HTTP polls) that genuinely exit non-zero

**Wagner's Phase 2 recommendation is Option C.** This means the 87 `output.error` checks in `when:` conditions will NOT be eliminated by Phase 1+2. They're correct for their purpose. The 27 error-gate nodes target infrastructure failures — those are the ones that can move to `on_error:`.

#### Human gate interaction

`human_gate` nodes do NOT raise error envelopes. They produce output (the selected option) on the success path. There is no mechanism for a human gate's answer to trigger `on_error:` routing in a downstream node. This means any gate that asks a human to choose "retry vs abort" is still a human gate and cannot be replaced by `on_error:`. The 27 remaining gates are not human-gate nodes — they are the script nodes' error handlers, which IS the right target.

#### Sub-workflow node interaction

When a `type: workflow` node's child workflow hits an unhandled `UnhandledWorkflowError`, the engine wraps it as a generic `ExecutionError` and surfaces it to the parent. The parent's `on_error:` routes would catch this as `internal.script_error` (from `engine/workflow.py` line 1213), not as the typed envelope from inside the sub-workflow. Phase 2 is planned to add parent-frame propagation. Until then, `type: workflow` nodes' `on_error:` can only catch "sub-workflow failed" generically, not "sub-workflow failed for kind X."

---

### 4. Risk Surface

**1. Silent abort regression.** When Phase 1 lands and the 4 pure-abort gates are retrofitted, infrastructure failures that previously paused for a human now auto-abort. This is the intended behavior, but it changes operator UX: no chance to inspect state before abort. Low risk for truly transient failures; higher risk if the failure was caused by a configuration problem that the operator could identify.

**2. Retry storms.** When Phase 2 `retry:` lands, every script node with a retry will re-execute up to N times before escalating. Polyphony verbs are idempotent by design (documented in the Phase 2 inventory), but if a verb has side effects that weren't considered, N retries amplify the damage. Particularly: `open_plan_pr` creates a PR — if it succeeds on the first attempt but returns an error envelope due to a response-parsing bug, retry creates N duplicate PRs. Mitigation: test each retrofitted node in the harness before merging.

**3. `internal.script_error` opt-in semantics.** From `errors.py` docstring: `make_script_error` is used "when a script exits non-zero, does not write an envelope, AND the node opts in via `raises` or any `on_error` route present." Without opt-in (no `raises:` and no `on_error:` route), a non-zero exit is LEGACY behavior — conductor does not raise an envelope. This means adding `on_error: true` to a node that currently exits non-zero on real failures will change behavior. Audit needed.

**4. Error-disposition signal loss.** The original `workflow_error_gate` in `actionable.yaml` was a single human gate that offered "retry at executor_router or abandon." PR #535 split this into per-step routing. The retry-at-executor path is now gone; Phase 2 should restore it via `on_error:` with retry on the 5 script parents. If Phase 2 only adds abort (not retry+retry-restart-from-executor), the signal fidelity issue persists.

**5. `seeder_error_gate` auto-continue danger.** Currently `seeder_error_gate` routes catastrophic verb failures to `child_router` (continues). Phase 2 should change this to `abort_run` on `on_error:`. If it's not caught, workflows with zero children dispatched will silently "succeed." See Beethoven's D1 decision in decisions.md.

---

### 5. Verdict

**Scale: 4/5 — Big, but gated on two upstream deliverables.**

**The cleanup IS substantial.** ~830 lines of boilerplate eliminated, 27 human_gate nodes removed, operator experience improved (no more clicking through deterministic retry/abort prompts). The mission alignment gain is real: humans stop seeing gates where they make no decisions.

**But it's not a free win.** The actual delivery has three distinct steps:

| Step | Who | Gates covered | Unblocked by |
|---|---|---|---|
| Phase 1 (pure-abort) | Wagner (YAML) | 5 gates | PR #229 merge + context.py fix |
| Phase 2a (catch-all abort for non-retry gates) | Wagner (YAML) | +5 gates | PR #229 merge |
| Phase 2b (retry+abort for 14 idempotent gates) | Wagner (YAML) + conductor team | 14 gates | RFC Phase 2 `retry:` designed + shipped |

The **biggest single dependency is PR #229 merging to upstream main.** Until then, nothing ships. That's blocked on the context.py sentinel-pattern resolution, which is upstream conductor team's call.

**For Daniel specifically:** The 14 retry+abort gates are the actual UX win (they're the ones that currently silently abort on transient network failures). Those require Phase 2 `retry:`. Phase 1 alone delivers the 5 pure-abort gates, which is a smaller win. Don't ship PR #547 as "Phase 2 complete" with only Phase 1 gates — call it "Phase 2a" and scope Phase 2b explicitly for the `retry:` work.

**Who needs to do what:**
1. **Conductor upstream team:** Resolve context.py conflict, merge PR #229 to main, design `retry:` route action for RFC Phase 2
2. **Wagner:** YAML retrofit in two batches per the Phase 2 scope (decisions.md lines 1394–1420)
3. **Mozart/Liszt:** NOT required for Phase 1/2a; needed for full verb integration (Option A/B from Phase 2 scope)
4. **Mahler:** Confirm `retry:` re-runs the same node (not a different target) once RFC Phase 2 draft lands

---

## Part B: Mahler's Top 3 Conductor Gaps

### Gap 1: `retry:` Route Action (RFC Phase 2)

**What's missing today**  
Conductor Phase 1 adds `on_error:` routing (catch and route to a different node). It does NOT add route-level retry: the ability to re-run the current node N times before escalating. The existing `RetryPolicy` (`max_attempts`, `backoff`, `retry_on`) is **agent-node-only** and only retries on provider errors/timeouts — it cannot be used on script nodes and does not apply to workflow-defined retry logic. From `schema.py` line 449: "Only applies to provider-backed agents (not script or human_gate)."

**What polyphony does to work around it**  
14 of the 19 AB#3257 gates had retry options that were removed by PR #535 because this primitive doesn't exist. Those 14 nodes now silently auto-abort on any infrastructure failure. Operators must manually re-trigger the entire workflow from the ADO work item. This includes common idempotent operations: `write_plan`, `ensure_plan_branch`, `commit_and_push`, `open_plan_pr`, `merge_plan_pr`, `poll_status`, etc. A transient network blip during a git push now aborts an entire in-flight planning run. See `decisions.md` lines 937, 1331–1338 for the full scope.

**What "fixed" looks like**  
Add a `retry:` key on error routes (not on the node itself) with `max`, `backoff`, and `initial_seconds`. The post-retry-exhaustion route is the next matching error route in document order:
```yaml
- name: commit_and_push
  type: script
  routes:
    - to: open_plan_pr      # success path
    - on_error: true         # Phase 2: retry up to 3 times
      retry: { max: 3, backoff: exponential, initial_seconds: 5 }
    - to: abort_run          # post-exhaustion fallback
      on_error: true
```
This replaces the 14 human gate nodes with 3 lines per node. The retry semantics re-run the current node (not a different target). One open design question: does `retry.max` count the first attempt or only retries? Polyphony's preference is "re-runs, not counting the first attempt" so `max: 3` = 4 total executions.

**Impact if fixed**  
**5/5.** This is the single highest-leverage conductor primitive for polyphony. It converts 14 silent-abort failure modes into automatic transient-failure recovery. Polyphony runs in overnight batch dispatch across many work items — any item that hits a transient network failure no longer aborts the entire batch. The operational improvement is immediate.

**Estimated cost**  
Medium. Schema change (add `retry` field to `RouteDef`), engine change (loop logic in `_handle_leaf_error`), test coverage (harness scenarios for retry+exhaustion). The design open question (same-node re-run vs `to:` target) must be resolved first.

---

### Gap 2: Sub-Workflow Error Envelope Propagation

**What's missing today**  
When a sub-workflow (called via `type: workflow`) hits an unhandled `UnhandledWorkflowError`, the parent engine wraps it as a generic `ExecutionError` with a string message. The typed envelope (kind, message, details) is swallowed. This is explicit in `workflow.py` lines 1207–1217:
```python
# Phase 1 invariant: error envelopes do NOT propagate across
# sub-workflow boundaries. The child's halt is surfaced to the
# parent as a generic ExecutionError.
# Phase 2 will introduce envelope propagation with parent frames.
```
The parent can only match `on_error: internal.script_error` (or `on_error: true`), not the original kind.

**What polyphony does to work around it**  
Polyphony's three-tier dispatch stack — `polyphony.yaml` → `root-batch-dispatch.yaml` → `root-item-dispatch.yaml` → lifecycle sub-workflows — means that any typed error from inside `plan-level.yaml` or `actionable.yaml` is invisible to the outer dispatch loop. The outer loop's `for_each` (in `root-batch-dispatch`) sees item failures only through the success-path output schema (`item_count`, `items_failed_count`, `failed_items`). The outer loop aggregates these via a script node (`aggregate_renegotiation`) that parses `dispatch_items.outputs` — an entire ~100-line script block exists purely to recover what the engine could return natively if envelope propagation worked. See `root-batch-dispatch.yaml` lines 108–155.

**What "fixed" looks like**  
When a sub-workflow's unhandled error propagates to the parent, the parent's `on_error:` routes see the original typed envelope (with an added `frames` field recording the sub-workflow path). The parent can match on `on_error: "external.ado.rate_limited"` even if the error originated two sub-workflow levels deep. At minimum, the parent can distinguish "sub-workflow hit an infra error" from "sub-workflow hit a content/logic error" — today both arrive as `ExecutionError` with a string.

**Impact if fixed**  
**4/5.** The immediate polyphony win is typed error routing at the dispatch level: `root-item-dispatch` could route `external.ado.*` errors (ADO rate limit) differently from `internal.script_error` (git failure) without a 100-line aggregation script. The `aggregate_renegotiation` script could be replaced by native output aggregation. More importantly, the polyphony outer loop could implement "pause on ADO rate limit, resume after N minutes" without script scaffolding.

**Estimated cost**  
Medium. The "Phase 2 will introduce envelope propagation with parent frames" comment in the code suggests this is already designed. The main implementation complexity is: how deep do frames propagate? What's the max stack depth before truncation? How does `for_each` propagate errors from individual iterations?

---

### Gap 3: Dynamic `workflow:` Path Templating

**What's missing today**  
The `workflow:` field on `type: workflow` nodes does not support Jinja2 template expressions. From `root-item-dispatch.yaml` header comment:
> "Conductor does NOT support dynamic templated `workflow:` paths (`workflow: "./{{ classify.output.workflow }}.yaml"`), so the branch-on-router pattern is the canonical mechanism for 'invoke sub-workflow X or Y based on a router output'."

Every possible sub-workflow target must be an explicit named node. Adding a new lifecycle type to polyphony requires editing `root-item-dispatch.yaml` to add a new `type: workflow` node + routing.

**What polyphony does to work around it**  
`root-item-dispatch.yaml` has 4 explicit lifecycle dispatch nodes (`plan_level`, `actionable`, `implement_merge_group`, `feature_pr`) plus a `fast_path` terminal node — 5 separate node definitions for "dispatch to the right lifecycle." The classifier's `lifecycle_workflow` output is matched via 5 `when:` conditions in the `spawn_worktree` routes block. Each new lifecycle type requires:
1. A new `type: workflow` node added to `root-item-dispatch.yaml`
2. A new `when:` route in `spawn_worktree`'s routes
3. A new route in any teardown/error path that aggregates per-lifecycle outputs

This pattern is well-contained now but will be friction at every new lifecycle type added.

**What "fixed" looks like**  
Allow `workflow: "{{ classify.output.lifecycle_workflow }}.yaml"` (or similar). The classifier emits the filename/identifier; the dispatch node resolves it at runtime. The 4 explicit dispatch nodes collapse into 1. Route conditions simplify from 5 `when:` branches to a single catch-all. Adding a new lifecycle = add a YAML file + add the lifecycle name to the classifier script. No `root-item-dispatch.yaml` touch required.

The implementation constraint: the path must be resolved at load time for the dashboard to know the sub-workflow's node graph. Dynamic dispatch is incompatible with static graph rendering. Acceptable fix: load at runtime (lazy resolution), with dashboard showing "dispatch:dynamic" placeholder. Or: require the YAML to declare `workflow_variants: [./plan-level.yaml, ./actionable.yaml]` as a schema annotation for pre-loading, with `workflow: "{{ expr }}"` at runtime.

**Impact if fixed**  
**3/5.** This is a developer-ergonomics win, not an operator-facing win. The branch-on-router pattern works correctly today; it's just verbose and imposes edit-everywhere maintenance. The leverage is: every polyphony instance that adds a new work item lifecycle type (research, spike, etc.) currently has to touch the dispatch scaffolding. Dynamic dispatch makes lifecycle extension a single-file change.

**Estimated cost**  
Large. Requires conductor schema change, resolver change, and potentially dashboard accommodations. Not suitable for near-term action. File as a future RFC ask.

---

## Appendix: Notes on Conductor Version State

| Location | Version / State |
|---|---|
| `origin/main` (upstream conductor) | v0.1.18 — **no `on_error:` support** |
| `dogfood/on-error+notifications` | v0.1.17 base + error routing (14 commits) + notifications (cherry-pick). on_error fully implemented and 61 tests passing. |
| Polyphony CI install | `git+https://github.com/microsoft/conductor.git@main` — no pin. Installs v0.1.18. |
| Blocker for PR #229 merge | `context.py` semantic conflict: `_add_agent_input` `is_dict_output` flag vs error-path `agent_outputs.get()`. Raised as PR comment 2026-05-28. Upstream team must resolve. |

---

## Summary

**on_error verdict (1 sentence):** Big cleanup (4/5) — eliminates ~830 lines and 27 human-gate error nodes — but is gated on conductor PR #229 merging upstream (blocked by context.py conflict) AND a not-yet-designed `retry:` route action for the 14 idempotent-retry gates that make up the real UX win.

**Top 3 other conductor gaps:**
1. **`retry:` route action** — highest impact (5/5), unblocks 14 gates, restores automatic transient-failure recovery
2. **Sub-workflow error envelope propagation** — impact 4/5, eliminates aggregation-script workarounds in the three-tier dispatch stack
3. **Dynamic `workflow:` path templating** — impact 3/5, developer ergonomics win for polyphony lifecycle extensibility
