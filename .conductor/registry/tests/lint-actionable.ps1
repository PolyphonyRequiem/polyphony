<#
.SYNOPSIS
    CI lint — validates actionable.yaml structural requirements.
.DESCRIPTION
    Parses workflows/actionable.yaml and verifies:
    1. Workflow name is 'actionable' with entry_point: executor_router
    2. Required inputs: work_item_id, apex_id, executor, platform,
       organization, project, repository, from_ref
    3. Required outputs: satisfied, executor, pr_url, pr_number,
       evidence_branch
    4. Required nodes (both legs): executor_router, ensure_evidence_branch,
       compose_addendum, actionable_agent, open_evidence_pr,
       evidence_floor_check, floor_failed_gate, evidence_reviewer,
       revise_loop_gate, merge_evidence_pr, human_satisfaction_gate,
       workflow_error_gate, workflow_completed, workflow_abandoned
    5. Phase 6 evidence verbs are wired:
       branch ensure-evidence-branch, pr open-evidence-pr,
       agent compose-addendum, pr check-evidence-floor
    6. actionable_agent + evidence_reviewer use opus models
    7. Routed agents declare an output: schema (M2)
    8. All route targets reference valid agent names or $end (M4)
    9. Type-agnostic (no Epic / Issue / Task / User Story / Bug
       hardcoded in the YAML)
    10. Phase 6 deferred-wiring TODO markers are present so they
        cannot be silently dropped before the follow-up PRs land
    11. Phase 6 PR #7 ships the floor — TODO(p6-pr7) marker MUST be
        absent (its presence indicates the wiring was reverted)
    11b. Phase 6 PR #5 ships the addendum — TODO(p6-pr5) marker MUST
         be absent (its presence indicates the wiring was reverted)
    11c. The actionable_agent prompt template injects the
         compose_addendum envelope (skills / mcps / guidance) — Phase 6 PR #5
    13. Verb argument-shape: each polyphony invocation that reads
        `workflow.input.work_item_id` MUST pass it via a named flag
        (`--work-item-id` or `--work-item`), not positionally. Guards
        against AB#3154-class verb-signature drift, where the workflow
        author wrote a positional value but the verb declares all
        parameters as named (ConsoleAppFramework default).
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'actionable.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: Workflow name ────────────────────────────────────────────────
if ($content -notmatch 'name:\s*actionable\s') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'actionable'"
    }
}

# ── Check 2: Entry point references executor_router ──────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $entryPoint = $Matches[1]
    if ($entryPoint -ne 'executor_router') {
        $violations += [PSCustomObject]@{
            Rule   = 'wrong-entry-point'
            Detail = "Entry point should be 'executor_router', got '$entryPoint'"
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
    'apex_id',
    'executor',
    'platform',
    'organization',
    'project',
    'repository',
    'from_ref'
)
foreach ($input in $requiredInputs) {
    if ($content -notmatch "(?m)^\s+${input}:\s*(#.*)?$") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-input'
            Detail = "Missing required input field: '$input'"
        }
    }
}

# ── Check 4: Required output fields ──────────────────────────────────────
$requiredOutputs = @('satisfied', 'executor', 'pr_url', 'pr_number', 'evidence_branch')
foreach ($output in $requiredOutputs) {
    if ($content -notmatch "(?m)^\s+${output}:") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-output'
            Detail = "Missing required output field: '$output'"
        }
    }
}

# ── Check 5: Required nodes (both legs) ──────────────────────────────────
$requiredNodes = @(
    'executor_router',
    'ensure_evidence_branch',
    'compose_addendum',
    'actionable_agent',
    'open_evidence_pr',
    'evidence_floor_check',
    'floor_failed_gate',
    'evidence_reviewer',
    'revise_loop_gate',
    'merge_evidence_pr',
    'human_satisfaction_gate',
    'workflow_error_gate',
    'workflow_completed',
    'workflow_abandoned'
)
foreach ($node in $requiredNodes) {
    if ($content -notmatch "name:\s*$node\s") {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-node'
            Detail = "Missing required node: '$node'"
        }
    }
}

# ── Check 6: Phase 6 evidence verbs are wired ────────────────────────────
$evidenceVerbs = @(
    @{ Verb = 'branch ensure-evidence-branch'; Pattern = '"ensure-evidence-branch"' },
    @{ Verb = 'pr open-evidence-pr';           Pattern = '"open-evidence-pr"' },
    @{ Verb = 'agent compose-addendum';        Pattern = '"compose-addendum"' },
    @{ Verb = 'pr check-evidence-floor';       Pattern = '"check-evidence-floor"' }
)
foreach ($entry in $evidenceVerbs) {
    if (-not $content.Contains($entry.Pattern)) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-evidence-verb'
            Detail = "Workflow must invoke '$($entry.Verb)' (Phase 6 evidence verb)"
        }
    }
}

# ── Check 7: Opus models on agent + reviewer ─────────────────────────────
function Get-AgentBlock {
    param([string]$AgentName, [string[]]$Lines)
    $block = ''
    $inAgent = $false
    foreach ($line in $Lines) {
        if ($line -match "^\s*-\s+name:\s*$AgentName\s*$") { $inAgent = $true; continue }
        if ($inAgent) {
            if ($line -match '^\s*-\s+name:') { break }
            $block += $line + "`n"
        }
    }
    return $block
}

foreach ($agentName in @('actionable_agent', 'evidence_reviewer')) {
    $block = Get-AgentBlock -AgentName $agentName -Lines $lines
    if ($block) {
        if ($block -notmatch '(?im)model:\s*[^\r\n]*opus') {
            $violations += [PSCustomObject]@{
                Rule   = 'wrong-agent-model'
                Detail = "Agent '$agentName' must use an opus-class model (model name must contain 'opus')"
            }
        }
    }
}

# ── Check 8: Output schemas on routed LLM agents ─────────────────────────
foreach ($agentName in @('actionable_agent', 'evidence_reviewer')) {
    $block = Get-AgentBlock -AgentName $agentName -Lines $lines
    if ($block -and $block -notmatch '(?m)^\s+output:\s*$') {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-output-schema'
            Detail = "Agent '$agentName' must declare an output: schema (per conductor-mechanics M2)"
        }
    }
}

# ── Check 9: Route target validation ─────────────────────────────────────
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

# ── Check 10: Type-agnostic (P5) ─────────────────────────────────────────
# The workflow YAML must not hardcode process-template type names. The
# only places these names may legitimately appear are in YAML comments
# whose intent is to NOTE the rule (e.g., "type is runtime-injected; do
# not hardcode type names"). We strip comment lines before scanning.
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

# ── Check 11: Phase 6 deferred-wiring TODO markers present ───────────────
# PR #5 (compose_addendum) and PR #7 (evidence floor check) have shipped
# and removed their respective markers. The remaining marker gates PR #8.
$todoMarkers = @(
    @{ Marker = 'TODO(p6-pr8)'; Detail = 'full evidence_reviewer rubric (PR #8)' }
)
foreach ($entry in $todoMarkers) {
    if (-not $content.Contains($entry.Marker)) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-deferred-wiring-todo'
            Detail = "Missing TODO marker '$($entry.Marker)' — $($entry.Detail). Removing the marker before the follow-up PR lands risks the wiring being silently dropped."
        }
    }
}

# ── Check 11b: Shipped TODOs must be ABSENT ──────────────────────────────
# When a Phase 6 follow-up PR ships, the corresponding TODO marker must
# be removed from the YAML — its presence indicates the wiring was
# reverted or never fully landed. The companion node check above
# enforces the positive side (the new node is present); this check
# enforces the negative side (the placeholder marker is gone).
$shippedTodos = @(
    @{ Marker = 'TODO(p6-pr5)'; ShippedIn = 'PR #5 (compose_addendum + prompt injection)' },
    @{ Marker = 'TODO(p6-pr7)'; ShippedIn = 'PR #7 (evidence floor check)' }
)
foreach ($entry in $shippedTodos) {
    if ($content.Contains($entry.Marker)) {
        $violations += [PSCustomObject]@{
            Rule   = 'shipped-todo-still-present'
            Detail = "Marker '$($entry.Marker)' is still present but its wiring shipped in $($entry.ShippedIn). Remove the marker so the lint can stay accurate."
        }
    }
}

# ── Check 11c: compose_addendum envelope is consumed by actionable_agent ─
# The compose_addendum step's output must be referenced from the
# actionable_agent prompt template. Without this, the wiring is structural
# theater — the verb runs but the agent never sees its envelope.
$actionableAgentBlock = Get-AgentBlock -AgentName 'actionable_agent' -Lines $lines
if ($actionableAgentBlock -and $actionableAgentBlock -notmatch 'compose_addendum\.output\.') {
    $violations += [PSCustomObject]@{
        Rule   = 'compose-addendum-envelope-not-consumed'
        Detail = "actionable_agent prompt template does not reference compose_addendum.output.* — the Phase 6 PR #5 wiring is incomplete (the verb runs but the agent never sees its envelope)."
    }
}

# ── Check 12: min_polyphony_version is declared ──────────────────────────
if ($content -notmatch 'min_polyphony_version:\s*"[0-9]') {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-min-polyphony-version'
        Detail = "Workflow must declare metadata.min_polyphony_version (per polyphony-workflow-author skill)"
    }
}

# ── Check 13: Named-flag invariant for work_item_id passing ──────────────
# AB#3154 — actionable.yaml's ensure_evidence_branch and open_evidence_pr
# steps drifted to passing the work item ID positionally. The CLI verbs
# declare those parameters as named (ConsoleAppFramework default), so the
# positional value is rejected at runtime ("Argument '3138' is not
# recognized."), parking the workflow at workflow_error_gate. The fix is
# always one line — insert the named flag before the templated value.
#
# Detection strategy: capture the step block from `- name: <step>` up to
# the next `- name:` and assert the named flag literal is present
# anywhere in the step. Works for both block-form and inline-form args.
function Get-StepBlock {
    param([string]$StepName, [string[]]$Lines)
    $block = ''
    $inStep = $false
    foreach ($line in $Lines) {
        if ($line -match "^\s*-\s+name:\s*$StepName\s*$") { $inStep = $true; continue }
        if ($inStep -and $line -match '^\s*-\s+name:') { break }
        if ($inStep) { $block += $line + "`n" }
    }
    return $block
}

$workItemFlagSteps = @(
    @{ Step = 'ensure_evidence_branch'; Flag = '--work-item-id'; Verb = 'branch ensure-evidence-branch' },
    @{ Step = 'open_evidence_pr';       Flag = '--work-item';    Verb = 'pr open-evidence-pr'           }
)
foreach ($entry in $workItemFlagSteps) {
    $stepBlock = Get-StepBlock -StepName $entry.Step -Lines $lines
    if (-not $stepBlock) { continue }
    if ($stepBlock -notmatch [regex]::Escape("`"$($entry.Flag)`"")) {
        $violations += [PSCustomObject]@{
            Rule   = 'positional-work-item-arg'
            Detail = "Step '$($entry.Step)' must pass work_item_id via the named flag '$($entry.Flag)' before the templated value (verb '$($entry.Verb)' rejects positional args). See AB#3154."
        }
    }
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) actionable.yaml violation(s)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)]: $($v.Detail)" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: actionable.yaml validated ($($requiredInputs.Count) inputs, $($requiredOutputs.Count) outputs, $($requiredNodes.Count) nodes, evidence verbs wired, deferred-wiring TODOs present, shipped TODOs removed)" -ForegroundColor Green
exit 0
