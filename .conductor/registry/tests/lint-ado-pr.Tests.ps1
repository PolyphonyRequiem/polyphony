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

        It 'Fails when error code is missing' {
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
  merged: "{{ ado_pr_manual_gate.output.choice == 'merged' }}"
  pr_url: ""

agents:
  - name: ado_pr_error
    type: script
    command: pwsh
    args:
      - "-Command"
      - "Write-Output 'some error'"
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
            ($output | Out-String) | Should -Match 'missing-error-code'
        }

        It 'Fails when abort option is missing from human gate' {
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
            $LASTEXITCODE | Should -Be 0
        }
    }
}
