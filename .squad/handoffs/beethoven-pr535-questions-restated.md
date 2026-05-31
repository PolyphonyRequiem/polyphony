## Question 1: Is partial-seed auto-continue safe?

**What we're deciding (in plain English, no jargon):**
When the seeder tool fails to create some (but not all) child work items, the current workflow shows a human gate with three options: retry, continue anyway, or abort. PR #535 proposes to remove the gate entirely and auto-continue. Should we auto-continue past partial seed failures, or should we keep the human gate?

**Why it matters:**
Auto-continue means a half-completed work-item tree moves forward silently. If you don't notice the failures in logs, you'll discover missing children much later when they don't show up for planning. Manual gates force you to see and acknowledge the failures before proceeding.

**Default I recommend:**
Keep the human gate. Reason: The seeder is idempotent (safe to retry), and half-seeded trees are a silent-failure risk. The cost of asking the human "continue anyway?" is low compared to the cost of discovering children went missing.

**Alternative if you disagree:**
Auto-continue to child_router on seeder.output.error_count > 0. Rationale: The seeder is forgiving and the next run will reconcile. Partial seeds aren't catastrophic; the gate adds friction for a recoverable outcome.

**What Daniel needs to do:**
Reply "default" / "alternative" / "ask me more". That's it.

---

## Question 2: Does workflow_abandoned write anything to ADO that's hard to undo?

**What we're deciding (in plain English, no jargon):**
When an actionable item is abandoned in the workflow, does that write a permanent "abandoned" marker to the ADO work item, or is it just an internal signal that the workflow didn't satisfy it?

**Why it matters:**
If `workflow_abandoned` writes ADO state, it's a one-way door — the operator has to manually un-abandon the work item in ADO before re-triggering the workflow. If it's conductor-only, the item stays ready and can be re-run immediately.

**Default I recommend:**
workflow_abandoned is conductor-state-only. Reason: I've verified the actionable.yaml terminal is a no-op that emits `satisfied: false` — no ADO writes. The parent workflow (root-item-dispatch) handles state transitions, not the actionable terminal itself. An abandoned workflow is reversible; just re-trigger.

**Alternative if you disagree:**
If the parent workflow (root-item-dispatch) does write an ADO "abandoned" disposition when it sees `satisfied: false`, then workflow_abandoned is harder to undo and should route to a different terminal (e.g., abort_run instead) for transient failures.

**What Daniel needs to do:**
Reply "default" / "alternative" / "ask me more". That's it.
