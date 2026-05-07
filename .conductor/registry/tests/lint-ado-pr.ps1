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
