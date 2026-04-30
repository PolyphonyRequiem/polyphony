<#
.SYNOPSIS
    Loads a work item hierarchy and produces PG-grouped PR structure.
.DESCRIPTION
    Replaces the reference load-work-tree.ps1 with a Polyphony-backed wrapper.
    Uses polyphony hierarchy --depth 3 for uniform tree loading (no type branches).
    Produces work_tree, pr_groups, and repo_slug output.
.PARAMETER WorkItemId
    ADO work item ID (root of the hierarchy).
#>
[CmdletBinding()]
param([Parameter(Mandatory)][int]$WorkItemId)
$ErrorActionPreference = 'Stop'

# ── Dot-sourced dependencies ─────────────────────────────────────────────────
. "$PSScriptRoot/resolve-gh-token.ps1"
. "$PSScriptRoot/invoke-gh.ps1"
. "$PSScriptRoot/lib/pg-helpers.ps1"

# ── Derive repo slug ─────────────────────────────────────────────────────────
$_ghRepo = ''
$_remoteUrl = (git remote get-url origin 2>$null) ?? ''
if ($_remoteUrl -match 'github\.com(?:/|:)([^/]+/[^/.]+)') { $_ghRepo = $Matches[1] }

# ── Sync and load hierarchy ──────────────────────────────────────────────────
twig sync --output json 2>$null | Out-Null
$hierarchyJson = polyphony hierarchy --work-item $WorkItemId --depth 3 2>$null
$hierarchy = $hierarchyJson | ConvertFrom-Json

# ── Flatten items from hierarchy ─────────────────────────────────────────────
function Flatten-Hierarchy($node) {
    $items = @($node)
    if ($node.children) {
        foreach ($child in $node.children) {
            $items += Flatten-Hierarchy $child
        }
    }
    return $items
}
$allItems = @(Flatten-Hierarchy $hierarchy)

# ── Build issues array from hierarchy children (#2661) ────────────────────────
$issues = @()
foreach ($child in @($hierarchy.children)) {
    if ($null -eq $child) { continue }
    $issue = [ordered]@{
        id         = $child.work_item_id
        title      = $child.title
        state      = $child.state
        type       = $child.type
        tags       = if ($child.tags) { $child.tags } else { '' }
        task_count = if ($child.children) { @($child.children).Count } else { 0 }
        tasks      = @()
    }
    if ($child.children) {
        $issue.tasks = @($child.children | ForEach-Object {
            [ordered]@{
                id    = $_.work_item_id
                title = $_.title
                state = $_.state
                tags  = if ($_.tags) { $_.tags } else { '' }
            }
        })
    }
    $issues += $issue
}

# ── Build work_tree structure (#2661) ─────────────────────────────────────────
$workTree = [ordered]@{
    epic_id    = $hierarchy.work_item_id
    epic_title = $hierarchy.title
    epic_type  = $hierarchy.type
    issues     = $issues
}

# ── PG grouping and PR groups (#2661) ─────────────────────────────────────────
$pgMap = Group-ByPG -items $allItems

$prGroups = @()
if ($pgMap.Count -gt 0) {
    $sortedPGs = $pgMap.Keys | Sort-Object { [int]($_ -replace '^PG-(\d+).*', '$1') }
    foreach ($pgName in $sortedPGs) {
        $pg = $pgMap[$pgName]
        $slug = ($pgName -replace '[^a-zA-Z0-9]+', '-').ToLower()
        $branchName = "feature/$slug"
        if ($branchName.Length -gt 60) { $branchName = $branchName.Substring(0, 60) }
        $prGroups += [ordered]@{
            name                   = $pgName
            task_ids               = $pg.implementable_ids
            issue_ids              = $pg.container_ids
            branch_name_suggestion = $branchName
        }
    }
}
else {
    Write-Warning "No PG tags found on work items. Creating single PG-1 with all items."
    $slug = ($hierarchy.title -replace '[^a-zA-Z0-9]+', '-' -replace '-+$', '').ToLower()
    $branchName = "feature/pg-1-$slug"
    if ($branchName.Length -gt 60) { $branchName = $branchName.Substring(0, 60) }
    $implementableIds = @($allItems |
        Where-Object { $_.capabilities -contains 'implementable' -and $_.capabilities -notcontains 'plannable' } |
        ForEach-Object { $_.work_item_id })
    $containerIds = @($allItems |
        Where-Object { $_.capabilities -contains 'plannable' } |
        ForEach-Object { $_.work_item_id })
    $prGroups += [ordered]@{
        name                   = "PG-1"
        task_ids               = $implementableIds
        issue_ids              = $containerIds
        branch_name_suggestion = $branchName
    }
}

# ── Build output ──────────────────────────────────────────────────────────────
$output = [ordered]@{
    work_tree = $workTree
    pr_groups = $prGroups
    repo_slug = $_ghRepo
}

$output | ConvertTo-Json -Depth 5
