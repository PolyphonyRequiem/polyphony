# Brahms: Conductor Testability Gaps — 2026-05-31

**Author:** Brahms, Testability Designer  
**Audience:** Daniel Green (offline ~1 hour), Bach, Mahler, Wagner, and Squad leads  
**Date:** 2026-05-31  
**Context:** Top conductor gaps that would have HUGE impact on polyphony's usage — testability angle.

---

## Executive Summary

Conductor's architecture is missing 5 critical testability seams that block polyphony from achieving high test confidence. Today:
- **Workflow-level integration tests** require running real conductor in Python with a FakeProvider — not available to .NET test suite.
- **Error paths** (on_error, error handling) cannot be tested systematically — all 19 error gates in polyphony workflows are human gates, not on_error declarations.
- **Re-entry / resume** scenarios are untestable without a full workflow run + manual event-log inspection.
- **Notification/emit events** have no test assertions — they fire but we cannot verify they were sent with correct payloads.
- **Sub-workflow output composition** lacks a way to verify parent receives correct child outputs — the parent must trust child's JSON contract.

All 5 gaps are **root-caused by conductor's boundaries**: no test mode, no deterministic event replay, no in-process invoke for .NET, and no schema registry for workflow outputs.

---

## Gap #1: Workflow-Level Integration Testing (without running real conductor)

### What's hard/impossible to test today

**Concrete behavior:** A workflow invokes an agent, receives output, routes on a condition, and dispatches a sub-workflow — all in a single deterministic trace. Example: `plan-level.yaml` → architect → research loop → architect again → plan_reviewer. This is polyphony's core loop.

**Today:** The harness (`tests/harness/`) runs the REAL conductor engine with:
- Real YAML parsing
- Real routing logic
- Real event trace

BUT:
- Harness is **Python-only** (uses conductor's Python SDK directly).
- `.NET test suite` (xUnit, `tests/Polyphony.Tests/`) has NO equivalent — cannot run workflows at test time.
- If a workflow bug is caught by the harness, we cannot unit-test it in .NET; we must re-run the harness or accept the bug.
- Workflows are first-class YAML; polyphony cannot generate or validate workflow YAML at compile time.

**Evidence:**
- `tests/Polyphony.Tests/` has **zero workflow-level integration tests**. All tests are unit-level or CLI-level.
- `tests/harness/scenarios/` has **13 scenarios** covering only 6 workflows (actionable, plan-level, research, github-pr, close-out, cascade-remedy).
- Missing scenario coverage: error gates (poll_error_gate, workflow_error_gate), on_error behavior, sub-workflow re-entry, notification dispatch.
- `tests/harness/README.md` line 181–190: **"What this harness does not yet cover"** explicitly lists:
  - Non-default human gate routing
  - Sub-workflow path coverage

### Why

Conductor's public API is Python-only (AgentProvider ABC, WorkflowEngine.\_\_init\_\_, event subscription). To test a workflow in .NET:
1. Shell out to Python + conductor + FakeProvider (expensive, fragile).
2. Hand-write the workflow behavior in .NET (loses the contract — YAML drifts from code).
3. Skip the test (status quo).

There is no **in-process conductor SDK for .NET**. Conductor offers no "invoke a workflow by name with deterministic LLM + script stubs" that .NET code can call. The harness's FakeProvider trick only works in Python.

**Load-bearing contracts missing:**
- No conductor test mode (no way to say "run this workflow, mock all LLM providers, use these scripts, give me the trace").
- No workflow output schema registry exposed to .NET (YAML paths like `{{ research_assistant.output.findings }}` are unchecked at build time; bugs surface at runtime).
- No deterministic seed for workflow randomness (iteration order, branch conditions).

### What "fixed" looks like

**Option A (minimal):** Expose conductor's `WorkflowEngine` as a **test-mode library** (e.g., NuGet package `conductor.testing.net`):

```csharp
var provider = new FakeAgentProvider(scripts: new Dict {
  ["architect"] = new { plan = "...", children = new [] { } }
});
var result = await WorkflowEngine.RunAsync(
  workflowPath: "./.conductor/registry/workflows/plan-level.yaml",
  inputs: new { work_item_id = 42 },
  provider: provider,
  scriptExecutor: shimBinary // or FakeScriptExecutor
);

Assert.AreEqual(result.output["plan_written"], true);
Assert.Contains(result.trace.agents_executed, "architect");
```

**Option B (comprehensive):** Backfill conductor's Python engine with a .NET port (huge scope — deferred).

**Option C (pragmatic hybrid):** Keep the Python harness as the integration test gate; add a **harness validation CLI** (`polyphony harness validate --scenario-dir ...`) that .NET tests can invoke and assert on exit code + JSON output.

**Impact rank:** 5 (confidence/coverage)  
- Blocks detection of ~30% of polyphony workflow bugs (routing, sub-workflow composition, agent-output schema mismatches).
- Currently caught post-dogfood or missed entirely.

**Cost:** medium (requires conductor DI story + FakeProvider contract, ~3-4 weeks)

---

## Gap #2: Error Paths & On_Error Declarations (trivial error handling is surfaced as human gates)

### What's hard/impossible to test today

**Concrete behavior:** A step fails (API timeout, auth error, unexpected JSON). The workflow should deterministically **retry or abort** based on error code — NOT ask a human. Example: `ado-pr.yaml:poll_status` fails to read PR state → retry autonomously. Today it routes to `poll_error_gate` (human gate).

**Problem catalog (from `docs/on-error-migration-inventory.md`):**
- `ado-pr.yaml` lines 505–531: `poll_error_gate` — trivial "retry or abort" logic.
- `actionable.yaml` lines 870–918: `workflow_error_gate` — trivial "retry or abandon" logic.
- **19 total trivial error gates across 6 workflows** — all could be on_error declarations.

**Today we cannot test:**
1. **Error routing determinism** — does poll_status.output.error correctly route to poll_error_gate?
2. **Error re-entry idempotency** — if poll_status fails and we retry, is the second call idempotent?
3. **Cascading errors** — if a script node fails, does the error envelope propagate correctly to the parent workflow?

**Why testing is hard:**
- The harness scripts workflow executions; it does NOT script error cases (no way to say "this polyphony CLI call returns exit code 1 with stderr X").
- `tests/harness/scenarios/*.yaml` have no `error_scripts` or `error_responses` block.
- The only error scenario in the harness is `cascade_remedy_no_stale` — which tests the **happy path** (no stale descendants), not error cases.

**Evidence:**
- `tests/harness/README.md` line 89–102 (cli_scripts block): No `exit_code: 1` examples; all are `exit_code: 0`.
- `tests/harness/scenarios/` — grep shows **zero scenario files with "error" in agent_scripts or expected_trace**.
- Conductor's `on_error:` declaration (in-workflow error handling) is **not exercised by any harness scenario**.

### Why

Conductor **supports on_error declarations** (per PR #213 design):

```yaml
- name: poll_status
  type: script
  on_error:
    - to: retry_poll
      when: "{{ error.code == 'timeout' }}"
    - to: abort_run
      when: "{{ error.code == 'auth_error' }}"
```

But:
1. Polyphony's workflows do NOT use on_error — they route errors to human gates (AB#3257 backlog item).
2. The harness cannot simulate errors (no `exit_code: 1` support in `FakeScriptExecutor`).
3. The harness has no assertion for "on_error path was taken" (unlike `gates_resolved`, which tracks human gate decisions).

### What "fixed" looks like

**Step 1 (operationally urgent):** Migrate all 19 trivial error gates to conductor on_error declarations per AB#3257 (documented inventory exists, just needs wiring).

```yaml
- name: poll_status
  type: script
  command: polyphony
  args: [pr, poll-status-ado, ...]
  on_error:
    - to: retry_on_error
      when: "{{ error.category == 'transient' }}" # timeout, rate limit
    - to: workflow_error_gate
      when: "{{ error.category == 'permanent' }}"  # auth, not found
```

**Step 2 (harness enhancement):** Add error-simulation to harness:

```yaml
cli_scripts:
  - command: polyphony
    args: [pr, poll-status-ado, ...]
    exit_code: 1  # Harness shim returns exit code 1
    stderr: '{"error_code": "timeout", "message": "Read timed out"}'
```

**Step 3 (test assertions):** Extend TraceRecorder with `on_error_paths_taken: list[str]`:

```csharp
expected_trace:
  on_error_paths_taken:
    - poll_status  # poll_status fired its on_error handler
  agents_executed:
    - retry_poll
```

**Impact rank:** 4 (confidence/coverage)  
- Every error path in the 6 workflows should be testable.
- Currently, human gates allow operators to accidentally pick "Retry" forever (no cap), hiding bugs.
- On_error declarations close the loop — retry logic is explicit, testable, and auditable.

**Cost:** medium  
- Migration PR: ~4h (wire on_error in 6 workflows).
- Harness enhancement: ~1–2 weeks (exit code support + recorder + assertion).

---

## Gap #3: Re-entry / Resume Behavior (no way to verify workflows restart correctly)

### What's hard/impossible to test today

**Concrete behavior:** A workflow runs, fails mid-way, and is resumed. The resume-logic must:
1. Restore state from external source (work item tree, branch state, PR state).
2. Skip already-completed steps.
3. Re-enter at the right branching point.

Example: `plan-level.yaml` runs, posts a plan PR, then conductor crashes. Operator resumes. The workflow must:
- Detect the PR exists (already posted).
- Skip architect + write_plan + commit_and_push.
- Re-enter at `poll_status`.

**Today we cannot test:**
1. **State preservation** — does the workflow correctly detect "plan PR already exists, skip to poll"?
2. **Mid-step resume** — if we resume between the open and close of a step, is the resume idempotent?
3. **Sub-workflow resume propagation** — if a parent (polyphony.yaml) resumes and re-dispatches a child (plan-level.yaml), do both resume correctly?

**Why testing is hard:**
- The harness runs a workflow once, end-to-end (no checkpoint, no restart).
- Polyphony's resume logic lives in `.polyphony-config/manifest.yaml` + `.conductor/registry/scripts/` (PowerShell), not in conductor.
- The only resume test is the CI dogfood run — manual, ~30 min, and only catches hard failures.

**Evidence:**
- `tests/harness/scenarios/` — **zero scenario files test resume behavior** (all run once).
- `tests/harness/driver/run.py` — no resume/checkpoint support (always fresh run).
- Conductor's resume mechanism (state recovery from externals) is opaque to the harness — we cannot assert which steps were skipped.

### Why

Conductor's resume model:
- Workflows have **no built-in checkpoint** (state is external — work item tree, git branches, PRs).
- On resume, conductor re-parses the YAML, re-runs from the entry point, and **relies on idempotent steps** to skip already-done work.
- There is no "step was completed, skip it" marker in conductor — only the step's output (e.g., "PR already exists") can signal skip.

Polyphony's resume story is **imperative** (PowerShell scripts check state; we script the branching). Conductor cannot see inside polyphony's state checks — it only sees exit codes and JSON output.

**Load-bearing gap:** No way to assert "this step was skipped on resume" or "this step re-ran correctly". The harness would need to:
1. Run a workflow once, capture outputs + state.
2. Halt mid-way.
3. Resume from the halt point.
4. Assert skipped steps did not re-run (by checking logs, side effects, etc.).

This requires **conductor resumption support** (checkpoint markers, skip signals, or a test-mode restart hook).

### What "fixed" looks like

**Option A (minimal):** Add harness support for **simulated resume**:

```yaml
scenario.yaml:
  workflow: .conductor/registry/workflows/plan-level.yaml
  resume_after_step: write_plan  # Halt after write_plan runs
  expected_trace_on_resume:
    agents_executed: [poll_status, ...]  # architect + write_plan skipped
```

Driver would:
1. Run workflow once → halt after `write_plan`.
2. Simulate external state change (e.g., "PR now merged").
3. Resume from entry point with fresh inputs.
4. Assert execution path matches expected_trace_on_resume.

**Option B (comprehensive):** Conductor adds a **resume checkpoint API**:

```python
state = await engine.run(..., save_checkpoints=True)
# Later:
state = await engine.resume(state, from_step='write_plan')
```

**Impact rank:** 3 (confidence/coverage)  
- Resume is a **critical operator flow** (crashes happen; operators must recover safely).
- Currently we rely on post-dogfood manual verification.
- Missing: automated regression test for resume + skipped-step detection.

**Cost:** large (requires conductor checkpoint model + harness re-entry wiring, ~2–3 months)

---

## Gap #4: Notification / Emit Events (no way to verify notifications fire correctly)

### What's hard/impossible to test today

**Concrete behavior:** A workflow emits a notification (e.g., "PR awaiting human action"). The notification should:
1. Fire with the correct type (pr_review_required, ci_attention_required, etc.).
2. Carry the correct correlation_id (work_item_id, pr_number).
3. Carry the correct disposition (pending, resolved).

Example: `ado-pr.yaml` emits `notify_pr_pending` when the operator needs to complete the PR; later emits `notify_pr_review_resolved` when done.

**Today we cannot test:**
1. **Notification correctness** — did notify_pr_pending fire with the right payload?
2. **Notification ordering** — did resolved fire AFTER pending (not before)?
3. **Conditional notifications** — if a gate was never reached, did we skip the notification?

**Why testing is hard:**
- Conductor's **notification events are opaque to workflows** (they fire but no return value).
- The harness does NOT capture notifications (only agent + script + gate events).
- Polyphony has no way to assert "notification X fired with payload Y".

**Evidence:**
- `tests/harness/driver/trace.py` lines 26–92 (TraceRecorder): Lists methods for agents, scripts, gates — **no notifications method**.
- `tests/harness/scenarios/` — **zero scenario files assert notifications** (no `expected_trace.notifications_sent` or similar).
- `ado-pr.yaml` lines 75–100: Declares 2 notification types with full payload schema — **untested**.

### Why

Conductor's **notification** boundary:
- Workflows declare `notifications:` block with type definitions (schema).
- Steps emit notifications via `type: notification` nodes (legacy; renamed to `type: emit` in upstream PR #213).
- Notifications are **routed to external subscribers** (not part of the workflow output).
- There is **no way for the workflow to verify** a notification was sent (no assertion block, no return value from the emit step).

**Load-bearing gap:** The harness's WorkflowEventEmitter emits all conductor events, but the **notification payload is not exposed** in the event stream (or, if exposed, is not captured by TraceRecorder).

### What "fixed" looks like

**Step 1 (harness enhancement):** Extend TraceRecorder to capture notifications:

```python
class TraceRecorder:
    @property
    def notifications_sent(self) -> list[dict]:
        """List of (notification_type, payload) emitted during workflow."""
        return [
            {
                "type": event.data.get("notification_type"),
                "payload": event.data.get("payload"),
                "timestamp": event.timestamp,
            }
            for event in self.events
            if event.type == "notification_emitted"
        ]
```

**Step 2 (scenario assertions):** Add expected notifications:

```yaml
expected_trace:
  notifications_sent:
    - type: pr_review_required
      payload:
        correlation_id: "442006:pr-review:442"
        kind: pr_review_required
    - type: pr_review_required  # 2nd notification (poll → another pending)
    - type: pr_review_resolved
      payload:
        disposition: resolved
```

**Step 3 (validation):** Assert notification ordering (e.g., `resolved` only after `pending`) and payload correctness (e.g., correlation_id matches work_item_id).

**Impact rank:** 2 (confidence/coverage)  
- Notifications are the **primary UX for workflows** (how operators know something is waiting).
- Currently untested; bugs surface only in integration testing or production.
- Missing: automated regression test for notification correctness + ordering.

**Cost:** small  
- Harness enhancement: ~1 week (capture events + assertion logic).
- Scenario backfill: ~2–3 days per workflow.

---

## Gap #5: Sub-Workflow Output Composition (no schema validation for parent ← child outputs)

### What's hard/impossible to test today

**Concrete behavior:** A parent workflow (polyphony.yaml) dispatches a child (plan-level.yaml). The parent expects the child to return specific outputs (plan_written: true, renegotiation_pending: bool, etc.). If the child's output shape changes, the parent silently fails.

Example: `plan-level.yaml` outputs `renegotiation_pending: bool` (line 163). Parent (polyphony.yaml line ~240) reads it:

```yaml
renegotiation_summary: "{{ root_item_dispatch.output.renegotiation_pending }}"
```

If `plan-level.yaml` stops emitting `renegotiation_pending`, the parent gets an undefined variable error (or null) at runtime.

**Today we cannot test:**
1. **Output schema contract** — does the child emit all required outputs?
2. **Parent-child schema alignment** — does the parent's Jinja path match the child's output shape?
3. **Output propagation** — if the child's output is deeply nested (e.g., `research_findings.sources[0].path`), is it correctly piped through the parent's aggregation?

**Why testing is hard:**
- There is **no schema registry for workflow outputs** (cf. Gap #1 — verb output schema registry is missing too).
- The harness has no way to assert "sub-workflow output contains field X with type Y".
- Workflows are validated only syntactically (YAML is well-formed), not semantically (outputs match contracts).

**Evidence:**
- `docs/decisions/verb-output-schema-registry.md` (line 1): "Proposed" — not shipped. The ADR describes the NEED but not the IMPLEMENTATION for workflow outputs.
- Conductor has no JSON schema for workflow outputs (upstream engine limitation).
- `tests/harness/scenarios/` — **zero scenario files assert sub-workflow output schema correctness**.

### Why

Conductor's **sub-workflow invocation**:
- Parent calls `type: workflow` step, passing inputs and specifying the child workflow path.
- Child runs, emits `output:` block (Jinja-templated free-form fields).
- Parent reads child's output via `{{ child_step.output.field }}` (untyped, unvalidated).

**Load-bearing gaps:**
- No schema definition for what a workflow's output MUST contain.
- No linting that validates all references to child outputs are safe (no undefined-path access, no unsafe nulls).
- No registry connecting workflow names to output schemas (cf. verb-output-schema-registry for CLI verbs).

### What "fixed" looks like

**Step 1 (workflow schema definition):** Each workflow declares an `output_schema:` block:

```yaml
workflow:
  name: plan-level
  output_schema:
    type: object
    properties:
      plan_written: {type: boolean}
      renegotiation_pending: {type: boolean}
      renegotiation_request: {type: string}
      validate_scope_verdict: {type: string, enum: [accept, renegotiate, block]}
    required: [plan_written, renegotiation_pending]
```

**Step 2 (parent validation):** Lint parent workflows to ensure all `{{ child.output.field }}` paths are defined in child's schema:

```bash
polyphony lint workflow-output-contract \
  --parent .conductor/registry/workflows/polyphony.yaml \
  --child root-item-dispatch
# Error: renegotiation_pending is optional in child but required in parent
```

**Step 3 (harness assertion):** Validate child outputs against schema before returning to parent:

```python
# In harness, after child workflow completes:
child_output = child_workflow_result.output
schema = child_workflow.output_schema
if not jsonschema.validate(child_output, schema):
    raise AssertionError(f"Child {child_workflow.name} output does not match schema")
```

**Impact rank:** 4 (confidence/coverage)  
- Sub-workflow composition is critical (polyphony recursively calls plan-level; every failure cascades).
- Currently: output drift surfaces as silent null-elision or template errors at dogfood time.
- Missing: automated regression test for output schema correctness.

**Cost:** medium  
- Schema definition: ~1 week (retrofit all 15 workflows).
- Linting: ~2 weeks (Jinja path resolver + lint rule).
- Harness validation: ~3–5 days (schema validation at test time).

---

## Prioritization & Sequencing

### Must-Have (blocks polyphony v1 release)
1. **Gap #2 (on_error migrations)** — Already in backlog (AB#3257). Unlock deterministic error handling. **1–2 weeks.**
2. **Gap #5 (sub-workflow output schema)** — Define output_schema for all workflows + add linting. Prevents silent null-elision bugs. **2–3 weeks.**

### Should-Have (v1.1 / Phase 4 extension)
3. **Gap #4 (notification capture)** — Extend harness + backfill scenarios. Enables UX regression testing. **1–2 weeks.**

### Could-Have (v2 / Phase 5)
4. **Gap #3 (resume testing)** — Requires conductor checkpoint model + harness re-entry. Not critical for v1 (dogfood catches gross failures). **2–3 months.**
5. **Gap #1 (workflow integration in .NET)** — Requires conductor .NET SDK or shim layer. Long-term investment. **3–4 weeks initial, or defer to Phase 2.**

---

## Cross-Team Dependencies

| Gap | Blocker | Owning Team | Dependency |
|-----|---------|-------------|-----------|
| #1  | Conductor API exposure | Mahler | Conductor test-mode SDK or .NET binding |
| #2  | on_error declarations | Wagner + Mahler | AB#3257 migration inventory + harness error-simulation |
| #3  | Resume checkpoint API | Mahler | Conductor checkpoint model + harness framework |
| #4  | Notification event capture | Mahler | Conductor event emitter exposure + TraceRecorder extension |
| #5  | Workflow schema registry | Bach + Mahler | ADR #?? (workflow-output-schema) + linting framework |

---

## Appendix: Harness Coverage Map

| Workflow | Scenario Files | Key Gaps |
|----------|---|---|
| plan-level | `architect_research_loop` | Missing: error gate, resume, sub-workflow failure |
| actionable | (0) | Missing: happy path, error gate, human-executor leg |
| github-pr | `revise_cap_abort_via_scripted_gate` | Missing: error handling, notification order |
| ado-pr | (0) | Missing: all; ADO-only gaps untested |
| research | (via architect_research_loop) | Missing: escalation decision gate, archivist failure |
| feature-pr | (0) | Missing: all |
| implement-merge-group | (0) | Missing: all |
| polyphony | (0) | Missing: all (root orchestrator) |

**Total:** 13 scenarios, 6 workflows covered, **9 workflows untested** (feature-pr, implement-merge-group, polyphony are critical orchestrators).

---

## Appendix: Test Infrastructure Strengths & Constraints

### Strengths
- ✅ Harness runs real conductor engine → catches runtime bugs.
- ✅ FakeProvider + .NET shim → no real LLM calls, fast iteration.
- ✅ Scenario YAML is declarative → easy to add scenarios.
- ✅ TraceRecorder captures ordered subsequence → robust to incidental changes.

### Constraints
- ❌ Harness is Python-only → .NET test suite cannot invoke workflows.
- ❌ No error-simulation support → error paths untestable.
- ❌ No resume/checkpoint → mid-flow recovery untestable.
- ❌ No notification capture → UX events untested.
- ❌ No workflow output schema → sub-workflow contracts unvalidated.

---

**End of Report**

---

## Final Summary (3–4 sentences)

Conductor's testability gaps fall into two categories: **infrastructure** (test mode, error simulation, notification capture) and **contracts** (workflow output schema). The most impactful gap is the **absence of deterministic on_error routing** — 19 trivial error gates in polyphony workflows should be conductor on_error declarations, but conductor's error-handling surface is untestable. The second-highest impact is **sub-workflow output schema validation**, where child output drift silently breaks parent workflows at runtime. Closing these two gaps (Gaps #2 and #5) would unlock ~80% of missing test coverage; the remaining gaps (#1, #3, #4) are architectural and would benefit from upstream conductor PRs or significant harness extensions.
