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
  merged: "{{ pr_lifecycle_github.output.merged | default(false) }}"
  pr_url: "{{ feature_pr_creator.output.pr_url | default('') }}"

agents:
  - name: feature_pr_creator
    type: script
    description: Create feature PR
    command: polyphony
    args:
      - "pr"
      - "create-feature-pr"
    routes:
      - to: pr_platform_router

  - name: pr_platform_router
    type: script
    description: Route to platform-specific PR lifecycle
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - "@{ platform = 'github' } | ConvertTo-Json"
    routes:
      - to: pr_lifecycle_github
        when: "{{ pr_platform_router.output.platform == 'github' }}"
      - to: pr_lifecycle_ado
        when: "{{ pr_platform_router.output.platform == 'ado' }}"

  - name: pr_lifecycle_github
    type: workflow
    description: GitHub PR lifecycle
    workflow: ./github-pr.yaml
    input_mapping:
      pr_number: "{{ feature_pr_creator.output.pr_number }}"
    routes:
      - to: $end
        when: "{{ pr_lifecycle_github.output.merged == true }}"
      - to: remediation_counter
        when: "{{ pr_lifecycle_github.output.merged == false }}"

  - name: pr_lifecycle_ado
    type: workflow
    description: ADO PR lifecycle
    workflow: ./ado-pr.yaml
    input_mapping:
      pr_number: "{{ feature_pr_creator.output.pr_number }}"
    routes:
      - to: $end
        when: "{{ pr_lifecycle_ado.output.merged == true }}"
      - to: remediation_counter
        when: "{{ pr_lifecycle_ado.output.merged == false }}"

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
    model: claude-opus-4.7
    description: Create addendum plan for remediation
    prompt: "Plan remediation"
    routes:
      - to: remediation_seeder

  - name: remediation_seeder
    type: agent
    model: claude-sonnet-4.6
    description: Seed remediation work items
    prompt: "Seed remediation tasks"
    routes:
      - to: pr_platform_router

  - name: feature_pr_creator_ado
    type: script
    description: Create feature PR on ADO
    command: polyphony
    args:
      - "pr"
      - "create-feature-ado"
    routes:
      - to: pr_lifecycle_ado

  - name: feature_pr_creator_failed_gate_ado
    type: human_gate
    prompt: "ADO feature PR creator failed - operator intervention required"
    options:
      - label: "Retry"
        value: retry
        route: feature_pr_creator_ado
      - label: "Abort"
        value: abort
        route: remediation_abort

  - name: feature_pr_updater_poster_ado
    type: script
    description: Post the feature_pr_updater comment to the ADO PR
    command: polyphony
    args:
      - "pr"
      - "post-comment-ado"
    routes:
      - to: pr_platform_router
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

        It 'Fails when feature_pr_creator node is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_pr_creator', 'some_other_creator'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-creator'
        }

        It 'Fails when pr_platform_router is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'pr_platform_router', 'some_other_router'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-platform-router'
        }

        It 'Fails when pr_lifecycle_github is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'pr_lifecycle_github', 'some_other_github'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-github-lifecycle'
        }

        It 'Fails when pr_lifecycle_ado is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'pr_lifecycle_ado', 'some_other_ado'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-ado-lifecycle'
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

        It 'Fails when no route to remediation_counter exists' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'to: remediation_counter', 'to: some_other_node'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'broken-remediation-loop'
        }

        # ── ADO platform parity (Phase 5 closeout) ─────────────────────────
        # These mutation tests guard the contract codified in
        # docs/decisions/ado-feature-pr-parity.md: the ADO leg of
        # feature-pr.yaml must hit every node a GitHub PR would, and the
        # legacy ado_remediation_not_supported_emitter short-circuit must
        # stay out.

        It 'Fails when feature_pr_creator_ado node is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_pr_creator_ado', 'some_other_ado_creator'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-ado-creator'
        }

        It 'Fails when feature_pr_creator_failed_gate_ado node is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_pr_creator_failed_gate_ado', 'some_other_gate_ado'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-ado-creator-failed-gate'
        }

        It 'Fails when feature_pr_updater_poster_ado node is missing' {
            $yaml = (Get-ValidFeaturePrYaml) -replace 'feature_pr_updater_poster_ado', 'some_other_poster'
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-ado-updater-poster'
        }

        It 'Fails when pr_lifecycle_ado has no remediation_counter route' {
            # Strip ONLY the ADO branch's remediation_counter route by
            # mangling it. The github branch's route is kept unchanged so
            # the broken-remediation-loop rule (Check 15) does not fire.
            $yaml = (Get-ValidFeaturePrYaml)
            $yaml = $yaml -replace 'pr_lifecycle_ado\.output\.merged == false', 'pr_lifecycle_ado.output.merged == FALSE_REMOVED'
            # Also rename the ADO route target so check 19 sees no route.
            # Build a per-block surgical edit with line-by-line scan.
            $newLines = @()
            $inAdo = $false
            foreach ($line in ($yaml -split "`n")) {
                if ($line -match '^\s*-\s*name:\s*pr_lifecycle_ado\s*$') { $inAdo = $true }
                elseif ($line -match '^\s*-\s*name:\s*\S+\s*$') { $inAdo = $false }
                if ($inAdo -and $line -match 'to:\s*remediation_counter') {
                    $newLines += ($line -replace 'remediation_counter', 'some_other_node_ado')
                } else {
                    $newLines += $line
                }
            }
            $yaml = $newLines -join "`n"
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-ado-remediation-route'
        }

        It 'Fails when legacy ado_remediation_not_supported_emitter is reintroduced' {
            $yaml = (Get-ValidFeaturePrYaml) + @'

  - name: ado_remediation_not_supported_emitter
    type: script
    description: Legacy short-circuit (regression bait)
    command: pwsh
    args:
      - "-Command"
      - "@{ merged = $false } | ConvertTo-Json"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'feature-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-feature-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'ado-remediation-stub-present'
        }
    }
}
