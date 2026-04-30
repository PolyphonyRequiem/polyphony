<#
.SYNOPSIS
    Discovers plannable children for a work item and outputs them for
    recursive planning in plan-level.yaml.

.DESCRIPTION
    Deterministic script agent consumed by plan-level.yaml. Calls
    polyphony hierarchy --depth 1 to discover immediate children, then
    filters for those with the 'plannable' capability. Outputs JSON that
    the workflow routes on — has_plannable_children=true routes to
    plan_children_group, false routes to $end.

    Always exits 0 (routing is condition-based, not exit-code-based).

.PARAMETER WorkItemId
    ADO work item ID whose children to discover.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$WorkItemId
)

$ErrorActionPreference = 'Stop'

try {
    # ── Fetch immediate children via polyphony hierarchy ──────────────────
    $hierarchyJson = polyphony hierarchy --work-item $WorkItemId --depth 1 2>$null
    if (-not $hierarchyJson) {
        throw "Failed to retrieve hierarchy for work item $WorkItemId"
    }
    $hierarchy = $hierarchyJson | ConvertFrom-Json

    # ── Filter children with plannable capability ────────────────────────
    $children = @()
    if ($hierarchy.children) {
        $children = @($hierarchy.children | Where-Object {
            $_.capabilities -contains 'plannable'
        })
    }

    $plannableChildren = @($children | ForEach-Object {
        [ordered]@{
            id    = $_.work_item_id
            type  = $_.work_item_type
            title = $_.title
        }
    })

    # ── Build output ─────────────────────────────────────────────────────
    [ordered]@{
        has_plannable_children = $plannableChildren.Count -gt 0
        plannable_children     = $plannableChildren
        parent_id              = $WorkItemId
        count                  = $plannableChildren.Count
    } | ConvertTo-Json -Depth 3
}
catch {
    [ordered]@{
        has_plannable_children = $false
        plannable_children     = @()
        parent_id              = $WorkItemId
        count                  = 0
        error                  = $_.Exception.Message
    } | ConvertTo-Json -Depth 3
}
