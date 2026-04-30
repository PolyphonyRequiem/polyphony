<#
.SYNOPSIS
    Shared ADO workspace derivation from twig config.
.DESCRIPTION
    Provides Get-AdoOrg, Get-AdoProject, and Get-AdoWorkspace functions
    that read values from `twig config` at runtime instead of hardcoding.
    Dot-source this file from consuming scripts.
#>

function Get-AdoOrg {
    $json = twig config organization --output json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { return '' }
    $result = $json | ConvertFrom-Json
    if ($result.info) { return $result.info } else { return '' }
}

function Get-AdoProject {
    $json = twig config project --output json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { return '' }
    $result = $json | ConvertFrom-Json
    if ($result.info) { return $result.info } else { return '' }
}

function Get-AdoWorkspace {
    $org = Get-AdoOrg
    $proj = Get-AdoProject
    if ($org -and $proj) { return "$org/$proj" } else { return '' }
}
