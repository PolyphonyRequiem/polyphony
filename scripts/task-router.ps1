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
    PR Group name (e.g., PG-1) to scope task selection to.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    [Parameter(Mandatory)][string]$PGName
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/pg-helpers.ps1"

try {
    # ── Sync & fetch hierarchy ────────────────────────────────────────────────
    twig sync --output json 2>$null | Out-Null
    $hierarchy = (polyphony hierarchy --work-item $WorkItemId --depth 3 2>$null) | ConvertFrom-Json

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
        } | ConvertTo-Json -Depth 3
        return
    }

    $nextItem = $nonDone[0]

    # ── Transition to Doing ───────────────────────────────────────────────────
    twig set $nextItem.work_item_id --output json 2>$null | Out-Null
    twig state Doing --output json 2>$null | Out-Null

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

    # ── Branch name ───────────────────────────────────────────────────────────
    $branchSlug = ($PGName -replace '[^a-zA-Z0-9]+', '-').ToLower()
    $expectedPrefix = "feature/$WorkItemId-$branchSlug"
    $currentBranch = (git branch --show-current 2>$null) ?? ''
    if ($currentBranch -and $currentBranch.StartsWith($expectedPrefix)) {
        $branchName = $currentBranch
    } else {
        $branchName = $expectedPrefix
        if ($branchName.Length -gt 60) { $branchName = $branchName.Substring(0, 60) }
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
    } | ConvertTo-Json -Depth 3
} catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
