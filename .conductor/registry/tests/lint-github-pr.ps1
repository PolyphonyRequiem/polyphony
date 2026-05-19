<#
.SYNOPSIS
    CI lint — validates github-pr.yaml interface contract and structural requirements.
.DESCRIPTION
    Parses workflows/github-pr.yaml and verifies:
    1. Interface contract matches ado-pr.yaml (inputs: pr_number, branch_name,
       target_branch, review_policy; outputs: merged, pr_url)
    2. PR initial reviewer agent exists using an Opus model (advisory review
       fired once per workflow run — sentiment-driven loop relies on
       pr_feedback_analyzer for subsequent cycles, NOT a re-running reviewer)
    3. PR feedback analyzer agent exists (sentiment-driven loop heart;
       replaces the pre-#440 pr_reviewer re-run loop)
    4. PR fixer agent exists
    5. PR merger agent exists
    6. Human gate exists for fix exhaustion (P7: fail honestly)
    7. Entry point references a valid agent name
    8. All route targets reference valid agent names or $end
    9. pr_fixer routes back into the polling loop (poll_status), NOT to
       pr_initial_reviewer — the advisory reviewer runs ONCE per workflow
       invocation under the sentiment-driven model.
    10. AB#3236 — revise_counter no-commit fast-fail invariants (see
        Checks 12/13).
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'github-pr.yaml'

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
    if ($content -notmatch "(?m)^\s+${input}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-input'
            Detail = "Missing required input field: '$input'"
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

# ── Check 3: PR initial reviewer agent with Opus ────────────────────────
# Renamed pr_reviewer → pr_initial_reviewer in PRs #438/#440 when the
# sentiment-driven loop replaced the re-running reviewer with a single
# advisory review fired once per workflow run.
if ($content -notmatch 'name:\s*pr_initial_reviewer\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-initial-reviewer'
        Detail = "No pr_initial_reviewer agent found"
    }
}
if ($content -notmatch 'name:\s*pr_initial_reviewer\b[^\n]*\n(?:[^\n]*\n)*?\s*model:\s*claude-opus-4(\.\d+)?(-[a-z0-9-]+)?') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-opus-initial-reviewer'
        Detail = "pr_initial_reviewer must use an Opus model from the claude-opus-4.* family"
    }
}

# ── Check 4: PR feedback analyzer agent (sentiment-driven loop) ─────────
# pr_feedback_analyzer is the heart of the new loop: it digests platform
# feedback into a single brief that pr_fixer addresses. Replaces the
# pre-#440 re-running reviewer.
if ($content -notmatch 'name:\s*pr_feedback_analyzer\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-feedback-analyzer'
        Detail = "No pr_feedback_analyzer agent found (sentiment-driven loop heart)"
    }
}

# ── Check 5: PR fixer agent ─────────────────────────────────────────────
if ($content -notmatch 'name:\s*pr_fixer\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-fixer'
        Detail = "No pr_fixer agent found"
    }
}

# ── Check 6: PR merger agent ─────────────────────────────────────────────
if ($content -notmatch 'name:\s*pr_merger\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-merger'
        Detail = "No pr_merger agent found"
    }
}

# ── Check 7: Human gate for fix exhaustion (P7) ──────────────────────────
if ($content -notmatch 'type:\s*human_gate') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-human-gate'
        Detail = "No human_gate node found for fix exhaustion (P7)"
    }
}

# ── Check 8: Human gate has force_merge, continue, and abort options ─────
$requiredOptions = @('force_merge', 'abort')
foreach ($opt in $requiredOptions) {
    if ($content -notmatch "value:\s*$opt") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-gate-option'
            Detail = "Human gate missing option value: '$opt'"
        }
    }
}

# ── Check 9: Entry point references a valid agent ────────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($content -notmatch "name:\s*$entryPoint") {
        $violations += [PSCustomObject]@{
            Rule   = 'invalid-entry-point'
            Detail = "Entry point '$entryPoint' does not match any agent name"
        }
    }
}

# ── Check 10: Workflow name is 'github-pr' ────────────────────────────────
if ($content -notmatch 'name:\s*github-pr') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'github-pr'"
    }
}

# ── Check 11: Fixer routes back into the polling loop (sentiment-driven) ─
# Verify pr_fixer routes to poll_status (NOT pr_initial_reviewer). Under
# the post-#440 sentiment-driven model, the advisory reviewer runs ONCE
# per workflow invocation; subsequent loops are driven by re-polling
# platform state on the new head SHA after the fixer's push.
$fixerBlock = ''
$m = [regex]::Match($content, '(?s)- name: pr_fixer\b.*?(?=\n  - name: |\Z)')
if ($m.Success) { $fixerBlock = $m.Value }
if ($fixerBlock -and $fixerBlock -notmatch 'to:\s*poll_status\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'broken-fix-loop'
        Detail = "pr_fixer must route back to poll_status (sentiment-driven loop re-evaluates on new head SHA, does NOT re-run pr_initial_reviewer)"
    }
}
if ($fixerBlock -and $fixerBlock -match 'to:\s*pr_initial_reviewer\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'reviewer-rerun-leak'
        Detail = "pr_fixer must NOT route back to pr_initial_reviewer — the advisory reviewer runs once per workflow invocation only (post-#440)"
    }
}

# ── Check 12: revise_counter no-commit fast-fail (AB#3236) ──────────────
# Mirrors the AB#3236 invariants enforced in lint-ado-pr.ps1. Two
# requirements:
#   a. revise_counter increments unconditionally per iteration (drops
#      pre-AB#3236 digest-keyed increment that infinite-looped when
#      pr_fixer reported success but committed nothing).
#   b. revise_counter tracks no_commit_count by comparing
#      poll_status.output.head_sha across passes and emits cap_reason
#      so revise_cap_gate can render distinct prompts.
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
    Write-Host "FAIL: $($violations.Count) github-pr.yaml violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: github-pr.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, initial reviewer/feedback analyzer/fixer/merger agents, sentiment-driven loop)" -ForegroundColor Green
exit 0
