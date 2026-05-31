# PR-Gate → Notification+Poll Migration Plan

**Author:** Wagner (Workflow Author)  
**Date:** 2026-05-29  
**Status:** Draft — awaiting Daniel's answers to OPEN QUESTIONS section  
**Companion doc:** `wagner-pr-gate-migration-diffs.md`  
**Scope:** PR-lifecycle `human_gate` nodes only. Non-PR gates (planning, scope, branch-mismatch, seeder) are explicitly excluded.

---

## 1. Inventory — PR-lifecycle `human_gate` nodes

Gates were found by grepping `type: human_gate` across the sub-workflow library, then filtering to those whose purpose involves a PR creation, review, approval, or merge operation.

### 1.1 `feature-pr.yaml`

| # | Gate name | Line | Currently asks | Routes on |
|---|-----------|------|----------------|-----------|
| 1 | `integrate_target_drift_conflict_gate` | 178 | Operator: rebase conflict hit — resolve by hand and force-push, then Retry | Retry → integrate_target_drift; Abort → $end |
| 2 | `integrate_target_drift_failed_gate` | 233 | Operator: drift integration failed (non-conflict) — diagnose error_code and retry | Retry → integrate_target_drift; Abort → $end |
| 3 | `feature_pr_inputs_missing_gate` | 397 | Operator: ADO inputs missing — workflow cannot proceed | Abort only → abort_run |
| 4 | `feature_pr_creator_failed_gate_ado` | 433 | Operator: ADO PR creation failed — retry (idempotent) or abort | Retry → feature_pr_creator_ado; Abort → abort_run |
| 5 | `remediation_cap_gate` | 654 | Operator: 3 remediation cycles exhausted — continue (override cap) or abort | Continue → remediation_guidance_loader; Abort → remediation_abort |

### 1.2 `github-pr.yaml`

| # | Gate name | Line | Currently asks | Routes on |
|---|-----------|------|----------------|-----------|
| 6 | `poll_error_gate` | 408 | Operator: poll_status failed — retry or abort | Retry → poll_status; Abort → abort_run |
| 7 | `revise_cap_gate` | 780 | Operator: pr_fixer hit revision cap or no-commit stuck — re-poll, force revision, force merge, or abort | Re-poll → poll_status; Force → pr_fixer; Force merge → pr_merger; Abort → abort_run |
| **8** | **`pending_review_gate`** | **1026** | **Operator: PR open, no negative feedback — merge, comment, or close it in GitHub then click Continue** | **Continue → poll_status; Abort → abort_run** |
| 9 | `stuck_review_gate` | 1090 | Operator: review pending past poll cap (60) — continue waiting, override approved, or abort | Continue → stuck_review_reset; Override → pr_merger; Abort → abort_run |
| 10 | `pr_pre_merge_gate` | 1219 | Operator: policy=manual — approve merge, defer, or abort | Approve → pr_merger; Defer → pending_review_gate; Abort → abort_run |

### 1.3 `ado-pr.yaml`

| # | Gate name | Line | Currently asks | Routes on |
|---|-----------|------|----------------|-----------|
| 11 | `ado_inputs_missing_gate` | 209 | Operator: ADO connection inputs missing | Abort only → abort_run |
| 12 | `poll_error_gate` | 507 | Operator: poll_status failed — retry or abort | Retry → poll_status; Abort → abort_run |
| 13 | `revise_cap_gate` | 907 | Operator: pr_fixer hit revision cap — same options as github | Re-poll / Force / Force merge / Abort |
| **14** | **`pending_review_gate`** | **1147** | **Operator: ADO PR open, no negative feedback — complete/vote/comment in ADO then click Continue** | **Continue → poll_status; Verified merge → treat_as_merged_emitter; Abort → abort_run** |
| 15 | `stuck_review_gate` | 1216 | Operator: ADO review pending past poll cap — continue waiting, override, or abort | Continue → stuck_review_reset; Override → pr_merger; Abort → abort_run |
| 16 | `pr_pre_merge_gate` | 1351 | Operator: policy=manual — approve merge, defer, or abort | Approve → pr_merger; Defer → pending_review_gate; Abort → abort_run |
| 17 | `merge_failed_gate` | 1446 | Operator: `pr merge-feature-ado` failed — retry, verified merge, re-poll, or abort | Retry → pr_merger; Verified → treat_as_merged_emitter; Re-poll → poll_status; Abort → abort_run |

### 1.4 `implement-merge-group.yaml` (MG PR gates only)

| # | Gate name | Line | Currently asks | Routes on |
|---|-----------|------|----------------|-----------|
| 18 | `mg_pr_inputs_missing_gate` | 2279 | Operator: ADO inputs missing for MG PR | Abort only → abort_run |
| 19 | `mg_pr_failed_gate_ado` | 2320 | Operator: ADO MG PR open/merge failed — retry or abort | Retry → mg_pr_open_ado; Abort → abort_run |

**Total: 19 PR-lifecycle gates across 4 files.**

---

## 2. Migration Table

### Summary decision per gate

| Gate | File | Compress? | Justification |
|------|------|-----------|---------------|
| `integrate_target_drift_conflict_gate` | feature-pr | ❌ STAYS | Resolution is a manual git operation (rebase + force-push) that a poll script cannot reliably detect as "completed correctly". No PR URL yet. |
| `integrate_target_drift_failed_gate` | feature-pr | ❌ STAYS | Operator must diagnose error_code (worktree_dirty, push_failed, etc.) and intervene. No observable external state change to poll. |
| `feature_pr_inputs_missing_gate` | feature-pr | ❌ STAYS | Configuration error — no external state to poll. Informational abort only. |
| `feature_pr_creator_failed_gate_ado` | feature-pr | ❌ STAYS | ADO PR creation failure. Could auto-retry, but failure may indicate permission/config issues requiring human intervention. Infra scope, not reaction scope. |
| `remediation_cap_gate` | feature-pr | ❌ STAYS | Genuine judgment call: 3 remediation cycles failed. Human decides whether to override the cap or abandon. |
| `poll_error_gate` (github) | github-pr | ❌ STAYS | Infrastructure failure (poll script crashed). Human decides retry vs abort. Out of scope for reaction-poll pattern. |
| `revise_cap_gate` (github) | github-pr | ❌ STAYS | Judgment call: pr_fixer exhausted or stuck. Human chooses from multiple non-deterministic paths. |
| **`pending_review_gate` (github)** | github-pr | ✅ **COMPRESS** | Classic category-1 gate: workflow waits for human to act on PR (merge/approve/comment/close). Poll script can detect all resolutions. Human's role is the PR action itself, not a workflow decision. |
| `stuck_review_gate` (github) | github-pr | ❌ STAYS | Review genuinely stalled past timeout. Human judgment: extend, override, or abort. This IS the timeout fallback for the compressed gate. |
| `pr_pre_merge_gate` (github) | github-pr | ❌ STAYS | Policy=manual explicit approval. High-stakes — operator must confirm before merge fires. Explicitly exempted by gate-compression-pattern ADR (irreversible action). |
| `ado_inputs_missing_gate` | ado-pr | ❌ STAYS | Configuration error, informational abort. |
| `poll_error_gate` (ado) | ado-pr | ❌ STAYS | Infrastructure failure. Same reasoning as github. |
| `revise_cap_gate` (ado) | ado-pr | ❌ STAYS | Same as github. |
| **`pending_review_gate` (ado)** | ado-pr | ✅ **COMPRESS** | Same as github version. ADO vote states (+10, +5, -10) are all pollable via `polyphony pr poll-status-ado`. |
| `stuck_review_gate` (ado) | ado-pr | ❌ STAYS | Same reasoning as github. |
| `pr_pre_merge_gate` (ado) | ado-pr | ❌ STAYS | Same as github. |
| `merge_failed_gate` (ado) | ado-pr | ❌ STAYS* | Retry is valid but "I manually verified merge" path is a judgment call. *See open question — bounded auto-retry may be sensible here, but it's a separate pattern from the reaction-poll migration. |
| `mg_pr_inputs_missing_gate` | impl-mg | ❌ STAYS | Configuration error, informational abort. |
| `mg_pr_failed_gate_ado` | impl-mg | ❌ STAYS* | Same caveats as merge_failed_gate. |

**Compress: 2 | Stay: 17**

### 2.1 Detailed migration spec — `pending_review_gate` (GitHub)

**Current flow:**
```
pr_feedback_analyzer (no neg feedback)
  → pending_poll_counter           # increments counter, checks cap
  → pending_review_gate_policy_router   # loads policy, routes on review_wait_mode
  → pending_review_gate            # HUMAN CLICKS CONTINUE
  → poll_status
```

**Proposed flow:**
```
pr_feedback_analyzer (no neg feedback)
  → notify_pr_pending              # emit domain signal (once)
  → poll_pr_state_delta            # Poll-PrStateDelta.ps1 — blocks until reaction or timeout
  → (route on reaction_kind)
       merged        → already_merged_emitter
       closed        → abort_unmerged
       new_review    → poll_status   (re-read absolute state; routes to analyzer or merge path)
       new_commit    → poll_status   (CI will restart; re-read state)
       ci_changed    → [OPEN QUESTION — see §4]
       timeout       → stuck_review_gate_policy_router
```

**Domain signal kind:** `pr_review_required`  
**CTA URL:** `{{ poll_status.output.pr_url }}`  
**CTA kind:** `review_pr`  
**Watermark fields:** `last_review_count`, `last_commit_sha`, `last_ci_status`, `last_comment_count`, `gate_opened_at`

**Incidental cleanup:** `pending_poll_counter` and `pending_review_gate_policy_router` become vestigial. The timeout logic moves into `Poll-PrStateDelta.ps1` (`-TimeoutSeconds`). The policy router's `review_wait_mode` concept maps as follows:
- `wait` (default) → emit notification + poll (the new behavior)
- `skip` → bypass notification but still poll (omit the notify step; route direct to `poll_pr_state_delta`)
- `auto` → no longer meaningful; `poll_pr_state_delta` IS auto. Flag as deprecated.

Daniel's judgment needed on whether to keep the policy router with updated semantics or drop it.

**Resolved-disposition emit:** After routing from `poll_pr_state_delta.reaction_kind == 'merged'` and before `already_merged_emitter`, emit a `disposition: resolved` signal on the same `pr_review_required` notification type. This closes the CTA lifecycle in platespinner.

### 2.2 Detailed migration spec — `pending_review_gate` (ADO)

Identical to GitHub spec except:
- Platform: `ado`
- Script additionally requires `-Organization`, `-Project`, `-Repository`, `-RootId` args
- ADO vote states mapped to reaction_kind:
  - `+10` (Approved) or `+5` (Approved with suggestions) → `new_review` (with `delta_details.review_state = 'approved'`)
  - `-10` (Rejected) → `new_review` (with `delta_details.review_state = 'changes_requested'`)
  - `-5` (Waiting for author) → `new_review` (with `delta_details.review_state = 'changes_requested'`)
  - PR Abandoned → `closed`
  - PR Completed → `merged`
- The ADO `pending_review_gate`'s "I manually verified merge" option is superseded by the poll detecting `merged` state. Drop this option from the YAML.
- ADO `pending_review_gate_policy_router` same incidental cleanup applies.

---

## 3. Shared Script Contract — `Poll-PrStateDelta.ps1`

This is the binding contract between Wagner (YAML) and Liszt (PowerShell implementation). Do NOT deviate from this interface on either side without coordinating both.

### 3.1 Script path

```
.conductor/registry/scripts/Poll-PrStateDelta.ps1
```

Invoked from workflow YAML via `type: script` → `command: pwsh`, `args: ["-File", ".."]`.

### 3.2 Parameters

```powershell
param (
    [Parameter(Mandatory)] [ValidateSet('github','ado')] [string] $Platform,

    # PR coordinates
    [Parameter(Mandatory)] [string]  $PrUrl,       # Full PR URL (GitHub: https://github.com/org/repo/pull/N; ADO: any valid pr_url)
    [Parameter(Mandatory)] [int]     $PrNumber,    # PR number (used for polyphony verb calls)

    # ADO-only (ignored for GitHub)
    [string] $Organization = '',
    [string] $Project      = '',
    [string] $Repository   = '',
    [string] $RootId       = '',

    # Watermark (re-entry support)
    [string] $WatermarkPath = '',  # Path to a JSON file for reading/writing watermark.
                                   # If empty: uses a temp-path file keyed by Platform+PrNumber.
                                   # If provided and file exists: reads as initial watermark.
                                   # Always writes updated watermark to same path on exit.

    # Timing
    [int] $TimeoutSeconds       = 86400,  # 24h default; 0 = no timeout
    [int] $PollIntervalSeconds  = 30      # Default 30s between polls
)
```

### 3.3 Behavior contract

1. **On first call (or if watermark file is absent/empty):** Record the current PR state as the baseline watermark. Immediately poll; if state already differs from watermark (race condition on entry), return that reaction at once.

2. **On subsequent polls:** Every `PollIntervalSeconds`, compare current PR state to the watermark. If any tracked field has changed, return the appropriate `reaction_kind`.

3. **Polling loop terminates when:**
   - A reaction is detected → exit 0 with reaction JSON
   - `TimeoutSeconds` has elapsed since `gate_opened_at` → exit 0 with `reaction_kind = "timeout"`

4. **Exit codes:**
   - `0` — reaction detected OR timeout (both are normal domain outcomes, not errors)
   - Non-zero — infrastructure failure only: authentication error, network unreachable, malformed input parameter, polyphony/gh CLI not on PATH. Non-zero exits should NOT be used for "condition not yet met".

5. **Watermark fields tracked:**

| Field | Type | Description |
|-------|------|-------------|
| `last_review_count` | int | Number of reviews/votes at watermark time |
| `last_commit_sha` | string | PR head commit SHA at watermark time |
| `last_ci_status` | string | `success`/`failure`/`pending`/`unknown` at watermark time |
| `last_comment_count` | int | Number of top-level PR comments at watermark time |
| `gate_opened_at` | ISO8601 string | UTC timestamp when the watermark was first established (used for timeout) |

### 3.4 Output JSON schema

Written to `$env:CONDUCTOR_OUTPUT` (the conductor output capture path). Always exit-0 on this path.

```json
{
  "reaction_kind": "<enum>",
  "new_watermark": {
    "last_review_count": 2,
    "last_commit_sha": "abc123def456",
    "last_ci_status": "success",
    "last_comment_count": 5,
    "gate_opened_at": "2026-05-29T09:22:12Z"
  },
  "delta_details": {
    "review_state": "approved",
    "old_ci_status": "pending",
    "new_ci_status": "failure",
    "merged_at": "2026-05-29T10:15:00Z"
  }
}
```

**`reaction_kind` enum:**

| Value | Trigger condition |
|-------|-------------------|
| `merged` | PR was merged/completed (GitHub: `state=closed AND mergedAt!=null`; ADO: `status=completed`) |
| `closed` | PR was closed/abandoned without merge (GitHub: `state=closed AND mergedAt==null`; ADO: `status=abandoned`) |
| `new_review` | New review posted, OR vote changed (GitHub: `reviews.length > last_review_count`; ADO: most-significant vote changed). `delta_details.review_state` = `approved` / `changes_requested` / `commented`. |
| `new_commit` | Head SHA changed (new push to PR branch) |
| `ci_changed` | CI overall status changed (e.g. pending→success, success→failure). `delta_details.old_ci_status` and `new_ci_status` populated. |
| `timeout` | `TimeoutSeconds` elapsed with no reaction |

**`delta_details` fields** — only populate the fields relevant to the `reaction_kind`. Consumers MUST ignore unknown fields.

### 3.5 Platform-specific implementation notes (for Liszt)

**GitHub:** Use `gh pr view {PrNumber} --json state,mergedAt,reviews,commits,statusCheckRollup` for polling. Map `statusCheckRollup[*].state` to a single `ci_status` string: all SUCCESS → `success`; any FAILURE → `failure`; otherwise → `pending`.

**ADO:** Use `polyphony pr poll-status-ado --organization ... --project ... --repository ... --root-id ... --pr-number ...`. The existing verb already returns `route`, `head_sha`, `pr_url`, and vote state. Augment watermark with PR thread count for comment detection.

### 3.6 Watermark file path (when `-WatermarkPath` is empty)

```
{[System.IO.Path]::GetTempPath()}/conductor-pr-delta-{Platform}-{PrNumber}.json
```

This mirrors the pattern used by existing counter files in ado-pr.yaml (line 1263).

---

## 4. OPEN QUESTIONS FOR DANIEL

These require judgment that Wagner cannot resolve unilaterally.

### Q1 — Which gates should stay genuinely human?

Recommendation above is: all 17 non-compressed gates stay. The 2 `pending_review_gate` nodes are the only clean compressions.

**Specific sub-question:** `merge_failed_gate` in `ado-pr.yaml` (line 1446) and `mg_pr_failed_gate_ado` in `implement-merge-group.yaml` (line 2320). Both offer "retry merge" (idempotent, automated) and "I manually verified merge" (judgment). Could be bounded auto-retry (retry once automatically, then surface gate if it fails again). Is that worth the YAML complexity?

**Daniel's call needed:** Yes/no on auto-retry for those two gates.

### Q2 — Default timeout per gate

The `Poll-PrStateDelta.ps1` contract specifies `-TimeoutSeconds 86400` (24h) as default. Is 24h the right default for a PR review gate? Options:

- A) 24h (1 working day) — conservative, allows overnight
- B) 8h (1 working shift) — faster escalation
- C) Per-workflow-input configurable with a documented default

**Bach's existing recommendation** (gate-compression-pattern ADR, Ask 1): configurable per workflow input with documented default. Wagner agrees. But what's the documented default?

**Daniel's call needed:** Default timeout value (or affirm configurable-only).

### Q3 — `ci_changed` reaction routing

When `Poll-PrStateDelta.ps1` returns `reaction_kind = 'ci_changed'`:

- Option A) **Auto-continue:** route to `poll_status` (re-read full state; if CI is green and review approved, proceed to merge path)
- Option B) **Notify + wait:** emit a `pr_ci_attention_required` domain signal and loop back to `poll_pr_state_delta` (good if CI turning red should alert the operator but not block)
- Option C) **CI green → continue, CI red → emit + wait:** split routing based on `delta_details.new_ci_status`

**Daniel's call needed:** Which option? Recommendation is C: CI green routes to `poll_status` (recheck merge readiness), CI red emits `pr_ci_attention_required` + re-polls (since pr_fixer is responsible for code fixes, the workflow shouldn't fail just because CI turned red on an external commit).

### Q4 — ADO review state vs GitHub review state — mapping fidelity

ADO votes are: `+10` (Approved), `+5` (Approved with suggestions), `0` (No vote), `-5` (Waiting for author), `-10` (Rejected). GitHub states are: `APPROVED`, `CHANGES_REQUESTED`, `COMMENTED`, `DISMISSED`.

The script maps both into the same `review_state` sub-enum (`approved` / `changes_requested` / `commented`). Is the coarsening acceptable? Specifically: should ADO `-5` (Waiting for author) be treated as `changes_requested` (workflow routes to pr_fixer) or as a no-op (keep polling)?

**Daniel's call needed:** ADO `-5` treatment.

### Q5 — `review_wait_mode` policy and the eliminated policy router

The current `pending_review_gate_policy_router` reads `policy.unattended.review_wait_mode`:
- `skip` → bypass gate, poll silently
- `auto` → reject (deprecated)
- `wait` (default) → show human gate

With the gate replaced by automated polling, `wait` becomes "emit notification + poll" and `skip` becomes "silent poll". The policy router becomes vestigial.

**Options:**
- A) Remove `pending_poll_counter` and `pending_review_gate_policy_router` entirely. The new default behavior IS the old `skip`+`wait` merged.
- B) Keep the router with updated semantics: `wait` → emit notification, `skip` → no notification (direct to `poll_pr_state_delta`)
- C) Keep the router only to preserve the `auto` rejection path (for orgs still misconfigured with `mode=auto`)

**Daniel's call needed:** Simplify (A) or preserve policy hook (B)?

### Q6 — Resolved-disposition signal requirement

After `poll_pr_state_delta` detects `merged`, should the workflow emit a `disposition: resolved` signal on the `pr_review_required` type (to close the CTA lifecycle in platespinner)?

**Bach's ADR recommendation:** Recommended (not required) — B from the gate-compression-pattern ADR open asks.

**Daniel's call needed:** Affirm B, or upgrade to required for PR gates specifically?

### Q7 — Conductor `on_error:` on `poll_pr_state_delta`

The script exits non-zero only on infrastructure failures. If it exits non-zero (auth failure, CLI missing), the workflow should route somewhere sensible. Options:
- A) `on_error:` → `poll_error_gate` (reuse existing error gate — operator retries)
- B) `on_error:` → `notify_pr_poll_failed` (emit error domain signal) + `poll_error_gate`
- C) Accept the current behavior (no `on_error:` declared; conductor default handling)

**Daniel's call needed:** Preferred on_error handling. Option A is simplest and consistent with current `poll_status → poll_error_gate` pattern.

---

## 5. Incidental Findings (cross-workflow surprises)

1. **`pending_poll_counter` is vestigial after this migration.** The counter tracks "how many times have we been stuck in the pending_review loop" to trigger `stuck_review_gate`. With `Poll-PrStateDelta.ps1` handling timeout internally, this counter is no longer needed. Its removal cleans up ~30 lines of YAML per file. Flag for Daniel: include this cleanup in the same PR as the gate migration?

2. **`pending_review_gate_policy_router` becomes vestigial too.** Same chain.

3. **`stuck_review_reset` still needed.** When the operator clicks "Continue waiting" at `stuck_review_gate`, the reset node zeros the counter file. With the counter removed, `stuck_review_reset` would need to re-route to `poll_pr_state_delta` instead of `pending_review_gate_policy_router`. Minor wiring change.

4. **ADO `treat_as_merged_emitter` remains relevant.** It's used by `pending_review_gate` ("I manually verified merge") AND by `merge_failed_gate`. Since `merge_failed_gate` stays as a human gate, `treat_as_merged_emitter` is still needed.

5. **`pending_review_gate.defer` route in `pr_pre_merge_gate`.** The `pr_pre_merge_gate` (policy=manual) has a "Defer" option that routes back to `pending_review_gate`. After migration, this needs to route to `poll_pr_state_delta` (or to `notify_pr_pending` to re-emit if the notification already expired). Minor wiring change — document in migration diffs.
