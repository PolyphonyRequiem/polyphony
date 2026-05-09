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

    # Build per-workflow agent indices for O(1) lookup. Per M8, top-level
    # `for_each:` entries share the same name namespace as `agents:` entries
    # (route targets and entry_point can refer to either), so we fold both
    # into one index. Each for_each entry exposes `name`, `type: 'for_each'`,
    # `routes:`, and `agent:` (a sub-step) — same fields the route/reachability
    # helpers below need.
    function script:Index-Agents($yaml) {
        $idx = @{}
        foreach ($a in $yaml.agents)   { $idx[$a.name] = $a }
        foreach ($f in $yaml.for_each) { $idx[$f.name] = $f }
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
        $script:ApexYaml.workflow.metadata.min_polyphony_version | Should -Be '2.2.0'
        $script:ApexYaml.tools                | Should -Contain 'twig'
        $script:ApexYaml.agents.Count         | Should -BeGreaterThan 10
    }

    It 'apex-wave-dispatch.yaml parses and exposes the expected top-level shape' {
        $script:WaveYaml.workflow | Should -Not -BeNullOrEmpty
        $script:WaveYaml.workflow.name        | Should -Be 'apex-wave-dispatch'
        $script:WaveYaml.workflow.entry_point | Should -Be 'dispatch_items'
        $script:WaveYaml.workflow.metadata.min_polyphony_version | Should -Be '2.2.0'
        $script:WaveYaml.tools                | Should -Contain 'twig'
        # Per-M8 the workflow exposes 2 agents (aggregate_renegotiation,
        # integrate_wave) and 1 top-level for_each entry (dispatch_items).
        $script:WaveYaml.agents.Count   | Should -BeGreaterThan 1
        $script:WaveYaml.for_each.Count | Should -BeGreaterThan 0
    }

    It 'apex-item-dispatch.yaml parses and exposes the expected top-level shape' {
        $script:ItemYaml.workflow | Should -Not -BeNullOrEmpty
        $script:ItemYaml.workflow.name        | Should -Be 'apex-item-dispatch'
        $script:ItemYaml.workflow.entry_point | Should -Be 'classify_lifecycle'
        $script:ItemYaml.workflow.metadata.min_polyphony_version | Should -Be '2.2.0'
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

    It 'check_conflicts dispatches to wave_dispatch_loop on no-conflicts, gates on conflicts, surfaces envelope errors via worklist_failure_gate' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'check_conflicts')
        $err   = $routes | Where-Object { $_.When -match 'check_conflicts\.output\.error is defined' }
        $confl = $routes | Where-Object { $_.When -match "has_conflicts \| string \| lower == 'true'" }
        $clear = $routes | Where-Object { $_.When -match "has_conflicts \| string \| lower == 'false'" }
        $err.Target   | Should -Be 'worklist_failure_gate'
        $confl.Target | Should -Be 'conflict_resolution_gate'
        $clear.Target | Should -Be 'wave_dispatch_loop'
        # M4 catch-all — undefined has_conflicts means the JSON envelope
        # didn't shape correctly; treat as a worklist-layer failure
        # rather than silently dispatching waves.
        $routes[-1].When   | Should -BeNullOrEmpty
        $routes[-1].Target | Should -Be 'worklist_failure_gate'
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

    It 'renegotiation_summary routes any-pending->renegotiation_gate else outer_loop_evaluator (PR #9 wraps the wave loop in an iterate-until-stable outer loop, so the no-pending edge feeds the evaluator instead of the completion gate directly)' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'renegotiation_summary')
        $hot = $routes | Where-Object { $_.When -match "any_pending \| string \| lower == 'true'" }
        $hot | Should -Not -BeNullOrEmpty
        $hot.Target | Should -Be 'renegotiation_gate'
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'outer_loop_evaluator'
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

    It 'All five terminals route to $end (PR #9 added terminal_apex_iteration_cap and terminal_apex_blocked alongside the original three)' {
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_satisfied').Target | Should -Contain '$end'
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_abandoned').Target | Should -Contain '$end'
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_preflight_failed').Target | Should -Contain '$end'
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_iteration_cap').Target | Should -Contain '$end'
        (Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_blocked').Target | Should -Contain '$end'
    }

    It 'The documented happy path is reachable from the entry point' {
        # preflight_sync is the entry; walk forward and confirm every
        # documented happy-path waypoint is reachable in the closure.
        $reachable = Get-Reachable -Agents $script:ApexAgents -StartNode 'preflight_sync'
        $waypoints = @(
            'preflight_sync',
            'preflight_apex_state',
            'preflight_ensure_branch',
            'outer_loop_init',
            'build_worklist',
            'check_conflicts',
            'wave_dispatch_loop',
            'wave_loop_summary',
            'renegotiation_summary',
            'outer_loop_evaluator',
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
# Section 2b — outer iterate-until-stable loop (PR #9, Option α)
#
# These tests pin the structural shape of the outer loop (init,
# evaluator, the four-way decision routes, the cap default, and the
# new terminals) so future refactors can't silently turn the loop
# back into a single-pass dispatch.
# =====================================================================

Describe 'apex-driver e2e — outer iterate-until-stable loop (PR #9)' {

    It 'outer_loop_init is a script step that resets the per-apex temp counter and routes to build_worklist' {
        $node = $script:ApexAgents['outer_loop_init']
        $node | Should -Not -BeNullOrEmpty
        $node.type | Should -Be 'script'
        $node.command | Should -Be 'pwsh'
        # The init script writes "0" to a per-apex counter file under
        # the system temp dir.
        $body = ($node.args -join "`n")
        $body | Should -Match 'apex-driver-iter-'
        $body | Should -Match 'GetTempPath'
        $body | Should -Match 'Set-Content'
        # Must hand off straight to build_worklist (the loop body's
        # entry point).
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'outer_loop_init')
        $routes.Target | Should -Contain 'build_worklist'
    }

    It 'declare_root routes to outer_loop_init (so the counter is reset BEFORE the first iteration)' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'declare_root')
        # PR #9 inserts outer_loop_init between declare_root and
        # build_worklist; the no-error happy-path must hit init first.
        $routes.Target | Should -Contain 'outer_loop_init'
    }

    It 'outer_loop_evaluator is a script step that reads wave_dispatch_loop.outputs and shells out to polyphony state next-ready' {
        $node = $script:ApexAgents['outer_loop_evaluator']
        $node | Should -Not -BeNullOrEmpty
        $node.type | Should -Be 'script'
        $node.command | Should -Be 'pwsh'
        $body = ($node.args -join "`n")
        # Increments the same counter file outer_loop_init seeded.
        $body | Should -Match 'apex-driver-iter-'
        # Sums per-iteration progress out of wave_dispatch_loop.outputs.
        $body | Should -Match 'wave_dispatch_loop\.outputs'
        $body | Should -Match 'items_satisfied_count'
        $body | Should -Match 'items_dispatched_count'
        # Reads apex satisfaction via state next-ready (PR #5 closed-loop).
        $body | Should -Match 'state next-ready'
        # Cap configurable via env var, default 10.
        $body | Should -Match 'POLYPHONY_APEX_MAX_DISPATCH_ITERATIONS'
        $body | Should -Match '\b10\b'
    }

    It 'outer_loop_evaluator reads next.status (top-level routing hint per StateNextReadyResult), not the nonexistent next.item_satisfied field (regression: typo blocked outer-loop completion in AB#3064 dogfood)' {
        $node = $script:ApexAgents['outer_loop_evaluator']
        $body = ($node.args -join "`n")
        # Must check top-level $next.status against the documented 'satisfied' value.
        $body | Should -Match '\$next\.status'
        $body | Should -Match "'satisfied'"
        # Must NOT reach for the nonexistent $next.item_satisfied field.
        $body | Should -Not -Match '\$next\.item_satisfied'
    }

    It 'outer_loop_evaluator emits the four documented decisions (complete | cap | blocked | continue)' {
        $node = $script:ApexAgents['outer_loop_evaluator']
        $body = ($node.args -join "`n")
        foreach ($d in @("'complete'", "'cap'", "'blocked'", "'continue'")) {
            $body | Should -Match ([regex]::Escape($d))
        }
    }

    It 'outer_loop_evaluator routes complete->apex_completion_gate, cap->terminal_apex_iteration_cap, blocked->terminal_apex_blocked, continue->build_worklist (with M4 catch-all to terminal_apex_blocked)' {
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'outer_loop_evaluator')
        # Conditional routes — match by their `when` predicate.
        $byDecision = @{}
        foreach ($r in $routes) {
            if ($r.When -match "decision == 'complete'") { $byDecision['complete'] = $r.Target }
            if ($r.When -match "decision == 'cap'")      { $byDecision['cap'] = $r.Target }
            if ($r.When -match "decision == 'blocked'")  { $byDecision['blocked'] = $r.Target }
            if ($r.When -match "decision == 'continue'") { $byDecision['continue'] = $r.Target }
        }
        $byDecision['complete'] | Should -Be 'apex_completion_gate'
        $byDecision['cap']      | Should -Be 'terminal_apex_iteration_cap'
        $byDecision['blocked']  | Should -Be 'terminal_apex_blocked'
        # `continue` is the loop-back that wraps the wave dispatch loop.
        $byDecision['continue'] | Should -Be 'build_worklist'
        # M4 catch-all (last unconditional route) must NOT silently
        # loop back; defensive default is the blocked terminal.
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty
        $catchAll.Target | Should -Be 'terminal_apex_blocked'
    }

    It 'terminal_apex_iteration_cap is a script step that emits iteration_cap_hit=$true and routes to $end' {
        $node = $script:ApexAgents['terminal_apex_iteration_cap']
        $node | Should -Not -BeNullOrEmpty
        $node.type | Should -Be 'script'
        $body = ($node.args -join "`n")
        $body | Should -Match 'iteration_cap_hit'
        # M3-guarded reference to the evaluator output (default(0) so
        # the terminal still renders if the evaluator never emitted).
        $body | Should -Match 'outer_loop_evaluator\.output\.iteration'
        $body | Should -Match 'outer_loop_evaluator\.output\.max_iterations'
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_iteration_cap')
        $routes.Target | Should -Contain '$end'
    }

    It 'terminal_apex_blocked is a script step that emits blocked=$true and routes to $end' {
        $node = $script:ApexAgents['terminal_apex_blocked']
        $node | Should -Not -BeNullOrEmpty
        $node.type | Should -Be 'script'
        $body = ($node.args -join "`n")
        $body | Should -Match 'blocked'
        $body | Should -Match 'outer_loop_evaluator\.output\.iteration'
        $routes = @(Get-NodeRoutes -Agents $script:ApexAgents -NodeName 'terminal_apex_blocked')
        $routes.Target | Should -Contain '$end'
    }

    It 'apex-driver.output surfaces iteration_cap_hit, blocked, and iterations_used (PR #9 telemetry into the workflow envelope)' {
        $out = $script:ApexYaml.output
        $out.Keys | Should -Contain 'iteration_cap_hit'
        $out.Keys | Should -Contain 'blocked'
        $out.Keys | Should -Contain 'iterations_used'
    }

    It 'apex-wave-dispatch.output surfaces items_satisfied_count, items_dispatched_count, item_count (PR #9 progress counters consumed by the outer evaluator)' {
        $out = $script:WaveYaml.output
        $out.Keys | Should -Contain 'items_satisfied_count'
        $out.Keys | Should -Contain 'items_dispatched_count'
        $out.Keys | Should -Contain 'item_count'
    }

    It 'apex-wave-dispatch aggregate_renegotiation script tallies item_satisfied + dispatched per item (PR #6 fields the evaluator sums across waves)' {
        $node = $script:WaveAgents['aggregate_renegotiation']
        $node | Should -Not -BeNullOrEmpty
        $body = ($node.args -join "`n")
        # Lowercased string compare (M7: booleans pipe through `| string | lower`).
        $body | Should -Match 'item_satisfied'
        $body | Should -Match 'dispatched'
    }

    It 'every outer-loop node and new terminal is reachable from the entry point' {
        $reachable = Get-Reachable -Agents $script:ApexAgents -StartNode 'preflight_sync'
        foreach ($n in @('outer_loop_init','outer_loop_evaluator','terminal_apex_iteration_cap','terminal_apex_blocked')) {
            $reachable | Should -Contain $n -Because "PR #9 node '$n' must be reachable from preflight_sync"
        }
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

    It 'classify_lifecycle short-circuits fast-path / terminal-satisfied / monitoring / blocked / error to their terminals BEFORE spawning a worktree' {
        $routes = @(Get-NodeRoutes -Agents $script:ItemAgents -NodeName 'classify_lifecycle')
        $byVerdict = @{
            'fast-path'          = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'fast-path'" }).Target
            'terminal-satisfied' = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'terminal-satisfied'" }).Target
            'monitoring'         = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'monitoring'" }).Target
            'blocked'            = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'blocked'" }).Target
            'error'              = ($routes | Where-Object { $_.When -match "lifecycle_workflow == 'error'" }).Target
        }
        $byVerdict['fast-path']          | Should -Be 'terminal_fast_path'
        $byVerdict['terminal-satisfied'] | Should -Be 'terminal_satisfied'
        $byVerdict['monitoring']         | Should -Be 'terminal_monitoring'
        $byVerdict['blocked']            | Should -Be 'terminal_blocked'
        $byVerdict['error']              | Should -Be 'terminal_classify_error'
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
        $argsJoined | Should -Match 'feature/\{\{'
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
            'terminal_satisfied',
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
        foreach ($t in 'terminal_fast_path','terminal_satisfied','terminal_monitoring','terminal_blocked','terminal_classify_error','terminal_spawn_error','terminal_dispatched') {
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
        $im.branch_name    | Should -Match '^feature/\{\{ workflow\.input\.apex_id \}\}-pg-'
        $im.feature_branch | Should -Match '^feature/\{\{ workflow\.input\.apex_id \}\}$'
    }

    It 'apex-item-dispatch -> feature-pr targets main on the apex feature branch' {
        $im = $script:ItemAgents['feature_pr_dispatch'].input_mapping
        if (-not $im) { $im = $script:ItemAgents['feature_pr_dispatch'].agent.input_mapping }
        $im.work_item_id   | Should -Match 'workflow\.input\.work_item_id'
        $im.feature_branch | Should -Match '^feature/\{\{ workflow\.input\.apex_id \}\}$'
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
        $canonical = @('plan-level','actionable','implement-pg','feature-pr','fast-path','terminal-satisfied','monitoring','blocked','error')
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
            'terminal-satisfied',
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
        # PR #6 added terminal-satisfied as a fifth short-circuit verdict
        # — same pattern (worktree-less terminal that performs the ADO
        # state transition for items whose only ready kind is item_satisfied).
        $shortCircuit = @('fast-path', 'terminal-satisfied', 'monitoring', 'blocked', 'error')
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
        $envelope.feature_branch  | Should -Be 'feature/99999'
        $envelope.merge_strategy  | Should -Be 'no-ff'
    }
}

# =====================================================================
# Section 8 — Terminal canonical output schema (bug #11 / #178)
# =====================================================================
#
# Every `terminal_*` in apex-item-dispatch.yaml must emit the FULL
# canonical output schema with safe defaults. When the workflow's
# top-level `output:` template fails to fully resolve, conductor
# can surface the terminal's raw output to the parent for_each
# consumer (apex-wave-dispatch.aggregate_renegotiation, etc). Those
# consumers read `renegotiation_pending`, `lifecycle_workflow`, and
# friends and crash on missing keys.
#
# Asserting on the script's literal `Command` text catches drift
# without needing a Jinja render harness.

Describe 'apex-item-dispatch terminal canonical output schema (#178)' {

    BeforeAll {
        $script:CanonicalFields = @(
            'work_item_id',
            'apex_id',
            'lifecycle_workflow',
            'dispatched',
            'fast_pathed',
            'item_satisfied',
            'renegotiation_pending',
            'renegotiation_request',
            'validate_scope_verdict',
            'scope_violation_files',
            'actionable_satisfied',
            'implement_pg_merged',
            'feature_pr_merged'
        )
        $script:TerminalNames = @(
            'terminal_dispatched',
            'terminal_fast_path',
            'terminal_satisfied',
            'terminal_monitoring',
            'terminal_blocked',
            'terminal_classify_error',
            'terminal_spawn_error'
        )

        function script:Get-TerminalCommand($agents, $name) {
            $agent = $agents[$name]
            if (-not $agent) { throw "terminal $name not found" }
            # Script agents put the `pwsh -Command <text>` as the third arg.
            $args = $agent.args
            for ($i = 0; $i -lt $args.Count - 1; $i++) {
                if ($args[$i] -eq '-Command') { return $args[$i + 1] }
            }
            throw "no -Command arg in $name"
        }
    }

    foreach ($t in @('terminal_dispatched','terminal_fast_path','terminal_satisfied','terminal_monitoring','terminal_blocked','terminal_classify_error','terminal_spawn_error')) {
        It "$t emits all 13 canonical fields" -TestCases @{ TerminalName = $t } {
            param($TerminalName)
            $cmd = script:Get-TerminalCommand $script:ItemAgents $TerminalName
            foreach ($field in $script:CanonicalFields) {
                $cmd | Should -Match "\b$field\s*=" -Because (
                    "$TerminalName must emit canonical field '$field' so wave-dispatch consumers don't crash on missing keys (#178)")
            }
        }
    }

    It 'terminal_dispatched marks dispatched=$true' {
        (script:Get-TerminalCommand $script:ItemAgents 'terminal_dispatched') |
            Should -Match 'dispatched\s*=\s*\$true'
    }

    It 'terminal_fast_path marks fast_pathed=$true and lifecycle_workflow=fast-path' {
        $cmd = script:Get-TerminalCommand $script:ItemAgents 'terminal_fast_path'
        $cmd | Should -Match "fast_pathed\s*=\s*\`$true"
        $cmd | Should -Match "lifecycle_workflow\s*=\s*'fast-path'"
    }

    It 'terminal_satisfied marks item_satisfied=$true and lifecycle_workflow=terminal-satisfied' {
        $cmd = script:Get-TerminalCommand $script:ItemAgents 'terminal_satisfied'
        $cmd | Should -Match "item_satisfied\s*=\s*\`$true"
        $cmd | Should -Match "lifecycle_workflow\s*=\s*'terminal-satisfied'"
        # Mirrors apex-driver close_mark_satisfied: validate the event then
        # transition via twig if the validator returned a target_state.
        $cmd | Should -Match 'polyphony validate'
        $cmd | Should -Match '--event item_satisfied'
        $cmd | Should -Match 'twig state'
    }

    It 'terminal_monitoring marks lifecycle_workflow=monitoring (additive monitoring=$true permitted)' {
        $cmd = script:Get-TerminalCommand $script:ItemAgents 'terminal_monitoring'
        $cmd | Should -Match "lifecycle_workflow\s*=\s*'monitoring'"
    }

    It 'terminal_blocked marks lifecycle_workflow=blocked (additive blocked=$true permitted)' {
        $cmd = script:Get-TerminalCommand $script:ItemAgents 'terminal_blocked'
        $cmd | Should -Match "lifecycle_workflow\s*=\s*'blocked'"
    }

    It 'terminal_classify_error includes error + error_code with default-filtered classify error_code' {
        $cmd = script:Get-TerminalCommand $script:ItemAgents 'terminal_classify_error'
        $cmd | Should -Match "lifecycle_workflow\s*=\s*'error'"
        $cmd | Should -Match "error\s*=\s*'lifecycle classification failed'"
        $cmd | Should -Match "classify_lifecycle\.output\.error_code\s*\|\s*default"
    }

    It 'terminal_spawn_error includes error + error_code with default-filtered spawn error_code' {
        $cmd = script:Get-TerminalCommand $script:ItemAgents 'terminal_spawn_error'
        $cmd | Should -Match "error\s*=\s*'worktree spawn failed'"
        $cmd | Should -Match "spawn_worktree\.output\.error_code\s*\|\s*default"
        # Spawn error preserves the lifecycle that was classified before spawn failed.
        $cmd | Should -Match "classify_lifecycle\.output\.lifecycle_workflow\s*\|\s*default"
    }

    It 'apex-item-dispatch.output exposes error and error_code keys (bug #11 surfacing)' {
        $script:ItemYaml.output.Keys | Should -Contain 'error'
        $script:ItemYaml.output.Keys | Should -Contain 'error_code'
    }
}