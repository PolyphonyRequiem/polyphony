<#
.SYNOPSIS
    Routes to the next implementable work item within a PG using polyphony hierarchy
    and capability-based filtering.
.DESCRIPTION
    Thin Polyphony-backed wrapper that replaces the reference task-router.ps1.
    Uses polyphony hierarchy and filters by PG tag + implementable capability
    instead of relying on implicit type assumptions. Transitions the first
    non-Done implementable item to Doing.
.PARAMETER WorkItemId
    ADO work item ID (root of the hierarchy).
.PARAMETER PGName
    PR Group name (e.g., PG-1) to scope task selection to. Mutually exclusive
    with -PgNumber; one of the two must be provided. Preserved for direct
    invocation, scripted callers, and the existing test suite.
.PARAMETER PgNumber
    PR Group number (e.g. 1 for PG-1). Convenience parameter for workflow
    YAML callers that already track the PG as an integer (see
    implement-pg.yaml's pg_router/task_router agents). When supplied,
    PGName is derived as "PG-$PgNumber".
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    [Parameter()][string]$PGName,
    [Parameter()][int]$PgNumber = 0
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/pg-helpers.ps1"
. "$PSScriptRoot/lib/ado-helpers.ps1"

# ── Resolve PGName from PgNumber if only the latter was supplied ─────────────
if (-not $PGName -and $PgNumber -gt 0) { $PGName = "PG-$PgNumber" }
if (-not $PGName) {
    [ordered]@{ error = 'Either -PGName or -PgNumber must be provided.' } | ConvertTo-Json
    exit 1
}

try {
    # ── Sync & fetch hierarchy ────────────────────────────────────────────────
    twig sync --output json 2>$null | Out-Null
    $hierarchy = (polyphony hierarchy --work-item $WorkItemId --depth 3 2>$null) | ConvertFrom-Json

    # ── Route to get workspace_hint for branch naming ─────────────────────────
    $workspaceHint = $null
    try {
        $routeJson = polyphony route --work-item $WorkItemId 2>$null
        if ($routeJson) {
            $routeResult = $routeJson | ConvertFrom-Json
            $workspaceHint = $routeResult.workspace_hint
        }
    } catch { <# fall back to manual derivation #> }

    # ── Flatten hierarchy ─────────────────────────────────────────────────────
    function Flatten-Hierarchy($node, [object]$parent = $null) {
        $node | Add-Member -NotePropertyName '_parent' -NotePropertyValue $parent -Force
        $items = @($node)
        if ($node.children) {
            foreach ($c in $node.children) { $items += Flatten-Hierarchy $c $node }
        }
        return $items
    }
    $allItems = @(Flatten-Hierarchy $hierarchy)

    # ── Filter implementable items for this PG ────────────────────────────────
    $implementable = @($allItems | Where-Object { $_.capabilities -contains 'implementable' })

    # Primary: implementable items directly tagged with $PGName
    $candidates = @($implementable | Where-Object { (Get-PGTag -Tags $_.tags) -eq $PGName })

    # Fallback 1: implementable children under containers tagged with $PGName
    if ($candidates.Count -eq 0) {
        $pgContainers = @($allItems | Where-Object {
            $_.capabilities -contains 'plannable' -and (Get-PGTag -Tags $_.tags) -eq $PGName
        })
        $containerIds = @($pgContainers | ForEach-Object { $_.work_item_id })
        $candidates = @($implementable | Where-Object {
            $_._parent -and ($containerIds -contains $_._parent.work_item_id)
        })
    }

    # Fallback 2: issue-as-task — plannable+implementable, tagged with $PGName, no children
    if ($candidates.Count -eq 0) {
        $candidates = @($allItems | Where-Object {
            $_.capabilities -contains 'plannable' -and
            $_.capabilities -contains 'implementable' -and
            (Get-PGTag -Tags $_.tags) -eq $PGName -and
            (-not $_.children -or $_.children.Count -eq 0)
        })
    }

    # Final fallback: all implementable items (no PG filter)
    if ($candidates.Count -eq 0) {
        $candidates = @($implementable)
    }

    # ── Select first non-Done item ────────────────────────────────────────────
    $nonDone = @($candidates | Where-Object { $_.state -ne 'Done' })
    $remaining = $nonDone.Count

    if ($remaining -eq 0) {
        # All tasks done
        [ordered]@{
            action          = 'all_tasks_done'
            task_id         = 0
            task_title      = ''
            issue_id        = 0
            issue_title     = ''
            remaining_count = 0
            current_pg      = $PGName
            branch_name     = ''
            ado_workspace   = Get-AdoWorkspace
        } | ConvertTo-Json -Depth 3
        return
    }

    $nextItem = $nonDone[0]

    # ── Transition to the per-template "in progress" state ──────────────────
    # Resolve the state name via polyphony validate (same canonical pattern
    # used in scripts/scope-closer.ps1:54-60). This is the first live use of
    # `begin_implementation`; standard template stubs from
    # scripts/bootstrap-conductor.ps1 already include it for every
    # implementable type, so a missing-row failure here means the repo's
    # process-config.yaml was hand-edited to drop the row.
    twig set $nextItem.work_item_id --output json 2>$null | Out-Null
    $validateJson = polyphony validate --work-item $nextItem.work_item_id --event begin_implementation 2>$null
    $validate = $validateJson | ConvertFrom-Json
    if (-not $validate.is_valid) {
        throw "Cannot start task $($nextItem.work_item_id) (event=begin_implementation): $($validate.message)"
    }
    twig state $validate.target_state --output json 2>$null | Out-Null

    # ── Derive parent container info (nearest plannable ancestor) ─────────────
    $issueId = 0
    $issueTitle = ''
    $ancestor = $nextItem._parent
    while ($ancestor) {
        if ($ancestor.capabilities -contains 'plannable') {
            $issueId = $ancestor.work_item_id
            $issueTitle = $ancestor.title
            break
        }
        $ancestor = $ancestor._parent
    }

    # ── Branch name (workspace_hint with current-branch priority) ────────────
    $expectedBranch = ''
    if ($workspaceHint -and $workspaceHint.pg_branch) {
        $pgNum = if ($PGName -match 'PG-(\d+)') { $Matches[1] } else { '1' }
        $expectedBranch = $workspaceHint.pg_branch -replace '\{n\}', $pgNum
    } else {
        $branchSlug = ($PGName -replace '[^a-zA-Z0-9]+', '-').ToLower()
        $expectedBranch = "feature/$WorkItemId-$branchSlug"
        if ($expectedBranch.Length -gt 60) { $expectedBranch = $expectedBranch.Substring(0, 60) }
    }
    $currentBranch = (git branch --show-current 2>$null) ?? ''
    if ($currentBranch -and $currentBranch -eq $expectedBranch) {
        $branchName = $currentBranch
    } else {
        $branchName = $expectedBranch
    }

    # ── Output JSON ───────────────────────────────────────────────────────────
    [ordered]@{
        action          = 'implement_task'
        task_id         = $nextItem.work_item_id
        task_title      = $nextItem.title
        issue_id        = $issueId
        issue_title     = $issueTitle
        remaining_count = $remaining
        current_pg      = $PGName
        branch_name     = $branchName
        ado_workspace   = Get-AdoWorkspace
    } | ConvertTo-Json -Depth 3
} catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
