<#
.SYNOPSIS
    Loads agent guidance markdown files and outputs a JSON role map.

.DESCRIPTION
    Reads all .md files from <ConfigPath>/agent-guidance/ and outputs a JSON
    object mapping role names (file basename without extension) to their
    content. Returns an empty JSON object when the directory does not exist,
    providing graceful degradation for repos without agent guidance.

.PARAMETER ConfigPath
    Path to the conductor config directory (default: .conductor).
#>
param(
    [string]$ConfigPath = '.conductor'
)

$ErrorActionPreference = 'Stop'

$guidancePath = Join-Path $ConfigPath 'agent-guidance'

if (-not (Test-Path $guidancePath)) {
    '{}' | Write-Output
    return
}

$roleMap = [ordered]@{}

$mdFiles = @(Get-ChildItem -Path $guidancePath -Filter '*.md' -File | Sort-Object Name)

foreach ($file in $mdFiles) {
    $roleName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $content  = Get-Content $file.FullName -Raw
    if ($null -eq $content) { $content = '' }
    $roleMap[$roleName] = $content
}

$roleMap | ConvertTo-Json -Depth 3
