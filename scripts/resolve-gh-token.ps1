<#
.SYNOPSIS
    Resolves a GitHub token for API calls.
.DESCRIPTION
    Sets $env:GH_TOKEN from available credential sources.
    Dot-source this script before calling invoke-gh.ps1 or gh CLI.
#>

# Token resolution is a no-op when GH_TOKEN is already set (e.g., CI).
if (-not $env:GH_TOKEN) {
    try { $env:GH_TOKEN = (gh auth token 2>$null) ?? '' }
    catch { $env:GH_TOKEN = '' }
}
