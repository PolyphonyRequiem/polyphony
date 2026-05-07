# Stuck-Review Timeout — Hard-Coded Poll Cap (MVP)

> **Status:** Accepted. Stuck-review timeout MVP.
> **Driver:** Pending-PR-review polls in `plan-level.yaml` and
> `ado-pr.yaml` re-enter on `state == 'pending'` with no upper bound.
> When a reviewer goes silent the workflow can sit on the
> `pending_review_gate` (or `ado_pr_pending_gate`) indefinitely with no
> escalation surface beyond "abort". We need a hard stop that promotes
> the situation from "still waiting" to "operator decision required".
> **Supersedes:** none — first iteration of the timeout machinery.

## Context

Two workflows host re-entrant pending-review polling loops today:

- **`plan-level.yaml`** — `poll_status` (github) / `poll_status_ado`
  (ado) → on `state == 'pending'` → `pending_review_gate` (human_gate
  with **Continue** → `pr_poll_platform_router` → loops back to
  `poll_status` / `poll_status_ado`).
- **`ado-pr.yaml`** — `ado_pr_status_check` → on `state == 'pending'`
  → `ado_pr_pending_gate` (human_gate with **Re-poll** → loops back to
  `ado_pr_status_check`).

The other PR-lifecycle YAMLs were audited and confirmed out of scope:

- `github-pr.yaml` — uses an LLM reviewer agent that returns
  `approved` / `changes_requested` directly; no `pending` state, no
  re-entrant poll loop.
- `feature-pr.yaml` — delegates to `pr_lifecycle_github` /
  `pr_lifecycle_ado` sub-workflows; the loop lives downstream.
- `actionable.yaml` — uses an LLM evidence reviewer that returns
  `approve` / `request_changes` / `block`; no polling.

In both in-scope loops the human is already in the loop on every
iteration (each re-poll is a deliberate click). What's missing is a
mechanism to recognise that the human has been clicking the same
"continue" button for too long and to surface a more emphatic
**stuck-review gate** with override semantics.

## Decision

**Hard-code a poll cap of 60 in each YAML and surface a
`stuck_review_gate` (P6-qualified human_gate) when the cap is hit.**
Defer the policy schema for per-pr-kind timeouts to a follow-up.

Concretely, each in-scope workflow grows three new agents:

1. **`*_pending_poll_counter`** (script) — increments a counter file
   keyed by `work_item_id` (plan-level) or `pr_number` (ado-pr) on
   each `state == 'pending'` arrival. Returns
   `{ count, cap, cap_reached }`. Routes to `*_stuck_review_gate` on
   `cap_reached == true`, else to the existing pending gate.
2. **`*_stuck_review_gate`** (human_gate) — operator picks
   `continue_waiting` (reset counter, return to pending gate),
   `override_approved` (treat the PR as approved, route to merger),
   or `abort` ($end). Surfaces the current count and the cap so the
   operator sees what default was applied.
3. **`*_stuck_review_reset`** (script) — zeros the counter file when
   the operator chose `continue_waiting`, then routes back to the
   pending gate so the operator gets the regular UX again with a
   fresh cap budget.

`plan-level.yaml` additionally needs **`stuck_review_override_router`**
(script) so `override_approved` can reach the platform-appropriate
merger (`merge_plan_pr` for github, `merge_plan_pr_ado` for ado)
without ambiguity. `ado-pr.yaml` is platform-bound so its
`override_approved` can route directly to `ado_pr_merger`.

Every modified YAML carries:

```yaml
# TODO(stuck-review-policy): elevate to policy.yaml > timeouts:
#   { review_pending: { by_pr_kind: { plan: 60, feature: 60, ... } } }
# when the policy schema lands. See docs/decisions/stuck-review-timeout.md.
```

so a future grep-and-promote pass can find every cap call site.

## Why hard-code, not policy?

User direction (verbatim, 2026-05-06):

> "1- could eventually be policy, easily adjusted now, low risk either way."

The policy elevation would require:

1. A new domain in `.conductor/policy.yaml` schema (`timeouts`).
2. A new `polyphony policy resolve --domain timeouts --pr-kind X`
   verb wiring + JSON contract test.
3. A wiring step in each workflow that resolves the cap before the
   counter step.

For an MVP whose only consequence on miscalibration is "operator sees
stuck-review gate sooner / later than ideal", that's overbuilt. The
hard-coded cap also makes the failure mode obvious in a single grep:

```pwsh
rg 'cap = 60' .conductor/registry/workflows
```

If the cap turns out to be too low (operators hit the gate too often
on healthy PRs) or too high (operators want the escalation faster),
that signal will land via PR feedback before it lands via policy
churn.

## What the policy schema would look like when promoted

```yaml
# .conductor/policy.yaml
timeouts:
  review_pending:
    by_pr_kind:
      plan:
        max_polls: 60
      feature:
        max_polls: 60
      remediation:
        max_polls: 30   # tighter — remediation should be visibly fast
    default:
      max_polls: 60
```

Resolved per-workflow via:

```yaml
- name: stuck_review_policy
  type: script
  command: polyphony
  args: ["policy", "resolve", "--domain", "timeouts", "--pr-kind", "plan"]
```

with the counter then reading `stuck_review_policy.output.max_polls`
instead of the literal `$cap = 60`. The TODO comments in the YAMLs
are the seed sites for that follow-up.

## Stuck-review gate options — what each means

| Option | Workflow effect | When to use |
|---|---|---|
| `continue_waiting` | Counter resets to zero; routes back to the existing pending gate. Operator effectively gets another cap budget. | The reviewer is known to be alive but slow (vacation, time zone, big PR). |
| `override_approved` | Bypasses review entirely; routes to the platform-appropriate merger. | The operator has confirmation out-of-band that the PR is good (verbal sign-off, paged reviewer who said "ship it") and the silence is not a quality signal. |
| `abort` | `$end` — workflow exits without merging. | The PR is genuinely abandoned and should be closed manually. |

`override_approved` is the operator's escape hatch from a missing
reviewer — it's not a routine path. The audit signal (gate fired,
operator chose override) is the trail of how the PR got in despite
the review gap.

## Consequences

**Positive**

- Workflows can no longer hang indefinitely on a silent reviewer; the
  stuck-review gate guarantees a decision surface within 60 polls.
- The operator's options are explicit and auditable
  (`continue_waiting` / `override_approved` / `abort` all carry
  semantic value names per `polyphony-workflow-author` conventions).
- The cap, the gate, and the reset are all standalone agents — easy
  to extend (e.g., add a `request_replacement_reviewer` option) or
  to lift into a sub-workflow when more loops gain the same need.

**Negative / accepted**

- The cap is hard-coded — same value (60) for plan PRs, feature PRs,
  and any future pr_kind. Operators can't tune per PR-kind without a
  YAML edit. Mitigated by the `TODO(stuck-review-policy)` comments
  marking every elevation site.
- Counter state lives in `$TMPDIR` and is shared with the existing
  `revise_counter` / `review_counter` files. A reboot wipes it,
  which means the cap effectively re-zeros across restarts — a
  property we already accept for the other counters in the registry.
- The cap counts polls, not wall-clock time. Sixty polls during one
  human's day-long debugging session is qualitatively different from
  sixty polls over four weeks. Acceptable for MVP — the policy
  schema can introduce wall-clock semantics if the poll-count
  abstraction proves wrong.

## Alternatives considered

1. **Policy schema in this PR.** Rejected per user direction — the
   surface area is too large to ship alongside an MVP whose only
   consequence on miscalibration is gate timing. The TODO comments
   keep the elevation path obvious.
2. **Use `limits.timeout_seconds` at workflow scope** (per
   `conductor-mechanics` M9). Rejected — that's a wall-clock cap on
   the *entire* workflow, not on the pending-review loop alone, and
   it crashes the run rather than escalating to a gate. We want
   "decision required" semantics, not "run failed".
3. **Use `limits.max_iterations` at workflow scope.** Same issue —
   it crashes rather than escalates, and it counts every node
   execution (including unrelated sub-workflow calls), so the budget
   is too entangled with workflow shape to use as a review timeout.
4. **Embed the cap in the existing `pending_review_gate` itself.**
   Rejected — `human_gate` nodes are stateless in the conductor
   contract; you can't accumulate "this gate has fired N times" inside
   the gate. The script step is required.
5. **Track the counter via `output_map` carrying the value forward
   across iterations.** Rejected — `output_map` is rendered at
   end-of-run (see `conductor-mechanics` M7), not per-iteration,
   so it can't carry mutating state between loop turns. The
   counter-file pattern is the established polyphony idiom (see
   `revise_counter` in `plan-level.yaml`, `review_counter` in
   `github-pr.yaml`).

## References

- Glossary: [Stuck review / Poll cap / Poll count / Stuck-review gate](../glossary.md#reviewers-status-and-merge-policy).
- Modified workflows: `.conductor/registry/workflows/plan-level.yaml`, `.conductor/registry/workflows/ado-pr.yaml`.
- Existing precedent for counter-file iteration tracking: `revise_counter` in `plan-level.yaml`, `review_counter` in `github-pr.yaml`.
- `conductor-mechanics` M9 — limits & retries (rejected as alternatives, see above).
- `conductor-design` P6 — human gates for genuine multi-option decisions (qualifies the stuck-review gate).
- Lint coverage: `.conductor/registry/tests/lint-plan-level.ps1` checks 18-23, `.conductor/registry/tests/lint-ado-pr.ps1` checks 7-11.
