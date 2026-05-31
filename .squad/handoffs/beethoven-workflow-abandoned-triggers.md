# workflow_abandoned — Concrete Trigger Analysis

**Author:** Beethoven (Mission Keeper)  
**Date:** 2026-05-29T09:29:44-07:00  
**Context:** Follow-up on Q2 from `beethoven-pr535-questions-restated.md`  
**Source files:**
- `polyphony/.conductor/registry/workflows/actionable.yaml`
- `polyphony/.conductor/registry/workflows/root-item-dispatch.yaml`

---

## 1. Routes INTO `workflow_abandoned` (all four, verbatim conditions)

Every single route is a human-gate choice. There is **no automatic routing** to `workflow_abandoned`; the terminal cannot be reached without a human clicking a button.

### Route A — `floor_failed_gate` → abort
**Gate fires when:** `evidence_floor_check.output.passes_floor == false`  
**Trigger:** The polyphony leg ran, the agent produced evidence, but the evidence failed the quality floor check — AND the operator chose **"🛑 Abort"** rather than retry.  
**Human signal:** "The agent's output isn't good enough, and I don't want to try again."

```yaml
- label: "🛑 Abort"
  value: abort
  route: workflow_abandoned
```

### Route B — `revise_loop_gate` → abandon
**Gate fires when:** `evidence_reviewer.output.decision == 'request_changes'`  
**Trigger:** The evidence PR was reviewed and the reviewer requested changes — AND the operator chose **"🛑 Abandon"** rather than sending the agent back in.  
**Human signal:** "The reviewer rejected the evidence again, and I'm not going to keep trying."

```yaml
- label: "🛑 Abandon"
  value: abandon
  route: workflow_abandoned
```

### Route C — `human_satisfaction_gate` → abandoned
**Gate fires when:** `executor_router.output.executor == 'human'`  
**Trigger:** This is the human-executor leg (the operator is doing the work themselves, not the agent). The gate re-shows until they confirm satisfaction OR choose to abandon. Operator chose **"🛑 Abandoned"**.  
**Human signal:** "I'm not going to complete this manual task."

```yaml
- label: "🛑 Abandoned"
  value: abandoned
  route: workflow_abandoned
```

### Route D — `workflow_error_gate` → abandon
**Gate fires when:** any upstream step has signaled an error (the gate is the shared catch-all for unexpected failures in the polyphony leg).  
**Trigger:** Something broke (agent error, branch error, PR open failure, etc.) — AND the operator chose **"🛑 Abandon"** rather than retry from `executor_router`.  
**Human signal:** "Something went wrong and I don't want to retry."

```yaml
- label: "🛑 Abandon"
  value: abandon
  route: workflow_abandoned
```

---

## 2. What "abandoned" means in plain English

**It means: operator gave up.** Every single path requires a human to click a button. The engine never autonomously routes here.

Breaking it down by archetype:
- Routes A and B: **quality-loop fatigue** — the engine tried, produced something, it wasn't good enough, human decided not to keep pushing.
- Route C: **human executor withdrawal** — the human committed to do the work themselves, then decided not to.
- Route D: **error-on-retry** — something broke, human evaluated the error and chose not to retry.

There is no "engine gave up" path. There is no "retries exceeded" path. The current design is entirely operator-volitional.

---

## 3. Reversibility check — does root-item-dispatch write ADO on `satisfied: false`?

**Prior claim was correct.** Verified from the code.

Here's the actual execution path when actionable exits via `workflow_abandoned`:

1. `actionable.yaml` terminal emits: `{ satisfied: false, abandoned: true, work_item_id: ... }`
2. In `root-item-dispatch.yaml`, the `actionable` node routes unconditionally to `teardown_worktree` (line 310) — there is no route fork on `satisfied`.
3. `teardown_worktree` runs (idempotent worktree cleanup), routes to `dispatched` terminal (line 381).
4. `dispatched` terminal is a pure JSON emit — **no `twig`, no `polyphony`, no ADO calls whatsoever** (lines 403–427).

The only terminal in root-item-dispatch that writes to ADO is `satisfied` (line 483), which calls `twig state` — and that terminal is only reachable via `classify_lifecycle.output.lifecycle_workflow == 'terminal-satisfied'` (line 220). That path is entirely separate from the actionable dispatch leg.

The output template (lines 171–173) surfaces `actionable_satisfied` as:
```
actionable.output.satisfied | default(false)
```
…which evaluates to `false` on abandonment. This propagates to the batch aggregator as `actionable_satisfied = false`, `item_satisfied = false`. The work item stays in whatever ADO state it was in before the run. **Fully reversible — re-trigger and go.**

---

## 4. Recommendation

**The current single "abandoned" bucket is fine for now — but name the smell.**

All four routes are operator-volitional, so conflating them into one terminal doesn't create a correctness problem. Abandonment is reversible in all cases, so there's no risk of a work item being stuck.

However, **routes A, B, and D have qualitatively different meanings**:
- A/B = "quality loop failed, operator fatigued" — these are candidates for a future `workflow_paused` or `revisit_later` terminal if the team wants to distinguish "temporarily shelved" from "given up."
- D = "unknown error at runtime, operator didn't retry" — this is the most dangerous one because it looks like abandonment but is actually a transient infrastructure failure. If you want better observability, Route D is the one to split out: route it to an `abandoned_on_error` terminal that preserves the error envelope rather than silently merging it with volitional abandonment.
- C = "human executor withdrawal" — genuinely volitional, no issue.

**Short answer for Daniel:** Don't split the bucket now. But when you retrofit error handling (AB#3257), consider breaking Route D off into `abandoned_on_error` so the batch aggregator can distinguish "operator gave up cleanly" from "something crashed and nobody retried it." That's the only confusion vector here.

---

*Filed: 2026-05-29T09:29:44-07:00*
