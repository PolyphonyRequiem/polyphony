# ADR: Gate Compression Pattern

**Status:** Accepted  
**Date:** 2026-05-28  
**Authors:** Bach (Architect)  
**Depends on:** `domain-signal-envelope.md`, `polyphony-verb-error-boundary.md`  
**Companion issues:** #536 (verb error boundary), #541/#542 (platespinner gaps)

---

## Context

Polyphony's workflows contain two broad categories of gate node:

1. **Deterministic-outcome gates** — the workflow pauses waiting for an
   observable external condition: PR approved, CI checks passed, work item
   in target state, review timeout elapsed. The human's _role_ is to take an
   external action (approve the PR, fix the tests), not to make a judgment
   _inside the workflow_.

2. **Judgment gates** — the workflow genuinely requires a human to evaluate
   something and decide: does this scope change make sense? is this design
   acceptable? should we proceed despite the conflict? No poll script can
   detect resolution here because "resolution" is defined by human judgment,
   not by observable state.

The current workflow suite treats both categories as `human_gate` nodes, which
means every pause requires a human to click through a gate UI — even when the
resolution is "PR was approved, continue." This wastes human attention on
logistics, obscures the gates that actually need judgment, and creates a
brittle dependency on a human being present and attentive.

**Gate compression** is the architectural pattern that converts category-1
gates from `human_gate` nodes to an `(emit domain signal + script-poll-loop)`
structure. The workflow emits a domain signal (platespinner renders a CTA
notification), then immediately begins polling for the observable condition.
When the condition resolves, the workflow continues automatically. The human
takes the external action (merges the PR, approves the review); the poll
script detects it.

This ADR defines:
- Which gate shapes compress vs which stay `human_gate`
- The canonical YAML structure for the compressed pattern
- The timing/backoff policy
- Open questions that require further input

---

## Decision

### The Compression Rule

A gate node is a candidate for compression if AND ONLY IF:

1. Resolution is detectable by a poll script (i.e. there exists a CLI command
   or API call that returns a deterministic boolean: "condition met or not")
2. The human's role is to take an _external action_, not to make a workflow
   decision
3. Timeout is acceptable as a fallback — the workflow can handle "condition
   never resolved within N minutes" gracefully

A gate stays `human_gate` if:
- Resolution requires a judgment call made _inside the workflow_ (scope
  approval, design sign-off, conflict resolution)
- The action is irreversible or high-risk (production deploy, destructive
  operation) — even if technically pollable, these warrant explicit human
  confirmation in the gate UI
- No reliable poll mechanism exists (e.g. external system has no queryable API)

### Gate Shape Catalogue

| Gate shape | Compresses? | Pattern |
|---|---|---|
| PR review approval | ✅ Yes | emit `pr_review_required` + poll `polyphony pr-status --approved` |
| CI/CD checks passing | ✅ Yes | emit `pr_checks_pending` + poll `polyphony pr-status --checks-passed` |
| PR merge | ✅ Yes | emit `pr_review_required` + poll `polyphony pr-status --merged` |
| Work item state transition | ✅ Yes | emit `work_item_pending` + poll `twig state` |
| Review timeout (wait N hours) | ✅ Yes | conductor `type: wait` (no emit needed — no human action required) |
| Scope violation decision | ❌ No — stays `human_gate` | Human evaluates, workflow adapts |
| Design / architectural sign-off | ❌ No — stays `human_gate` | Judgment inside workflow |
| Conflict resolution | ❌ No — stays `human_gate` | Human judgment + possible replan |
| Production deploy confirmation | ❌ No — stays `human_gate` | High-risk irreversible action |
| Stuck review (human override) | ❌ No — stays `human_gate` | Human decides to proceed or escalate |

### Canonical Compressed Gate Pattern (YAML)

The pattern is: **emit domain signal → poll → wait-on-miss → loop**.

```yaml
# Step 1: emit domain signal (CTA fires in platespinner)
- name: notify_pr_review_required
  type: emit                       # dogfood pre-refresh: type: notification
  emit: pr_review_required         # dogfood pre-refresh: notification: pr_review_required
  payload:
    kind: pr_review_required
    severity: warning
    title: "PR Review Required"
    message: "PR #{{ workflow.input.pr_number }} is awaiting review before the workflow continues."
    cta_url: "{{ workflow.input.pr_url }}"
    cta_kind: review_pr
    correlation_id: "{{ workflow.input.run_id }}:pr-review:{{ workflow.input.root_id }}"
    expires_at: "{{ workflow.input.review_deadline }}"
    disposition: pending
    details:
      pr_number: "{{ workflow.input.pr_number }}"
  routes:
    - to: poll_pr_approved

# Step 2: poll the observable condition
- name: poll_pr_approved
  type: script
  script: |
    $result = polyphony pr-status --pr-id "{{ workflow.input.pr_number }}" --repo "{{ workflow.input.repo }}"
    $out = $result | ConvertFrom-Json
    $out | ConvertTo-Json -Compress | Out-File -Encoding utf8 $env:CONDUCTOR_OUTPUT
  routes:
    - condition: "{{ agent.output.approved == true }}"
      to: post_review_step
    - condition: "{{ agent.output.approved == false }}"
      to: wait_before_retry_poll

# Step 3: wait before retrying (backoff)
- name: wait_before_retry_poll
  type: wait
  seconds: 120
  routes:
    - to: poll_pr_approved
```

#### Notes on the Pattern

- The emit step fires **once** when the gate opens. The poll loop does NOT
  re-emit on every miss — that would spam platespinner with duplicate
  notifications.
- If the poll script fails with a non-zero exit (infrastructure failure, not
  a "condition not met" result), the `on_error:` handler on `poll_pr_approved`
  should route to the appropriate retry or escalation path.
- The wait step's `seconds` value is a policy choice — see Open Asks below.
- The poll script should write a deterministic JSON output: `{"approved": true}`
  or `{"approved": false}`. It should NOT return a non-zero exit for "condition
  not yet met" — that is a domain outcome, not an infrastructure failure
  (per the `polyphony-verb-error-boundary` ADR).

### When to Emit a "Resolved" Domain Signal

When the poll loop detects resolution, workflows SHOULD emit a second domain
signal with `disposition: resolved` so platespinner can update the notification
lifecycle (suppress future toasts, mark the item resolved). This is optional
but strongly encouraged for gates with `cta_url`.

```yaml
- name: notify_pr_review_approved
  type: emit                       # dogfood pre-refresh: type: notification
  emit: pr_review_required         # dogfood pre-refresh: notification: pr_review_required
  payload:
    kind: pr_review_required
    severity: info
    title: "PR Review Approved"
    message: "PR #{{ workflow.input.pr_number }} received approval. Workflow continuing."
    cta_url: "{{ workflow.input.pr_url }}"
    cta_kind: review_pr
    correlation_id: "{{ workflow.input.run_id }}:pr-review:{{ workflow.input.root_id }}"
    disposition: resolved
  routes:
    - to: post_review_step
```

### Maximum Poll Iterations

To prevent infinite loops, every compressed gate MUST have a maximum poll
count or a wall-clock expiry. The pattern uses the `expires_at` field in the
domain signal as the semantic expiry, but the workflow itself enforces it
via one of:

1. A conductor `on_error: timeout` at the workflow level
2. An iteration counter in the poll script that routes to a fallback
   `human_gate` after N misses
3. A sentinel date check in the poll script against `expires_at`

Without one of these, a compressed gate over an unresolvable condition will
loop forever. Wagner must choose which approach to use in each workflow.

---

## Alternatives Considered

**Compress ALL gates including judgment calls:** Rejected. Judgment gates
require human evaluation that no poll script can substitute. Compressing them
would either cause infinite poll loops or require complex "did the human
decide" detection that would reinvent the gate UI.

**Keep all gates as `human_gate`, add CTA rendering only:** Rejected.
This is a half-measure — the human still has to click through the gate UI even
when the workflow can self-advance once the external condition resolves. The
poll loop is what makes the gate truly automatic.

**Script-only with no domain signal (silent poll):** Rejected. Without a
domain signal, the user has no visibility that the workflow is waiting for
them to take an action. Platespinner shows no notification; the user may not
know a PR review is needed. The domain signal is the user's actionable prompt.

---

## Consequences

### Positive
- Human attention is reserved for genuine judgment calls (scope, design,
  conflict resolution) — the only gates that remain as `human_gate`
- Observable conditions (PR approval, CI, work item state) resolve
  automatically; operators do not need to be present at their workflow
  dashboard to advance the run
- Platespinner's CTA notifications give operators an ambient awareness of
  what is pending without requiring them to actively monitor the gate UI
- Gate infrastructure failures (twig unavailable, ADO unreachable) route
  through `on_error:` chains rather than blocking on a human gate

### Negative
- Poll loops consume workflow execution time; a long review can hold a
  workflow active for hours (mitigated by `type: wait` backoff)
- Workflow YAML is more verbose: each compressed gate is 3 steps
  (emit + poll + wait) vs 1 `human_gate` node
- If the poll script misclassifies a domain outcome (condition not met) as a
  script failure (non-zero exit), `on_error:` will trigger spuriously —
  careful implementation of poll scripts is required

---

## OPEN ASKS FOR DANIEL

These are specific judgment calls where the architectural decision has an
impact on operator experience and workflow authoring conventions. Bach has
made a recommendation for each; Daniel's input confirms or overrides.

### Ask 1 — Default Poll Backoff

**Question:** What is the default wait duration between poll retries?  
**Current pattern:** 120 seconds (2 minutes) hardcoded in `type: wait`  
**Options:**
- A) Fixed 120s (simple, predictable, may be slow for fast CI)
- B) Fixed 60s (faster, more aggressive)
- C) Configurable per workflow via a workflow input (most flexible, most
  verbose for authors)
- D) Exponential backoff (1 min → 2 min → 4 min → cap at 15 min) starting
  fresh on each workflow run

**Bach's recommendation:** C — configurable per workflow input with a
documented default of 120s. This way operator-facing docs can specify the
default, and high-urgency workflows (fast CI runs) can dial it down.

### Ask 2 — Expiry Behavior

**Question:** When `expires_at` is reached while the poll loop is still
running, what should the workflow do?  
**Options:**
- A) Abort the poll loop and fail the workflow with an error
- B) Emit an "expired" domain signal (`disposition: expired`) and continue
  polling indefinitely (the expiry is advisory only)
- C) Route to a fallback `human_gate` where the operator decides whether to
  extend or abort
- D) Emit an "expired" signal and abort without human intervention

**Bach's recommendation:** C — route to a `human_gate` fallback. The expiry
is a signal that the automated wait has hit its limit; the human should decide
whether to extend, escalate, or abort. This preserves the principle that
judgment calls stay human.

### Ask 3 — Resolved Signal Convention

**Question:** Should emitting a `disposition: resolved` signal on poll
success be a workflow authoring REQUIREMENT or a RECOMMENDATION?  
**Options:**
- A) Required — polyphony's linter will flag a compressed gate that has no
  resolved-disposition emit on its success path
- B) Recommended — documented convention, not enforced
- C) Optional — left entirely to workflow authors

**Bach's recommendation:** B — recommended but not linted for now, since the
linter infrastructure for this check does not yet exist. Revisit to A when the
Jinja-resolver lint (ADR #175 companion) ships.

### Ask 4 — Maximum Poll Iteration Cap

**Question:** Should polyphony's workflow conventions specify a maximum number
of poll iterations (a hard cap) in addition to the `expires_at` field?  
**Options:**
- A) Yes — workflow template includes a `max_poll_attempts` input with a
  documented default (e.g. 50); poll script increments a counter and routes
  to `human_gate` when exceeded
- B) No — `expires_at` is the only cap; wall-clock expiry is sufficient
- C) Yes — but implemented as a conductor-level `on_error: timeout` at the
  workflow level, not as a poll-script counter

**Bach's recommendation:** C — conductor-level timeout is cleaner than a poll
counter (no counter state to thread through YAML), but requires Mahler to
confirm that conductor's `on_error: timeout` works correctly for long-running
poll loops. If Mahler confirms, prefer C; otherwise fall back to A.
