BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-github-pr.ps1'
    $script:GithubPrYaml = Join-Path $PSScriptRoot '..' 'workflows' 'github-pr.yaml'
}

Describe 'lint-github-pr.ps1' {

    Context 'Production github-pr.yaml validation' {

        It 'Passes on the real github-pr.yaml' {
            $script:GithubPrYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Interface contract' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-github-pr-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-github-pr.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when pr_number input is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_reviewer
  input:
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "{{ pr_merger.output.merged | default(false) }}"
  pr_url: "{{ pr_merger.output.pr_url | default('') }}"

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR"
    routes:
      - to: review_counter
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_reviewer.output.verdict == 'approved' }}"
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: pr_reviewer
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Force Merge"
        value: force_merge
        route: pr_merger
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input'
        }

        It 'Fails when merged output is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_reviewer
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  pr_url: ""

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR"
    routes:
      - to: review_counter
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_reviewer.output.verdict == 'approved' }}"
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: pr_reviewer
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Force Merge"
        value: force_merge
        route: pr_merger
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output'
        }

        It 'Fails when pr_reviewer agent is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: review_counter
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "false"
  pr_url: ""

agents:
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: review_counter
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Force Merge"
        value: force_merge
        route: pr_merger
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-reviewer'
        }

        It 'Fails when pr_merger agent is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_reviewer
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "false"
  pr_url: ""

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR"
    routes:
      - to: review_counter
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: pr_reviewer
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Force Merge"
        value: force_merge
        route: $end
      - label: "Abort"
        value: abort
        route: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-merger'
        }

        It 'Fails when human gate is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_reviewer
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "false"
  pr_url: ""

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: review_counter
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_reviewer.output.verdict == 'approved' }}"
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: $end
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_reviewer
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-human-gate'
        }

        It 'Fails when force_merge option is missing from human gate' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_reviewer
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "false"
  pr_url: ""

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: review_counter
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_reviewer.output.verdict == 'approved' }}"
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_reviewer
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-gate-option'
        }

        It 'Fails when entry point references non-existent agent' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: nonexistent_agent
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "false"
  pr_url: ""

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: review_counter
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_reviewer.output.verdict == 'approved' }}"
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_reviewer
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Force Merge"
        value: force_merge
        route: pr_merger
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'invalid-entry-point'
        }

        It 'Fails when review_counter is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_reviewer
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "false"
  pr_url: ""

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: pr_fixer
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_reviewer.output.verdict == 'approved' }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_reviewer
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Force Merge"
        value: force_merge
        route: pr_merger
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-counter'
        }

        It 'Passes when all contract requirements are met' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_reviewer
  input:
    pr_number:
      type: number
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "{{ pr_merger.output.merged | default(false) }}"
  pr_url: "{{ pr_merger.output.pr_url | default('') }}"

agents:
  - name: pr_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR"
    routes:
      - to: review_counter
        when: "{{ pr_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_reviewer.output.verdict == 'approved' }}"
  - name: review_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = ($count -le 10) } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ review_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ review_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_reviewer
  - name: pr_fix_exhausted_gate
    type: human_gate
    prompt: "Fix loop exhausted"
    options:
      - label: "Force Merge"
        value: force_merge
        route: pr_merger
      - label: "Continue"
        value: continue
        route: pr_fixer
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }
}
