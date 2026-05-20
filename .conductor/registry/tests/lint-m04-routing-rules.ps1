<#
.SYNOPSIS
    CI lint — assert M4 (routing rules) compliance across the workflow
    registry.

.DESCRIPTION
    Conductor's router (`engine/router.py`) walks each agent's `routes:`
    table top-down and selects the first route whose `when:` clause
    evaluates truthy. If no route matches, conductor raises
    `ValueError: No matching route found.` at runtime — often from a
    state nobody intended to reach (a script returning an unexpected
    phase, an undefined output, a template eval edge case).

    Per `references/m04-routing-rules.md` in the conductor-mechanics
    skill, every reachable agent state must have an explicit `when:`
    route OR a final unconditional catch-all (`- to: <node>` with no
    `when:` field), and the catch-all MUST be the LAST entry in the
    table — earlier bare routes always match and shadow later routes.

    This lint walks every workflow YAML, identifies routes-bearing
    agents (script / agent / parallel / for_each — NOT human_gate, which
    routes via `options[].route`), and asserts:

    ERROR M4-001 invalid-route-target
      - Route `to:` references a name that doesn't match any agent or
        top-level for_each/parallel group in the workflow.
      - `$end` is always valid.

    ERROR M4-002 catch-all-not-last
      - A bare `- to:` (no `when:`) appears before later routes.
      - Later routes are dead code; conductor's first-match selection
        means the bare route always wins.
      - Fix: delete dead duplicates OR move the bare route to last
        position OR give it an explicit `when:` clause.

    ERROR M4-003 missing-catch-all
      - Agent has a `routes:` table but no bare unconditional catch-all.
      - Even boolean-exhaustive discriminators (`== true` + `== false`)
        need a defensive catch-all for error / undefined / null payloads.
      - Fix: add `- to: <gate>` (no `when:`) as the LAST route.

    ERROR M4-004 routes-on-gate
      - `human_gate` agent has a `routes:` table.
      - Gates route via `options[i].route` — `routes:` is silently
        ignored (and rejected by stricter validators).
      - Fix: remove `routes:` and use `options[].route` per option.

    ERROR M4-005 when-true-not-last
      - A route with literal `when: "true"` (or `when: "{{ true }}"`)
        appears before later routes.
      - Same shadowing failure mode as M4-002.

    ERROR M4-006 route-missing-to
      - A route entry has no `to:` field.

    ERROR M4-007 invalid-gate-option-route
      - A `human_gate` option's `route:` references an unknown node.

    Rule evaluation order — M4-006 runs first per route, and target
    validation (M4-001 / M4-007) is skipped for routes flagged as
    missing-to to avoid cascading noise.

.PARAMETER WorkflowsDir
    Directory of workflow YAMLs to scan. Defaults to
    `<lint-dir>/../workflows`.

.PARAMETER Format
    Output format: `human` (default) or `github` (Actions annotations).

.OUTPUTS
    Per-violation FAIL lines on stdout. Exit 0 if all routing tables
    M4-compliant, 1 if any ERROR-level violations.
#>
[CmdletBinding()]
param(
    [string]$WorkflowsDir = (Join-Path $PSScriptRoot '..' 'workflows'),
    [ValidateSet('human', 'github')]
    [string]$Format = 'human'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $WorkflowsDir)) {
    Write-Host "SKIP: workflows dir not found: $WorkflowsDir" -ForegroundColor Yellow
    exit 0
}

Import-Module powershell-yaml -ErrorAction Stop

# Classify a `when:` clause:
#   - missing / null / whitespace-only → 'bare' (true catch-all)
#   - literal "true" or "{{ true }}"   → 'literal-true' (also shadows later)
#   - anything else                    → 'conditional'
function Get-WhenKind {
    param([AllowNull()] $When)
    if ($null -eq $When) { return 'bare' }
    $s = "$When".Trim()
    if ([string]::IsNullOrWhiteSpace($s)) { return 'bare' }
    if ($s -match '^(true|"true"|''true''|\{\{\s*true\s*\}\})$') {
        return 'literal-true'
    }
    return 'conditional'
}

# Collect all valid route target names from a parsed workflow:
#   - every agent name under `agents:`
#   - every top-level for_each / parallel group name (these live at
#     workflow root, NOT under `agents:` — per M8 / conductor schema)
function Get-ValidTargets {
    param($Parsed)
    $names = @('$end')
    if ($Parsed.Contains('agents') -and $Parsed['agents']) {
        foreach ($a in $Parsed['agents']) {
            if ($a.Contains('name') -and $a['name']) { $names += $a['name'] }
        }
    }
    foreach ($groupKey in @('for_each', 'parallel')) {
        if ($Parsed.Contains($groupKey) -and $Parsed[$groupKey]) {
            foreach ($g in $Parsed[$groupKey]) {
                if ($g.Contains('name') -and $g['name']) { $names += $g['name'] }
            }
        }
    }
    return $names
}

# Run M4 rules on one routes-bearing node (regular agent or top-level
# for_each/parallel group). $type is the YAML `type:` value.
function Test-RouteTable {
    param(
        [Parameter(Mandatory)] $Node,
        [Parameter(Mandatory)] [string]$WorkflowName,
        [Parameter(Mandatory)] [string]$NodeName,
        [Parameter(Mandatory)] [string]$NodeType,
        [Parameter(Mandatory)] [string[]]$ValidTargets
    )
    $found = @()
    $routes = @($Node['routes'])

    # First pass: M4-006 (missing-to). Flag and skip target validation
    # for affected indices to avoid cascading M4-001 noise.
    $missingToIdx = @{}
    for ($i = 0; $i -lt $routes.Count; $i++) {
        $r = $routes[$i]
        $hasTo = $r.Contains('to') -and -not [string]::IsNullOrWhiteSpace("$($r['to'])")
        if (-not $hasTo) {
            $missingToIdx[$i] = $true
            $found += [PSCustomObject]@{
                Workflow = $WorkflowName
                Node     = $NodeName
                Rule     = 'M4-006-route-missing-to'
                Detail   = "Route entry at index $i has no 'to:' field."
            }
        }
    }

    # Second pass: M4-001 (invalid-route-target).
    for ($i = 0; $i -lt $routes.Count; $i++) {
        if ($missingToIdx.ContainsKey($i)) { continue }
        $to = "$($routes[$i]['to'])"
        if ($to -notin $ValidTargets) {
            $found += [PSCustomObject]@{
                Workflow = $WorkflowName
                Node     = $NodeName
                Rule     = 'M4-001-invalid-route-target'
                Detail   = "Route at index $i targets '$to' which is not an agent name, not a top-level for_each/parallel group name, and not '`$end'."
            }
        }
    }

    # Third pass: classify when-clauses for catch-all / shadowing checks.
    $kinds = @()
    for ($i = 0; $i -lt $routes.Count; $i++) {
        if ($missingToIdx.ContainsKey($i)) { $kinds += 'invalid'; continue }
        $when = if ($routes[$i].Contains('when')) { $routes[$i]['when'] } else { $null }
        $kinds += (Get-WhenKind -When $when)
    }

    # M4-002 catch-all-not-last: any 'bare' kind earlier than the last
    # *valid* route index shadows everything after.
    $lastValidIdx = -1
    for ($i = $kinds.Count - 1; $i -ge 0; $i--) {
        if ($kinds[$i] -ne 'invalid') { $lastValidIdx = $i; break }
    }
    for ($i = 0; $i -lt $kinds.Count; $i++) {
        if ($kinds[$i] -ne 'bare') { continue }
        if ($i -lt $lastValidIdx) {
            $shadowedTargets = @()
            for ($j = $i + 1; $j -lt $routes.Count; $j++) {
                if ($missingToIdx.ContainsKey($j)) { continue }
                $shadowedTargets += "'$($routes[$j]['to'])'"
            }
            $sameDest = $false
            $bareTo = "$($routes[$i]['to'])"
            for ($j = $i + 1; $j -lt $routes.Count; $j++) {
                if ($missingToIdx.ContainsKey($j)) { continue }
                if ("$($routes[$j]['to'])" -eq $bareTo) { $sameDest = $true; break }
            }
            $hint = if ($sameDest) {
                "Later route shares this destination — delete the later duplicate (it is dead code)."
            } else {
                "Move the bare route to last position OR give it an explicit 'when:' clause."
            }
            $found += [PSCustomObject]@{
                Workflow = $WorkflowName
                Node     = $NodeName
                Rule     = 'M4-002-catch-all-not-last'
                Detail   = "Bare 'to: $bareTo' at index $i shadows later route(s): $($shadowedTargets -join ', '). $hint"
            }
        }
    }

    # M4-005 when-true-not-last: 'literal-true' kind earlier than last valid.
    for ($i = 0; $i -lt $kinds.Count; $i++) {
        if ($kinds[$i] -ne 'literal-true') { continue }
        if ($i -lt $lastValidIdx) {
            $found += [PSCustomObject]@{
                Workflow = $WorkflowName
                Node     = $NodeName
                Rule     = 'M4-005-when-true-not-last'
                Detail   = "Route at index $i with literal 'when: true' shadows later routes (same failure mode as M4-002)."
            }
        }
    }

    # M4-003 missing-catch-all: no 'bare' route present anywhere.
    if (-not ($kinds -contains 'bare')) {
        $found += [PSCustomObject]@{
            Workflow = $WorkflowName
            Node     = $NodeName
            Rule     = 'M4-003-missing-catch-all'
            Detail   = "Routes table has no bare unconditional catch-all. Even boolean-exhaustive discriminators need defensive coverage for error/undefined/null payloads. Fix: add '- to: <gate>' (no 'when:') as the LAST route."
        }
    }

    return ,$found
}

# Run M4 rules on a human_gate's options[].route entries.
function Test-GateOptions {
    param(
        [Parameter(Mandatory)] $Node,
        [Parameter(Mandatory)] [string]$WorkflowName,
        [Parameter(Mandatory)] [string]$NodeName,
        [Parameter(Mandatory)] [string[]]$ValidTargets
    )
    $found = @()

    if ($Node.Contains('routes') -and $Node['routes']) {
        $found += [PSCustomObject]@{
            Workflow = $WorkflowName
            Node     = $NodeName
            Rule     = 'M4-004-routes-on-gate'
            Detail   = "human_gate has a 'routes:' table. Gates route via 'options[i].route' — the 'routes:' field is silently ignored (and rejected by stricter validators)."
        }
    }

    if ($Node.Contains('options') -and $Node['options']) {
        for ($i = 0; $i -lt $Node['options'].Count; $i++) {
            $opt = $Node['options'][$i]
            if (-not ($opt.Contains('route') -and -not [string]::IsNullOrWhiteSpace("$($opt['route'])"))) {
                continue  # missing-route on a gate option is a different concern (not M4)
            }
            $to = "$($opt['route'])"
            if ($to -notin $ValidTargets) {
                $found += [PSCustomObject]@{
                    Workflow = $WorkflowName
                    Node     = $NodeName
                    Rule     = 'M4-007-invalid-gate-option-route'
                    Detail   = "Option at index $i routes to '$to' which is not an agent name, not a top-level for_each/parallel group name, and not '`$end'."
                }
            }
        }
    }

    return ,$found
}

$workflows = Get-ChildItem $WorkflowsDir -Filter '*.yaml' -File
$findings = @()
$inspected = 0
$skippedParse = @()

foreach ($wf in $workflows) {
    try {
        $parsed = ConvertFrom-Yaml (Get-Content $wf.FullName -Raw) -Ordered
    } catch {
        $skippedParse += "$($wf.Name): $($_.Exception.Message)"
        continue
    }
    if (-not $parsed) { continue }

    $validTargets = Get-ValidTargets -Parsed $parsed

    # Regular agents under `agents:`.
    if ($parsed.Contains('agents') -and $parsed['agents']) {
        foreach ($agent in $parsed['agents']) {
            $inspected++
            $name = "$($agent['name'])"
            $type = "$($agent['type'])"

            if ($type -eq 'human_gate') {
                $findings += Test-GateOptions -Node $agent `
                                              -WorkflowName $wf.Name `
                                              -NodeName $name `
                                              -ValidTargets $validTargets
                continue
            }

            if (-not ($agent.Contains('routes') -and $agent['routes'])) { continue }

            $findings += Test-RouteTable -Node $agent `
                                         -WorkflowName $wf.Name `
                                         -NodeName $name `
                                         -NodeType $type `
                                         -ValidTargets $validTargets
        }
    }

    # Top-level for_each / parallel groups (live at workflow root, not
    # under `agents:` — per M8 / conductor schema).
    foreach ($groupKey in @('for_each', 'parallel')) {
        if (-not ($parsed.Contains($groupKey) -and $parsed[$groupKey])) { continue }
        foreach ($g in $parsed[$groupKey]) {
            $inspected++
            $name = "$($g['name'])"
            if (-not ($g.Contains('routes') -and $g['routes'])) { continue }
            $findings += Test-RouteTable -Node $g `
                                         -WorkflowName $wf.Name `
                                         -NodeName $name `
                                         -NodeType $groupKey `
                                         -ValidTargets $validTargets
        }
    }
}

# --- report ------------------------------------------------------------------

Write-Host ''
Write-Host "M4 routing-rules audit — scanned $($workflows.Count) workflow(s); inspected $inspected node(s)." -ForegroundColor Cyan
Write-Host ''

if ($skippedParse.Count -gt 0) {
    Write-Host "Skipped (YAML parse error):" -ForegroundColor Yellow
    foreach ($s in $skippedParse) { Write-Host "  $s" -ForegroundColor Yellow }
    Write-Host ''
}

if ($findings.Count -gt 0) {
    foreach ($f in $findings) {
        if ($Format -eq 'github') {
            $msg = "$($f.Rule) [$($f.Node)] — $($f.Detail)"
            Write-Host "::error file=.conductor/registry/workflows/$($f.Workflow)::$msg"
        } else {
            Write-Host "FAIL [$($f.Workflow)] $($f.Rule): node=$($f.Node)" -ForegroundColor Red
            Write-Host "  $($f.Detail)" -ForegroundColor DarkGray
            Write-Host ''
        }
    }
    Write-Host "FAIL: $($findings.Count) M4 violation(s)" -ForegroundColor Red
    exit 1
}

Write-Host "PASS: $inspected node(s) M4-compliant across $($workflows.Count) workflow(s)" -ForegroundColor Green
exit 0
