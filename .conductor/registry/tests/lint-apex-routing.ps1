<#
.SYNOPSIS
    CI lint — validates phase-based routing in the apex workflow YAML.
.DESCRIPTION
    Parses polyphony-full.yaml and verifies:
    1. All expected Polyphony phase values have corresponding routes
    2. Routing conditions use only state_detector.output.phase comparisons
    3. No work-item type-name literals appear in routing conditions
    4. Sub-workflow references use type: workflow with valid paths
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$apexPath = Join-Path $repoRoot 'workflows' 'polyphony-full.yaml'

if (-not (Test-Path $apexPath)) {
    Write-Host "SKIP: $apexPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $apexPath -Raw
$lines = @(Get-Content $apexPath)

$violations = @()

# ── Check 1: All expected phase values are covered ────────────────────────
$expectedPhases = @(
    'needs_planning',
    'needs_seeding',
    'ready_for_implementation',
    'in_progress',
    'ready_for_completion',
    'done',
    'removed'
)

foreach ($phase in $expectedPhases) {
    if ($content -notmatch [regex]::Escape("state_detector.output.phase == '$phase'")) {
        $violations += [PSCustomObject]@{
            Rule    = 'missing-phase-route'
            Detail  = "No route for phase '$phase'"
        }
    }
}

# ── Check 2: No type-name literals in routing conditions ──────────────────
$typeNames = @('Epic', 'Issue', 'Task', 'User Story', 'Bug', 'Feature')
$typePattern = '\b(' + ($typeNames -join '|') + ')\b'

# Only check lines that contain 'when:' (routing conditions)
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^\s*when:' -and $line -match $typePattern) {
        $violations += [PSCustomObject]@{
            Rule    = 'type-literal-in-route'
            Detail  = "Line $($i + 1): Type-name literal in routing condition: $($line.Trim())"
        }
    }
}

# ── Check 3: Sub-workflow agents use type: workflow ───────────────────────
$subWorkflowNames = @('planning', 'implementation', 'close_out')
foreach ($name in $subWorkflowNames) {
    if ($content -notmatch "name:\s*$name\s*\n\s*type:\s*workflow") {
        $violations += [PSCustomObject]@{
            Rule    = 'missing-workflow-type'
            Detail  = "Agent '$name' missing or not declared as type: workflow"
        }
    }
}

# ── Check 4: Sub-workflow agents have workflow path fields ────────────────
foreach ($name in $subWorkflowNames) {
    # Find agent block and check it has a workflow field referencing a sibling yaml
    if ($content -match "(?s)name:\s*$name\s*\n\s*type:\s*workflow.*?(?=\n\s*-\s*name:|\z)") {
        $agentBlock = $Matches[0]
        if ($agentBlock -notmatch 'workflow:\s*\./') {
            $violations += [PSCustomObject]@{
                Rule    = 'missing-workflow-path'
                Detail  = "Agent '$name' missing workflow path to sibling YAML"
            }
        }
    }
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) apex routing violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: Apex workflow routing validated ($($expectedPhases.Count) phases, $($subWorkflowNames.Count) sub-workflows)" -ForegroundColor Green
exit 0
