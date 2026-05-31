# PR-Gate Migration — Proposed YAML Diffs

**Author:** Wagner (Workflow Author)  
**Date:** 2026-05-29  
**Status:** Draft — do NOT apply until Daniel answers OPEN QUESTIONS in migration-plan.md  
**Companion doc:** `wagner-pr-gate-migration-plan.md`

Each section shows a `# FROM:` block (current YAML) and a `# TO:` block (proposed replacement).
`type: notification` / `notification:` are the dogfood-compatible names. When Mahler's upstream PR #213 cherry-pick (`27006af`) lands, a follow-up bulk-rename pass converts to `type: emit` / `emit:`.

---

## CHANGE 1 — `github-pr.yaml`: Replace `pending_review_gate` and supporting nodes

### CHANGE 1a — Add `notifications:` block to `github-pr.yaml` workflow header

```yaml
# FROM: (no notifications: block in github-pr.yaml)

workflow:
  name: github-pr
  # ...
```

```yaml
# TO: add notifications block after workflow metadata

workflow:
  name: github-pr
  # ...
  notifications:
    namespace: polyphony.github_pr
    correlation:
      - work_item_id
    types:
      pr_review_required:
        version: 1
        description: PR is awaiting human action (review, approval, merge, or close)
        payload:
          kind:           {type: string}
          severity:       {type: string}
          title:          {type: string}
          message:        {type: string}
          cta_url:        {type: string}
          cta_kind:       {type: string}
          correlation_id: {type: string}
          expires_at:     {type: string}
          disposition:    {type: string}
          details:        {type: object}
```

---

### CHANGE 1b — Remove `pending_poll_counter`, `pending_review_gate_policy_router`, `pending_review_gate`; add `notify_pr_pending` + `poll_pr_state_delta`

```yaml
# FROM: github-pr.yaml:963–1062
# (Three nodes: pending_poll_counter → pending_review_gate_policy_router → pending_review_gate)

  - name: pending_poll_counter
    type: script
    description: Track pending-review poll iterations; route to stuck gate at cap
    # ... (full counter script)
    routes:
      - to: stuck_review_gate_policy_router
        when: "{{ pending_poll_counter.output.cap_reached == true }}"
      - to: pending_review_gate_policy_router
        when: "{{ pending_poll_counter.output.cap_reached == false }}"
      - to: stuck_review_gate_policy_router

  - name: pending_review_gate_policy_router
    type: script
    description: Bypass pending_review_gate when policy.unattended.review_wait_mode is skip; reject auto.
    command: polyphony
    args:
      - "policy"
      - "load"
    routes:
      - to: abort_auto_mode_unsupported
        when: "{{ pending_review_gate_policy_router.output.unattended.review_wait_mode == 'auto' }}"
      - to: poll_status
        when: "{{ pending_review_gate_policy_router.output.unattended.review_wait_mode == 'skip' }}"
      - to: pending_review_gate

  - name: pending_review_gate
    type: human_gate
    prompt: |
      ## ⏳ PR Awaiting Action — PR #{{ workflow.input.pr_number }}

      The PR is open. The polyphony bot reviewer has posted (see the
      marker comment on the PR) and the sentiment-driven analyzer did
      NOT find actionable negative feedback this poll.

      - **PR:** {{ poll_status.output.pr_url }}
      - **Branch:** `{{ workflow.input.branch_name }}`

      To proceed, take one of these actions on GitHub, then click
      **Continue** to re-poll:
      ...
    options:
      - label: "🔄 Continue"
        value: continue
        route: poll_status
      - label: "🛑 Abort"
        value: abort
        route: abort_run
```

```yaml
# TO: replace all three nodes above with two nodes

  # ── PR pending — emit domain signal (fires once when gate opens) ──────
  #
  # Emits a pr_review_required domain signal. Platespinner renders the
  # CTA notification. Does NOT re-emit on subsequent poll misses — the
  # poll loop handles waiting silently until a reaction is detected.
  #
  # Replaces: pending_poll_counter + pending_review_gate_policy_router +
  #           pending_review_gate (all three collapsed into emit + poll).
  #
  # Invariants:
  #   Pre:  pr_feedback_analyzer found no actionable negative feedback.
  #   Post: Domain signal emitted; poll loop running.
  - name: notify_pr_pending
    type: notification         # TODO(post-upstream-merge): rename to type: emit
    notification: pr_review_required  # TODO(post-upstream-merge): rename to emit: pr_review_required
    payload:
      kind: pr_review_required
      severity: warning
      title: "PR Review Required"
      message: >-
        PR #{{ workflow.input.pr_number }} on `{{ workflow.input.branch_name }}`
        is open and awaiting action (approve, merge, comment, or close) before
        the workflow continues.
      cta_url: "{{ poll_status.output.pr_url }}"
      cta_kind: review_pr
      correlation_id: "{{ workflow.input.work_item_id }}:pr-review:{{ workflow.input.pr_number }}"
      expires_at: "{{ workflow.input.review_deadline | default('') }}"
      disposition: pending
      details:
        pr_number: "{{ workflow.input.pr_number }}"
        branch_name: "{{ workflow.input.branch_name }}"
        work_item_id: "{{ workflow.input.work_item_id }}"
    routes:
      - to: poll_pr_state_delta

  # ── PR state delta poll ───────────────────────────────────────────────
  #
  # Calls Poll-PrStateDelta.ps1 which blocks internally (polling every
  # PollIntervalSeconds) until a reaction is detected or TimeoutSeconds
  # elapses. Script exits 0 for both outcomes; non-zero only for
  # infrastructure failures (auth, network).
  #
  # reaction_kind routing:
  #   merged        → already_merged_emitter (PR completed)
  #   closed        → abort_unmerged (PR closed without merge)
  #   new_review    → poll_status (re-read absolute state; routes to
  #                   analyzer or merge path based on fresh data)
  #   new_commit    → poll_status (CI will restart; re-read state)
  #   ci_changed    → poll_status (re-read; TODO: see OPEN QUESTION Q3
  #                   on whether to split green/red routing here)
  #   timeout       → stuck_review_gate_policy_router (human escalation
  #                   after TimeoutSeconds with no reaction)
  #   (catch-all)   → poll_status (defensive; re-read on unknown kinds)
  #
  # on_error: → poll_error_gate (infrastructure failure; operator retries)
  # See OPEN QUESTION Q7.
  #
  # Invariants:
  #   Pre:  notify_pr_pending has fired; domain signal is live.
  #   Post: reaction_kind populated; new_watermark written to watermark file.
  - name: poll_pr_state_delta
    type: script
    description: Poll for PR state delta since last reaction watermark — blocks until reaction or timeout
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "{{ workflow.dir }}/../scripts/Poll-PrStateDelta.ps1"
      - "-Platform"
      - "github"
      - "-PrUrl"
      - "{{ poll_status.output.pr_url }}"
      - "-PrNumber"
      - "{{ workflow.input.pr_number }}"
      - "-TimeoutSeconds"
      - "{{ workflow.input.poll_timeout_seconds | default(86400) }}"
      - "-PollIntervalSeconds"
      - "{{ workflow.input.poll_interval_seconds | default(30) }}"
    routes:
      - condition: "{{ poll_pr_state_delta.output.reaction_kind == 'merged' }}"
        to: notify_pr_review_resolved    # emit disposition:resolved, then already_merged_emitter
      - condition: "{{ poll_pr_state_delta.output.reaction_kind == 'closed' }}"
        to: abort_unmerged
      - condition: "{{ poll_pr_state_delta.output.reaction_kind == 'timeout' }}"
        to: stuck_review_gate_policy_router
      # new_review, new_commit, ci_changed, and catch-all: re-read absolute state
      - to: poll_status
```

---

### CHANGE 1c — Add `notify_pr_review_resolved` (resolved-disposition signal before `already_merged_emitter`)

```yaml
# FROM: (no resolved-disposition signal — already_merged_emitter fires directly)

  - name: already_merged_emitter
    type: script
    # ...
```

```yaml
# TO: insert notify_pr_review_resolved before already_merged_emitter
# (already_merged_emitter is unchanged)

  # ── PR review resolved signal ─────────────────────────────────────────
  #
  # Emits disposition:resolved so platespinner closes the pending CTA
  # notification. Fires only on the poll_pr_state_delta.merged path.
  # Non-blocking: always routes to already_merged_emitter regardless of
  # whether platespinner has processed the signal.
  #
  # See domain-signal-envelope ADR §"When to Emit a Resolved Signal".
  - name: notify_pr_review_resolved
    type: notification         # TODO(post-upstream-merge): rename to type: emit
    notification: pr_review_required  # TODO(post-upstream-merge): rename to emit: pr_review_required
    payload:
      kind: pr_review_required
      severity: info
      title: "PR Merged"
      message: "PR #{{ workflow.input.pr_number }} on `{{ workflow.input.branch_name }}` was merged. Workflow continuing."
      cta_url: "{{ poll_status.output.pr_url }}"
      cta_kind: open_pr
      correlation_id: "{{ workflow.input.work_item_id }}:pr-review:{{ workflow.input.pr_number }}"
      disposition: resolved
      details:
        pr_number: "{{ workflow.input.pr_number }}"
        branch_name: "{{ workflow.input.branch_name }}"
    routes:
      - to: already_merged_emitter
```

---

### CHANGE 1d — Update `stuck_review_reset` route target

```yaml
# FROM: stuck_review_reset routes to pending_review_gate_policy_router

  - name: stuck_review_reset
    # ...
    routes:
      - to: pending_review_gate_policy_router
```

```yaml
# TO: stuck_review_reset routes to poll_pr_state_delta
# (counter no longer meaningful; just re-enter the poll loop after human "Continue waiting")

  - name: stuck_review_reset
    # ...
    routes:
      - to: poll_pr_state_delta
```

---

### CHANGE 1e — Update `pr_pre_merge_gate` Defer route

```yaml
# FROM: pr_pre_merge_gate "Defer" option routes to pending_review_gate

    options:
      - label: "✅ Approve merge"
        value: approve
        route: pr_merger
      - label: "⏸️ Defer (back to pending)"
        value: defer
        route: pending_review_gate
      - label: "🛑 Abort"
        value: abort
        route: abort_run
```

```yaml
# TO: Defer routes to poll_pr_state_delta (re-enter poll loop after human deferred)
# The domain signal for this gate was already emitted; no re-emit needed.

    options:
      - label: "✅ Approve merge"
        value: approve
        route: pr_merger
      - label: "⏸️ Defer (back to pending)"
        value: defer
        route: poll_pr_state_delta
      - label: "🛑 Abort"
        value: abort
        route: abort_run
```

---

## CHANGE 2 — `ado-pr.yaml`: Replace `pending_review_gate` and supporting nodes

### CHANGE 2a — Add `notifications:` block to `ado-pr.yaml` workflow header

```yaml
# FROM: (no notifications: block in ado-pr.yaml)

workflow:
  name: ado-pr
  # ...
```

```yaml
# TO:

workflow:
  name: ado-pr
  # ...
  notifications:
    namespace: polyphony.ado_pr
    correlation:
      - work_item_id
    types:
      pr_review_required:
        version: 1
        description: ADO PR awaiting human action (vote, complete, comment, or abandon)
        payload:
          kind:           {type: string}
          severity:       {type: string}
          title:          {type: string}
          message:        {type: string}
          cta_url:        {type: string}
          cta_kind:       {type: string}
          correlation_id: {type: string}
          expires_at:     {type: string}
          disposition:    {type: string}
          details:        {type: object}
```

---

### CHANGE 2b — Remove `pending_poll_counter`, `pending_review_gate_policy_router`, `pending_review_gate`; add `notify_pr_pending` + `poll_pr_state_delta`

```yaml
# FROM: ado-pr.yaml (equivalent three-node chain as github-pr.yaml)
# pending_poll_counter → pending_review_gate_policy_router → pending_review_gate
#
# Notable difference: pending_review_gate in ado-pr.yaml has a third option:
#   "✅ I manually verified merge" → treat_as_merged_emitter
# This option is DROPPED in the migration because the poll detects merge
# automatically. treat_as_merged_emitter remains in the file (still used by
# merge_failed_gate).

  - name: pending_review_gate
    type: human_gate
    prompt: |
      ## ⏳ PR Awaiting Action — ADO PR #{{ workflow.input.pr_number }}
      ...
    options:
      - label: "🔄 Continue"
        value: continue
        route: poll_status
      - label: "✅ I manually verified merge"
        value: verified_merge
        route: treat_as_merged_emitter
      - label: "🛑 Abort"
        value: abort
        route: abort_run
```

```yaml
# TO: same two-node pattern as github-pr.yaml, with ADO-specific args

  # ── PR pending — emit domain signal (fires once) ───────────────────────
  - name: notify_pr_pending
    type: notification         # TODO(post-upstream-merge): rename to type: emit
    notification: pr_review_required  # TODO(post-upstream-merge): rename to emit: pr_review_required
    payload:
      kind: pr_review_required
      severity: warning
      title: "ADO PR Review Required"
      message: >-
        ADO PR #{{ workflow.input.pr_number }} on `{{ workflow.input.branch_name }}`
        is open and awaiting action (vote +5/+10, complete, comment, or abandon)
        before the workflow continues.
      cta_url: "{{ poll_status.output.pr_url }}"
      cta_kind: review_pr
      correlation_id: "{{ workflow.input.work_item_id }}:pr-review:{{ workflow.input.pr_number }}"
      expires_at: "{{ workflow.input.review_deadline | default('') }}"
      disposition: pending
      details:
        pr_number: "{{ workflow.input.pr_number }}"
        branch_name: "{{ workflow.input.branch_name }}"
        work_item_id: "{{ workflow.input.work_item_id }}"
        organization: "{{ workflow.input.organization }}"
        project: "{{ workflow.input.project }}"
    routes:
      - to: poll_pr_state_delta

  # ── PR state delta poll (ADO) ─────────────────────────────────────────
  #
  # ADO vote reaction mapping (for Liszt reference):
  #   +10 (Approved) or +5 (Approved with suggestions) →
  #       reaction_kind=new_review, delta_details.review_state=approved
  #   -10 (Rejected) or -5 (Waiting for author) →
  #       reaction_kind=new_review, delta_details.review_state=changes_requested
  #   PR Completed → reaction_kind=merged
  #   PR Abandoned → reaction_kind=closed
  - name: poll_pr_state_delta
    type: script
    description: Poll for ADO PR state delta — blocks until reaction or timeout
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "{{ workflow.dir }}/../scripts/Poll-PrStateDelta.ps1"
      - "-Platform"
      - "ado"
      - "-PrUrl"
      - "{{ poll_status.output.pr_url }}"
      - "-PrNumber"
      - "{{ workflow.input.pr_number }}"
      - "-Organization"
      - "{{ workflow.input.organization }}"
      - "-Project"
      - "{{ workflow.input.project }}"
      - "-Repository"
      - "{{ workflow.input.repository }}"
      - "-RootId"
      - "{{ workflow.input.root_id }}"
      - "-TimeoutSeconds"
      - "{{ workflow.input.poll_timeout_seconds | default(86400) }}"
      - "-PollIntervalSeconds"
      - "{{ workflow.input.poll_interval_seconds | default(30) }}"
    routes:
      - condition: "{{ poll_pr_state_delta.output.reaction_kind == 'merged' }}"
        to: notify_pr_review_resolved
      - condition: "{{ poll_pr_state_delta.output.reaction_kind == 'closed' }}"
        to: abort_unmerged
      - condition: "{{ poll_pr_state_delta.output.reaction_kind == 'timeout' }}"
        to: stuck_review_gate_policy_router
      - to: poll_status
```

---

### CHANGE 2c — Add `notify_pr_review_resolved` (ADO)

```yaml
# FROM: (no resolved signal — already_merged_emitter fires directly)
```

```yaml
# TO: insert before already_merged_emitter (identical pattern to github-pr.yaml CHANGE 1c)

  - name: notify_pr_review_resolved
    type: notification         # TODO(post-upstream-merge): rename to type: emit
    notification: pr_review_required  # TODO(post-upstream-merge): rename to emit: pr_review_required
    payload:
      kind: pr_review_required
      severity: info
      title: "ADO PR Completed"
      message: "ADO PR #{{ workflow.input.pr_number }} on `{{ workflow.input.branch_name }}` was completed. Workflow continuing."
      cta_url: "{{ poll_status.output.pr_url }}"
      cta_kind: open_pr
      correlation_id: "{{ workflow.input.work_item_id }}:pr-review:{{ workflow.input.pr_number }}"
      disposition: resolved
      details:
        pr_number: "{{ workflow.input.pr_number }}"
        branch_name: "{{ workflow.input.branch_name }}"
    routes:
      - to: already_merged_emitter
```

---

### CHANGE 2d — Update `stuck_review_reset` route in `ado-pr.yaml`

```yaml
# FROM:
      - to: pending_review_gate_policy_router

# TO:
      - to: poll_pr_state_delta
```

---

### CHANGE 2e — Update `pr_pre_merge_gate` Defer route in `ado-pr.yaml`

```yaml
# FROM:
      - label: "⏸️ Defer (back to pending)"
        value: defer
        route: pending_review_gate

# TO:
      - label: "⏸️ Defer (back to pending)"
        value: defer
        route: poll_pr_state_delta
```

---

## Notes on Apply Order

1. Apply workflow `notifications:` header block first (CHANGE 1a / CHANGE 2a)
2. Add the new nodes (`notify_pr_pending`, `poll_pr_state_delta`, `notify_pr_review_resolved`) in place of the removed nodes (CHANGE 1b / CHANGE 2b + 1c / 2c)
3. Update routing cross-references (CHANGE 1d, 1e / CHANGE 2d, 2e)
4. Remove the now-dead `pending_poll_counter` and `pending_review_gate_policy_router` nodes (if Daniel approves simplification — see OPEN QUESTION Q5)
5. Validate: `conductor validate` on each file. Expect the existing plan-level.yaml circular-sub-workflow false positive (pre-existing issue, not introduced here).

## Notes on `workflow.input` additions

Both `github-pr.yaml` and `ado-pr.yaml` will need two new optional workflow inputs:

```yaml
# Add to workflow inputs: section in each file
  poll_timeout_seconds:
    type: integer
    required: false
    default: 86400
    description: Seconds before poll_pr_state_delta times out and escalates to stuck_review_gate
  poll_interval_seconds:
    type: integer
    required: false
    default: 30
    description: Seconds between polls inside Poll-PrStateDelta.ps1
```

These propagate from `feature-pr.yaml` and `implement-merge-group.yaml` inputs → platform-specific sub-workflow inputs. Threading TBD when Daniel confirms the defaults.
