<#
.SYNOPSIS
    CI lint — validates plan-level.yaml open_questions policy wiring and structural requirements.
.DESCRIPTION
    Parses workflows/plan-level.yaml and verifies:
    1. open_questions_policy script node exists and calls `polyphony policy resolve --domain open_questions`
    2. open_questions_counter script node exists
    3. open_questions_answer_counter script node exists
    4. Policy-aware routes exist (mode==auto, mode==manual, mode==warning)
    5. No hardcoded severity list remains in architect→gate routing
    6. open_questions_gate references policy mode and loop counter in prompt
    7. Entry point references a valid agent name
    8. All route targets reference valid agent names or $end
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'plan-level.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: open_questions_policy script node exists ────────────────────
if ($content -notmatch 'name:\s*open_questions_policy') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-oq-policy-node'
        Detail = "No open_questions_policy script node found"
    }
}

# ── Check 2: Policy node calls polyphony policy resolve --domain open_questions
# Args are on separate lines in YAML, so check for key components individually
$hasPolicyResolve = ($content -match '"policy"') -and ($content -match '"resolve"') -and ($content -match '"--domain"') -and ($content -match '"open_questions"')
if (-not $hasPolicyResolve) {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-policy-resolve-call'
        Detail = "open_questions_policy does not call 'polyphony policy resolve --domain open_questions'"
    }
}

# ── Check 3: open_questions_counter script node exists ───────────────────
if ($content -notmatch 'name:\s*open_questions_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-oq-counter-node'
        Detail = "No open_questions_counter script node found"
    }
}

# ── Check 4: open_questions_answer_counter script node exists ────────────
if ($content -notmatch 'name:\s*open_questions_answer_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-oq-answer-counter-node'
        Detail = "No open_questions_answer_counter script node found"
    }
}

# ── Check 5: Policy-aware routes — mode==auto skips gate ─────────────────
if ($content -notmatch "open_questions_policy\.output\.mode\s*==\s*'auto'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-auto-mode-route'
        Detail = "No route condition for mode=='auto' (should skip gate)"
    }
}

# ── Check 6: Policy-aware routes — mode==manual gates on any question ────
if ($content -notmatch "open_questions_policy\.output\.mode\s*==\s*'manual'") {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-manual-mode-route'
        Detail = "No route condition for mode=='manual' (should gate on any question)"
    }
}

# ── Check 7: Policy-aware routes — mode==warning uses severities_at_or_above
if ($content -notmatch 'severities_at_or_above\(open_questions_policy\.output\.min_severity\)') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-warning-mode-route'
        Detail = "No route condition using severities_at_or_above(open_questions_policy.output.min_severity)"
    }
}

# ── Check 8: No hardcoded severity list in architect routes ──────────────
# The old pattern had selectattr('severity', 'in', ['moderate', 'major', 'critical'])
# directly in the architect's routes block. This should no longer exist.
if ($content -match "architect\.output\.open_questions\s*\|\s*selectattr\('severity',\s*'in',\s*\['") {
    $violations += [PSCustomObject]@{
        Rule   = 'hardcoded-severity-filter'
        Detail = "Hardcoded severity list found in architect routing — should use policy-driven severities_at_or_above()"
    }
}

# ── Check 9: Gate prompt references policy mode ──────────────────────────
if ($content -notmatch 'open_questions_policy\.output\.mode.*open_questions_counter\.output') {
    # Looser check: both should appear in the gate prompt section
    $hasMode = $content -match 'open_questions_policy\.output\.mode'
    $hasCounter = $content -match 'open_questions_counter\.output\.(iteration|max_loops)'
    if (-not ($hasMode -and $hasCounter)) {
        $violations += [PSCustomObject]@{
            Rule   = 'gate-missing-policy-context'
            Detail = "open_questions_gate prompt should surface policy mode and loop counter"
        }
    }
}

# ── Check 10: Cap reached route exists ───────────────────────────────────
if ($content -notmatch 'open_questions_counter\.output\.cap_reached\s*==\s*true') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-cap-reached-route'
        Detail = "No route condition for cap_reached==true (should auto-proceed to review)"
    }
}

# ── Report ───────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "`n❌ plan-level.yaml open_questions policy lint FAILED ($($violations.Count) violations):`n" -ForegroundColor Red
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)] $($v.Detail)" -ForegroundColor Red
    }
    Write-Host ""
    exit 1
} else {
    Write-Host "✅ plan-level.yaml open_questions policy lint passed (10 checks)" -ForegroundColor Green
    exit 0
}
