<#
.SYNOPSIS
    Loads a work item hierarchy and produces PG-grouped JSON with completion status.
.PARAMETER WorkItemId
    ADO work item ID (root of the hierarchy).
#>
[CmdletBinding()]
param([Parameter(Mandatory)][int]$WorkItemId)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/resolve-gh-token.ps1"
. "$PSScriptRoot/invoke-gh.ps1"
. "$PSScriptRoot/lib/pg-helpers.ps1"

try {
$_ghRepo = ''
$_remoteUrl = (git remote get-url origin 2>$null) ?? ''
if ($_remoteUrl -match 'github\.com(?:/|:)([^/]+/[^/.]+)') { $_ghRepo = $Matches[1] }

twig sync --output json 2>$null | Out-Null
$hierarchy = (polyphony hierarchy --work-item $WorkItemId --depth 3 2>$null) | ConvertFrom-Json

function Flatten-Hierarchy($node) {
    $items = @($node)
    if ($node.children) { foreach ($c in $node.children) { $items += Flatten-Hierarchy $c } }
    return $items
}
$allItems = @(Flatten-Hierarchy $hierarchy)

$issues = @(foreach ($child in @($hierarchy.children)) {
    if ($null -eq $child) { continue }
    $tasks = @(if ($child.children) { $child.children | ForEach-Object {
        [ordered]@{ id = $_.work_item_id; title = $_.title; state = $_.state; tags = if ($_.tags) { $_.tags } else { '' } }
    }})
    [ordered]@{
        id = $child.work_item_id; title = $child.title; state = $child.state; type = $child.type
        tags = if ($child.tags) { $child.tags } else { '' }
        task_count = $tasks.Count; tasks = $tasks
    }
})

$workTree = [ordered]@{
    epic_id = $hierarchy.work_item_id; epic_title = $hierarchy.title
    epic_type = $hierarchy.type; issues = $issues
}

$pgMap = Group-ByPG -items $allItems
$isFallback = $pgMap.Count -eq 0

function New-BranchName([string]$slug) {
    $b = "feature/$slug"; if ($b.Length -gt 60) { $b = $b.Substring(0, 60) }; $b
}

$prGroups = @()
if (-not $isFallback) {
    foreach ($pgName in ($pgMap.Keys | Sort-Object { [int]($_ -replace '^PG-(\d+).*', '$1') })) {
        $pg = $pgMap[$pgName]
        $prGroups += [ordered]@{
            name = $pgName; task_ids = $pg.implementable_ids; issue_ids = $pg.container_ids
            branch_name_suggestion = New-BranchName (($pgName -replace '[^a-zA-Z0-9]+', '-').ToLower())
        }
    }
} else {
    Write-Warning "No PG tags found on work items. Creating single PG-1 with all items."
    $slug = ($hierarchy.title -replace '[^a-zA-Z0-9]+', '-' -replace '-+$', '').ToLower()
    $prGroups += [ordered]@{
        name = "PG-1"
        task_ids = @($allItems | Where-Object { $_.capabilities -contains 'implementable' -and $_.capabilities -notcontains 'plannable' } | ForEach-Object { $_.work_item_id })
        issue_ids = @($allItems | Where-Object { $_.capabilities -contains 'plannable' } | ForEach-Object { $_.work_item_id })
        branch_name_suggestion = New-BranchName "pg-1-$slug"
    }
}

# ── PG completion status (#2662) ──────────────────────────────────────────────
$mergedPRs = @()
if ($_ghRepo) {
    $json = Invoke-GH 'pr','list','--repo',$_ghRepo,'--state','merged','--limit','50','--json','number,headRefName,mergedAt'
    if ($json) { $mergedPRs = @($json | ConvertFrom-Json) }
}

foreach ($pg in $prGroups) {
    $match = $mergedPRs | Where-Object { $_.headRefName -eq $pg.branch_name_suggestion } | Select-Object -First 1
    $pg['merged_pr'] = if ($match) { $match.number } else { 0 }
    $pgItemIds = @($pg.task_ids) + @($pg.issue_ids)
    $pgItems = @($allItems | Where-Object { $pgItemIds -contains $_.work_item_id })
    $allDone = ($pgItems.Count -gt 0) -and (@($pgItems | Where-Object { $_.state -ne 'Done' }).Count -eq 0)
    $pg['completed'] = if ($isFallback) { ($pg.merged_pr -gt 0) -and $allDone } else { $pg.merged_pr -gt 0 }
    $pg['non_done_task_ids'] = @(); $pg['stale_doing_task_ids'] = @(); $pg['non_done_issue_ids'] = @()
    if ($pg.completed) {
        $pg['non_done_task_ids'] = @($allItems | Where-Object { ($pg.task_ids -contains $_.work_item_id) -and $_.state -ne 'Done' } | ForEach-Object { $_.work_item_id })
        $pg['stale_doing_task_ids'] = @($allItems | Where-Object { ($pg.task_ids -contains $_.work_item_id) -and $_.state -eq 'Doing' } | ForEach-Object { $_.work_item_id })
        $pg['non_done_issue_ids'] = @($allItems | Where-Object { ($pg.issue_ids -contains $_.work_item_id) -and $_.state -ne 'Done' } | ForEach-Object { $_.work_item_id })
    }
    $pg['needs_reconciliation'] = ($pg.stale_doing_task_ids.Count -gt 0) -or ($pg.non_done_issue_ids.Count -gt 0)
}

# ── Summary and output (#2662) ────────────────────────────────────────────────
$completedPGs = @($prGroups | Where-Object { $_.completed })
$pendingPGs = @($prGroups | Where-Object { -not $_.completed })
$taggedCount = @($allItems | Where-Object { Get-PGTag -Tags $_.tags }).Count

[ordered]@{
    work_tree = $workTree; pr_groups = $prGroups
    completed_pgs = @($completedPGs | ForEach-Object { $_.name })
    pending_pgs = @($pendingPGs | ForEach-Object { $_.name })
    next_pg = if ($pendingPGs.Count -gt 0) { $pendingPGs[0].name } else { '' }
    pgs_needing_reconciliation = @($prGroups | Where-Object { $_.needs_reconciliation } | ForEach-Object {
        [ordered]@{ name = $_.name; non_done_task_ids = $_.non_done_task_ids; stale_doing_task_ids = $_.stale_doing_task_ids; non_done_issue_ids = $_.non_done_issue_ids }
    })
    total_tasks = @($allItems | Where-Object { $_.capabilities -contains 'implementable' -and $_.capabilities -notcontains 'plannable' }).Count
    total_issues = @($allItems | Where-Object { $_.capabilities -contains 'plannable' }).Count
    tagged_items = $taggedCount; untagged_items = ($allItems.Count - $taggedCount)
} | ConvertTo-Json -Depth 5

} catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
