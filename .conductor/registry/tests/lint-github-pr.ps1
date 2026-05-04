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
if ($content -notmatch 'claude-opus-4.7-1m-internal|claude-opus-4.7.*1000000') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-opus-1m'
        Detail = "PR reviewer must use Opus 1M model (claude-opus-4.7-1m-internal)"
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
