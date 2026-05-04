<#
.SYNOPSIS
    Generates stub .conductor/ files for repos onboarding to the v2 SDLC workflow.

.DESCRIPTION
    Auto-detects the process template from .twig/config when present, falling
    back to an explicit -ProcessTemplate parameter. Generates stub files for
    process-config.yaml, work-item-type definitions, templates, agent-guidance
    (architect, coder, reviewer), and profile.yaml.

    Existing files are skipped by default (with a warning) unless -Force is set.

.PARAMETER ProcessTemplate
    Process template name (Basic, Agile, Scrum, CMMI). Required when .twig/config
    is absent or does not contain a process_template field.

.PARAMETER Force
    Overwrite existing files. Default: skip existing files with a warning.

.PARAMETER OutputPath
    Target directory for .conductor/ output. Default: current directory.
#>
param(
    [string]$ProcessTemplate = '',

    [switch]$Force,

    [string]$OutputPath = '.'
)

$ErrorActionPreference = 'Stop'

# ── Process template type registries ─────────────────────────────────────────
# Each template maps to its ordered type list. The hierarchy is always:
#   [0] = top-level plannable, [1] = mid-level plannable+implementable, [2] = leaf implementable
$script:TemplateTypes = @{
    'Basic' = @('Epic', 'Issue', 'Task')
    'Agile' = @('Epic', 'User Story', 'Task')
    'Scrum'  = @('Epic', 'Product Backlog Item', 'Task')
    'CMMI'  = @('Epic', 'Requirement', 'Task')
}

# State mappings per template for transitions.
# 'removed' is omitted for Basic, which has no Removed state (state set: To Do, Doing, Done).
$script:TemplateTransitions = @{
    'Basic' = @{
        active = 'Doing'
        done   = 'Done'
    }
    'Agile' = @{
        active  = 'Active'
        done    = 'Closed'
        removed = 'Removed'
    }
    'Scrum' = @{
        active     = 'In Progress'
        done       = 'Done'
        mid_active = 'Committed'
        removed    = 'Removed'
    }
    'CMMI' = @{
        active  = 'Active'
        done    = 'Closed'
        removed = 'Removed'
    }
}

# ── Helper: detect process template from .twig/config ────────────────────────
function Get-ProcessTemplateFromConfig {
    param([string]$BasePath)

    $configPath = Join-Path $BasePath '.twig' 'config'
    if (-not (Test-Path $configPath)) {
        return $null
    }

    $content = Get-Content $configPath -Raw
    if ($content -match '(?m)^\s*process_template\s*[:=]\s*(.+?)\s*$') {
        return $Matches[1].Trim()
    }

    return $null
}

# ── Helper: write file with skip/force logic ─────────────────────────────────
function Write-StubFile {
    param(
        [string]$FilePath,
        [string]$Content,
        [bool]$ForceOverwrite
    )

    if ((Test-Path $FilePath) -and -not $ForceOverwrite) {
        Write-Warning "Skipping existing file: $FilePath"
        return $false
    }

    $dir = Split-Path $FilePath -Parent
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    Set-Content -Path $FilePath -Value $Content -NoNewline
    return $true
}

# ── Helper: get type slug (lowercase, spaces to hyphens) ─────────────────────
function Get-TypeSlug {
    param([string]$TypeName)
    return ($TypeName.ToLower() -replace '\s+', '-')
}

# ── Generator: process-config.yaml ───────────────────────────────────────────
function New-ProcessConfigYaml {
    param(
        [string]$Template,
        [string[]]$Types
    )

    $transitions = $script:TemplateTransitions[$Template]
    $activeState  = $transitions.active
    $doneState    = $transitions.done
    $removedState = if ($transitions.ContainsKey('removed')) { $transitions.removed } else { $null }

    $lines = @()
    $lines += "process_template: $Template"
    $lines += ''
    $lines += 'types:'

    for ($i = 0; $i -lt $Types.Count; $i++) {
        $type = $Types[$i]
        $lines += "  $($type):"

        if ($i -eq 0) {
            # Top-level: plannable only
            $lines += '    capabilities: [plannable]'
            $lines += '    filing_eligible: false'
            $lines += '    max_nesting_depth: 1'
            $lines += '    decomposition_guidance: |'
            $lines += "      Always decompose into $($Types[1])s. $($Types[0])s are never implemented directly."
        }
        elseif ($i -eq ($Types.Count - 1)) {
            # Leaf: implementable only
            $lines += '    capabilities: [implementable]'
            $lines += '    filing_eligible: true'
        }
        else {
            # Mid-level: plannable + implementable
            $lines += '    capabilities: [plannable, implementable]'
            $lines += '    filing_eligible: true'
            $lines += '    max_nesting_depth: 1'
            $lines += '    decomposition_guidance: |'
            $lines += "      Decompose into $($Types[$i + 1])s when scope exceeds a single PG."
            $lines += '      Implement directly when the change is focused and fits one PG.'
        }
    }

    $lines += ''
    $lines += 'transitions:'

    for ($i = 0; $i -lt $Types.Count; $i++) {
        $type = $Types[$i]
        $lines += "  $($type):"

        if ($i -eq 0) {
            # Top-level
            $lines += "    begin_planning: $activeState"
            $lines += "    all_children_complete: $doneState"
            if ($removedState) { $lines += "    scope_removed: $removedState" }
        }
        elseif ($i -eq ($Types.Count - 1)) {
            # Leaf
            $taskActive = if ($transitions.ContainsKey('mid_active')) { $activeState } else { $activeState }
            $lines += "    begin_implementation: $activeState"
            $lines += "    implementation_complete: $doneState"
            if ($removedState) { $lines += "    scope_removed: $removedState" }
        }
        else {
            # Mid-level
            $midActive = if ($transitions.ContainsKey('mid_active')) { $transitions.mid_active } else { $activeState }
            $lines += "    begin_planning: $midActive"
            $lines += "    begin_implementation: $midActive"
            $lines += "    implementation_complete: $doneState"
            if ($removedState) { $lines += "    scope_removed: $removedState" }
        }
    }

    $lines += ''
    $lines += 'branch_strategy:'
    $lines += '  feature_branch: "feature/{root_id}-{slug}"'
    $lines += '  planning_branch: "planning/{root_id}"'
    $lines += '  pg_branch: "pg-{n}/{root_id}-{slug}"'
    $lines += '  target: main'
    $lines += ''
    $lines += 'platform: github'
    $lines += ''

    return ($lines -join "`n")
}

# ── Generator: type definition markdown ──────────────────────────────────────
function New-TypeDefinition {
    param(
        [string]$TypeName,
        [string]$Template
    )

    $lines = @()
    $lines += "# $TypeName `u{2014} Work Item Type Definition ($Template Process)"
    $lines += ''
    $lines += '## Definition'
    $lines += ''
    $lines += "<!-- TODO: Describe what a $TypeName represents in your project -->"
    $lines += ''
    $lines += '## Purpose'
    $lines += ''
    $lines += "<!-- TODO: What question does a $TypeName answer? -->"
    $lines += ''
    $lines += '## Audience'
    $lines += ''
    $lines += '| Role | Usage |'
    $lines += '|------|-------|'
    $lines += "| **Project Owner** | <!-- TODO --> |"
    $lines += "| **Contributor** | <!-- TODO --> |"
    $lines += "| **AI Agent** | <!-- TODO --> |"
    $lines += ''
    $lines += '## Naming Conventions'
    $lines += ''
    $lines += "<!-- TODO: Define naming rules for $TypeName items -->"
    $lines += ''
    $lines += '## Description Template'
    $lines += ''
    $slug = Get-TypeSlug $TypeName
    $lines += "See: ``templates/$slug-template.md``"
    $lines += ''

    return ($lines -join "`n")
}

# ── Generator: type template markdown ────────────────────────────────────────
function New-TypeTemplate {
    param(
        [string]$TypeName
    )

    $lines = @()
    $lines += "## Summary"
    $lines += "<!-- TODO: 2-3 sentence summary of this $TypeName -->"
    $lines += ''
    $lines += '## Acceptance Criteria'
    $lines += "- [ ] <!-- TODO: Add acceptance criteria -->"
    $lines += '- [ ] Build passes with zero errors and warnings'
    $lines += '- [ ] All existing tests pass; new tests cover changed behavior'
    $lines += ''
    $lines += '## Context (optional)'
    $lines += '<!-- TODO: Dependencies, gotchas, related code paths -->'
    $lines += ''

    return ($lines -join "`n")
}

# ── Generator: agent guidance markdown ───────────────────────────────────────
function New-AgentGuidance {
    param(
        [string]$RoleName
    )

    $lines = @()
    $lines += "# $RoleName Guidance"
    $lines += ''
    $lines += "<!-- TODO: Define guidance for the $($RoleName.ToLower()) agent role -->"
    $lines += ''
    $lines += '## Responsibilities'
    $lines += ''
    $lines += "<!-- TODO: List the $($RoleName.ToLower()) agent's key responsibilities -->"
    $lines += ''
    $lines += '## Conventions'
    $lines += ''
    $lines += "<!-- TODO: Project-specific conventions the $($RoleName.ToLower()) should follow -->"
    $lines += ''

    return ($lines -join "`n")
}

# ── Generator: profile.yaml ─────────────────────────────────────────────────
function New-ProfileYaml {
    $lines = @()
    $lines += '# Project profile for conductor SDLC workflows'
    $lines += ''
    $lines += 'project:'
    $lines += '  name: <!-- TODO: Project name -->'
    $lines += '  description: >'
    $lines += '    <!-- TODO: Brief project description -->'
    $lines += '  repository: <!-- TODO: org/repo -->'
    $lines += ''
    $lines += 'tech_stack:'
    $lines += '  language: <!-- TODO: Primary language -->'
    $lines += '  framework: <!-- TODO: Framework -->'
    $lines += '  testing: <!-- TODO: Test framework -->'
    $lines += ''
    $lines += 'build:'
    $lines += '  restore: <!-- TODO: restore command -->'
    $lines += '  build: <!-- TODO: build command -->'
    $lines += '  test: <!-- TODO: test command -->'
    $lines += ''
    $lines += 'conventions:'
    $lines += '  - <!-- TODO: Add project conventions -->'
    $lines += ''

    return ($lines -join "`n")
}

# ══════════════════════════════════════════════════════════════════════════════
# ── Main ─────────────────────────────────────────────────────────────────────
# ══════════════════════════════════════════════════════════════════════════════

# Resolve process template
$detectedTemplate = Get-ProcessTemplateFromConfig -BasePath $OutputPath

if ($detectedTemplate) {
    if ($ProcessTemplate -and $ProcessTemplate -ne $detectedTemplate) {
        Write-Warning "Detected template '$detectedTemplate' from .twig/config differs from specified '$ProcessTemplate'. Using detected: '$detectedTemplate'."
    }
    $resolvedTemplate = $detectedTemplate
}
elseif ($ProcessTemplate) {
    $resolvedTemplate = $ProcessTemplate
}
else {
    Write-Error "No process template detected. Provide -ProcessTemplate or ensure .twig/config contains process_template."
    exit 1
}

# Validate template name
if (-not $script:TemplateTypes.ContainsKey($resolvedTemplate)) {
    $valid = ($script:TemplateTypes.Keys | Sort-Object) -join ', '
    Write-Error "Unknown process template '$resolvedTemplate'. Valid templates: $valid"
    exit 1
}

$types = $script:TemplateTypes[$resolvedTemplate]
$conductorPath = Join-Path $OutputPath '.conductor'
$forceFlag = [bool]$Force

$filesWritten = @()
$filesSkipped = @()

# 1. process-config.yaml
$pcPath = Join-Path $conductorPath 'process-config.yaml'
$pcContent = New-ProcessConfigYaml -Template $resolvedTemplate -Types $types
if (Write-StubFile -FilePath $pcPath -Content $pcContent -ForceOverwrite $forceFlag) {
    $filesWritten += $pcPath
} else {
    $filesSkipped += $pcPath
}

# 2. Work item type definitions + templates
foreach ($type in $types) {
    $slug = Get-TypeSlug $type

    $defPath = Join-Path $conductorPath "work-item-types/$slug.md"
    $defContent = New-TypeDefinition -TypeName $type -Template $resolvedTemplate
    if (Write-StubFile -FilePath $defPath -Content $defContent -ForceOverwrite $forceFlag) {
        $filesWritten += $defPath
    } else {
        $filesSkipped += $defPath
    }

    $tplPath = Join-Path $conductorPath "work-item-types/templates/$slug-template.md"
    $tplContent = New-TypeTemplate -TypeName $type
    if (Write-StubFile -FilePath $tplPath -Content $tplContent -ForceOverwrite $forceFlag) {
        $filesWritten += $tplPath
    } else {
        $filesSkipped += $tplPath
    }
}

# 3. Agent guidance files (type-neutral)
foreach ($type in $types) {
    $slug = Get-TypeSlug $type
    $guidancePath = Join-Path $conductorPath "agent-guidance/$slug.md"
    $guidanceContent = New-AgentGuidance -RoleName $type
    if (Write-StubFile -FilePath $guidancePath -Content $guidanceContent -ForceOverwrite $forceFlag) {
        $filesWritten += $guidancePath
    } else {
        $filesSkipped += $guidancePath
    }
}

# 4. profile.yaml
$profilePath = Join-Path $conductorPath 'profile.yaml'
$profileContent = New-ProfileYaml
if (Write-StubFile -FilePath $profilePath -Content $profileContent -ForceOverwrite $forceFlag) {
    $filesWritten += $profilePath
} else {
    $filesSkipped += $profilePath
}

# ── Output summary ───────────────────────────────────────────────────────────
$summary = [ordered]@{
    process_template = $resolvedTemplate
    files_written    = $filesWritten
    files_skipped    = $filesSkipped
    types            = $types
}

$summary | ConvertTo-Json -Depth 3
