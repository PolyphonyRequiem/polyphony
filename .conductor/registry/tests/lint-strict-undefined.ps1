<#
.SYNOPSIS
    CI lint — flags the StrictUndefined `default(<agent>.output.<...>)` trap in workflow YAMLs.

.DESCRIPTION
    Conductor's Jinja runs with StrictUndefined (per conductor M3). The shorthand
    `default(other_agent.output.field)` does NOT lazy-evaluate when `other_agent` itself
    is undefined — only when the looked-up attribute on a defined object is undefined.
    So in branched workflows where only one of several mutually-exclusive agents runs,
    referencing a non-running agent inside `default(...)` raises UndefinedError at render time.

    Correct pattern (from implement-pg.yaml):
        {% if other_agent is defined %}{{ other_agent.output.field }}{% else %}fallback{% endif %}

    This lint walks every YAML under .conductor/registry/workflows/ and flags any line
    matching `default\s*\(\s*<identifier>\.output\.`. YAML comment lines are ignored.

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

$pattern = 'default\s*\(\s*([A-Za-z_]\w*)\.output\.'
$violations = @()
$yamlFiles = @(Get-ChildItem -Path $WorkflowsDir -Filter '*.yaml' -File)

foreach ($file in $yamlFiles) {
    $lines = @(Get-Content $file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*#') { continue }

        $matchResult = [regex]::Matches($line, $pattern)
        foreach ($m in $matchResult) {
            $violations += [PSCustomObject]@{
                File       = $file.Name
                Line       = $i + 1
                Identifier = $m.Groups[1].Value
                Snippet    = $line.Trim()
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "`n[FAIL] StrictUndefined default-trap lint failed ($($violations.Count) violations):`n" -ForegroundColor Red
    foreach ($v in $violations) {
        Write-Host "  $($v.File):$($v.Line) -> default($($v.Identifier).output.*)" -ForegroundColor Red
        Write-Host "    $($v.Snippet)" -ForegroundColor DarkGray
    }
    Write-Host "`nFix: replace 'default($($violations[0].Identifier).output.X)' with an explicit guard:" -ForegroundColor Yellow
    Write-Host "  {% if $($violations[0].Identifier) is defined %}{{ $($violations[0].Identifier).output.X }}{% else %}fallback{% endif %}`n" -ForegroundColor Yellow
    exit 1
}

Write-Host "[OK] StrictUndefined default-trap lint passed ($($yamlFiles.Count) workflow(s) scanned)" -ForegroundColor Green
exit 0
