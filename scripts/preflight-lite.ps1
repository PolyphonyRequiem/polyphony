<#
.SYNOPSIS
    Lightweight preflight check for the twig SDLC planning sub-workflow.
    Runs 3 quick validations before expensive planning work begins.

.DESCRIPTION
    Checks that the minimum required tools and configuration are present
    for planning: git repository, twig CLI, and polyphony CLI. Does NOT
    check gh auth, .NET SDK, or ADO connectivity — those are validated by
    the full preflight-check.ps1 used in the apex workflow.

    Outputs JSON consumed by the preflight_lite_gate Jinja2 template.
#>
param()

$ErrorActionPreference = 'Stop'

function New-CheckResult {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail,
        [string]$Remediation = ''
    )
    $result = [ordered]@{
        name   = $Name
        passed = $Passed
        detail = $Detail
    }
    if ($Remediation) {
        $result['remediation'] = $Remediation
    }
    return $result
}

try {
    $checks = @()

    # ── Check 1: Git repository ───────────────────────────────────────────
    $gitTopLevel = git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $gitTopLevel) {
        $checks += New-CheckResult -Name 'git_repo' -Passed $true `
            -Detail "Git repository found at $gitTopLevel"
    }
    else {
        $checks += New-CheckResult -Name 'git_repo' -Passed $false `
            -Detail 'Not inside a git repository' `
            -Remediation 'Run this workflow from within a git repository'
    }

    # ── Check 2: twig CLI available ───────────────────────────────────────
    $twigVersion = twig --version 2>$null
    if ($LASTEXITCODE -eq 0 -and $twigVersion) {
        $checks += New-CheckResult -Name 'twig_cli' -Passed $true `
            -Detail "twig CLI available: $twigVersion"
    }
    else {
        $checks += New-CheckResult -Name 'twig_cli' -Passed $false `
            -Detail 'twig CLI not found or not responding' `
            -Remediation 'Install twig CLI and ensure it is in PATH'
    }

    # ── Check 3: polyphony CLI available ──────────────────────────────────
    $polyVersion = polyphony --version 2>$null
    if ($LASTEXITCODE -eq 0 -and $polyVersion) {
        $checks += New-CheckResult -Name 'polyphony_cli' -Passed $true `
            -Detail "Polyphony CLI available: $polyVersion"
    }
    else {
        $checks += New-CheckResult -Name 'polyphony_cli' -Passed $false `
            -Detail 'Polyphony CLI not found' `
            -Remediation 'Install polyphony CLI or add to PATH'
    }

    # ── Aggregate results ─────────────────────────────────────────────────
    $failedCount = @($checks | Where-Object { -not $_.passed }).Count
    $ready       = $failedCount -eq 0

    $summary = if ($ready) {
        'All preflight lite checks passed.'
    }
    else {
        "$failedCount required check(s) failed. Fix before proceeding."
    }

    [ordered]@{
        ready        = $ready
        summary      = $summary
        checks       = @($checks)
        failed_count = $failedCount
    } | ConvertTo-Json -Depth 4
}
catch {
    [ordered]@{
        ready        = $false
        summary      = "Preflight lite error: $($_.Exception.Message)"
        checks       = @()
        failed_count = 1
    } | ConvertTo-Json -Depth 4
    exit 1
}
