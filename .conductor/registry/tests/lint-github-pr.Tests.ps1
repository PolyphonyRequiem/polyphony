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
  entry_point: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR"
    routes:
      - to: revise_counter
        when: "{{ pr_initial_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_initial_reviewer.output.verdict == 'approved' }}"
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ revise_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ revise_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: pr_initial_reviewer
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
  entry_point: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR"
    routes:
      - to: revise_counter
        when: "{{ pr_initial_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_initial_reviewer.output.verdict == 'approved' }}"
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ revise_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ revise_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: pr_initial_reviewer
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

        It 'Fails when pr_initial_reviewer agent is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: revise_counter
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
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ revise_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ revise_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: revise_counter
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
            ($output | Out-String) | Should -Match 'missing-initial-reviewer'
        }

        It 'Fails when pr_merger agent is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR"
    routes:
      - to: revise_counter
        when: "{{ pr_initial_reviewer.output.verdict == 'changes_requested' }}"
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ revise_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ revise_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues. Max 10 iterations."
    routes:
      - to: pr_initial_reviewer
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
  entry_point: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: revise_counter
        when: "{{ pr_initial_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_initial_reviewer.output.verdict == 'approved' }}"
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ revise_counter.output.under_limit == true }}"
      - to: $end
        when: "{{ revise_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_initial_reviewer
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
  entry_point: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: revise_counter
        when: "{{ pr_initial_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_initial_reviewer.output.verdict == 'approved' }}"
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ revise_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ revise_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: revise_counter
        when: "{{ pr_initial_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_initial_reviewer.output.verdict == 'approved' }}"
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ iteration = 1; under_limit = $true } | ConvertTo-Json"
    routes:
      - to: pr_fixer
        when: "{{ revise_counter.output.under_limit == true }}"
      - to: pr_fix_exhausted_gate
        when: "{{ revise_counter.output.under_limit == false }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_initial_reviewer
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

        It 'Fails when revise_counter is missing' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.7-1m-internal
    context_window: 1000000
    description: Review PR
    prompt: "Review the PR. Max 10 iterations."
    routes:
      - to: pr_fixer
        when: "{{ pr_initial_reviewer.output.verdict == 'changes_requested' }}"
      - to: pr_merger
        when: "{{ pr_initial_reviewer.output.verdict == 'approved' }}"
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Fix PR issues
    prompt: "Fix the PR issues"
    routes:
      - to: pr_initial_reviewer
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
            ($output | Out-String) | Should -Match 'missing-revise-counter'
        }

        It 'Passes when all contract requirements are met' {
            $yaml = @'
workflow:
  name: github-pr
  entry_point: pr_initial_reviewer
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
  - name: pr_initial_reviewer
    type: agent
    model: claude-opus-4.6
    description: Single advisory review fired once per workflow invocation
    prompt: "Review the PR"
    routes:
      - to: poll_status
  - name: poll_status
    type: script
    command: pwsh
    args:
      - "-Command"
      - "@{ route = 'none'; head_sha = 'abc' } | ConvertTo-Json"
    routes:
      - to: pr_feedback_analyzer
        when: "{{ poll_status.output.route == 'none' }}"
      - to: pr_pre_merge_policy_router
  - name: pr_feedback_analyzer
    type: agent
    model: claude-sonnet-4.6
    description: Sentiment-driven loop heart — digest feedback into a fixer brief
    prompt: "Analyze feedback"
    routes:
      - to: revise_counter
        when: "{{ pr_feedback_analyzer.output.has_negative_feedback == true }}"
      - to: poll_status
  - name: revise_counter
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
          # AB#3236: increment unconditionally per iteration; track
          # no_commit_count via poll_status.output.head_sha comparison
          # and emit cap_reason so revise_cap_gate can branch prompts.
          $count = $count + 1
          $no_commit_count = 0
          $head = '{{ poll_status.output.head_sha }}'
          $cap_reason = 'max_revisions'
          @{ iteration = $count; no_commit_count = $no_commit_count; cap_reason = $cap_reason; cap_reached = $false } | ConvertTo-Json
    routes:
      - to: revise_cap_gate
        when: "{{ revise_counter.output.cap_reached == true }}"
      - to: pr_fixer
  - name: revise_cap_gate
    type: human_gate
    prompt: |
      {% if revise_counter.output.cap_reason == 'no_commit_stuck' %}
      Stuck — fixer made no commit across N passes.
      {% else %}
      Revise cap reached.
      {% endif %}
    options:
      - label: "Force Merge"
        value: force_merge
        route: pr_merger
      - label: "Abort"
        value: abort
        route: $end
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    description: Address analyzer-summarized review feedback
    prompt: "Fix the PR issues"
    routes:
      - to: poll_status
  - name: pr_merger
    type: agent
    model: claude-sonnet-4.6
    description: Merge PR
    prompt: "Merge the PR"
    routes:
      - to: $end
  - name: revise_cap_gate_policy_router
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "../scripts/resolve-unattended-cap-mode.ps1"
    routes:
      - to: terminal_cap_auto_fail
        when: "{{ revise_cap_gate_policy_router.output.cap_mode == 'auto_fail' }}"
      - to: revise_cap_gate
  - name: terminal_cap_auto_fail
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - "echo done"
      - "-Reason"
      - "cap-auto-fail"
    routes:
      - to: $end
  # AB#3184 — pre-merge policy router + gate fixtures.
  - name: pr_pre_merge_policy_router
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-File"
      - "../scripts/resolve-pr-policy.ps1"
    routes:
      - to: pr_pre_merge_gate
        when: "{{ pr_pre_merge_policy_router.output.mode == 'manual' }}"
      - to: pr_merger
        when: "{{ pr_pre_merge_policy_router.output.mode in ['auto', 'warning'] }}"
      - to: pr_pre_merge_gate
  - name: pr_pre_merge_gate
    type: human_gate
    prompt: "Approve merge?"
    options:
      - label: "Approve"
        value: approve
        route: pr_merger
      - label: "Abort"
        value: abort
        route: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }
}

Describe 'lint-github-pr.ps1 — revise_counter no-commit fast-fail (AB#3236)' {

    BeforeAll {
        $script:LintScript    = Join-Path $PSScriptRoot 'lint-github-pr.ps1'
        $script:GithubPrYaml  = Join-Path $PSScriptRoot '..' 'workflows' 'github-pr.yaml'
        $script:Yaml          = Get-Content $script:GithubPrYaml -Raw
    }

    Context 'Production github-pr.yaml — AB#3236 wiring assertions' {

        It 'revise_counter increments unconditionally (no digest gate)' {
            $script:Yaml | Should -Match '(?s)- name: revise_counter\b.*?# AB#3236.*?increment unconditionally.*?\$count\s*=\s*\$count\s*\+\s*1'
        }

        It 'revise_counter tracks no_commit_count via poll_status.output.head_sha' {
            $script:Yaml | Should -Match '(?s)- name: revise_counter\b.*?no_commit_count'
            $script:Yaml | Should -Match '(?s)- name: revise_counter\b.*?poll_status\.output\.head_sha'
        }

        It 'revise_counter emits cap_reason for downstream prompt branching' {
            $script:Yaml | Should -Match '(?s)- name: revise_counter\b.*?cap_reason'
        }

        It 'revise_cap_gate prompt branches on cap_reason == ''no_commit_stuck''' {
            $script:Yaml | Should -Match "(?s)- name: revise_cap_gate\b.*?cap_reason\s*==\s*'no_commit_stuck'"
        }
    }

    Context 'Synthetic mutations — AB#3236 negative cases' {

        BeforeEach {
            $script:TempRoot     = Join-Path ([System.IO.Path]::GetTempPath()) "lint-github-3236-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir     = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-github-pr.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when revise_counter no_commit_count tracking is stripped' {
            $mutated = $script:Yaml -replace 'no_commit_count', 'XYZ_REMOVED_XYZ'
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $mutated
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'revise-counter-missing-no-commit-detection'
        }

        It 'Fails when revise_counter cap_reason emission is stripped' {
            $mutated = $script:Yaml -replace 'cap_reason', 'capReasonRemoved'
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $mutated
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'revise-counter-missing-cap-reason'
        }

        It 'Fails when revise_cap_gate stops branching on cap_reason' {
            $mutated = $script:Yaml -replace "cap_reason\s*==\s*'no_commit_stuck'", "cap_reason == 'always_false_sentinel'"
            Set-Content (Join-Path $script:WorkflowsDir 'github-pr.yaml') $mutated
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-github-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'revise-cap-gate-not-branched'
        }
    }
}
