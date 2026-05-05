BeforeAll {
    $script:LintScriptPath = Join-Path $PSScriptRoot 'lint-strict-undefined.ps1'
}

Describe 'lint-strict-undefined.ps1' {

    BeforeAll {
        function New-TempWorkflowsDir {
            $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "lint-strict-undefined-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $wfd = Join-Path $tmp 'workflows'
            New-Item $wfd -ItemType Directory -Force | Out-Null
            return $wfd
        }

        function Invoke-Lint {
            param(
                [Parameter(Mandatory)] [string] $LintPath,
                [Parameter(Mandatory)] [string] $WorkflowsDir
            )
            $output = pwsh -NoProfile -File $LintPath -WorkflowsDir $WorkflowsDir 2>&1
            # Read $global:LASTEXITCODE explicitly. Reading bare $LASTEXITCODE here
            # would auto-create a local-scope shadow (= 0 if never assigned), masking
            # the real exit code set by the child pwsh process.
            return @{ Output = ($output -join "`n"); ExitCode = $global:LASTEXITCODE }
        }
    }

    Context 'Production workflow validation' {

        It 'Passes on the real workflows directory' {
            $realDir = Join-Path $PSScriptRoot '..' 'workflows'
            $realDir | Should -Exist
            $output = pwsh -NoProfile -File $script:LintScriptPath 2>&1
            $global:LASTEXITCODE | Should -Be 0
            ($output -join "`n") | Should -Match '\[OK\]'
        }
    }

    Context 'Pattern detection' {

        It 'Flags default(agent.output.field) in an output mapping' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
  output:
    merged: "{{ pr_lifecycle_github.output.merged | default(pr_lifecycle_ado.output.merged) | default(false) }}"
'@
                Set-Content (Join-Path $wfd 'example.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'pr_lifecycle_ado'
                $r.Output | Should -Match 'is defined'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Flags default(agent.output.field) in an input_mapping' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
agents:
  - name: feature_pr
    type: workflow
    input_mapping:
      feature_branch: "{{ state_detector.output.workspace_hint.feature_branch | default(ensure_feature_branch.output.branch | default('')) }}"
'@
                Set-Content (Join-Path $wfd 'example.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'ensure_feature_branch'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Passes when guarded with {% if X is defined %}' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
  output:
    merged: "{% if pr_lifecycle_github is defined %}{{ pr_lifecycle_github.output.merged }}{% elif pr_lifecycle_ado is defined %}{{ pr_lifecycle_ado.output.merged }}{% else %}false{% endif %}"
'@
                Set-Content (Join-Path $wfd 'example.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match '\[OK\]'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Passes when default takes a literal' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
  output:
    branch: "{{ ensure_feature_branch.output.branch | default('main') }}"
    merged: "{{ pr_lifecycle.output.merged | default(false) }}"
'@
                Set-Content (Join-Path $wfd 'example.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Ignores YAML comment lines containing the trap pattern' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
  # NOTE: don't write `default(other_agent.output.field)` here — that's the StrictUndefined trap (see AB#3026)
  output:
    safe: "literal"
'@
                Set-Content (Join-Path $wfd 'example.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Reports file name and line number for each violation' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
  output:
    a: "{{ x.output.y | default(z.output.w) }}"
    b: "{{ p.output.q | default(r.output.s) }}"
'@
                Set-Content (Join-Path $wfd 'multi.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'multi\.yaml:4'
                $r.Output | Should -Match 'multi\.yaml:5'
                $r.Output | Should -Match '2 violations'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Scans every yaml file in the workflows directory' {
            $wfd = New-TempWorkflowsDir
            try {
                $bad = @'
workflow:
  output:
    x: "{{ a.output.b | default(c.output.d) }}"
'@
                $good = @'
workflow:
  output:
    x: "literal"
'@
                Set-Content (Join-Path $wfd 'bad.yaml') $bad
                Set-Content (Join-Path $wfd 'good.yaml') $good
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'bad\.yaml'
                $r.Output | Should -Not -Match 'good\.yaml'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'Edge cases' {

        It 'Skips gracefully when workflows directory is missing' {
            $missing = Join-Path ([System.IO.Path]::GetTempPath()) "does-not-exist-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $missing
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'SKIP'
        }

        It 'Passes on an empty workflows directory' {
            $wfd = New-TempWorkflowsDir
            try {
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Tolerates extra whitespace inside default(...)' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  output:
    x: "{{ a.output.b | default(    c.output.d   ) }}"
'@
                Set-Content (Join-Path $wfd 'ws.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
