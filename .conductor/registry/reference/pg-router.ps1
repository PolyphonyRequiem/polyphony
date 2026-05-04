<#
.SYNOPSIS
    Deterministic PG router for the implementation workflow.

.DESCRIPTION
    Replaces the LLM pr_group_manager for routing decisions. Each invocation
    queries ADO and git ground truth to determine which PR group needs action.

    Actions:
      create_branch — PG has no feature branch yet
      route_tasks   — PG has a branch but tasks remain
      submit_pr     — all tasks done, needs PR (or has open PR)
      all_complete  — every PG has a merged PR

.PARAMETER WorkItemId
    The Epic ADO work item ID.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][int]$WorkItemId)

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
    # ── Sync local cache ──────────────────────────────────────────────
    twig sync --output json *>$null

    # ── Load work tree ────────────────────────────────────────────────
    twig set $WorkItemId --output json *>$null
    $tree = (twig tree --depth 2 --output json 2>$null) | ConvertFrom-Json
    $focus = $tree.focus
    $children = $tree.children

    # ── Build issues + tasks ──────────────────────────────────────────
    $issues = @()
    $allTasks = @()

    foreach ($child in $children) {
        $issue = @{
            id = $child.id; title = $child.title; state = $child.state
            type = $child.type; tags = $child.tags; tasks = @()
        }

        $issueTasks = $child.children
        if (-not $issueTasks -or $issueTasks.Count -eq 0) {
            twig set $child.id --output json *>$null
            $issueTasks = ((twig tree --depth 1 --output json 2>$null) | ConvertFrom-Json).children
        }

        if ($issueTasks) {
            foreach ($t in $issueTasks) {
                $taskObj = @{ id = $t.id; title = $t.title; state = $t.state; type = $t.type; tags = $t.tags }
                $allTasks += $taskObj
                $issue.tasks += $taskObj
            }
        }
        $issues += $issue
    }

    # Restore focus to Epic
    twig set $WorkItemId --output json *>$null

    # ── Discover PG structure from tags ───────────────────────────────
    $pgMap = [ordered]@{}
    $allItems = @($issues) + @($allTasks)

    foreach ($item in $allItems) {
        if (-not $item.tags) { continue }
        $match = ($item.tags -split ';\s*') | Where-Object { $_.Trim() -match '^PG-\d+' } | Select-Object -First 1
        if ($match) {
            $pgTag = $match.Trim()
            if (-not $pgMap.Contains($pgTag)) {
                $pgMap[$pgTag] = @{ task_ids = [System.Collections.ArrayList]@(); issue_ids = [System.Collections.ArrayList]@() }
            }
            if ($item.type -eq 'Task') { [void]$pgMap[$pgTag].task_ids.Add($item.id) }
            else { [void]$pgMap[$pgTag].issue_ids.Add($item.id) }
        }
    }

    $prGroups = @()
    # Include work item ID in branch names to prevent cross-epic collisions.
    # Without this, "feature/pg-1" from a prior epic's merged PR would falsely
    # mark the current epic's PG-1 as complete.
    if ($pgMap.Count -gt 0) {
        foreach ($pgName in ($pgMap.Keys | Sort-Object { [int]($_ -replace '^PG-(\d+).*', '$1') })) {
            $pg = $pgMap[$pgName]
            $slug = ($pgName -replace '[^a-zA-Z0-9]+', '-').ToLower()
            $prGroups += @{
                name = $pgName
                task_ids = @($pg.task_ids)
                issue_ids = @($pg.issue_ids)
                branch_name = "feature/$WorkItemId-$slug"
            }
        }
    }
    else {
        # Fallback: single PG with all items
        $slug = ($focus.title -replace '[^a-zA-Z0-9]+', '-' -replace '-+$', '').ToLower()
        if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40) }
        $prGroups += @{
            name = 'PG-1'
            task_ids = @($allTasks | ForEach-Object { $_.id })
            issue_ids = @($issues | ForEach-Object { $_.id })
            branch_name = "feature/$WorkItemId-pg-1-$slug"
        }
    }

    # ── Check git branches and GitHub PRs ─────────────────────────────
    $remoteBranches = @(git branch -r 2>$null | ForEach-Object { $_.Trim() -replace '^origin/', '' })

    $mergedPRs = @()
    $mpJson = _InvokeGh @('pr', 'list', '--repo', $_ghRepo, '--state', 'merged', '--limit', '100', '--json', 'number,headRefName')
    if ($mpJson) { $mergedPRs = $mpJson | ConvertFrom-Json }

    $openPRs = @()
    $opJson = _InvokeGh @('pr', 'list', '--repo', $_ghRepo, '--state', 'open', '--limit', '50', '--json', 'number,headRefName,url')
    if ($opJson) { $openPRs = $opJson | ConvertFrom-Json }

    # ── Determine each PG's state and pick target ─────────────────────
    $completedPGs = @()
    $targetPG = $null

    foreach ($pg in $prGroups) {
        $hasMergedPR = ($mergedPRs | Where-Object { $_.headRefName -eq $pg.branch_name }).Count -gt 0

        # Defense-in-depth: even if a merged PR matches the branch name, verify
        # that at least one of the PG's issues has progressed beyond To Do. This
        # catches stale/orphaned branch name collisions from prior runs.
        if ($hasMergedPR) {
            $pgIssueStates = @($issues | Where-Object { $_.id -in $pg.issue_ids } | ForEach-Object { $_.state })
            $allIssuesToDo = ($pgIssueStates | Where-Object { $_ -eq 'To Do' }).Count -eq $pgIssueStates.Count -and $pgIssueStates.Count -gt 0
            if ($allIssuesToDo) {
                # Merged PR doesn't match this PG's actual work — treat as incomplete
                $hasMergedPR = $false
            }
        }

        # Backwards compatibility / gh-failure recovery: if no merged PR matches
        # the branch name, trust ADO state as source of truth. This fires when:
        #   - Branch was renamed or created under a prior convention
        #   - gh pr list failed or returned stale data (auth issues, timeouts)
        #   - Branch deletion failed after merge (permissions), leaving a stale ref
        # Only require the branch to be absent when there IS an open PR on it —
        # otherwise an undeletable branch would block completion forever.
        $hasOpenPR = ($openPRs | Where-Object { $_.headRefName -eq $pg.branch_name }).Count -gt 0
        if (-not $hasMergedPR -and -not $hasOpenPR) {
            $pgIssueStates = @($issues | Where-Object { $_.id -in $pg.issue_ids } | ForEach-Object { $_.state })
            $allIssuesDone = $pgIssueStates.Count -gt 0 -and ($pgIssueStates | Where-Object { $_ -ne 'Done' }).Count -eq 0

            # Task-only PGs (no issue_ids): fall back to task state check
            $pgTaskStates = @($allTasks | Where-Object { $_.id -in $pg.task_ids } | ForEach-Object { $_.state })
            $allTasksDoneInPG = $pgTaskStates.Count -gt 0 -and ($pgTaskStates | Where-Object { $_ -ne 'Done' }).Count -eq 0

            if ($allIssuesDone -or $allTasksDoneInPG) {
                $hasMergedPR = $true
            }
        }

        if ($hasMergedPR) {
            $completedPGs += $pg.name
            continue
        }

        if (-not $targetPG) {
            $branchExists = $pg.branch_name -in $remoteBranches
            $openPR = $openPRs | Where-Object { $_.headRefName -eq $pg.branch_name } | Select-Object -First 1
            $allTasksDone = -not ($allTasks | Where-Object { $_.id -in $pg.task_ids -and $_.state -ne 'Done' })

            # Taskless PG: check if PG-tagged issues are all Done
            $pgIssuesDone = $pg.issue_ids.Count -gt 0 -and
                ($issues | Where-Object { $_.id -in $pg.issue_ids -and $_.state -ne 'Done' }).Count -eq 0

            if ($openPR) {
                $pg['pg_state'] = 'submit_pr'
                $pg['pr_number'] = $openPR.number
                $pg['pr_url'] = $openPR.url
            }
            elseif ($allTasksDone -and ($pg.task_ids.Count -gt 0 -or $pgIssuesDone)) {
                $pg['pg_state'] = 'submit_pr'
            }
            elseif ($branchExists) {
                # Branch exists — route through branch_manager to rebase onto
                # updated main (stacked PR pattern: PG-1 merged → rebase PG-2)
                $pg['pg_state'] = 'create_branch'
            }
            else {
                $pg['pg_state'] = 'create_branch'
            }
            $targetPG = $pg
        }
    }

    # ── Output ────────────────────────────────────────────────────────
    if (-not $targetPG) {
        [ordered]@{
            action = 'all_complete'
            current_pg = ''; branch_name = ''
            issue_ids = @(); task_ids = @()
            pr_number = 0; pr_url = ''
            completed_pgs = $completedPGs; remaining_pgs = @()
            total_pgs = $prGroups.Count
        } | ConvertTo-Json -Depth 3
    }
    else {
        $remaining = @($prGroups |
            Where-Object { $_.name -ne $targetPG.name -and $_.name -notin $completedPGs } |
            ForEach-Object { $_.name })

        [ordered]@{
            action = $targetPG['pg_state']
            current_pg = $targetPG.name
            branch_name = $targetPG.branch_name
            issue_ids = @($targetPG.issue_ids)
            task_ids = @($targetPG.task_ids)
            pr_number = if ($targetPG['pr_number']) { $targetPG['pr_number'] } else { 0 }
            pr_url = if ($targetPG['pr_url']) { $targetPG['pr_url'] } else { '' }
            completed_pgs = $completedPGs
            remaining_pgs = $remaining
            total_pgs = $prGroups.Count
        } | ConvertTo-Json -Depth 3
    }
}
catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
