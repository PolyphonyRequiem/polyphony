<#
.SYNOPSIS
    Closes ADO issues for a PR group after its PR is merged.

.DESCRIPTION
    Deterministic issue closure script. Finds all issues belonging to the
    specified PG (via tags on issues or their child tasks) and transitions
    them to Done with a merge note.

.PARAMETER WorkItemId
    The Epic ADO work item ID.

.PARAMETER PGName
    The PR group name (e.g., "PG-1").

.PARAMETER PRNumber
    The merged PR number (included in closure notes).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    [Parameter(Mandatory)][string]$PGName,
    [int]$PRNumber = 0
)

$ErrorActionPreference = 'Stop'

try {
    # ── Sync and load tree ────────────────────────────────────────────
    twig sync --output json *>$null
    twig set $WorkItemId --output json *>$null
    $tree = (twig tree --depth 2 --output json 2>$null) | ConvertFrom-Json
    $children = $tree.children

    # ── Find issues for this PG ───────────────────────────────────────
    $pgIssueIds = @()

    # First: check issue-level PG tags
    foreach ($child in $children) {
        if (-not $child.tags) { continue }
        $tags = ($child.tags -split ';\s*') | ForEach-Object { $_.Trim() }
        if ($PGName -in $tags) {
            $pgIssueIds += $child.id
        }
    }

    # Fallback: if no PG tags on issues, infer from child task tags
    if ($pgIssueIds.Count -eq 0) {
        foreach ($child in $children) {
            $issueTasks = $child.children
            if (-not $issueTasks -or $issueTasks.Count -eq 0) {
                twig set $child.id --output json *>$null
                $issueTasks = ((twig tree --depth 1 --output json 2>$null) | ConvertFrom-Json).children
            }
            if ($issueTasks) {
                $hasPGTask = $issueTasks | Where-Object {
                    $_.tags -and ($_.tags -split ';\s*' | ForEach-Object { $_.Trim() }) -contains $PGName
                }
                if ($hasPGTask) { $pgIssueIds += $child.id }
            }
        }
    }

    # ── Close each issue ──────────────────────────────────────────────
    $closed = @()
    $failed = @()

    foreach ($issueId in $pgIssueIds) {
        try {
            twig set $issueId --output json *>$null
            $noteText = "Done: closed after PR #$PRNumber merged to main ($PGName)"
            twig note --text $noteText --output json *>$null 2>&1
            twig state Done --output json *>$null 2>&1
            $closed += $issueId
        }
        catch {
            $failed += [ordered]@{ id = $issueId; error = $_.Exception.Message }
        }
    }

    # Restore focus to Epic
    twig set $WorkItemId --output json *>$null

    [ordered]@{
        closed_issues = $closed
        failed_closures = $failed
        pg_name = $PGName
        pr_number = $PRNumber
    } | ConvertTo-Json -Depth 3
}
catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
