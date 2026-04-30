<#
.SYNOPSIS
    Deterministic state detector for the twig SDLC apex workflow.
    Inspects ADO work item state, plan artifacts, and git state to determine
    the current lifecycle phase and validate user intent.

.DESCRIPTION
    Thin wrapper around Polyphony CLI commands for phase detection and routing.
    Outputs JSON with: work_item_id, work_item_type, work_item_state, intent,
    phase, plan info, seed status, and error/conflict flags.

.PARAMETER WorkItemId
    ADO work item ID to inspect.

.PARAMETER Intent
    User intent: new, redo, or resume (default: resume).

.PARAMETER PlanPath
    Explicit plan file override for debugging/recovery.
#>
param(
    [Parameter(Mandatory = $true)]
    [int]$WorkItemId,

    [ValidateSet('new', 'redo', 'resume')]
    [string]$Intent = 'resume',

    [string]$PlanPath = ''
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/ado-helpers.ps1"

try {
    # ── Derive ADO workspace from twig config (#2651) ────────────────────────
    $_adoOrg = Get-AdoOrg
    $_adoProject = Get-AdoProject
    $_adoWorkspace = Get-AdoWorkspace

    # ── Step 0: Sync local cache from ADO ────────────────────────────────────
    # The local .twig SQLite cache may be stale. Force a refresh before
    # reading any state to prevent routing on stale data.
    twig sync --output json 2>$null | Out-Null

    # ── Plan discovery (#2633) ────────────────────────────────────────────────
    # Priority chain: explicit override → artifact link (TODO #2059) → filesystem fallback.
    $planStatus = 'none'
    $planSource = 'none'
    $errorMsg   = ''

    # Priority 1: Explicit override — if -PlanPath provided and file exists
    if ($PlanPath -and (Test-Path $PlanPath)) {
        $planStatus = 'complete'
        $planSource = 'explicit_override'
        $PlanPath   = (Resolve-Path $PlanPath).Path
    }
    # Priority 2: Artifact link (TODO #2059 — skip to Priority 3)
    # Priority 3: Filesystem fallback — scan docs/projects/*.plan.md
    elseif (-not $PlanPath) {
        $planFiles = @(Get-ChildItem -Path 'docs/projects/*.plan.md' -ErrorAction SilentlyContinue)
        $matchedPaths = @()

        foreach ($file in $planFiles) {
            $content = Get-Content $file.FullName -Raw
            $matched = $false

            # YAML frontmatter: extract work_item_id
            if ($content -match '(?s)^---\s*\n(.*?)\n---') {
                $frontmatter = $Matches[1]
                if ($frontmatter -match 'work_item_id:\s*(\d+)') {
                    if ([int]$Matches[1] -eq $WorkItemId) {
                        $matched = $true
                    }
                }
            }

            # Legacy table metadata: | **Work Item** | #<id>
            if (-not $matched -and $content -match '\|\s*\*{0,2}Work\s*Item\*{0,2}\s*\|\s*#(\d+)') {
                if ([int]$Matches[1] -eq $WorkItemId) {
                    $matched = $true
                }
            }
            # Legacy table metadata: | **<any label>** | #<id>
            if (-not $matched -and $content -match '\|\s*\*{0,2}[^|*]+\*{0,2}\s*\|\s*#(\d+)') {
                if ([int]$Matches[1] -eq $WorkItemId) {
                    $matched = $true
                }
            }

            if ($matched) {
                $matchedPaths += $file.FullName
            }
        }

        if ($matchedPaths.Count -eq 1) {
            $planStatus = 'complete'
            $planSource = 'filesystem_fallback'
            $PlanPath   = $matchedPaths[0]
        }
        elseif ($matchedPaths.Count -gt 1) {
            $planStatus = 'ambiguous'
        }
    }
    else {
        # PlanPath provided but file doesn't exist — no fallback
        $PlanPath = ''
    }

    $hasPlan = $planStatus -in @('complete')

    # ── Set active work item (#2632) ──────────────────────────────────────────
    twig set $WorkItemId --output json 2>$null | Out-Null

    # ── Polyphony route (#2632) ───────────────────────────────────────────────
    # Replaces ~100 lines of manual state detection with a single call.
    # RouteResult provides: phase, action, message, workspace_hint.
    $routeJson = polyphony route --work-item $WorkItemId 2>$null
    $routeResult = $routeJson | ConvertFrom-Json

    $phase = $routeResult.phase
    $implementationStatus = switch ($routeResult.action) {
        'plan'      { 'not_started' }
        'seed'      { 'not_started' }
        'implement' { 'not_started' }
        'monitor'   { 'in_progress' }
        'close'     { 'done' }
        'none' {
            if ($routeResult.phase -eq 'done') { 'done' }
            elseif ($routeResult.phase -eq 'removed') { 'removed' }
            else { 'not_started' }
        }
        default     { $routeResult.action }
    }

    $workspaceHint = if ($routeResult.workspace_hint) {
        $routeResult.workspace_hint | ConvertTo-Json -Compress
    } else { '{}' }

    # ── Read work item metadata from twig tree (#2632) ────────────────────────
    # twig tree provides type, state, title, and child hierarchy
    # that polyphony route doesn't return.
    $treeJson = twig tree --depth 2 --output json 2>$null
    $tree = $treeJson | ConvertFrom-Json

    $workItemType  = if ($tree.type)  { $tree.type }  else { '' }
    $workItemState = if ($tree.state) { $tree.state } else { '' }
    $workItemTitle = if ($tree.title) { $tree.title } else { '' }

    # ── Children analysis (#2632) ─────────────────────────────────────────────
    $children = @()
    if ($tree.children) { $children = @($tree.children) }
    $childCount = $children.Count
    $doneCount  = @($children | Where-Object { $_.state -eq 'Done' }).Count
    $doingCount = @($children | Where-Object { $_.state -eq 'Doing' }).Count
    $todoCount  = @($children | Where-Object { $_.state -eq 'To Do' }).Count

    $hasSeededChildren = $childCount -gt 0

    $anyChildMissingTasks = $false
    foreach ($child in $children) {
        if ($child.state -ne 'Done') {
            $grandchildren = @()
            if ($child.children) { $grandchildren = @($child.children) }
            if ($grandchildren.Count -eq 0) {
                $anyChildMissingTasks = $true
                break
            }
        }
    }

    $seedStatus = if ($childCount -eq 0) { 'unseeded' }
                  elseif ($anyChildMissingTasks) { 'partial' }
                  else { 'seeded' }

    # ── Unmerged branches check (#2632) ───────────────────────────────────────
    # Detect unmerged feature branches for implementation status.
    # Repo slug derived at runtime from git remote — no hardcoded values.
    $remoteUrl = git remote get-url origin 2>$null
    $repoSlug = ($remoteUrl -replace '.*github\.com[:/]' -replace '\.git$').Trim()

    if ($repoSlug -and $routeResult.workspace_hint) {
        $featureBranch = $routeResult.workspace_hint.feature_branch
        if ($featureBranch) {
            $remoteBranches = @(git ls-remote --heads origin "${featureBranch}*" 2>$null)
            if ($remoteBranches.Count -gt 0) {
                $openPrsJson = gh pr list --repo $repoSlug --head $featureBranch --state open --json number 2>$null
                if ($openPrsJson) {
                    $openPrs = $openPrsJson | ConvertFrom-Json
                    if ($openPrs.Count -gt 0 -and $implementationStatus -ne 'done') {
                        $implementationStatus = 'in_progress'
                    }
                }
            }
        }
    }

    # ── Step 1: State transition via polyphony validate (#2634) ───────────
    # Replace the former type-name guard with a type-agnostic
    # validation call. polyphony validate checks the process config to
    # determine if begin_planning is a valid event for this work item type.
    $validateJson = polyphony validate --work-item $WorkItemId --event begin_planning 2>$null
    $validateResult = $validateJson | ConvertFrom-Json
    if ($validateResult.is_valid -and $workItemState -eq 'To Do' -and $hasSeededChildren) {
        twig set $WorkItemId --output json 2>$null | Out-Null
        twig state $validateResult.target_state --output json 2>$null | Out-Null
        $workItemState = $validateResult.target_state
    }

    # ── Step 2: Intent conflict detection (#2634) ─────────────────────────
    # Detects conflicts between the user's stated intent and the current
    # work item state. Ported from reference lines 200-210.
    $intentConflict = $false
    $needsCleanup   = $false

    switch ($Intent) {
        'new' {
            if ($hasSeededChildren -or $hasPlan) {
                $intentConflict = $true
            }
        }
        'redo' {
            $needsCleanup = $hasSeededChildren -or $hasPlan
        }
    }

    # ── Step 3: Phase override by intent (#2634) ─────────────────────────
    # When intent conflicts or cleanup is needed, preserve the phase from
    # polyphony route (let conflict/cleanup take precedence over intent).
    # When plan status is ambiguous, surface an error message.
    if (-not $intentConflict -and -not $needsCleanup) {
        if ($planStatus -eq 'ambiguous') {
            $errorMsg = "Plan status is ambiguous: multiple plan sources detected. Resolve before proceeding."
        }
        # Otherwise, use the phase from polyphony route (set by #2632)
    }

    # ── Build output ─────────────────────────────────────────────────────
    $childrenSummary = @{
        total = $childCount
        done  = $doneCount
        doing = $doingCount
        todo  = $todoCount
    } | ConvertTo-Json -Compress

    $output = [ordered]@{
        work_item_id            = $WorkItemId
        work_item_type          = $workItemType
        work_item_state         = $workItemState
        work_item_title         = $workItemTitle
        intent                  = $Intent
        phase                   = $phase
        has_plan                = $hasPlan
        plan_status             = $planStatus
        plan_path               = $planPath
        plan_source             = $planSource
        has_seeded_children     = $hasSeededChildren
        any_child_missing_tasks = $anyChildMissingTasks
        seed_status             = $seedStatus
        children_summary        = $childrenSummary
        implementation_status   = $implementationStatus
        workspace_hint          = $workspaceHint
        ado_org                 = $_adoOrg
        ado_project             = $_adoProject
        ado_workspace           = $_adoWorkspace
        intent_conflict         = $intentConflict
        needs_cleanup           = $needsCleanup
        error                   = $errorMsg
    }

    $output | ConvertTo-Json -Depth 3
}
catch {
    [ordered]@{
        error        = $_.Exception.Message
        phase        = 'error'
        work_item_id = $WorkItemId
    } | ConvertTo-Json
    exit 1
}
