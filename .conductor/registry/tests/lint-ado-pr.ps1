<#
.SYNOPSIS
    CI lint — validates ado-pr.yaml interface contract and structural requirements.
.DESCRIPTION
    Parses workflows/ado-pr.yaml and verifies:
    1. Interface contract matches github-pr.yaml (inputs: pr_number, branch_name,
       target_branch, review_policy; outputs: merged, pr_url)
    2. Human gate node exists with at least an 'abort' option (operator must
       always have an exit path per P6)
    3. Entry point references a valid agent name
    4. Workflow name is 'ado-pr'
    5. Stuck-review timeout MVP — pending_poll_counter exists,
       poll_status routes 'pending' through it, the counter
       routes to stuck_review_gate on cap_reached, the gate exposes
       continue_waiting / override_approved / abort, and
       stuck_review_reset exists.

    NOTE: Earlier stub revisions of ado-pr.yaml emitted an
    'ADO_PR_NOT_IMPLEMENTED' sentinel and required a 'merged' gate option;
    both checks were removed when the real lifecycle landed (poll-status →
    human gates per state → merge-feature-ado). Operator choices are now
    semantic (repoll, verified_merge, retry, abort) instead of literal
    'merged'/'aborted' values.

    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'ado-pr.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: Required input fields ───────────────────────────────────────
$requiredInputs = @('pr_number', 'branch_name', 'target_branch', 'review_policy')
foreach ($input in $requiredInputs) {
    if ($content -notmatch "(?m)^\s+${input}:\s*$") {
        # Try alternate pattern with type on same line
        if ($content -notmatch "(?m)^\s+${input}:") {
            $violations += [PSCustomObject]@{
                Rule   = 'missing-input'
                Detail = "Missing required input field: '$input'"
            }
        }
    }
}

# ── Check 2: Required output fields ──────────────────────────────────────
$requiredOutputs = @('merged', 'pr_url')
foreach ($output in $requiredOutputs) {
    if ($content -notmatch "(?m)^\s+${output}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-output'
            Detail = "Missing required output field: '$output'"
        }
    }
}

# ── Check 3: Human gate exists ───────────────────────────────────────────
if ($content -notmatch 'type:\s*human_gate') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-human-gate'
        Detail = "No human_gate node found"
    }
}

# ── Check 4: Human gate has at least an abort option ─────────────────────
# Per P6, every gate must give the operator an exit path. The real
# lifecycle uses semantic option values (repoll, verified_merge, retry,
# abort, trigger_remediation); only 'abort' is universally required.
$requiredOptions = @('abort')
foreach ($opt in $requiredOptions) {
    if ($content -notmatch "value:\s*$opt") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-gate-option'
            Detail = "Human gate missing option value: '$opt'"
        }
    }
}

# ── Check 5: Entry point references a valid agent ────────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($content -notmatch "name:\s*$entryPoint") {
        $violations += [PSCustomObject]@{
            Rule   = 'invalid-entry-point'
            Detail = "Entry point '$entryPoint' does not match any agent name"
        }
    }
}

# ── Check 6: Workflow name is 'ado-pr' ────────────────────────────────────
if ($content -notmatch 'name:\s*ado-pr') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'ado-pr'"
    }
}

# ── Check 7: Stuck-review timeout MVP — pending_poll_counter exists ─
if ($content -notmatch 'name:\s*pending_poll_counter\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-pending-poll-counter'
        Detail = "No pending_poll_counter script node found (stuck-review timeout MVP)"
    }
}

# ── Check 8: pr_feedback_analyzer routes to pending_poll_counter when waiting ───
# Under the sentiment-driven loop (post-#440), pending_poll_counter is reached
# from pr_feedback_analyzer's default (or has_negative_feedback==false) route,
# not from poll_status — poll_status routes on the deterministic `route` field
# (abort_unmerged / already_merged / merge_now / none), not on a 'pending' state.
if ($content -notmatch "(?s)- name: pr_feedback_analyzer\b.*?to:\s*pending_poll_counter.*?(?=\n  - name: |\Z)") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-pending-route'
        Detail = "pr_feedback_analyzer does not route to pending_poll_counter (continue-polling default)"
    }
}

# ── Check 9: counter routes to stuck-review gate on cap_reached ─────────
if ($content -notmatch "pending_poll_counter\.output\.cap_reached\s*==\s*true") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-cap-reached-route'
        Detail = "pending_poll_counter does not route on cap_reached==true"
    }
}

# ── Check 10: stuck_review_gate exists with all three options ───────
if ($content -notmatch 'name:\s*stuck_review_gate\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-stuck-review-gate'
        Detail = "No stuck_review_gate human_gate found (stuck-review timeout MVP)"
    }
}
$stuckOptions = @('continue_waiting', 'override_approved', 'abort')
foreach ($opt in $stuckOptions) {
    $gateBlock = ''
    $m = [regex]::Match($content, '(?s)- name: stuck_review_gate\b.*?(?=\n  - name: |\Z)')
    if ($m.Success) { $gateBlock = $m.Value }
    if ($gateBlock -notmatch "value:\s*$opt\b") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-stuck-review-option'
            Detail = "stuck_review_gate missing option value: '$opt'"
        }
    }
}

# ── Check 11: stuck_review_reset script exists ──────────────────────
if ($content -notmatch 'name:\s*stuck_review_reset\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-stuck-review-reset'
        Detail = "No stuck_review_reset script node found (stuck-review timeout MVP)"
    }
}

# ── Check 12: revise_counter no-commit fast-fail (AB#3236) ──────────────
# Two invariants enforced:
#   a. revise_counter increments unconditionally per iteration (drops
#      pre-AB#3236 digest-keyed increment that infinite-looped when
#      pr_fixer reported success but committed nothing).
#   b. revise_counter tracks no_commit_count by comparing
#      poll_status.output.head_sha across passes and emits cap_reason
#      so the cap gate can render distinct prompts for "max revisions"
#      vs "stuck fixer".
$reviseCounterBlock = ''
$m = [regex]::Match($content, '(?s)- name: revise_counter\b.*?(?=\n  - name: |\Z)')
if ($m.Success) { $reviseCounterBlock = $m.Value }
if (-not $reviseCounterBlock) {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-revise-counter'
        Detail = "No revise_counter script node found (AB#3236)"
    }
} else {
    if ($reviseCounterBlock -notmatch '\$count\s*=\s*\$count\s*\+\s*1' -or
        $reviseCounterBlock -notmatch '(?s)# AB#3236.*?increment unconditionally') {
        $violations += [PSCustomObject]@{
            Rule   = 'revise-counter-not-unconditional'
            Detail = "revise_counter must increment count unconditionally per AB#3236 (drop digest-keyed increment)"
        }
    }
    if ($reviseCounterBlock -notmatch 'no_commit_count' -or
        $reviseCounterBlock -notmatch 'poll_status\.output\.head_sha') {
        $violations += [PSCustomObject]@{
            Rule   = 'revise-counter-missing-no-commit-detection'
            Detail = "revise_counter must track no_commit_count via poll_status.output.head_sha comparison (AB#3236)"
        }
    }
    if ($reviseCounterBlock -notmatch 'cap_reason') {
        $violations += [PSCustomObject]@{
            Rule   = 'revise-counter-missing-cap-reason'
            Detail = "revise_counter must emit cap_reason ('max_revisions' | 'no_commit_stuck') so revise_cap_gate can branch prompts (AB#3236)"
        }
    }
}

# ── Check 13: revise_cap_gate branches on cap_reason (AB#3236) ──────────
$capGateBlock = ''
$m = [regex]::Match($content, '(?s)- name: revise_cap_gate\b.*?(?=\n  - name: |\Z)')
if ($m.Success) { $capGateBlock = $m.Value }
if ($capGateBlock -and $capGateBlock -notmatch "cap_reason\s*==\s*'no_commit_stuck'") {
    $violations += [PSCustomObject]@{
        Rule   = 'revise-cap-gate-not-branched'
        Detail = "revise_cap_gate prompt must branch on revise_counter.output.cap_reason == 'no_commit_stuck' (AB#3236)"
    }
}

# ── AB#3186 — Unattended cap_mode policy-router wiring ──────────────────
#
# Each cap-hit gate must be preceded by a `<gate>_policy_router` script
# node that invokes resolve-unattended-cap-mode.ps1 and offers three
# routes:
#   auto_proceed → workflow-specific target (the "force one more / accept"
#                  semantic for this site; not checked here — site-specific)
#   auto_fail    → terminal_cap_auto_fail
#   (fallthrough)→ the cap-hit gate itself (manual + catch-all)
#
# These checks enumerate the concrete cap-hit gates known to this
# workflow rather than suffix-matching `*_cap_gate` so that an
# accidentally-deleted router (vs. a renamed/removed gate) shows up
# as a violation rather than vanishing silently.
foreach ($gate in @('revise_cap_gate')) {
    $routerName = "${gate}_policy_router"
    if ($content -notmatch "name:\s*$([regex]::Escape($routerName))\b") {
        $violations += [PSCustomObject]@{
            Rule   = "missing-cap-mode-policy-router-$gate"
            Detail = "AB#3186: '$gate' must be preceded by a '$routerName' script node that calls resolve-unattended-cap-mode.ps1 and routes cap_mode=auto_proceed/auto_fail/manual."
        }
        continue
    }
    $routerBlock = ''
    $routerMatch = [regex]::Match($content, "(?s)- name:\s*$([regex]::Escape($routerName))\b.*?(?=\n  - name: |\Z)")
    if ($routerMatch.Success) { $routerBlock = $routerMatch.Value }
    if ($routerBlock -notmatch 'resolve-unattended-cap-mode\.ps1') {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-wrong-helper-$gate"
            Detail = "AB#3186: '$routerName' must invoke the shared 'resolve-unattended-cap-mode.ps1' helper, not inline policy lookup."
        }
    }
    if ($routerBlock -notmatch 'to:\s*terminal_cap_auto_fail\b') {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-missing-auto-fail-route-$gate"
            Detail = "AB#3186: '$routerName' must include a 'to: terminal_cap_auto_fail' route guarded by cap_mode == 'auto_fail'."
        }
    }
    if ($routerBlock -notmatch "to:\s*$([regex]::Escape($gate))\b") {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-missing-manual-fallthrough-$gate"
            Detail = "AB#3186: '$routerName' must include a final unconditional 'to: $gate' route as the manual + catch-all fallthrough (per conductor-mechanics M4)."
        }
    }
}

if ($content -notmatch 'name:\s*terminal_cap_auto_fail\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-terminal-cap-auto-fail'
        Detail = "AB#3186: 'terminal_cap_auto_fail' terminal node missing. Required as the auto_fail target for cap-mode policy routers; must invoke abort-run.ps1 with -Reason 'cap-auto-fail'."
    }
} else {
    $terminalMatch = [regex]::Match($content, '(?s)- name:\s*terminal_cap_auto_fail\b.*?(?=\n  - name: |\Z)')
    if ($terminalMatch.Success -and $terminalMatch.Value -notmatch '"cap-auto-fail"') {
        $violations += [PSCustomObject]@{
            Rule   = 'terminal-cap-auto-fail-wrong-reason'
            Detail = "AB#3186: 'terminal_cap_auto_fail' must invoke abort-run.ps1 with -Reason 'cap-auto-fail' (the discriminator vs 'operator-abort' for post-mortem diagnostics)."
        }
    }
}

        # ── AB#3184 — pre-merge policy router for policy.pr.defaults.mode ────────
        #
        # The auto-merge path (poll_status route == 'merge_now') must transit
        # through `pr_pre_merge_policy_router` so `policy.pr.defaults.mode ==
        # 'manual'` can interpose `pr_pre_merge_gate` before pr_merger fires.
        # Operator-initiated merges (force_merge / override_approved / retry_merge
        # gate options) intentionally bypass the router — the operator IS the
        # policy decision at those gates.
        if ($content -notmatch 'name:\s*pr_pre_merge_policy_router\b') {
            $violations += [PSCustomObject]@{
                Rule   = 'missing-pre-merge-policy-router'
                Detail = "AB#3184: 'pr_pre_merge_policy_router' script node missing. Required to read policy.pr.defaults.mode via resolve-pr-policy.ps1 and route mode=='manual' to pr_pre_merge_gate."
            }
        } else {
            $routerMatch = [regex]::Match($content, '(?s)- name:\s*pr_pre_merge_policy_router\b.*?(?=\n  - name: |\Z)')
            $routerBlock = if ($routerMatch.Success) { $routerMatch.Value } else { '' }
            if ($routerBlock -notmatch 'resolve-pr-policy\.ps1') {
                $violations += [PSCustomObject]@{
                    Rule   = 'pre-merge-router-wrong-helper'
                    Detail = "AB#3184: 'pr_pre_merge_policy_router' must invoke the shared 'resolve-pr-policy.ps1' helper, not inline policy lookup."
                }
            }
            if ($routerBlock -notmatch 'to:\s*pr_pre_merge_gate\b') {
                $violations += [PSCustomObject]@{
                    Rule   = 'pre-merge-router-missing-manual-route'
                    Detail = "AB#3184: 'pr_pre_merge_policy_router' must include a 'to: pr_pre_merge_gate' route guarded by mode == 'manual'."
                }
            }
            if ($routerBlock -notmatch 'to:\s*pr_merger\b') {
                $violations += [PSCustomObject]@{
                    Rule   = 'pre-merge-router-missing-auto-route'
                    Detail = "AB#3184: 'pr_pre_merge_policy_router' must include a 'to: pr_merger' route for mode in ['auto', 'warning']."
                }
            }
        }

        if ($content -notmatch 'name:\s*pr_pre_merge_gate\b') {
            $violations += [PSCustomObject]@{
                Rule   = 'missing-pre-merge-gate'
                Detail = "AB#3184: 'pr_pre_merge_gate' human_gate missing. Required as the mode=='manual' divert target; must offer approve→pr_merger / defer→pending_review_gate / abort→terminal_abort_run options."
            }
        }

        # Reject any direct 'to: pr_merger' from poll_status — the merge_now
        # path MUST go through pr_pre_merge_policy_router for AB#3184.
        $pollStatusMatch = [regex]::Match($content, '(?s)- name:\s*poll_status\b.*?(?=\n  - name: |\Z)')
        if ($pollStatusMatch.Success -and $pollStatusMatch.Value -match '(?m)^\s*-\s*to:\s*pr_merger\b') {
            $violations += [PSCustomObject]@{
                Rule   = 'poll-status-bypasses-pre-merge-router'
                Detail = "AB#3184: 'poll_status' must route merge_now through 'pr_pre_merge_policy_router', not directly to 'pr_merger'."
            }
        }

        # ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) ado-pr.yaml violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: ado-pr.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, human gate with $($requiredOptions.Count) required option(s))" -ForegroundColor Green
exit 0
