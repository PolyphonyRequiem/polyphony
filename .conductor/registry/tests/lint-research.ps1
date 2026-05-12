<#
.SYNOPSIS
    CI lint — validates research.yaml structural requirements.
.DESCRIPTION
    Parses workflows/research.yaml and verifies:
    1. Workflow name is 'research' with entry_point: validate_input
    2. Required inputs: topic, work_item_id, scratch_slug, archive_path
    3. Required outputs: findings_path, citation_count, topic
    4. Required nodes: validate_input, scan_archive, research_assistant,
       write_findings
    5. research_assistant uses a lightweight model (haiku tier)
    6. Routed agents declare an output: schema (M2)
    7. All route targets reference valid agent names or $end (M4)
    8. Type-agnostic (no Epic / Issue / Task / User Story / Bug
       hardcoded in the YAML)
    9. Prompt file reference exists for research-assistant
    Exits 0 if clean, 1 if violations found.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$yamlPath = Join-Path $repoRoot 'workflows' 'research.yaml'

if (-not (Test-Path $yamlPath)) {
    Write-Host "SKIP: $yamlPath not found" -ForegroundColor Yellow
    exit 0
}

$content = Get-Content $yamlPath -Raw
$lines = @(Get-Content $yamlPath)

$violations = @()

# ── Check 1: Workflow name ────────────────────────────────────────────────
if ($content -notmatch 'name:\s*research\s') {
    $violations += [PSCustomObject]@{
        Rule   = 'wrong-workflow-name'
        Detail = "Workflow name should be 'research'"
    }
}

# ── Check 2: Entry point references validate_input ───────────────────────
if ($content -match 'entry_point:\s*(\S+)') {
    $ep = $Matches[1]
    if ($ep -ne 'validate_input') {
        $violations += [PSCustomObject]@{
            Rule   = 'wrong-entry-point'
            Detail = "entry_point should be 'validate_input', found '$ep'"
        }
    }
} else {
    $violations += [PSCustomObject]@{
        Rule   = 'missing-entry-point'
        Detail = "No entry_point found"
    }
}

# ── Check 3: Required inputs ─────────────────────────────────────────────
$requiredInputs = @('topic', 'work_item_id', 'scratch_slug', 'archive_path')
foreach ($input in $requiredInputs) {
    if ($content -notmatch "(?m)^\s{4}${input}\s*:") {
        $violations += [PSCustomObject]@{
            Rule   = "missing-input-$input"
            Detail = "Required input '$input' not found in workflow input block"
        }
    }
}

# ── Check 4: Required outputs ────────────────────────────────────────────
$requiredOutputs = @('findings_path', 'citation_count', 'topic')
foreach ($output in $requiredOutputs) {
    if ($content -notmatch "(?m)^\s{2}${output}\s*:") {
        $violations += [PSCustomObject]@{
            Rule   = "missing-output-$output"
            Detail = "Required output '$output' not found in workflow output block"
        }
    }
}

# ── Check 5: Required nodes ──────────────────────────────────────────────
$requiredNodes = @('validate_input', 'scan_archive', 'research_assistant', 'write_findings')
foreach ($node in $requiredNodes) {
    if ($content -notmatch "name:\s*$node\b") {
        $violations += [PSCustomObject]@{
            Rule   = "missing-node-$node"
            Detail = "Required node '$node' not found"
        }
    }
}

# ── Check 6: research_assistant uses a lightweight model ─────────────────
# The research_assistant should use a haiku-tier model, not opus/sonnet
if ($content -match 'name:\s*research_assistant') {
    # Extract the model line near research_assistant
    $inAssistant = $false
    $foundModel = $false
    foreach ($line in $lines) {
        if ($line -match 'name:\s*research_assistant') {
            $inAssistant = $true
            continue
        }
        if ($inAssistant -and $line -match '^\s{4}model:\s*(.+)') {
            $model = $Matches[1].Trim()
            $foundModel = $true
            if ($model -notmatch 'haiku') {
                $violations += [PSCustomObject]@{
                    Rule   = 'research-assistant-not-lightweight'
                    Detail = "research_assistant should use a lightweight (haiku) model, found '$model'"
                }
            }
            break
        }
        if ($inAssistant -and $line -match '^\s{2}-\s*name:') {
            # Hit next agent without finding model
            break
        }
    }
    if (-not $foundModel) {
        $violations += [PSCustomObject]@{
            Rule   = 'research-assistant-no-model'
            Detail = "research_assistant does not declare a model"
        }
    }
}

# ── Check 7: research_assistant declares output schema (M2) ──────────────
if ($content -match 'name:\s*research_assistant') {
    $inAssistant = $false
    $foundOutput = $false
    foreach ($line in $lines) {
        if ($line -match 'name:\s*research_assistant') {
            $inAssistant = $true
            continue
        }
        if ($inAssistant -and $line -match '^\s{4}output:') {
            $foundOutput = $true
            break
        }
        if ($inAssistant -and $line -match '^\s{2}-\s*name:') {
            break
        }
    }
    if (-not $foundOutput) {
        $violations += [PSCustomObject]@{
            Rule   = 'research-assistant-no-output-schema'
            Detail = "research_assistant must declare an output: schema (M2)"
        }
    }
}

# ── Check 8: All route targets are valid agent names or $end (M4) ────────
$agentNames = @()
foreach ($line in $lines) {
    if ($line -match '^\s+-\s*name:\s*(\S+)') {
        $agentNames += $Matches[1]
    }
}
foreach ($line in $lines) {
    if ($line -match '[-\s]+to:\s*(\S+)') {
        $target = $Matches[1]
        if ($target -ne '$end' -and $target -notin $agentNames) {
            $violations += [PSCustomObject]@{
                Rule   = 'invalid-route-target'
                Detail = "Route target '$target' is not a declared agent name or `$end"
            }
        }
    }
}

# ── Check 9: Type-agnostic — no hardcoded work item type names ───────────
$typeNames = @('Epic', 'Issue', 'Task', 'User Story', 'Bug')
foreach ($typeName in $typeNames) {
    # Skip comments (lines starting with #) and description/prompt strings
    foreach ($line in $lines) {
        $trimmed = $line.TrimStart()
        if ($trimmed.StartsWith('#')) { continue }
        # Check for type name as a literal token (not in prose/comments)
        if ($line -match "type:\s*['""]?$typeName['""]?" -and $line -notmatch 'type:\s*(string|number|array|object|boolean|agent|script|human_gate)') {
            $violations += [PSCustomObject]@{
                Rule   = 'hardcoded-type-name'
                Detail = "Hardcoded work item type '$typeName' found — workflow must be type-agnostic"
            }
        }
    }
}

# ── Check 10: Prompt file reference exists ───────────────────────────────
if ($content -match '!file\s+prompts/research-assistant\.md') {
    $promptPath = Join-Path $repoRoot 'prompts' 'research-assistant.md'
    if (-not (Test-Path $promptPath)) {
        $violations += [PSCustomObject]@{
            Rule   = 'missing-prompt-file'
            Detail = "Prompt file 'prompts/research-assistant.md' referenced but not found"
        }
    }
} else {
    $violations += [PSCustomObject]@{
        Rule   = 'no-prompt-file-reference'
        Detail = "research_assistant should reference prompt via !file prompts/research-assistant.md"
    }
}

# ── Check 11: StrictUndefined guards on outputs (M3) ─────────────────────
# Workflow output blocks that reference agent node outputs must contain
# `is defined` guards. We check that each output key's full value
# (which may span multiple lines via >- folding) contains the guard.
$outputNodes = @('write_findings', 'research_assistant', 'scan_archive')
foreach ($node in $outputNodes) {
    # Check if the node is referenced in the output section at all
    $outputStartIdx = -1
    $agentsStartIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^output:') { $outputStartIdx = $i }
        if ($lines[$i] -match '^agents:') { $agentsStartIdx = $i }
    }
    if ($outputStartIdx -ge 0 -and $agentsStartIdx -gt $outputStartIdx) {
        $outputBlock = ($lines[$outputStartIdx..$($agentsStartIdx - 1)]) -join "`n"
        if ($outputBlock -match $node -and $outputBlock -notmatch "$node\s+is defined") {
            $violations += [PSCustomObject]@{
                Rule   = 'missing-strict-undefined-guard'
                Detail = "Output block references '$node' without 'is defined' guard (M3)"
            }
        }
    }
}

# ── Report ────────────────────────────────────────────────────────────────
if ($violations.Count -gt 0) {
    Write-Host "`n❌  research.yaml lint failed — $($violations.Count) violation(s):`n" -ForegroundColor Red
    foreach ($v in $violations) {
        Write-Host "  [$($v.Rule)] $($v.Detail)" -ForegroundColor Yellow
    }
    Write-Host ""
    exit 1
} else {
    Write-Host "✅  research.yaml lint passed" -ForegroundColor Green
    exit 0
}
