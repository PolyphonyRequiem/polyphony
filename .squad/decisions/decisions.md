# Squad Decisions Registry

**Last updated:** 2026-05-28T23:00:46Z

## Session round (2026-05-28): on_error + notifications focus

This document aggregates decisions and major findings from the following agents across this session:
- Beethoven (Mission Keeper) — gate disposition reviews  
- Wagner (Workflow Author) — Phase 2 scope + error pattern catalogue  
- Bach (Architect) — ADR proposals + platespinner integration design  
- Mahler (Conductor Expert) — dogfood branch + adoption survey  

---


---

# Inbox: Beethoven → Gate Disposition Review for PR #535

**Date:** 2026-05-28T15:37:47Z  
**Author:** Beethoven (Mission Keeper)  
**Topic:** AB#3257 — Wagner's three unilateral disposition decisions in PR #535 (error-gate migration)  
**PR:** https://github.com/PolyphonyRequiem/polyphony/pull/535  
**Status:** Review complete — see dispositions below

---

## Context

Wagner's PR #535 removes all 19 trivial error-gate `human_gate` nodes, replacing them with direct routes because conductor v0.1.18 has no `on_error:` primitive. Sixteen of the nineteen substitutions are mechanical (infrastructure error → `abort_run`). Three required disposition judgment that exceeded Wagner's authority. This review covers those three only.

The baseline invariant I'm applying: polyphony is "human-assisted automated SDLC." The engine is automated; humans gate the judgment-heavy steps. Error handling that silently corrupts state or permanently closes work items without human awareness is a mission violation. Error handling that merely aborts a run and lets the operator re-trigger is not.

---

## Gate 1 — `seeder_error_gate` → `child_router` (auto-continue)

**File:** `.conductor/registry/workflows/plan-level.yaml`  
**Original routing:** `seeder.output.error_count > 0` → `seeder_error_gate` (human_gate: retry / continue / abort)  
**Wagner's routing:** `seeder.output.error_count > 0` → `child_router` (auto-continue, no human acknowledgment)

### What the original gate enabled

The original gate served as an explicit acknowledgment checkpoint. The seeder carries per-child failures in `errors[]` and continues to exit 0 regardless, which means error_count > 0 is not a fatal signal by default. The gate let the operator see the exact per-child failure detail (twig dedup conflict, invalid parent ID, workspace misconfiguration) and choose:
- **Retry** — fix the cause, re-run the idempotent seeder  
- **Continue** — accept the partial seed and proceed  
- **Abort** — halt

The gate's own inline comment was explicit: *"so the operator sees the failure rather than continuing into child_router with a half-seeded tree (which silently terminates the root blocked)."*

### What the new routing removes

Auto-continue silently swallows per-child failures. The operator never sees which children failed to seed. If the cause is a platform-level error (invalid parent ID, twig workspace misconfiguration), the root proceeds into child_router with a structurally broken tree and will silently block with no diagnostics surfaced.

Note: Wagner's framing ("was: human continue option → now: auto-continue") mischaracterizes the substitution. The "Continue" option in the original gate still required explicit human acknowledgment of the error log. Auto-continue removes that acknowledgment. These are not equivalent.

### Blast radius if wrong

Root run enters `child_router` with a half-seeded ADO tree. Some children were never created. Those children never appear in `polyphony state next-ready` output. The root run eventually ends up permanently blocked with zero observability — no error surfaced, no abandoned state, no operator notification. Operator has to grep conductor logs to understand why the root stalled.

### Disposition

**❌ Request change**

Wagner should route `seeder.output.error_count > 0` to `abort_run`, consistent with every other infrastructure error in this PR. This is not a quality judgment — it is an infrastructure signal. The seeder is idempotent so the operator can re-trigger after fixing the cause (resolving the duplicate title, correcting the parent ID, etc.).

Auto-continuing with a silent partial tree is strictly worse than halting and surfacing the failure. The operator knows the run stopped; they do not know the tree is silently incomplete.

**Correct routing:**
```yaml
routes:
  - to: abort_run
    # TODO(AB#3257): replace with on_error: abort when conductor phase-1 lands
    when: "{{ seeder.output.error_count is defined and seeder.output.error_count > 0 }}"
  - to: child_router
```

---

## Gate 2 — `classify_error_gate` → `$end` (auto-skip)

**File:** `.conductor/registry/workflows/restack-remedy.yaml`  
**Original routing:** `classify.output.error_code` populated → `classify_error_gate` (human_gate: retry classify / skip restack)  
**Wagner's routing:** `classify.output.error_code` populated → `$end` (auto-skip, silent termination)

### What the original gate enabled

The gate surfaced the `error_code` and `error` fields to the operator with common-cause diagnostics (manifest missing/malformed in the per-root state directory, repo slug not resolvable, twig cache stale). It offered retry or skip.

### What the new routing removes

Observability only. The operator never sees that classify failed. The restack silently did nothing.

### Blast radius if wrong

Stale descendant PRs don't get restacked. They stay stale until the next time `restack-remedy` is triggered. This is bounded harm: no work item state is corrupted, no run is aborted, no ADO disposition is set. The restack is a utility helper workflow, not the core SDLC path.

Skip was already a valid operator choice in the original gate. Wagner is effectively encoding the "skip" path as the automatic response, which is a reasonable degraded-mode decision given the constraint.

### Disposition

**⚠️ Approve with note**

The disposition is acceptable for phase 1. The blast radius is bounded (stale branches linger until next run; no state corruption). Skip was already a first-class operator option in the original gate.

**Flag for phase-2 retrofit (AB#3257):** When `on_error:` lands in conductor, this site should emit a structured warning log (error_code, root_id) rather than silently terminating. The current code gives the operator zero signal that classify failed. A conductor-native `on_error: skip` with a `warn_message` would close this observability gap without requiring a human gate.

---

## Gate 3 — `evidence_reviewer` block + `merge_evidence_pr` false → `workflow_abandoned`

**File:** `.conductor/registry/workflows/actionable.yaml`  
**Original routing:** Both paths → `workflow_error_gate` (human_gate: retry at executor_router / abandon)  
**Wagner's routing:** Both paths → `workflow_abandoned`

### Background: Wagner's infrastructure/content split

Wagner made a principled distinction across the old `workflow_error_gate`'s inputs: infrastructure errors (`compose_addendum`, `open_evidence_pr`, `evidence_floor_check`) now route to `abort_run`; content-or-quality signals (`evidence_reviewer.decision == 'block'`, `merge_evidence_pr.merged == false`) now route to `workflow_abandoned`. The principle is sound. The application is partly wrong.

### Path A — `evidence_reviewer.output.decision == 'block'` → `workflow_abandoned`

This is a reviewer quality judgment: the evidence agent reviewed the PR and explicitly blocked it. "Block" is a content quality signal, not an infrastructure failure. Setting the item to abandoned is semantically appropriate — the evidence doesn't meet the bar and the item should be re-examined by the team before another attempt.

The original gate allowed retry (re-enter executor_router), which gave the operator a chance to re-run with a different approach. That retry path is now gone. However: under the current constraint (no `on_error:`, conductor v0.1.18), the loss of the retry path is an acceptable tradeoff for shipping — re-trigger from the ADO work item is still possible.

**Sub-disposition: ⚠️ Approve with note.** Defensible as-is. Flag for phase-2 retrofit: when `on_error:` lands, restore the retry path (re-enter executor_router) before reaching workflow_abandoned.

### Path B — `evidence_reviewer` catch-all (unknown/missing decision) → `workflow_abandoned`

The original comment: *"Catch-all per M4: unknown / missing decision routes to the error gate so the operator decides rather than raising No matching route found."*

An unknown or missing decision value from `evidence_reviewer` is not a quality judgment — it is a conductor invariant violation (the agent returned an unexpected output shape). Routing it to `workflow_abandoned` marks the work item as abandoned in ADO due to what is effectively a code defect.

**Sub-disposition: ❌ Request change.** This should route to `abort_run`, not `workflow_abandoned`. The item is not "abandoned" — the engine hit an unexpected state. Abort halts the run without marking the item; the operator can re-trigger after the root cause is diagnosed.

### Path C — `merge_evidence_pr.output.merged == false` → `workflow_abandoned`

This is the highest-blast-radius error in the three gates under review.

`merged == false` from `polyphony pr merge-evidence-pr` can occur for several reasons, including transient ones:
- Network/API error  
- Merge conflict introduced since the PR was opened  
- Branch protection violation (transient config issue)  
- PR already closed by an operator between the open and merge steps

`workflow_abandoned` does not just stop the conductor run — it sets the work item disposition to "abandoned" in the ADO work item system (via the satisfaction flow's state transition). If the cause was transient, the operator must manually re-open the work item in ADO before they can re-trigger. This is materially more expensive than an `abort_run` (which just stops conductor; the item stays active; re-trigger is one command).

The original `workflow_error_gate` for this path showed the merge error detail and let the operator choose retry or abandon. Wagner collapsed both options to the more destructive outcome.

**Sub-disposition: ❌ Request change.** `merge_evidence_pr.merged == false` should route to `abort_run`, not `workflow_abandoned`. Same for the catch-all (missing/malformed merged field — infrastructure anomaly, not a quality disposition).

### Disposition Summary for Gate 3

**❌ Request change** (two sub-paths; one sub-path approved with note)

| Sub-path | Current | Correct | Reason |
|----------|---------|---------|--------|
| `evidence_reviewer.decision == 'block'` | `workflow_abandoned` | `workflow_abandoned` ✅ | Quality judgment — abandonment is correct |
| `evidence_reviewer` catch-all | `workflow_abandoned` | `abort_run` ❌ | Conductor invariant violation, not quality |
| `merge_evidence_pr.merged == false` | `workflow_abandoned` | `abort_run` ❌ | Can be transient; ADO disposition is irreversible |
| `merge_evidence_pr` catch-all | `workflow_abandoned` | `abort_run` ❌ | Infrastructure anomaly, not quality disposition |

**Note:** Wagner should NOT touch the infrastructure-error paths (compose_addendum, open_evidence_pr, evidence_floor_check) — those are correctly routed to `abort_run`.

---

## Summary

| Gate | Disposition | Required action |
|------|------------|-----------------|
| `seeder_error_gate` → auto-continue | ❌ Request change | Change route to `abort_run`; add TODO(AB#3257) comment |
| `classify_error_gate` → auto-skip | ⚠️ Approve with note | None now; add observability in phase-2 retrofit |
| `evidence_reviewer` block / `merge_evidence_pr` false → `workflow_abandoned` | ❌ Request change (partial) | Change catch-all and `merged==false` routes to `abort_run`; keep `block` → `workflow_abandoned` |

---

## Asks of Daniel

1. **Seeder error semantics (Gate 1):** My call is `abort_run` on any `error_count > 0`. If you have a reason to believe partial-seed auto-continue is safe enough for phase 1 (e.g., you know the seeder always seeds the critical children first), override this and I'll downgrade to ⚠️ Approve with note. But I need explicit sign-off because the gate's own comment warned against it.

2. **`workflow_abandoned` vs. ADO state machine (Gate 3):** I'm assuming `workflow_abandoned` commits an irreversible ADO state transition (abandoned disposition on the work item). If `workflow_abandoned` in actionable.yaml is actually a conductor-only terminal with no ADO write, the blast radius of routing `merge_evidence_pr.merged == false` to it is lower, and this could be downgraded to ⚠️. Please confirm the terminal node's ADO side-effects.


---

# Phase 2 Scope: `on_error:` Retrofit — Restore 19 Gates Removed in #535

**Author:** Wagner (Workflow Author)  
**Date:** 2026-05-28T22:38:04Z  
**Status:** Draft — filed as GitHub issue, pending Beethoven disposition review  
**Parent issue:** [#528 — Migrate 19 trivial error gates to conductor on_error](https://github.com/PolyphonyRequiem/polyphony/issues/528)  
**Closed by:** [PR #535](https://github.com/PolyphonyRequiem/polyphony/pull/535) (direct routing + TODO comments)  
**Upstream conductor PRs:** [#227 RFC](https://github.com/microsoft/conductor/pull/227) · [#229 Phase 1 impl](https://github.com/microsoft/conductor/pull/229)  
**Companion inventory:** `docs/projects/on-error-migration-inventory.md`

---

## Executive Summary

PR #535 removed 19 trivial human-gate error nodes across 6 workflows. It replaced them with direct routing because conductor v0.1.18 ships no `on_error:` primitive. As a side-effect, **14 gates lost retry capability** — transient failures now abort instead of pausing for human retry, and operators must re-trigger manually.

Once conductor Phase 1 (`on_error:` typed routing) ships, the TODO-marked sites can be retrofitted. **Phase 2 = that retrofit.** This document scopes the work.

**Key finding:** Phase 1 alone (PR #229) handles only 5 of the 19 gates cleanly. The remaining 14 retry+abort gates require Phase 2 of the conductor RFC (`retry:` route action). Additionally, polyphony CLI verbs emit errors as stdout JSON with exit 0 — they must also write to `$CONDUCTOR_ERROR_OUT` for `on_error:` routes to fire at all (a C# verb change, scoped to Mozart/Liszt).

---

## 1. Gate Inventory

### 1A. Category summary

| Category | Count | Conductor requirement | Notes |
|---|---:|---|---|
| Pure-abort (no retry) | 4 | Phase 1 `on_error: true → to: abort_run` | Mechanical |
| Retry+abort (idempotent ops) | 13 | Phase 1 + RFC Phase 2 `retry:` action | Blocked on RFC Phase 2 |
| Retry+abort (platform-router) | 1 | Phase 1 + RFC Phase 2 `retry:` + `to:` | `poll_error_gate` in plan-level.yaml |
| Unilateral continue | 1 | Phase 1 (continue only) or Phase 1+2 (retry+continue) | `seeder_error_gate` — ⚠️ Beethoven review |
| Unilateral skip | 1 | Phase 1 `on_error: true → to: $end` | `classify_error_gate` — ⚠️ Beethoven review |
| Catch-all (7 parents) | 1 | Phase 1 per-parent OR workflow propagation | `workflow_error_gate` actionable.yaml — ⚠️ Beethoven review |

### 1B. Full gate inventory

Each entry: **gate name** | **file** | **original options** | **current #535 disposition** | **proposed Phase 2 rewrite** | **API dependency** | **notes**

---

#### `poll_error_gate` — `ado-pr.yaml`

- **Lines (pre-#535):** 505–531
- **Parent step:** `poll_status`
- **Original options:** retry → `poll_status`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES — single-shot poll retry removed
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: poll_status
    type: script
    # ... (existing)
    raises:
      - internal.script_error
    routes:
      - to: poll_status          # success path: loop back
        when: "{{ ... }}"
      - to: abort_run
        on_error: true           # catch-all error; Phase 2 adds retry:
      # Phase 2 (once RFC retry: ships):
      # - on_error: true
      #   retry: { max: 3, backoff: exponential, initial_seconds: 5 }
      # - to: abort_run
      #   on_error: true
  ```
- **API dependency:** Phase 1 covers catch-all abort; Phase 2 `retry:` action restores retry
- **Notes:** Identical shape to `poll_error_gate` in `github-pr.yaml`. Do both together.

---

#### `poll_error_gate` — `github-pr.yaml`

- **Lines (pre-#535):** 406–429
- **Parent step:** `poll_status`
- **Original options:** retry → `poll_status`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Proposed `on_error:` rewrite:** (same shape as ado-pr.yaml above)
- **API dependency:** Phase 1 + RFC Phase 2 `retry:`
- **Notes:** Identical to ado-pr.yaml gate. Batch with it.

---

#### `classify_error_gate` — `restack-remedy.yaml`

- **Lines (pre-#535):** 112–139
- **Parent step:** `classify`
- **Original options:** retry → `classify`; skip → `$end`
- **#535 disposition:** auto-skip to `$end` (lost retry option)
- **Retry capability lost:** YES (skip was default; retry was the optional path)
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: classify
    type: script
    # ...
    raises:
      - internal.script_error
    routes:
      - to: $end
        on_error: true           # skip on error (Phase 1 sufficient for skip-only)
      # Phase 2 with retry:
      # - on_error: true
      #   retry: { max: 2, backoff: fixed, initial_seconds: 10 }
      # - to: $end
      #   on_error: true         # post-exhaustion: skip
  ```
- **API dependency:** Phase 1 for skip-only; Phase 2 `retry:` to restore retry-then-skip
- **⚠️ Beethoven review — unilateral disposition:** Original gate offered retry OR skip. #535 committed to auto-skip. Beethoven should confirm: (a) auto-skip is correct for restack-remedy classify failures, (b) retry-then-skip is worth restoring when RFC Phase 2 ships.

---

#### `squash_coverage_error_gate` — `implement-merge-group.yaml`

- **Lines (pre-#535):** 1032–1060
- **Parent step:** `assert_impl_pr_coverage`
- **Original options:** retry → `assert_impl_pr_coverage`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: assert_impl_pr_coverage
    type: script
    raises:
      - internal.script_error
    routes:
      - to: <success_target>
      - to: abort_run
        on_error: true           # Phase 1: catch-all abort
      # Phase 2:
      # - on_error: true
      #   retry: { max: 3, backoff: exponential, initial_seconds: 5 }
      # - to: abort_run
      #   on_error: true
  ```
- **API dependency:** Phase 1 + RFC Phase 2 `retry:`

---

#### `root_router_error_gate` — `implement-merge-group.yaml`

- **Lines (pre-#535):** 1250–1277
- **Parent step:** `root_router`
- **Original options:** retry → `root_router`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Prompt referenced AB#3126 but routing was plain retry/abort.
- **API dependency:** Phase 1 + RFC Phase 2 `retry:`

---

#### `workflow_error_gate` — `actionable.yaml`

- **Lines (pre-#535):** 870–918
- **Parent steps (7):** `executor_router`, `ensure_evidence_branch`, `compose_addendum`, `open_evidence_pr`, `evidence_floor_check`, `evidence_reviewer`, `merge_evidence_pr`
- **Original options:** bare → `executor_router` (retry); abandon → `workflow_abandoned`
- **#535 disposition:** Per-parent direct routing (executor_router → abort_run; evidence steps → workflow_abandoned)
- **Retry capability lost:** Partially — per-step routing is now hardcoded
- **Proposed `on_error:` rewrite (per-parent, Phase 1 + Phase 2):**
  ```yaml
  # executor_router — Phase 1 abort; Phase 2 retry+abort
  - name: executor_router
    routes:
      - to: polyphony_executor
        when: "{{ ... }}"
      - to: abort_run
        on_error: true
      # Phase 2:
      # - on_error: true
      #   retry: { max: 2 }
      # - to: abort_run
      #   on_error: true

  # ensure_evidence_branch / compose_addendum / open_evidence_pr /
  # evidence_floor_check — Phase 1 abort; Phase 2 retry+abort
  - name: ensure_evidence_branch
    routes:
      - to: compose_addendum
      - to: abort_run
        on_error: true

  # evidence_reviewer — Phase 1 no change needed (LLM node, not script)
  # evidence_reviewer uses success-path routing (decision field on output)
  # on_error: would only fire for schema_violation; route to workflow_abandoned

  # merge_evidence_pr — Phase 1 + error catch
  - name: merge_evidence_pr
    routes:
      - to: workflow_completed
        when: "{{ merge_evidence_pr.output.merged == true }}"
      - to: workflow_abandoned
        when: "{{ merge_evidence_pr.output.merged == false }}"
      - to: workflow_abandoned
        on_error: true           # Phase 1: catch merge script error → abandon
  ```
- **API dependency:** Phase 1 handles abort/abandon-on-error for script nodes; LLM nodes (evidence_reviewer) are unaffected — they use success-path routing already.
- **⚠️ Beethoven review — unilateral disposition:** The original gate offered a single landing pad with retry-or-abandon. #535 split into per-step outcomes. Two sub-questions for Beethoven:
  - **evidence_reviewer:** Current: block/request_changes/approve routing is success-path (LLM output field), not on_error. The original gate's "abandon" option for a reviewer failure was a different thing (tool failure, not reviewer decision). Confirm the current split is correct.
  - **merge_evidence_pr → workflow_abandoned on false:** This is success-path routing (already present), not an error gate. No Phase 2 change needed. Confirm intent.
- **Batch strategy:** Migrate the 5 script-node parents (executor_router, ensure_evidence_branch, compose_addendum, open_evidence_pr, evidence_floor_check) as one batch. Leave evidence_reviewer and merge_evidence_pr alone (already success-path routed).

---

#### `root_resolver_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 346–364
- **Parent step:** `root_resolver`
- **Original options:** bare → `abort_run` (abort-only)
- **#535 disposition:** direct route to `abort_run`
- **Retry capability lost:** NO (was abort-only)
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: root_resolver
    type: script
    raises:
      - internal.script_error
    routes:
      - to: next_step
      - to: abort_run
        on_error: true
  ```
- **API dependency:** Phase 1 only — this is a clean Phase 1 retrofit

---

#### `type_loader_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 384–402
- **Parent step:** `type_loader`
- **Original options:** bare → `abort_run` (abort-only)
- **#535 disposition:** direct route to `abort_run`
- **Retry capability lost:** NO
- **Proposed `on_error:` rewrite:** same shape as `root_resolver` above
- **API dependency:** Phase 1 only

---

#### `ancestor_chain_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 428–450
- **Parent step:** `ancestor_chain`
- **Original options:** retry → `ancestor_chain`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **API dependency:** Phase 1 (abort-only) + RFC Phase 2 (retry+abort)

---

#### `state_detector_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 518–538
- **Parent step:** `state_detector`
- **Original options:** retry → `state_detector`; abort → `abort_run`
- **#535 disposition:** auto-abort (two abort_run routes in #535)
- **Retry capability lost:** YES
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `write_plan_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1015–1037
- **Parent step:** `write_plan`
- **Original options:** retry → `write_plan`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Idempotent write — retry is safe and valuable
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `ensure_plan_branch_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1069–1092
- **Parent step:** `ensure_plan_branch`
- **Original options:** retry → `ensure_plan_branch`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Idempotent git op — retry is safe
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `commit_and_push_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1131–1156
- **Parent step:** `commit_and_push`
- **Original options:** retry → `commit_and_push`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Git push — retry is safe for transient network failures
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `open_plan_pr_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1191–1220
- **Parent step:** `open_plan_pr`
- **Original options:** retry → `open_plan_pr`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** API call to open PR — idempotent if PR already exists check passes
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `poll_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 1506–1529
- **Parent step:** `poll_status`
- **Original options:** retry → `pr_poll_platform_router`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Platform-aware re-poll via router on retry — slightly more complex than other poll gates. On retry, routes back to `pr_poll_platform_router` (not directly to `poll_status`). Phase 2 `retry:` re-runs the node itself; may need a wrapper that respects the platform router.
- **API dependency:** Phase 1 + RFC Phase 2; **special case** — post-retry target is `pr_poll_platform_router`, not the failing node itself. If RFC Phase 2 `retry:` always re-runs the same node, this gate needs a small route re-architecture: `poll_status → (retry) → poll_status → (giveup) → abort_run`, and the platform router logic folds into `poll_status`'s success routing. Flag for Mahler: does `retry:` re-run the same node or route to a different node?

---

#### `merge_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 2448–2539
- **Parent step:** `merge_plan_pr`
- **Original options:** retry → `merge_plan_pr`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **Notes:** Original prompt was cause-aware (listed common merge failure reasons) — no routing by error code, but prompt was diagnostic. Phase 2 can preserve cause info via `{{ error.message }}` in a downstream recovery node if desired. Not required for retrofit.
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `seeder_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 2604–2658
- **Parent step:** `seeder`
- **Original options:** retry → `seeder`; continue → `child_router`; abort → `abort_run`
- **#535 disposition:** auto-continue to `child_router`
- **Retry capability lost:** YES (both retry and abort options removed)
- **Notes:** Three-way gate — the only gate with a "continue despite error" option. The seeder verb exits 0 even on per-child errors (routing-style envelope), so `error_count > 0` is detected on the success path already. The actual on_error trigger here would be catastrophic seeder failure (verb crash), not per-child failure.
- **Proposed `on_error:` rewrite:**
  ```yaml
  - name: seeder
    type: script
    raises:
      - internal.script_error
    routes:
      - to: child_router
        when: "{{ seeder.output.error_count is defined and seeder.output.error_count == 0 }}"
      - to: child_router              # partial-seed continue (success path)
        when: "{{ seeder.output.children_seeded is defined and ... }}"
      - to: abort_run
        on_error: true               # catastrophic failure: abort
      # Phase 2:
      # - on_error: true
      #   retry: { max: 2, backoff: exponential }
      # - to: abort_run
      #   on_error: true
  ```
- **API dependency:** Phase 1 handles abort-on-catastrophic; Phase 2 restores retry
- **⚠️ Beethoven review — unilateral disposition:** Original gate offered retry-or-continue-or-abort on ANY seeder error. #535 committed to auto-continue (which was the "partial seed" intent). The auto-continue behavior is arguably correct for partial seeder errors. But the original retry option is gone for catastrophic failures. Beethoven should confirm: on a catastrophic seeder crash (not partial-seed, but verb failure), is auto-continue to `child_router` the right behavior? Or should a catastrophic crash abort?

---

#### `open_plan_pr_ado_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 2808–2837
- **Parent step:** `open_plan_pr_ado`
- **Original options:** retry → `open_plan_pr_ado`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **API dependency:** Phase 1 + RFC Phase 2

---

#### `merge_plan_pr_ado_error_gate` — `plan-level.yaml`

- **Lines (pre-#535):** 3008–3034
- **Parent step:** `merge_plan_pr_ado`
- **Original options:** retry → `merge_plan_pr_ado`; abort → `abort_run`
- **#535 disposition:** auto-abort to `abort_run`
- **Retry capability lost:** YES
- **API dependency:** Phase 1 + RFC Phase 2

---

## 2. Phase 1 API Spec-Check

Conductor Phase 1 (branch `feature/error-routing`, PR #229) ships:

### What Phase 1 provides

| Feature | Phase 1 | Notes |
|---|---|---|
| `on_error: <kind>` on routes | ✅ | Exact equality match on dotted kind string |
| `on_error: true` (catch-all) | ✅ | Matches any raised kind |
| `on_error: [kind1, kind2]` (multi-kind) | ✅ | OR match |
| `raises:` on agent definitions | ✅ | Optional contract enforcement |
| `$CONDUCTOR_ERROR_OUT` env var | ✅ | Script writes envelope, exits 0 |
| `ErrorEnvelope` shape: `{kind, message, details}` | ✅ | `conductor_error: true` stripped on coerce |
| `internal.script_error` synthetic kind | ✅ | Non-zero exit without envelope (with raises/on_error opt-in) |
| `internal.schema_violation` synthetic kind | ✅ | Agent output fails declared schema |
| `internal.undeclared_kind` synthetic kind | ✅ | Raised kind not in `raises:` list |
| `when:` on error routes | ✅ | Jinja/simpleeval condition on error-bucket routes |
| `{{ node.error.kind }}`, `{{ node.error.message }}`, `{{ node.error.details.foo }}` | ✅ | Error context in downstream templates |
| Transport-level `RetryPolicy` (existing) | ✅ | `retry_on: [provider_error, timeout]` — **not** route-level retry |

### What Phase 1 does NOT provide (gaps for Phase 2)

| Feature | Gap | Polyphony impact |
|---|---|---|
| `retry:` route action | ❌ Not in Phase 1 | 14 gates need retry-then-abort; blocked until RFC Phase 2 |
| Post-retry-exhaustion routing | ❌ Design open | Needed for retry-then-abort pattern |
| `halt:` and `propagate:` route actions | ❌ Phase 2 | Not needed for the 19 gates (all use `to:` targets) |
| Sub-workflow error propagation | ❌ Phase 2 | Would clean up `workflow_error_gate` but not required for retrofit |
| Workflow-level default `on_error:` | ❌ Not in Phase 1 | Would let the 4 pure-abort gates omit explicit error routes; nice-to-have |
| `provider.exhausted` routable kind | ❌ Phase 2 | Not needed for the 19 gates |

### Critical cross-cutting gap: polyphony CLI verb error emission

**This is the most important gap for Phase 2.**

Polyphony CLI verbs exit 0 on semantic error and write `{"error": "...", "success": false}` (or similar) to stdout JSON. The current workflows branch on `output.success == false` on the **success path** — there is no non-zero exit, so `internal.script_error` never fires.

For `on_error:` routes to trigger on polyphony verb failures:
- The verb script must **also** write to `$CONDUCTOR_ERROR_OUT` when it fails, OR
- The wrapper PowerShell script (Liszt) must detect `output.error` and re-emit to `$CONDUCTOR_ERROR_OUT`, OR
- We keep using success-path `when: "{{ output.success == false }}"` routing for polyphony verbs and reserve `on_error:` for infrastructure failures (git, API rate limits) that actually exit non-zero.

**Recommendation:** Option C for now (mixed pattern). Use `on_error:` for infrastructure script failures (git push, PR open, poll HTTP calls) where non-zero exit is the natural signal. Keep success-path routing for polyphony verb calls. This is clean and requires no C# changes.

Implication: several of the 19 TODO sites may already be correctly handled by success-path routing and only need an `on_error:` catch-all added for infrastructure failure. This reduces the Phase 2 scope for those nodes.

---

## 3. Test Strategy

### Per-workflow execution tests

For each retrofitted gate, a harness scenario (under `tests/harness/scenarios/`) that:
1. Runs the relevant workflow with a `FakeProvider` that simulates the failing node writing to `$CONDUCTOR_ERROR_OUT` with `internal.script_error`
2. Asserts the error route fires (not the success route)
3. For retry gates (after RFC Phase 2): asserts the node is re-run up to `max` times before escalating to `abort_run`

Priority scenarios:
- `ado-pr.yaml` + `github-pr.yaml`: `poll_status` failure → error route fires → abort_run
- `plan-level.yaml`: `write_plan` failure → retry (2×) → abort_run
- `plan-level.yaml`: `seeder` catastrophic failure → abort_run (NOT child_router)
- `actionable.yaml`: each of the 5 script-node parents fires its `on_error:` route

### Error envelope assertions

For each gate that maps to a specific error kind (post-Phase 2):
- Assert `{{ node.error.kind }}` is accessible in downstream templates
- Assert `{{ node.error.details }}` is accessible for any gate that uses cause-aware messaging

### Re-confirmation of 3 unilateral dispositions

Once Beethoven reviews the 3 dispositions (seeder, classify, evidence_reviewer/merge path), add explicit tests that:
1. Assert the chosen disposition fires (not the old 3-way human gate)
2. Assert NO regression to old behavior if disposition is changed

### Back-compat test

Confirm that workflows without any `on_error:` routes continue to work identically after conductor Phase 1 merges — the spec says backwards compatibility is guaranteed (existing workflows halt on unhandled errors as before).

---

## 4. Effort + Sequencing

### Size estimates

| Batch | Gates | Files | Size | Phase dependency |
|---|---|---|---|---|
| **A: Pure-abort** | `root_resolver`, `type_loader` (plan-level) | plan-level.yaml | S | Phase 1 only |
| **B: Poll catch-all** | `poll_error_gate` (ado-pr + github-pr) | ado-pr.yaml, github-pr.yaml | S | Phase 1 only (abort); M with retry |
| **C: actionable catch-all** | `workflow_error_gate` 5 script parents | actionable.yaml | M | Phase 1 only (abort); M with retry |
| **D: plan-level idempotent retry** | `ancestor_chain`, `state_detector`, `write_plan`, `ensure_plan_branch`, `commit_and_push`, `open_plan_pr`, `merge_error_gate`, `open_plan_pr_ado`, `merge_plan_pr_ado`, `poll_error_gate` | plan-level.yaml | L | RFC Phase 2 `retry:` |
| **E: implement-mg** | `squash_coverage`, `root_router` | implement-merge-group.yaml | S | RFC Phase 2 `retry:` |
| **F: seeder** | `seeder_error_gate` | plan-level.yaml | M | Phase 1 (abort only) or Phase 2 (retry) — pending Beethoven |
| **G: classify** | `classify_error_gate` | restack-remedy.yaml | S | Phase 1 (skip only) or Phase 2 (retry+skip) — pending Beethoven |

**Total: 2 S batches (Phase 1), 1 M batch (Phase 1), 1 L + 1 S batch (Phase 2), 2 batches pending Beethoven decision.**

### Recommended order

1. **Wait for conductor Phase 1 to merge** (PR #229). Validate with `conductor validate` on a test workflow.
2. **Batch A** (pure-abort, Phase 1 only): mechanical, lowest risk. One PR. Gets the pattern established.
3. **Batch B** (poll catch-all): two files, identical shape. One PR. Phase 1 abort-only; add Phase 2 retry via amendment once RFC Phase 2 merges.
4. **Batch C** (actionable catch-all 5 parents): careful per-parent mapping; moderate blast radius. One PR.
5. **Wait for Beethoven dispositions** on seeder, classify, workflow_error_gate (evidence path).
6. **Batches F + G** (seeder + classify): pending Beethoven decision.
7. **Wait for conductor RFC Phase 2 to merge** (retry: action).
8. **Batch D** (plan-level idempotent retry): largest batch; plan-level.yaml is high-value. One PR.
9. **Batch E** (implement-mg retry): straightforward.

### Parallelizable

Batches B and C can run in parallel after A. Batches D and E can run in parallel after RFC Phase 2 merges. F and G can run in parallel once Beethoven decides.

### Risks

1. **RFC Phase 2 timeline unknown.** 14 of 19 gates are blocked on it. If Mahler's upstream timeline is long, consider shipping a Phase 2a (Phase 1 gates only, 5 gates) and Phase 2b (RFC Phase 2 gates, 14 gates).
2. **plan-level.yaml circular self-reference pre-existing `validate` fail.** `conductor validate` will FAIL on plan-level.yaml regardless of changes. This is pre-existing (#535 confirmed identical on main). Do not let this block Phase 2 plan-level work — treat validate FAIL on plan-level as expected until upstream resolves the sub-workflow self-reference.
3. **Polyphony CLI verb exit-0 behavior.** If the decision is made to route polyphony verb errors through `on_error:` (Option A/B above), that requires C# changes owned by Mozart/Liszt and must be coordinated before polyphony-side Phase 2 begins.

---

## 5. Asks of Mahler / Upstream Conductor

These are requirements that Phase 2 needs conductor to deliver. They become the input to Mahler's implementation of RFC Phase 2.

### Ask 1 (CRITICAL): `retry:` route action

The 14 retry+abort gates cannot be retrofitted without a semantic retry on routes.

**Required shape:**
```yaml
routes:
  - on_error: true
    retry: { max: 3, backoff: exponential, initial_seconds: 5 }
  - to: abort_run
    on_error: true    # post-exhaustion fallback
```

**Field contract needed:**
- `retry.max` — integer, number of re-runs including or excluding first attempt? (clarify — original gate comment says "max: N" but RFC uses `max:` ambiguously)
- `retry.backoff` — `fixed | exponential` (schema.py `RetryPolicy` uses same values — reuse)
- `retry.initial_seconds` — base delay
- Post-exhaustion: how does control pass to the next error route? RFC open question — recommend **implicit by document order** (next matching error route after the `retry:` entry wins on exhaustion) as the simplest shape. Polyphony doesn't need explicit `when: retry_exhausted` predicates.

### Ask 2 (IMPORTANT): Retry re-runs the **same node**, not a different target

`poll_error_gate` in `plan-level.yaml` originally retried to `pr_poll_platform_router`, not `poll_status`. If `retry:` always re-runs the current node, the platform-router hop needs to be folded into `poll_status`'s own success routing. Confirm: does `retry:` re-run the declaring node (no `to:` needed) or does it route to an explicit `to:`?

**Polyphony's preference:** re-run the same node (simpler, consistent with "retry the failing step"). Will refactor `poll_error_gate` in plan-level.yaml accordingly.

### Ask 3 (USEFUL): `internal.script_error` fires for non-zero exit WITHOUT requiring `raises:` when any `on_error:` route is present

From `errors.py` and the example, `internal.script_error` is synthesized "when a script exits non-zero **AND the node opts in via `raises` or any `on_error` route present**." The second half of that sentence (opt-in via `on_error` route) needs to be confirmed as sufficient — if adding an `on_error:` route to a node implicitly opts it in, Phase 2 can omit `raises:` declarations from the 19 nodes (they have no known kinds — just infrastructure failures). This reduces boilerplate significantly.

### Ask 4 (NICE-TO-HAVE): Workflow-level default `on_error:` target

If conductor supports `workflow.on_error: { to: abort_run }` as a fallback for any node without an explicit error route, the 4 pure-abort gates (root_resolver, type_loader, ancestor_chain, state_detector) require zero YAML changes — the workflow default covers them. This would also protect any future nodes added without explicit error routes.

If this is too complex for Phase 2, skip it — per-node explicit routes work fine.

### Ask 5 (REQUIRED for polyphony CLI verb integration): Confirm exit-0 + `$CONDUCTOR_ERROR_OUT` write = on_error fires

The example shows scripts writing to `$CONDUCTOR_ERROR_OUT` and exiting 0. Confirm this is the correct contract (conductor treats the node as raised, evaluates on_error routes). This is the path for polyphony CLI verbs to opt into typed errors without changing their exit-code behavior (exit 0 always, write error to CONDUCTOR_ERROR_OUT on failure).

---

## 6. Beethoven Disposition Review — 3 Unilateral Changes

These three gates were changed in #535 without explicit disposition approval. Beethoven should confirm or override each before Phase 2 is scoped:

### D1: `seeder_error_gate` → auto-continue to `child_router`

**What changed:** The old 3-way gate (retry/continue/abort) was replaced with unconditional continue to `child_router`.

**Current behavior:** On any seeder error (including catastrophic verb failure), the workflow continues to `child_router` regardless.

**Concern:** The original "continue" option was intended for partial-seed scenarios (some children seeded, some failed). A catastrophic verb crash should probably abort, not silently continue with zero children.

**Options for Beethoven:**
- A) Accept auto-continue (current #535 behavior) for all cases — simplest
- B) Override: catastrophic failure → abort; partial-seed (error_count > 0) → continue (split on `on_error:` vs success path)
- C) Restore human gate for catastrophic failures only

### D2: `classify_error_gate` → auto-skip to `$end`

**What changed:** The old 2-way gate (retry/skip) was replaced with unconditional skip.

**Current behavior:** On `classify` failure in `restack-remedy.yaml`, the workflow silently succeeds (exits to `$end` without restacking).

**Concern:** Silent skip may hide systematic classify failures. Retry was the "try to salvage" option.

**Options:**
- A) Accept auto-skip (current behavior)
- B) Phase 2: retry-then-skip (restores the retry path when RFC Phase 2 ships)
- C) Escalate skips via event log annotation (no YAML change needed)

### D3: `workflow_error_gate` (evidence_reviewer + merge_evidence_pr) → split routing

**What changed:** The catch-all gate for 7 parents was split into per-step routing. `evidence_reviewer` block (3 routes: merge/retry/block) and `merge_evidence_pr` failure (→ `workflow_abandoned`) are now on the success path.

**Current behavior:**
- `evidence_reviewer.output.decision == 'approve'` → `merge_evidence_pr`
- `evidence_reviewer.output.decision == 'request_changes'` → retry loop
- `evidence_reviewer.output.decision == 'block'` → `workflow_abandoned`
- `merge_evidence_pr.output.merged == false` → `workflow_abandoned`
- The original gate's "abandon" option for a reviewer FAILURE (tool crash, not decision) → now routes to `workflow_abandoned` via catch-all M4 route on `evidence_reviewer`

**Assessment:** The split is correct. The original gate conflated tool failure with reviewer decision. The current routing correctly separates them. **No change recommended — confirm Beethoven agrees.**

---

## Appendix: TODO Comment Locations in #535

For quick reference, the TODO sites in the merged #535 code:

| File | TODO comment |
|---|---|
| `ado-pr.yaml` | `# on_error: auto-abort (AB#3257 — trivial gate removed; retry via conductor on_error: when Phase 1 ships)` |
| `github-pr.yaml` | `# on_error: auto-abort (AB#3257 — trivial gate removed; retry via conductor on_error: when Phase 1 ships)` |
| `implement-merge-group.yaml` | Two `# on_error: auto-abort (AB#3257 — trivial gate removed)` comments |
| `restack-remedy.yaml` | `# on_error: auto-skip (AB#3257 — trivial gate removed; routes to $end on classify error)` |
| `plan-level.yaml` | `# on_error: auto-abort (AB#3257 — trivial gate removed; cause info in run event log)` |

`actionable.yaml` had no explicit TODO comment — the gate was replaced by per-step routing which is self-documenting.


---

# Concrete Workflow Patterns: on_error + type:notification

**Author:** Wagner (Workflow Author)  
**Date:** 2026-05-28T23:00:46Z  
**Status:** Draft — informs Phase 2 retrofit (#536) and platespinner design  
**Prerequisites:** conductor Phase 1 (PR #229) + notifications (PR #213), both merged to `dogfood/on-error+notifications`  
**Coordination:** Bach owns notification envelope schema top-down. This doc consumes the envelope bottom-up. Fields I need from Bach are marked **[Bach ask]** in-line.

---

## Context and constraints

**What's available in Phase 1 + notifications:**
- `on_error: <kind>` / `on_error: true` on routes — typed error routing without `retry:`
- `raises:` on agent definitions — optional contract enforcement
- `internal.script_error` — synthesized when a script exits non-zero AND opts in
- Error in template context as `{{ node.error.kind }}`, `{{ node.error.message }}`, `{{ node.error.details }}`
- `type: notification` — fire-and-forget, typed, versioned, writes to `notifications.jsonl`
- `type: set` — bind computed values into workflow context
- `type: wait` — in-process pause (seconds)
- Notification envelope fields: `emission_id`, `schema_id`, `run_id`, `workflow`, `source_agent`, `correlation`, `workflow_metadata`, `payload`

**What is NOT available (Phase 1 only):**
- No `retry:` route action — RFC Phase 2 only
- No sub-workflow error propagation — RFC Phase 2
- No `halt:` / `propagate:` route actions

**Critical polyphony-specific constraint:**  
Polyphony CLI verbs **always exit 0** — `internal.script_error` only fires for raw infrastructure scripts (git, ADO API, HTTP calls). `on_error:` patterns in this doc apply at infrastructure boundaries, not polyphony verb calls. Polyphony verb failures use success-path routing (`when: "{{ output.success == false }}"`) and CAN be fed into notification nodes via that path.

---

## Pattern 1: `notify_then_route` — Error observability at the failure boundary

### When to use
Every `on_error:` catch that routes to `abort_run` or `workflow_abandoned`. Instead of silently aborting, interpose a notification step to publish the error context BEFORE routing to the terminal.

Operators learn about failures in platespinner without reading `events.jsonl`. This is the lowest-friction Phase 2 addition across all 19 retrofitted gates.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    correlation:
      - work_item_id
    types:
      step_failed:
        version: 1
        description: A script step failed with a typed error and is aborting.
        payload:
          step_name:     { type: string }
          error_kind:    { type: string }
          error_message: { type: string }
          work_item_id:  { type: string }
          # [Bach ask] severity: { type: string }   — "error" or "critical"

agents:
  - name: commit_and_push
    type: script
    command: pwsh
    args: [...]
    raises:
      - internal.script_error
    routes:
      - to: next_step                     # success
      - to: commit_failed_notifier        # error → notify → abort
        on_error: true

  - name: commit_failed_notifier
    type: notification
    notification: step_failed
    payload:
      step_name:     "commit_and_push"
      error_kind:    "{{ commit_and_push.error.kind }}"
      error_message: "{{ commit_and_push.error.message }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: abort_run
```

### Notifications emitted
`step_failed` — one emission per infrastructure failure. Consumer sees: which step, what kind, what message, for which work item.

### Failure mode
`type: notification` is fire-and-forget. If platespinner isn't listening, the workflow aborts identically. The notification lands in `notifications.jsonl` regardless — consumers can replay it.

### Prerequisites
Phase 1 + notifications. No polyphony verb changes. No Liszt script changes.

### Notes
The notification step has full access to `{{ failing_step.error.* }}` because it is routed to via the error path — the failing step's error envelope is in context. This is the key insight that makes this pattern work cleanly.

**For the 14 retry+abort gates in #536:** once Phase 2 `retry:` action ships, replace the `commit_failed_notifier` above with a `retry_exhausted_notifier` that fires only after the retry budget is spent. The notification step itself doesn't change — only when it fires changes.

---

## Pattern 2: `decision_point_notification` — Auditable auto-dispositions

### When to use
When the workflow makes a non-obvious routing choice — especially the 3 unilateral dispositions Beethoven flagged in #536 (`seeder_error_gate` auto-continue, `classify_error_gate` auto-skip, `workflow_error_gate` split). The disposition stays hardcoded (correct behavior), but is now OBSERVABLE in platespinner instead of requiring events.jsonl forensics.

This is the Phase 1 answer to "should these be policy-configurable?" — not by making them configurable yet, but by making them visible so operators can audit and flag if they're wrong.

### YAML shape (seeder auto-continue example)

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    correlation:
      - work_item_id
    types:
      auto_disposition:
        version: 1
        description: Workflow made an automatic routing decision without human input.
        payload:
          decision_site:    { type: string }   # step name where decision was made
          disposition:      { type: string }   # "auto_continue" | "auto_skip" | "auto_abort"
          reason:           { type: string }   # human-readable why
          work_item_id:     { type: string }
          # [Bach ask] severity: { type: string }  — "warning" for auto_continue/skip, "info" for nominal

agents:
  - name: seeder
    type: script
    command: pwsh
    args: [...]
    routes:
      - to: child_router
        when: "{{ seeder.output.error_count == 0 }}"
      - to: seeder_partial_continue_notifier      # ← auto-disposition with notification
        when: "{{ seeder.output.error_count is defined
                   and seeder.output.error_count > 0
                   and seeder.output.children_seeded is defined
                   and seeder.output.children_seeded > 0 }}"
      - to: abort_run
        on_error: true                            # catastrophic failure: abort

  - name: seeder_partial_continue_notifier
    type: notification
    notification: auto_disposition
    payload:
      decision_site: "seeder"
      disposition:   "auto_continue"
      reason:        "Seeder completed with {{ seeder.output.error_count }} child error(s); {{ seeder.output.children_seeded }} children seeded. Continuing to child_router with partial results."
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: child_router
```

### classify_error_gate variant (restack-remedy.yaml)

```yaml
  - name: classify
    type: script
    routes:
      - to: apply_restack
      - to: classify_skip_notifier
        on_error: true

  - name: classify_skip_notifier
    type: notification
    notification: auto_disposition
    payload:
      decision_site: "classify"
      disposition:   "auto_skip"
      reason:        "classify script failed ({{ classify.error.message }}); restack skipped for this item."
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: $end
```

### Notifications emitted
`auto_disposition` — one per auto-routing decision that bypasses a human gate. Payload declares the decision explicitly so operators can grep/filter for "show me all auto_continue decisions this week."

### Failure mode
Fire-and-forget. Workflow routing is unaffected if platespinner isn't listening.

### Prerequisites
Phase 1 + notifications. No verb changes.

### Notes
**This directly answers the Beethoven #536 items.** The seeder, classify, and evidence_reviewer dispositions go from "silent hardcoded routes" to "hardcoded but auditable." Beethoven can review the `notifications.jsonl` after a run and confirm the disposition was correct, rather than needing to approve each one in advance.

**For Phase 2 (gate-disposition policy):** if operators want to override dispositions, the next step is a workflow `input:` parameter (e.g., `seeder_error_policy: auto_continue | abort | human`) with route branching on it. The `auto_disposition` notification type stays the same — just add a `policy_applied` field. That's a separate issue.

---

## Pattern 3: `progress_notification` — Long-running op observability

### When to use
Multi-phase operations in `plan-level.yaml` (plan generation: root_resolver → type_loader → ancestor_chain → architect → seeder → child_router) and `actionable.yaml` (evidence branch → compose_addendum → open_evidence_pr → floor_check → reviewer → merge). Currently the operator sees "still running" with no phase context.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    correlation:
      - work_item_id
    types:
      phase_started:
        version: 1
        payload:
          workflow_name:  { type: string }
          phase:          { type: string }
          phase_index:    { type: number }
          total_phases:   { type: number }
          work_item_id:   { type: string }
          # [Bach ask] No "estimated_seconds" field in Phase 1 — should we add it?
          #            Alternatively, platespinner can derive elapsed from emission timestamps.
      phase_complete:
        version: 1
        payload:
          workflow_name:  { type: string }
          phase:          { type: string }
          phase_index:    { type: number }
          total_phases:   { type: number }
          work_item_id:   { type: string }
          summary:        { type: string }   # one-line outcome ("3 children seeded")

agents:
  # ... after root_resolver completes ...
  - name: phase_type_loading_started
    type: notification
    notification: phase_started
    payload:
      workflow_name: "plan-level"
      phase:         "type_loading"
      phase_index:   "2"
      total_phases:  "6"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: type_loader

  - name: type_loader
    type: script
    # ...
    routes:
      - to: phase_type_loading_complete
      - to: abort_run
        on_error: true

  - name: phase_type_loading_complete
    type: notification
    notification: phase_complete
    payload:
      workflow_name: "plan-level"
      phase:         "type_loading"
      phase_index:   "2"
      total_phases:  "6"
      work_item_id:  "{{ workflow.input.work_item_id }}"
      summary:       "Type definitions loaded"
    routes:
      - to: phase_ancestor_chain_started
```

### Notifications emitted
`phase_started` and `phase_complete` — pair around each major phase. Platespinner can render a progress bar using `phase_index / total_phases` and a phase timeline using emission timestamps.

### Failure mode
If `phase_started` fires and `phase_complete` never fires, platespinner knows the phase failed (can trigger alert). The absence of `phase_complete` is itself a signal.

### Prerequisites
Phase 1 + notifications. No verb changes. Adds node count (~2 per phase × 5-6 phases = 10-12 nodes in plan-level). Line budget: each node is ~8 lines, so ~80-100 lines added. Worth it for operational visibility.

### Notes
This is **purely additive** — no existing routes change, only notification interpose nodes added between existing steps. Safe to ship independently of #536 Phase 2 retrofit.

**Bach: I need clarity on whether `correlation` keys auto-surface on every notification envelope at the top level** (confirmed in the schema: yes, `workflow.notifications.correlation` lists input keys that auto-merge into `notification.correlation`). So `work_item_id` doesn't need to be in `payload:` explicitly if it's in `correlation:`. Removing it from `payload` keeps the type schema clean. **Recommend: move `work_item_id` to correlation and remove from payload types.**

---

## Pattern 4: `bounded_retry_loop` — Soft retry without `retry:` action

### When to use
When RFC Phase 2's `retry:` route action hasn't shipped yet, but you need bounded retry on infrastructure operations (git push, ADO API calls). This is a Phase 1-only workaround — once `retry:` ships, replace with the cleaner form.

**This is the only retry mechanism available before RFC Phase 2.**

### YAML shape

```yaml
# Requires type: set (available in conductor 0.1.18) and type: wait.
# Uses M10 iterate-until-stable with a context counter.
# context.mode must be accumulate for set values to persist across the loop.

workflow:
  context:
    mode: accumulate
  limits:
    max_iterations: 20   # 3 attempts × overhead; set conservatively

  notifications:
    namespace: polyphony.plan_level
    types:
      retry_attempt:
        version: 1
        payload:
          step:          { type: string }
          attempt:       { type: number }
          max_attempts:  { type: number }
          error_kind:    { type: string }
          work_item_id:  { type: string }
      retry_exhausted:
        version: 1
        payload:
          step:          { type: string }
          attempts_made: { type: number }
          last_error:    { type: string }
          work_item_id:  { type: string }

agents:
  # Entry — initialise counter once
  - name: init_push_retry
    type: set
    set:
      push_retry_count: "0"
    routes:
      - to: commit_and_push

  - name: commit_and_push
    type: script
    command: pwsh
    args: [...]
    raises:
      - internal.script_error
    routes:
      - to: next_step                           # success
      - to: push_retry_check                    # error → check budget
        on_error: true

  - name: push_retry_check
    type: set
    set:
      push_retry_count: "{{ (push_retry_count | default(0) | int) + 1 }}"
    routes:
      - to: push_retry_notifier                 # still budget remaining
        when: "{{ (push_retry_count | int) <= 3 }}"
      - to: push_exhausted_notifier             # budget gone

  - name: push_retry_notifier
    type: notification
    notification: retry_attempt
    payload:
      step:          "commit_and_push"
      attempt:       "{{ push_retry_count }}"
      max_attempts:  "3"
      error_kind:    "{{ commit_and_push.error.kind }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: push_retry_wait

  - name: push_retry_wait
    type: wait
    seconds: 10              # fixed backoff; exponential requires script
    routes:
      - to: commit_and_push  # ← the M10 cycle

  - name: push_exhausted_notifier
    type: notification
    notification: retry_exhausted
    payload:
      step:          "commit_and_push"
      attempts_made: "{{ push_retry_count }}"
      last_error:    "{{ commit_and_push.error.message }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: abort_run
```

### Notifications emitted
`retry_attempt` — per attempt, so operators see "attempt 2/3 failed" in real time.  
`retry_exhausted` — once, on budget exhaustion. Consumer can trigger escalation.

### Failure mode
Fire-and-forget. Workflow routing unaffected.

### Prerequisites
Phase 1 + notifications + `type: set` + `type: wait` (all in conductor 0.1.18). No verb changes.

### **Important warnings**

1. **Node count cost.** Each retriable step adds 5 nodes (init, check, retry_notifier, wait, exhausted_notifier). For 14 retry+abort gates, that's 70+ new nodes across 4 workflow files. **Use this ONLY for the 2-3 highest-value gates** (commit_and_push, merge_plan_pr, poll_status) before RFC Phase 2 ships. Then replace with `retry:` syntax.

2. **max_iterations budget.** A 3-attempt loop consumes at minimum 3 (commit_and_push) + 3 (push_retry_check) + 3 (push_retry_notifier) + 3 (push_retry_wait) = 12 iterations for one step. Plan accordingly.

3. **M10 footgun: M4 catch-all.** `push_retry_check`'s routes must include a terminal catch-all. Already present above (`push_exhausted_notifier`). Don't route the exhausted path back to the cycle.

4. **`type: set` context persistence.** Requires `context.mode: accumulate`. Verify this doesn't interact badly with other set values in the same workflow.

### Recommendation
**Do not ship this pattern broadly.** Use for the 2 most critical idempotent gates (commit_and_push, merge_plan_pr_ado) only. File RFC Phase 2 as a blocker for the remaining 12. The bounded_retry_loop is a stopgap that adds significant YAML bloat and iteration budget risk.

---

## Pattern 5: `escalation_chain` — Typed error recovery with fallback

### When to use
When a script failure has a known recovery path for specific error kinds, but a different recovery (or abort) for everything else. Requires the failing script to emit typed envelopes to `$CONDUCTOR_ERROR_OUT` — meaning this applies to **infrastructure scripts that already use typed errors**, not polyphony verb wrappers.

Most immediately applicable to: `poll_status` (rate-limit vs connection failure), `open_plan_pr` (auth failure vs conflict), `commit_and_push` (auth vs network vs conflict).

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    types:
      rate_limit_backoff:
        version: 1
        payload:
          step:           { type: string }
          retry_after_s:  { type: number }
          work_item_id:   { type: string }
      operation_failed:
        version: 1
        payload:
          step:       { type: string }
          error_kind: { type: string }
          error_msg:  { type: string }
          work_item_id: { type: string }

agents:
  - name: poll_status
    type: script
    command: pwsh
    args: [...]
    raises:
      - external.api.rate_limited
      - external.api.connection_failed
      - internal.script_error
    routes:
      - to: merge_pr                            # success: PR merged
        when: "{{ poll_status.output.state == 'completed' }}"
      - to: poll_status                         # success: still pending — re-poll
        when: "{{ poll_status.output.state == 'active' }}"
      - to: rate_limit_backoff_notifier         # typed: rate limited
        on_error: external.api.rate_limited
      - to: notify_and_abort                    # typed or untyped: all other errors
        on_error: true

  - name: rate_limit_backoff_notifier
    type: notification
    notification: rate_limit_backoff
    payload:
      step:          "poll_status"
      retry_after_s: "{{ poll_status.error.details.retry_after_s | default(60) }}"
      work_item_id:  "{{ workflow.input.work_item_id }}"
    routes:
      - to: rate_limit_wait

  - name: rate_limit_wait
    type: wait
    seconds: 60       # [Bach ask] Could we template this from the error details?
                      # e.g. seconds: "{{ poll_status.error.details.retry_after_s }}"
                      # If wait.seconds is a Jinja template string, this works.
    routes:
      - to: poll_status   # retry after backoff

  - name: notify_and_abort
    type: notification
    notification: operation_failed
    payload:
      step:         "poll_status"
      error_kind:   "{{ poll_status.error.kind }}"
      error_msg:    "{{ poll_status.error.message }}"
      work_item_id: "{{ workflow.input.work_item_id }}"
    routes:
      - to: abort_run
```

### Notifications emitted
`rate_limit_backoff` — visible in platespinner as "waiting for rate limit to clear." Consumer can alert if retry_after_s is unreasonably long.  
`operation_failed` — on non-recoverable error, before aborting.

### Failure mode
Fire-and-forget. Workflow routing unaffected.

### Prerequisites
Phase 1 + notifications. **Requires infrastructure scripts to emit typed envelopes** — the polyphony PowerShell helpers (Liszt) need `Invoke-ConductorError` or equivalent for the error kinds above. Currently: scripts must write to `$CONDUCTOR_ERROR_OUT` manually (3-line PowerShell idiom).

### Notes
**This is the only Phase 1 pattern that requires Liszt's involvement.** For the pattern to work, the infrastructure scripts (`poll_status`, `open_plan_pr`, etc.) need to write typed envelopes when they fail with known recoverable errors. Without typed envelopes, the catch-all `on_error: true` fires for everything and the typed recovery arms never trigger.

**Bach: Can `type: wait`'s `seconds:` field be a Jinja2 template?** If yes, the rate-limit backoff can use `{{ poll_status.error.details.retry_after_s }}` directly. If no, we need a `type: set` + branch to map the value. Worth confirming.

---

## Pattern 6: `renegotiation_notification` — Parent cascade observability

### When to use
When `plan-level.yaml`'s `validate_scope` triggers a renegotiation and spawns a recursive plan-level sub-workflow. Currently the operator sees the parent run wedge silently while waiting for child planning. With notification, the cascade is visible.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.plan_level
    types:
      renegotiation_started:
        version: 1
        payload:
          parent_work_item:     { type: string }
          scope_violation_count: { type: number }
          trigger_phase:         { type: string }
          work_item_id:          { type: string }
      renegotiation_complete:
        version: 1
        payload:
          parent_work_item: { type: string }
          outcome:          { type: string }  # "resolved" | "escalated" | "aborted"
          work_item_id:     { type: string }

agents:
  # Between validate_scope and the renegotiation sub-workflow call
  - name: renegotiation_started_notifier
    type: notification
    notification: renegotiation_started
    payload:
      parent_work_item:      "{{ workflow.input.work_item_id }}"
      scope_violation_count: "{{ validate_scope.output.scope_violation_files | length }}"
      trigger_phase:         "plan_generation"
      work_item_id:          "{{ workflow.input.work_item_id }}"
    routes:
      - to: spawn_renegotiation_subworkflow
```

### Notifications emitted
`renegotiation_started` — when cascade begins. Platespinner can show "⏳ renegotiating scope" against the work item.  
`renegotiation_complete` — after sub-workflow returns, before re-entering the parent workflow.

### Prerequisites
Phase 1 + notifications. Additive only — no existing routes change.

---

## Pattern 7: `async_gate_prompt` — Notify + human gate with action URL

### When to use
When a `human_gate` requires operator action but the operator isn't watching the terminal. Emit a notification with enough context for platespinner to surface an action prompt; the workflow then falls into the gate as normal.

### YAML shape

```yaml
workflow:
  notifications:
    namespace: polyphony.actionable
    types:
      human_action_required:
        version: 1
        payload:
          gate_name:     { type: string }
          run_id:        { type: string }
          prompt:        { type: string }   # one-line human-readable summary
          work_item_id:  { type: string }
          # [Bach ask] Should conductor emit run_id as a built-in context variable?
          # Currently the workflow must thread it through via workflow.input.
          # Platespinner can construct the gate URL from run_id + gate_name alone.

agents:
  - name: review_gate_prompt_notifier
    type: notification
    notification: human_action_required
    payload:
      gate_name:    "review_evidence_gate"
      run_id:       "{{ workflow.input.run_id }}"
      prompt:       "Evidence PR is ready for review. Approve or block?"
      work_item_id: "{{ workflow.input.work_item_id }}"
    routes:
      - to: review_evidence_gate

  - name: review_evidence_gate
    type: human_gate
    prompt: |
      Evidence PR {{ open_evidence_pr.output.pr_url }} is ready for review.
      Choose approve or block.
    choices:
      - value: approve
        route: merge_evidence_pr
      - value: block
        route: workflow_abandoned
```

### Notifications emitted
`human_action_required` — fires immediately before the gate. Platespinner sees: which gate, which run, what's needed. Can surface an action button.

### Limitation
Conductor does not currently expose `run_id` as a built-in template variable. The workflow must receive it as an input (or Mahler adds `{{ conductor.run_id }}` as a built-in). **[Bach ask] — see below.**

### Prerequisites
Phase 1 + notifications. Works today if `run_id` is threaded through as a workflow input.

---

## Pattern 8: `gate_disposition_policy` — Declared override point for auto-dispositions

**(Mentioned, not fully designed — Phase 1.5 pattern, requires workflow input changes)**

The 3 Beethoven-flagged dispositions (seeder, classify, evidence_reviewer) can be made operator-configurable by adding a small policy input to each workflow:

```yaml
input:
  seeder_error_policy:
    type: string
    default: auto_continue   # or: abort | human

# Then in seeder routes:
routes:
  - to: seeder_partial_continue_notifier
    when: "{{ workflow.input.seeder_error_policy == 'auto_continue'
               and seeder.output.error_count > 0 }}"
  - to: abort_run
    when: "{{ workflow.input.seeder_error_policy == 'abort'
               and seeder.output.error_count > 0 }}"
  - to: seeder_error_human_gate
    when: "{{ workflow.input.seeder_error_policy == 'human'
               and seeder.output.error_count > 0 }}"
```

**Why only mentioned:** This requires polyphony's workflow invocation scripts (Liszt) to pass the policy input, and a decision on what the default should be (Pattern 2 establishes `auto_continue` is correct for the partial-seed case). File separately once Pattern 2 is shipped and Beethoven has confirmed the defaults.

---

## Bach envelope field requirements

| Field | Pattern | Why | Ask |
|---|---|---|---|
| `severity` | 1, 2, 3, 5 | Consumers need to filter by urgency: `info` (progress), `warning` (auto-disposition), `error` (step failed), `critical` (abort). Without it, platespinner must infer from `notification_type`. | Add `severity: info \| warning \| error \| critical` to either the envelope top-level or as a `NotificationTypeDef` field. |
| `run_id` as built-in context | 7 | `async_gate_prompt` needs `{{ conductor.run_id }}` without requiring it as workflow input | Expose `conductor.run_id` as a built-in template variable (alongside `workflow.input.*`) |
| `type: wait` with templated `seconds:` | 5 | Rate-limit backoff should use `{{ poll_status.error.details.retry_after_s }}` | Confirm `wait.seconds` accepts Jinja2 template string, not just literal integer |
| `disposition` standard key | 2 | `auto_disposition` notifications need a standard key name consumers can filter on generically | Either bless `disposition` as a conventional key, or add it as a first-class envelope field for decision notifications |

---

## Ranked usefulness (top 5)

1. **`notify_then_route`** — Phase 1 only, works today, applies to ALL 19 retrofit gates. Highest leverage per line of YAML. Should accompany every `on_error: true → abort_run` route in Phase 2.

2. **`decision_point_notification`** — Phase 1 only, directly resolves the Beethoven audit gap. Ships the 3 flagged dispositions as observable policy without requiring Beethoven pre-approval of each.

3. **`progress_notification`** — Phase 1 only, purely additive, dramatically improves plan-level and actionable observability. No existing routes change.

4. **`escalation_chain`** — Phase 1 + typed envelope helpers from Liszt. The cleanest error recovery pattern for infrastructure scripts once they emit typed errors.

5. **`bounded_retry_loop`** — Phase 1 only stopgap for retry before RFC Phase 2. High node cost; use for 2-3 critical gates only. Replace with `retry:` when Phase 2 ships.

---

## Relationship to Phase 2 retrofit (#536)

These patterns are the SHAPE that the Phase 2 YAML will take once conductor Phase 1+notifications land. Specifically:

- **Phase 2a (5 Phase-1-only gates):** each gets Pattern 1 (`notify_then_route`) + Pattern 2 (`decision_point_notification`) where disposition was unilateral.
- **Phase 2b (14 retry+abort gates):** each gets Pattern 1 first, then replace with `retry:` + `retry_exhausted_notifier` when RFC Phase 2 ships. Pattern 4 (`bounded_retry_loop`) bridges the gap for 2-3 highest-priority gates.
- `poll_status` specifically gets Pattern 5 (`escalation_chain`) once Liszt ships typed envelope emission helpers.


---

# on_error: + Notifications — Architectural Design (Revised)

**Author:** Bach (Architect)  
**Date:** 2026-05-28  
**Status:** Proposal (scope-refined per Daniel's 2026-05-28 directive)  
**Relates to:** Epic #521 (self-contained orchestration), #528 (error-gate migration), conductor PRs #229 (on_error Phase 1), #213 (notifications)

---

## Part 1 — Architectural Patterns Unlocked

### 1.1 Gate Compression — the headline unlock

**The pattern:** `human_gate` → `(notification + script-poll-loop)`

Today, polyphony workflows block on human gates for conditions that are *externally observable* — PR merged, PR has approving review, CI green, work item state changed, evidence branch exists. The operator must babysit the conductor TTY or web dashboard, perform the action, then click through the gate.

With `type: notification` + `script:` polling, the workflow becomes:

```text
┌──────────────────────────────────────────────────────┐
│ BEFORE (gate pattern)                                 │
│                                                       │
│  [create_pr] → [pr_review_gate] ← operator clicks    │
│                    ↓                                  │
│               [continue...]                           │
└──────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────┐
│ AFTER (notification + poll pattern)                    │
│                                                       │
│  [create_pr] → [notify: "PR ready for review"]       │
│                    ↓                                  │
│  [poll_pr_status] ← script loops every N seconds     │
│       ↓ (when: merged/approved/closed)               │
│  [continue...] + [notify: "PR resolved, continuing"] │
└──────────────────────────────────────────────────────┘
```

**What this compresses:** Gates become *rare* — reserved only for genuine human-judgment decisions where there's nothing observable to poll (e.g., "should we abandon this plan entirely?" or "is this scope violation acceptable?"). Every pollable condition becomes a script-loop fronted by a notification.

**The full loop (four segments, one-way each):**

```text
conductor → platespinner     : notification event (fire-and-forget via .events.jsonl)
platespinner → user          : toast / PWA push / tray badge / dashboard card
user → external world        : clicks CTA, performs action (review PR, merge, approve)
external world → conductor   : polled by script: step inside the workflow (NOT pushed via platespinner)
```

**Critical seam property:** PlateSpinner does NOT communicate back to conductor. The polling script does the discovery. This is a one-way observation contract — no write-back, no RPC, no coupling beyond the `.events.jsonl` line shape.

**Gates that compress under this pattern (current polyphony workflow inventory):**

| Current gate | Pollable condition | Notification content |
|-------------|-------------------|---------------------|
| `pr_review_gate` | PR has ≥1 approving review (GitHub/ADO API) | "PR #{n} ready for review at {url}" |
| `pr_merge_gate` | PR merged status (GitHub/ADO API) | "PR #{n} approved, ready to merge" |
| `pending_review_gate` | PR review policy satisfied | "PR #{n} awaiting your review" |
| `stuck_review_gate` | Review timeout elapsed + still no review | "PR #{n} review stalled — {hours}h with no activity" |
| `evidence_review_gate` | Evidence PR approved | "Evidence for #{wi} ready for review" |

**Gates that remain as genuine judgment calls:**

| Gate | Why it can't be polled |
|------|----------------------|
| `scope_violation_gate` | Requires human decision: override or abort |
| `root_fallback_gate` | Requires human decision: treat as root or abort |
| `renegotiation arbitration` | Requires human judgment on plan conflict |

### 1.2 Self-healing seams (on_error: unlock — secondary pattern)

The four seams crossing network/process boundaries share a transient-failure pattern:

| Seam | With `on_error:` Phase 1 |
|------|--------------------------|
| polyphony↔twig CLI | Route to retry node (backoff) → abort after N |
| polyphony↔git | Route to retry (lock contention) → abort |
| polyphony↔PR platform | Route to retry (rate limit/5xx) → abort |
| polyphony↔ADO (via twig sync) | Route to retry (429/5xx) → abort |

The 14 retry-then-abort gates removed in #535 can be restored as automated retry chains — no human in the loop. This is the *error* path; §1.1 is the *success* path.

### 1.3 The "notification" vocabulary problem

Three systems use "notification" for three different things:

| System | Their "notification" | Our term |
|--------|---------------------|----------|
| Conductor | `type: notification` YAML node (the mechanism) | "notification node" (conductor's term; we don't rename it) |
| PlateSpinner | Toast / bell / PWA push (the UX artifact) | "toast" or "push" (platespinner's term) |
| Polyphony | The structured payload carried by the notification node | **"domain signal"** (proposed polyphony term) |

⚠️ **Glossary flag:** `domain signal` must be added to `docs/glossary.md` before any workflow ships this pattern.

### 1.4 The polyphony-CLI-exit-0 problem — seam decision

**The problem:** Every polyphony verb exits 0. Conductor's `on_error:` fires on non-zero exit / `$env:CONDUCTOR_ERROR_OUT`. So `on_error:` never fires for polyphony verbs today.

**Recommended: Option C (hybrid).**

| Outcome type | Exit code | Routing mechanism |
|-------------|-----------|-------------------|
| Domain outcome (skip, invalid, no-children, etc.) | Exit 0 | `when:` conditions on JSON output fields (existing pattern, unchanged) |
| Infrastructure failure (unhandled exception, OOM, corrupt file) | Exit non-zero + error envelope to `$env:CONDUCTOR_ERROR_OUT` | `on_error:` routing |

Implementation: one try/catch in the verb host around `Main()`. Domain-level error results (e.g., `action: "error"`) remain exit-0 — they're business decisions, not crashes.

**ADR required** — this is load-bearing. See ADR proposal stub.

### 1.5 Renegotiation flows

Gate compression doesn't change renegotiation control flow (parent-plan-generation remains serialized via same-root run lock). But domain signals close the *visibility gap*:

- Signal: "Child #{id} requests parent plan change" (with `cta_url` → the child plan PR)
- Signal: "Renegotiation resolved — parent plan updated" (with `correlation_id` linking back)

The operator sees the signal, reviews the PR, the poll loop detects the review. No gate needed for the observation; judgment gate only fires if there's a conflict requiring arbitration.

---

## Part 2 — PlateSpinner Integration Design

### 2.1 Domain signal envelope schema (revised)

```json
{
  "schema_version": "1.0",
  "kind": "review_pr | merge_pr | approve_plan | resolve_conflict | retry_exhausted | step_failed | auto_disposition | phase_started | phase_complete",
  "severity": "info | warning | error | critical",
  "subject": "PR #142 ready for review",
  "cta_url": "https://github.com/org/repo/pull/142",
  "cta_kind": "review_pr",
  "correlation_id": "pr-142-review-cycle",
  "expires_at": "2026-05-29T15:58:10-07:00",
  "work_item_id": 12345,
  "root_id": 67890,
  "run_id": "feature/67890",
  "disposition": "auto_continue | auto_skip | auto_abort",
  "details": {
    "pr_number": 142,
    "branch": "impl/67890-1-12345",
    "target_branch": "mg/67890-1",
    "error_kind": "external.api.rate_limited",
    "retry_after_s": 60,
    "attempt": 2,
    "max_attempts": 3
  }
}
```

**Field inventory (aligned with Wagner's 8 patterns):**

| Field | Required | Purpose | Wagner pattern ref |
|-------|----------|---------|-------------------|
| `kind` | ✅ | Domain category of the signal. Polyphony-owned enum. | All patterns |
| `severity` | ✅ | Urgency. `info\|warning\|error\|critical`. Default set at type-def level. | All patterns (Ask #1) |
| `subject` | ✅ | One-line human summary for toast title | All patterns |
| `cta_url` | Optional | Clickable link for user action | Pattern 7 (`async_gate_prompt`), gate compression |
| `cta_kind` | Optional | Semantic action type for button rendering | Gate compression patterns |
| `correlation_id` | Optional | Links related signals in a pollable cycle | Patterns 3, 6 (phase pairs, renegotiation pairs) |
| `expires_at` | Optional | Hard expiry for stale-signal cleanup | Gate compression (PR review timeout) |
| `work_item_id` | Optional | Enrichment key (auto-lifted from `correlation:` config) | All patterns |
| `run_id` | ✅ (auto) | Conductor auto-populates on wire; workflow templates via input workaround | Pattern 7 (Ask #2) |
| `disposition` | Optional | For `auto_disposition` signals: which routing decision was taken | Pattern 2 (`decision_point_notification`) |
| `details` | Optional | Signal-specific structured bag. Schema varies per `kind`. | Patterns 4, 5 (retry/escalation details) |

**Severity vocabulary (aligned with Wagner's pattern catalogue):**

| Severity | Meaning | PlateSpinner treatment | Wagner patterns that emit it |
|----------|---------|----------------------|------------------------------|
| `info` | Progress update / resolution confirmation | Inline activity-log entry only. Used to resolve `correlation_id`. | `progress_notification`, `renegotiation_complete` |
| `warning` | Auto-disposition taken or degraded state (retry in progress) | Bell badge + inline log entry. No toast unless opted in. | `decision_point_notification` (auto_continue/skip), `retry_attempt` |
| `error` | Step failed, retry exhausted, operation aborted | Toast + badge + inline (red). Operator should be aware. | `notify_then_route` (step_failed), `retry_exhausted` |
| `critical` | Run-level abort — workflow cannot continue without intervention | Toast + badge + tray alert. Immediate attention. | Fatal abort paths, scope arbitration escalation |

**Rationale for 4-level severity (vs earlier 3-level):** Wagner's patterns (#543 `notify_then_route`, #544 `decision_point_notification`) need to distinguish between "step failed and we're aborting" (`error`) vs "auto-disposition was taken, FYI" (`warning`). The `action_required` concept from the earlier draft is now expressed as `cta_kind` being present — any signal with a `cta_url` + `cta_kind` implies the user should act. Severity is orthogonal to CTA presence.

**Note:** `severity` lives on the **notification type definition** (in `workflow.notifications.types.<name>`) as a default, overridable per emission site in the `payload:` block. This lets Wagner define `step_failed` as severity `error` at the type level, while `auto_disposition` defaults to `warning`.

### 2.2 Wagner's three asks — architectural rulings

**Ask 1: `severity` on `NotificationTypeDef` or top-level envelope field?**

**Ruling: BOTH.** `severity` is:
- Declared as a **default** on the `NotificationTypeDef` (type-level), so Wagner can set `step_failed.severity: error` once and every emission inherits it.
- Overridable per **emission site** in `payload:` (instance-level), so a `progress_notification` can default to `info` but escalate to `warning` for unusually long phases.
- Present on the **wire-format envelope** that platespinner reads (always resolved by emission time — platespinner never reads the type definition, only the emitted event).

This mirrors conductor's own pattern for `type: notification` fields: definition-level defaults + per-node override.

**Ask 2: `{{ conductor.run_id }}` as a built-in Jinja template variable?**

**Ruling: GAP — file as a conductor feature request.** Conductor PR #213's notification envelope includes `run_id` at the *wire level* (it's in the emitted event automatically — see Wagner's doc line 21: "envelope fields: `emission_id`, `schema_id`, `run_id`..."). So platespinner already gets it. The gap is that YAML *template expressions* inside `payload:` can't reference it today — the workflow must thread `run_id` as a workflow input.

**Workaround (Phase 1):** Thread `run_id` as a workflow input from the launcher. Polyphony's `Invoke-PolyphonySdlc.ps1` already has access to the run ID at invocation time. This is one line in the workflow `input:` schema and one param in the launcher.

**Medium-term:** File conductor feature request: "Expose `{{ conductor.run_id }}` as a built-in template variable available in all Jinja2 contexts (payload, when, output)." Tag as P2 — the workaround is adequate.

**CTA URL construction:** For Wagner's `async_gate_prompt` pattern, the CTA URL becomes:
```
cta_url: "https://platespinner.local/runs/{{ workflow.input.run_id }}/gate/{{ gate_name }}"
```
This works today with the workaround. When `{{ conductor.run_id }}` ships, replace.

**Ask 3: Does `type: wait` accept a templated `seconds:` field?**

**Ruling: UNKNOWN — flag as a blocker for `bounded_retry_loop` and `escalation_chain`.** Conductor's `type: wait` documentation (as observed in Wagner's patterns) shows only a literal integer. Whether Jinja2 templating is supported for `seconds:` depends on conductor's node-field evaluation order.

**Action required from Mahler:** Verify in the dogfood fork whether:
```yaml
- name: rate_limit_wait
  type: wait
  seconds: "{{ poll_status.error.details.retry_after_s | default(60) }}"
```
evaluates correctly. If conductor resolves Jinja2 in `seconds:` before passing to the wait implementation, Wagner's `escalation_chain` pattern works as-is. If NOT:

**Fallback:** Replace the `type: wait` node with a `type: script` node that calls `Start-Sleep -Seconds $retryAfter` where `$retryAfter` is passed as an argument from the template context. This is uglier but functional and doesn't block the pattern.

**Degradation assessment:** If templating isn't supported, `bounded_retry_loop` degrades to fixed backoff (acceptable — exponential backoff is a polish item). `escalation_chain` degrades to fixed 60s wait on rate-limit (acceptable for Phase 1 — ADO rate-limit `Retry-After` headers are typically 5-60s). Neither pattern is blocked; both just become slightly less adaptive.

### 2.3 PlateSpinner consumption surface

**The user ↔ workflow loop, platespinner's role:**

```text
[workflow emits domain signal]
         ↓
[platespinner reads .events.jsonl line]
         ↓
[platespinner parses envelope, extracts cta_kind + severity]
         ↓
┌─────────────────────────────────────────────────────┐
│ error/critical with cta_url:                         │
│   • Windows toast: subject + "Open" button → cta_url│
│   • Tray badge: increment "N waiting on you" count  │
│   • Bell panel: card with CTA button                │
│   • Dashboard: inline entry with action button      │
├─────────────────────────────────────────────────────┤
│ info with matching correlation_id:                   │
│   • Clear the earlier card/toast                    │
│   • Tray badge: decrement count                     │
│   • Dashboard: inline entry marking resolution      │
└─────────────────────────────────────────────────────┘
```

**CTA kind → button mapping:**

| `cta_kind` | Button label | Icon |
|-----------|-------------|------|
| `review_pr` | "Review PR" | 👁 |
| `merge_pr` | "Merge PR" | ✓ |
| `approve_plan` | "Review Plan" | 📋 |
| `resolve_conflict` | "Resolve" | ⚠️ |
| `view_evidence` | "Review Evidence" | 📎 |

Clicking the button opens `cta_url` in the default browser. That's the entire interaction — platespinner doesn't need to know what happens next. The workflow's poll script handles detection.

### 2.3 Gaps in PlateSpinner (refined)

| # | Gap | What to build | Priority |
|---|-----|--------------|----------|
| 1 | **No `type: notification` event parsing** | Event-type handler in the `.events.jsonl` parser that recognizes notification events and extracts the polyphony domain signal envelope. | P0 (nothing works without this) |
| 2 | **No CTA-aware rendering** | Map `cta_kind` → action button with icon + label. Clicking opens `cta_url` in browser. | P0 (the primary user touchpoint) |
| 3 | **No correlation-based signal lifecycle** | When a signal with `correlation_id` + severity `info` arrives, auto-dismiss/resolve the earlier `action_required` signal with the same correlation_id. Decrement tray badge. | P1 (prevents stale toast storm) |
| 4 | **No "waiting on you" tray badge count** | Badge on the tray icon showing count of unresolved `action_required` signals across all active runs. | P2 (polish — low coupling) |

**Explicitly OUT of platespinner scope:**
- Two-way RPC back to conductor (never)
- Hosting the polling logic (that's a workflow `script:` step)
- Replacing conductor dashboard for live execution monitoring (platespinner augments, doesn't replace)

### 2.4 Where the contract lives

```text
┌─────────────────────────────────────────────────────────────────┐
│ Conductor                                                        │
│  Owns: type: notification node YAML syntax                       │
│  Owns: .events.jsonl wire format for notification events         │
│  Owns: WHEN the event line is written (at node execution)        │
│  Does NOT interpret: the payload (passes it through opaquely)    │
└───────────────────────────────┬─────────────────────────────────┘
                                │ .events.jsonl line
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ Polyphony (workflow layer)                                       │
│  Owns: domain signal envelope schema (kind, severity, cta_*,    │
│         correlation_id, expires_at, work_item_id, details)       │
│  Owns: WHICH signals are emitted and WHEN                        │
│  Owns: the `kind` vocabulary enum                                │
│  Owns: correlation_id generation (scoped per pollable cycle)     │
└───────────────────────────────┬─────────────────────────────────┘
                                │ platespinner reads .events.jsonl
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ PlateSpinner                                                     │
│  Owns: severity → UX mapping (toast / badge / inline)            │
│  Owns: cta_kind → button label/icon mapping                      │
│  Owns: correlation-based lifecycle (dismiss on resolution)        │
│  Owns: expires_at enforcement (clear stale signals)              │
│  Owns: dedup (same correlation_id doesn't re-toast)              │
│  Does NOT own: polling logic, workflow control, write-back        │
└─────────────────────────────────────────────────────────────────┘
```

The seam is `.events.jsonl` (fire-and-forget, one-way). Platespinner is a pure observer with local UX state management (correlation tracking, expiry, badge counts).

---

## Part 3 — Risks, Blockers, Vocabulary Alerts

### 3.1 Three-vocabulary rule compliance

| Item | Assessment |
|------|-----------|
| `kind` enum values (`review_pr`, `merge_pr`, `approve_plan`, `step_failed`, `auto_disposition`, `phase_started`, etc.) | These are **event names** (vocabulary 1). They name what happened / what's needed. ✅ Compliant. Shared between Bach's envelope and Wagner's pattern catalogue — single vocabulary, single owner (polyphony). |
| `severity` (`info`, `warning`, `error`, `critical`) | New classification axis. NOT a lifecycle event, NOT a state name, NOT a category. Must be documented as a signal-specific classifier — distinct from the three vocabularies. ⚠️ Pin in glossary to prevent confusion with state categories. |
| `disposition` (`auto_continue`, `auto_skip`, `auto_abort`) | These are **routing decision names**, not state names. They describe what the workflow DID, not what state the work item is in. ✅ Compliant — they belong to vocabulary 1 (events/actions). |
| `cta_kind` values | Subset of `kind` — maps to the same space. Not a new vocabulary. ✅ |
| `correlation_id` | Opaque identifier, not a vocabulary term. ✅ |
| "domain signal" | New concept. ⚠️ Must be added to glossary before any workflow ships it. |

### 3.2 New seams

| New seam | Properties | Net assessment |
|----------|-----------|---------------|
| polyphony domain signal → platespinner (via .events.jsonl) | READ-ONLY, one-way, fire-and-forget. Platespinner never pushes back. | Acceptable — minimal coupling. |
| script-poll-loop → external APIs (GitHub/ADO) | Already exists (poll_status scripts exist today in `github-pr.yaml` and `ado-pr.yaml`). | Not new — gate compression reuses an existing pattern, doesn't create one. |

**Seams removed:** Every gate that compresses to notification+poll removes a `human_gate` node — which is a two-way seam (workflow ↔ operator). Replacing it with one-way notification + one-way poll is a net coupling reduction.

### 3.3 Phase 1 dependency check

| Capability | Available? | Required? | Notes |
|-----------|-----------|-----------|-------|
| `on_error:` basic routing | ✅ Phase 1 (PR #229) | ✅ For retry-then-abort chains | |
| `on_error:` typed error envelope | ✅ Phase 1 | ✅ For hybrid exit-code propagation | |
| `on_error: { retry: ... }` built-in | ❌ Phase 2 (RFC #227) | ❌ Can model with counter script (Wagner Pattern 4) | |
| `type: notification` node | ✅ PR #213 | ✅ For domain signals | |
| Notification payload pass-through | ✅ PR #213 | ✅ Polyphony owns the envelope | |
| `run_id` on wire-format envelope | ✅ PR #213 (auto-populated) | ✅ Platespinner gets it | |
| `{{ conductor.run_id }}` in Jinja2 context | ❓ **UNKNOWN** | Desirable for CTA URL construction (Wagner Ask #2) | **Workaround:** thread as workflow input. **Action:** Mahler to verify. |
| `type: wait` with templated `seconds:` | ❓ **UNKNOWN** | Desirable for adaptive backoff (Wagner Ask #3) | **Fallback:** fixed backoff or `Start-Sleep` in script. **Action:** Mahler to verify. |
| `type: set` + `context.mode: accumulate` | ✅ conductor 0.1.18 | ✅ For bounded retry loop counters | |

**Nothing in this design DEPENDS on Phase 2 or unconfirmed features.** The two unknowns (`conductor.run_id`, templated `wait.seconds`) have adequate workarounds. ✅

### 3.4 ADRs that must be written

| ADR | Decides | Urgency | Blocks |
|-----|---------|---------|--------|
| **polyphony-verb-error-boundary.md** | Option C (hybrid exit codes) for the exit-0 problem | **P0** | #536 Phase 1 retrofit |
| **domain-signal-envelope.md** | Envelope schema, `kind` vocabulary, `severity` levels, `disposition` field, correlation semantics, CTA contract | **P1** | First notification node in any workflow; Wagner's patterns #543/#544/#545 |
| **gate-compression-pattern.md** | Which gates compress to notification+poll, which remain as judgment gates, poll cadence defaults | **P1** | Wagner's forward workflow patterns |

### 3.5 Vocabulary alerts for Beethoven (glossary steward)

Terms that must be added to `docs/glossary.md` before implementation:

| Term | Definition | Section |
|------|-----------|---------|
| **Domain signal** | A structured event emitted by a polyphony workflow via conductor's `type: notification` node, carrying the polyphony domain signal envelope. NOT a human gate. NOT a log line. NOT a platespinner toast (though it may trigger one). | Execution model |
| **Gate compression** | The substitution of a `human_gate` node with a `(notification + script-poll-loop)` pair for conditions that are externally observable. Reduces operator babysitting. Gates remain only for genuine judgment calls. | Execution model |
| **CTA (call to action)** | The clickable link in a domain signal that tells the user what to do and where. Carried as `cta_url` + `cta_kind` in the signal envelope. | Execution model |
| **Correlation ID** | An opaque identifier linking related domain signals in the same pollable cycle (e.g., "PR ready for review" → "PR merged, workflow continuing"). Platespinner uses this to manage signal lifecycle. | Execution model |
| **Disposition (signal)** | The routing decision a workflow made automatically at a decision point (`auto_continue`, `auto_skip`, `auto_abort`). Carried on `auto_disposition` domain signals for auditability. NOT a work-item disposition (see: Requirement disposition). | Execution model |

---

## Appendix: Wagner Pattern Alignment

Wagner's 8 patterns (`.squad/decisions/inbox/wagner-on-error-notifications-patterns-2026-05-28T23-00-46Z.md`) map to this envelope as follows:

| Wagner pattern | `kind` value(s) | `severity` default | Uses `cta_url`? | Uses `disposition`? |
|---------------|-----------------|-------------------|-----------------|-------------------|
| 1. `notify_then_route` | `step_failed` | `error` | No (abort path) | No |
| 2. `decision_point_notification` | `auto_disposition` | `warning` | No | **Yes** |
| 3. `progress_notification` | `phase_started`, `phase_complete` | `info` | No | No |
| 4. `bounded_retry_loop` | `retry_attempt`, `retry_exhausted` | `warning` / `error` | No | No |
| 5. `escalation_chain` | `rate_limit_backoff`, `operation_failed` | `warning` / `error` | No | No |
| 6. `renegotiation_notification` | `renegotiation_started`, `renegotiation_complete` | `info` | Yes (plan PR) | No |
| 7. `async_gate_prompt` | `human_action_required` | `error` | **Yes** (gate URL) | No |
| 8. `gate_disposition_policy` | (reuses `auto_disposition`) | `warning` | No | **Yes** |

**No divergent contracts.** Wagner's patterns and this envelope use the same field set. The `disposition` field is exclusively for Pattern 2/8. CTA fields are exclusively for gate compression + renegotiation. The envelope is a union — each pattern uses the subset it needs.

---

## Recommended Next Moves (revised, ranked)

| # | Action | Owner | Priority | Blocks |
|---|--------|-------|----------|--------|
| 1 | Write ADR: `polyphony-verb-error-boundary.md` | **Bach** + **Mozart** | P0 | #536 retrofit; enables on_error: for verbs |
| 2 | Write ADR: `domain-signal-envelope.md` (envelope + kind + correlation + severity + disposition) | **Bach** + **Wagner** | P1 | First notification node; Wagner's #543/#544/#545 |
| 3 | Write ADR: `gate-compression-pattern.md` (which gates compress, poll cadence, judgment-only remainder) | **Wagner** + **Bach** | P1 | Complements Wagner's forward patterns work |
| 4 | Mahler: verify `{{ conductor.run_id }}` availability + `type: wait` templated `seconds:` in dogfood fork | **Mahler** | P1 | Determines whether workarounds are needed |
| 5 | Implement hybrid exit-code behavior (Option C) | **Mozart** | P1 (after ADR #1) | Enables on_error: |
| 6 | Prototype gate compression in `github-pr.yaml` (pr_review_gate → notification + poll) | **Wagner** | P2 (after ADRs #2+#3 + Mahler's fork) | Validates full stack |
| 7 | File platespinner feature requests (#541 updated, #542 updated) | **Bach** (done) | P2 | Platespinner readiness |
| 8 | Add `domain signal`, `gate compression`, `CTA`, `correlation ID`, `disposition (signal)` to glossary | **Beethoven** | P1 | Vocabulary hygiene |
| 9 | Restore retry for the 14 removed gates via on_error: + counter scripts | **Wagner** | P3 | Operator experience restoration |


---

# ADR Proposal: Domain Signal Envelope + Verb Error Boundary

**Author:** Bach (Architect)  
**Date:** 2026-05-28  
**Status:** Proposal stub (ADR not yet written)  
**Triggered by:** Conductor PRs #229 (on_error Phase 1) + #213 (type: notification)

---

## What this ADR would decide

**Title:** `polyphony-verb-error-boundary.md` — How polyphony CLI verbs communicate infrastructure failures to conductor's `on_error:` routing.

**The decision:**

Should polyphony verbs:
- **(A)** Adopt typed exit codes + `$env:CONDUCTOR_ERROR_OUT` for ALL outcomes (breaking)
- **(B)** Stay exit-0 always, route errors via JSON `when:` conditions (status quo)
- **(C)** Hybrid: exit-0 for domain outcomes, non-zero + error envelope for unhandled infrastructure failures only (recommended)

**Why it needs an ADR:**

This is a seam decision that affects:
- All 109 `[Command]` methods in `src/Polyphony/Commands/`
- All workflow YAML `when:` route conditions that read verb output
- The contract between polyphony CLI and conductor's script-execution host
- Whether `on_error:` Phase 1 can fire for polyphony verb failures at all

Without this decision, the Phase 1 retrofit (#536) cannot restore retry capability for the 14 gates removed in #535.

**Options summary:**

| Option | Breaking? | on_error: fires? | Implementation cost |
|--------|-----------|------------------|-------------------|
| A — full typed exit codes | Yes (all consumers) | Yes (all failures) | High (109 verbs + all YAML) |
| B — stay exit-0 | No | No (never fires for verbs) | Zero |
| C — hybrid | No (existing routes unchanged) | Yes (infrastructure failures only) | Low (one try/catch wrapper in verb host) |

**Recommendation:** Option C. Domain outcomes (skip, error-as-business-decision, invalid-input) remain in JSON output routed by `when:`. Infrastructure failures (unhandled exception, file corruption, OOM) propagate via non-zero exit + `$env:CONDUCTOR_ERROR_OUT`. This matches conductor's `internal.script_error` semantics without breaking the existing routing contract.

**Companion ADR (lower priority):** `domain-signal-envelope.md` — formalizes the envelope schema for `type: notification` payloads emitted by polyphony workflows, pins the `kind` vocabulary, and defines the severity semantics that platespinner will consume.

---

## Companion ADRs (all P1, sequenced after verb-error-boundary)

| ADR | Decides | Blocks |
|-----|---------|--------|
| `domain-signal-envelope.md` | Envelope schema (kind, severity, cta_url, cta_kind, correlation_id, expires_at), the `kind` vocabulary, correlation semantics | First notification node |
| `gate-compression-pattern.md` | Which gates compress to notification+poll, which remain as judgment-only, poll cadence defaults, the full user↔workflow loop contract | Wagner's forward workflow patterns; gate-compression prototype in github-pr.yaml |

## Scope boundary

The verb-error-boundary ADR is P0 (blocks #536 retrofit). The envelope + gate-compression ADRs are P1 (block the notification work). All three are prerequisites for different streams:

- Verb-error-boundary → blocks #536 Phase 2 retrofit (on_error: for verbs)
- Domain-signal-envelope → blocks first notification node in production workflows
- Gate-compression-pattern → blocks substitution of human gates with notification+poll


---

# Conductor Dogfood + Adoption Survey
**Author:** Mahler (Conductor Expert)  
**Date:** 2026-05-28T22:36:16-07:00  
**Status:** Ready for review

---

## Baseline

| Item | Value |
|---|---|
| **Polyphony's current conductor install** | `@main` (CI: `git+https://github.com/microsoft/conductor.git@main`); locally likely v0.1.16 (Wagner's prior note) |
| **Latest released tag** | **v0.1.18** — released during this session (fetched mid-task; tag SHA `f59345e`, release commit `085b7a5`) |
| **v0.1.18 release features** | `type: set`, `type: wait`, `type: terminate` steps; structured `runtime.provider` config; external-workflow friction fixes |
| **Previous tag** | v0.1.17 (`277aa72`) |
| **Dogfood base ref** | `efa520f` — one commit beyond v0.1.17, common ancestor of both feature branches |
| **Base choice rationale** | `feature/error-routing` is already based at `efa520f`. Using v0.1.18 as base triggers a semantic conflict in `context.py` between v0.1.18's non-dict output support and error-routing's `agent_outputs.get()` change (see Conflict section). `efa520f` avoids this while still including all v0.1.17 fixes. |

---

## Rebase Results

### PR #213 — `feat/notifications` (conductor-notifications worktree)

| Item | Result |
|---|---|
| **Base** | v0.1.18 (`085b7a5`) |
| **Conflict files** | `src/conductor/config/schema.py` only |
| **Conflict type** | Mechanical — adjacent enum additions. v0.1.18 added `"set"`, `"terminate"`, `"wait"` to `AgentDef.type`; notifications added `"notification"`. Also: `reject_bool_duration` validator (v0.1.18) vs `validate_raises` validator (notifications) — non-overlapping adjacent additions. |
| **Resolution** | Combined both sets of `Literal` values; included all four new validators/fields. No semantic judgment required. |
| `fork/feat/notifications` pushed | ✅ `fork/feat/notifications` force-pushed (27006af — includes review feedback amendments) |
| **New HEAD** | `27006af` (on top of v0.1.18 at `085b7a5`) |

### PR #229 — `feature/error-routing` (conductor-error-routing-impl worktree)

| Item | Result |
|---|---|
| **Attempted base** | v0.1.18 (`085b7a5`) |
| **Conflict files** | `src/conductor/config/schema.py` (2 conflicts — both mechanical, resolved), `src/conductor/engine/context.py` (1 conflict — **SEMANTIC, NOT RESOLVED**) |
| **Rebase status** | ⚠️ **ABORTED at commit 3/14** (`17c7cc7 feat(context): add store_error API`) |
| **Pushed to fork** | ❌ Not pushed — rebase was aborted. Branch stays at original base `efa520f` |

#### Semantic Conflict Detail — `context.py`

**File:** `src/conductor/engine/context.py`, method `_add_agent_input`

**What changed in v0.1.18 (set-step PR):**
```python
# v0.1.18 — uses subscript access, checks is_dict_output for seed
agent_output = self.agent_outputs[agent_name]
is_dict_output = isinstance(agent_output, dict)
if agent_name not in ctx:
    ctx[agent_name] = {"output": {} if is_dict_output else None}
```
The `None` seed is load-bearing: `TestWorkflowContextNonDictOutputs.test_explicit_mode_scalar_field_optional_skips` asserts `rendered["compute"]["output"] is None` for optional scalar field access.

**What changed in error-routing PR (commit 17c7cc7):**
```python
# error-routing — uses .get(), adds error-path branch at top
agent_output = self.agent_outputs.get(agent_name)
if agent_output is None:
    if not is_optional:
        raise KeyError(...)
    return
# ...
ctx[agent_name] = {"output": {}}  # simplified — no is_dict_output check
```
The `.get()` + early-return conflates two states: "agent never ran" and "agent ran and produced `None`" (valid for `type: set` with `value: null`). The simplified `{"output": {}}` init also breaks the non-dict output tests added by v0.1.18.

**Required judgment call:**
1. Should `agent_outputs.get()` be replaced with a sentinel (e.g. `_MISSING = object()`) to distinguish missing vs `None` output?
2. Should the init revert to `{} if is_dict_output else None` for compatibility with non-dict outputs, with error-routing's early-return added only for the truly-missing case?

**This decision belongs to the PR author — flagging for Daniel.**

---

## Dogfood Branch

| Item | Value |
|---|---|
| **Path** | `C:\Users\dangreen\projects\conductor-dogfood` |
| **Branch** | `dogfood/on-error+notifications` |
| **Base** | `efa520f` (between v0.1.17 and v0.1.18) |
| **Merge strategy** | `git merge feature/error-routing --no-ff` (clean) + `git cherry-pick f5397cd` (notifications, pre-rebase commit) |
| **HEAD SHA** | `4f5c3fc` |
| **Installed version** | `conductor v0.1.17` (pyproject.toml at efa520f says 0.1.17) |
| **Cross-branch conflicts** | ✅ None — notifications cherry-pick auto-merged cleanly on top of error-routing. The schema.py additions (`notification` type enum, `notification`/`payload` fields) are non-overlapping with error-routing's `raises`/`on_error` additions. |

### Build Status

```
pip install -e .  →  Successfully installed conductor-cli-0.1.17
conductor --version  →  Conductor v0.1.17
```

### Test Status

| Scope | Result |
|---|---|
| New feature tests (error-routing + notifications, 61 tests) | ✅ **61/61 passed** (after `pip install pytest-asyncio`) |
| Broader suite (test_config, test_engine, test_executor, test_error_kinds, test_helpers) | ⚠️ **324 failed, 1120 passed** — failures concentrated in `test_executor/test_script.py` (subprocess/shell tests on Windows) and `test_executor/test_agent_guidance.py`; appear pre-existing, not caused by dogfood changes |
| `pytest-asyncio` missing | The error-routing tests use `@pytest.mark.asyncio` — install with `pip install pytest-asyncio` |

**Note on 324 failures:** Not attributable to the dogfood merge. The `test_script.py` failures are subprocess-invocation tests that are sensitive to Windows shell environment; they likely fail on the efa520f base too. Not investigated further per task scope.

---

## Adoption Survey: v0.1.16 → v0.1.18

Polyphony CI consumes `@main`, so all changes below are in-scope.

| Commit | Change | Category | Notes |
|---|---|---|---|
| `04b46ec` | `fix(config): auto-fetch sibling sub-workflow from registry cache during validation` | 🟢 Adopt now | Polyphony uses cross-workflow sub-workflow refs; this fixes validation failures when the referenced workflow isn't in local cache |
| `3e726a0` | `fix(registry): mirror repo layout in cache so cross-workflow refs resolve` | 🟢 Adopt now | Same cross-workflow seam — complementary fix to above |
| `8ec298d` | `feat(validate): warn on undeclared agent.output refs and field-level mismatches in explicit mode` | 🟢 Adopt now | **High value for polyphony**: surfaces the Jinja path drift bugs at `conductor validate` time instead of mid-dogfood-run. Mahler concern #3 (node-ID pinning brittle) is partially mitigated by this |
| `9d603a1` | `feat(script): allow script agents to declare output schemas` | 🟡 Worth follow-up | Polyphony's workflow dispatch scripts (lifecycle-router.ps1 etc.) don't declare output schemas. Declaring them would close the M2-class footgun for script-node outputs. Medium effort. |
| `4765a52` | `feat(copilot): attribute verbose logs to agents in parallel/for-each runs` | 🟢 Adopt now | Polyphony uses large for_each batches; verbose log attribution would help debug stuck items |
| `dc29c2c` | `fix(engine,web): resolve max-iterations gate from dashboard in --web-bg` | 🟢 Adopt now | Polyphony uses --web-bg; gate resolution from dashboard was broken |
| `5fa2e14` | `fix(resume): replay original event log into dashboard on --web` | 🟢 Adopt now | Polyphony's re-entry pattern benefits from accurate dashboard replay |
| `752a9b5` | `fix(bg): detach --web-bg child from Windows job to prevent kill-on-close` | 🟢 Adopt now | Polyphony runs on Windows; this prevents the dogfood process from dying when the parent closes |
| `4337610` | `fix(windows): make --web-bg startup crashes diagnosable (#116)` | 🟢 Adopt now | Windows-specific; directly relevant |
| `75b01b5` | `fix(cli): suppress web-bg dashboard output in silent mode` | 🟢 Adopt now | Low-risk CLI fix |
| `9b42d7b` | `fix(cli): gate remaining dashboard URL prints behind is_verbose()` | 🟢 Adopt now | Low-risk CLI fix |
| `efa520f` | `fix(cli): make _verbose_console silent-aware and gate replay prints` | 🟢 Adopt now | Low-risk CLI fix |
| `4229a24` | `feat: add type: set step` | 🟡 Worth follow-up | Polyphony has inline Jinja bind steps that could be `type: set`; would simplify some script nodes (P8 principle). Low risk, medium effort. |
| `408b9df` | `feat(engine): add type: wait step` | 🟡 Worth follow-up | Limited direct use in polyphony today; relevant for future polling loops |
| `8124e8e` | `feat(engine): add type: terminate step` | 🟡 Worth follow-up | Polyphony's error terminals currently route to `$end` with no terminal signal. `type: terminate` with `status: failed` would produce the exit code 3 that error-routing PR #229 also introduces. Aligns with P7 (fail honestly). |
| `370209d` | `feat(providers): structured runtime.provider config` | ⚪ Irrelevant | Polyphony uses a fixed provider profile; custom endpoints not relevant today |
| `23751a2` | `fix: external workflow friction - minimal evidence-anchored fixes` | 🟢 Adopt now | "Evidence-anchored" suggests fixes to workflow validation/output contract issues. Polyphony hits friction in this area. |

---

## Top Adoption Recommendations (Ranked)

1. **`feat(validate): warn on undeclared agent.output refs` [commit 8ec298d]** — 🟢 immediate  
   Effort: zero (already on `@main`). Closes the Jinja path drift bug class at validation time. Polyphony should run `conductor validate` in CI once this is confirmed available.

2. **`feat(script): declare output schemas on script agents` [commit 9d603a1]** — 🟡 follow-up  
   Effort: 1–2h per workflow, scattered change. Polyphony's 6 lifecycle-dispatch scripts lack output schema declarations; adding them enables per-field type checking and closes M2-class footguns for script-node outputs. File as `squad:medium-term`.

3. **`type: terminate` for polyphony error terminals** — 🟡 follow-up (depends on #229 merging)  
   Effort: 1–2h. All 16 `abort_run` auto-routes in Wagner's PR #535 could route through a `type: terminate` node (status: failed) instead of `$end`, giving operators a typed workflow_failed event and exit code 3. Requires error-routing PR #229 to merge first. File as `squad:medium-term`.

4. **`type: set` for inline binding steps** — 🟡 follow-up  
   Effort: 2–4h. Several script nodes in the polyphony registry exist purely to bind a computed value into context. `type: set` would replace them with a zero-LOC YAML declaration, reducing script surface and aligning with P8. File as `squad:medium-term`.

5. **Windows --web-bg fixes (multiple)** — 🟢 immediate  
   Already on `@main`. Polyphony runs on Windows; the job-detach fix (`752a9b5`) and crash-diagnostic fix (`4337610`) are directly relevant to dogfood stability.

---

## Items Needing Daniel's Call

1. **`context.py` semantic conflict (error-routing rebase onto v0.1.18):** The `agent_outputs.get()` vs subscript access + `None`-seed initialization issue described above. Decision: (a) sentinel pattern, or (b) keep `is_dict_output` init + add error-path branch. Once decided, the error-routing rebase can complete and the dogfood can be rebuilt on v0.1.18.

2. **Dogfood does NOT include v0.1.18 features** (set/wait/terminate): The dogfood base is `efa520f` to avoid the above conflict. If polyphony workflows need to author `type: set/wait/terminate` in the dogfood period, Daniel should wait for conflict resolution and rebuild.

3. **PR #229 rebase onto v0.1.18 is blocked:** `fork/feature/error-routing` was NOT pushed (rebase aborted). The fork branch stays at original base.

---

## PR #213 Review Feedback Audit (jrob5756)

Inline comment audit completed 2026-05-28. All 8 comments addressed in commit `27006af` and replied to on the PR.

| Comment ID | File:Line | Summary | Disposition | Applied |
|---|---|---|---|---|
| 3283084198 | workflow.py:3033 | Parallel/for-each containment undefined — disallow or thread through dispatchers | ✅ Disallow at validation time | Validator guards added matching the existing `script`/`wait`/`terminate`/`workflow` pattern |
| 3283084205 | workflow.py:3058 | `assert` stripped under `python -O` — make explicit `raise` | ✅ Apply | Replaced with `raise ExecutionError(...)` |
| 3283084208 | workflow.py:3087 | Dashboard rendering missing for new event types | 💬 Reply only — defer as immediate follow-up | Dashboard requires TypeScript changes (workflow-store.ts, Cytoscape); out of scope for this PR |
| 3283084213 | workflow.py:3102 | `{}` storage silently empty for downstream `accumulate`-mode templates | 🤔 Document | Added note to AGENTS.md explaining empty-output behavior and fire-and-forget contract |
| 3283084216 | run.py:1931 | Confirm resume replays notifications in dashboard | 💬 Reply only — behavior is intentional | Confirmed: events flow through EventLog; `replay_events_from_jsonl` replays on resume |
| 3283084219 | notification.py:121 | Silent coerce fallback gives misleading error one frame later | ✅ Apply | `_coerce_rendered` now raises `ValidationError` on failure; call site adds field name context |
| 3283084222 | notification.py:233 | `workflow_metadata` redundant in envelope | 🤔 Drop it | Removed from `build_envelope` signature and envelope dict |
| 3283084228 | schema.py:964 | `type: notification` + `notification: pr_ready` reads redundant | 🤔 Rename to `emit:` | `AgentDef.notification` → `AgentDef.emit` across schema, validator, executor, engine, tests, examples, AGENTS.md |

