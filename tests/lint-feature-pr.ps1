<#
.SYNOPSIS
    CI lint — validates feature-pr.yaml interface contract and structural requirements.
.DESCRIPTION
    Parses workflows/feature-pr.yaml and verifies:
    1. Required inputs: work_item_id, feature_branch, target_branch
    2. Required outputs: merged, pr_url
    3. Feature PR creator agent exists
    4. Feature PR review agent exists
    5. Feature PR merger agent exists
    6. Remediation counter script exists with max 3 cap
    7. Remediation cap gate (human_gate) exists with continue and abort options
    8. Remediation planner agent exists
    9. Remediation seeder agent exists
    10. Entry point references a valid agent name
    11. Abort option routes to remediation_abort or $end (merged=false)
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'feature-pr.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: Required input fields ───────────────────────────────────────
$requiredInputs = @('work_item_id', 'feature_branch', 'target_branch')
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

# ── Check 3: Feature PR creator agent ────────────────────────────────────
if ($content -notmatch 'name:\s*feature_pr_creator') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-creator'
        Detail = "No feature_pr_creator agent found"
    }
}

# ── Check 4: Feature PR review agent ─────────────────────────────────────
if ($content -notmatch 'name:\s*feature_pr_review') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-reviewer'
        Detail = "No feature_pr_review agent found"
    }
}

# ── Check 5: Feature PR merger agent ─────────────────────────────────────
if ($content -notmatch 'name:\s*feature_pr_merger') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-merger'
        Detail = "No feature_pr_merger agent found"
    }
}

# ── Check 6: Remediation counter with max 3 cap ─────────────────────────
if ($content -notmatch 'name:\s*remediation_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-counter'
        Detail = "No remediation_counter script node found for cycle tracking"
    }
}
if ($content -notmatch '-lt\s+3|-le\s+2|max.*3|3\s*cycle') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-cycle-cap'
        Detail = "No remediation cycle cap of 3 found"
    }
}

# ── Check 7: Remediation cap gate (human_gate) ──────────────────────────
if ($content -notmatch 'name:\s*remediation_cap_gate') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-cap-gate'
        Detail = "No remediation_cap_gate human_gate node found"
    }
}

# ── Check 8: Gate has continue and abort options ─────────────────────────
$requiredOptions = @('continue', 'abort')
foreach ($opt in $requiredOptions) {
    if ($content -notmatch "value:\s*$opt") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-gate-option'
            Detail = "Remediation cap gate missing option value: '$opt'"
        }
    }
}

# ── Check 9: Remediation planner agent ───────────────────────────────────
if ($content -notmatch 'name:\s*remediation_planner') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-planner'
        Detail = "No remediation_planner agent found"
    }
}

# ── Check 10: Remediation seeder agent ───────────────────────────────────
if ($content -notmatch 'name:\s*remediation_seeder') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-seeder'
        Detail = "No remediation_seeder agent found"
    }
}

# ── Check 11: Entry point references a valid agent ──────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($content -notmatch "name:\s*$entryPoint") {
        $violations += [PSCustomObject]@{
            Rule   = 'invalid-entry-point'
            Detail = "Entry point '$entryPoint' does not match any agent name"
        }
    }
}

# ── Check 12: Workflow name is 'feature-pr' ──────────────────────────────
if ($content -notmatch 'name:\s*feature-pr') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'feature-pr'"
    }
}

# ── Check 13: Remediation abort emits merged=false ───────────────────────
if ($content -notmatch 'name:\s*remediation_abort') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-abort-handler'
        Detail = "No remediation_abort node found for abort routing"
    }
}

# ── Check 14: Review routes to counter on changes_requested ──────────────
$reviewBlock = ''
$inReview = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*feature_pr_review') { $inReview = $true }
    if ($inReview) { $reviewBlock += $line + "`n" }
    if ($inReview -and $reviewBlock.Length -gt 100 -and $line -match '^\s*-\s*name:') { break }
}
if ($reviewBlock -and $reviewBlock -notmatch 'to:\s*remediation_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'broken-remediation-loop'
        Detail = "feature_pr_review must route to remediation_counter on changes_requested"
    }
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) feature-pr.yaml violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: feature-pr.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, creator/reviewer/merger agents, remediation counter (max 3), cap gate, planner, seeder)" -ForegroundColor Green
exit 0
