# Decisions

## 2026-05-29

### 2026-05-29T18:06Z: User directive — Wagner's PR-gate migration: all defaults accepted

**By:** Daniel Green (via Copilot)

**What:**
Daniel greenlit all 7 of Wagner's recommended defaults on the PR-gate
compression migration (Qs 1-7 in `wagner-pr-gate-migration-plan.md`).

Bound answers:
- Q1: Keep `merge_failed_gate` + `mg_pr_failed_gate_ado` as human gates
  (no auto-retry — YAML complexity not worth it).
- Q2: 24h default `timeout_seconds` for `Poll-PrStateDelta.ps1`, configurable
  per workflow input. (Confirms Bach's Ask 1 in gate-compression ADR.)
- Q3: `ci_changed` Option C — CI green routes to `poll_status`,
  CI red emits `pr_ci_attention_required` + re-polls.
- Q4: ADO `-5` ("Waiting for author") → maps to `changes_requested`
  (routes to `pr_fixer`).
- Q5: Option A — remove `pending_poll_counter` and
  `pending_review_gate_policy_router` entirely. New default = old
  `skip`+`wait` merged.
- Q6: Resolved-disposition signal — affirm Bach's "Recommended"
  (not required).
- Q7: `on_error:` → reuse `poll_error_gate` (Option A).

Also greenlit Wagner's Incidental Findings cleanup:
- Remove vestigial `pending_poll_counter` per-file.
- Rewire `stuck_review_reset` to route to `poll_pr_state_delta` (since
  the policy router is gone).
- Rewire `pr_pre_merge_gate.defer` to route to `poll_pr_state_delta`.
- `treat_as_merged_emitter` stays (still used by `merge_failed_gate`).

**Why:** Daniel: "I agree to all defaults."

Wagner is unblocked to apply the YAML diffs from his plan + diffs file
and open the PR.

---

### 2026-05-29T18:06Z: Wagner PR gate migration shipped

**By:** Wagner (Workflow Author)

**PR:** https://github.com/PolyphonyRequiem/polyphony/pull/547

**Branch:** `refactor/pr-gate-compression-v2`

**Commit:** `d5b37d8`

## Files touched

| File | Change |
|------|--------|
| `.conductor/registry/workflows/github-pr.yaml` | Gate compression (v2.4.8 → v2.5.0) |
| `.conductor/registry/workflows/ado-pr.yaml` | Gate compression (v2.4.8 → v2.5.0) |
| `scripts/Poll-PrStateDelta.ps1` | Liszt's script added + WatermarkPath optional patch |
| `scripts/Poll-PrStateDelta.Tests.ps1` | Liszt's tests added (not yet run in CI) |
| `docs/decisions/gate-compression-pattern.md` | Bach's ADR added (was untracked) |
| `docs/decisions/domain-signal-envelope.md` | Bach's ADR dependency (was untracked) |
| `docs/decisions/polyphony-verb-error-boundary.md` | Bach's ADR dependency (was untracked) |
| `docs/north-star.md` | Bach's vision doc (was untracked) |

## Gates compressed

| File | Gate removed | Replaced with |
|------|-------------|---------------|
| `github-pr.yaml` | `pending_review_gate` (human_gate) | `notify_pr_pending` + `poll_pr_state_delta` + `notify_pr_ci_attention` |
| `ado-pr.yaml` | `pending_review_gate` (human_gate) | `notify_pr_pending` + `poll_pr_state_delta` + `notify_pr_ci_attention` |

**Total compressed: 2. Gates staying human: 17.**

## Vestigial cleanup applied

- `pending_poll_counter` removed from both files (counter logic superseded by TimeoutSeconds)
- `pending_review_gate_policy_router` removed from both files (skip/wait/auto modes superseded)
- `stuck_review_reset` rewired → `poll_pr_state_delta` (both files)
- `pr_pre_merge_gate.defer` rewired → `poll_pr_state_delta` (both files)
- `stuck_review_gate` prompts updated (removed `pending_poll_counter.output.count/cap` template refs)

## Script contract gaps vs. Wagner's plan (documented in history.md)

1. **WatermarkPath**: was mandatory in Liszt's impl — patched to optional (auto-derived from URL coords). Small fix, Liszt should verify.
2. **reaction_kind enum**: Liszt uses `pr_merged`/`pr_closed`/`ci_status_changed`/`new_review_approved` etc. (not `merged`/`closed`/`ci_changed`/`new_review`). YAML uses Liszt's actual values.
3. **initial_observation**: New reaction kind (first call, no watermark); YAML routes back to re-poll.
4. No `-PrNumber`, `-Organization`, etc. params — Liszt parses coords from PrUrl. YAML does not pass these.

## Open follow-ups for Daniel

| # | What | Where |
|---|------|--------|
| 1 | Add `on_error: to: poll_error_gate` to `poll_pr_state_delta` nodes | AB#3257 (conductor RFC Phase 2) |
| 2 | Bulk-rename `type: notification` → `type: emit`, `notification:` → `emit:` | After conductor PR #213 cherry-pick lands |
| 3 | Run `Poll-PrStateDelta.Tests.ps1` in CI pipeline | Follow-up test PR |
| 4 | Thread `poll_timeout_seconds`/`poll_interval_seconds` through `feature-pr.yaml` + `implement-merge-group.yaml` callers | Optional — defaults are functional |
| 5 | Liszt to verify WatermarkPath optional-default patch matches intended behaviour | Small review task |
