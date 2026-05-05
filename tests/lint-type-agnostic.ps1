<#
.SYNOPSIS
    CI lint — ensures scripts/ contains zero hardcoded work-item type-name literals.
.DESCRIPTION
    Scans production PowerShell scripts under scripts/ for ADO work-item type names
    (Epic, Issue, Task, User Story, Bug, Feature) used as literals in code.
    Type-name literals violate P5 (type-agnostic workflow structure).
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$scriptsDir = Join-Path $PSScriptRoot '..' 'scripts'

# ADO work-item type names that must not appear as literals in production scripts.
$typeNames = @('Epic', 'Issue', 'Task', 'User Story', 'Bug', 'Feature')
$typePattern = '\b(' + ($typeNames -join '|') + ')\b'

# Scan production scripts only — exclude .Tests.ps1 fixtures
$files = @(Get-ChildItem $scriptsDir -Filter '*.ps1' -Recurse |
    Where-Object { $_.Name -notmatch '\.Tests\.ps1$' })

if ($files.Count -eq 0) {
    Write-Host 'No production .ps1 files found in scripts/' -ForegroundColor Yellow
    exit 0
}

# Case-sensitive Select-String; filter out pure comment lines
$violations = @($files |
    Select-String -Pattern $typePattern -CaseSensitive |
    Where-Object { $_.Line.TrimStart() -match '^[^#]' })

if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) type-name literal(s) in scripts/ (violates P5)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        $rel = $v.Path.Replace((Resolve-Path $scriptsDir).Path, 'scripts')
        Write-Host "  ${rel}:$($v.LineNumber): $($v.Line.Trim())" -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'Fix: Replace type-name literals with polyphony facet/hierarchy queries.' -ForegroundColor Cyan
    exit 1
}

Write-Host "PASS: No type-name literals found ($($files.Count) files scanned)" -ForegroundColor Green
exit 0

