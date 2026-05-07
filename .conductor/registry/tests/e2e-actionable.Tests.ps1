<#
.SYNOPSIS
    End-to-end behavior tests for actionable.yaml.

.DESCRIPTION
    Phase 6 PR #8 capstone — the structural lint
    (`lint-actionable.ps1`) verifies presence-of-things; this suite
    walks the parsed workflow graph and asserts the END-TO-END
    BEHAVIORS the executor router promises:

      • Polyphony executor leg — happy path reaches workflow_completed
        through `ensure_evidence_branch → compose_addendum →
        actionable_agent → open_evidence_pr → evidence_floor_check →
        evidence_reviewer → merge_evidence_pr → workflow_completed`.
        Failure variants (floor-failed, reviewer request_changes,
        reviewer block, error gate) route to the documented gate.
      • Human executor leg — only `human_satisfaction_gate` fires;
        no polyphony-only node is a direct successor; the three
        gate options route to the documented terminals.
      • Cross-leg coverage — default executor is `polyphony`; unknown
        executor values surface via the router's `error` envelope and
        fall through the workflow's catch-all to `workflow_error_gate`
        before any side-effecting verb runs.
      • Agent prompt template threads inputs and upstream verb output
        the design pins (work_item_id, apex_id, evidence branch,
        compose_addendum's skills/mcps/guidance, revise-loop comment).

    These checks complement (do not duplicate) `lint-actionable.ps1`
    and the C# `RouteActionableExecutorScriptTests` — the former
    pins structural presence, the latter pins the router script's
    JSON envelope. This suite pins the GRAPH the workflow declares
    and the agent prompt's template threading.

    Approach: Pester structural+graph validation (Phase 6 PR #8
    "Approach B" per the PR brief). A workflow-execution harness
    that drives conductor with mocked tools does not exist in this
    repo today; bootstrapping one is too large for this PR and is
    deferred per the PR brief.
#>
[CmdletBinding()]
param()

BeforeAll {
    Import-Module powershell-yaml -Force

    $script:WorkflowsDir = Join-Path $PSScriptRoot '..' 'workflows'
    $script:WorkflowPath = Join-Path $script:WorkflowsDir 'actionable.yaml'
    $script:ScriptsDir   = Join-Path $PSScriptRoot '..' 'scripts'
    $script:RouterScript = Join-Path $script:ScriptsDir 'route-actionable-executor.ps1'

    $script:Yaml = ConvertFrom-Yaml (Get-Content $script:WorkflowPath -Raw)
    # Conductor workflow YAML layout: `workflow:` is the metadata
    # block; `agents:`, `output:`, `tools:` are TOP-LEVEL siblings
    # (not nested under `workflow:`). Index agents by name for O(1)
    # lookup.
    $script:Agents = @{}
    foreach ($a in $script:Yaml.agents) {
        $script:Agents[$a.name] = $a
    }

    # Helper: returns the route entries for a node. Conductor route
    # entries always have a `to` (script/agent) or `route` (human_gate
    # option). Normalize both shapes into a uniform list of { target,
    # when, value, label }.
    function script:Get-NodeRoutes {
        param([string]$NodeName)
        $node = $script:Agents[$NodeName]
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

    # Helper: returns the set of nodes reachable from $StartNode under
    # an optional route filter (`when`-text predicate). Used to assert
    # leg disjointness — the human leg must NOT reach polyphony-only
    # nodes via the executor_router's `executor == 'human'` route.
    function script:Get-Reachable {
        param(
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
            foreach ($r in (Get-NodeRoutes -NodeName $cur)) {
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
}

Describe 'actionable.yaml e2e — workflow loads cleanly' {

    It 'Parses as YAML and exposes the expected top-level shape' {
        $script:Yaml.workflow | Should -Not -BeNullOrEmpty
        $script:Yaml.workflow.name | Should -Be 'actionable'
        $script:Yaml.workflow.entry_point | Should -Be 'executor_router'
        $script:Yaml.agents.Count | Should -BeGreaterThan 10
    }

    It 'Has every route target resolve to a declared agent or $end' {
        $declared = $script:Agents.Keys
        $bad = @()
        foreach ($name in $declared) {
            foreach ($r in (Get-NodeRoutes -NodeName $name)) {
                if (-not $r.Target) { continue }
                if ($r.Target -eq '$end') { continue }
                if ($declared -notcontains $r.Target) {
                    $bad += "$name → $($r.Target)"
                }
            }
        }
        $bad | Should -BeNullOrEmpty -Because (
            "every route target must point at a declared agent or `$end; got: $($bad -join '; ')")
    }

    It 'Declares the polyphony executor input default' {
        $exec = $script:Yaml.workflow.input.executor
        $exec.default | Should -Be 'polyphony' -Because (
            "callers that omit executor must opt into the default polyphony leg")
    }
}

Describe 'actionable.yaml e2e — Polyphony executor leg' {

    It 'executor=polyphony routes from the router to ensure_evidence_branch' {
        # The router emits output.executor = 'polyphony'; the workflow's
        # first conditional route is the polyphony-leg entry.
        $routes = Get-NodeRoutes -NodeName 'executor_router'
        $polyRoute = $routes | Where-Object { $_.When -match "executor == 'polyphony'" }
        $polyRoute | Should -Not -BeNullOrEmpty -Because (
            "executor_router must declare a route conditioned on executor == 'polyphony'")
        $polyRoute.Target | Should -Be 'ensure_evidence_branch'
    }

    It 'Polyphony happy path reaches workflow_completed through every documented stage' {
        # Walk the documented happy-path edges (ignoring error/abort
        # branches) and assert the chain matches the design sketch.
        $expected = @(
            @{ From = 'ensure_evidence_branch';  To = 'compose_addendum';     Via = 'default-route' },
            @{ From = 'compose_addendum';        To = 'actionable_agent';     Via = 'default-route' },
            @{ From = 'actionable_agent';        To = 'open_evidence_pr';     Via = 'default-route' },
            @{ From = 'open_evidence_pr';        To = 'evidence_floor_check'; Via = 'default-route' },
            @{ From = 'evidence_floor_check';    To = 'evidence_reviewer';    Via = 'floor-pass' },
            @{ From = 'evidence_reviewer';       To = 'merge_evidence_pr';    Via = 'approve' },
            @{ From = 'merge_evidence_pr';       To = 'workflow_completed';   Via = 'merged==true' }
        )

        foreach ($edge in $expected) {
            $routes = Get-NodeRoutes -NodeName $edge.From
            $hit = $routes | Where-Object { $_.Target -eq $edge.To }
            $hit | Should -Not -BeNullOrEmpty -Because (
                "polyphony happy-path edge $($edge.From) → $($edge.To) [$($edge.Via)] must exist")
        }
    }

    It 'Floor failure routes to floor_failed_gate, not the reviewer' {
        $routes = Get-NodeRoutes -NodeName 'evidence_floor_check'
        $floorFail = $routes | Where-Object { $_.When -match 'passes_floor == false' }
        $floorFail | Should -Not -BeNullOrEmpty
        $floorFail.Target | Should -Be 'floor_failed_gate' -Because (
            "design pick #6: floor failure must NOT reach the LLM reviewer")
    }

    It 'floor_failed_gate exposes abort/retry/manual_complete with the documented routes' {
        $opts = @($script:Agents['floor_failed_gate'].options)
        $opts.Count | Should -Be 3
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['abort']            | Should -Be 'workflow_abandoned'
        $byValue['retry']            | Should -Be 'actionable_agent' -Because (
            "the retry path re-enters the agent — not the executor router — because the upstream verbs are idempotent")
        $byValue['manual_complete']  | Should -Be 'workflow_completed' -Because (
            "manual_complete asserts out-of-band satisfaction; the polyphony leg short-circuits to the satisfied terminal")
    }

    It 'evidence_reviewer routes approve / request_changes / block as documented' {
        $routes = Get-NodeRoutes -NodeName 'evidence_reviewer'
        $byOutcome = @{
            approve         = ($routes | Where-Object { $_.When -match "decision == 'approve'" }).Target
            request_changes = ($routes | Where-Object { $_.When -match "decision == 'request_changes'" }).Target
            block           = ($routes | Where-Object { $_.When -match "decision == 'block'" }).Target
        }
        $byOutcome['approve']         | Should -Be 'merge_evidence_pr'
        $byOutcome['request_changes'] | Should -Be 'revise_loop_gate'
        $byOutcome['block']           | Should -Be 'workflow_error_gate' -Because (
            "block always escalates to a human; never auto-approve a blocked decision")
    }

    It 'evidence_reviewer has a catch-all route to workflow_error_gate (M4)' {
        $routes = Get-NodeRoutes -NodeName 'evidence_reviewer'
        $catchAll = $routes | Where-Object { -not $_.When }
        $catchAll | Should -Not -BeNullOrEmpty -Because (
            "missing/malformed reviewer decisions must fail safely to the error gate, not raise 'No matching route found'")
        $catchAll[-1].Target | Should -Be 'workflow_error_gate'
    }

    It 'revise_loop_gate exposes retry/abandon with the documented routes' {
        $opts = @($script:Agents['revise_loop_gate'].options)
        $opts.Count | Should -Be 2
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['retry']   | Should -Be 'actionable_agent' -Because (
            "retry feeds reviewer feedback to a fresh agent invocation; both upstream verbs are idempotent")
        $byValue['abandon'] | Should -Be 'workflow_abandoned'
    }

    It 'compose_addendum errors route to the workflow_error_gate before the agent runs' {
        $routes = Get-NodeRoutes -NodeName 'compose_addendum'
        $errRoute = $routes | Where-Object { $_.When -match 'compose_addendum.output.error' }
        $errRoute | Should -Not -BeNullOrEmpty
        $errRoute.Target | Should -Be 'workflow_error_gate' -Because (
            "PR #5: a malformed addendum must fail safely; the agent never sees a partial envelope")
        # And the default route is the agent.
        $defaultRoute = $routes | Where-Object { -not $_.When }
        $defaultRoute.Target | Should -Be 'actionable_agent'
    }

    It 'merge_evidence_pr only declares a satisfied terminal when merged == true' {
        $routes = Get-NodeRoutes -NodeName 'merge_evidence_pr'
        $satisfied = $routes | Where-Object { $_.Target -eq 'workflow_completed' }
        $satisfied | Should -Not -BeNullOrEmpty
        $satisfied.When | Should -Match 'merged == true' -Because (
            "an unmerged PR must NOT reach workflow_completed — failure routes to the error gate")
    }

    It 'workflow_error_gate retry re-enters the executor router (operator can flip executor mid-recovery)' {
        $opts = @($script:Agents['workflow_error_gate'].options)
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['retry']   | Should -Be 'executor_router'
        $byValue['abandon'] | Should -Be 'workflow_abandoned'
    }
}

Describe 'actionable.yaml e2e — Polyphony agent prompt threading' {

    BeforeAll {
        $script:AgentPrompt = $script:Agents['actionable_agent'].prompt
    }

    It 'Threads work_item_id, apex_id, and evidence branch context into the agent prompt' {
        $script:AgentPrompt | Should -Match 'workflow\.input\.work_item_id'
        $script:AgentPrompt | Should -Match 'workflow\.input\.apex_id'
        $script:AgentPrompt | Should -Match 'ensure_evidence_branch\.output\.branch'
        $script:AgentPrompt | Should -Match 'ensure_evidence_branch\.output\.base_branch'
    }

    It 'Composes facet-profile addendum (skills + mcps + guidance + facets) into the agent prompt' {
        # PR #5 wired this in. The prompt MUST consume every field
        # compose_addendum produces; otherwise the addendum ships
        # without reaching the agent and the PR's value evaporates.
        $script:AgentPrompt | Should -Match 'compose_addendum\.output\.facets'
        $script:AgentPrompt | Should -Match 'compose_addendum\.output\.skills'
        $script:AgentPrompt | Should -Match 'compose_addendum\.output\.mcps'
        $script:AgentPrompt | Should -Match 'compose_addendum\.output\.guidance'
        $script:AgentPrompt | Should -Match 'compose_addendum\.output\.guidance_present'
    }

    It 'Surfaces reviewer feedback in the revise-loop re-entry block (guarded by is defined)' {
        $script:AgentPrompt | Should -Match 'revise_loop_gate is defined'
        $script:AgentPrompt | Should -Match 'evidence_reviewer is defined'
        $script:AgentPrompt | Should -Match 'evidence_reviewer\.output\.comment'
    }

    It 'Pins the agent to an opus model (capability requirement, not an arbitrary choice)' {
        $script:Agents['actionable_agent'].model    | Should -Match 'opus'
        $script:Agents['evidence_reviewer'].model   | Should -Match 'opus'
    }
}

Describe 'actionable.yaml e2e — Human executor leg' {

    It 'executor=human routes from the router to human_satisfaction_gate' {
        $routes = Get-NodeRoutes -NodeName 'executor_router'
        $humanRoute = $routes | Where-Object { $_.When -match "executor == 'human'" }
        $humanRoute | Should -Not -BeNullOrEmpty
        $humanRoute.Target | Should -Be 'human_satisfaction_gate'
    }

    It 'human_satisfaction_gate exposes satisfied/not_yet/abandoned with the documented routes' {
        $opts = @($script:Agents['human_satisfaction_gate'].options)
        $opts.Count | Should -Be 3
        $byValue = @{}
        foreach ($o in $opts) { $byValue[$o.value] = $o.route }
        $byValue['satisfied'] | Should -Be 'workflow_completed'
        $byValue['not_yet']   | Should -Be 'human_satisfaction_gate' -Because (
            "not_yet must self-loop so the operator can leave the workflow open without abandoning")
        $byValue['abandoned'] | Should -Be 'workflow_abandoned'
    }

    It 'Human leg never reaches a polyphony-only node when the operator stays on the human path' {
        # Reachability under the constraint: only follow the human
        # branch off the executor_router and never traverse the error
        # gate's retry edge (which intentionally re-enters at the
        # router and would conflate the legs).
        $polyOnly = @(
            'ensure_evidence_branch',
            'compose_addendum',
            'actionable_agent',
            'open_evidence_pr',
            'evidence_floor_check',
            'floor_failed_gate',
            'evidence_reviewer',
            'revise_loop_gate',
            'merge_evidence_pr'
        )

        $reachable = Get-Reachable -StartNode 'human_satisfaction_gate' `
            -Stop @('workflow_error_gate')

        $leak = $reachable | Where-Object { $polyOnly -contains $_ }
        $leak | Should -BeNullOrEmpty -Because (
            "the human leg must not transitively reach any polyphony-only node — got: $($leak -join ', ')")
    }
}

Describe 'actionable.yaml e2e — cross-leg coverage' {

    It 'Unknown executor values fall through to the workflow_error_gate (no side-effecting verb runs)' {
        # The router catch-all route (no `when:` clause) MUST be the
        # last entry on executor_router and MUST target the error
        # gate. Together with the router script's `error` envelope,
        # this is the contract for "garbage executor input does not
        # produce evidence."
        $routes = @(Get-NodeRoutes -NodeName 'executor_router')
        $routes.Count | Should -BeGreaterThan 2
        $catchAll = $routes[-1]
        $catchAll.When   | Should -BeNullOrEmpty -Because (
            "the catch-all must be unconditional and last")
        $catchAll.Target | Should -Be 'workflow_error_gate'
    }

    It 'Both terminals route to $end' {
        (Get-NodeRoutes -NodeName 'workflow_completed').Target | Should -Contain '$end'
        (Get-NodeRoutes -NodeName 'workflow_abandoned').Target | Should -Contain '$end'
    }

    It 'Workflow output map satisfies StrictUndefined when only one leg fires' {
        # M3 (StrictUndefined): every reference to a verb's output on
        # the cross-leg outputs must be guarded with `is defined` or a
        # default — otherwise rendering the workflow's output dict
        # crashes for the leg that did NOT produce that envelope.
        $outputBlock = $script:Yaml.output | ConvertTo-Yaml -ErrorAction SilentlyContinue
        if (-not $outputBlock) {
            # Fallback: re-read the workflow YAML and extract the
            # output: block as text (covers shapes ConvertTo-Yaml
            # may not round-trip cleanly).
            $raw = Get-Content $script:WorkflowPath -Raw
            if ($raw -match '(?ms)\noutput:\s*\n(?<body>.*?)\nagents:') {
                $outputBlock = $Matches['body']
            }
        }
        # All leg-specific outputs must guard with `is defined`.
        $outputBlock | Should -Match 'open_evidence_pr is defined'
        $outputBlock | Should -Match 'ensure_evidence_branch is defined'
        $outputBlock | Should -Match 'workflow_completed is defined'
    }
}

Describe 'actionable.yaml e2e — router script behavior (live execution)' {

    BeforeAll {
        function script:Invoke-Router {
            param([string]$ArgString)
            $stdout = pwsh -NoProfile -File $script:RouterScript $ArgString.Split(' ') 2>&1
            return [PSCustomObject]@{
                ExitCode = $LASTEXITCODE
                Json     = ($stdout | Out-String).Trim() | ConvertFrom-Json
            }
        }
    }

    It 'Router script exists alongside the workflow' {
        $script:RouterScript | Should -Exist
    }

    It 'executor=polyphony emits the polyphony envelope and exits 0' {
        $r = Invoke-Router '-WorkItemId 1 -Executor polyphony'
        $r.ExitCode | Should -Be 0
        $r.Json.executor     | Should -Be 'polyphony'
        $r.Json.work_item_id | Should -Be 1
        $r.Json.error        | Should -BeNullOrEmpty
    }

    It 'executor=human emits the human envelope and exits 0' {
        $r = Invoke-Router '-WorkItemId 7 -Executor human'
        $r.ExitCode | Should -Be 0
        $r.Json.executor     | Should -Be 'human'
        $r.Json.work_item_id | Should -Be 7
        $r.Json.error        | Should -BeNullOrEmpty
    }

    It 'Default (no -Executor flag) is polyphony — matches workflow.input.executor.default' {
        $r = Invoke-Router '-WorkItemId 99'
        $r.ExitCode | Should -Be 0
        $r.Json.executor | Should -Be 'polyphony'
        $r.Json.error    | Should -BeNullOrEmpty
    }

    It 'Unknown executor populates error but still exits 0 (workflow catch-all picks it up)' {
        $r = Invoke-Router '-WorkItemId 42 -Executor robot'
        $r.ExitCode | Should -Be 0
        $r.Json.error | Should -Match 'polyphony'
        $r.Json.error | Should -Match 'human'
        $r.Json.error | Should -Match 'robot'
    }
}
