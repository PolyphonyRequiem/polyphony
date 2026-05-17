<#
.SYNOPSIS
    CI lint — detect workflow `args:` that pass a value after a CAF `bool`
    flag (e.g. `["--delete-branch", "false"]`), which the
    ConsoleAppFramework dispatcher rejects at parse time with
    `Argument 'false' is not recognized.`

.DESCRIPTION
    ConsoleAppFramework v5 treats `bool` parameters as no-value-only
    switches. The presence of the flag means `true`; absence means `false`;
    the explicit value form (`--flag true|false|=true|:true`) is a parse
    error. When a workflow YAML contains:

        args:
          - "--delete-branch"
          - "false"

    and the underlying verb declares `bool deleteBranch = true`, the
    dispatcher returns exit 1 with an error envelope and the workflow
    node — having no error route — silently routes past it.

    The fix on the CLI side is to flip the parameter to `string` and
    parse it via `Polyphony.StringBoolArg.Parse`. Verbs that have been
    converted appear in the verb output schema registry with
    `clr_type: "string"` for that input, so this lint detects the bug
    class by reading the schema:

      * Iterate every `agents:` step whose `command: polyphony`.
      * Walk `args:` left-to-right. When element N is a `--flag` and
        element N+1 is `"true"` or `"false"` (case-insensitive), look
        up the verb's input schema for that flag.
      * If the input's `clr_type` is `bool`, fire CAFBOOL001.
      * If the input is not declared in the schema, defer to the
        existing VERB002 lint in lint-jinja-resolver.ps1.
      * If the input's `clr_type` is `string` (already shimmed via
        StringBoolArg), this is the supported path — silent.

    Output codes:
      CAFBOOL001  Error   bool flag passed an explicit value — CAF will
                          reject it. Either remove the value (bare flag
                          means true) or flip the parameter to string +
                          StringBoolArg in the verb body.

.PARAMETER WorkflowsDir
    Directory of workflow YAMLs to scan.
    Default: .conductor/registry/workflows/

.PARAMETER RegistryPath
    Path to verb-output-schemas.json. Default:
    artifacts/verb-output-schemas.json. Falls back to
    tests/lint/fixtures/verb-output-schemas.json when -UseFixtureRegistry
    is set.

.PARAMETER UseFixtureRegistry
    When set, load the hand-curated fixture instead of the build
    artifact. Used by Pester tests.

.PARAMETER Format
    Output format: 'human' (default) or 'github' (::error workflow
    commands for CI annotation).

.OUTPUTS
    Lines describing each finding. Exit 0 on clean, 1 on any
    CAFBOOL001, 2 on configuration error.
#>
[CmdletBinding()]
param(
    [string]$WorkflowsDir = (Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'workflows'),
    [string]$RegistryPath = (Join-Path $PSScriptRoot '..' 'artifacts' 'verb-output-schemas.json'),
    [switch]$UseFixtureRegistry,
    [ValidateSet('human', 'github')][string]$Format = 'human'
)
$ErrorActionPreference = 'Stop'

# ── Registry load ─────────────────────────────────────────────────────────
if ($UseFixtureRegistry) {
    $RegistryPath = Join-Path $PSScriptRoot 'lint' 'fixtures' 'verb-output-schemas.json'
}
if (-not (Test-Path $RegistryPath)) {
    Write-Host "ERROR: Verb output schema registry not found at: $RegistryPath. Run 'dotnet build src/Polyphony.SchemaExporter' to produce it, or pass -UseFixtureRegistry." -ForegroundColor Red
    exit 2
}
try {
    $registry = Get-Content $RegistryPath -Raw | ConvertFrom-Json -Depth 32
} catch {
    Write-Host "ERROR: Failed to parse registry at $RegistryPath : $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
if (-not $registry.PSObject.Properties.Match('verbs')) {
    Write-Host "ERROR: Registry at $RegistryPath has no 'verbs' field — registry is malformed." -ForegroundColor Red
    exit 2
}

if (-not (Test-Path $WorkflowsDir)) {
    Write-Host "SKIP: workflows directory not found at $WorkflowsDir" -ForegroundColor Yellow
    exit 0
}

# ── Helpers ───────────────────────────────────────────────────────────────
function Get-VerbInputs {
    param([string]$VerbKey)
    $verb = $registry.verbs.PSObject.Properties[$VerbKey]
    if ($null -eq $verb) { return $null }
    return $verb.Value.inputs
}

function Find-InputByFlag {
    param($Inputs, [string]$Flag)
    if ($null -eq $Inputs) { return $null }
    # Flag form is "--name" or "--name-with-dashes"; schema name is the
    # un-dashed form (e.g. "delete-branch"). Compare on the dashed-name
    # without leading dashes.
    $bare = $Flag -replace '^-+', ''
    foreach ($input in $Inputs) {
        if ($input.name -eq $bare) { return $input }
    }
    return $null
}

# Parse the agents array out of a workflow YAML.
# We rely on a permissive parser: a YAML library would be cleaner, but
# this repo's existing lints (lint-jinja-resolver.ps1, lint-version-drift.ps1)
# all use regex-based scanning to avoid a YAML module dependency in CI.
function Get-AgentSteps {
    param([string]$Path)
    $lines = Get-Content $Path
    $agents = @()
    $inAgents = $false
    $current = $null
    $argsList = $null
    $stepIndent = 0

    foreach ($raw in $lines) {
        $line = $raw.TrimEnd()
        if ($line -match '^\s*agents:\s*$') { $inAgents = $true; continue }
        if ($inAgents -and $line -match '^[A-Za-z_]') {
            # Left agents block — top-level key.
            if ($null -ne $current) { $current.args = $argsList; $agents += $current }
            $inAgents = $false
            $current = $null
            $argsList = $null
            continue
        }
        if (-not $inAgents) { continue }

        if ($line -match '^(\s*)-\s+name:\s*(.+)$') {
            if ($null -ne $current) { $current.args = $argsList; $agents += $current }
            $stepIndent = $matches[1].Length
            $current = [PSCustomObject]@{
                name = $matches[2].Trim()
                command = ''
                args = @()
                argLines = @()
            }
            $argsList = @()
            continue
        }
        if ($null -eq $current) { continue }

        if ($line -match '^\s+command:\s*[''"]?([^''"\s]+)[''"]?\s*$') {
            $current.command = $matches[1]
            continue
        }
        if ($line -match '^\s+args:\s*$') {
            $argsList = @()
            continue
        }
        # args list element. Two shapes: `- "value"` or `- value` or
        # `- >-\n   multi-line`. We only care about scalar entries here;
        # values that span lines aren't candidates for "true|false".
        if ($null -ne $argsList -and $line -match '^\s+-\s+(.*)$') {
            $val = $matches[1].Trim()
            # Strip surrounding quotes.
            if (($val.StartsWith('"') -and $val.EndsWith('"')) -or
                ($val.StartsWith("'") -and $val.EndsWith("'"))) {
                $val = $val.Substring(1, $val.Length - 2)
            }
            $argsList += [PSCustomObject]@{
                value = $val
                lineNumber = ($lines.IndexOf($raw) + 1)
            }
        }
    }
    if ($null -ne $current) { $current.args = $argsList; $agents += $current }
    return $agents
}

# ── Scan ──────────────────────────────────────────────────────────────────
$findings = @()
$workflowFiles = @(Get-ChildItem $WorkflowsDir -Filter '*.yaml' -File)

foreach ($wf in $workflowFiles) {
    $steps = Get-AgentSteps -Path $wf.FullName
    foreach ($step in $steps) {
        if ($step.command -ne 'polyphony') { continue }
        if ($null -eq $step.args -or $step.args.Count -lt 2) { continue }

        # Reconstruct the verb key from the first two arg values. CAF
        # verb groups have shape "<group> <verb>" (e.g. "pr merge-impl-pr").
        # Some single-word verbs (e.g. "validate") use just the first arg.
        # We try the two-word form first, then the one-word.
        $first = $step.args[0].value
        $second = if ($step.args.Count -ge 2) { $step.args[1].value } else { '' }
        $verbKey = "$first $second".Trim()
        $verbInputs = Get-VerbInputs $verbKey
        if ($null -eq $verbInputs) {
            $verbKey = $first
            $verbInputs = Get-VerbInputs $verbKey
        }
        if ($null -eq $verbInputs) { continue }  # unknown verb — VERB001 handles it

        # Walk args looking for flag+value-form pairs.
        for ($i = 0; $i -lt $step.args.Count - 1; $i++) {
            $a = $step.args[$i].value
            $b = $step.args[$i + 1].value
            if ($a -notmatch '^--[a-z][a-z0-9-]*$') { continue }
            if ($b -notmatch '^(?i:true|false)$') { continue }

            $input = Find-InputByFlag -Inputs $verbInputs -Flag $a
            if ($null -eq $input) { continue }  # unknown flag — VERB002 handles it
            if ($input.clr_type -ne 'bool') { continue }  # string-bool shim or other — fine

            $findings += [PSCustomObject]@{
                File = $wf.FullName
                Line = $step.args[$i + 1].lineNumber
                Step = $step.name
                Verb = $verbKey
                Flag = $a
                Value = $b
                Code = 'CAFBOOL001'
                Message = "Verb '$verbKey' declares '$a' as bool — CAF rejects '$a $b' at parse time (`"Argument '$b' is not recognized.`"). Either drop the value (bare flag means true; omit for false) or flip the CLI parameter to string + Polyphony.StringBoolArg.Parse."
            }
        }
    }
}

# ── Output ────────────────────────────────────────────────────────────────
if ($Format -eq 'github') {
    foreach ($f in $findings) {
        Write-Host ("::error file={0},line={1}::CAFBOOL001 {2}: {3}" -f $f.File, $f.Line, $f.Step, $f.Message)
    }
} else {
    if ($findings.Count -eq 0) {
        Write-Host "lint-caf-bool-value-form: 0 findings across $($workflowFiles.Count) workflow YAML(s)." -ForegroundColor Green
    } else {
        foreach ($f in $findings) {
            Write-Host ""
            Write-Host ("CAFBOOL001 {0}" -f $f.File) -ForegroundColor Red
            Write-Host ("  line {0} : agent '{1}' : verb '{2}'" -f $f.Line, $f.Step, $f.Verb) -ForegroundColor DarkGray
            Write-Host ("  args : {0} {1}" -f $f.Flag, $f.Value)
            Write-Host ("  {0}" -f $f.Message) -ForegroundColor Yellow
        }
        Write-Host ""
        Write-Host ("lint-caf-bool-value-form: {0} CAFBOOL001 finding(s) across {1} workflow YAML(s)." -f $findings.Count, $workflowFiles.Count) -ForegroundColor Red
    }
}

if ($findings.Count -gt 0) { exit 1 } else { exit 0 }
