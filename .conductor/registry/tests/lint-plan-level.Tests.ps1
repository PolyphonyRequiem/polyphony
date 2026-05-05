BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-plan-level.ps1'
    $script:PlanLevelYaml = Join-Path $PSScriptRoot '..' 'workflows' 'plan-level.yaml'
}

Describe 'lint-plan-level.ps1' {

    Context 'Production plan-level.yaml validation' {

        It 'Passes on the real plan-level.yaml' {
            $script:PlanLevelYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Policy wiring checks' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-plan-level-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-plan-level.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when open_questions_policy node is missing' {
            # Minimal valid-looking yaml without the policy node
            $yaml = @'
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number

agents:
  - name: architect
    type: agent
    routes:
      - to: review_group
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
'@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-oq-policy-node'
        }

        It 'Fails when hardcoded severity list is present' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        `$capReached = `$count -ge `$maxLoops
        @{ iteration = `$count; cap_reached = `$capReached } | ConvertTo-Json
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
      - to: review_group
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }} of {{ open_questions_counter.output.max_loops }}
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: architect
    type: agent
    routes:
      - to: open_questions_gate
        when: "{{ architect.output.open_questions | selectattr('severity', 'in', ['moderate', 'major', 'critical']) | list | length > 0 }}"
      - to: review_group
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'hardcoded-severity-filter'
        }

        It 'Fails when mode=auto route is missing' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        @{ iteration = 0; cap_reached = `$false } | ConvertTo-Json
    routes:
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
      - to: review_group
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }} of {{ open_questions_counter.output.max_loops }}
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: architect
    type: agent
    routes:
      - to: open_questions_policy
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-auto-mode-route'
        }

        It 'Passes with all policy wiring present and no hardcoded severities' {
            $yaml = @"
workflow:
  name: plan-level
  entry_point: depth_guard
  input:
    work_item_id:
      type: number

agents:
  - name: open_questions_policy
    type: script
    command: polyphony
    args:
      - "policy"
      - "resolve"
      - "--domain"
      - "open_questions"
  - name: open_questions_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        @{ iteration = 0; cap_reached = `$false } | ConvertTo-Json
    routes:
      - to: review_group
        when: "{{ open_questions_policy.output.mode == 'auto' }}"
      - to: review_group
        when: "{{ open_questions_counter.output.cap_reached == true }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'manual' and architect.output.open_questions | length > 0 }}"
      - to: open_questions_gate
        when: "{{ open_questions_policy.output.mode == 'warning' and architect.output.open_questions | selectattr('severity', 'in', severities_at_or_above(open_questions_policy.output.min_severity)) | list | length > 0 }}"
      - to: review_group
  - name: open_questions_gate
    type: human_gate
    prompt: |
      Policy mode: {{ open_questions_policy.output.mode }}
      Loop: {{ open_questions_counter.output.iteration }} of {{ open_questions_counter.output.max_loops }}
  - name: open_questions_answer_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "echo test"
  - name: architect
    type: agent
    routes:
      - to: open_questions_policy
"@
            Set-Content (Join-Path $script:WorkflowsDir 'plan-level.yaml') $yaml
            $lintScript = Join-Path $script:TestsDir 'lint-plan-level.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }
}
