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

try {
    # ── Derive GitHub repo slug ───────────────────────────────────────────────
    $_ghRepo = ''
    $_remoteUrl = (git remote get-url origin 2>$null) ?? ''
    if ($_remoteUrl -match 'github\.com(?:/|:)([^/]+/[^/.]+)') { $_ghRepo = $Matches[1] }

    # ── Sync & fetch hierarchy ────────────────────────────────────────────────
    twig sync --output json 2>$null | Out-Null
    $hierarchy = (polyphony hierarchy --work-item $WorkItemId --depth 3 2>$null) | ConvertFrom-Json

    # ── Flatten hierarchy & classify by PG ────────────────────────────────────
    function Flatten-Hierarchy($node) {
        $items = @($node)
        if ($node.children) { foreach ($c in $node.children) { $items += Flatten-Hierarchy $c } }
        return $items
    }
    $allItems = @(Flatten-Hierarchy $hierarchy)
    $pgMap = Group-ByPG -items $allItems
    $isFallback = $pgMap.Count -eq 0

    # ── Build PG branch name helper ───────────────────────────────────────────
    function New-BranchName([string]$slug) {
        $b = "feature/$WorkItemId-$slug"
        if ($b.Length -gt 60) { $b = $b.Substring(0, 60) }
        $b
    }

    # ── Build PR groups ───────────────────────────────────────────────────────
    $prGroups = @()
    if (-not $isFallback) {
        foreach ($pgName in ($pgMap.Keys | Sort-Object { [int]($_ -replace '^PG-(\d+).*', '$1') })) {
            $pg = $pgMap[$pgName]
            $slug = ($pgName -replace '[^a-zA-Z0-9]+', '-').ToLower()
            $prGroups += [ordered]@{
                name        = $pgName
                task_ids    = @($pg.implementable_ids)
                issue_ids   = @($pg.container_ids)
                branch_name = New-BranchName $slug
            }
        }
    } else {
        $slug = ($hierarchy.title -replace '[^a-zA-Z0-9]+', '-' -replace '-+$', '').ToLower()
        $prGroups += [ordered]@{
            name        = 'PG-1'
            task_ids    = @($allItems | Where-Object { $_.capabilities -contains 'implementable' -and $_.capabilities -notcontains 'plannable' } | ForEach-Object { $_.work_item_id })
            issue_ids   = @($allItems | Where-Object { $_.capabilities -contains 'plannable' } | ForEach-Object { $_.work_item_id })
            branch_name = New-BranchName $slug
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
            $completedPGs += $pg.name
            $pg['completed'] = $true
            $pg['pr_number'] = $mergedPR.number
            $pg['pr_url'] = if ($mergedPR.url) { $mergedPR.url } else { '' }
        } elseif ($openPR) {
            $pg['completed'] = $false
            $pg['pr_number'] = $openPR.number
            $pg['pr_url'] = if ($openPR.url) { $openPR.url } else { '' }
            $pg['action'] = 'submit_pr'
            if (-not $currentPG) { $currentPG = $pg }
        } elseif ($branchExists) {
            $pg['completed'] = $false
            $pg['pr_number'] = 0
            $pg['pr_url'] = ''
            $pg['action'] = 'submit_pr'
            if (-not $currentPG) { $currentPG = $pg }
        } else {
            $pg['completed'] = $false
            $pg['pr_number'] = 0
            $pg['pr_url'] = ''
            $pg['action'] = 'create_branch'
            if (-not $currentPG) { $currentPG = $pg }
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
        } | ConvertTo-Json -Depth 3
    }
} catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
