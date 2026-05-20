<#
.SYNOPSIS
    CI lint — assert M10 (iterate-until-stable) safety for every counter
    node in the workflow registry.

.DESCRIPTION
    Conductor has no first-class loop primitive. The canonical
    iterate-until-stable pattern (per `references/m10-iterate-until-stable.md`
    in the conductor-mechanics skill) is:

      - a script node that increments a per-instance counter
      - one route guarded by `cap_reached == true` (or `under_limit == false`)
        diverting to a cap-hit gate / terminal
      - one route guarded by `cap_reached == false` (or `under_limit == true`)
        continuing the iteration
      - a defensive catch-all (no `when:`) routing to the cap-hit
        destination — so a template eval edge case or a counter-script
        crash fails SAFE to the gate rather than re-entering the cycle
        with a stale counter

    The infinite-loop bug M10 calls out by name is:

      "Catch-all loops back to the cycle 'just in case' — this **is** the
      infinite-loop bug. Catch-all must terminate."

    This lint enforces that contract by walking every workflow YAML,
    identifying counter nodes (script type, name contains 'counter'),
    and asserting:

    ERROR M10-catchall-into-cycle
      - Counter has a cap-hit-guarded route (cap_reached == true or
        under_limit == false).
      - Counter has a catch-all route.
      - The catch-all target is REACHABLE-BACK-TO this counter without
        passing through a `human_gate` or a `terminal_*` node (within
        a bounded forward walk of `MaxReachabilityHops`).
      - → The catch-all IS the cycle-back path. Make cycle-back explicit
        (cap_reached == false / under_limit == true) and add a defensive
        catch-all to the cap-hit destination.

    Intentionally NOT flagged (would over-fire):
      - Counters whose catch-all routes to a different gate / terminal
        than the cap-hit target (e.g. pending_poll_counter routing to
        the "normal pending" gate as catch-all and the "stuck" gate as
        cap-hit). These are gate-safe by construction.
      - Counters with > 2 conditional routes whose catch-all matches
        the cap-hit destination.
      - Counters with explicit `cap_reached == false` cycle-back AND a
        defensive catch-all to the cap-hit destination (the canonical
        M10-compliant shape).

.PARAMETER WorkflowsDir
    Directory of workflow YAMLs to scan. Defaults to
    `<lint-dir>/../workflows`.

.PARAMETER Format
    Output format: `human` (default) or `github` (Actions annotations).

.PARAMETER MaxReachabilityHops
    Maximum forward-walk depth from a catch-all target when checking
    cycle-back reachability. Default 10. Counters reachable beyond this
    depth are considered safely-terminating.

.OUTPUTS
    Per-counter inventory + per-violation FAIL lines on stdout. Exit 0 if
    all counters M10-compliant, 1 if any ERROR-level violations.
#>
[CmdletBinding()]
param(
    [string]$WorkflowsDir = (Join-Path $PSScriptRoot '..' 'workflows'),
    [ValidateSet('human', 'github')]
    [string]$Format = 'human',
    [int]$MaxReachabilityHops = 10
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $WorkflowsDir)) {
    Write-Host "SKIP: workflows dir not found: $WorkflowsDir" -ForegroundColor Yellow
    exit 0
}

Import-Module powershell-yaml -ErrorAction Stop

# Classify a `when:` clause as one of the M10 route kinds.
function Get-RouteKind {
    param([string]$When)
    if ([string]::IsNullOrWhiteSpace($When)) { return 'catch-all' }
    if ($When -match 'cap_reached\s*==\s*true|under_limit\s*==\s*false') {
        return 'cap-hit'
    }
    if ($When -match 'cap_reached\s*==\s*false|under_limit\s*==\s*true') {
        return 'cycle-back'
    }
    return 'other-conditional'
}

# A node is a "safe terminator" if it ends the iteration without
# re-entering the cycle: human_gate (blocks for operator input) or
# any node whose name starts with `terminal_`.
function Test-SafeTerminator {
    param($Node, [string]$Name)
    if (-not $Node) { return $true }  # External / $end — treated as safe.
    if ($Name -like 'terminal_*') { return $true }
    if ($Node['type'] -eq 'human_gate') { return $true }
    return $false
}

# Forward-walk from $startName, bounded by $MaxHops, looking for $target.
# Stops at safe terminators. Returns $true if $target is reached.
function Test-ReachesNode {
    param(
        [string]$StartName,
        [string]$TargetName,
        $ByName,
        [int]$MaxHops
    )
    $visited = @{}
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue(@{ Name = $StartName; Depth = 0 })
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        if ($visited.ContainsKey($cur.Name)) { continue }
        $visited[$cur.Name] = $true
        if ($cur.Depth -gt 0 -and $cur.Name -eq $TargetName) { return $true }
        if ($cur.Depth -ge $MaxHops) { continue }
        $node = $ByName[$cur.Name]
        if (Test-SafeTerminator -Node $node -Name $cur.Name) { continue }
        if (-not $node) { continue }
        # Collect outgoing edges: `routes:` for script/agent, `options[].route` for human_gate.
        $edges = @()
        if ($node['routes']) {
            foreach ($r in $node['routes']) {
                if ($r['to']) { $edges += $r['to'] }
            }
        }
        if ($node['options']) {
            foreach ($opt in $node['options']) {
                if ($opt['route']) { $edges += $opt['route'] }
            }
        }
        foreach ($e in $edges) {
            if (-not $visited.ContainsKey($e)) {
                $queue.Enqueue(@{ Name = $e; Depth = $cur.Depth + 1 })
            }
        }
    }
    return $false
}

$workflows = Get-ChildItem $WorkflowsDir -Filter '*.yaml' -File
$counters = @()
$findings = @()

foreach ($wf in $workflows) {
    try {
        $parsed = ConvertFrom-Yaml (Get-Content $wf.FullName -Raw) -Ordered
    } catch { continue }
    if (-not $parsed.agents) { continue }

    $byName = @{}
    foreach ($a in $parsed.agents) { $byName[$a.name] = $a }

    foreach ($a in $parsed.agents) {
        if ($a.name -notmatch 'counter') { continue }
        if ($a['type'] -ne 'script') { continue }
        if (-not $a['routes']) { continue }

        $orderedRoutes = @()
        foreach ($r in $a['routes']) {
            $when = if ($r.Keys -contains 'when') { $r['when'] } else { $null }
            $orderedRoutes += [PSCustomObject]@{
                Target = $r['to']
                When   = $when
                Kind   = (Get-RouteKind -When $when)
            }
        }

        $counters += [PSCustomObject]@{
            Workflow = $wf.Name
            Counter  = $a.name
            Routes   = $orderedRoutes
        }

        $capHitRoutes   = @($orderedRoutes | Where-Object { $_.Kind -eq 'cap-hit' })
        $cycleBackRoutes = @($orderedRoutes | Where-Object { $_.Kind -eq 'cycle-back' })
        $catchAllRoutes = @($orderedRoutes | Where-Object { $_.Kind -eq 'catch-all' })

        # Only counters with cap-hit guarded routes are M10-iterate-stable candidates.
        if ($capHitRoutes.Count -eq 0) { continue }
        $capHitTarget = $capHitRoutes[0].Target

        # No catch-all → counter relies entirely on conditional matching;
        # different concern (would raise "No matching route found" at runtime
        # if conditions miss). Skip — that's an M4 concern, not M10.
        if ($catchAllRoutes.Count -eq 0) { continue }
        $catchAllTarget = $catchAllRoutes[0].Target

        # M10-compliant patterns:
        #   1. Explicit cycle-back AND catch-all to cap-hit destination
        #   2. Catch-all target is a safe terminator (gate / terminal_*)
        #   3. Catch-all target == cap-hit destination (defensive fail-safe)
        if ($catchAllTarget -eq $capHitTarget) { continue }
        if ($cycleBackRoutes.Count -gt 0 -and $catchAllTarget -eq $capHitTarget) { continue }

        $catchAllNode = $byName[$catchAllTarget]
        if (Test-SafeTerminator -Node $catchAllNode -Name $catchAllTarget) {
            continue  # Catch-all goes straight to a gate/terminal — safe.
        }

        # Now the meaningful check: does the catch-all target lead back to
        # this counter within MaxReachabilityHops without hitting a safe
        # terminator first?
        $reaches = Test-ReachesNode -StartName $catchAllTarget `
                                    -TargetName $a.name `
                                    -ByName $byName `
                                    -MaxHops $MaxReachabilityHops
        if ($reaches) {
            $findings += [PSCustomObject]@{
                Workflow = $wf.Name
                Severity = 'ERROR'
                Counter  = $a.name
                Rule     = 'M10-catchall-into-cycle'
                Detail   = "Catch-all route targets '$catchAllTarget' which loops back to this counter within $MaxReachabilityHops hops without passing a human_gate or terminal_* node. M10: the catch-all is your defensive fail-safe — it must terminate, not re-enter the cycle. Fix: make cycle-back explicit (cap_reached == false / under_limit == true) and route the catch-all to the cap-hit destination ('$capHitTarget')."
            }
        }
    }
}

# --- report ------------------------------------------------------------------

Write-Host ''
Write-Host "M10 counter audit — scanned $($workflows.Count) workflow(s); found $($counters.Count) counter node(s)." -ForegroundColor Cyan
Write-Host ''

if ($Format -eq 'human') {
    Write-Host "=== COUNTER INVENTORY ===" -ForegroundColor Cyan
    foreach ($c in $counters) {
        Write-Host "[$($c.Workflow)] $($c.Counter)" -ForegroundColor White
        foreach ($r in $c.Routes) {
            $kindColor = switch ($r.Kind) {
                'cap-hit'           { 'Red' }
                'cycle-back'        { 'Green' }
                'catch-all'         { 'Yellow' }
                'other-conditional' { 'DarkCyan' }
                default             { 'DarkGray' }
            }
            $whenDisplay = if ($r.When) { $r.When } else { '<no when: — catch-all>' }
            Write-Host "    → $($r.Target)  " -NoNewline
            Write-Host "[$($r.Kind)]" -ForegroundColor $kindColor -NoNewline
            Write-Host "  when: $whenDisplay" -ForegroundColor DarkGray
        }
        Write-Host ''
    }
}

$errors = @($findings | Where-Object { $_.Severity -eq 'ERROR' })

if ($errors.Count -gt 0) {
    foreach ($f in $errors) {
        if ($Format -eq 'github') {
            $msg = "$($f.Rule) [$($f.Counter)] — $($f.Detail)"
            Write-Host "::error file=.conductor/registry/workflows/$($f.Workflow)::$msg"
        } else {
            Write-Host "FAIL [$($f.Workflow)] $($f.Rule): counter=$($f.Counter)" -ForegroundColor Red
            Write-Host "  $($f.Detail)"
            Write-Host ''
        }
    }
    Write-Host ''
    Write-Host "FAIL: $($errors.Count) M10 violation(s)" -ForegroundColor Red
    exit 1
}

Write-Host "PASS: $($counters.Count) counter(s) M10-compliant across $($workflows.Count) workflow(s)" -ForegroundColor Green
exit 0
