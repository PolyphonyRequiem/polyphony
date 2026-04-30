<#
.SYNOPSIS
    Validates recursion depth against a configurable maximum for the
    plan-level recursive planning workflow.

.DESCRIPTION
    Deterministic depth guard consumed as a type: script agent in
    plan-level.yaml.  Outputs JSON with an allowed flag that the
    workflow routes on — always exits 0 (routing is condition-based,
    not exit-code-based).

.PARAMETER Depth
    Current recursion depth (0 = root level).

.PARAMETER MaxDepth
    Maximum allowed recursion depth (default 6).
#>
param(
    [Parameter(Mandatory = $true)]
    [int]$Depth,

    [int]$MaxDepth = 6
)

$ErrorActionPreference = 'Stop'

$allowed   = $Depth -lt $MaxDepth
$remaining = if ($allowed) { $MaxDepth - $Depth } else { 0 }
$message   = if ($allowed) {
    "Depth $Depth is within limit (max $MaxDepth). $remaining level(s) remaining."
} else {
    "Recursion depth $Depth reached maximum $MaxDepth"
}

[ordered]@{
    allowed   = $allowed
    depth     = $Depth
    max_depth = $MaxDepth
    remaining = $remaining
    message   = $message
} | ConvertTo-Json -Depth 3
