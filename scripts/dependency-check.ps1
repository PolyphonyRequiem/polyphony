<#
.SYNOPSIS
    Checks ADO predecessor links for blocking dependencies on a work item.
.DESCRIPTION
    Queries ADO predecessor links for the given work item ID and checks
    whether all predecessors are in a terminal state (Done/Removed/Closed).
    Outputs a structured JSON result with status (blocked/not_blocked) and
    a list of blocking items with their details.
.PARAMETER WorkItemId
    ADO work item ID to check predecessor links for.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][int]$WorkItemId)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/ado-helpers.ps1"

try {
    # ── Sync local cache ─────────────────────────────────────────────────────
    twig sync --output json 2>$null | Out-Null

    # ── Fetch work item with relations ───────────────────────────────────────
    $itemJson = twig show $WorkItemId --output json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $itemJson) {
        throw "Failed to fetch work item $WorkItemId"
    }
    $item = $itemJson | ConvertFrom-Json

    # ── Extract predecessor relations ────────────────────────────────────────
    $predecessors = @()
    if ($item.relations) {
        $predecessors = @($item.relations | Where-Object {
            $_.rel -eq 'System.LinkTypes.Dependency-Reverse' -or
            $_.attributes.name -eq 'Predecessor'
        })
    }

    # ── If no predecessors, not blocked ──────────────────────────────────────
    if ($predecessors.Count -eq 0) {
        [ordered]@{
            blocked        = $false
            status         = 'not_blocked'
            work_item_id   = $WorkItemId
            blocking_items = @()
            ready_count    = 0
            total_count    = 0
            message        = 'No predecessor links found'
        } | ConvertTo-Json -Depth 5
        exit 0
    }

    # ── Check each predecessor's state ───────────────────────────────────────
    $terminalStates = @('Done', 'Removed', 'Closed')
    $blockingItems = @()

    foreach ($pred in $predecessors) {
        # Extract predecessor work item ID from the URL or id field
        $predId = $null
        if ($pred.url) {
            $predId = [int]($pred.url -split '/' | Select-Object -Last 1)
        } elseif ($pred.id) {
            $predId = [int]$pred.id
        }

        if (-not $predId) { continue }

        $predJson = twig show $predId --output json 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $predJson) {
            $blockingItems += @{
                id    = $predId
                title = 'Unknown (failed to fetch)'
                state = 'Unknown'
            }
            continue
        }
        $predItem = $predJson | ConvertFrom-Json
        $predState = $predItem.fields.'System.State'
        $predTitle = $predItem.fields.'System.Title'

        if ($predState -notin $terminalStates) {
            $blockingItems += @{
                id    = $predId
                title = if ($predTitle) { $predTitle } else { '' }
                state = if ($predState) { $predState } else { 'Unknown' }
            }
        }
    }

    # ── Emit result ──────────────────────────────────────────────────────────
    $readyCount = $predecessors.Count - $blockingItems.Count

    if ($blockingItems.Count -gt 0) {
        [ordered]@{
            blocked        = $true
            status         = 'blocked'
            work_item_id   = $WorkItemId
            blocking_items = $blockingItems
            ready_count    = $readyCount
            total_count    = $predecessors.Count
            message        = "$($blockingItems.Count) predecessor(s) not in terminal state"
        } | ConvertTo-Json -Depth 5
    } else {
        [ordered]@{
            blocked        = $false
            status         = 'not_blocked'
            work_item_id   = $WorkItemId
            blocking_items = @()
            ready_count    = $predecessors.Count
            total_count    = $predecessors.Count
            message        = "All $($predecessors.Count) predecessor(s) are complete"
        } | ConvertTo-Json -Depth 5
    }
} catch {
    [ordered]@{
        blocked        = $false
        status         = 'not_blocked'
        work_item_id   = $WorkItemId
        blocking_items = @()
        ready_count    = 0
        total_count    = 0
        message        = "Error checking dependencies: $($_.Exception.Message)"
        error          = $true
    } | ConvertTo-Json -Depth 5
}
