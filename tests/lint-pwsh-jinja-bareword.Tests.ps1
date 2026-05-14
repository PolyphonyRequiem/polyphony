BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-pwsh-jinja-bareword.ps1'

    function New-TempWorkflowsDir {
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) `
            "lint-pwsh-jinja-bareword-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        return $dir
    }

    function Invoke-Lint {
        param([string] $WorkflowsDir, [string] $Format = 'human')
        $output = pwsh -NoProfile -File $script:LintScript `
            -WorkflowsDir $WorkflowsDir -Format $Format 2>&1
        return @{ Output = ($output -join "`n"); ExitCode = $global:LASTEXITCODE }
    }
}

Describe 'lint-pwsh-jinja-bareword.ps1' {

    BeforeEach {
        $script:WorkflowsDir = New-TempWorkflowsDir
        $global:LASTEXITCODE = 0
    }

    AfterEach {
        Remove-Item $script:WorkflowsDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Clean inputs (exit 0)' {

        It 'Passes on an empty workflows directory' {
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }

        It 'Passes when workflows directory does not exist' {
            $missing = Join-Path $script:WorkflowsDir 'does-not-exist'
            $r = Invoke-Lint -WorkflowsDir $missing
            $r.ExitCode | Should -Be 0
        }

        It 'Passes a single-quoted Jinja render compared to a string (the canonical safe form)' {
            $body = @'
agents:
  - name: ok-quoted-bool
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - |
        $hasTopics = '{{ (architect.output.topics | default([]) | length > 0) | string | lower }}' -eq 'true'
        Write-Host $hasTopics
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'ok.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }

        It 'Passes a double-quoted Jinja render' {
            $body = @'
agents:
  - name: ok-double-quote
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - |
        $name = "{{ architect.output.name }}"
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'ok-double.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }

        It 'Passes Jinja renders embedded inside an array literal of quoted strings' {
            $body = @'
agents:
  - name: ok-array
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - |
        $arr = @('{{ architect.output.a }}', '{{ architect.output.b }}')
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'ok-array.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }

        It 'Passes the quote-and-cast pattern: $count = [int]''{{ ... }}''' {
            $body = @'
agents:
  - name: ok-int-cast
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - |
        $count = [int]'{{ context.history | length }}'
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'ok-int.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }

        It 'Ignores agents whose command is not pwsh' {
            $body = @'
agents:
  - name: a-twig-step
    type: script
    command: twig
    args:
      - "set"
      - "{{ workflow.input.id }}"
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'twig.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }
    }

    Context 'Violations (exit 1)' {

        It 'Flags $hasTopics = {{ ... }} (the AB#3156 Bug 1 shape)' {
            $body = @'
agents:
  - name: bad-bool
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - |
        $hasTopics = {{ (architect.output.topics | default([]) | length > 0) | string | lower }}
        Write-Host $hasTopics
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'bad-bool.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match '\$hasTopics'
            $r.Output | Should -Match 'bad-bool\.yaml'
        }

        It 'Flags $count = {{ items | length }} (integer bareword)' {
            $body = @'
agents:
  - name: bad-int
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - |
        $count = {{ items | length }}
        Write-Host $count
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'bad-int.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match '\$count'
        }

        It 'Flags a bareword statement inside a folded (>-) block scalar with semicolons' {
            $body = @'
agents:
  - name: bad-folded
    type: script
    command: pwsh
    args:
      - "-NoProfile"
      - "-Command"
      - >-
        $prNumber = {{ poll.output.pr_number }};
        Write-Host $prNumber
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'bad-folded.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match '\$prNumber'
        }

        It 'Aggregates violations across multiple agents in the same file' {
            $body = @'
agents:
  - name: bad-a
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        $a = {{ x.output.a }}
  - name: bad-b
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        $b = {{ x.output.b }}
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'multi.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match '\$a'
            $r.Output | Should -Match '\$b'
        }

        It 'Reports the source line number of the violation' {
            $body = @'
agents:
  - name: bad-line
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        $hasTopics = {{ x.output.topics | length > 0 }}
'@
            $path = Join-Path $script:WorkflowsDir 'bad-line.yaml'
            Set-Content -Path $path -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 1
            # The body line `$hasTopics = …` lives on line 8 of the file
            # (1-based). Match against the file:line snippet pattern the
            # human formatter emits.
            $r.Output | Should -Match 'bad-line\.yaml:8'
        }
    }

    Context 'Whitelist marker' {

        It 'Suppresses a violation when # bareword-ok appears on the preceding line' {
            $body = @'
agents:
  - name: ok-whitelisted
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        # bareword-ok: integer used in arithmetic; render is always digits
        $count = {{ items | length }}
        Write-Host $count
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'wl.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }

        It 'Suppresses a violation when # bareword-ok appears on the same line' {
            $body = @'
agents:
  - name: ok-same-line
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        $count = {{ items | length }} # bareword-ok: integer literal
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'wl-same.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir
            $r.ExitCode | Should -Be 0
        }
    }

    Context 'GitHub Actions output format' {

        It 'Emits ::error annotations under -Format github' {
            $body = @'
agents:
  - name: gha-bad
    type: script
    command: pwsh
    args:
      - "-Command"
      - |
        $hasTopics = {{ x.output.topics | length > 0 }}
'@
            Set-Content -Path (Join-Path $script:WorkflowsDir 'gha.yaml') -Value $body
            $r = Invoke-Lint -WorkflowsDir $script:WorkflowsDir -Format 'github'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match '::error file=\.conductor/registry/workflows/gha\.yaml,line=\d+::'
            $r.Output | Should -Match '\$hasTopics'
        }
    }

    Context 'Live tree' {

        It 'The real .conductor/registry/workflows/ tree passes the lint' {
            # Defense: this test is the gate that catches a real workflow
            # introducing the bareword pattern. If it ever fails, fix the
            # workflow — do not loosen the lint.
            $r = pwsh -NoProfile -File $script:LintScript 2>&1
            $global:LASTEXITCODE | Should -Be 0
        }
    }
}
