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
    5. Stuck-review timeout MVP — ado_pending_poll_counter exists,
       ado_pr_status_check routes 'pending' through it, the counter
       routes to ado_stuck_review_gate on cap_reached, the gate exposes
       continue_waiting / override_approved / abort, and
       ado_stuck_review_reset exists.

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

# ── Check 7: Stuck-review timeout MVP — ado_pending_poll_counter exists ─
if ($content -notmatch 'name:\s*ado_pending_poll_counter\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-pending-poll-counter'
        Detail = "No ado_pending_poll_counter script node found (stuck-review timeout MVP)"
    }
}

# ── Check 8: ado_pr_status_check routes 'pending' through the counter ───
if ($content -notmatch "to:\s*ado_pending_poll_counter[\s\S]{0,200}?ado_pr_status_check\.output\.state\s*==\s*'pending'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-pending-route'
        Detail = "ado_pr_status_check does not route 'pending' through ado_pending_poll_counter"
    }
}

# ── Check 9: counter routes to stuck-review gate on cap_reached ─────────
if ($content -notmatch "ado_pending_poll_counter\.output\.cap_reached\s*==\s*true") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-cap-reached-route'
        Detail = "ado_pending_poll_counter does not route on cap_reached==true"
    }
}

# ── Check 10: ado_stuck_review_gate exists with all three options ───────
if ($content -notmatch 'name:\s*ado_stuck_review_gate\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-stuck-review-gate'
        Detail = "No ado_stuck_review_gate human_gate found (stuck-review timeout MVP)"
    }
}
$stuckOptions = @('continue_waiting', 'override_approved', 'abort')
foreach ($opt in $stuckOptions) {
    $gateBlock = ''
    $m = [regex]::Match($content, '(?s)- name: ado_stuck_review_gate\b.*?(?=\n  - name: |\Z)')
    if ($m.Success) { $gateBlock = $m.Value }
    if ($gateBlock -notmatch "value:\s*$opt\b") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-stuck-review-option'
            Detail = "ado_stuck_review_gate missing option value: '$opt'"
        }
    }
}

# ── Check 11: ado_stuck_review_reset script exists ──────────────────────
if ($content -notmatch 'name:\s*ado_stuck_review_reset\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-stuck-review-reset'
        Detail = "No ado_stuck_review_reset script node found (stuck-review timeout MVP)"
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
