<#
.SYNOPSIS
    Loads type-specific context for the plan-level recursive planning workflow.

.DESCRIPTION
    Reads the work item type from twig, then loads the type definition,
    plan template, and decomposition guidance from .conductor/.
    Outputs JSON consumed by the architect agent in plan-level.yaml.

.PARAMETER WorkItemId
    ADO work item ID to load type context for.

.PARAMETER ConfigPath
    Path to the conductor config directory (default: .conductor).
#>
param(
    [Parameter(Mandatory = $true)]
    [int]$WorkItemId,

    [string]$ConfigPath = '.conductor'
)

$ErrorActionPreference = 'Stop'

try {
    # ── Step 1: Get work item type via twig ────────────────────────────────
    $showJson = twig show $WorkItemId --output json 2>$null
    if (-not $showJson) {
        throw "Failed to retrieve work item $WorkItemId from twig"
    }
    $showResult = $showJson | ConvertFrom-Json
    $typeName   = if ($showResult.type) { $showResult.type } else { '' }
    if (-not $typeName) {
        throw "Work item $WorkItemId has no type field"
    }
    $typeSlug = $typeName.ToLower() -replace '\s+', '-'

    # ── Step 2: Read type definition ──────────────────────────────────────
    $definitionPath = Join-Path $ConfigPath "work-item-types/$typeSlug.md"
    if (-not (Test-Path $definitionPath)) {
        throw "Type definition not found: $definitionPath"
    }
    $definition = Get-Content $definitionPath -Raw

    # ── Step 3: Read plan template (optional) ─────────────────────────────
    $templatePath = Join-Path $ConfigPath "work-item-types/templates/$typeSlug-template.md"
    $template = if (Test-Path $templatePath) {
        Get-Content $templatePath -Raw
    } else {
        ''
    }

    # ── Step 4: Read decomposition guidance from process-config.yaml ──────
    $processConfigPath = Join-Path $ConfigPath 'process-config.yaml'
    $decompositionGuidance = ''
    if (Test-Path $processConfigPath) {
        $configContent = Get-Content $processConfigPath -Raw
        # Parse the decomposition_guidance for this type from the YAML.
        # Match the type's section (2-space indent) and all its properties (4-space indent).
        $pattern = "(?m)^  ${typeName}:\s*$\n((?:^    .*\n?)*)"
        if ($configContent -match $pattern) {
            $typeBlock = $Matches[1]
            if ($typeBlock -match '(?ms)decomposition_guidance:\s*\|\s*\n((?:\s{6,}.*\n?)*)') {
                $decompositionGuidance = ($Matches[1] -replace '(?m)^\s{6}', '').Trim()
            }
        }
    }

    # ── Build output ──────────────────────────────────────────────────────
    [ordered]@{
        type                    = $typeName
        definition              = $definition
        template                = $template
        decomposition_guidance  = $decompositionGuidance
    } | ConvertTo-Json -Depth 3
}
catch {
    [ordered]@{
        error = $_.Exception.Message
        type  = ''
    } | ConvertTo-Json -Depth 3
    exit 1
}
