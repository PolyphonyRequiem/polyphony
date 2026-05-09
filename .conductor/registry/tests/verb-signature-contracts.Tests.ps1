<#
.SYNOPSIS
    Verb-signature contract tests for the apex-driver pipeline.

.DESCRIPTION
    Pins the EXACT polyphony CLI invocations the apex-driver pipeline
    makes — both inline `command: polyphony` agents in the three
    workflow YAMLs AND the `& $PolyphonyExe ...` calls inside the
    helper PowerShell scripts. Designed to catch the class of bug
    that took down the dogfood run against ADO Epic #3043:

      • Workflow agent passes `feature/N` positionally to
        `polyphony branch ensure-feature` — verb wants `--branch`.
      • Workflow agent passes `N --json` positionally to
        `polyphony worklist build` — verb wants `--root-id N --json`.
      • Workflow agent passes `--work-item N` to `polyphony edges
        check` — verb went positional in PR #158.
      • Helper script (wave-integrator.ps1) keeps the old
        `--work-item N` form after the same drift.
      • Workflow has no `init_manifest` step, so `worklist build`
        always errors with `manifest_not_found`.

    These tests assert the FIXED form is in place. They do NOT (yet)
    invoke `polyphony --help` to cross-check against the live CLI;
    that's a useful future hardening pass once we're comfortable
    relying on a polyphony binary on PATH during test runs.

    Convention:
      • One Describe block per agent / script call site.
      • Each It pins one signature property (verb, flag, positional).
      • Failure messages say WHAT changed and POINT to the verb help.
#>
[CmdletBinding()]
param()

BeforeAll {
    Import-Module powershell-yaml -Force

    $script:WorkflowsDir = Join-Path $PSScriptRoot '..' 'workflows'
    $script:ScriptsDir   = Join-Path $PSScriptRoot '..' 'scripts'

    $script:ApexYaml = ConvertFrom-Yaml (Get-Content (Join-Path $script:WorkflowsDir 'apex-driver.yaml') -Raw)

    function script:Get-AgentArgs {
        param([hashtable]$Yaml, [string]$AgentName)
        $agent = $Yaml.agents | Where-Object { $_.name -eq $AgentName } | Select-Object -First 1
        if (-not $agent) {
            throw "agent '$AgentName' not found in workflow"
        }
        return @($agent.args)
    }

    function script:Get-AgentCommand {
        param([hashtable]$Yaml, [string]$AgentName)
        $agent = $Yaml.agents | Where-Object { $_.name -eq $AgentName } | Select-Object -First 1
        if (-not $agent) { throw "agent '$AgentName' not found" }
        return $agent.command
    }
}

Describe 'apex-driver.yaml :: preflight_ensure_branch invokes branch ensure-feature with --branch' {
    It 'uses the polyphony command' {
        (Get-AgentCommand $script:ApexYaml 'preflight_ensure_branch') | Should -Be 'polyphony'
    }
    It 'first arg is "branch"' {
        (Get-AgentArgs $script:ApexYaml 'preflight_ensure_branch')[0] | Should -Be 'branch'
    }
    It 'second arg is "ensure-feature"' {
        (Get-AgentArgs $script:ApexYaml 'preflight_ensure_branch')[1] | Should -Be 'ensure-feature'
    }
    It 'passes branch via --branch flag (NOT positional)' {
        $args = Get-AgentArgs $script:ApexYaml 'preflight_ensure_branch'
        $args | Should -Contain '--branch'
        # The branch value should immediately follow the --branch flag.
        # Per branch-model spec: feature/{apex_id} (no apex- prefix; the
        # apex- sub-prefix was a YAML-side drift fixed in PR #176).
        $idx = [Array]::IndexOf($args, '--branch')
        $args[$idx + 1] | Should -Match '^feature/\{\{ workflow\.input\.apex_id \}\}$'
    }
    It 'does not pass the branch positionally (regression — pre-fix bug)' {
        $args = Get-AgentArgs $script:ApexYaml 'preflight_ensure_branch'
        # If the third arg is a feature/... string and there is no --branch,
        # it's the old broken shape.
        if ($args[2] -match '^feature/') {
            throw "preflight_ensure_branch is passing branch positionally — verb requires --branch flag"
        }
    }
}

Describe 'apex-driver.yaml :: init_manifest agent exists and runs before build_worklist' {
    It 'init_manifest agent is present' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'init_manifest' }
        $agent | Should -Not -BeNullOrEmpty
    }
    It 'init_manifest invokes pwsh -File manifest-bootstrap.ps1' {
        $args = Get-AgentArgs $script:ApexYaml 'init_manifest'
        (Get-AgentCommand $script:ApexYaml 'init_manifest') | Should -Be 'pwsh'
        ($args -join ' ') | Should -Match 'manifest-bootstrap\.ps1'
    }
    It 'init_manifest threads workflow inputs (apex_id, organization, project)' {
        $args = Get-AgentArgs $script:ApexYaml 'init_manifest'
        $argsText = $args -join ' '
        $argsText | Should -Match '\-ApexId'
        $argsText | Should -Match '\-Organization'
        $argsText | Should -Match '\-Project'
    }
    It 'init_manifest routes to declare_root on success' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'init_manifest' }
        $successRoute = $agent.routes | Where-Object { $_.to -eq 'declare_root' -and $_.when -match 'success' }
        $successRoute | Should -Not -BeNullOrEmpty
    }
    It 'init_manifest routes to preflight_failure_gate as fallback' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'init_manifest' }
        $failureTargets = $agent.routes | Where-Object { $_.to -eq 'preflight_failure_gate' }
        # At least one explicit failure-side route + the M4 catch-all.
        @($failureTargets).Count | Should -BeGreaterOrEqual 1
    }
    It 'preflight_ensure_branch routes to init_manifest on success (init_manifest is reachable)' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'preflight_ensure_branch' }
        $successRoute = $agent.routes | Where-Object { $_.to -eq 'init_manifest' }
        $successRoute | Should -Not -BeNullOrEmpty
    }
    It 'manifest-bootstrap.ps1 helper script exists on disk' {
        Test-Path (Join-Path $script:ScriptsDir 'manifest-bootstrap.ps1') | Should -BeTrue
    }
}

Describe 'apex-driver.yaml :: declare_root agent stamps polyphony:root before build_worklist' {
    # Per docs/polyphony-tags.md §"Workflow integration":
    #   "Entry: tree-walker receives root_id as input. Calls
    #    `polyphony root declare {root_id}` (idempotent) to stamp the root tag."
    #
    # If this agent is missing, every descendant's `polyphony root resolve`
    # call returns fallback_required=true and the descent gate fires.
    It 'declare_root agent is present' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'declare_root' }
        $agent | Should -Not -BeNullOrEmpty
    }
    It 'declare_root invokes polyphony root declare with --work-item' {
        (Get-AgentCommand $script:ApexYaml 'declare_root') | Should -Be 'polyphony'
        $args = Get-AgentArgs $script:ApexYaml 'declare_root'
        $argsText = $args -join ' '
        $argsText | Should -Match '^root declare'
        $argsText | Should -Match '\-\-work-item'
    }
    It 'declare_root threads apex_id from workflow input' {
        $args = Get-AgentArgs $script:ApexYaml 'declare_root'
        ($args -join ' ') | Should -Match 'workflow\.input\.apex_id'
    }
    It 'declare_root routes to outer_loop_init on success (PR #9 inserted outer_loop_init between declare_root and build_worklist; outer_loop_init then routes to build_worklist)' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'declare_root' }
        $successRoute = $agent.routes | Where-Object { $_.to -eq 'outer_loop_init' }
        $successRoute | Should -Not -BeNullOrEmpty
        # And outer_loop_init must hand off to build_worklist so the
        # original "declare_root reaches build_worklist" invariant still
        # holds transitively.
        $initAgent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'outer_loop_init' }
        $initRoute = $initAgent.routes | Where-Object { $_.to -eq 'build_worklist' }
        $initRoute | Should -Not -BeNullOrEmpty
    }
    It 'declare_root routes envelope errors to preflight_failure_gate' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'declare_root' }
        $failureRoute = $agent.routes | Where-Object {
            $_.to -eq 'preflight_failure_gate' -and $_.when -match 'declare_root\.output\.error'
        }
        $failureRoute | Should -Not -BeNullOrEmpty
    }
    It 'init_manifest routes to declare_root (declare_root is reachable)' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'init_manifest' }
        $successRoute = $agent.routes | Where-Object { $_.to -eq 'declare_root' }
        $successRoute | Should -Not -BeNullOrEmpty
    }
}

Describe 'apex-driver.yaml :: build_worklist invokes worklist build with --root-id' {
    It 'first three args are "worklist","build","--root-id"' {
        $args = Get-AgentArgs $script:ApexYaml 'build_worklist'
        $args[0] | Should -Be 'worklist'
        $args[1] | Should -Be 'build'
        $args[2] | Should -Be '--root-id'
    }
    It 'passes --json flag' {
        (Get-AgentArgs $script:ApexYaml 'build_worklist') | Should -Contain '--json'
    }
    It 'does not pass apex_id positionally (regression — pre-fix bug)' {
        $args = Get-AgentArgs $script:ApexYaml 'build_worklist'
        # Pre-fix shape was: ["worklist","build","{{ apex_id }}","--json"] (no --root-id).
        if ($args[2] -notmatch '^--' -and $args -notcontains '--root-id') {
            throw "build_worklist is passing apex_id positionally — verb requires --root-id flag"
        }
    }
}

Describe 'apex-driver.yaml :: check_conflicts invokes edges check positionally (post PR #158)' {
    It 'first two args are "edges","check"' {
        $args = Get-AgentArgs $script:ApexYaml 'check_conflicts'
        $args[0] | Should -Be 'edges'
        $args[1] | Should -Be 'check'
    }
    It 'passes apex_id positionally (NOT via --work-item)' {
        $args = Get-AgentArgs $script:ApexYaml 'check_conflicts'
        $args | Should -Not -Contain '--work-item'
        # The third arg should be the apex_id template substitution, not a flag.
        $args[2] | Should -Not -Match '^--'
        $args[2] | Should -Match 'apex_id'
    }
    It 'passes --render json' {
        $args = Get-AgentArgs $script:ApexYaml 'check_conflicts'
        $args | Should -Contain '--render'
        $idx = [Array]::IndexOf($args, '--render')
        $args[$idx + 1] | Should -Be 'json'
    }
}

Describe 'apex-driver.yaml :: check_conflicts has explicit error route to worklist_failure_gate' {
    It 'routes envelope errors before evaluating has_conflicts' {
        $agent = $script:ApexYaml.agents | Where-Object { $_.name -eq 'check_conflicts' }
        # Find the envelope-error route. It must exist AND come before the
        # has_conflicts routes so envelope errors don't get masked by the
        # has_conflicts string comparisons.
        $routes = $agent.routes
        $errorRouteIdx = -1
        $hasConflictsRouteIdx = -1
        for ($i = 0; $i -lt $routes.Count; $i++) {
            if ($routes[$i].when -match 'check_conflicts\.output\.error is defined') {
                $errorRouteIdx = $i
            }
            if ($hasConflictsRouteIdx -eq -1 -and $routes[$i].when -match 'has_conflicts') {
                $hasConflictsRouteIdx = $i
            }
        }
        $errorRouteIdx | Should -BeGreaterThan -1 -Because 'check_conflicts must explicitly route envelope errors'
        $errorRouteIdx | Should -BeLessThan $hasConflictsRouteIdx -Because 'envelope-error route must precede has_conflicts routes'
    }
}

Describe 'wave-integrator.ps1 :: edges check uses positional apex_id (post PR #158)' {
    BeforeAll {
        $script:WaveIntegrator = Join-Path $script:ScriptsDir 'wave-integrator.ps1'
        $script:WaveIntegratorRaw = Get-Content $script:WaveIntegrator -Raw
    }
    It 'invokes edges check positionally — no --work-item flag' {
        # Look for the actual invocation line, not docstring references.
        $invocationLines = $script:WaveIntegratorRaw -split "`n" | Where-Object {
            $_ -match '\$PolyphonyExe\s+edges\s+check'
        }
        $invocationLines | Should -Not -BeNullOrEmpty -Because 'wave-integrator must invoke edges check'
        foreach ($line in $invocationLines) {
            $line | Should -Not -Match '--work-item' -Because "wave-integrator drifted with PR #158: $line"
        }
    }
    It 'docstring references positional form' {
        # The header docstring should describe the current invocation form.
        $script:WaveIntegratorRaw | Should -Not -Match 'edges check --work-item' -Because 'docstring references stale --work-item form'
    }
}
