<#
.SYNOPSIS
    CI lint — flags two StrictUndefined `default()` traps in workflow YAMLs.

.DESCRIPTION
    Conductor's Jinja runs with StrictUndefined (per conductor M3). Two related
    `default()` patterns LOOK defensive but raise UndefinedError at render time:

    Rule 1 — AGENT_VAR_DEFAULT (AB#3026):
        The shorthand `default(other_agent.output.field)` does NOT lazy-evaluate
        when `other_agent` itself is undefined — only when the looked-up attribute
        on a defined object is undefined. So in branched workflows where only one of
        several mutually-exclusive agents runs, referencing a non-running agent inside
        `default(...)` raises UndefinedError before the filter can supply the fallback.

        Correct pattern (from implement-merge-group.yaml):
            {% if other_agent is defined %}{{ other_agent.output.field }}{% else %}fallback{% endif %}

    Rule 2 — ATTR_DEFAULT (AB#3160 / AB#3156 Bug 2):
        `agent.output.required_field.optional_subkey | default(x)` ALSO trips
        StrictUndefined: attribute access on the bound dict raises UndefinedError
        BEFORE `default(x)` sees the value. Killed AB#3127 dogfood relaunch.

        Correct pattern (from PR #354):
            {{ agent.output.required_field.get('optional_subkey', x) }}

        `default()` on the result of a `.get(...)` call is fine; so is `default()`
        on a top-level required field (e.g. `workflow.input.foo | default('bar')` —
        `workflow.input` is always-defined).

    Whitelist marker — `{# strict-undefined-ok: <reason> #}` on the same line
    suppresses both rules. Use sparingly and only with a reason.

    This lint walks every YAML under .conductor/registry/workflows/ line by line.
    Exits 0 if clean, 1 if any violations are found.
#>
[CmdletBinding()]
param(
    [string] $WorkflowsDir
)

$ErrorActionPreference = 'Stop'

if (-not $WorkflowsDir) {
    $WorkflowsDir = Join-Path $PSScriptRoot '..' 'workflows'
}

if (-not (Test-Path $WorkflowsDir)) {
    Write-Host "SKIP: workflows dir not found: $WorkflowsDir" -ForegroundColor Yellow
    exit 0
}

# Rule 1: default(<id>.output.<...>) — undefined agent variable trap.
$patternAgentVarDefault = 'default\s*\(\s*([A-Za-z_]\w*)\.output\.'

# Rule 2: <id>.output.<key>.<key>... | default(...) — attribute-on-dict trap.
# Requires at least 2 dotted segments after `.output.` so that single-level
# accesses like `pr_lifecycle.output.merged | default(false)` (the verb's
# top-level required field) remain allowed. `.get('key', default)` doesn't
# match because `(` follows `.get`, not `|`.
$patternAttrDefault = '\b([A-Za-z_]\w*)\.output\.\w+(?:\.\w+)+\s*\|\s*default\s*\('

$violations = @()
$yamlFiles = @(Get-ChildItem -Path $WorkflowsDir -Filter '*.yaml' -File)

foreach ($file in $yamlFiles) {
    $lines = @(Get-Content $file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*#') { continue }
        if ($line -match '\{#\s*strict-undefined-ok\s*:') { continue }

        foreach ($m in [regex]::Matches($line, $patternAgentVarDefault)) {
            $violations += [PSCustomObject]@{
                Rule       = 'AGENT_VAR_DEFAULT'
                File       = $file.Name
                Line       = $i + 1
                Identifier = $m.Groups[1].Value
                Match      = $m.Value
                Snippet    = $line.Trim()
            }
        }

        foreach ($m in [regex]::Matches($line, $patternAttrDefault)) {
            $violations += [PSCustomObject]@{
                Rule       = 'ATTR_DEFAULT'
                File       = $file.Name
                Line       = $i + 1
                Identifier = $m.Groups[1].Value
                Match      = $m.Value
                Snippet    = $line.Trim()
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "`n[FAIL] StrictUndefined default-trap lint failed ($($violations.Count) violations):`n" -ForegroundColor Red
    foreach ($v in $violations) {
        if ($v.Rule -eq 'AGENT_VAR_DEFAULT') {
            Write-Host "  [AGENT_VAR_DEFAULT] $($v.File):$($v.Line) -> default($($v.Identifier).output.*)" -ForegroundColor Red
        } else {
            Write-Host "  [ATTR_DEFAULT] $($v.File):$($v.Line) -> $($v.Match)...)" -ForegroundColor Red
        }
        Write-Host "    $($v.Snippet)" -ForegroundColor DarkGray
    }

    $firstAgent = $violations | Where-Object { $_.Rule -eq 'AGENT_VAR_DEFAULT' } | Select-Object -First 1
    $firstAttr  = $violations | Where-Object { $_.Rule -eq 'ATTR_DEFAULT' }      | Select-Object -First 1

    if ($firstAgent) {
        Write-Host "`nFix [AGENT_VAR_DEFAULT]: replace 'default($($firstAgent.Identifier).output.X)' with an explicit guard:" -ForegroundColor Yellow
        Write-Host "  {% if $($firstAgent.Identifier) is defined %}{{ $($firstAgent.Identifier).output.X }}{% else %}fallback{% endif %}" -ForegroundColor Yellow
    }
    if ($firstAttr) {
        Write-Host "`nFix [ATTR_DEFAULT]: replace '<id>.output.A.B | default(x)' with a dict-method call:" -ForegroundColor Yellow
        Write-Host "  {{ $($firstAttr.Identifier).output.A.get('B', x) }}" -ForegroundColor Yellow
        Write-Host "(Whitelist with `{# strict-undefined-ok: <reason> #}` on the same line if intentional.)" -ForegroundColor Yellow
    }
    Write-Host ""
    exit 1
}

Write-Host "[OK] StrictUndefined default-trap lint passed ($($yamlFiles.Count) workflow(s) scanned)" -ForegroundColor Green
exit 0
