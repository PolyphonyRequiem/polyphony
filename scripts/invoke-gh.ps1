<#
.SYNOPSIS
    Wrapper for GitHub CLI (gh) calls with error handling.
.DESCRIPTION
    Provides Invoke-GH function for consistent gh CLI invocation.
    Dot-source this script from consuming scripts.
#>

function Invoke-GH {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    $result = & gh @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "gh command failed with exit code $LASTEXITCODE"
        return $null
    }
    return $result
}
