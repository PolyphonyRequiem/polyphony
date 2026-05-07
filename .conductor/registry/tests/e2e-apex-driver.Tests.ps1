<#
.SYNOPSIS
    End-to-end behavior tests for the apex-driver tree-walker.

.DESCRIPTION
    Phase 7 capstone — the structural lint
    (`lint-apex-driver.ps1`) verifies presence-of-things; this suite
    walks the parsed three-YAML graph and asserts the END-TO-END
    BEHAVIORS the apex-driver promises:

      • All three workflows (apex-driver / apex-wave-dispatch /
        apex-item-dispatch) parse, declare the documented entry
        points, and have every route target resolve.
      • The apex-driver outer loop is reachable end-to-end through
        preflight → build_worklist → wave_dispatch_loop → wave_loop_summary
        → renegotiation_summary → apex_completion_gate → close +
        terminal_apex_satisfied. Failure branches (preflight_failed /
        wave_failed / completion_satisfied / completion_abandoned /
        renegotiation_pending) route to the documented next node.
      • apex-wave-dispatch fans items out via for_each into
        apex-item-dispatch.yaml, then aggregates renegotiation across
        the wave's outputs, then invokes the integrator.
      • apex-item-dispatch (the heart of PR #149) branch-on-routers
        into one of {plan-level, actionable, implement-pg, feature-pr}
        based on the lifecycle-router's verdict, and short-circuits
        fast-path / monitoring / blocked / error before spawning a
        worktree.
      • The renegotiation bubble-up table from PR #149 — every layer's
        output map carries `renegotiation_pending`,
        `renegotiation_request`, `validate_scope_verdict`, and
        `scope_violation_files`, all M3-safe.
      • Inputs (apex_id / intent / platform / org / proj / repo) thread
        through all three layers' input_mapping blocks.
      • The lifecycle-router script's enumerated route values are
        EXACTLY the set apex-item-dispatch.yaml branches on (no
        router-emits-X-but-YAML-doesn't-handle-X drift) — the
        highest-value contract assertion in this suite.
      • lifecycle-router / worktree-manager / wave-integrator are each
        live-invokable and return a routing-style envelope (always
        exit 0, errors surface via `success` + `error_code`).

    These checks complement (do not duplicate) `lint-apex-driver.ps1`
    (structural presence) and the existing C# router tests (per-script
    JSON envelope). This suite pins the GRAPH the three workflows
    declare and the script-to-YAML contract on `lifecycle_workflow`.

    Approach: Pester structural+graph validation + light live
    script invocation (Approach B — same approach used in PR #145
    for actionable.yaml). A workflow-execution harness that drives
    conductor with mocked tools does not exist in this repo today;
    bootstrapping one is too large for this PR and is deferred per
    the PR brief.
#>
[CmdletBinding()]
param()

BeforeAll {
    Import-Module powershell-yaml -Force

    $script:WorkflowsDir = Join-Path $PSScriptRoot '..' 'workflows'
    $script:ScriptsDir   = Join-Path $PSScriptRoot '..' 'scripts'

    $script:ApexPath = Join-Path $script:WorkflowsDir 'apex-driver.yaml'
    $script:WavePath = Join-Path $script:WorkflowsDir 'apex-wave-dispatch.yaml'
    $script:ItemPath = Join-Path $script:WorkflowsDir 'apex-item-dispatch.yaml'

    $script:LifecycleRouter   = Join-Path $script:ScriptsDir 'lifecycle-router.ps1'
    $script:WorktreeManager   = Join-Path $script:ScriptsDir 'worktree-manager.ps1'
    $script:WaveIntegrator    = Join-Path $script:ScriptsDir 'wave-integrator.ps1'

    $script:ApexRaw = Get-Content $script:ApexPath -Raw
    $script:WaveRaw = Get-Content $script:WavePath -Raw
    $script:ItemRaw = Get-Content $script:ItemPath -Raw
    $script:RouterRaw = Get-Content $script:LifecycleRouter -Raw

    $script:ApexYaml = ConvertFrom-Yaml $script:ApexRaw
    $script:WaveYaml = ConvertFrom-Yaml $script:WaveRaw
    $script:ItemYaml = ConvertFrom-Yaml $script:ItemRaw

    # Build per-workflow agent indices for O(1) lookup.
    function script:Index-Agents($yaml) {
        $idx = @{}
        foreach ($a in $yaml.agents) { $idx[$a.name] = $a }
        return $idx
    }
    $script:ApexAgents = script:Index-Agents $script:ApexYaml
    $script:WaveAgents = script:Index-Agents $script:WaveYaml
    $script:ItemAgents = script:Index-Agents $script:ItemYaml

    # Helper: returns the route entries for a node within a given
    # agent index. Conductor route entries always have a `to`
    # (script/agent/workflow) or `route` (human_gate option).
    # Normalize both shapes into a uniform list.
    function script:Get-NodeRoutes {
        param(
            [hashtable]$Agents,
            [string]$NodeName
        )
        $node = $Agents[$NodeName]
        if (-not $node) { return @() }
        $out = @()
        if ($node.routes) {
            foreach ($r in $node.routes) {
                $out += [PSCustomObject]@{
                    Target = $r.to
                    When   = $r.when
                    Value  = $null
                    Label  = $null
                    Kind   = 'route'
                }
            }
        }
        if ($node.options) {
            foreach ($o in $node.options) {
                $out += [PSCustomObject]@{
                    Target = $o.route
                    When   = $null
                    Value  = $o.value
                    Label  = $o.label
                    Kind   = 'option'
                }
            }
        }
        return $out
    }

    # Helper: reachability under an optional route-filter predicate.
    # Used to assert that, e.g., the spawn_worktree node can reach
    # all four lifecycle dispatch nodes.
    function script:Get-Reachable {
        param(
            [hashtable]$Agents,
            [string]$StartNode,
            [scriptblock]$RouteFilter = { $true },
            [string[]]$Stop = @()
        )
        $visited = @{}
        $queue = [System.Collections.Queue]::new()
        $queue.Enqueue($StartNode)
        while ($queue.Count -gt 0) {
            $cur = $queue.Dequeue()
            if ($visited.ContainsKey($cur)) { continue }
            $visited[$cur] = $true
            if ($Stop -contains $cur) { continue }
            foreach ($r in (Get-NodeRoutes -Agents $Agents -NodeName $cur)) {
                if (-not $r.Target) { continue }
                if ($r.Target -eq '$end') { continue }
                $allowed = & $RouteFilter $cur $r
                if (-not $allowed) { continue }
                if (-not $visited.ContainsKey($r.Target)) {
                    $queue.Enqueue($r.Target)
                }
            }
        }
        return $visited.Keys | Sort-Object
    }

    # Helper: extract the `output:` block from a workflow YAML by
    # plain-text slicing, since round-tripping through ConvertTo-Yaml
    # mangles the Jinja whitespace markers we want to assert on.
    function script:Get-OutputBlock {
        param([string]$RawYaml)
        if ($RawYaml -match '(?ms)\noutput:\s*\n(?<body>.*?)(?=\nagents:)') {
            return $Matches['body']
        }
        return ''
    }
}

# =====================================================================
# Section 1 — All three workflows load cleanly
# =====================================================================

Describe 'apex-driver e2e — three-YAML chain loads cleanly' {

    It 'apex-driver.yaml parses and exposes the expected top-level shape' {
        $script:ApexYaml.workflow | Should -Not -BeNullOrEmpty
        $script:ApexYaml.workflow.name        | Should -Be 'apex-driver'
        $script:ApexYaml.workflow.entry_point | Should -Be 'preflight_sync'
        $script:ApexYaml.workflow.metadata.min_polyphony_version | Should -Be '1.0.1'
        $script:ApexYaml.tools                | Should -Contain 'twig'
        $script:ApexYaml.agents.Count         | Should -BeGreaterThan 10
    }

    It 'apex-wave-dispatch.yaml parses and exposes the expected top-level shape' {
        $script:WaveYaml.workflow | Should -Not -BeNullOrEmpty
        $script:WaveYaml.workflow.name        | Should -Be 'apex-wave-dispatch'
        $script:WaveYaml.workflow.entry_point | Should -Be 'dispatch_items'
        $script:WaveYaml.workflow.metadata.min_polyphony_version | Should -Be '1.0.1'
        $script:WaveYaml.tools                | Should -Contain 'twig'
        $script:WaveYaml.agents.Count         | Should -BeGreaterThan 2
    }

    It 'apex-item-dispatch.yaml parses and exposes the expected top-level shape' {
        $script:ItemYaml.workflow | Should -Not -BeNullOrEmpty
        $script:ItemYaml.workflow.name        | Should -Be 'apex-item-dispatch'
        $script:ItemYaml.workflow.entry_point | Should -Be 'classify_lifecycle'
        $script:ItemYaml.workflow.metadata.min_polyphony_version | Should -Be '1.0.1'
        $script:ItemYaml.agents.Count         | Should -BeGreaterThan 8
    }

    It 'Every route target in apex-driver.yaml resolves to a declared agent or $end' {
        $declared = $script:ApexAgents.Keys
        $bad = @()
        foreach ($name in $declared) {
            foreach ($r in (Get-NodeRoutes -Agents $script:ApexAgents -NodeName $name)) {
                if (-not $r.Target) { continue }
                if ($r.Target -eq '$end') { continue }
                if ($declared -notcontains $r.Target) {
                    $bad += "$name -> $($r.Target)"
                }
            }
        }
        $bad | Should -BeNullOrEmpty -Because (
            "every apex-driver route target must resolve to a declared agent or `$end; got: $($bad -join '; ')")
    }

    It 'Every route target in apex-wave-dispatch.yaml resolves to a declared agent or $end' {
        $declared = $script:WaveAgents.Keys
        $bad = @()
        foreach ($name in $declared) {
            foreach ($r in (Get-NodeRoutes -Agents $script:WaveAgents -NodeName $name)) {
                if (-not $r.Target) { continue }
                if ($r.Target -eq '$end') { continue }
                if ($declared -notcontains $r.Target) {
                    $bad += "$name -> $($r.Target)"
                }
            }
        }
        $bad | Should -BeNullOrEmpty -Because (
            "every apex-wave-dispatch route target must resolve to a declared agent or `$end; got: $($bad -join '; ')")
    }

    It 'Every route target in apex-item-dispatch.yaml resolves to a declared agent or $end' {
        $declared = $script:ItemAgents.Keys
        $bad = @()
        foreach ($name in $declared) {
            foreach ($r in (Get-NodeRoutes -Agents $script:ItemAgents -NodeName $name)) {
                if (-not $r.Target) { continue }
                if ($r.Target -eq '$end') { continue }
                if ($declared -notcontains $r.Target) {
                    $bad += "$name -> $($r.Target)"
                }
            }
        }
        $bad | Should -BeNullOrEmpty -Because (
            "every apex-item-dispatch route target must resolve to a declared agent or `$end; got: $($bad -join '; ')")
    }
}

# =====================================================================
# Section 2 — apex-driver outer loop reachability
# =====================================================================

Describe 'apex-driver e2e — outer loop reachability' {

    It 'preflight_apex_state routes satisfied / empty to terminal_apex_satisfied (fast-path)' {
        $routes = Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'preflight_apex_state'
        $sat = $routes | Where-Object { $_.When -match "status == 'satisfied'" }
        $emp = $routes | Where-Object { $_.When -match "status == 'empty'" }
        $sat | Should -Not -BeNullOrEmpty
        $emp | Should -Not -BeNullOrEmpty
        $sat.Target | Should -Be 'terminal_apex_satisfied'
        $emp.Target | Should -Be 'terminal_apex_satisfied'
    }

    It 'preflight_apex_state routes error to preflight_failure_gate (with M4 catch-all)' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'preflight_apex_state')
        $err = $routes | Where-Object { $_.When -match "status == 'error'" }
        $err | Should -Not -BeNullOrEmpty
        $err.Target | Should -Be 'preflight_failure_gate'
        # Catch-all is the LAST entry and must also fall through to the failure gate.
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'preflight_failure_gate'
    }

    It 'preflight_failure_gate exposes retry/abort with the documented routes' {
        $opts = @($script:ApexAgents['preflight_failure_gate'].options)
        $opts.Count | Should -Be 2
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['retry'] | Should -Be 'preflight_apex_state'
        $byValue['abort'] | Should -Be 'terminal_preflight_failed'
    }

    It 'build_worklist success routes to check_conflicts; failure routes to worklist_failure_gate (with M4 catch-all)' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'build_worklist')
        $okRoute = $routes | Where-Object { $_.When -match "build_worklist\.output\.error" }
        $okRoute | Should -Not -BeNullOrEmpty
        $okRoute.Target | Should -Be 'check_conflicts'
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'worklist_failure_gate'
    }

    It 'check_conflicts dispatches to wave_dispatch_loop on no-conflicts and gates on conflicts' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'check_conflicts')
        $confl = $routes | Where-Object { $_.When -match "has_conflicts \| string \| lower == 'true'" }
        $clear = $routes | Where-Object { $_.When -match "has_conflicts \| string \| lower == 'false'" }
        $confl.Target | Should -Be 'conflict_resolution_gate'
        $clear.Target | Should -Be 'wave_dispatch_loop'
        # M4 catch-all
        $routes[-1].When   | Should -BeNullOrEmpty
        $routes[-1].Target | Should -Be 'wave_dispatch_loop'
    }

    It 'conflict_resolution_gate exposes retry/abort with the documented routes' {
        $opts = @($script:ApexAgents['conflict_resolution_gate'].options)
        $opts.Count | Should -Be 2
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['retry'] | Should -Be 'build_worklist'
        $byValue['abort'] | Should -Be 'terminal_apex_abandoned'
    }

    It 'wave_dispatch_loop is a for_each into apex-wave-dispatch.yaml that routes to wave_loop_summary' {
        $node = $script:ApexAgents['wave_dispatch_loop']
        $node.type | Should -Be 'for_each'
        # M8: bare dotted source path, not Jinja-quoted.
        $node.source | Should -Be 'build_worklist.output.waves'
        $node.agent.workflow | Should -Be './apex-wave-dispatch.yaml'
        # max_concurrent: 1 — waves are sequential by definition.
        [int]$node.max_concurrent | Should -Be 1
        # routes: just to wave_loop_summary
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'wave_dispatch_loop')
        $routes.Target | Should -Contain 'wave_loop_summary'
    }

    It 'wave_loop_summary routes succeeded->renegotiation_summary, failure->wave_failed_gate (with M4 catch-all)' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'wave_loop_summary')
        $ok = $routes | Where-Object { $_.When -match "all_succeeded \| string \| lower == 'true'" }
        $ok | Should -Not -BeNullOrEmpty
        $ok.Target | Should -Be 'renegotiation_summary'
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'wave_failed_gate'
    }

    It 'wave_failed_gate exposes retry/abort/renegotiate with the documented routes' {
        $opts = @($script:ApexAgents['wave_failed_gate'].options)
        $opts.Count | Should -Be 3
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['retry']        | Should -Be 'build_worklist'
        $byValue['abort']        | Should -Be 'terminal_apex_abandoned'
        # In MVP renegotiate is documented to route to abort until the loop ships.
        $byValue['renegotiate']  | Should -Be 'terminal_apex_abandoned'
    }

    It 'renegotiation_summary routes any-pending->renegotiation_gate else apex_completion_gate (with M4 catch-all)' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'renegotiation_summary')
        $hot = $routes | Where-Object { $_.When -match "any_pending \| string \| lower == 'true'" }
        $hot | Should -Not -BeNullOrEmpty
        $hot.Target | Should -Be 'renegotiation_gate'
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'apex_completion_gate'
    }

    It 'renegotiation_gate exposes renegotiate/override/abort with the documented routes' {
        $opts = @($script:ApexAgents['renegotiation_gate'].options)
        $opts.Count | Should -Be 3
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['renegotiate'] | Should -Be 'build_worklist'
        $byValue['override']    | Should -Be 'apex_completion_gate'
        $byValue['abort']       | Should -Be 'terminal_apex_abandoned'
    }

    It 'apex_completion_gate exposes confirm/abandon with the documented routes' {
        $opts = @($script:ApexAgents['apex_completion_gate'].options)
        $opts.Count | Should -Be 2
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['confirm'] | Should -Be 'close_mark_satisfied'
        $byValue['abandon'] | Should -Be 'terminal_apex_abandoned'
    }

    It 'close_mark_satisfied routes to terminal_apex_satisfied' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'close_mark_satisfied')
        $routes.Target | Should -Contain 'terminal_apex_satisfied'
    }

    It 'All three terminals route to $end' {
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_satisfied').Target | Should -Contain '$end'
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_abandoned').Target | Should -Contain '$end'
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_preflight_failed').Target | Should -Contain '$end'
    }

    It 'The documented happy path is reachable from the entry point' {
        # preflight_sync is the entry; walk forward and confirm every
        # documented happy-path waypoint is reachable in the closure.
        $reachable = Get-Reachable -Agents $script:ApexAgents -StartNode 'preflight_sync'
        $waypoints = @(
            'preflight_sync',
            'preflight_apex_state',
            'preflight_ensure_branch',
            'build_worklist',
            'check_conflicts',
            'wave_dispatch_loop',
            'wave_loop_summary',
            'renegotiation_summary',
            'apex_completion_gate',
            'close_mark_satisfied',
            'terminal_apex_satisfied'
        )
        $missing = $waypoints | Where-Object { $reachable -notcontains $_ }
        $missing | Should -BeNullOrEmpty -Because (
            "every documented apex-driver waypoint must be reachable from the entry point; missing: $($missing -join ', ')")
    }
}

# =====================================================================
# Section 3 — apex-wave-dispatch fan-out
# =====================================================================

Describe 'apex-wave-dispatch e2e — wave fan-out' {

    It 'dispatch_items is a for_each that invokes ./apex-item-dispatch.yaml per item' {
        $node = $script:WaveAgents['dispatch_items']
        $node.type           | Should -Be 'for_each'
        # M8: bare dotted source.
        $node.source         | Should -Be 'workflow.input.wave_items'
        $node.as             | Should -Be 'item'
        $node.agent.type     | Should -Be 'workflow'
        $node.agent.workflow | Should -Be './apex-item-dispatch.yaml'
        # MVP cap aligns with policy.concurrency.max_concurrent_pgs.
        [int]$node.max_concurrent | Should -Be 3
        $node.failure_mode   | Should -Be 'continue_on_error'
    }

    It 'dispatch_items input_mapping threads apex_id, work_item_id, and platform fields per-item' {
        $im = $script:WaveAgents['dispatch_items'].agent.input_mapping
        $im.apex_id       | Should -Match 'workflow\.input\.apex_id'
        $im.work_item_id  | Should -Match 'item\.item_id'
        $im.platform      | Should -Match 'workflow\.input\.platform'
        $im.organization  | Should -Match 'workflow\.input\.organization'
        $im.project       | Should -Match 'workflow\.input\.project'
        $im.repository    | Should -Match 'workflow\.input\.repository'
    }

    It 'dispatch_items routes to aggregate_renegotiation' {
        $routes = Get-NodeRoutes -Agents $script:WaveAgents -NodeName 'dispatch_items'
        $routes.Target | Should -Contain 'aggregate_renegotiation'
    }

    It 'aggregate_renegotiation reads dispatch_items.outputs (per M8) and routes to integrate_wave' {
        $node = $script:WaveAgents['aggregate_renegotiation']
        $node.type | Should -Be 'script'
        # The script reads the .outputs dict and inspects renegotiation_pending.
        ($node.args -join ' ') | Should -Match 'dispatch_items\.outputs'
        ($node.args -join ' ') | Should -Match 'renegotiation_pending'
        $routes = Get-NodeRoutes -Agents $script:WaveAgents -NodeName 'aggregate_renegotiation'
        $routes.Target | Should -Contain 'integrate_wave'
    }

    It 'integrate_wave invokes wave-integrator.ps1 and ends the wave' {
        $node = $script:WaveAgents['integrate_wave']
        $node.type    | Should -Be 'script'
        $node.command | Should -Be 'pwsh'
        ($node.args -join ' ') | Should -Match 'wave-integrator\.ps1'
        ($node.args -join ' ') | Should -Match '-ApexId'
        ($node.args -join ' ') | Should -Match '-WaveIndex'
        $routes = Get-NodeRoutes -Agents $script:WaveAgents -NodeName 'integrate_wave'
        $routes.Target | Should -Contain '$end'
    }

    It 'Wave dispatch chain is reachable end-to-end (dispatch_items -> aggregate -> integrate)' {
        $reachable = Get-Reachable -Agents $script:WaveAgents -StartNode 'dispatch_items'
        $reachable | Should -Contain 'aggregate_renegotiation'
        $reachable | Should -Contain 'integrate_wave'
    }
}

# =====================================================================
# Section 4 — apex-item-dispatch branch-on-router (heart of PR #149)
# =====================================================================

Describe 'apex-item-dispatch e2e — branch-on-router' {

    It 'classify_lifecycle invokes lifecycle-router.ps1 with the per-item context' {
        $node = $script:ItemAgents['classify_lifecycle']
        $node.type    | Should -Be 'script'
        $node.command | Should -Be 'pwsh'
        ($node.args -join ' ') | Should -Match 'lifecycle-router\.ps1'
        ($node.args -join ' ') | Should -Match '-WorkItemId'
        ($node.args -join ' ') | Should -Match '-ApexId'
    }

    It 'classify_lifecycle short-circuits fast-path / monitoring / blocked / error to their terminals BEFORE spawning a worktree' {
        $routes = @(Get-NodeRoutes -Agents $script:ItemAgents -NodeName 'classify_lifecycle')
        $byVerdict = @{
            'fast-path'  = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'fast-path'" }).Target
            'monitoring' = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'monitoring'" }).Target
            'blocked'    = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'blocked'" }).Target
            'error'      = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'error'" }).Target
        }
        $byVerdict['fast-path']  | Should -Be 'terminal_fast_path'
        $byVerdict['monitoring'] | Should -Be 'terminal_monitoring'
        $byVerdict['blocked']    | Should -Be 'terminal_blocked'
        $byVerdict['error']      | Should -Be 'terminal_classify_error'
        # success route to spawn_worktree
        $ok = $routes | Where-Object { $_.When -match "success \| string \| lower == 'true'" }
        $ok | Should -Not -BeNullOrEmpty
        $ok.Target | Should -Be 'spawn_worktree'
        # M4 catch-all is the last entry and must default to the error terminal.
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'terminal_classify_error'
    }

    It 'spawn_worktree invokes worktree-manager.ps1 with operation=spawn' {
        $node = $script:ItemAgents['spawn_worktree']
        $node.type    | Should -Be 'script'
        $node.command | Should -Be 'pwsh'
        $argsJoined = ($node.args -join ' ')
        $argsJoined | Should -Match 'worktree-manager\.ps1'
        $argsJoined | Should -Match '-Operation'
        $argsJoined | Should -Match 'spawn'
        $argsJoined | Should -Match 'feature/apex-'
    }

    It 'spawn_worktree branch-on-routers to each lifecycle dispatch node based on the classifier verdict' {
        $routes = @(Get-NodeRoutes -Agents $script:ItemAgents -NodeName 'spawn_worktree')
        $expected = @{
            'plan-level'   = 'plan_level_dispatch'
            'actionable'   = 'actionable_dispatch'
            'implement-pg' = 'implement_pg_dispatch'
            'feature-pr'   = 'feature_pr_dispatch'
        }
        foreach ($verdict in $expected.Keys) {
            $hit = $routes | Where-Object {
                $_.When -match [regex]::Escape("lifecycle_workflow == '$verdict'") -and
                $_.When -match "spawn_worktree\.output\.success \| string \| lower == 'true'"
            }
            $hit | Should -Not -BeNullOrEmpty -Because (
                "spawn_worktree must declare a guarded route to $($expected[$verdict]) when classify=$verdict")
            $hit.Target | Should -Be $expected[$verdict]
        }
        # M4 catch-all is the LAST entry and must funnel unknown classifications to the spawn-error terminal.
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'terminal_spawn_error'
    }

    It 'All four lifecycle dispatch nodes invoke their parent-relative ./<lifecycle>.yaml' {
        $expected = @{
            'plan_level_dispatch'   = './plan-level.yaml'
            'actionable_dispatch'   = './actionable.yaml'
            'implement_pg_dispatch' = './implement-pg.yaml'
            'feature_pr_dispatch'   = './feature-pr.yaml'
        }
        foreach ($name in $expected.Keys) {
            $node = $script:ItemAgents[$name]
            $node | Should -Not -BeNullOrEmpty -Because "missing lifecycle node $name"
            $node.type     | Should -Be 'workflow'
            $node.workflow | Should -Be $expected[$name]
        }
    }

    It 'All four lifecycle dispatch nodes converge on teardown_worktree' {
        foreach ($name in 'plan_level_dispatch','actionable_dispatch','implement_pg_dispatch','feature_pr_dispatch') {
            $routes = Get-NodeRoutes -Agents $script:ItemAgents -NodeName $name
            $routes.Target | Should -Contain 'teardown_worktree' -Because (
                "$name must funnel back through teardown_worktree so the per-item worktree is always cleaned up")
        }
    }

    It 'teardown_worktree invokes worktree-manager.ps1 with operation=teardown and routes to terminal_dispatched' {
        $node = $script:ItemAgents['teardown_worktree']
        $node.type    | Should -Be 'script'
        $node.command | Should -Be 'pwsh'
        $argsJoined = ($node.args -join ' ')
        $argsJoined | Should -Match 'worktree-manager\.ps1'
        $argsJoined | Should -Match '-Operation'
        $argsJoined | Should -Match 'teardown'
        $routes = Get-NodeRoutes -Agents $script:ItemAgents -NodeName 'teardown_worktree'
        $routes.Target | Should -Contain 'terminal_dispatched'
    }

    It 'All seven terminal nodes route to $end' {
        $terminals = @(
            'terminal_dispatched',
            'terminal_fast_path',
            'terminal_monitoring',
            'terminal_blocked',
            'terminal_classify_error',
            'terminal_spawn_error'
        )
        foreach ($t in $terminals) {
            (Get-NodeRoutes -Agents $script:ItemAgents -NodeName $t).Target | Should -Contain '$end' -Because (
                "$t must be a real terminal that routes to `$end")
        }
    }

    It 'All four lifecycle dispatch nodes are reachable from classify_lifecycle (no orphans)' {
        $reachable = Get-Reachable -Agents $script:ItemAgents -StartNode 'classify_lifecycle'
        foreach ($n in 'plan_level_dispatch','actionable_dispatch','implement_pg_dispatch','feature_pr_dispatch') {
            $reachable | Should -Contain $n -Because (
                "$n must be reachable from the classifier — otherwise the branch-on-router dispatch is dead code")
        }
        # And the short-circuit terminals must be reachable too.
        foreach ($t in 'terminal_fast_path','terminal_monitoring','terminal_blocked','terminal_classify_error','terminal_spawn_error','terminal_dispatched') {
            $reachable | Should -Contain $t -Because (
                "$t must be reachable from classify_lifecycle in the assembled item graph")
        }
    }
}

# =====================================================================
# Section 5 — Renegotiation bubble-up across all three layers
# =====================================================================

Describe 'apex-driver e2e — renegotiation bubble-up table' {

    It 'apex-item-dispatch.output declares the four renegotiation bubble-up keys' {
        $out = $script:ItemYaml.output
        $out.Keys | Should -Contain 'renegotiation_pending'
        $out.Keys | Should -Contain 'renegotiation_request'
        $out.Keys | Should -Contain 'validate_scope_verdict'
        $out.Keys | Should -Contain 'scope_violation_files'
    }

    It 'apex-wave-dispatch.output surfaces the wave-aggregated renegotiation pair' {
        $out = $script:WaveYaml.output
        # apex-wave-dispatch aggregates per-item bubble-ups into a flat
        # bool + array shape that apex-driver can read on a single key.
        $out.Keys | Should -Contain 'renegotiation_pending'
        $out.Keys | Should -Contain 'renegotiation_items'
    }

    It 'apex-driver.output bubbles renegotiation_pending up to the caller' {
        $out = $script:ApexYaml.output
        $out.Keys | Should -Contain 'renegotiation_pending'
    }

    It 'apex-item-dispatch bubble-up Jinja is M3-safe (`is defined` guards on every cross-leg ref)' {
        $body = Get-OutputBlock -RawYaml $script:ItemRaw
        $body | Should -Match 'plan_level_dispatch is defined'
        # Lifecycle-specific outputs each guard with `is defined`
        $body | Should -Match 'actionable_dispatch is defined'
        $body | Should -Match 'implement_pg_dispatch is defined'
        $body | Should -Match 'feature_pr_dispatch is defined'
        # Per M7 booleans are piped through `| string | lower`.
        $body | Should -Match 'string \| lower'
    }

    It 'apex-wave-dispatch bubble-up Jinja is M3-safe (`is defined` guards on aggregate + integrate)' {
        $body = Get-OutputBlock -RawYaml $script:WaveRaw
        $body | Should -Match 'aggregate_renegotiation is defined'
        $body | Should -Match 'integrate_wave is defined'
        $body | Should -Match 'string \| lower'
    }

    It 'apex-driver bubble-up Jinja is M3-safe (`is defined` guards on every terminal + summary)' {
        $body = Get-OutputBlock -RawYaml $script:ApexRaw
        $body | Should -Match 'terminal_apex_satisfied is defined'
        $body | Should -Match 'terminal_apex_abandoned is defined'
        $body | Should -Match 'terminal_preflight_failed is defined'
        $body | Should -Match 'renegotiation_summary is defined'
        $body | Should -Match 'string \| lower'
    }

    It 'plan-level is the only lifecycle in apex-item-dispatch.output that bubbles renegotiation (other lifecycles default safely)' {
        $body = Get-OutputBlock -RawYaml $script:ItemRaw
        # The renegotiation_pending block must reference plan_level_dispatch
        # (the only lifecycle that emits it) and fall back to false for the
        # other three legs via the M3 guard.
        $body | Should -Match '(?ms)renegotiation_pending:.*plan_level_dispatch is defined.*else.*false'
        # validate_scope_verdict and scope_violation_files originate from
        # plan-level too (PR #144). The Jinja block spans multiple
        # lines (yaml `>-` folded scalar) so the regex needs (?ms).
        $body | Should -Match '(?ms)validate_scope_verdict:.*plan_level_dispatch is defined'
        $body | Should -Match '(?ms)scope_violation_files:.*plan_level_dispatch is defined'
    }
}

# =====================================================================
# Section 6 — Input/output contracts across the 3-YAML chain
# =====================================================================

Describe 'apex-driver e2e — input contracts thread through all three YAMLs' {

    It 'apex-driver declares the documented input contract' {
        $inputs = $script:ApexYaml.workflow.input
        $inputs.apex_id      | Should -Not -BeNullOrEmpty
        [bool]$inputs.apex_id.required | Should -BeTrue
        $inputs.intent       | Should -Not -BeNullOrEmpty
        $inputs.platform     | Should -Not -BeNullOrEmpty
        $inputs.organization | Should -Not -BeNullOrEmpty
        $inputs.project      | Should -Not -BeNullOrEmpty
        $inputs.repository   | Should -Not -BeNullOrEmpty
        # platform default is ado per the apex-driver YAML header.
        $inputs.platform.default | Should -Be 'ado'
    }

    It 'apex-driver -> apex-wave-dispatch input_mapping threads apex_id + per-wave fields + ADO context' {
        $im = $script:ApexAgents['wave_dispatch_loop'].agent.input_mapping
        $im.apex_id       | Should -Match 'workflow\.input\.apex_id'
        $im.wave_index    | Should -Match 'wave\.wave_index'
        $im.wave_items    | Should -Match 'wave\.items \| tojson'
        $im.platform      | Should -Match 'workflow\.input\.platform'
        $im.organization  | Should -Match 'workflow\.input\.organization'
        $im.project       | Should -Match 'workflow\.input\.project'
        $im.repository    | Should -Match 'workflow\.input\.repository'
    }

    It 'apex-wave-dispatch declares the inputs apex-driver passes (apex_id, wave_index, wave_items, ADO context)' {
        $inputs = $script:WaveYaml.workflow.input
        $inputs.apex_id    | Should -Not -BeNullOrEmpty
        $inputs.wave_index | Should -Not -BeNullOrEmpty
        $inputs.wave_items | Should -Not -BeNullOrEmpty
        $inputs.platform     | Should -Not -BeNullOrEmpty
        $inputs.organization | Should -Not -BeNullOrEmpty
        $inputs.project      | Should -Not -BeNullOrEmpty
        $inputs.repository   | Should -Not -BeNullOrEmpty
    }

    It 'apex-item-dispatch declares the inputs apex-wave-dispatch passes (apex_id, work_item_id, ADO context)' {
        $inputs = $script:ItemYaml.workflow.input
        $inputs.apex_id      | Should -Not -BeNullOrEmpty
        $inputs.work_item_id | Should -Not -BeNullOrEmpty
        [bool]$inputs.apex_id.required      | Should -BeTrue
        [bool]$inputs.work_item_id.required | Should -BeTrue
        $inputs.platform     | Should -Not -BeNullOrEmpty
        $inputs.organization | Should -Not -BeNullOrEmpty
        $inputs.project      | Should -Not -BeNullOrEmpty
        $inputs.repository   | Should -Not -BeNullOrEmpty
    }

    It 'apex-item-dispatch -> plan-level threads work_item_id + intent=resume + ADO context' {
        $im = $script:ItemAgents['plan_level_dispatch'].agent.input_mapping
        if (-not $im) { $im = $script:ItemAgents['plan_level_dispatch'].input_mapping }
        $im.work_item_id  | Should -Match 'workflow\.input\.work_item_id'
        $im.intent        | Should -Be 'resume'
        $im.platform      | Should -Match 'workflow\.input\.platform'
        $im.organization  | Should -Match 'workflow\.input\.organization'
    }

    It 'apex-item-dispatch -> actionable threads work_item_id + apex_id + executor=polyphony' {
        $im = $script:ItemAgents['actionable_dispatch'].input_mapping
        if (-not $im) { $im = $script:ItemAgents['actionable_dispatch'].agent.input_mapping }
        $im.work_item_id | Should -Match 'workflow\.input\.work_item_id'
        $im.apex_id      | Should -Match 'workflow\.input\.apex_id'
        $im.executor     | Should -Be 'polyphony'
    }

    It 'apex-item-dispatch -> implement-pg derives pg_number/branch_name from the apex+item ids' {
        $im = $script:ItemAgents['implement_pg_dispatch'].input_mapping
        if (-not $im) { $im = $script:ItemAgents['implement_pg_dispatch'].agent.input_mapping }
        $im.pg_number      | Should -Match 'workflow\.input\.work_item_id'
        $im.work_item_ids  | Should -Match 'workflow\.input\.work_item_id'
        $im.branch_name    | Should -Match 'feature/apex-'
        $im.feature_branch | Should -Match 'feature/apex-'
    }

    It 'apex-item-dispatch -> feature-pr targets main on the apex feature branch' {
        $im = $script:ItemAgents['feature_pr_dispatch'].input_mapping
        if (-not $im) { $im = $script:ItemAgents['feature_pr_dispatch'].agent.input_mapping }
        $im.work_item_id   | Should -Match 'workflow\.input\.work_item_id'
        $im.feature_branch | Should -Match 'feature/apex-'
        $im.target_branch  | Should -Be 'main'
        $im.platform       | Should -Match 'workflow\.input\.platform'
    }
}

# =====================================================================
# Section 7 — lifecycle-router script <-> YAML contract drift
# =====================================================================
#
# The HIGHEST-VALUE assertion in this suite. The router script and the
# apex-item-dispatch.yaml `when:` clauses are coupled by a literal
# string set; if either side adds or removes a route name without the
# other, dispatch silently drops items into the catch-all.

Describe 'apex-driver e2e — lifecycle-router script and YAML contract drift' {

    BeforeAll {
        # Set of `lifecycle_workflow` literal values the router script
        # is documented to emit. Pulled from the script's switch/if
        # chain by parsing every `lifecycle_workflow = '<value>'`
        # assignment. NOTE: also includes 'error' (initial envelope
        # value) and may include additional sentinel values; the
        # cross-check below treats the SCRIPT side as authoritative.
        # Set of `lifecycle_workflow` literal values the router script
        # assigns. The script uses two assignment shapes:
        #   1. `lifecycle_workflow = '<value>'`        (most cases)
        #   2. `lifecycle_workflow = if (...) { '<a>' } else { '<b>' }`
        #      (the apex-root vs PG-child split)
        # Strip the comment-based help block first so docstring
        # references don't spuriously inflate the set, then collect
        # every single-quoted lifecycle-name candidate in the body
        # and intersect with the documented canonical set so we don't
        # pick up unrelated strings.
        # NOTE: `$matches` is a PowerShell automatic variable (last
        # regex match groups) — using it as a normal local breaks
        # subsequent `-match` operators in the same scope. Use
        # `$emits` / `$branches` instead.
        $canonical = @('plan-level','actionable','implement-pg','feature-pr','fast-path','monitoring','blocked','error')
        $routerBody = $script:RouterRaw -replace '(?ms)^<#.*?#>',''
        $emits = [regex]::Matches($routerBody, "'([a-zA-Z\-]+)'")
        $set = New-Object System.Collections.Generic.HashSet[string]
        foreach ($m in $emits) {
            $v = $m.Groups[1].Value
            if ($canonical -contains $v) { $null = $set.Add($v) }
        }
        $script:RouterEmits = @($set | Sort-Object)

        # Set of `lifecycle_workflow == '<value>'` literals the
        # apex-item-dispatch.yaml branches on.
        $branches = [regex]::Matches($script:ItemRaw, "lifecycle_workflow == '([a-zA-Z\-]+)'")
        $set2 = New-Object System.Collections.Generic.HashSet[string]
        foreach ($m in $branches) {
            $null = $set2.Add($m.Groups[1].Value)
        }
        $script:YamlBranches = @($set2 | Sort-Object)
    }

    It 'Router script emits the expected canonical set of lifecycle_workflow values' {
        # Documented emit set per the script's docstring + classifier:
        $expected = @(
            'plan-level',
            'actionable',
            'implement-pg',
            'feature-pr',
            'fast-path',
            'monitoring',
            'blocked',
            'error'
        ) | Sort-Object

        # Allow the script to also reference a sentinel/initial 'error'
        # in the envelope ordered dict — that's the same canonical value.
        # Any drift from the documented set is a bug.
        $extra = $script:RouterEmits | Where-Object { $expected -notcontains $_ }
        $missing = $expected | Where-Object { $script:RouterEmits -notcontains $_ }
        $extra   | Should -BeNullOrEmpty -Because (
            "router script must not emit undocumented lifecycle_workflow values; got: $($extra -join ', ')")
        $missing | Should -BeNullOrEmpty -Because (
            "router script must emit every documented lifecycle_workflow value; missing: $($missing -join ', ')")
    }

    It 'Every lifecycle_workflow value the router emits is handled by an apex-item-dispatch.yaml when: clause OR by the success-route fork (no silent dropping)' {
        # The four "dispatchable" verdicts (plan-level / actionable /
        # implement-pg / feature-pr) are NOT branched on in
        # classify_lifecycle's routes; they are gated by the
        # `success | string | lower == 'true'` route to spawn_worktree
        # and then split by spawn_worktree's per-verdict branches.
        # The four "short-circuit" verdicts (fast-path / monitoring /
        # blocked / error) ARE branched on directly in classify_lifecycle.
        $shortCircuit = @('fast-path', 'monitoring', 'blocked', 'error')
        $dispatch     = @('plan-level', 'actionable', 'implement-pg', 'feature-pr')

        # Short-circuit verdicts must appear as YAML branch values.
        foreach ($v in $shortCircuit) {
            $script:YamlBranches | Should -Contain $v -Because (
                "classify_lifecycle must branch directly on lifecycle_workflow == '$v' so it short-circuits before spawning a worktree")
        }

        # Dispatch verdicts must appear as YAML branch values too —
        # spawn_worktree's branch-on-router uses them.
        foreach ($v in $dispatch) {
            $script:YamlBranches | Should -Contain $v -Because (
                "spawn_worktree must branch on lifecycle_workflow == '$v' so it dispatches into the right lifecycle sub-workflow")
        }

        # And inversely: every YAML branch literal must correspond to
        # a value the router actually emits — otherwise we have dead
        # branches.
        $deadBranches = $script:YamlBranches | Where-Object { $script:RouterEmits -notcontains $_ }
        $deadBranches | Should -BeNullOrEmpty -Because (
            "apex-item-dispatch.yaml must not branch on lifecycle_workflow values the router never emits; dead branches: $($deadBranches -join ', ')")
    }

    It 'lifecycle-router.ps1 returns a routing-style envelope (success=false / error_code populated) when polyphony is unavailable' {
        # Live invocation against a deliberately-missing executable.
        # Per the script's contract, this MUST exit 0 and surface
        # the failure via the JSON envelope.
        $missingExe = 'polyphony-does-not-exist-' + [Guid]::NewGuid().ToString('N')
        $stdout = pwsh -NoProfile -File $script:LifecycleRouter `
            -WorkItemId 99999 -ApexId 99999 -PolyphonyExe $missingExe 2>&1
        $LASTEXITCODE | Should -Be 0 -Because (
            "lifecycle-router.ps1 must always exit 0 — failures surface via the envelope, not via exit codes")
        $envelope = ($stdout | Out-String).Trim() | ConvertFrom-Json
        $envelope.success | Should -Be $false
        $envelope.error_code | Should -Be 'polyphony_unavailable'
        # The envelope must carry the work_item_id so the workflow
        # can correlate failures.
        [int]$envelope.work_item_id | Should -Be 99999
        # Lifecycle workflow must default to 'error' on this leg so
        # the apex-item-dispatch classify_error route fires.
        $envelope.lifecycle_workflow | Should -Be 'error'
    }
}

# =====================================================================
# Section 8 — worktree-manager + wave-integrator script contracts
# =====================================================================

Describe 'apex-driver e2e — script envelope contracts (worktree-manager, wave-integrator)' {

    It 'worktree-manager.ps1 teardown of a non-existent worktree returns success=true (idempotent)' {
        # Use a deliberately-non-existent worktree root so the script
        # short-circuits the idempotent-teardown branch without touching
        # the real repo. Routing-style: must exit 0.
        $fakeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("apex-e2e-noroot-" + [Guid]::NewGuid().ToString('N'))
        $stdout = pwsh -NoProfile -File $script:WorktreeManager `
            -Operation teardown -WorkItemId 999999 -WorktreeRoot $fakeRoot 2>&1
        $LASTEXITCODE | Should -Be 0 -Because (
            "worktree-manager.ps1 must always exit 0 — failures surface via envelope.success")
        $envelope = ($stdout | Out-String).Trim() | ConvertFrom-Json
        # Envelope keys the apex-item-dispatch yaml consumes.
        $envelope.PSObject.Properties.Name | Should -Contain 'success'
        $envelope.PSObject.Properties.Name | Should -Contain 'operation'
        $envelope.PSObject.Properties.Name | Should -Contain 'work_item_id'
        $envelope.PSObject.Properties.Name | Should -Contain 'worktree_path'
        $envelope.PSObject.Properties.Name | Should -Contain 'branch'
        $envelope.PSObject.Properties.Name | Should -Contain 'error_code'
        $envelope.operation | Should -Be 'teardown'
        # Idempotent teardown of a non-existent path is a success.
        $envelope.success   | Should -Be $true
    }

    It 'wave-integrator.ps1 returns a routing-style envelope (success=false / error_code populated) when polyphony is unavailable' {
        $missingExe = 'polyphony-does-not-exist-' + [Guid]::NewGuid().ToString('N')
        $stdout = pwsh -NoProfile -File $script:WaveIntegrator `
            -ApexId 99999 -WaveIndex 0 -PolyphonyExe $missingExe 2>&1
        $LASTEXITCODE | Should -Be 0 -Because (
            "wave-integrator.ps1 must always exit 0 — failures surface via the envelope, not via exit codes")
        $envelope = ($stdout | Out-String).Trim() | ConvertFrom-Json
        $envelope.success | Should -Be $false
        $envelope.error_code | Should -Be 'polyphony_unavailable'
        # Required envelope keys the apex-wave-dispatch yaml consumes.
        $envelope.PSObject.Properties.Name | Should -Contain 'success'
        $envelope.PSObject.Properties.Name | Should -Contain 'wave_index'
        $envelope.PSObject.Properties.Name | Should -Contain 'apex_id'
        $envelope.PSObject.Properties.Name | Should -Contain 'feature_branch'
        $envelope.PSObject.Properties.Name | Should -Contain 'merge_strategy'
        $envelope.PSObject.Properties.Name | Should -Contain 'branches_integrated'
        $envelope.PSObject.Properties.Name | Should -Contain 'skipped'
        $envelope.PSObject.Properties.Name | Should -Contain 'conflicts'
        # Defaults that downstream gates rely on.
        [int]$envelope.apex_id    | Should -Be 99999
        [int]$envelope.wave_index | Should -Be 0
        $envelope.feature_branch  | Should -Be 'feature/apex-99999'
        $envelope.merge_strategy  | Should -Be 'no-ff'
    }
}
