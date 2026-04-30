<#
.SYNOPSIS
    Shared PG tag discovery and capability-based item classification.
.DESCRIPTION
    Provides Get-PGTag and Group-ByPG functions used by load-work-tree.ps1,
    pg-router.ps1, task-router.ps1, and scope-closer.ps1.
    Dot-source this file from consuming scripts.
#>

function Get-PGTag([string]$Tags) {
    if (-not $Tags) { return $null }
    ($Tags -split ';\s*') | Where-Object { $_.Trim() -match '^PG-\d+' } |
        Select-Object -First 1 | ForEach-Object { $_.Trim() }
}

function Group-ByPG($items) {
    $pgMap = [ordered]@{}
    foreach ($item in $items) {
        $pgTag = Get-PGTag -Tags $item.tags
        if (-not $pgTag) { continue }
        if (-not $pgMap.Contains($pgTag)) {
            $pgMap[$pgTag] = @{ implementable_ids = @(); container_ids = @() }
        }
        $isImplementable = $item.capabilities -contains 'implementable'
        $isContainer = $item.capabilities -contains 'plannable'
        if ($isImplementable -and -not $isContainer) {
            $pgMap[$pgTag].implementable_ids += $item.work_item_id
        } else {
            $pgMap[$pgTag].container_ids += $item.work_item_id
        }
    }
    return $pgMap
}
