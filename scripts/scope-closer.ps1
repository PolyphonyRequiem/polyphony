<#
.SYNOPSIS
    Closes all non-Done items in a PG scope using polyphony validate before each transition.
.DESCRIPTION
    Type-agnostic scope closer that discovers items via polyphony hierarchy and Group-ByPG,
    validates each transition with polyphony validate --event implementation_complete,
    and transitions valid items to Done. Items failing validation are recorded in failed_closures.
.PARAMETER WorkItemId
    ADO work item ID (root of the hierarchy).
.PARAMETER PGName
    PR Group name (e.g., PG-1) to scope closure to.
.PARAMETER PRNumber
    Pull request number associated with the PG closure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$WorkItemId,
    [Parameter(Mandatory)][string]$PGName,
    [Parameter()][int]$PRNumber = 0
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/pg-helpers.ps1"

try {
    twig sync --output json 2>$null | Out-Null

    # Load hierarchy and flatten all items
    $hierarchy = (polyphony hierarchy --work-item $WorkItemId --depth 3 2>$null) | ConvertFrom-Json

    function Flatten-Hierarchy($node) {
        $items = @($node)
        if ($node.children) { foreach ($c in $node.children) { $items += Flatten-Hierarchy $c } }
        return $items
    }
    $allItems = @(Flatten-Hierarchy $hierarchy)

    # Discover items belonging to the target PG
    $pgMap = Group-ByPG -items $allItems
    $pgItemIds = @()
    if ($pgMap.Contains($PGName)) {
        $pg = $pgMap[$PGName]
        $pgItemIds = @($pg.implementable_ids) + @($pg.container_ids)
    }
    $pgItems = @($allItems | Where-Object { $pgItemIds -contains $_.work_item_id })

    # Filter to non-Done items
    $nonDoneItems = @($pgItems | Where-Object { $_.state -ne 'Done' })

    $closedItems = @()
    $failedClosures = @()

    foreach ($item in $nonDoneItems) {
        # Validate transition before close (#2647)
        $validateJson = polyphony validate --work-item $item.work_item_id --event implementation_complete 2>$null
        $validateResult = $validateJson | ConvertFrom-Json

        if ($validateResult.is_valid) {
            twig set $item.work_item_id --output json 2>$null | Out-Null
            twig state $validateResult.target_state --output json 2>$null | Out-Null
            $closedItems += [ordered]@{
                id           = $item.work_item_id
                title        = $item.title
                target_state = $validateResult.target_state
            }
        } else {
            $failedClosures += [ordered]@{
                id     = $item.work_item_id
                title  = $item.title
                reason = if ($validateResult.message) { $validateResult.message } else { '' }
            }
        }
    }

    [ordered]@{
        pg_name         = $PGName
        pr_number       = $PRNumber
        closed_items    = @($closedItems)
        failed_closures = @($failedClosures)
        total_closed    = $closedItems.Count
        total_failed    = $failedClosures.Count
    } | ConvertTo-Json -Depth 3
} catch {
    [ordered]@{ error = $_.Exception.Message } | ConvertTo-Json
    exit 1
}
