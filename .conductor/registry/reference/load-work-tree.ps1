<#
.SYNOPSIS
    Loads the ADO work tree and discovers PR group structure from work item tags.

.DESCRIPTION
    Deterministic script that reads the ADO hierarchy via twig CLI and groups
    work items into PR groups based on their PG-N tags (P1: work items are
    source of truth).

    Fallback: if no PG tags exist on any children, creates a single PG-1
    containing all items with a warning.

.PARAMETER WorkItemId
    The Epic, Issue, or Task ADO ID.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId
)

$ErrorActionPreference = 'Stop'

# Resolve GH_TOKEN (bypasses credential helper deadlock in non-TTY)
. "$PSScriptRoot/resolve-gh-token.ps1"

# Timeout-safe gh CLI wrapper (prevents hangs on gh pr list etc.)
. "$PSScriptRoot/invoke-gh.ps1"

# Derive --repo slug for all gh CLI calls (prevents repo-selection prompts)
$_ghRepo = ''
$_remoteUrl = (git remote get-url origin 2>$null) ?? ''
if ($_remoteUrl -match 'github\.com(?:/|:)([^/]+/[^/.]+)') { $_ghRepo = $Matches[1] }

try {
# ── Step 0: Sync local cache from ADO ────────────────────────────────────────
# Ensure the local .twig cache reflects the latest ADO state before loading
# the work tree. Prevents stale state from prior runs or other agents.

twig sync --output json 2>$null | Out-Null

# ── Step 1: Load the work tree from ADO ──────────────────────────────────────

twig set $WorkItemId --output json 2>$null | Out-Null
$treeJson = twig tree --depth 2 --output json 2>$null
$tree = $treeJson | ConvertFrom-Json

$focus = $tree.focus
$children = $tree.children

# Build flat list of all issues and tasks
$issues = @()
$allTasks = @()

if ($focus.type -eq 'Epic') {
    foreach ($child in $children) {
        $issue = [ordered]@{
            id         = $child.id
            title      = $child.title
            state      = $child.state
            type       = $child.type
            tags       = $child.tags
            task_count = 0
            tasks      = @()
        }
        # Get tasks under this issue
        # First try: grandchildren from depth-2 tree (works if twig tree depth bug is fixed)
        $issueTasks = $child.children
        # Fallback: if no grandchildren returned, drill into the Issue directly
        if (-not $issueTasks -or $issueTasks.Count -eq 0) {
            twig set $child.id --output json 2>$null | Out-Null
            $issueTreeJson = twig tree --depth 1 --output json 2>$null
            $issueTree = $issueTreeJson | ConvertFrom-Json
            $issueTasks = $issueTree.children
        }
        if ($issueTasks) {
            foreach ($task in $issueTasks) {
                $allTasks += [ordered]@{
                    id    = $task.id
                    title = $task.title
                    state = $task.state
                    type  = $task.type
                    tags  = $task.tags
                }
                $issue.task_count++
            }
            $issue.tasks = @($issueTasks | ForEach-Object {
                [ordered]@{ id = $_.id; title = $_.title; state = $_.state; tags = $_.tags }
            })
        }
        $issues += $issue
    }
    # Restore focus to the Epic after drilling into Issues
    twig set $WorkItemId --output json 2>$null | Out-Null
}
elseif ($focus.type -eq 'Issue') {
    $issues += [ordered]@{
        id         = $focus.id
        title      = $focus.title
        state      = $focus.state
        type       = $focus.type
        tags       = $focus.tags
        task_count = if ($children) { $children.Count } else { 0 }
        tasks      = @()
    }
    if ($children) {
        foreach ($task in $children) {
            $allTasks += [ordered]@{
                id    = $task.id
                title = $task.title
                state = $task.state
                type  = $task.type
                tags  = $task.tags
            }
        }
        $issues[0].tasks = @($children | ForEach-Object {
            [ordered]@{ id = $_.id; title = $_.title; state = $_.state; tags = $_.tags }
        })
    }
}
elseif ($focus.type -eq 'Task') {
    $allTasks += [ordered]@{
        id    = $focus.id
        title = $focus.title
        state = $focus.state
        type  = $focus.type
        tags  = $focus.tags
    }
}

# ── Step 2: Discover PR groups from tags ─────────────────────────────────────

$pgMap = @{}  # PG name → list of items

# Extract PG tag from each work item's tags string
function Get-PGTag {
    param([string]$Tags)
    if (-not $Tags) { return $null }
    $tagList = $Tags -split ';\s*'
    foreach ($tag in $tagList) {
        $tag = $tag.Trim()
        if ($tag -match '^PG-\d+') { return $tag }
    }
    return $null
}

# Scan all issues and tasks for PG tags
$allItems = @($issues) + @($allTasks)
$taggedCount = 0

foreach ($item in $allItems) {
    $pgTag = Get-PGTag -Tags $item.tags
    if ($pgTag) {
        $taggedCount++
        if (-not $pgMap.ContainsKey($pgTag)) {
            $pgMap[$pgTag] = @{ task_ids = @(); issue_ids = @() }
        }
        if ($item.type -eq 'Task') {
            $pgMap[$pgTag].task_ids += $item.id
        }
        else {
            $pgMap[$pgTag].issue_ids += $item.id
        }
    }
}

# ── Step 3: Build PR groups array ────────────────────────────────────────────

$prGroups = @()

if ($pgMap.Count -gt 0) {
    # Sort PG keys numerically (PG-1, PG-2, ...)
    $sortedPGs = $pgMap.Keys | Sort-Object { [int]($_ -replace '^PG-(\d+).*', '$1') }
    foreach ($pgName in $sortedPGs) {
        $pg = $pgMap[$pgName]
        $slug = ($pgName -replace '[^a-zA-Z0-9]+', '-').ToLower()
        $branchName = "feature/$slug"
        if ($branchName.Length -gt 60) { $branchName = $branchName.Substring(0, 60) }

        $prGroups += [ordered]@{
            name                   = $pgName
            task_ids               = $pg.task_ids
            issue_ids              = $pg.issue_ids
            branch_name_suggestion = $branchName
        }
    }
}
else {
    # Fallback: no PG tags found — create single PG with all items
    Write-Warning "No PG tags found on work items. Creating single PG-1 with all items."
    $slug = ($focus.title -replace '[^a-zA-Z0-9]+', '-' -replace '-+$', '').ToLower()
    $branchName = "feature/pg-1-$slug"
    if ($branchName.Length -gt 60) { $branchName = $branchName.Substring(0, 60) }

    $prGroups += [ordered]@{
        name                   = "PG-1"
        task_ids               = @($allTasks | ForEach-Object { $_.id })
        issue_ids              = @($issues | ForEach-Object { $_.id })
        branch_name_suggestion = $branchName
    }
}

# ── Step 4: Determine PG completion status (P3 resume support) ───────────────

# Check for merged PRs on branches matching PG names
$mergedPRs = @()
$prListJson = _InvokeGh @('pr', 'list', '--repo', $_ghRepo, '--state', 'merged', '--limit', '50', '--json', 'number,headRefName,mergedAt')
if ($prListJson) {
    $mergedPRs = $prListJson | ConvertFrom-Json
}

foreach ($pg in $prGroups) {
    $branchSlug = $pg.branch_name_suggestion
    # Check if a merged PR exists for this PG's branch — exact branch name match only
    $matchedPR = $mergedPRs | Where-Object { $_.headRefName -eq $branchSlug }

    # Check if all tasks in this PG are Done
    $pgTasksDone = $true
    foreach ($taskId in $pg.task_ids) {
        $task = $allTasks | Where-Object { $_.id -eq $taskId }
        if ($task -and $task.state -ne 'Done') { $pgTasksDone = $false; break }
    }

    # When PG tags exist, a merged PR is sufficient to mark complete (the PG
    # structure was explicitly defined by the seeder). When falling back to a
    # single untagged PG, also require all tasks Done — a merged PR alone is
    # unreliable because the fallback branch name may collide with prior PRs.
    if ($taggedCount -eq 0) {
        $pg.completed = ($matchedPR.Count -gt 0) -and $pgTasksDone
    } else {
        $pg.completed = ($matchedPR.Count -gt 0)
    }
    $pg.merged_pr = if ($matchedPR.Count -gt 0) { $matchedPR[0].number } else { 0 }

    # Identify non-Done items in completed PGs (for resume reconciliation)
    # Tasks: only include "Doing" tasks (started but interrupted). "To Do" tasks
    # may be genuinely unimplemented and should NOT be auto-closed.
    # Issues: include all non-Done (Issues represent PG-level scope).
    $pg.non_done_task_ids = @()
    $pg.stale_doing_task_ids = @()
    $pg.non_done_issue_ids = @()
    if ($pg.completed) {
        foreach ($taskId in $pg.task_ids) {
            $task = $allTasks | Where-Object { $_.id -eq $taskId }
            if ($task -and $task.state -ne 'Done') {
                $pg.non_done_task_ids += $task.id
                if ($task.state -eq 'Doing') {
                    $pg.stale_doing_task_ids += $task.id
                }
            }
        }
        foreach ($issueId in $pg.issue_ids) {
            $issue = $issues | Where-Object { $_.id -eq $issueId }
            if ($issue -and $issue.state -ne 'Done') { $pg.non_done_issue_ids += $issue.id }
        }
        $pg.needs_reconciliation = ($pg.stale_doing_task_ids.Count -gt 0 -or $pg.non_done_issue_ids.Count -gt 0)
    }
    else {
        $pg.needs_reconciliation = $false
    }
}

$completedPGs = @($prGroups | Where-Object { $_.completed })
$pendingPGs = @($prGroups | Where-Object { -not $_.completed })
$nextPG = if ($pendingPGs.Count -gt 0) { $pendingPGs[0].name } else { '' }

# ── Step 5: Output ───────────────────────────────────────────────────────────

[ordered]@{
    work_tree  = [ordered]@{
        epic_id    = $focus.id
        epic_title = $focus.title
        epic_type  = $focus.type
        issues     = $issues
    }
    pr_groups       = $prGroups
    completed_pgs   = @($completedPGs | ForEach-Object { $_.name })
    pending_pgs     = @($pendingPGs | ForEach-Object { $_.name })
    next_pg         = $nextPG
    pgs_needing_reconciliation = @($prGroups | Where-Object { $_.needs_reconciliation } | ForEach-Object {
        $pg = $_
        [ordered]@{
            name                  = $pg.name
            merged_pr             = $pg.merged_pr
            stale_doing_task_ids  = $pg.stale_doing_task_ids
            non_done_issue_ids    = $pg.non_done_issue_ids
            skipped_todo_task_ids = @($pg.non_done_task_ids | Where-Object { $_ -notin $pg.stale_doing_task_ids })
        }
    })
    total_tasks     = $allTasks.Count
    total_issues    = $issues.Count
    tagged_items    = $taggedCount
    untagged_items  = ($allItems.Count - $taggedCount)
} | ConvertTo-Json -Depth 5
}
catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
