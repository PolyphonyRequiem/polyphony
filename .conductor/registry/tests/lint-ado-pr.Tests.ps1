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
  entry_point: ado_pr_status_check
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
  - name: ado_pr_status_check
    type: script
    command: pwsh
    args:
      - "-Command"
      - "Write-Output '{}'"
    routes:
      - to: ado_pending_poll_counter
        when: "{{ ado_pr_status_check.output.state == 'pending' }}"
      - to: ado_pr_manual_gate
  - name: ado_pending_poll_counter
    type: script
    command: pwsh
    args: ["-Command", "Write-Output '{}'"]
    routes:
      - to: ado_stuck_review_gate
        when: "{{ ado_pending_poll_counter.output.cap_reached == true }}"
      - to: ado_pr_manual_gate
  - name: ado_stuck_review_gate
    type: human_gate
    prompt: "stuck"
    options:
      - label: "Continue"
        value: continue_waiting
        route: ado_stuck_review_reset
      - label: "Override"
        value: override_approved
        route: ado_pr_manual_gate
      - label: "Abort"
        value: abort
        route: $end
  - name: ado_stuck_review_reset
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

        It 'Real ado-pr.yaml carries the ado_pending_poll_counter step' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'name:\s*ado_pending_poll_counter'
        }

        It 'Real ado-pr.yaml has ado_stuck_review_gate with all three required options' {
            $content = Get-Content $script:RealYaml -Raw
            $content | Should -Match 'name:\s*ado_stuck_review_gate'
            $m = [regex]::Match($content, '(?s)- name: ado_stuck_review_gate\b.*?(?=\n  - name: |\Z)')
            $m.Success | Should -BeTrue
            $m.Value | Should -Match 'value:\s*continue_waiting'
            $m.Value | Should -Match 'value:\s*override_approved'
            $m.Value | Should -Match 'value:\s*abort'
        }

        It 'Real ado-pr.yaml has ado_stuck_review_reset' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'name:\s*ado_stuck_review_reset'
        }

        It 'Real ado-pr.yaml routes pending through ado_pending_poll_counter' {
            (Get-Content $script:RealYaml -Raw) | Should -Match "to:\s*ado_pending_poll_counter[\s\S]{0,200}?ado_pr_status_check\.output\.state\s*==\s*'pending'"
        }

        It 'Real ado-pr.yaml routes from counter to ado_stuck_review_gate on cap_reached' {
            (Get-Content $script:RealYaml -Raw) | Should -Match 'ado_pending_poll_counter\.output\.cap_reached\s*==\s*true'
        }

        It 'Lint fails when ado_pending_poll_counter is removed from ado-pr.yaml' {
            $content = Get-Content $script:RealYaml -Raw
            $stripped = $content -replace '(?s)\n\s*# ── Pending poll counter.*?(?=\n  # ── Pending gate)', "`n"
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $stripped
            $lintScript = Join-Path $script:TestsDir 'lint-ado-pr.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'missing-pending-poll-counter'
        }

        It 'Lint fails when ado_stuck_review_gate is missing the override_approved option' {
            $content = Get-Content $script:RealYaml -Raw
            $mutated = $content -replace '(?s)(- label: "✅ Override approved"\s*\r?\n\s*value:\s*override_approved\s*\r?\n\s*route:\s*ado_pr_merger\s*\r?\n)', ''
            Set-Content (Join-Path $script:WorkflowsDir 'ado-pr.yaml') $mutated
            $lintScript = Join-Path $script:TestsDir 'lint-ado-pr.ps1'
            $output = pwsh -NoProfile -File $lintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match "missing-stuck-review-option"
        }
    }
}
