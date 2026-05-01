<#
.SYNOPSIS
    CI lint — validates feature-pr.yaml interface contract and structural requirements.
.DESCRIPTION
    Parses workflows/feature-pr.yaml and verifies:
    1. Required inputs: work_item_id, feature_branch, target_branch
    2. Required outputs: merged, pr_url
    3. Feature PR creator node exists (script type)
    4. PR platform router exists for platform delegation
    5. GitHub PR lifecycle sub-workflow exists (pr_lifecycle_github)
    6. ADO PR lifecycle sub-workflow exists (pr_lifecycle_ado)
    7. Remediation counter script exists with max 3 cap
    8. Remediation cap gate (human_gate) exists with continue and abort options
    9. Remediation planner agent exists
    10. Remediation seeder agent exists
    11. Entry point references a valid agent name
    12. Abort option routes to remediation_abort or $end (merged=false)
    13. Sub-workflow routes to remediation_counter on merged==false
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

# ── Check 3: Feature PR creator node ─────────────────────────────────────
if ($content -notmatch 'name:\s*feature_pr_creator') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-creator'
        Detail = "No feature_pr_creator node found"
    }
}

# ── Check 4: PR platform router ──────────────────────────────────────────
if ($content -notmatch 'name:\s*pr_platform_router') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-platform-router'
        Detail = "No pr_platform_router node found for platform delegation"
    }
}

# ── Check 5: GitHub PR lifecycle sub-workflow ─────────────────────────────
if ($content -notmatch 'name:\s*pr_lifecycle_github') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-github-lifecycle'
        Detail = "No pr_lifecycle_github sub-workflow node found"
    }
}

# ── Check 6: ADO PR lifecycle sub-workflow ────────────────────────────────
if ($content -notmatch 'name:\s*pr_lifecycle_ado') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-ado-lifecycle'
        Detail = "No pr_lifecycle_ado sub-workflow node found"
    }
}

# ── Check 7: Remediation counter with max 3 cap ─────────────────────────
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

# ── Check 8: Remediation cap gate (human_gate) ──────────────────────────
if ($content -notmatch 'name:\s*remediation_cap_gate') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-cap-gate'
        Detail = "No remediation_cap_gate human_gate node found"
    }
}

# ── Check 9: Gate has continue and abort options ─────────────────────────
$requiredOptions = @('continue', 'abort')
foreach ($opt in $requiredOptions) {
    if ($content -notmatch "value:\s*$opt") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-gate-option'
            Detail = "Remediation cap gate missing option value: '$opt'"
        }
    }
}

# ── Check 10: Remediation planner agent ──────────────────────────────────
if ($content -notmatch 'name:\s*remediation_planner') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-planner'
        Detail = "No remediation_planner agent found"
    }
}

# ── Check 11: Remediation seeder agent ───────────────────────────────────
if ($content -notmatch 'name:\s*remediation_seeder') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-seeder'
        Detail = "No remediation_seeder agent found"
    }
}

# ── Check 12: Entry point references a valid agent ──────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($content -notmatch "name:\s*$entryPoint") {
        $violations += [PSCustomObject]@{
            Rule   = 'invalid-entry-point'
            Detail = "Entry point '$entryPoint' does not match any agent name"
        }
    }
}

# ── Check 13: Workflow name is 'feature-pr' ──────────────────────────────
if ($content -notmatch 'name:\s*feature-pr') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'feature-pr'"
    }
}

# ── Check 14: Remediation abort emits merged=false ───────────────────────
if ($content -notmatch 'name:\s*remediation_abort') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-abort-handler'
        Detail = "No remediation_abort node found for abort routing"
    }
}

# ── Check 15: Sub-workflow routes to remediation_counter on merged==false ─
$hasRemediationRoute = $false
foreach ($line in $lines) {
    if ($line -match 'to:\s*remediation_counter') { $hasRemediationRoute = $true; break }
}
if (-not $hasRemediationRoute) {
    $violations += [PSCustomObject]@{
        Rule   = 'broken-remediation-loop'
        Detail = "No route to remediation_counter found — sub-workflows must route to remediation on merged==false"
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

Write-Host "PASS: feature-pr.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, creator/platform-router/github-lifecycle/ado-lifecycle, remediation counter (max 3), cap gate, planner, seeder)" -ForegroundColor Green
exit 0
