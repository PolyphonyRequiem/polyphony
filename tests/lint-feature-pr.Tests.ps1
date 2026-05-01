BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-feature-pr.ps1'
    $script:FeaturePrYaml = Join-Path $PSScriptRoot '..' 'workflows' 'feature-pr.yaml'
}

Describe 'lint-feature-pr.ps1' {

    Context 'Production feature-pr.yaml validation' {

        It 'Passes on the real feature-pr.yaml' {
            $script:FeaturePrYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Interface contract' {

        BeforeAll {
            # Helper: generates a valid minimal feature-pr.yaml for mutation testing
            function script:Get-ValidFeaturePrYaml {
                return @'
workflow:
  name: feature-pr
  entry_point: feature_pr_creator
  input:
    work_item_id:
      type: number
    feature_branch:
      type: string
    target_branch:
      type: string

output:
  merged: "{{ feature_pr_merger.output.merged | default(false) }}"
  pr_url: "{{ feature_pr_merger.output.pr_url | default('') }}"

agents:
  - name: feature_pr_creator
    type: agent
    model: claude-sonnet-4-20250514
    description: Create feature PR
    prompt: "Create the feature PR"
    routes:
      - to: feature_pr_review

  - name: feature_pr_review
    type: agent
    model: claude-opus-4-20250514
    context_window: 1000000
    description: Review feature PR
    prompt: "Review the feature PR"
    routes:
      - to: feature_pr_merger
        when: "{{ feature_pr_review.output.verdict == 'approved' }}"
      - to: remediation_counter
        when: "{{ feature_pr_review.output.verdict == 'changes_requested' }}"

  - name: remediation_counter
    type: script
    description: Track remediation cycle count (max 3 cycles)
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - |
        $count = 1
        @{ iteration = $count; under_limit = ($count -lt 3) } | ConvertTo-Json
    routes:
      - to: remediation_planner
        when: "{{ remediation_counter.output.under_limit == true }}"
      - to: remediation_cap_gate
        when: "{{ remediation_counter.output.under_limit == false }}"

  - name: remediation_cap_gate
    type: human_gate
    prompt: "Remediation cycle cap reached (3 cycles)"
    options:
      - label: "Continue Anyway"
        value: continue
        route: remediation_planner
      - label: "Abort"
        value: abort
        route: remediation_abort

  - name: remediation_abort
    type: script
    description: Emit merged=false when remediation is aborted
    command: pwsh
    args:
      - "-Command"
      - "@{ merged = $false; pr_url = '' } | ConvertTo-Json"
    routes:
      - to: $end

  - name: remediation_planner
    type: agent
    model: claude-opus-4-20250514
    description: Create addendum plan for remediation
    prompt: "Plan remediation"
    routes:
      - to: remediation_seeder

  - name: remediation_seeder
    type: agent
    model: claude-sonnet-4-20250514
    description: Seed remediation work items
    prompt: "Seed remediation tasks"
    routes:
      - to: feature_pr_review

  - name: feature_pr_merger
    type: agent
    model: claude-sonnet-4-20250514
    description: Merge the approved feature PR
    prompt: "Merge the feature PR"
    routes:
      - to: $end
'@
            }
        }

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-feature-pr-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-feature-pr.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Passes when all contract requirements are met' {
            $yaml = Get-ValidFeaturePrYaml
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when work_item_id input is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'work_item_id:', 'xxx_work_item_id:'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input'
        }

        It 'Fails when feature_branch input is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_branch:', 'xxx_feature_branch:'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input'
        }

        It 'Fails when merged output is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'merged:', 'xxx_merged:'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output'
        }

        It 'Fails when feature_pr_creator agent is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_pr_creator', 'some_other_creator'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-creator'
        }

        It 'Fails when feature_pr_review agent is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_pr_review', 'some_other_review'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-reviewer'
        }

        It 'Fails when feature_pr_merger agent is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_pr_merger', 'some_other_merger'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-merger'
        }

        It 'Fails when remediation_counter is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'remediation_counter', 'some_other_counter'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-counter'
        }

        It 'Fails when remediation_cap_gate is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'remediation_cap_gate', 'some_other_gate'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-cap-gate'
        }

        It 'Fails when continue option is missing from cap gate' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'value: continue', 'value: retry'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-gate-option'
        }

        It 'Fails when abort option is missing from cap gate' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'value: abort', 'value: stop'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-gate-option'
        }

        It 'Fails when remediation_planner is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'remediation_planner', 'some_other_planner'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-planner'
        }

        It 'Fails when remediation_seeder is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'remediation_seeder', 'some_other_seeder'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-seeder'
        }

        It 'Fails when remediation_abort handler is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'remediation_abort', 'some_other_abort'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-abort-handler'
        }

        It 'Fails when entry point references non-existent agent' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'entry_point: feature_pr_creator', 'entry_point: nonexistent_agent'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'invalid-entry-point'
        }
    }
}
