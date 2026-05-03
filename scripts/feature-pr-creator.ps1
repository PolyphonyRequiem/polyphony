<#
.SYNOPSIS
    Creates a feature PR from the feature branch to the target branch.
.DESCRIPTION
    Deterministic script for feature-pr.yaml. Uses gh CLI to create a pull
    request from the feature branch (containing all merged PG work) to the
    target branch (e.g., main). Reads workspace_hint from polyphony route
    to confirm branch names match. If an open PR already exists for the
    same head/base pair, reuses it instead of creating a duplicate.
.PARAMETER WorkItemId
    ADO work item ID of the root (Epic/Feature-level) item.
.PARAMETER FeatureBranch
    Feature branch containing all merged PG work.
.PARAMETER TargetBranch
    Target branch the feature PR merges into (e.g., main).
.PARAMETER Title
    PR title. When omitted, auto-generated from the work item tree.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    [Parameter(Mandatory)][string]$FeatureBranch,
    [Parameter(Mandatory)][string]$TargetBranch,
    [Parameter()][string]$Title = ''
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/io-helpers.ps1"
. "$PSScriptRoot/resolve-gh-token.ps1"
. "$PSScriptRoot/invoke-gh.ps1"
. "$PSScriptRoot/lib/gh-helpers.ps1"

try {
    # ── Validate branches via workspace_hint ───────────────────────────────────
    try {
        $routeJson = polyphony route --work-item $WorkItemId 2>$null
        if ($routeJson) {
            $routeResult = $routeJson | ConvertFrom-Json
            $hint = $routeResult.workspace_hint
            if ($hint -and $hint.feature_branch) {
                if ($hint.feature_branch -ne $FeatureBranch) {
                    Write-StderrWarning "workspace_hint feature_branch '$($hint.feature_branch)' differs from supplied FeatureBranch '$FeatureBranch'"
                }
            }
        }
    } catch { <# polyphony may not be available in all environments #> }

    # ── Verify feature branch exists on remote ─────────────────────────────────
    $remoteBranches = @(git ls-remote --heads origin "refs/heads/$FeatureBranch" 2>$null)
    if ($remoteBranches.Count -eq 0) {
        throw "Feature branch '$FeatureBranch' does not exist on remote"
    }

    # ── Get repo slug ──────────────────────────────────────────────────────────
    $_ghRepo = Get-RepoSlug

    # ── Resolve PR title ───────────────────────────────────────────────────────
    $prTitle = $Title
    if (-not $prTitle) {
        $prTitle = "feat: deliver work item #$WorkItemId AB#$WorkItemId"
        $treeOutput = $null
        try {
            $treeOutput = twig show $WorkItemId --tree --output json 2>$null
        } catch { <# twig may not be available in all environments #> }
        if ($treeOutput) {
            $tree = $treeOutput | ConvertFrom-Json
            if ($tree.title) {
                $prTitle = "feat: $($tree.title) AB#$WorkItemId"
            }
        }
    }

    # ── Build PR body ──────────────────────────────────────────────────────────
    $body = "## Feature PR for Work Item #$WorkItemId`n`n"
    $body += "Delivers all PR group work from ``$FeatureBranch`` into ``$TargetBranch``.`n"

    $treeOutput = $null
    try {
        $treeOutput = twig show $WorkItemId --tree --output json 2>$null
    } catch { <# twig may not be available in all environments #> }

    if ($treeOutput) {
        $body += "`n### Work Item Hierarchy`n`n"
        $body += "``````json`n$treeOutput`n```````n"
    }

    # ── Check for existing open PR ─────────────────────────────────────────────
    $existingJson = Invoke-GH 'pr', 'list', '--repo', $_ghRepo, `
        '--head', $FeatureBranch, '--base', $TargetBranch, `
        '--state', 'open', '--json', 'number,url', '--limit', '1'
    if ($existingJson) {
        $existing = @($existingJson | ConvertFrom-Json)
        if ($existing.Count -gt 0) {
            [ordered]@{
                pr_number           = $existing[0].number
                pr_url              = if ($existing[0].url) { $existing[0].url } else { '' }
                title               = $prTitle
                description_summary = 'Reusing existing open feature PR'
                created             = $false
            } | ConvertTo-Json
            exit 0
        }
    }

    # ── Create the feature PR ──────────────────────────────────────────────────
    $prUrl = Invoke-GH 'pr', 'create', '--repo', $_ghRepo, `
        '--base', $TargetBranch, '--head', $FeatureBranch, `
        '--title', $prTitle, '--body', $body

    if (-not $prUrl) {
        throw "gh pr create failed — no URL returned"
    }

    # ── Extract PR number from URL ─────────────────────────────────────────────
    $prNumber = 0
    if ($prUrl -match '/pull/(\d+)') { $prNumber = [int]$Matches[1] }

    [ordered]@{
        pr_number           = $prNumber
        pr_url              = $prUrl.Trim()
        title               = $prTitle
        description_summary = "Feature PR created: $FeatureBranch -> $TargetBranch"
        created             = $true
    } | ConvertTo-Json

} catch {
    [ordered]@{
        pr_number           = 0
        pr_url              = ''
        title               = ''
        description_summary = "Error: $($_.Exception.Message)"
        created             = $false
        error               = $_.Exception.Message
    } | ConvertTo-Json
    exit 1
}
