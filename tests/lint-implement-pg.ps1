<#
.SYNOPSIS
    CI lint — validates implement-pg.yaml structural requirements.
.DESCRIPTION
    Parses workflows/implement-pg.yaml and verifies:
    1. Workflow name is 'implement-pg' with correct entry point
    2. Required inputs: work_item_id, pg_number, work_item_ids, branch_name, feature_branch
    3. Required outputs: merged, pr_url
    4. Task loop agents: task_router, coder (Opus 1M), reducer_code, task_reviewer, task_completer
    5. Issue review agents: reducer_issue, issue_reviewer (Opus 1M)
    6. PR creation: pr_submit, pr_platform_router, pr_lifecycle_github, pr_lifecycle_ado
    7. Dependency gate: dependency_check script + dependency_gate human_gate
    8. User acceptance human_gate
    9. Scope closer script
    10. All route targets reference valid agent names or $end
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'implement-pg.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: Workflow name ────────────────────────────────────────────────
if ($content -notmatch 'name:\s*implement-pg') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'implement-pg'"
    }
}

# ── Check 2: Entry point references pg_router ─────────────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($entryPoint -ne 'pg_router') {
        $violations += [PSCustomObject]@{
            Rule   = 'wrong-entry-point'
            Detail = "Entry point should be 'pg_router', got '$entryPoint'"
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
$requiredInputs = @('work_item_id', 'pg_number', 'work_item_ids', 'branch_name', 'feature_branch')
foreach ($input in $requiredInputs) {
    if ($content -notmatch "(?m)^\s+${input}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-input'
            Detail = "Missing required input field: '$input'"
        }
    }
}

# ── Check 4: Required output fields ──────────────────────────────────────
$requiredOutputs = @('merged', 'pr_url')
foreach ($output in $requiredOutputs) {
    if ($content -notmatch "(?m)^\s+${output}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-output'
            Detail = "Missing required output field: '$output'"
        }
    }
}

# ── Check 5: Task loop agents ────────────────────────────────────────────
$taskLoopAgents = @('task_router', 'coder', 'reducer_code', 'task_reviewer', 'task_completer')
foreach ($agent in $taskLoopAgents) {
    if ($content -notmatch "name:\s*$agent") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-task-loop-agent'
            Detail = "Missing task loop agent: '$agent'"
        }
    }
}

# ── Check 6: Coder uses Opus 1M ──────────────────────────────────────────
# Extract the coder agent block and verify its model
$coderBlock = ''
$inCoder = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*coder\s*$') { $inCoder = $true }
    if ($inCoder) { $coderBlock += $line + "`n" }
    if ($inCoder -and $coderBlock.Length -gt 50 -and $line -match '^\s*-\s*name:') { break }
}
if ($coderBlock -and $coderBlock -notmatch 'claude-opus-4-1m') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-coder-model'
        Detail = "Coder agent must use Opus 1M model (claude-opus-4-1m)"
    }
}

# ── Check 7: Issue review agents ─────────────────────────────────────────
$issueAgents = @('reducer_issue', 'issue_reviewer')
foreach ($agent in $issueAgents) {
    if ($content -notmatch "name:\s*$agent") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-issue-review-agent'
            Detail = "Missing issue review agent: '$agent'"
        }
    }
}

# ── Check 8: Issue reviewer uses Opus 1M ──────────────────────────────────
$issueReviewerBlock = ''
$inIssueReviewer = $false
foreach ($line in $lines) {
    if ($line -match 'name:\s*issue_reviewer\s*$') { $inIssueReviewer = $true }
    if ($inIssueReviewer) { $issueReviewerBlock += $line + "`n" }
    if ($inIssueReviewer -and $issueReviewerBlock.Length -gt 50 -and $line -match '^\s*-\s*name:') { break }
}
if ($issueReviewerBlock -and $issueReviewerBlock -notmatch 'claude-opus-4-1m') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-issue-reviewer-model'
        Detail = "Issue reviewer must use Opus 1M model (claude-opus-4-1m) for cross-cutting review"
    }
}

# ── Check 9: PR creation agents ──────────────────────────────────────────
$prAgents = @('pr_submit', 'pr_platform_router')
foreach ($agent in $prAgents) {
    if ($content -notmatch "name:\s*$agent") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-pr-agent'
            Detail = "Missing PR creation agent: '$agent'"
        }
    }
}

# ── Check 10: PR lifecycle sub-workflows ──────────────────────────────────
if ($content -notmatch 'workflow:\s*\./github-pr\.yaml') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-github-pr-subworkflow'
        Detail = "Missing github-pr.yaml sub-workflow reference"
    }
}
if ($content -notmatch 'workflow:\s*\./ado-pr\.yaml') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-ado-pr-subworkflow'
        Detail = "Missing ado-pr.yaml sub-workflow reference"
    }
}

# ── Check 11: Dependency gate ─────────────────────────────────────────────
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

# ── Check 12: Dependency gate options (wait/override/reassign) ────────────
$requiredGateOptions = @('wait', 'override', 'reassign')
foreach ($opt in $requiredGateOptions) {
    if ($content -notmatch "value:\s*$opt") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-gate-option'
            Detail = "Dependency gate missing option value: '$opt'"
        }
    }
}

# ── Check 13: User acceptance gate ────────────────────────────────────────
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

# ── Check 15: Route target validation ─────────────────────────────────────
$agentNames = @()
foreach ($line in $lines) {
    if ($line -match '^\s*-?\s*name:\s*(\S+)') {
        $name = $Matches[1]
        if ($name -ne 'implement-pg') {
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

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) implement-pg.yaml violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: implement-pg.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, $($taskLoopAgents.Count) task-loop agents, issue review, dependency gate, PR sub-workflows)" -ForegroundColor Green
exit 0
