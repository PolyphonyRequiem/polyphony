<#
.SYNOPSIS
    CI lint — ensures all dot-sourced paths in scripts/ use $PSScriptRoot-relative references
    and no hardcoded absolute paths exist.
.DESCRIPTION
    Scans production PowerShell scripts under scripts/ for:
    1. Dot-source statements (". ...") that do NOT use $PSScriptRoot
    2. Hardcoded absolute paths (drive letters, UNC, Unix absolute)
    Absolute paths break portability when deployed to a different machine or user home directory.
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$scriptsDir = Join-Path $PSScriptRoot '..' 'scripts'

# Scan production scripts only — exclude .Tests.ps1 fixtures
$files = @(Get-ChildItem $scriptsDir -Filter '*.ps1' -Recurse |
    Where-Object { $_.Name -notmatch '\.Tests\.ps1$' })

if ($files.Count -eq 0) {
    Write-Host 'No production .ps1 files found in scripts/' -ForegroundColor Yellow
    exit 0
}

$violations = @()

foreach ($file in $files) {
    $lines = @(Get-Content $file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $lineNum = $i + 1

        # Skip comment-only lines
        if ($line.TrimStart() -match '^\s*#') { continue }

        # Check 1: Dot-source without $PSScriptRoot
        # Match `. "path"` or `. 'path'` patterns that don't reference $PSScriptRoot
        if ($line -match '^\s*\.\s+[''"]([^''"]+)[''"]' -or $line -match '^\s*\.\s+(\S+)') {
            $sourcePath = $Matches[1]
            # Only flag if it looks like a file path (has .ps1 extension) and doesn't use $PSScriptRoot
            if ($sourcePath -match '\.ps1' -and $sourcePath -notmatch '\$PSScriptRoot') {
                $violations += [PSCustomObject]@{
                    File    = $file.FullName
                    Line    = $lineNum
                    Rule    = 'dot-source-missing-psscriptroot'
                    Content = $line.Trim()
                }
            }
        }

        # Check 2: Hardcoded absolute paths (drive letters, UNC, Unix absolute)
        # Pattern: C:\Users, D:\config, \\server\, /home/, /Users/
        # Require 2+ letters after :\ to avoid false positives on regex escapes (\d, \s, \w)
        if ($line -match '[A-Za-z]:\\[A-Za-z]{2,}' -or
            $line -match '\\\\[A-Za-z]' -or
            $line -match '(?<![A-Za-z])/(?:home|Users|usr|opt|var|tmp)/') {
            $violations += [PSCustomObject]@{
                File    = $file.FullName
                Line    = $lineNum
                Rule    = 'hardcoded-absolute-path'
                Content = $line.Trim()
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) path violation(s) in scripts/ (breaks portability)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        $rel = $v.File.Replace((Resolve-Path $scriptsDir).Path, 'scripts')
        Write-Host "  ${rel}:$($v.Line) [$($v.Rule)]: $($v.Content)" -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'Fix: Use $PSScriptRoot-relative paths for all dot-source and file references.' -ForegroundColor Cyan
    exit 1
}

Write-Host "PASS: All paths are `$PSScriptRoot-relative ($($files.Count) files scanned)" -ForegroundColor Green
exit 0
