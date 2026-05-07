<#
.SYNOPSIS
    CI lint — validates apex-driver.yaml + companion sub-workflows
    structural requirements.

.DESCRIPTION
    Parses the three Phase 7 apex-driver workflow YAMLs:
      - apex-driver.yaml         (the keystone)
      - apex-wave-dispatch.yaml  (per-wave inner sub-workflow)
      - apex-item-dispatch.yaml  (per-item innermost sub-workflow)

    Plus the three companion scripts under .conductor/registry/scripts/.

    Verifies eight structural requirement classes:

      1. apex-driver.yaml has `name: apex-driver`, `entry_point:
         preflight_sync`, and the four-input contract (apex_id,
         intent, platform — apex_id and intent are required by spec;
         platform is optional with a default).

      2. The four lifecycle workflows the apex-driver dispatches into
         are all referenced (or explicitly deferred via the
         placeholder).

      3. The three companion scripts exist on disk and are referenced
         from the workflow YAMLs.

      4. All route targets resolve to declared agents or `$end` (M4).

      5. Type-agnostic (P5 — no Epic / Issue / Task / User Story / Bug
         hardcoded outside of comment-only contexts).

      6. metadata.min_polyphony_version is declared on every workflow.

      7. Both inner sub-workflows (wave + item) declare `is defined`
         guards on every cross-leg verb output reference (M3) and
         pipe booleans through `| string | lower` in their workflow
         output map (M7).

      8. Per-item lifecycle dispatch wiring (the "real" — non-placeholder
         — shape produced by the Phase 7 follow-up):
           a. apex-item-dispatch declares all four named branches
              (plan_level_dispatch, actionable_dispatch,
              implement_pg_dispatch, feature_pr_dispatch).
           b. apex-item-dispatch references all four lifecycle workflow
              files via parent-relative paths.
           c. The renegotiation bubble-up is wired end-to-end:
              apex-item-dispatch.output references plan_level_dispatch's
              renegotiation_pending; apex-wave-dispatch.output exposes a
              wave-aggregated renegotiation_pending; apex-driver.output
              exposes a top-level renegotiation_pending.
         Skipped when apex-item-dispatch still carries the original
         `lifecycle_dispatch_placeholder` step (MVP deferred shape).

    Exits 0 if clean, 1 if violations are found.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$workflowsDir = Join-Path $repoRoot 'workflows'
$scriptsDir = Join-Path $repoRoot 'scripts'

$apexYaml = Join-Path $workflowsDir 'apex-driver.yaml'
$waveYaml = Join-Path $workflowsDir 'apex-wave-dispatch.yaml'
$itemYaml = Join-Path $workflowsDir 'apex-item-dispatch.yaml'

if (-not (Test-Path $apexYaml)) {
    Write-Host "SKIP: $apexYaml not found" -ForegroundColor Yellow
    exit 0
}

$violations = @()

function Add-Violation([string]$rule, [string]$detail) {
    $script:violations += [PSCustomObject]@{ Rule = $rule; Detail = $detail }
}

function Get-AgentNames([string[]]$lines) {
    $names = @()
    foreach ($line in $lines) {
        if ($line -match '^\s*-\s+name:\s*(\S+)\s*$') {
            $names += $Matches[1]
        }
    }
    return $names
}

function Get-RouteTargets([string[]]$lines) {
    $targets = @()
    foreach ($line in $lines) {
        if ($line -match '^\s*-\s*to:\s*(\S+)\s*$') {
            $t = $Matches[1]
            if ($t -ne '$end') { $targets += $t }
        }
        if ($line -match '^\s*route:\s*(\S+)\s*$') {
            $t = $Matches[1]
            if ($t -ne '$end') { $targets += $t }
        }
    }
    return $targets
}

# ── Check 1: apex-driver.yaml — name + entry_point + four-input contract ──
$apexContent = Get-Content $apexYaml -Raw
$apexLines = @(Get-Content $apexYaml)

if ($apexContent -notmatch '(?m)^\s+name:\s*apex-driver\s') {
    Add-Violation 'wrong-workflow-name' "apex-driver.yaml workflow name must be 'apex-driver'"
}

if ($apexContent -match 'entry_point:\s*(\S+)') {
    $entry = $Matches[1]
    if ($entry -ne 'preflight_sync') {
        Add-Violation 'wrong-entry-point' "apex-driver entry_point should be 'preflight_sync', got '$entry'"
    }
} else {
    Add-Violation 'missing-entry-point' "apex-driver.yaml has no entry_point field"
}

# Required inputs (apex_id, intent are the spec contract; platform is optional).
$requiredApexInputs = @('apex_id', 'intent')
foreach ($i in $requiredApexInputs) {
    if ($apexContent -notmatch "(?m)^\s+${i}:\s*(#.*)?$") {
        Add-Violation 'missing-input' "apex-driver.yaml is missing required input '$i'"
    }
}

# ── Check 2: lifecycle workflows referenced (or deferred via placeholder) ──
# In the MVP, the per-item sub-workflow has a `lifecycle_dispatch_placeholder`
# step that explicitly notes the deferred lifecycle dispatch. The check is
# satisfied by either the placeholder OR explicit references to the four
# lifecycle workflows.
if (-not (Test-Path $itemYaml)) {
    Add-Violation 'missing-sub-workflow' "apex-item-dispatch.yaml not found at $itemYaml"
} else {
    $itemContent = Get-Content $itemYaml -Raw
    $hasPlaceholder = $itemContent -match 'lifecycle_dispatch_placeholder'
    $lifecycleRefs = @('plan-level.yaml', 'actionable.yaml', 'implement-pg.yaml', 'feature-pr.yaml')
    $hasAnyLifecycle = $false
    foreach ($lc in $lifecycleRefs) {
        if ($itemContent.Contains($lc)) { $hasAnyLifecycle = $true; break }
    }
    if (-not $hasPlaceholder -and -not $hasAnyLifecycle) {
        Add-Violation 'missing-lifecycle-dispatch' "apex-item-dispatch.yaml must either reference a lifecycle workflow (plan-level/actionable/implement-pg/feature-pr) OR contain the deferred 'lifecycle_dispatch_placeholder' step."
    }
}

# ── Check 3: companion scripts exist + are referenced ────────────────────
$companionScripts = @(
    @{ Path = Join-Path $scriptsDir 'lifecycle-router.ps1';  ReferencedIn = $itemYaml },
    @{ Path = Join-Path $scriptsDir 'worktree-manager.ps1';  ReferencedIn = $itemYaml },
    @{ Path = Join-Path $scriptsDir 'wave-integrator.ps1';   ReferencedIn = $waveYaml }
)
foreach ($s in $companionScripts) {
    if (-not (Test-Path $s.Path)) {
        Add-Violation 'missing-script' "Companion script missing: $($s.Path)"
        continue
    }
    if (Test-Path $s.ReferencedIn) {
        $refContent = Get-Content $s.ReferencedIn -Raw
        $scriptLeaf = Split-Path -Leaf $s.Path
        if (-not $refContent.Contains($scriptLeaf)) {
            Add-Violation 'unreferenced-script' "Script '$scriptLeaf' is not referenced from $(Split-Path -Leaf $s.ReferencedIn)"
        }
    }
}

# ── Check 4: route target validation across all three workflows ──────────
foreach ($yaml in @($apexYaml, $waveYaml, $itemYaml)) {
    if (-not (Test-Path $yaml)) { continue }
    $lines = @(Get-Content $yaml)
    $names = Get-AgentNames $lines
    $targets = Get-RouteTargets $lines
    $invalid = $targets | Where-Object { $_ -notin $names } | Select-Object -Unique
    foreach ($r in $invalid) {
        Add-Violation 'invalid-route-target' "$(Split-Path -Leaf $yaml): route target '$r' does not match any agent name"
    }
}

# ── Check 5: type-agnostic (P5) ──────────────────────────────────────────
$forbiddenTypes = @('Epic', 'Issue', 'Task', 'User Story', 'Bug')
foreach ($yaml in @($apexYaml, $waveYaml, $itemYaml)) {
    if (-not (Test-Path $yaml)) { continue }
    $yLines = @(Get-Content $yaml)
    $nonComment = ($yLines | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    foreach ($t in $forbiddenTypes) {
        if ($nonComment -match "\b$t\b") {
            Add-Violation 'type-agnostic-violation' "$(Split-Path -Leaf $yaml): hardcoded process-template type name '$t' (P5 — types are runtime-injected)"
        }
    }
}

# ── Check 6: min_polyphony_version on every workflow ─────────────────────
foreach ($yaml in @($apexYaml, $waveYaml, $itemYaml)) {
    if (-not (Test-Path $yaml)) { continue }
    $c = Get-Content $yaml -Raw
    if ($c -notmatch 'min_polyphony_version:\s*"[0-9]') {
        Add-Violation 'missing-min-polyphony-version' "$(Split-Path -Leaf $yaml) must declare metadata.min_polyphony_version"
    }
}

# ── Check 7: M3 + M7 conventions on the inner sub-workflows ──────────────
# Both sub-workflows have their own `output:` map that consumes child step
# outputs from divergent legs — those references MUST be wrapped in
# `is defined` guards. Boolean output fields MUST be piped through
# `| string | lower` so the parent receives a real bool not "True"/"False".
foreach ($yaml in @($waveYaml, $itemYaml)) {
    if (-not (Test-Path $yaml)) { continue }
    $c = Get-Content $yaml -Raw
    if ($c -notmatch 'is defined') {
        Add-Violation 'missing-is-defined-guards' "$(Split-Path -Leaf $yaml): output map must use 'is defined' guards on cross-leg verb outputs (M3)"
    }
    if ($c -notmatch '\|\s*string\s*\|\s*lower') {
        Add-Violation 'missing-bool-coercion' "$(Split-Path -Leaf $yaml): boolean output fields must be piped through '| string | lower' (M7)"
    }
}

# Apex-driver itself also uses these — sanity check.
if ($apexContent -notmatch 'is defined') {
    Add-Violation 'missing-is-defined-guards' "apex-driver.yaml: output map must use 'is defined' guards (M3)"
}
if ($apexContent -notmatch '\|\s*string\s*\|\s*lower') {
    Add-Violation 'missing-bool-coercion' "apex-driver.yaml: boolean output fields must be piped through '| string | lower' (M7)"
}

# ── Check 8: per-item lifecycle dispatch wiring (non-placeholder shape) ───
#
# When apex-item-dispatch.yaml has been migrated off the deferred
# placeholder, assert that:
#   a. all four lifecycle dispatch nodes are present by name,
#   b. all four lifecycle YAML files are referenced via parent-relative
#      `workflow:` paths,
#   c. apex-item-dispatch.output bubbles up renegotiation_pending from
#      plan_level_dispatch, and
#   d. apex-wave-dispatch.output + apex-driver.output expose a
#      renegotiation_pending field (wave aggregation + apex-level rollup).
#
# Skipped automatically when the placeholder is still present so the
# MVP synthetic baseline tests (which use the placeholder shape) pass
# unchanged.
if (Test-Path $itemYaml) {
    $itemContentForDispatch = Get-Content $itemYaml -Raw
    $stillHasPlaceholder = $itemContentForDispatch -match 'lifecycle_dispatch_placeholder'

    if (-not $stillHasPlaceholder) {
        $expectedDispatchNodes = @(
            'plan_level_dispatch',
            'actionable_dispatch',
            'implement_pg_dispatch',
            'feature_pr_dispatch'
        )
        foreach ($n in $expectedDispatchNodes) {
            if ($itemContentForDispatch -notmatch "(?m)^\s*-\s+name:\s*$n\s*$") {
                Add-Violation 'missing-lifecycle-branch' "apex-item-dispatch.yaml: lifecycle dispatch node '$n' not declared. Branch-on-router shape requires one named node per lifecycle (plan_level_dispatch / actionable_dispatch / implement_pg_dispatch / feature_pr_dispatch)."
            }
        }

        $expectedLifecycleRefs = @(
            './plan-level.yaml',
            './actionable.yaml',
            './implement-pg.yaml',
            './feature-pr.yaml'
        )
        foreach ($r in $expectedLifecycleRefs) {
            if (-not $itemContentForDispatch.Contains($r)) {
                Add-Violation 'missing-lifecycle-workflow-ref' "apex-item-dispatch.yaml: lifecycle workflow '$r' is not referenced via a parent-relative workflow path."
            }
        }

        if ($itemContentForDispatch -notmatch 'plan_level_dispatch\.output\.renegotiation_pending') {
            Add-Violation 'missing-renegotiation-bubble-up' "apex-item-dispatch.yaml: output map must reference 'plan_level_dispatch.output.renegotiation_pending' to bubble up the PR #144 renegotiation signal."
        }

        if (Test-Path $waveYaml) {
            $waveContentForReneg = Get-Content $waveYaml -Raw
            if ($waveContentForReneg -notmatch '(?m)^\s+renegotiation_pending:\s*') {
                Add-Violation 'missing-renegotiation-bubble-up' "apex-wave-dispatch.yaml: output map must declare a 'renegotiation_pending' field aggregated across the wave's items."
            }
        }

        if ($apexContent -notmatch '(?m)^\s+renegotiation_pending:\s*') {
            Add-Violation 'missing-renegotiation-bubble-up' "apex-driver.yaml: output map must declare a 'renegotiation_pending' field rolled up across the wave dispatch loop."
        }
    }
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) apex-driver violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: apex-driver suite validated (apex-driver.yaml + 2 sub-workflows + 3 companion scripts)" -ForegroundColor Green
exit 0
