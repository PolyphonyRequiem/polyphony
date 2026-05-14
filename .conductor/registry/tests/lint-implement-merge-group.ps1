<#
.SYNOPSIS
    CI lint — validates implement-merge-group.yaml structural requirements.
.DESCRIPTION
    Parses workflows/implement-merge-group.yaml and verifies:
    1. Workflow name is 'implement-merge-group' with entry_point: branch_ensure_mg
    2. Required inputs: work_item_id, root_id, pg_number, mg_path,
       work_item_ids, feature_branch
    3. Required outputs: merged, pr_url, pr_number, mg_path
    4. Primary loop agents: primary_router, impl_branch_ensure, coder,
       primary_reviewer, impl_pr_open, impl_pr_merge, primary_completer
    5. Coder + scope_reviewer use an "opus" model (flexible match — versioning
       drift across opus revisions does not break this lint).
    6. Scope review agent: scope_reviewer
    7. MG PR creation + merge: mg_pr_open, mg_pr_merge
    8. Dependency gate: dependency_check script + dependency_gate human_gate
       with options wait, override, reassign
    9. User acceptance human_gate
    10. Scope closer script
    11. MG branch + impl branch verbs use the new Rev 4 grammar verbs:
        branch ensure-mg + branch ensure-impl; pr open-mg-pr,
        pr open-impl-pr, pr merge-mg-pr, pr merge-impl-pr
    12. All route targets reference valid agent names or $end
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'implement-merge-group.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: Workflow name ────────────────────────────────────────────────
if ($content -notmatch 'name:\s*implement-merge-group') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'implement-merge-group'"
    }
}

# ── Check 2: Entry point references branch_ensure_mg ──────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($entryPoint -ne 'branch_ensure_mg') {
        $violations += [PSCustomObject]@{
            Rule   = 'wrong-entry-point'
            Detail = "Entry point should be 'branch_ensure_mg', got '$entryPoint'"
        }
    }
    if ($content -notmatch "name:\s*$entryPoint") {
        $violations += [PSCustomObject]@{
            Rule   = 'invalid-entry-point'
            Detail = "Entry point '$entryPoint' does not match any agent name"
        }
    }
} else {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-entry-point'
        Detail = "No entry_point field found"
    }
}

# ── Check 3: Required input fields ───────────────────────────────────────
$requiredInputs = @(
    'work_item_id',
    'root_id',
    'pg_number',
    'mg_path',
    'work_item_ids',
    'feature_branch'
)
foreach ($input in $requiredInputs) {
    if ($content -notmatch "(?m)^\s+${input}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-input'
            Detail = "Missing required input field: '$input'"
        }
    }
}

# ── Check 4: Required output fields ──────────────────────────────────────
$requiredOutputs = @('merged', 'pr_url', 'pr_number', 'mg_path')
foreach ($output in $requiredOutputs) {
    if ($content -notmatch "(?m)^\s+${output}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-output'
            Detail = "Missing required output field: '$output'"
        }
    }
}

# ── Check 5: Primary loop agents ─────────────────────────────────────────
$primaryLoopAgents = @(
    'primary_router',
    'impl_branch_ensure',
    'coder',
    'primary_reviewer',
    'impl_pr_open',
    'impl_pr_merge',
    'primary_completer'
)
foreach ($agent in $primaryLoopAgents) {
    if ($content -notmatch "name:\s*$agent") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-primary-loop-agent'
            Detail = "Missing primary loop agent: '$agent'"
        }
    }
}

# ── Check 6: Coder uses an Opus model ────────────────────────────────────
# Flexible "contains opus" match — versioning drift across opus revisions
# (4.5 / 4.6 / 4.7 / 4.7-1m / 4.7-high / 4.7-xhigh / future 5.x) should
# not break this lint. Pinning a specific opus version is a known
# fragility — see commit history for the long tail of model-pin bumps.
# We codify the principle ("must be an opus-class model") instead.
$coderBlock = ''
$inCoder = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*coder\s*$') { $inCoder = $true }
    if ($inCoder) { $coderBlock += $line + "`n" }
    if ($inCoder -and $coderBlock.Length -gt 50 -and $line -match '^\s*-\s*name:') { break }
}
if ($coderBlock) {
    if ($coderBlock -notmatch '(?im)model:\s*[^\r\n]*opus') {
        $violations += [PSCustomObject]@{
            Rule   = 'wrong-coder-model'
            Detail = "Coder agent must use an opus-class model (model name must contain 'opus')"
        }
    }
}

# ── Check 7: Scope review agent present ──────────────────────────────────
$scopeAgents = @('scope_reviewer')
foreach ($agent in $scopeAgents) {
    if ($content -notmatch "name:\s*$agent") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-scope-review-agent'
            Detail = "Missing scope review agent: '$agent'"
        }
    }
}

# ── Check 8: Scope reviewer uses an Opus model ───────────────────────────
$scopeReviewerBlock = ''
$inScopeReviewer = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*scope_reviewer\s*$') { $inScopeReviewer = $true }
    if ($inScopeReviewer) { $scopeReviewerBlock += $line + "`n" }
    if ($inScopeReviewer -and $scopeReviewerBlock.Length -gt 50 -and $line -match '^\s*-\s*name:') { break }
}
if ($scopeReviewerBlock) {
    if ($scopeReviewerBlock -notmatch '(?im)model:\s*[^\r\n]*opus') {
        $violations += [PSCustomObject]@{
            Rule   = 'wrong-scope-reviewer-model'
            Detail = "Scope reviewer must use an opus-class model (model name must contain 'opus') for cross-cutting review"
        }
    }
}

# ── Check 9: MG PR open + merge agents ───────────────────────────────────
$prAgents = @('mg_pr_open', 'mg_pr_merge')
foreach ($agent in $prAgents) {
    if ($content -notmatch "name:\s*$agent") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-pr-agent'
            Detail = "Missing MG PR agent: '$agent'"
        }
    }
}

# ── Check 10: Rev 4 grammar verbs are wired ──────────────────────────────
# implement-merge-group adopts the Rev 4 branch grammar verbs. If these
# go missing, the workflow has degenerated and the Rev 4 ADR's purpose
# is defeated.
$grammarVerbs = @(
    @{ Verb = 'branch ensure-mg';     Pattern = '"ensure-mg"' },
    @{ Verb = 'branch ensure-impl';   Pattern = '"ensure-impl"' },
    @{ Verb = 'pr open-mg-pr';        Pattern = '"open-mg-pr"' },
    @{ Verb = 'pr open-impl-pr';      Pattern = '"open-impl-pr"' },
    @{ Verb = 'pr merge-mg-pr';       Pattern = '"merge-mg-pr"' },
    @{ Verb = 'pr merge-impl-pr';     Pattern = '"merge-impl-pr"' }
)
foreach ($entry in $grammarVerbs) {
    if (-not $content.Contains($entry.Pattern)) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-rev4-grammar-verb'
            Detail = "Workflow must invoke '$($entry.Verb)' (Rev 4 branch model verb)"
        }
    }
}

# ── Check 11: Dependency gate ────────────────────────────────────────────
if ($content -notmatch 'name:\s*dependency_check') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-dependency-check'
        Detail = "Missing dependency_check script node"
    }
}
if ($content -notmatch 'name:\s*dependency_gate') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-dependency-gate'
        Detail = "Missing dependency_gate human gate"
    }
}

# ── Check 12: Dependency gate options (wait/override/reassign) ───────────
$requiredGateOptions = @('wait', 'override', 'reassign')
foreach ($opt in $requiredGateOptions) {
    if ($content -notmatch "value:\s*$opt") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-gate-option'
            Detail = "Dependency gate missing option value: '$opt'"
        }
    }
}

# ── Check 13: User acceptance gate ───────────────────────────────────────
if ($content -notmatch 'name:\s*user_acceptance') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-user-acceptance'
        Detail = "Missing user_acceptance human gate"
    }
}

# ── Check 14: Scope closer ───────────────────────────────────────────────
if ($content -notmatch 'name:\s*scope_closer') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-scope-closer'
        Detail = "Missing scope_closer script node"
    }
}

# ── Check 15: Output schemas on routed agents ────────────────────────────
# Per conductor-mechanics M2, any LLM agent whose output is consumed by
# a route MUST have an `output:` schema. Otherwise the conductor packs
# the entire response into output.result and the routes silently break.
$schemaAgents = @('primary_reviewer', 'scope_reviewer')
foreach ($agentName in $schemaAgents) {
    $block = ''
    $inAgent = $false
    foreach ($line in $lines) {
        if ($line -match "name:\s*$agentName\s*$") { $inAgent = $true; continue }
        if ($inAgent) {
            if ($line -match '^\s*-\s*name:') { break }
            $block += $line + "`n"
        }
    }
    if ($block -and $block -notmatch '(?m)^\s+output:\s*$') {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-output-schema'
            Detail = "Agent '$agentName' must declare an output: schema (per conductor-mechanics M2 — routes read its output fields)"
        }
    }
}

# ── Check 16: max_iterations is high enough for the task loop ────────────
# The MG task loop is wide because each task has
# seven nodes (ensure-impl → coder → reviewer → impl-pr-open → impl-pr-merge
# → completer → router). Ten tasks ~= 70 iterations baseline, doubled by
# changes-requested loops. Anything < 200 risks hitting the cap on
# medium-sized MGs.
if ($content -match '(?m)^\s*max_iterations:\s*(\d+)') {
    $maxIter = [int]$Matches[1]
    if ($maxIter -lt 200) {
        $violations += [PSCustomObject]@{
            Rule   = 'max-iterations-too-low'
            Detail = "max_iterations is $maxIter; should be >= 200 to accommodate the seven-node-per-task loop with changes-requested cycles"
        }
    }
}

# ── Check 17: Route target validation ────────────────────────────────────
$agentNames = @()
foreach ($line in $lines) {
    if ($line -match '^\s*-?\s*name:\s*(\S+)') {
        $name = $Matches[1]
        if ($name -ne 'implement-merge-group') {
            $agentNames += $name
        }
    }
}

$routeTargets = @()
foreach ($line in $lines) {
    if ($line -match 'to:\s*(\S+)') {
        $target = $Matches[1]
        if ($target -ne '$end') {
            $routeTargets += $target
        }
    }
    if ($line -match 'route:\s*(\S+)') {
        $target = $Matches[1]
        if ($target -ne '$end') {
            $routeTargets += $target
        }
    }
}

$invalidRoutes = $routeTargets | Where-Object { $_ -notin $agentNames } | Select-Object -Unique
foreach ($route in $invalidRoutes) {
    $violations += [PSCustomObject]@{
        Rule   = 'invalid-route-target'
        Detail = "Route target '$route' does not match any agent name"
    }
}

# ── Check 18: Scope-revise cap structure (AB#3125) ───────────────────────
# The scope_reviewer → primary_router revise loop must be capped to
# prevent infinite loops on structurally-broken MGs (empty branch,
# zero implementable items). The required nodes mirror the canonical
# `revise_counter`/`revise_cap_gate` pattern from plan-level.yaml and
# the `review_counter`/`pr_fix_exhausted_gate` pattern from
# github-pr.yaml.
#
# Required structure:
#   1. scope_revise_counter script node (with cap_reached output field).
#   2. scope_revise_cap_gate human_gate node with options re_loop /
#      force_accept / abort.
#   3. scope_revise_reset script node.
#   4. scope_reviewer routes the `changes_requested` verdict (and its
#      catch-all) to scope_revise_counter, NOT directly to primary_router.
$reviseNodes = @('scope_revise_counter', 'scope_revise_cap_gate', 'scope_revise_reset')
foreach ($node in $reviseNodes) {
    if ($content -notmatch "name:\s*$node\b") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-scope-revise-node'
            Detail = "Missing $node — required to cap the scope_reviewer revise loop (AB#3125)"
        }
    }
}

# scope_revise_cap_gate options: re_loop / force_accept / abort
$capGateBlock = ''
$inCapGate = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*scope_revise_cap_gate\s*$') { $inCapGate = $true; continue }
    if ($inCapGate) {
        if ($line -match '^\s*-\s*name:') { break }
        $capGateBlock += $line + "`n"
    }
}
if ($capGateBlock) {
    foreach ($opt in @('re_loop', 'force_accept', 'abort')) {
        if ($capGateBlock -notmatch "value:\s*$opt\b") {
            $violations += [PSCustomObject]@{
                Rule   = 'missing-scope-revise-gate-option'
                Detail = "scope_revise_cap_gate missing option value: '$opt'"
            }
        }
    }
}

# scope_revise_counter must declare cap_reached output (routes read it per M2)
$counterBlock = ''
$inCounter = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*scope_revise_counter\s*$') { $inCounter = $true; continue }
    if ($inCounter) {
        if ($line -match '^\s*-\s*name:') { break }
        $counterBlock += $line + "`n"
    }
}
if ($counterBlock -and $counterBlock -notmatch 'scope_revise_counter\.output\.cap_reached') {
    $violations += [PSCustomObject]@{
        Rule   = 'scope-revise-counter-missing-cap-reached-route'
        Detail = "scope_revise_counter must route based on output.cap_reached (per conductor-mechanics M2)"
    }
}

# scope_reviewer must route changes_requested through scope_revise_counter
$scopeReviewerBlock = ''
$inReviewer = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*scope_reviewer\s*$') { $inReviewer = $true; continue }
    if ($inReviewer) {
        if ($line -match '^\s*-\s*name:') { break }
        $scopeReviewerBlock += $line + "`n"
    }
}
if ($scopeReviewerBlock -and $scopeReviewerBlock -notmatch 'to:\s*scope_revise_counter') {
    $violations += [PSCustomObject]@{
        Rule   = 'scope-reviewer-bypasses-cap'
        Detail = "scope_reviewer must route to scope_revise_counter (changes_requested + catch-all), not directly to primary_router (AB#3125)"
    }
}

# ── Check 19: Scope empty-MG triage + auto-approve (AB#3166) ─────────────
# The zero-commit MG direction-asymmetry fix requires:
#   1. scope_empty_mg_triage script node (deterministic triage)
#   2. scope_auto_approve script node (deterministic approval for Case C)
#   3. scope_guidance_loader routes to scope_empty_mg_triage (not directly
#      to scope_reviewer)
$triageNodes = @('scope_empty_mg_triage', 'scope_auto_approve')
foreach ($node in $triageNodes) {
    if ($content -notmatch "name:\s*$node\b") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-scope-triage-node'
            Detail = "Missing $node — required to disambiguate zero-commit MGs (AB#3166)"
        }
    }
}

# scope_guidance_loader must route to scope_empty_mg_triage (not scope_reviewer)
$guidanceLoaderBlock = ''
$inLoader = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*scope_guidance_loader\s*$') { $inLoader = $true; continue }
    if ($inLoader) {
        if ($line -match '^\s*-\s*name:') { break }
        $guidanceLoaderBlock += $line + "`n"
    }
}
if ($guidanceLoaderBlock -and $guidanceLoaderBlock -notmatch 'to:\s*scope_empty_mg_triage') {
    $violations += [PSCustomObject]@{
        Rule   = 'scope-guidance-loader-bypasses-triage'
        Detail = "scope_guidance_loader must route to scope_empty_mg_triage, not directly to scope_reviewer (AB#3166)"
    }
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) implement-merge-group.yaml violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: implement-merge-group.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, $($primaryLoopAgents.Count) primary-loop agents, scope review, dependency gate, MG PR open+merge, Rev 4 grammar verbs, AB#3166 triage)" -ForegroundColor Green
exit 0
