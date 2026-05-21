<#
.SYNOPSIS
    Pester tests for tests/lint-primary-completer-trust-chain.ps1.

.DESCRIPTION
    Hermetic fixture-based tests. Constructs synthetic
    implement-merge-group.yaml fragments in a temp directory and
    asserts the lint reports pass / fail exactly as documented.

    Also runs the lint against the real workflow to assert the
    trust chain currently holds.
#>
[CmdletBinding()]
param()

BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-primary-completer-trust-chain.ps1'
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot

    function script:New-WorkflowFixture {
        param([string] $Body)
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("lint-pc-trust-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $path = Join-Path $dir 'workflow.yaml'
        Set-Content -LiteralPath $path -Value $Body -Encoding UTF8 -NoNewline
        return $path
    }

    function script:Invoke-Lint {
        param([string] $WorkflowPath)
        $output = pwsh -NoProfile -File $script:LintScript -WorkflowPath $WorkflowPath 2>&1
        return @{
            Output   = ($output | Out-String)
            ExitCode = $global:LASTEXITCODE
        }
    }
}

Describe 'lint-primary-completer-trust-chain.ps1' {

    Context 'PASS scenarios' {

        It 'PASSes when primary_completer ← delete_impl_branch ← {assert_impl_pr_coverage, squash_coverage_mismatch_gate}' {
            $body = @'
agents:
  - name: assert_impl_pr_coverage
    type: script
    routes:
      - to: delete_impl_branch
        when: "ok"
      - to: squash_coverage_mismatch_gate
        when: "mismatch"
  - name: squash_coverage_mismatch_gate
    type: human_gate
    options:
      - value: abort
        route: $end
      - value: force_accept
        route: delete_impl_branch
  - name: delete_impl_branch
    type: script
    routes:
      - to: primary_completer
  - name: primary_completer
    type: script
    routes:
      - to: primary_router
'@
            $path = New-WorkflowFixture -Body $body
            $r = Invoke-Lint -WorkflowPath $path
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS'
        }

        It 'PASSes against the real implement-merge-group.yaml in the repo' {
            $real = Join-Path $script:RepoRoot '.conductor/registry/workflows/implement-merge-group.yaml'
            $r = Invoke-Lint -WorkflowPath $real
            if ($r.ExitCode -ne 0) {
                Write-Host $r.Output
            }
            $r.ExitCode | Should -Be 0
        }
    }

    Context 'FAIL scenarios' {

        It 'FAILs when a step bypasses delete_impl_branch and routes directly into primary_completer' {
            $body = @'
agents:
  - name: assert_impl_pr_coverage
    type: script
    routes:
      - to: delete_impl_branch
        when: "ok"
  - name: squash_coverage_mismatch_gate
    type: human_gate
    options:
      - value: force_accept
        route: delete_impl_branch
  - name: delete_impl_branch
    type: script
    routes:
      - to: primary_completer
  - name: rogue_skipper
    type: script
    routes:
      - to: primary_completer
  - name: primary_completer
    type: script
    routes:
      - to: primary_router
'@
            $path = New-WorkflowFixture -Body $body
            $r = Invoke-Lint -WorkflowPath $path
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'I1'
            $r.Output | Should -Match 'rogue_skipper'
        }

        It 'FAILs when delete_impl_branch is reachable from a step outside the trust chain' {
            $body = @'
agents:
  - name: assert_impl_pr_coverage
    type: script
    routes:
      - to: delete_impl_branch
        when: "ok"
  - name: squash_coverage_mismatch_gate
    type: human_gate
    options:
      - value: force_accept
        route: delete_impl_branch
  - name: rogue_router
    type: script
    routes:
      - to: delete_impl_branch
  - name: delete_impl_branch
    type: script
    routes:
      - to: primary_completer
  - name: primary_completer
    type: script
    routes:
      - to: primary_router
'@
            $path = New-WorkflowFixture -Body $body
            $r = Invoke-Lint -WorkflowPath $path
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'I2'
            $r.Output | Should -Match 'rogue_router'
        }

        It 'FAILs when assert_impl_pr_coverage is missing from the trust chain' {
            $body = @'
agents:
  - name: squash_coverage_mismatch_gate
    type: human_gate
    options:
      - value: force_accept
        route: delete_impl_branch
  - name: delete_impl_branch
    type: script
    routes:
      - to: primary_completer
  - name: primary_completer
    type: script
    routes:
      - to: primary_router
'@
            $path = New-WorkflowFixture -Body $body
            $r = Invoke-Lint -WorkflowPath $path
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'I2'
            $r.Output | Should -Match 'assert_impl_pr_coverage'
        }
    }
}
