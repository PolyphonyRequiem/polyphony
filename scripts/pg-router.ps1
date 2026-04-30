<#
.SYNOPSIS
    Routes work-item hierarchy to the next PR-group action using polyphony
    hierarchy and capability-based classification.
.DESCRIPTION
    Thin Polyphony-backed wrapper that replaces the 237-line reference pg-router.ps1.
    Uses polyphony hierarchy and Group-ByPG for capability-based item classification,
    eliminating type-name literals.
.PARAMETER WorkItemId
    ADO work item ID (root of the hierarchy).
#>
[CmdletBinding()]
param([Parameter(Mandatory)][int]$WorkItemId)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/resolve-gh-token.ps1"
. "$PSScriptRoot/invoke-gh.ps1"
. "$PSScriptRoot/lib/pg-helpers.ps1"
. "$PSScriptRoot/lib/ado-helpers.ps1"
. "$PSScriptRoot/lib/gh-helpers.ps1"

try {
    # ── Derive GitHub repo slug ───────────────────────────────────────────────
    $_ghRepo = Get-RepoSlug

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

    # ── Flatten hierarchy & classify by PG ────────────────────────────────────
    function Flatten-Hierarchy($node) {
        $items = @($node)
        if ($node.children) { foreach ($c in $node.children) { $items += Flatten-Hierarchy $c } }
        return $items
    }
    $allItems = @(Flatten-Hierarchy $hierarchy)
    $pgMap = Group-ByPG -items $allItems
    $isFallback = $pgMap.Count -eq 0

    # ── Build PR groups ───────────────────────────────────────────────────────
    # ── Resolve branch name from workspace_hint or manual fallback ───────────
    function Resolve-PGBranch([string]$pgName) {
        if ($workspaceHint -and $workspaceHint.pg_branch) {
            $pgNum = if ($pgName -match 'PG-(\d+)') { $Matches[1] } else { '1' }
            return $workspaceHint.pg_branch -replace '\{n\}', $pgNum
        }
        $slug = ($pgName -replace '[^a-zA-Z0-9]+', '-').ToLower()
        $b = "feature/$WorkItemId-$slug"
        if ($b.Length -gt 60) { $b = $b.Substring(0, 60) }
        return $b
    }

    function Resolve-FeatureBranch([string]$title) {
        if ($workspaceHint -and $workspaceHint.feature_branch) {
            return $workspaceHint.feature_branch
        }
        $slug = ($title -replace '[^a-zA-Z0-9]+', '-' -replace '-+$', '').ToLower()
        if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40) -replace '-+$', '' }
        $b = "feature/$WorkItemId-$slug"
        if ($b.Length -gt 60) { $b = $b.Substring(0, 60) }
        return $b
    }

    $prGroups = @()
    if (-not $isFallback) {
        foreach ($pgName in ($pgMap.Keys | Sort-Object { [int]($_ -replace '^PG-(\d+).*', '$1') })) {
            $pg = $pgMap[$pgName]
            $prGroups += [ordered]@{
                name        = $pgName
                task_ids    = @($pg.implementable_ids)
                issue_ids   = @($pg.container_ids)
                branch_name = Resolve-PGBranch $pgName
            }
        }
    } else {
        $prGroups += [ordered]@{
            name        = 'PG-1'
            task_ids    = @($allItems | Where-Object {
                $cap = $_.capabilities
                $cap -contains 'implementable' -and (
                    $cap -notcontains 'plannable' -or
                    (-not $_.children -or $_.children.Count -eq 0)
                )
            } | ForEach-Object { $_.work_item_id })
            issue_ids   = @($allItems | Where-Object {
                $cap = $_.capabilities
                $cap -contains 'plannable' -and (
                    $cap -notcontains 'implementable' -or
                    ($_.children -and $_.children.Count -gt 0)
                )
            } | ForEach-Object { $_.work_item_id })
            branch_name = Resolve-FeatureBranch $hierarchy.title
        }
    }

    # ── Check remote branches ─────────────────────────────────────────────────
    $remoteBranches = @(git branch -r 2>$null) | ForEach-Object { $_.Trim() -replace '^origin/', '' }

    # ── Check merged & open PRs ───────────────────────────────────────────────
    $mergedPRs = @()
    $openPRs = @()
    if ($_ghRepo) {
        $mergedJson = Invoke-GH 'pr','list','--repo',$_ghRepo,'--state','merged','--limit','50','--json','number,headRefName,url'
        if ($mergedJson) { $mergedPRs = @($mergedJson | ConvertFrom-Json) }
        $openJson = Invoke-GH 'pr','list','--repo',$_ghRepo,'--state','open','--limit','50','--json','number,headRefName,url'
        if ($openJson) { $openPRs = @($openJson | ConvertFrom-Json) }
    }

    # ── Determine PG states ───────────────────────────────────────────────────
    $completedPGs = @()
    $currentPG = $null

    foreach ($pg in $prGroups) {
        $branchExists = $remoteBranches -contains $pg.branch_name
        $mergedPR = $mergedPRs | Where-Object { $_.headRefName -eq $pg.branch_name } | Select-Object -First 1
        $openPR = $openPRs | Where-Object { $_.headRefName -eq $pg.branch_name } | Select-Object -First 1

        if ($mergedPR) {
            # Stale-branch defense: verify at least one container item progressed beyond "To Do"
            $stale = $false
            if ($pg.issue_ids.Count -gt 0) {
                $progressedCount = @($allItems | Where-Object {
                    $pg.issue_ids -contains $_.work_item_id -and $_.state -ne 'To Do'
                }).Count
                $stale = $progressedCount -eq 0
            }

            if (-not $stale) {
                $completedPGs += $pg.name
                $pg['completed'] = $true
                $pg['pr_number'] = $mergedPR.number
                $pg['pr_url'] = if ($mergedPR.url) { $mergedPR.url } else { '' }
            } else {
                # Stale branch from prior run — treat as incomplete
                $pg['completed'] = $false
                $pg['pr_number'] = 0
                $pg['pr_url'] = ''
                $pg['action'] = 'create_branch'
                if (-not $currentPG) { $currentPG = $pg }
            }
        } elseif ($openPR) {
            $pg['completed'] = $false
            $pg['pr_number'] = $openPR.number
            $pg['pr_url'] = if ($openPR.url) { $openPR.url } else { '' }
            $pg['action'] = 'submit_pr'
            if (-not $currentPG) { $currentPG = $pg }
        } else {
            # ADO-state-only completion fallback — gh failure recovery
            $allDone = $false
            if ($pg.issue_ids.Count -gt 0) {
                $doneCount = @($allItems | Where-Object {
                    $pg.issue_ids -contains $_.work_item_id -and $_.state -eq 'Done'
                }).Count
                $allDone = $doneCount -eq $pg.issue_ids.Count
            } elseif ($pg.task_ids.Count -gt 0) {
                # Task-only PG: rely on task states
                $doneCount = @($allItems | Where-Object {
                    $pg.task_ids -contains $_.work_item_id -and $_.state -eq 'Done'
                }).Count
                $allDone = $doneCount -eq $pg.task_ids.Count
            }

            if ($allDone) {
                $completedPGs += $pg.name
                $pg['completed'] = $true
                $pg['pr_number'] = 0
                $pg['pr_url'] = ''
            } else {
                # Branch exists or not → create_branch (branch_manager handles rebase)
                $pg['completed'] = $false
                $pg['pr_number'] = 0
                $pg['pr_url'] = ''
                $pg['action'] = 'create_branch'
                if (-not $currentPG) { $currentPG = $pg }
            }
        }
    }

    # ── Build output ──────────────────────────────────────────────────────────
    $remainingPGs = @($prGroups | Where-Object { -not $_.completed } | ForEach-Object { $_.name })

    if ($currentPG) {
        [ordered]@{
            action        = $currentPG.action
            current_pg    = $currentPG.name
            branch_name   = $currentPG.branch_name
            issue_ids     = @($currentPG.issue_ids)
            task_ids      = @($currentPG.task_ids)
            pr_number     = $currentPG.pr_number
            pr_url        = $currentPG.pr_url
            completed_pgs = @($completedPGs)
            remaining_pgs = @($remainingPGs)
            total_pgs     = $prGroups.Count
            ado_workspace = Get-AdoWorkspace
        } | ConvertTo-Json -Depth 3
    } else {
        [ordered]@{
            action        = 'all_complete'
            current_pg    = ''
            branch_name   = ''
            issue_ids     = @()
            task_ids      = @()
            pr_number     = 0
            pr_url        = ''
            completed_pgs = @($completedPGs)
            remaining_pgs = @()
            total_pgs     = $prGroups.Count
            ado_workspace = Get-AdoWorkspace
        } | ConvertTo-Json -Depth 3
    }
} catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
