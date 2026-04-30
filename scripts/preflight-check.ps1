<#
.SYNOPSIS
    Deterministic preflight check for the twig SDLC apex workflow.
    Validates external dependencies before expensive LLM work begins.

.DESCRIPTION
    Lightweight deterministic check that validates prerequisites:
    git repo state, twig config presence, ADO connectivity, and tool
    availability. Outputs JSON consumed by the preflight_gate Jinja2
    template for human decision rendering.

.PARAMETER WorkItemId
    ADO work item ID to validate access for.
#>
param(
    [Parameter(Mandatory = $true)]
    [int]$WorkItemId
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/ado-helpers.ps1"

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
    $requiredChecks  = @()
    $advisoryChecks  = @()

    # ── Required: Git repository ──────────────────────────────────────────
    $gitTopLevel = git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $gitTopLevel) {
        $requiredChecks += New-CheckResult -Name 'git_repo' -Passed $true `
            -Detail "Git repository found at $gitTopLevel"
    }
    else {
        $requiredChecks += New-CheckResult -Name 'git_repo' -Passed $false `
            -Detail 'Not inside a git repository'
    }

    # ── Required: twig CLI available ──────────────────────────────────────
    $twigVersion = twig --version 2>$null
    if ($LASTEXITCODE -eq 0 -and $twigVersion) {
        $requiredChecks += New-CheckResult -Name 'twig_cli' -Passed $true `
            -Detail "twig CLI available: $twigVersion"
    }
    else {
        $requiredChecks += New-CheckResult -Name 'twig_cli' -Passed $false `
            -Detail 'twig CLI not found or not responding'
    }

    # ── Required: twig config (ADO workspace) ─────────────────────────────
    $_adoOrg     = Get-AdoOrg
    $_adoProject = Get-AdoProject
    if ($_adoOrg -and $_adoProject) {
        $requiredChecks += New-CheckResult -Name 'twig_config' -Passed $true `
            -Detail "ADO workspace: $_adoOrg/$_adoProject"
    }
    else {
        $requiredChecks += New-CheckResult -Name 'twig_config' -Passed $false `
            -Detail 'twig config missing organization or project'
    }

    # ── Required: ADO connectivity (work item accessible) ─────────────────
    $showJson = twig show $WorkItemId --output json 2>$null
    if ($LASTEXITCODE -eq 0 -and $showJson) {
        $showResult = $showJson | ConvertFrom-Json
        $wiTitle = if ($showResult.title) { $showResult.title } else { "ID $WorkItemId" }
        $requiredChecks += New-CheckResult -Name 'ado_access' -Passed $true `
            -Detail "Work item accessible: $wiTitle"
    }
    else {
        $requiredChecks += New-CheckResult -Name 'ado_access' -Passed $false `
            -Detail "Cannot access work item $WorkItemId"
    }

    # ── Advisory: gh CLI authenticated ────────────────────────────────────
    $ghStatus = gh auth status 2>&1
    if ($LASTEXITCODE -eq 0) {
        $advisoryChecks += New-CheckResult -Name 'gh_auth' -Passed $true `
            -Detail 'GitHub CLI authenticated' `
            -Remediation ''
    }
    else {
        $advisoryChecks += New-CheckResult -Name 'gh_auth' -Passed $false `
            -Detail 'GitHub CLI not authenticated' `
            -Remediation 'Run: gh auth login'
    }

    # ── Advisory: polyphony CLI available ─────────────────────────────────
    $polyVersion = polyphony --version 2>$null
    if ($LASTEXITCODE -eq 0 -and $polyVersion) {
        $advisoryChecks += New-CheckResult -Name 'polyphony_cli' -Passed $true `
            -Detail "Polyphony CLI available: $polyVersion" `
            -Remediation ''
    }
    else {
        $advisoryChecks += New-CheckResult -Name 'polyphony_cli' -Passed $false `
            -Detail 'Polyphony CLI not found' `
            -Remediation 'Install polyphony CLI or add to PATH'
    }

    # ── Advisory: dotnet SDK available ────────────────────────────────────
    $dotnetVersion = dotnet --version 2>$null
    if ($LASTEXITCODE -eq 0 -and $dotnetVersion) {
        $advisoryChecks += New-CheckResult -Name 'dotnet_sdk' -Passed $true `
            -Detail "dotnet SDK $dotnetVersion" `
            -Remediation ''
    }
    else {
        $advisoryChecks += New-CheckResult -Name 'dotnet_sdk' -Passed $false `
            -Detail 'dotnet SDK not found' `
            -Remediation 'Install .NET SDK: https://dot.net'
    }

    # ── Aggregate results ─────────────────────────────────────────────────
    $failedCount  = @($requiredChecks | Where-Object { -not $_.passed }).Count
    $warningCount = @($advisoryChecks | Where-Object { -not $_.passed }).Count
    $ready        = $failedCount -eq 0

    $summary = if ($ready -and $warningCount -eq 0) {
        'All preflight checks passed.'
    }
    elseif ($ready) {
        "All required checks passed. $warningCount advisory warning(s)."
    }
    else {
        "$failedCount required check(s) failed. Fix before proceeding."
    }

    # ── Build output ──────────────────────────────────────────────────────
    $output = [ordered]@{
        ready            = $ready
        summary          = $summary
        required_checks  = @($requiredChecks)
        advisory_checks  = @($advisoryChecks)
        failed_count     = $failedCount
        warning_count    = $warningCount
        details          = [ordered]@{
            work_item_id = $WorkItemId
            ado_org      = $_adoOrg
            ado_project  = $_adoProject
        }
    }

    $output | ConvertTo-Json -Depth 4
}
catch {
    [ordered]@{
        ready            = $false
        summary          = "Preflight check error: $($_.Exception.Message)"
        required_checks  = @()
        advisory_checks  = @()
        failed_count     = 1
        warning_count    = 0
        details          = [ordered]@{
            work_item_id = $WorkItemId
            error        = $_.Exception.Message
        }
    } | ConvertTo-Json -Depth 4
    exit 1
}
