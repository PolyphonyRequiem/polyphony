<#
.SYNOPSIS
    Deterministic task router for the implementation workflow.

.DESCRIPTION
    Replaces the LLM task_manager for routing decisions. Queries ADO ground truth
    each invocation to find the next undone task in a PR group.

    Actions:
      implement_task — found a task to implement (transitioned to Doing)
      all_tasks_done — every task in this PG is Done

.PARAMETER WorkItemId
    The Epic ADO work item ID.

.PARAMETER PGName
    The PR group name (e.g., "PG-1") to scope task lookup.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    [Parameter(Mandatory)][string]$PGName
)

$ErrorActionPreference = 'Stop'

try {
    # ── Sync and load tree ────────────────────────────────────────────
    twig sync --output json *>$null
    twig set $WorkItemId --output json *>$null
    $tree = (twig tree --depth 2 --output json 2>$null) | ConvertFrom-Json
    $children = $tree.children

    # ── Build task list for this PG ───────────────────────────────────
    $pgTasks = @()
    $pgIssueMap = @{}  # task_id → issue info

    foreach ($child in $children) {
        $issueTasks = $child.children
        if (-not $issueTasks -or $issueTasks.Count -eq 0) {
            twig set $child.id --output json *>$null
            $issueTasks = ((twig tree --depth 1 --output json 2>$null) | ConvertFrom-Json).children
        }

        if ($issueTasks) {
            foreach ($t in $issueTasks) {
                if (-not $t.tags) { continue }
                $tags = ($t.tags -split ';\s*') | ForEach-Object { $_.Trim() }
                if ($PGName -in $tags) {
                    $pgTasks += $t
                    $pgIssueMap[$t.id] = @{ id = $child.id; title = $child.title }
                }
            }
        }
    }

    # Fallback: if no PG-tagged tasks, check issues for PG tag
    if ($pgTasks.Count -eq 0) {
        foreach ($child in $children) {
            if (-not $child.tags) { continue }
            $tags = ($child.tags -split ';\s*') | ForEach-Object { $_.Trim() }
            if ($PGName -notin $tags) { continue }

            $issueTasks = $child.children
            if (-not $issueTasks -or $issueTasks.Count -eq 0) {
                twig set $child.id --output json *>$null
                $issueTasks = ((twig tree --depth 1 --output json 2>$null) | ConvertFrom-Json).children
            }
            if ($issueTasks) {
                foreach ($t in $issueTasks) {
                    $pgTasks += $t
                    $pgIssueMap[$t.id] = @{ id = $child.id; title = $child.title }
                }
            }
        }
    }

    # Issue-as-task: when a PG has tagged issues but no child tasks,
    # treat the undone issues themselves as work items for the coder.
    # This MUST run before the final fallback (which pulls all tasks from
    # all issues and would mask the missing-tasks case with Done tasks
    # from other PGs).
    if ($pgTasks.Count -eq 0) {
        foreach ($child in $children) {
            if (-not $child.tags) { continue }
            $tags = ($child.tags -split ';\s*') | ForEach-Object { $_.Trim() }
            if ($PGName -in $tags) {
                $pgTasks += $child
                $pgIssueMap[$child.id] = @{ id = $child.id; title = $child.title }
            }
        }
    }

    # Final fallback: if no PG-tagged tasks, issues, or issue-as-task matches,
    # include all tasks (mirrors pg_router's single-PG fallback when no PG tags exist)
    if ($pgTasks.Count -eq 0) {
        foreach ($child in $children) {
            $issueTasks = $child.children
            if (-not $issueTasks -or $issueTasks.Count -eq 0) {
                twig set $child.id --output json *>$null
                $issueTasks = ((twig tree --depth 1 --output json 2>$null) | ConvertFrom-Json).children
            }
            if ($issueTasks) {
                foreach ($t in $issueTasks) {
                    $pgTasks += $t
                    $pgIssueMap[$t.id] = @{ id = $child.id; title = $child.title }
                }
            }
        }
    }

    # Restore focus to Epic
    twig set $WorkItemId --output json *>$null

    # ── Derive branch name ────────────────────────────────────────────
    # Use pg_router's output if available (passed via conductor context),
    # otherwise check current git branch, then fall back to slug derivation
    $branchSlug = ($PGName -replace '[^a-zA-Z0-9]+', '-').ToLower()
    $currentBranch = (git branch --show-current 2>$null)
    if ($currentBranch -and $currentBranch -like "feature/$branchSlug*") {
        $branchName = $currentBranch
    } else {
        $branchName = "feature/$branchSlug"
    }

    # ── Find next undone task ─────────────────────────────────────────
    $pendingTasks = @($pgTasks | Where-Object { $_.state -ne 'Done' })
    $nextTask = $pendingTasks | Select-Object -First 1

    if (-not $nextTask) {
        [ordered]@{
            action = 'all_tasks_done'
            task_id = 0; task_title = ''
            issue_id = 0; issue_title = ''
            remaining_count = 0
            current_pg = $PGName; branch_name = $branchName
        } | ConvertTo-Json -Depth 3
    }
    else {
        # Transition task to Doing (idempotent — ignores error if already Doing)
        twig set $nextTask.id --output json *>$null
        twig state Doing --output json *>$null 2>&1

        $issueInfo = $pgIssueMap[$nextTask.id]

        [ordered]@{
            action = 'implement_task'
            task_id = $nextTask.id
            task_title = $nextTask.title
            issue_id = if ($issueInfo) { $issueInfo.id } else { 0 }
            issue_title = if ($issueInfo) { $issueInfo.title } else { '' }
            remaining_count = ($pendingTasks.Count - 1)
            current_pg = $PGName
            branch_name = $branchName
        } | ConvertTo-Json -Depth 3
    }
}
catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
