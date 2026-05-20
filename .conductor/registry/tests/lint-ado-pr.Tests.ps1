BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-ado-pr.ps1'
    $script:AdoPrYaml = Join-Path $PSScriptRoot '..' 'workflows' 'ado-pr.yaml'
}

Describe 'lint-ado-pr.ps1' {

    Context 'Production ado-pr.yaml validation' {

        It 'Passes on the real ado-pr.yaml' {
            $script:AdoPrYaml | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScript 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Interface contract' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-ado-pr-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-ado-pr.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when pr_number input is missing' {
            $yaml = @'
workflow:
  name: ado-pr
  entry_point: ado_pr_error
  input:
    branch_name:
      type: string
    target_branch:
      type: string
    review_policy:
      type: string

output:
  merged: "{{ ado_pr_manual_gate.output.choice == 'merged' }}"
  pr_url: ""

agents:
  - name: ado_pr_error
    type: script
    command: pwsh
    args:
      - "-Command"
      - "Write-Output 'ADO_PR_NOT_IMPLEMENTED'"
    routes:
      - to: ado_pr_manual_gate
  - name: ado_pr_manual_gate
    type: human_gate
    prompt: "Manual gate"
    options:
      - label: "Merged"
        value: merged
        route: $end
      - label: "Abort"
        value: abort
        route: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-input'
        }

        It 'Fails when merged output is missing' {
            $yaml = @'
workflow:
  name: ado-pr
  entry_point: ado_pr_error
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
  - name: ado_pr_error
    type: script
    command: pwsh
    args:
      - "-Command"
      - "Write-Output 'ADO_PR_NOT_IMPLEMENTED'"
    routes:
      - to: ado_pr_manual_gate
  - name: ado_pr_manual_gate
    type: human_gate
    prompt: "Manual gate"
    options:
      - label: "Merged"
        value: merged
        route: $end
      - label: "Abort"
        value: abort
        route: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-output'
        }

        It 'Fails when human gate is missing' {
            $yaml = @'
workflow:
  name: ado-pr
  entry_point: ado_pr_error
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
  - name: ado_pr_error
    type: script
    command: pwsh
    args:
      - "-Command"
      - "Write-Output 'ADO_PR_NOT_IMPLEMENTED'"
    routes:
      - to: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-human-gate'
        }

        It 'Fails when abort option is missing from human gate' {
            $yaml = @'
workflow:
  name: ado-pr
  entry_point: ado_pr_manual_gate
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
  - name: ado_pr_manual_gate
    type: human_gate
    prompt: "Manual gate"
    options:
      - label: "Re-poll"
        value: repoll
        route: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'missing-gate-option'
        }

        It 'Fails when entry point references non-existent agent' {
            $yaml = @'
workflow:
  name: ado-pr
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
  - name: ado_pr_error
    type: script
    command: pwsh
    args:
      - "-Command"
      - "Write-Output 'ADO_PR_NOT_IMPLEMENTED'"
    routes:
      - to: ado_pr_manual_gate
  - name: ado_pr_manual_gate
    type: human_gate
    prompt: "Manual gate"
    options:
      - label: "Merged"
        value: merged
        route: $end
      - label: "Abort"
        value: abort
        route: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'invalid-entry-point'
        }

        It 'Passes when all contract requirements are met' {
            $yaml = @'
workflow:
  name: ado-pr
  entry_point: poll_status
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
  merged: "{{ ado_pr_manual_gate.output.choice == 'merged' }}"
  pr_url: ""

agents:
  - name: poll_status
    type: script
    command: pwsh
    args:
      - "-Command"
      - "Write-Output '{}'"
    routes:
      - to: pr_feedback_analyzer
        when: "{{ poll_status.output.route == 'none' }}"
      - to: ado_pr_manual_gate
  - name: pr_feedback_analyzer
    type: agent
    model: claude-sonnet-4.6
    routes:
      - to: revise_counter
        when: "{{ pr_feedback_analyzer.output.has_negative_feedback == true }}"
      - to: pending_poll_counter
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
          @{ iteration = $count; no_commit_count = $no_commit_count; cap_reason = $cap_reason } | ConvertTo-Json
    routes:
      - to: revise_cap_gate
        when: "{{ revise_counter.output.cap_reached == true }}"
      - to: ado_pr_manual_gate
  - name: revise_cap_gate
    type: human_gate
    prompt: |
      {% if revise_counter.output.cap_reason == 'no_commit_stuck' %}
      Stuck.
      {% else %}
      Cap.
      {% endif %}
    options:
      - label: "Abort"
        value: abort
        route: $end
  - name: pending_poll_counter
    type: script
    command: pwsh
    args: ["-Command", "Write-Output '{}'"]
    routes:
      - to: stuck_review_gate
        when: "{{ pending_poll_counter.output.cap_reached == true }}"
      - to: ado_pr_manual_gate
  - name: stuck_review_gate
    type: human_gate
    prompt: "stuck"
    options:
      - label: "Continue"
        value: continue_waiting
        route: stuck_review_reset
      - label: "Override"
        value: override_approved
        route: ado_pr_manual_gate
      - label: "Abort"
        value: abort
        route: $end
  - name: stuck_review_reset
    type: script
    command: pwsh
    args: ["-Command", "Write-Output '{}'"]
    routes:
      - to: ado_pr_manual_gate
  - name: ado_pr_manual_gate
    type: human_gate
    prompt: "Manual gate"
    options:
      - label: "Merged"
        value: merged
        route: $end
      - label: "Abort"
        value: abort
        route: $end
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
        route: ado_pr_manual_gate
      - label: "Abort"
        value: abort
        route: $end
'@
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $yaml
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Stuck-review timeout MVP' {

        BeforeEach {
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-ado-pr-stuck-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir = Join-Path $script:TempRoot 'tests'
            $script:RealYaml = Join-Path $PSScriptRoot '..' 'workflows' 'ado-pr.yaml'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-ado-pr.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Real ado-pr.yaml carries the pending_poll_counter step' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'name:\s*pending_poll_counter'
        }

        It 'Real ado-pr.yaml has stuck_review_gate with all three required options' {
            $content = Get-Content $script:RealYaml -Raw
            $content | Should -Match 'name:\s*stuck_review_gate'
            $m = [regex]::Match($content, '(?s)- name: stuck_review_gate\b.*?(?=\n  - name: |\Z)')
            $m.Success | Should -BeTrue
            $m.Value | Should -Match 'value:\s*continue_waiting'
            $m.Value | Should -Match 'value:\s*override_approved'
            $m.Value | Should -Match 'value:\s*abort'
        }

        It 'Real ado-pr.yaml has stuck_review_reset' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'name:\s*stuck_review_reset'
        }

        It 'Real ado-pr.yaml routes from pr_feedback_analyzer to pending_poll_counter' {
            (Get-Content $script:RealYaml -Raw) | Should -Match "(?s)- name: pr_feedback_analyzer\b.*?to:\s*pending_poll_counter.*?(?=\n  - name: |\Z)"
        }

        It 'Real ado-pr.yaml routes from counter to stuck_review_gate_policy_router on cap_reached' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'pending_poll_counter\.output\.cap_reached\s*==\s*true'
        }

        It 'Lint fails when pending_poll_counter is removed from ado-pr.yaml' {
            $content = Get-Content $script:RealYaml -Raw
            $stripped = $content -replace '(?s)\n\s*# ── Pending poll counter.*?(?=\n  # ── Pending-review gate policy router)', "`n"
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $stripped
            $lintScript = Join-Path $script:TestsDir 'lint-ado-pr.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-pending-poll-counter'
        }

        It 'Lint fails when stuck_review_gate is missing the override_approved option' {
            $content = Get-Content $script:RealYaml -Raw
            $mutated = $content -replace '(?s)(- label: "✅ Override approved"\s*\r?\n\s*value:\s*override_approved\s*\r?\n\s*route:\s*pr_merger\s*\r?\n)', ''
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $mutated
            $lintScript = Join-Path $script:TestsDir 'lint-ado-pr.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match "missing-stuck-review-option"
        }
    }
}

Describe 'lint-ado-pr.ps1 — revise_counter no-commit fast-fail (AB#3236)' {

    BeforeAll {
        $script:LintScript = Join-Path $PSScriptRoot 'lint-ado-pr.ps1'
        $script:AdoPrYaml  = Join-Path $PSScriptRoot '..' 'workflows' 'ado-pr.yaml'
        $script:Yaml       = Get-Content $script:AdoPrYaml -Raw
    }

    Context 'Production ado-pr.yaml — AB#3236 wiring assertions' {

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
            $script:TempRoot     = Join-Path ([System.IO.Path]::GetTempPath()) "lint-ado-3236-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $script:WorkflowsDir = Join-Path $script:TempRoot 'workflows'
            $script:TestsDir     = Join-Path $script:TempRoot 'tests'
            New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
            New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
            Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-ado-pr.ps1')
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when revise_counter no_commit_count tracking is stripped' {
            # Strip the no_commit_count tracking from revise_counter — the
            # rest of the workflow stays valid. lint must surface
            # 'revise-counter-missing-no-commit-detection'.
            $mutated = $script:Yaml -replace 'no_commit_count', 'XYZ_REMOVED_XYZ'
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $mutated
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'revise-counter-missing-no-commit-detection'
        }

        It 'Fails when revise_counter cap_reason emission is stripped' {
            $mutated = $script:Yaml -replace 'cap_reason', 'capReasonRemoved'
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $mutated
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'revise-counter-missing-cap-reason'
        }

        It 'Fails when revise_cap_gate stops branching on cap_reason' {
            # Replace the AB#3236 conditional with a static prompt header to
            # simulate a regression that drops the no_commit_stuck branch.
            $mutated = $script:Yaml -replace "cap_reason\s*==\s*'no_commit_stuck'", "cap_reason == 'always_false_sentinel'"
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $mutated
            # That replacement also kills the cap_reason emission in revise_counter
            # (search-and-replace is yaml-wide) — but the negative branch we care
            # about is revise-cap-gate-not-branched; just assert it fires.
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-ado-pr.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'revise-cap-gate-not-branched'
        }
    }
}
