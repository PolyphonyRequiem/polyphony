BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-apex-driver.ps1'
    $script:WorkflowsDir = Join-Path $PSScriptRoot '..' 'workflows'
    $script:ScriptsDir = Join-Path $PSScriptRoot '..' 'scripts'

    $script:ApexYaml = Join-Path $script:WorkflowsDir 'apex-driver.yaml'
    $script:WaveYaml = Join-Path $script:WorkflowsDir 'apex-wave-dispatch.yaml'
    $script:ItemYaml = Join-Path $script:WorkflowsDir 'apex-item-dispatch.yaml'

    $script:LifecycleRouterScript = Join-Path $script:ScriptsDir 'lifecycle-router.ps1'
    $script:WorktreeManagerScript = Join-Path $script:ScriptsDir 'worktree-manager.ps1'
    $script:WaveIntegratorScript = Join-Path $script:ScriptsDir 'wave-integrator.ps1'
}

Describe 'lint-apex-driver.ps1' {

    Context 'Production apex-driver.yaml validation' {

        It 'Passes on the real apex-driver.yaml + sub-workflows' {
            $script:ApexYaml | Should -Exist
            $script:WaveYaml | Should -Exist
            $script:ItemYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Real apex-driver.yaml passes the strict-undefined lint' {
            # Per M3 — every reference to a verb's output across divergent
            # routes must be guarded with `is defined`. Reuse the existing
            # lint to catch StrictUndefined trap regressions in the new YAMLs.
            $strict = Join-Path $PSScriptRoot 'lint-strict-undefined.ps1'
            $output = pwsh -NoProfile -File $strict 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'lifecycle-router.ps1 is present and parses' {
            $script:LifecycleRouterScript | Should -Exist
            $tokens = $null
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile(
                $script:LifecycleRouterScript, [ref]$tokens, [ref]$errors) | Out-Null
            $errors | Should -BeNullOrEmpty
        }

        It 'worktree-manager.ps1 is present and parses' {
            $script:WorktreeManagerScript | Should -Exist
            $tokens = $null
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile(
                $script:WorktreeManagerScript, [ref]$tokens, [ref]$errors) | Out-Null
            $errors | Should -BeNullOrEmpty
        }

        It 'wave-integrator.ps1 is present and parses' {
            $script:WaveIntegratorScript | Should -Exist
            $tokens = $null
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile(
                $script:WaveIntegratorScript, [ref]$tokens, [ref]$errors) | Out-Null
            $errors | Should -BeNullOrEmpty
        }
    }

    Context 'Structural requirements (synthetic fixtures)' {

        BeforeAll {
            # Minimal trio of YAMLs that satisfies every lint check.
            # Mirrors the production shape just enough to pass.
            $script:ValidApexYaml = @'
workflow:
  name: apex-driver
  entry_point: preflight_sync
  metadata:
    min_polyphony_version: "1.0.1"
  limits:
    max_iterations: 500
  input:
    apex_id:
      type: number
    intent:
      type: string

output:
  apex_id: "{{ workflow.input.apex_id }}"
  satisfied: "{% if t is defined %}true{% else %}false{% endif %}"
  ok: "{{ x | string | lower }}"

agents:
  - name: preflight_sync
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "Write-Host"]
    routes:
      - to: t
  - name: t
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "Write-Host"]
    routes:
      - to: $end
'@

            $script:ValidWaveYaml = @'
workflow:
  name: apex-wave-dispatch
  entry_point: integrate_wave
  metadata:
    min_polyphony_version: "1.0.1"
  limits:
    max_iterations: 50
  input:
    apex_id:
      type: number

output:
  ok: "{% if integrate_wave is defined %}true{% else %}false{% endif %}"
  flag: "{{ x | string | lower }}"

agents:
  - name: integrate_wave
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "wave-integrator.ps1"
    routes:
      - to: $end
'@

            $script:ValidItemYaml = @'
workflow:
  name: apex-item-dispatch
  entry_point: classify_lifecycle
  metadata:
    min_polyphony_version: "1.0.1"
  limits:
    max_iterations: 50
  input:
    apex_id:
      type: number
    work_item_id:
      type: number

output:
  ok: "{% if classify_lifecycle is defined %}true{% else %}false{% endif %}"
  flag: "{{ x | string | lower }}"

agents:
  - name: classify_lifecycle
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "lifecycle-router.ps1"
    routes:
      - to: lifecycle_dispatch_placeholder
  - name: lifecycle_dispatch_placeholder
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "worktree-manager.ps1"
    routes:
      - to: $end
'@

            # Set up a temp workflows + scripts dir layout that mirrors
            # the production .conductor/registry layout. The lint script
            # uses $PSScriptRoot/../workflows etc., so we sit a synthetic
            # tests/ dir above synthetic workflows/ + scripts/ siblings.
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("apex-lint-" + [Guid]::NewGuid().ToString('N'))
            $script:TempTests = Join-Path $script:TempRoot 'tests'
            $script:TempWorkflows = Join-Path $script:TempRoot 'workflows'
            $script:TempScripts = Join-Path $script:TempRoot 'scripts'
            New-Item -ItemType Directory -Force -Path $script:TempTests, $script:TempWorkflows, $script:TempScripts | Out-Null

            # Drop a copy of the lint script into the synthetic tests dir
            # so $PSScriptRoot resolves correctly.
            Copy-Item $script:LintScript -Destination (Join-Path $script:TempTests 'lint-apex-driver.ps1') -Force
            # Drop the strict-undefined lint too (the apex lint may delegate)
            $strict = Join-Path $PSScriptRoot 'lint-strict-undefined.ps1'
            if (Test-Path $strict) {
                Copy-Item $strict -Destination (Join-Path $script:TempTests 'lint-strict-undefined.ps1') -Force
            }
            # Stub the three companion scripts so Test-Path checks pass.
            'param() exit 0' | Out-File -FilePath (Join-Path $script:TempScripts 'lifecycle-router.ps1') -Encoding utf8
            'param() exit 0' | Out-File -FilePath (Join-Path $script:TempScripts 'worktree-manager.ps1') -Encoding utf8
            'param() exit 0' | Out-File -FilePath (Join-Path $script:TempScripts 'wave-integrator.ps1') -Encoding utf8
        }

        AfterAll {
            if (Test-Path $script:TempRoot) {
                Remove-Item -Recurse -Force $script:TempRoot -ErrorAction SilentlyContinue
            }
        }

        BeforeEach {
            # Reset all three workflow YAMLs to the valid baseline.
            $script:ValidApexYaml | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-driver.yaml') -Encoding utf8
            $script:ValidWaveYaml | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-wave-dispatch.yaml') -Encoding utf8
            $script:ValidItemYaml | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-item-dispatch.yaml') -Encoding utf8
        }

        It 'Passes on a synthetic baseline trio of YAMLs' {
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when apex-driver.yaml has the wrong workflow name' {
            $bad = $script:ValidApexYaml -replace 'name:\s*apex-driver', 'name: not-apex-driver'
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-driver.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'wrong-workflow-name'
        }

        It 'Fails when entry_point is not preflight_sync' {
            $bad = $script:ValidApexYaml -replace 'entry_point:\s*preflight_sync', 'entry_point: somewhere_else'
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-driver.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'wrong-entry-point'
        }

        It 'Fails when a required input is missing' {
            $bad = $script:ValidApexYaml -replace '(?ms)\s+intent:\s+type:\s*string', ''
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-driver.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match "missing-input"
        }

        It 'Fails when min_polyphony_version is missing' {
            $bad = $script:ValidApexYaml -replace 'min_polyphony_version:\s*"1\.0\.1"', ''
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-driver.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-min-polyphony-version'
        }

        It 'Fails when a hardcoded process-template type name is present (P5)' {
            # `\bEpic\b` requires non-word chars on both sides; underscores are
            # word chars, so place Epic on its own surrounded by spaces.
            $bad = $script:ValidApexYaml -replace 'apex_id: "\{\{ workflow\.input\.apex_id \}\}"', 'apex_id_label: "Epic apex"'
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-driver.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'type-agnostic-violation'
        }

        It "Fails when a route target doesn't match any agent (M4)" {
            $bad = $script:ValidApexYaml -replace '- to: t$', '- to: nonexistent_target'
            if ($bad -eq $script:ValidApexYaml) {
                # Fallback: do a multiline-friendly substitution.
                $bad = $script:ValidApexYaml -replace '(?m)^      - to: t\s*$', '      - to: nonexistent_target'
            }
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-driver.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'invalid-route-target'
        }

        It 'Fails when a sub-workflow output map omits is-defined guards (M3)' {
            $bad = $script:ValidWaveYaml -replace 'is defined', 'is whatever'
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-wave-dispatch.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-is-defined-guards'
        }

        It "Fails when a sub-workflow output map drops boolean coercion (M7)" {
            $bad = $script:ValidWaveYaml -replace '\|\s*string\s*\|\s*lower', ''
            $bad | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-wave-dispatch.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-bool-coercion'
        }

        # ── Phase 7 follow-up — per-item lifecycle dispatch wiring ─────
        # These tests live inside this Context (rather than a sibling)
        # because they reuse the synthetic temp tree set up in the
        # outer BeforeAll / BeforeEach. The dispatch-wiring lint check
        # only fires when the placeholder is removed, so each test
        # crafts a non-placeholder shape with one specific defect.

        It 'Fails when the dispatch shape lacks a named lifecycle branch (Phase 7 follow-up)' {
            $stripped = $script:ValidItemYaml -replace 'lifecycle_dispatch_placeholder', 'plan_level_dispatch'
            $stripped = $stripped + @"

  - name: actionable_dispatch
    type: workflow
    workflow: ./actionable.yaml
    routes:
      - to: `$end
  - name: implement_pg_dispatch
    type: workflow
    workflow: ./implement-pg.yaml
    routes:
      - to: `$end
"@
            $stripped | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-item-dispatch.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-lifecycle-branch'
        }

        It 'Fails when a lifecycle workflow file is not referenced (Phase 7 follow-up)' {
            $renamed = $script:ValidItemYaml -replace 'lifecycle_dispatch_placeholder', 'plan_level_dispatch'
            $renamed = $renamed + @"

  - name: actionable_dispatch
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "Write-Host"]
    routes:
      - to: `$end
  - name: implement_pg_dispatch
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "Write-Host"]
    routes:
      - to: `$end
  - name: feature_pr_dispatch
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "Write-Host"]
    routes:
      - to: `$end
"@
            $renamed | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-item-dispatch.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-lifecycle-workflow-ref'
        }

        It 'Fails when renegotiation bubble-up is not wired (Phase 7 follow-up)' {
            $bare = @'
workflow:
  name: apex-item-dispatch
  entry_point: classify_lifecycle
  metadata:
    min_polyphony_version: "1.0.1"
  limits:
    max_iterations: 50
  input:
    apex_id:
      type: number
    work_item_id:
      type: number

output:
  ok: "{% if classify_lifecycle is defined %}true{% else %}false{% endif %}"
  flag: "{{ x | string | lower }}"

agents:
  - name: classify_lifecycle
    type: script
    command: pwsh
    args: ["-NoProfile", "-File", "lifecycle-router.ps1"]
    routes:
      - to: plan_level_dispatch
  - name: plan_level_dispatch
    type: workflow
    workflow: ./plan-level.yaml
    routes:
      - to: $end
  - name: actionable_dispatch
    type: workflow
    workflow: ./actionable.yaml
    routes:
      - to: $end
  - name: implement_pg_dispatch
    type: workflow
    workflow: ./implement-pg.yaml
    routes:
      - to: $end
  - name: feature_pr_dispatch
    type: workflow
    workflow: ./feature-pr.yaml
    routes:
      - to: $end
'@
            $bare | Out-File -FilePath (Join-Path $script:TempWorkflows 'apex-item-dispatch.yaml') -Encoding utf8
            $lint = Join-Path $script:TempTests 'lint-apex-driver.ps1'
            $output = pwsh -NoProfile -File $lint 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-renegotiation-bubble-up'
        }
    }

    Context 'Per-item lifecycle dispatch wiring (Phase 7 follow-up — real YAMLs)' {

        It 'Real apex-item-dispatch.yaml has all four lifecycle dispatch nodes by name' {
            $itemContent = Get-Content $script:ItemYaml -Raw
            foreach ($n in @('plan_level_dispatch', 'actionable_dispatch', 'implement_pg_dispatch', 'feature_pr_dispatch')) {
                $itemContent | Should -Match "(?m)^\s*-\s+name:\s*$n\s*$"
            }
        }

        It 'Real apex-item-dispatch.yaml references all four lifecycle workflow files' {
            $itemContent = Get-Content $script:ItemYaml -Raw
            foreach ($r in @('./plan-level.yaml', './actionable.yaml', './implement-pg.yaml', './feature-pr.yaml')) {
                $itemContent.Contains($r) | Should -BeTrue -Because "Expected '$r' to be referenced from apex-item-dispatch.yaml"
            }
        }

        It 'Real apex-item-dispatch.yaml output map bubbles up plan_level_dispatch renegotiation_pending' {
            $itemContent = Get-Content $script:ItemYaml -Raw
            $itemContent | Should -Match 'plan_level_dispatch\.output\.renegotiation_pending'
        }

        It 'Real apex-wave-dispatch.yaml output map declares wave-aggregated renegotiation_pending' {
            $waveContent = Get-Content $script:WaveYaml -Raw
            $waveContent | Should -Match '(?m)^\s+renegotiation_pending:\s*'
        }

        It 'Real apex-driver.yaml output map declares apex-level renegotiation_pending rollup' {
            $apexContent = Get-Content $script:ApexYaml -Raw
            $apexContent | Should -Match '(?m)^\s+renegotiation_pending:\s*'
        }

        It 'Real apex-driver.yaml has a renegotiation_gate human gate and renegotiation_summary script' {
            $apexContent = Get-Content $script:ApexYaml -Raw
            $apexContent | Should -Match '(?m)^\s*-\s+name:\s*renegotiation_gate\s*$'
            $apexContent | Should -Match '(?m)^\s*-\s+name:\s*renegotiation_summary\s*$'
        }

        It 'Real apex-item-dispatch.yaml routes from spawn_worktree to all four lifecycle nodes' {
            $itemContent = Get-Content $script:ItemYaml -Raw
            foreach ($n in @('plan_level_dispatch', 'actionable_dispatch', 'implement_pg_dispatch', 'feature_pr_dispatch')) {
                $itemContent | Should -Match "to:\s*$n"
            }
        }
    }
}
