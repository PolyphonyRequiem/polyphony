<#
.SYNOPSIS
    Shared GitHub repo slug derivation from git remote.
.DESCRIPTION
    Provides Get-RepoSlug function that derives the GitHub owner/repo slug
    from the git remote URL at runtime. Dot-source this file from consuming scripts.
#>

function Get-RepoSlug {
    $remoteUrl = (git remote get-url origin 2>$null) ?? ''
    if ($remoteUrl -match 'github\.com(?:/|:)([^/]+/[^/.]+)') {
        return $Matches[1]
    }
    return ''
}
