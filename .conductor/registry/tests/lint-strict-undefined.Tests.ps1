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

    Context 'ATTR_DEFAULT pattern detection (AB#3160)' {

        It 'Flags <id>.output.X.Y | default(literal) — the AB#3156 Bug 2 pattern' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
agents:
  - name: research_dispatch
    type: workflow
    input_mapping:
      escalation_cap: "{{ architect.output.research_needs.escalation_cap | default(1) }}"
'@
                Set-Content (Join-Path $wfd 'plan.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'ATTR_DEFAULT'
                $r.Output | Should -Match 'architect\.output\.research_needs\.escalation_cap'
                $r.Output | Should -Match "\.get\('B', x\)"
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Flags <id>.output.X.Y | default(string)' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  output:
    budget: "{{ planner.output.scope.budget | default('low') }}"
'@
                Set-Content (Join-Path $wfd 'sc.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'planner\.output\.scope\.budget'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "Passes when the optional access uses .get('key', default) — the canonical fix" {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  name: example
agents:
  - name: research_dispatch
    type: workflow
    input_mapping:
      escalation_cap: "{{ architect.output.research_needs.get('escalation_cap', 1) }}"
'@
                Set-Content (Join-Path $wfd 'plan.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match '\[OK\]'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "Passes when default() filters a top-level workflow.input field" {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  output:
    foo: "{{ workflow.input.foo | default('bar') }}"
'@
                Set-Content (Join-Path $wfd 'wi.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "Passes when .get(...) result feeds a downstream filter chain (no default trap)" {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  output:
    nonempty: "{{ architect.output.research_needs.get('topics', []) | length > 0 }}"
'@
                Set-Content (Join-Path $wfd 'topics.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Passes single-level <id>.output.field | default(literal) — top-level required field' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  output:
    merged: "{{ pr_lifecycle.output.merged | default(false) }}"
    branch: "{{ ensure_feature_branch.output.branch | default('main') }}"
'@
                Set-Content (Join-Path $wfd 'tl.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Honors the {# strict-undefined-ok: <reason> #} whitelist marker on the same line' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  output:
    cap: "{{ architect.output.research_needs.escalation_cap | default(1) }}"  {# strict-undefined-ok: schema guarantees research_needs.escalation_cap is set #}
'@
                Set-Content (Join-Path $wfd 'wl.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 0
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Reports both AGENT_VAR_DEFAULT and ATTR_DEFAULT violations on the same file' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  output:
    a: "{{ planner.output.scope.budget | default('low') }}"
    b: "{{ x.output.y | default(z.output.w) }}"
'@
                Set-Content (Join-Path $wfd 'mix.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'ATTR_DEFAULT'
                $r.Output | Should -Match 'AGENT_VAR_DEFAULT'
                $r.Output | Should -Match '2 violations'
            } finally {
                Remove-Item (Split-Path $wfd -Parent) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'Flags ATTR_DEFAULT inside script: command blocks (YAML literal)' {
            $wfd = New-TempWorkflowsDir
            try {
                $yaml = @'
workflow:
  steps:
    - name: gate
      command: pwsh
      args:
        - "-Command"
        - |
          $hasTopics = '{{ (architect.output.research_needs.topics | default([]) | length > 0) | string | lower }}' -eq 'true'
'@
                Set-Content (Join-Path $wfd 'script.yaml') $yaml
                $r = Invoke-Lint -LintPath $script:LintScriptPath -WorkflowsDir $wfd
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'research_needs\.topics'
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
