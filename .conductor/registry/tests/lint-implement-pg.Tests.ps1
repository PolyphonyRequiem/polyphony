BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-implement-pg.ps1'
    $script:ImplementPgYaml = Join-Path $PSScriptRoot '..' 'workflows' 'implement-pg.yaml'
}

Describe 'lint-implement-pg.ps1' {

    Context 'Production implement-pg.yaml validation' {

        It 'Passes on the real implement-pg.yaml' {
            $script:ImplementPgYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Structural requirements' {

        BeforeAll {
            # Helper: minimal valid YAML with all required structure
            $script:ValidYaml = @'
workflow:
  name: implement-pg
  entry_point: pg_router
  input:
    work_item_id:
      type: number
    pg_number:
      type: number
    work_item_ids:
      type: array
    branch_name:
      type: string
    feature_branch:
      type: string

output:
  merged: "{{ scope_closer.exit_code == 0 }}"
  pr_url: "{{ pr_submit.output.pr_url }}"

agents:
  - name: pg_router
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: task_router
  - name: task_router
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: coder
        when: "{{ task_router.output.action == 'implement_task' }}"
      - to: dependency_check
        when: "{{ task_router.output.action == 'all_tasks_done' }}"
  - name: coder
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Implement a single task
    prompt: "Implement the task"
    routes:
      - to: task_reviewer
  - name: task_reviewer
    type: agent
    model: claude-sonnet-4.6
    description: Review task implementation
    prompt: "Review the implementation"
    routes:
      - to: task_completer
        when: "{{ task_reviewer.output.verdict == 'approved' }}"
      - to: coder
        when: "{{ task_reviewer.output.verdict == 'changes_requested' }}"
  - name: task_completer
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: task_router
  - name: dependency_check
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: dependency_gate
        when: "{{ dependency_check.output.status == 'blocked' }}"
      - to: issue_reviewer
        when: "{{ dependency_check.output.status == 'not_blocked' }}"
  - name: dependency_gate
    type: human_gate
    prompt: "Dependencies blocked"
    options:
      - label: "Wait"
        value: wait
        route: dependency_check
      - label: "Override"
        value: override
        route: issue_reviewer
      - label: "Reassign"
        value: reassign
        route: $end
  - name: issue_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review issue-level work
    prompt: "Review the PG"
    routes:
      - to: user_acceptance
        when: "{{ issue_reviewer.output.verdict == 'approved' }}"
      - to: task_router
        when: "{{ issue_reviewer.output.verdict == 'changes_requested' }}"
  - name: user_acceptance
    type: human_gate
    prompt: "Accept PG?"
    options:
      - label: "Accept"
        value: accepted
        route: pr_submit
      - label: "Changes"
        value: changes
        route: task_router
  - name: pr_submit
    type: agent
    model: claude-sonnet-4.6
    description: Create PR
    prompt: "Create the PR"
    routes:
      - to: pr_platform_router
  - name: pr_platform_router
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: pr_lifecycle_github
        when: "{{ pr_platform_router.output.platform == 'github' }}"
      - to: pr_lifecycle_ado
        when: "{{ pr_platform_router.output.platform == 'ado' }}"
  - name: pr_lifecycle_github
    type: workflow
    workflow: ./github-pr.yaml
    input_mapping:
      pr_number: "{{ pr_submit.output.pr_number }}"
    routes:
      - to: scope_closer
  - name: pr_lifecycle_ado
    type: workflow
    workflow: ./ado-pr.yaml
    input_mapping:
      pr_number: "{{ pr_submit.output.pr_number }}"
    routes:
      - to: scope_closer
  - name: scope_closer
    type: script
    command: pwsh
    args: ["-Command", "@{} | ConvertTo-Json"]
    routes:
      - to: $end
'@
        }

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-implement-pg-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-implement-pg.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Passes when all structural requirements are met' {
            $yaml = $script:ValidYaml
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Fails when work_item_id input is missing' {
            $yaml = ($script:ValidYaml) -replace 'work_item_id:', 'parent_item_id:'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input'
        }

        It 'Fails when merged output is missing' {
            $yaml = ($script:ValidYaml) -replace '(?m)^\s+merged:.*\n', ''
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output'
        }

        It 'Fails when task_router agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: task_router', 'name: task_dispatcher'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-task-loop-agent'
        }

        It 'Fails when coder agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: coder', 'name: implementer'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-task-loop-agent'
        }

        It 'Fails when coder uses wrong model' {
            $yaml = ($script:ValidYaml) -replace '(name: coder[\s\S]*?model: )claude-opus-4.7-1m-internal', '$1claude-sonnet-4.6'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-coder-model'
        }

        It 'Fails when issue_reviewer agent is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: issue_reviewer', 'name: pg_reviewer'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-issue-review-agent'
        }

        It 'Fails when issue_reviewer uses wrong model' {
            $yaml = ($script:ValidYaml) -replace '(name: issue_reviewer[\s\S]*?model: )claude-opus-4.7-1m-internal', '$1claude-sonnet-4.6'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-issue-reviewer-model'
        }

        It 'Fails when dependency_check is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: dependency_check', 'name: dep_checker'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-dependency-check'
        }

        It 'Fails when dependency_gate is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: dependency_gate', 'name: dep_gate'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-dependency-gate'
        }

        It 'Fails when dependency gate wait option is missing' {
            $yaml = ($script:ValidYaml) -replace 'value: wait', 'value: pause'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-gate-option'
        }

        It 'Fails when user_acceptance gate is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: user_acceptance', 'name: user_review'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-user-acceptance'
        }

        It 'Fails when github-pr.yaml sub-workflow is missing' {
            $yaml = ($script:ValidYaml) -replace '\./github-pr\.yaml', './some-pr.yaml'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-github-pr-subworkflow'
        }

        It 'Fails when scope_closer is missing' {
            $yaml = ($script:ValidYaml) -replace 'name: scope_closer', 'name: pg_closer'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-scope-closer'
        }

        It 'Fails when entry point is wrong' {
            $yaml = ($script:ValidYaml) -replace 'entry_point: pg_router', 'entry_point: task_router'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'wrong-entry-point'
        }

        It 'Fails when route target references nonexistent agent' {
            $yaml = ($script:ValidYaml) -replace 'to: scope_closer', 'to: nonexistent_closer'
            Set-Content (Join-Path $script:WorkflowsDir 'implement-pg.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-implement-pg.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'invalid-route-target'
        }
    }
}

