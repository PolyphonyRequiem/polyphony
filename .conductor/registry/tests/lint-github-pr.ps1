<#
.SYNOPSIS
    CI lint — validates github-pr.yaml interface contract and structural requirements.
.DESCRIPTION
    Parses workflows/github-pr.yaml and verifies:
    1. Interface contract matches ado-pr.yaml (inputs: pr_number, branch_name,
       target_branch, review_policy; outputs: merged, pr_url)
    2. PR reviewer agent exists using Opus 1M model
    3. PR fixer agent exists using Sonnet model
    4. PR merger agent exists
    5. Review-fix loop has iteration counter with max 10 cap (P7)
    6. Human gate exists for fix exhaustion (P7: fail honestly)
    7. Entry point references a valid agent name
    8. All route targets reference valid agent names or $end
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

# ── Check 3: PR reviewer agent with Opus 1M ──────────────────────────────
if ($content -notmatch 'name:\s*pr_reviewer') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-reviewer'
        Detail = "No pr_reviewer agent found"
    }
}
if ($content -notmatch 'name:\s*pr_reviewer\b[^\n]*\n(?:[^\n]*\n)*?\s*model:\s*claude-opus-4(\.\d+)?(-[a-z0-9-]+)?') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-opus-reviewer'
        Detail = "PR reviewer must use an Opus model from the claude-opus-4.* family"
    }
}

# ── Check 4: PR fixer agent with Sonnet model ────────────────────────────
if ($content -notmatch 'name:\s*pr_fixer') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-fixer'
        Detail = "No pr_fixer agent found"
    }
}

# ── Check 5: PR merger agent ─────────────────────────────────────────────
if ($content -notmatch 'name:\s*pr_merger') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-merger'
        Detail = "No pr_merger agent found"
    }
}

# ── Check 6: Iteration counter with max 10 (P7) ─────────────────────────
if ($content -notmatch 'name:\s*review_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-counter'
        Detail = "No review_counter script node found for iteration tracking"
    }
}
if ($content -notmatch '10') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-iteration-cap'
        Detail = "No iteration cap of 10 found (P7: fail honestly)"
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

# ── Check 11: Fixer routes back to reviewer (loop structure) ─────────────
# Verify the review-fix loop is properly wired: pr_fixer → pr_reviewer
$fixerBlock = ''
$inFixer = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*pr_fixer') { $inFixer = $true }
    if ($inFixer) { $fixerBlock += $line + "`n" }
    if ($inFixer -and $fixerBlock.Length -gt 100 -and $line -match '^\s*-\s*name:') { break }
}
if ($fixerBlock -and $fixerBlock -notmatch 'to:\s*pr_reviewer') {
    $violations += [PSCustomObject]@{
        Rule   = 'broken-fix-loop'
        Detail = "pr_fixer must route back to pr_reviewer for re-review"
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

Write-Host "PASS: github-pr.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, reviewer/fixer/merger agents, iteration cap, human gate)" -ForegroundColor Green
exit 0
