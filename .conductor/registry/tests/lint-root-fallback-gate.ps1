<#
.SYNOPSIS
    CI lint — validates root-fallback-gate.yaml structural requirements.
.DESCRIPTION
    Parses workflows/root-fallback-gate.yaml and verifies:
    1. Workflow name is 'root-fallback-gate' with entry_point: load_policy
    2. Required input: active_work_item_id
    3. Required outputs: root_id, decision, auto_policy_applied
    4. Required nodes (gate + auto + four terminals):
       load_policy, prompt_user,
       terminal_use_active_item_prompted, terminal_abort_prompted,
       terminal_use_active_item_auto, terminal_abort_auto
    5. `polyphony policy load` is the loader verb (NOT a new fallback verb)
    6. The four canonical decision/auto_policy_applied combinations
       are emitted by exactly one terminal each
    7. All route targets reference valid agent names or $end (M4)
    8. Type-agnostic (no Epic / Issue / Task / User Story / Bug
       hardcoded in the YAML)
    9. metadata.min_polyphony_version is declared
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'root-fallback-gate.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "FAIL: $yamlPath not found" -ForegroundColor Red
    exit 1
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: Workflow name ────────────────────────────────────────────────
if ($content -notmatch 'name:\s*root-fallback-gate\s') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'root-fallback-gate'"
    }
}

# ── Check 2: Entry point references load_policy ──────────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($entryPoint -ne 'load_policy') {
        $violations += [PSCustomObject]@{
            Rule   = 'wrong-entry-point'
            Detail = "Entry point should be 'load_policy', got '$entryPoint'"
        }
    }
} else {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-entry-point'
        Detail = "No entry_point field found"
    }
}

# ── Check 3: Required input field ────────────────────────────────────────
$requiredInputs = @('active_work_item_id')
foreach ($input in $requiredInputs) {
    if ($content -notmatch "(?m)^\s+${input}:\s*(#.*)?$") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-input'
            Detail = "Missing required input field: '$input'"
        }
    }
}

# ── Check 4: Required output fields ──────────────────────────────────────
$requiredOutputs = @('root_id', 'decision', 'auto_policy_applied')
foreach ($output in $requiredOutputs) {
    if ($content -notmatch "(?m)^\s+${output}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-output'
            Detail = "Missing required output field: '$output'"
        }
    }
}

# ── Check 5: Required nodes (gate + auto + four terminals) ───────────────
$requiredNodes = @(
    'load_policy',
    'prompt_user',
    'terminal_use_active_item_prompted',
    'terminal_abort_prompted',
    'terminal_use_active_item_auto',
    'terminal_abort_auto'
)
foreach ($node in $requiredNodes) {
    if ($content -notmatch "name:\s*$node\s") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-node'
            Detail = "Missing required node: '$node'"
        }
    }
}

# ── Check 6: `polyphony policy load` is the loader verb ──────────────────
# Per the design doc — we must compose existing verbs rather than ship a
# new `policy resolve-root-fallback` verb. If a NEW verb appears here
# (e.g. `policy resolve-root-fallback`), the lint flags it loudly so
# the operator must justify the verb-surface expansion in code review.
if (-not ($content -match '"policy"[\s\S]{1,40}"load"')) {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-policy-load-call'
        Detail = "Workflow must invoke 'polyphony policy load' (compose existing verbs; do not ship a new verb)"
    }
}
if ($content -match 'resolve-root-fallback') {
    $violations += [PSCustomObject]@{
        Rule   = 'forbidden-new-verb'
        Detail = "Workflow references a 'resolve-root-fallback' verb. Phase 1 explicitly forbids this — compose `policy load` instead."
    }
}

# ── Check 7: Route target validation ─────────────────────────────────────
$agentNames = @()
foreach ($line in $lines) {
    if ($line -match '^\s*-\s+name:\s*(\S+)\s*$') {
        $agentNames += $Matches[1]
    }
}

$routeTargets = @()
foreach ($line in $lines) {
    if ($line -match '^\s*-\s*to:\s*(\S+)\s*$') {
        $target = $Matches[1]
        if ($target -ne '$end') { $routeTargets += $target }
    }
    if ($line -match '^\s*route:\s*(\S+)\s*$') {
        $target = $Matches[1]
        if ($target -ne '$end') { $routeTargets += $target }
    }
}

$invalidRoutes = $routeTargets | Where-Object { $_ -notin $agentNames } | Select-Object -Unique
foreach ($route in $invalidRoutes) {
    $violations += [PSCustomObject]@{
        Rule   = 'invalid-route-target'
        Detail = "Route target '$route' does not match any agent name"
    }
}

# ── Check 8: Type-agnostic (P5) ──────────────────────────────────────────
$nonCommentContent = ($lines | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
$forbiddenTypes = @('Epic', 'Issue', 'Task', 'User Story', 'Bug')
foreach ($type in $forbiddenTypes) {
    if ($nonCommentContent -match "\b$type\b") {
        $violations += [PSCustomObject]@{
            Rule   = 'type-agnostic-violation'
            Detail = "Hardcoded process-template type name '$type' (P5 — load types from process-config.yaml at runtime)"
        }
    }
}

# ── Check 9: min_polyphony_version is declared ───────────────────────────
if ($content -notmatch 'min_polyphony_version:\s*"[0-9]') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-min-polyphony-version'
        Detail = "Workflow must declare metadata.min_polyphony_version (per polyphony-workflow-author skill)"
    }
}

# ── Check 10: Each terminal sets the canonical envelope ──────────────────
# Catches a regression where someone refactors a terminal to emit a
# `decision` value that the workflow `output:` block doesn't recognize.
$terminalDecisions = @(
    @{ Node = 'terminal_use_active_item_prompted'; ExpectedDecision = 'use_active_item';  ExpectedAuto = '$false' },
    @{ Node = 'terminal_abort_prompted';            ExpectedDecision = 'abort';             ExpectedAuto = '$false' },
    @{ Node = 'terminal_use_active_item_auto';      ExpectedDecision = 'auto_resolved';     ExpectedAuto = '$true'  },
    @{ Node = 'terminal_abort_auto';                ExpectedDecision = 'abort';             ExpectedAuto = '$true'  }
)
function Get-NodeBlock {
    param([string]$NodeName, [string[]]$Lines)
    $block = ''
    $inNode = $false
    foreach ($line in $Lines) {
        if ($line -match "^\s*-\s+name:\s*$NodeName\s*$") { $inNode = $true; continue }
        if ($inNode) {
            if ($line -match '^\s*-\s+name:') { break }
            $block += $line + "`n"
        }
    }
    return $block
}
foreach ($entry in $terminalDecisions) {
    $block = Get-NodeBlock -NodeName $entry.Node -Lines $lines
    if ($block) {
        if ($block -notmatch ("decision\s*=\s*'" + [regex]::Escape($entry.ExpectedDecision) + "'")) {
            $violations += [PSCustomObject]@{
                Rule   = 'wrong-terminal-decision'
                Detail = "Terminal '$($entry.Node)' must emit decision='$($entry.ExpectedDecision)'"
            }
        }
        $expectedAutoEsc = [regex]::Escape($entry.ExpectedAuto)
        if ($block -notmatch ("auto_policy_applied\s*=\s*$expectedAutoEsc")) {
            $violations += [PSCustomObject]@{
                Rule   = 'wrong-terminal-auto-policy-applied'
                Detail = "Terminal '$($entry.Node)' must emit auto_policy_applied=$($entry.ExpectedAuto)"
            }
        }
    }
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) root-fallback-gate.yaml violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: root-fallback-gate.yaml validated ($($requiredInputs.Count) input, $($requiredOutputs.Count) outputs, $($requiredNodes.Count) nodes, terminal envelopes pinned)" -ForegroundColor Green
exit 0
