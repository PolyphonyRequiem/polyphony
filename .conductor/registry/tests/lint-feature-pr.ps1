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
    14. ADO platform parity (Phase 5 closeout):
        - feature_pr_creator_ado node exists
        - feature_pr_creator_failed_gate_ado node exists
        - feature_pr_updater_poster_ado node exists
        - pr_lifecycle_ado has at least one explicit `to: remediation_counter` route
        - the legacy `ado_remediation_not_supported_emitter` short-circuit
          is GONE (positive removal assertion)
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

# ── Check 16-20: ADO platform parity (Phase 5 closeout) ──────────────────
# These checks codify the parity contract from
# docs/decisions/ado-feature-pr-parity.md: feature-pr.yaml must NOT carry
# any ADO-specific short-circuits, and the ADO leg must hit every node a
# GitHub PR would (creator, failure gate, remediation chain, updater
# poster).

# 16: ADO creator node exists.
if ($content -notmatch 'name:\s*feature_pr_creator_ado') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-ado-creator'
        Detail = "No feature_pr_creator_ado node found - ADO leg must have its own creator (calls 'pr create-feature-ado')"
    }
}

# 17: ADO creator-failed gate exists.
if ($content -notmatch 'name:\s*feature_pr_creator_failed_gate_ado') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-ado-creator-failed-gate'
        Detail = "No feature_pr_creator_failed_gate_ado node found - ADO leg must surface creator failures via a human gate"
    }
}

# 18: ADO updater poster exists. Mirrors plan-level.yaml's
# plan_reviewer_poster_ado: agent emits comment_body, sibling script
# posts via `polyphony pr post-comment-ado`.
if ($content -notmatch 'name:\s*feature_pr_updater_poster_ado') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-ado-updater-poster'
        Detail = "No feature_pr_updater_poster_ado script found - ADO remediation must post the updater's comment_body via 'polyphony pr post-comment-ado'"
    }
}

# 19: pr_lifecycle_ado must route to remediation_counter (no short-circuit
# allowed). Scan only the routes block of pr_lifecycle_ado.
$adoLifecycleBlock = ''
$inAdoLifecycle = $false
$inAdoRoutes = $false
$adoRoutesLines = @()
foreach ($line in $lines) {
    if ($line -match '^\s*-\s*name:\s*pr_lifecycle_ado\s*$') {
        $inAdoLifecycle = $true
        continue
    }
    if ($inAdoLifecycle -and $line -match '^\s*-\s*name:\s*\S+\s*$') {
        # Next node — exit the block.
        $inAdoLifecycle = $false
        $inAdoRoutes = $false
        continue
    }
    if ($inAdoLifecycle) {
        if ($line -match '^\s*routes:\s*$') {
            $inAdoRoutes = $true
            continue
        }
        if ($inAdoRoutes) {
            $adoRoutesLines += $line
        }
    }
}
$adoRoutesText = [string]::Join([Environment]::NewLine, $adoRoutesLines)
# AB#3181 added pr_remediation_policy as an intermediate resolver step:
# pr_lifecycle_ado → pr_remediation_policy → remediation_counter. Accept
# either direct routing to remediation_counter (pre-AB#3181) OR routing
# through pr_remediation_policy (post-AB#3181). Both enter the
# remediation chain; what matters is that the ADO leg does NOT
# short-circuit past it on merged==false.
if ($adoRoutesText -notmatch 'to:\s*(remediation_counter|pr_remediation_policy)') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-ado-remediation-route'
        Detail = "pr_lifecycle_ado has no route to remediation_counter or pr_remediation_policy - ADO leg must enter the remediation chain on merged==false (no short-circuit)"
    }
}

# 20: The legacy `ado_remediation_not_supported_emitter` short-circuit must
# be GONE. Positive removal assertion so a future regression that adds it
# back fails lint.
if ($content -match 'ado_remediation_not_supported_emitter') {
    $violations += [PSCustomObject]@{
        Rule   = 'ado-remediation-stub-present'
        Detail = "Legacy ado_remediation_not_supported_emitter short-circuit found - ADO remediation is fully wired since v1.2.0; remove this node"
    }
}

# ── Check 21-24: AB#3238 — drift integration entry guard ─────────────────
# Every feature PR (apex→main, child→feature/<apex>, GitHub or ADO) must
# integrate origin/<target_branch> drift before opening the PR, otherwise
# the diff includes unrelated drift as false-positive review feedback
# (which compounds with AB#3236's revise_counter loop to burn unbounded
# LLM tokens). The guard lives at the feature-pr.yaml entry point so it
# runs once per feature PR regardless of platform leg.

# 21: integrate_target_drift node exists.
if ($content -notmatch '(?m)name:\s*integrate_target_drift\s*$') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-drift-integrator'
        Detail = "No integrate_target_drift node found - AB#3238 requires drift integration before opening any feature PR"
    }
}

# 22: integrate_target_drift is the workflow entry point.
if ($content -match 'entry_point:\s*(\S+)') {
    if ($Matches[1] -ne 'integrate_target_drift') {
        $violations += [PSCustomObject]@{
            Rule   = 'drift-integrator-not-entry'
            Detail = "AB#3238: feature-pr.yaml entry_point must be integrate_target_drift (was '$($Matches[1])') — any other entry bypasses drift integration"
        }
    }
}

# 23: Both AB#3238 gates exist (conflict + non-conflict failure).
foreach ($gate in @('integrate_target_drift_conflict_gate', 'integrate_target_drift_failed_gate')) {
    if ($content -notmatch "(?m)name:\s*$gate\s*$") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-drift-gate'
            Detail = "AB#3238: missing $gate human_gate - drift integration failures must route to an operator decision"
        }
    }
}

# 24: integrate_target_drift's routes branch on error_code (merge_conflict
# → conflict gate, other error → failed gate, otherwise → pr_platform_router).
$driftBlock = ''
$inDrift = $false
$driftLines = @()
foreach ($line in $lines) {
    if ($line -match '^\s*-\s*name:\s*integrate_target_drift\s*$') {
        $inDrift = $true
        continue
    }
    if ($inDrift -and $line -match '^\s*-\s*name:\s*\S+\s*$') {
        $inDrift = $false
        continue
    }
    if ($inDrift) { $driftLines += $line }
}
$driftText = [string]::Join([Environment]::NewLine, $driftLines)
$requireSubstrings = @(
    "'merge_conflict'",
    'to: integrate_target_drift_conflict_gate',
    'to: integrate_target_drift_failed_gate',
    'to: pr_platform_router'
)
foreach ($needle in $requireSubstrings) {
    if ($driftText -notmatch [regex]::Escape($needle)) {
        $violations += [PSCustomObject]@{
            Rule   = 'drift-routes-malformed'
            Detail = "AB#3238: integrate_target_drift routes block is missing required edge or condition fragment: '$needle'"
        }
    }
}

# ── AB#3186 — Unattended cap_mode policy-router wiring ──────────────────
#
# Each cap-hit gate must be preceded by a `<gate>_policy_router` script
# node that invokes resolve-unattended-cap-mode.ps1 and offers three
# routes:
#   auto_proceed → workflow-specific target (the "force one more / accept"
#                  semantic for this site; not checked here — site-specific)
#   auto_fail    → terminal_cap_auto_fail
#   (fallthrough)→ the cap-hit gate itself (manual + catch-all)
#
# These checks enumerate the concrete cap-hit gates known to this
# workflow rather than suffix-matching `*_cap_gate` so that an
# accidentally-deleted router (vs. a renamed/removed gate) shows up
# as a violation rather than vanishing silently.
foreach ($gate in @('remediation_cap_gate')) {
    $routerName = "${gate}_policy_router"
    if ($content -notmatch "name:\s*$([regex]::Escape($routerName))\b") {
        $violations += [PSCustomObject]@{
            Rule   = "missing-cap-mode-policy-router-$gate"
            Detail = "AB#3186: '$gate' must be preceded by a '$routerName' script node that calls resolve-unattended-cap-mode.ps1 and routes cap_mode=auto_proceed/auto_fail/manual."
        }
        continue
    }
    $routerBlock = ''
    $routerMatch = [regex]::Match($content, "(?s)- name:\s*$([regex]::Escape($routerName))\b.*?(?=\n  - name: |\Z)")
    if ($routerMatch.Success) { $routerBlock = $routerMatch.Value }
    if ($routerBlock -notmatch 'resolve-unattended-cap-mode\.ps1') {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-wrong-helper-$gate"
            Detail = "AB#3186: '$routerName' must invoke the shared 'resolve-unattended-cap-mode.ps1' helper, not inline policy lookup."
        }
    }
    if ($routerBlock -notmatch 'to:\s*terminal_cap_auto_fail\b') {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-missing-auto-fail-route-$gate"
            Detail = "AB#3186: '$routerName' must include a 'to: terminal_cap_auto_fail' route guarded by cap_mode == 'auto_fail'."
        }
    }
    if ($routerBlock -notmatch "to:\s*$([regex]::Escape($gate))\b") {
        $violations += [PSCustomObject]@{
            Rule   = "cap-mode-router-missing-manual-fallthrough-$gate"
            Detail = "AB#3186: '$routerName' must include a final unconditional 'to: $gate' route as the manual + catch-all fallthrough (per conductor-mechanics M4)."
        }
    }
}

if ($content -notmatch 'name:\s*terminal_cap_auto_fail\b') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-terminal-cap-auto-fail'
        Detail = "AB#3186: 'terminal_cap_auto_fail' terminal node missing. Required as the auto_fail target for cap-mode policy routers; must invoke abort-run.ps1 with -Reason 'cap-auto-fail'."
    }
} else {
    $terminalMatch = [regex]::Match($content, '(?s)- name:\s*terminal_cap_auto_fail\b.*?(?=\n  - name: |\Z)')
    if ($terminalMatch.Success -and $terminalMatch.Value -notmatch '"cap-auto-fail"') {
        $violations += [PSCustomObject]@{
            Rule   = 'terminal-cap-auto-fail-wrong-reason'
            Detail = "AB#3186: 'terminal_cap_auto_fail' must invoke abort-run.ps1 with -Reason 'cap-auto-fail' (the discriminator vs 'operator-abort' for post-mortem diagnostics)."
        }
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

Write-Host "PASS: feature-pr.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, creator/platform-router/github-lifecycle/ado-lifecycle, remediation counter (max 3), cap gate, planner, seeder, ADO parity: creator+failed-gate+updater-poster+remediation-route, no legacy stub)" -ForegroundColor Green
exit 0
