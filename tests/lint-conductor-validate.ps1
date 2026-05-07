<#
.SYNOPSIS
    CI lint — runs conductor validate on all workflow YAMLs.
.DESCRIPTION
    Iterates over all .yaml files in workflows/ and runs `conductor validate`
    on each. Reports per-YAML PASS/FAIL status and exits 0 if all pass,
    1 if any fail. Skips gracefully if conductor CLI or workflows/ are not found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$workflowsDir = Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'workflows'

if (-not (Test-Path $workflowsDir)) {
    Write-Host "FAIL: workflows directory not found at $workflowsDir" -ForegroundColor Red
    Write-Host '  This lint expects .conductor/registry/workflows/ to exist relative to the repo root.' -ForegroundColor Yellow
    Write-Host '  If the registry layout has moved, update $workflowsDir in this script.' -ForegroundColor Yellow
    exit 1
}

$yamlFiles = @(Get-ChildItem $workflowsDir -Filter '*.yaml' -File)

if ($yamlFiles.Count -eq 0) {
    Write-Host "FAIL: No .yaml files found in $workflowsDir" -ForegroundColor Red
    Write-Host '  An empty workflows directory is treated as a regression (silent SKIP would mask real bugs).' -ForegroundColor Yellow
    exit 1
}

$conductorCmd = Get-Command conductor -ErrorAction SilentlyContinue
if (-not $conductorCmd) {
    Write-Host 'SKIP: conductor command not found (install conductor CLI or set PATH)' -ForegroundColor Yellow
    exit 0
}

$failed = @()
$passed = @()

foreach ($yaml in $yamlFiles) {
    $validateOutput = $null
    $ErrorActionPreference = 'Continue'
    $validateOutput = & conductor validate $yaml.FullName 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    if ($exitCode -ne 0) {
        $failed += $yaml.Name
        Write-Host "FAIL: $($yaml.Name)" -ForegroundColor Red
        if ($validateOutput) {
            foreach ($line in $validateOutput) {
                Write-Host "  $line" -ForegroundColor Yellow
            }
        }
    } else {
        $passed += $yaml.Name
        Write-Host "PASS: $($yaml.Name)" -ForegroundColor Green
    }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "FAIL: $($failed.Count)/$($yamlFiles.Count) workflow YAML(s) failed validation" -ForegroundColor Red
    foreach ($f in $failed) {
        Write-Host "  - $f" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "PASS: All $($yamlFiles.Count) workflow YAMLs validated successfully" -ForegroundColor Green
exit 0
