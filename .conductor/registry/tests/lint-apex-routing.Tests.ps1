BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-apex-routing.ps1'
    $script:ApexYaml = Join-Path $PSScriptRoot '..' 'workflows' 'polyphony-full.yaml'
}

Describe 'lint-apex-routing.ps1' {

    Context 'Production apex YAML validation' {

        It 'Passes on the real polyphony-full.yaml' {
            $script:ApexYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Phase coverage' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-apex-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-apex-routing.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when a phase route is missing' {
            # Omit "removed" phase
            $yaml = @'
agents:
  - name: state_detector
    type: script
    routes:
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_planning' }}"
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_seeding' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'ready_for_implementation' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'in_progress' }}"
      - to: close_out
        when: "{{ state_detector.output.phase == 'ready_for_completion' }}"
      - to: $end
        when: "{{ state_detector.output.phase == 'done' }}"
  - name: planning
    type: workflow
    workflow: ./polyphony-planning.yaml
  - name: implementation
    type: workflow
    workflow: ./polyphony-implement.yaml
  - name: close_out
    type: workflow
    workflow: ./close-out.yaml
'@
            Set-Content (Join-Path $script:WorkflowsDir 'polyphony-full.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-apex-routing.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'removed'
        }

        It 'Fails when type-name literal appears in a routing condition' {
            $yaml = @'
agents:
  - name: state_detector
    type: script
    routes:
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_planning' }}"
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_seeding' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'ready_for_implementation' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'in_progress' }}"
      - to: close_out
        when: "{{ state_detector.output.phase == 'ready_for_completion' }}"
      - to: $end
        when: "{{ state_detector.output.phase == 'done' }}"
      - to: $end
        when: "{{ state_detector.output.phase == 'removed' }}"
      - to: planning
        when: "{{ state_detector.output.work_item_type == 'Epic' }}"
  - name: planning
    type: workflow
    workflow: ./polyphony-planning.yaml
  - name: implementation
    type: workflow
    workflow: ./polyphony-implement.yaml
  - name: close_out
    type: workflow
    workflow: ./close-out.yaml
'@
            Set-Content (Join-Path $script:WorkflowsDir 'polyphony-full.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-apex-routing.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'type-literal-in-route'
        }

        It 'Fails when sub-workflow agent missing type: workflow' {
            $yaml = @'
agents:
  - name: state_detector
    type: script
    routes:
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_planning' }}"
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_seeding' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'ready_for_implementation' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'in_progress' }}"
      - to: close_out
        when: "{{ state_detector.output.phase == 'ready_for_completion' }}"
      - to: $end
        when: "{{ state_detector.output.phase == 'done' }}"
      - to: $end
        when: "{{ state_detector.output.phase == 'removed' }}"
  - name: planning
    type: agent
    workflow: ./polyphony-planning.yaml
  - name: implementation
    type: workflow
    workflow: ./polyphony-implement.yaml
  - name: close_out
    type: workflow
    workflow: ./close-out.yaml
'@
            Set-Content (Join-Path $script:WorkflowsDir 'polyphony-full.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-apex-routing.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-workflow-type'
        }

        It 'Passes when all phases covered and no type literals' {
            $yaml = @'
agents:
  - name: state_detector
    type: script
    routes:
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_planning' }}"
      - to: planning
        when: "{{ state_detector.output.phase == 'needs_seeding' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'ready_for_implementation' }}"
      - to: implementation
        when: "{{ state_detector.output.phase == 'in_progress' }}"
      - to: close_out
        when: "{{ state_detector.output.phase == 'ready_for_completion' }}"
      - to: $end
        when: "{{ state_detector.output.phase == 'done' }}"
      - to: $end
        when: "{{ state_detector.output.phase == 'removed' }}"
  - name: planning
    type: workflow
    workflow: ./polyphony-planning.yaml
  - name: implementation
    type: workflow
    workflow: ./polyphony-implement.yaml
  - name: close_out
    type: workflow
    workflow: ./close-out.yaml
'@
            Set-Content (Join-Path $script:WorkflowsDir 'polyphony-full.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-apex-routing.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }
}
