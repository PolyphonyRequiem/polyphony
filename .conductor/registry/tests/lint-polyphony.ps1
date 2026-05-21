<#
.SYNOPSIS
    CI lint — validates polyphony.yaml + companion sub-workflows
    structural requirements.

.DESCRIPTION
    Parses the three Phase 7 polyphony workflow YAMLs:
      - polyphony.yaml         (the keystone)
      - root-batch-dispatch.yaml  (per-batch inner sub-workflow)
      - root-item-dispatch.yaml  (per-item innermost sub-workflow)

    Plus the three companion scripts under .conductor/registry/scripts/.

    Verifies nine structural requirement classes:

      1. polyphony.yaml has `name: polyphony`, `entry_point:
         preflight_sync`, and the four-input contract (root_id,
         intent, platform — root_id and intent are required by spec;
         platform is optional with a default).

      2. The four lifecycle workflows the polyphony dispatches into
         are all referenced (or explicitly deferred via the
         placeholder).

      3. The three companion scripts exist on disk and are referenced
         from the workflow YAMLs.

      4. All route targets resolve to declared agents or `$end` (M4).

      5. Type-agnostic (P5 — no Epic / Issue / Task / User Story / Bug
         hardcoded outside of comment-only contexts).

      6. metadata.min_polyphony_version is declared on every workflow.

      7. Both inner sub-workflows (batch + item) declare `is defined`
         guards on every cross-leg verb output reference (M3) and
         pipe booleans through `| string | lower` in their workflow
         output map (M7).

      8. Per-item lifecycle dispatch wiring (the "real" — non-placeholder
         — shape produced by the Phase 7 follow-up):
           a. root-item-dispatch declares all four named branches
              (plan_level, actionable,
              implement_merge_group, feature_pr).
           b. root-item-dispatch references all four lifecycle workflow
              files via parent-relative paths.
           c. The renegotiation bubble-up is wired end-to-end:
              root-item-dispatch.output references plan_level's
              renegotiation_pending; root-batch-dispatch.output exposes a
              batch-aggregated renegotiation_pending; polyphony.output
              exposes a top-level renegotiation_pending.
         Skipped when root-item-dispatch still carries the original
         `lifecycle_dispatch_placeholder` step (MVP deferred shape).

      9. State-mutation durability (AB#3129, sister-bug to AB#3126):
         every PowerShell `script` node that calls `twig state` must
         declare the fail-fast prologue (`$ErrorActionPreference = 'Stop'`
         + `$PSNativeCommandUseErrorActionPreference = $true`) and follow
         the `twig state` invocation with a `twig sync` so the staged
         transition is flushed to ADO before the script returns.

    Exits 0 if clean, 1 if violations are found.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$workflowsDir = Join-Path $repoRoot 'workflows'
$scriptsDir = Join-Path $repoRoot 'scripts'

$rootYaml = Join-Path $workflowsDir 'polyphony.yaml'
$batchYaml = Join-Path $workflowsDir 'root-batch-dispatch.yaml'
$itemYaml = Join-Path $workflowsDir 'root-item-dispatch.yaml'

if (-not (Test-Path $rootYaml)) {
    Write-Host "SKIP: $rootYaml not found" -ForegroundColor Yellow
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

# ── Check 1: polyphony.yaml — name + entry_point + four-input contract ──
$rootContent = Get-Content $rootYaml -Raw
$rootLines = @(Get-Content $rootYaml)

if ($rootContent -notmatch '(?m)^\s+name:\s*polyphony\s') {
    Add-Violation 'wrong-workflow-name' "polyphony.yaml workflow name must be 'polyphony'"
}

if ($rootContent -match 'entry_point:\s*(\S+)') {
    $entry = $Matches[1]
    if ($entry -ne 'preflight_sync') {
        Add-Violation 'wrong-entry-point' "polyphony entry_point should be 'preflight_sync', got '$entry'"
    }
} else {
    Add-Violation 'missing-entry-point' "polyphony.yaml has no entry_point field"
}

# Required inputs (root_id, intent are the spec contract; platform is optional).
$requiredApexInputs = @('root_id', 'intent')
foreach ($i in $requiredApexInputs) {
    if ($rootContent -notmatch "(?m)^\s+${i}:\s*(#.*)?$") {
        Add-Violation 'missing-input' "polyphony.yaml is missing required input '$i'"
    }
}

# ── Check 2: lifecycle workflows referenced (or deferred via placeholder) ──
# In the MVP, the per-item sub-workflow has a `lifecycle_dispatch_placeholder`
# step that explicitly notes the deferred lifecycle dispatch. The check is
# satisfied by either the placeholder OR explicit references to the four
# lifecycle workflows.
if (-not (Test-Path $itemYaml)) {
    Add-Violation 'missing-sub-workflow' "root-item-dispatch.yaml not found at $itemYaml"
} else {
    $itemContent = Get-Content $itemYaml -Raw
    $hasPlaceholder = $itemContent -match 'lifecycle_dispatch_placeholder'
    $lifecycleRefs = @('plan-level.yaml', 'actionable.yaml', 'implement-merge-group.yaml', 'feature-pr.yaml')
    $hasAnyLifecycle = $false
    foreach ($lc in $lifecycleRefs) {
        if ($itemContent.Contains($lc)) { $hasAnyLifecycle = $true; break }
    }
    if (-not $hasPlaceholder -and -not $hasAnyLifecycle) {
        Add-Violation 'missing-lifecycle-dispatch' "root-item-dispatch.yaml must either reference a lifecycle workflow (plan-level/actionable/implement-merge-group/feature-pr) OR contain the deferred 'lifecycle_dispatch_placeholder' step."
    }
}

# ── Check 3: companion scripts exist + are referenced ────────────────────
$companionScripts = @(
    @{ Path = Join-Path $scriptsDir 'lifecycle-router.ps1';  ReferencedIn = $itemYaml },
    @{ Path = Join-Path $scriptsDir 'worktree-manager.ps1';  ReferencedIn = $itemYaml },
    @{ Path = Join-Path $scriptsDir 'batch-integrator.ps1';   ReferencedIn = $batchYaml }
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
foreach ($yaml in @($rootYaml, $batchYaml, $itemYaml)) {
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
foreach ($yaml in @($rootYaml, $batchYaml, $itemYaml)) {
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
foreach ($yaml in @($rootYaml, $batchYaml, $itemYaml)) {
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
foreach ($yaml in @($batchYaml, $itemYaml)) {
    if (-not (Test-Path $yaml)) { continue }
    $c = Get-Content $yaml -Raw
    if ($c -notmatch 'is defined') {
        Add-Violation 'missing-is-defined-guards' "$(Split-Path -Leaf $yaml): output map must use 'is defined' guards on cross-leg verb outputs (M3)"
    }
    if ($c -notmatch '\|\s*string\s*\|\s*lower') {
        Add-Violation 'missing-bool-coercion' "$(Split-Path -Leaf $yaml): boolean output fields must be piped through '| string | lower' (M7)"
    }
}

# polyphony itself also uses these — sanity check.
if ($rootContent -notmatch 'is defined') {
    Add-Violation 'missing-is-defined-guards' "polyphony.yaml: output map must use 'is defined' guards (M3)"
}
if ($rootContent -notmatch '\|\s*string\s*\|\s*lower') {
    Add-Violation 'missing-bool-coercion' "polyphony.yaml: boolean output fields must be piped through '| string | lower' (M7)"
}

# ── Check 8: per-item lifecycle dispatch wiring (non-placeholder shape) ───
#
# When root-item-dispatch.yaml has been migrated off the deferred
# placeholder, assert that:
#   a. all four lifecycle dispatch nodes are present by name,
#   b. all four lifecycle YAML files are referenced via parent-relative
#      `workflow:` paths,
#   c. root-item-dispatch.output bubbles up renegotiation_pending from
#      plan_level, and
#   d. root-batch-dispatch.output + polyphony.output expose a
#      renegotiation_pending field (batch aggregation + root-level rollup).
#
# Skipped automatically when the placeholder is still present so the
# MVP synthetic baseline tests (which use the placeholder shape) pass
# unchanged.
if (Test-Path $itemYaml) {
    $itemContentForDispatch = Get-Content $itemYaml -Raw
    $stillHasPlaceholder = $itemContentForDispatch -match 'lifecycle_dispatch_placeholder'

    if (-not $stillHasPlaceholder) {
        $expectedDispatchNodes = @(
            'plan_level',
            'actionable',
            'implement_merge_group',
            'feature_pr'
        )
        foreach ($n in $expectedDispatchNodes) {
            if ($itemContentForDispatch -notmatch "(?m)^\s*-\s+name:\s*$n\s*$") {
                Add-Violation 'missing-lifecycle-branch' "root-item-dispatch.yaml: lifecycle dispatch node '$n' not declared. Branch-on-router shape requires one named node per lifecycle (plan_level / actionable / implement_merge_group / feature_pr)."
            }
        }

        $expectedLifecycleRefs = @(
            './plan-level.yaml',
            './actionable.yaml',
            './implement-merge-group.yaml',
            './feature-pr.yaml'
        )
        foreach ($r in $expectedLifecycleRefs) {
            if (-not $itemContentForDispatch.Contains($r)) {
                Add-Violation 'missing-lifecycle-workflow-ref' "root-item-dispatch.yaml: lifecycle workflow '$r' is not referenced via a parent-relative workflow path."
            }
        }

        if ($itemContentForDispatch -notmatch 'plan_level\.output\.renegotiation_pending') {
            Add-Violation 'missing-renegotiation-bubble-up' "root-item-dispatch.yaml: output map must reference 'plan_level.output.renegotiation_pending' to bubble up the PR #144 renegotiation signal."
        }

        if (Test-Path $batchYaml) {
            $batchContentForReneg = Get-Content $batchYaml -Raw
            if ($batchContentForReneg -notmatch '(?m)^\s+renegotiation_pending:\s*') {
                Add-Violation 'missing-renegotiation-bubble-up' "root-batch-dispatch.yaml: output map must declare a 'renegotiation_pending' field aggregated across the batch's items."
            }
        }

        if ($rootContent -notmatch '(?m)^\s+renegotiation_pending:\s*') {
            Add-Violation 'missing-renegotiation-bubble-up' "polyphony.yaml: output map must declare a 'renegotiation_pending' field rolled up across the batch dispatch loop."
        }
    }
}

# ── Check 9: state-mutation durability (AB#3126 / AB#3129 class-of-bug) ───
#
# Every PowerShell `script` node in the polyphony suite that calls
# `twig state` MUST:
#   (a) declare the fail-fast prologue so a failed `twig sync` halts the
#       script rather than silently propagating stale state:
#         $ErrorActionPreference = 'Stop'
#         $PSNativeCommandUseErrorActionPreference = $true
#   (b) follow the `twig state` invocation with a `twig sync` that flushes
#       the staged transition back to ADO before the script returns.
#
# Without (b), the transition sits in twig's local pending queue and is
# never pushed; subsequent processes reading ADO see stale state. This
# is the AB#3126 root cause; AB#3129 was the same shape in polyphony
# > close_mark_satisfied and root-item-dispatch > satisfied.
# See `.github/skills/polyphony-cli-developer/SKILL.md` >
# "State-mutation durability" for the full convention.
function Test-StateMutationDurability {
    param(
        [string]$Yaml
    )
    if (-not (Test-Path $Yaml)) { return }
    $leaf = Split-Path -Leaf $Yaml
    $content = Get-Content $Yaml -Raw

    # Slice each `- name: <foo>\n    type: script` block out of the YAML
    # so we can examine its embedded pwsh body in isolation.
    $blockPattern = '(?ms)^  - name:\s*(\S+)\s*\r?\n    type:\s*script\b.*?(?=^  - name: |\Z)'
    foreach ($m in [regex]::Matches($content, $blockPattern)) {
        $name = $m.Groups[1].Value
        $body = $m.Value

        # Only flag blocks that actually invoke `twig state` (the mutation
        # we care about). `twig note` is also a stager but no current
        # workflow uses it without a downstream sync.
        if ($body -notmatch '(?m)^\s*twig\s+state\b') { continue }

        # (a) fail-fast prologue
        if ($body -notmatch [regex]::Escape("`$ErrorActionPreference = 'Stop'")) {
            Add-Violation 'missing-fail-fast-prologue' "${leaf}: script '$name' calls 'twig state' but does not declare `$ErrorActionPreference = 'Stop' (AB#3129 — required so a failed 'twig sync' halts the script rather than silently propagating stale state)."
        }
        if ($body -notmatch [regex]::Escape("`$PSNativeCommandUseErrorActionPreference = `$true")) {
            Add-Violation 'missing-fail-fast-prologue' "${leaf}: script '$name' calls 'twig state' but does not declare `$PSNativeCommandUseErrorActionPreference = `$true (AB#3129 — required so native-command (twig) failures honor `$ErrorActionPreference)."
        }

        # (b) every `twig state` must be followed by a `twig sync` later in
        # the body. A simple ordinal check is sufficient — the script bodies
        # are short and a missing post-state sync is the bug we want to
        # catch, not subtle ordering games.
        $stateMatches = [regex]::Matches($body, '(?m)^\s*twig\s+state\b')
        $syncMatches  = [regex]::Matches($body, '(?m)^\s*twig\s+sync\b')
        $lastStateIdx = ($stateMatches | ForEach-Object { $_.Index } | Measure-Object -Maximum).Maximum
        $hasPostStateSync = $false
        foreach ($s in $syncMatches) {
            if ($s.Index -gt $lastStateIdx) { $hasPostStateSync = $true; break }
        }
        if (-not $hasPostStateSync) {
            Add-Violation 'missing-post-state-sync' "${leaf}: script '$name' calls 'twig state' but does not follow it with 'twig sync' (AB#3129 — staged transition must be flushed to ADO before the script returns; see polyphony-cli-developer SKILL.md > 'State-mutation durability')."
        }
    }
}

foreach ($yaml in @($rootYaml, $batchYaml, $itemYaml)) {
    Test-StateMutationDurability -Yaml $yaml
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) polyphony violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: polyphony suite validated (polyphony.yaml + 2 sub-workflows + 3 companion scripts)" -ForegroundColor Green
exit 0
