BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-root-fallback-gate.ps1'
    $script:WorkflowYaml = Join-Path $PSScriptRoot '..' 'workflows' 'root-fallback-gate.yaml'
    $script:RegistryIndex = Join-Path $PSScriptRoot '..' 'index.yaml'
}

Describe 'lint-root-fallback-gate.ps1' {

    Context 'Production root-fallback-gate.yaml validation' {

        It 'Passes on the real root-fallback-gate.yaml' {
            $script:WorkflowYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Real root-fallback-gate.yaml passes the strict-undefined lint' {
            # Per M3 — every reference to a verb's output across the
            # router (only ONE terminal runs in any given execution)
            # must be guarded with `is defined`. Reuse the existing
            # workflow-wide lint to catch StrictUndefined trap regressions.
            $strict = Join-Path $PSScriptRoot 'lint-strict-undefined.ps1'
            $output = pwsh -NoProfile -File $strict 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Workflow is registered in registry/index.yaml' {
            $script:RegistryIndex | Should -Exist
            $idx = Get-Content $script:RegistryIndex -Raw
            $idx | Should -Match '(?m)^\s+root-fallback-gate:\s*$'
            $idx | Should -Match 'workflows/root-fallback-gate\.yaml'
        }
    }

    Context 'Structural requirements' {

        BeforeAll {
            # Helper: minimal valid YAML with all required structure for
            # root-fallback-gate.yaml. Mirrors the production shape just
            # enough to satisfy every check in lint-root-fallback-gate.ps1.
            $script:ValidYaml = @'
workflow:
  name: root-fallback-gate
  entry_point: load_policy
  metadata:
    min_polyphony_version: "1.0.1"
  limits:
    max_iterations: 20
  input:
    active_work_item_id:
      type: number

output:
  root_id: "{% if terminal_use_active_item_prompted is defined %}{{ workflow.input.active_work_item_id }}{% else %}0{% endif %}"
  decision: "{% if terminal_use_active_item_prompted is defined %}use_active_item{% else %}abort{% endif %}"
  auto_policy_applied: "{% if terminal_use_active_item_auto is defined %}true{% else %}false{% endif %}"

agents:
  - name: load_policy
    type: script
    command: polyphony
    args: ["policy", "load"]
    routes:
      - to: prompt_user
        when: "{{ load_policy.output.root_fallback.auto_decide == 'prompt' }}"
      - to: terminal_use_active_item_auto
        when: "{{ load_policy.output.root_fallback.auto_decide == 'use_active_item' }}"
      - to: terminal_abort_auto
        when: "{{ load_policy.output.root_fallback.auto_decide == 'abort' }}"
      - to: prompt_user

  - name: prompt_user
    type: human_gate
    prompt: "Pick one"
    options:
      - label: "Use active"
        value: use_active_item
        route: terminal_use_active_item_prompted
      - label: "Abort"
        value: abort
        route: terminal_abort_prompted

  - name: terminal_use_active_item_prompted
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "@{ decision = 'use_active_item'; auto_policy_applied = $false; root_id = 1 } | ConvertTo-Json -Compress"]
    routes:
      - to: $end

  - name: terminal_abort_prompted
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "@{ decision = 'abort'; auto_policy_applied = $false; root_id = 0 } | ConvertTo-Json -Compress"]
    routes:
      - to: $end

  - name: terminal_use_active_item_auto
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "@{ decision = 'auto_resolved'; auto_policy_applied = $true; root_id = 1 } | ConvertTo-Json -Compress"]
    routes:
      - to: $end

  - name: terminal_abort_auto
    type: script
    command: pwsh
    args: ["-NoProfile", "-Command", "@{ decision = 'abort'; auto_policy_applied = $true; root_id = 0 } | ConvertTo-Json -Compress"]
    routes:
      - to: $end
'@
        }

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-root-fallback-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Passes when all structural requirements are met' {
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $script:ValidYaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when active_work_item_id input is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+active_work_item_id:\s*\n\s+type: number\s*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input.*active_work_item_id'
        }

        It 'Fails when root_id output is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+root_id:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output.*root_id'
        }

        It 'Fails when decision output is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+decision:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output.*decision'
        }

        It 'Fails when auto_policy_applied output is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+auto_policy_applied:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output.*auto_policy_applied'
        }

        It 'Fails when load_policy node is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: load_policy', 'name: load_settings'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*load_policy'
        }

        It 'Fails when prompt_user node is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: prompt_user', 'name: ask_user'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*prompt_user'
        }

        It 'Fails when an auto terminal is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: terminal_use_active_item_auto', 'name: terminal_auto_pick'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-node.*terminal_use_active_item_auto'
        }

        It 'Fails when policy load is not invoked' {
            $yaml = ($script:ValidYaml) -replace 'args: \["policy", "load"\]', 'args: ["health"]'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-policy-load-call'
        }

        It 'Fails when a forbidden new verb appears (resolve-root-fallback)' {
            $yaml = ($script:ValidYaml) -replace 'args: \["policy", "load"\]', 'args: ["policy", "resolve-root-fallback", "--policy", "use_active_item"]'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'forbidden-new-verb'
        }

        It 'Fails when a route target references a non-existent agent' {
            $yaml = ($script:ValidYaml) -replace 'to: terminal_abort_auto', 'to: not_a_real_terminal'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'invalid-route-target'
        }

        It 'Fails when entry point is wrong' {
            $yaml = ($script:ValidYaml) -replace 'entry_point: load_policy', 'entry_point: prompt_user'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-entry-point'
        }

        It 'Fails when an Epic literal is in non-comment YAML' {
            $yaml = ($script:ValidYaml) -replace 'prompt: "Pick one"', 'prompt: "Pick one - Epic owners only"'
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'type-agnostic-violation'
        }

        It 'Fails when min_polyphony_version is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+min_polyphony_version:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-min-polyphony-version'
        }

        It 'Fails when an auto terminal emits the wrong decision value' {
            # Swap the prompted-pick `use_active_item` decision into the
            # auto terminal — auto MUST emit `auto_resolved`.
            $yaml = ($script:ValidYaml) -replace "(name: terminal_use_active_item_auto[\s\S]*?decision = ')auto_resolved", "`$1use_active_item"
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-terminal-decision.*terminal_use_active_item_auto'
        }

        It 'Fails when an auto terminal emits auto_policy_applied=$false' {
            $yaml = ($script:ValidYaml) -replace "(name: terminal_abort_auto[\s\S]*?auto_policy_applied = )\`$true", "`$1`$false"
            Set-Content (Join-Path $script:WorkflowsDir 'root-fallback-gate.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-root-fallback-gate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-terminal-auto-policy-applied.*terminal_abort_auto'
        }
    }
}
