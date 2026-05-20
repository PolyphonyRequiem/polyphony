BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-m10-iterate-stable.ps1'

    # Each test builds a tiny synthetic workflow directory containing one
    # `mini.yaml` with the counter under test. The lint scans the whole
    # directory, so we never touch production YAMLs.
    function script:Write-Workflow {
        param(
            [Parameter(Mandatory)] [string]$Body
        )
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $yamlBody = "agents:`n" + $Body
        Set-Content -Path (Join-Path $dir 'mini.yaml') -Value $yamlBody -Encoding utf8
        return $dir
    }

    function script:Invoke-Lint {
        param([Parameter(Mandatory)] [string]$Dir)
        $out = & pwsh -NoProfile -File $script:LintScript -WorkflowsDir $Dir -Format human 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = ($out -join "`n") }
    }
}

Describe 'lint-m10-iterate-stable' {

    Context 'M10-compliant patterns (PASS)' {

        It 'passes on a canonical 3-route counter (cap-hit, cycle-back, defensive catch-all)' {
            $body = @'
  - name: revise_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: revise_cap_gate
        when: "{{ revise_counter.output.cap_reached == true }}"
      - to: pr_fixer
        when: "{{ revise_counter.output.cap_reached == false }}"
      - to: revise_cap_gate

  - name: revise_cap_gate
    type: human_gate
    prompt: cap hit
    options:
      - label: continue
        route: pr_fixer

  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    prompt: fix it
    routes:
      - to: revise_counter
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS'
        }

        It 'passes when catch-all targets a human_gate directly (safe terminator)' {
            $body = @'
  - name: poll_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: cap_gate
        when: "{{ poll_counter.output.cap_reached == true }}"
      - to: pending_gate

  - name: cap_gate
    type: human_gate
    prompt: cap
    options:
      - label: continue
        route: poll_again
  - name: pending_gate
    type: human_gate
    prompt: pending
    options:
      - label: continue
        route: poll_again
  - name: poll_again
    type: agent
    model: claude-sonnet-4.6
    prompt: poll
    routes:
      - to: poll_counter
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }

        It 'passes when catch-all targets a terminal_* node' {
            $body = @'
  - name: my_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: cap_gate
        when: "{{ my_counter.output.cap_reached == true }}"
      - to: terminal_fallback

  - name: cap_gate
    type: human_gate
    prompt: cap
    options:
      - label: continue
        route: terminal_fallback
  - name: terminal_fallback
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo done']
    routes: []
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }

        It 'passes when counter has no catch-all (out of M10 scope)' {
            $body = @'
  - name: no_catchall_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: cap_gate
        when: "{{ no_catchall_counter.output.cap_reached == true }}"
      - to: cycle_node
        when: "{{ no_catchall_counter.output.cap_reached == false }}"

  - name: cap_gate
    type: human_gate
    prompt: cap
    options:
      - label: continue
        route: cycle_node
  - name: cycle_node
    type: agent
    model: claude-sonnet-4.6
    prompt: cycle
    routes:
      - to: no_catchall_counter
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }

        It 'passes when counter has no cap-hit guard (not iterate-stable)' {
            $body = @'
  - name: trivial_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: next_step
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }

        It 'passes on under_limit alias' {
            $body = @'
  - name: under_limit_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: cap_gate
        when: "{{ under_limit_counter.output.under_limit == false }}"
      - to: cycle_node
        when: "{{ under_limit_counter.output.under_limit == true }}"
      - to: cap_gate

  - name: cap_gate
    type: human_gate
    prompt: cap
    options:
      - label: continue
        route: cycle_node
  - name: cycle_node
    type: agent
    model: claude-sonnet-4.6
    prompt: cycle
    routes:
      - to: under_limit_counter
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }
    }

    Context 'M10 violations (FAIL)' {

        It 'fails when catch-all routes to an LLM agent that loops back to the counter' {
            $body = @'
  - name: bad_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: cap_gate
        when: "{{ bad_counter.output.cap_reached == true }}"
      - to: pr_fixer

  - name: cap_gate
    type: human_gate
    prompt: cap
    options:
      - label: continue
        route: pr_fixer
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    prompt: fix
    routes:
      - to: bad_counter
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M10-catchall-into-cycle'
            $r.Output | Should -Match 'bad_counter'
        }

        It 'fails when catch-all routes through a policy router that bypasses gate (skip-mode pattern)' {
            $body = @'
  - name: skip_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: cap_gate
        when: "{{ skip_counter.output.cap_reached == true }}"
      - to: skip_router

  - name: cap_gate
    type: human_gate
    prompt: cap
    options:
      - label: continue
        route: poll_step
  - name: skip_router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: terminal_abort
        when: "{{ skip_router.output.mode == 'abort' }}"
      - to: poll_step
        when: "{{ skip_router.output.mode == 'skip' }}"
      - to: pending_gate
  - name: pending_gate
    type: human_gate
    prompt: pending
    options:
      - label: continue
        route: poll_step
  - name: poll_step
    type: agent
    model: claude-sonnet-4.6
    prompt: poll
    routes:
      - to: skip_counter
  - name: terminal_abort
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo done']
    routes: []
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M10-catchall-into-cycle'
        }
    }

    Context 'Behaviour / edge cases' {

        It 'skips gracefully when workflows directory is missing' {
            $bogus = Join-Path $TestDrive 'does-not-exist'
            $r = Invoke-Lint -Dir $bogus
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'SKIP'
        }

        It 'passes on an empty workflows directory' {
            $dir = Join-Path $TestDrive 'empty'
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $r = Invoke-Lint -Dir $dir
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS'
        }

        It 'ignores non-counter script nodes' {
            $body = @'
  - name: just_a_script
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: next_node
        when: "{{ just_a_script.output.cap_reached == true }}"
      - to: cycle_node

  - name: next_node
    type: agent
    model: claude-sonnet-4.6
    prompt: x
    routes: []
  - name: cycle_node
    type: agent
    model: claude-sonnet-4.6
    prompt: y
    routes:
      - to: just_a_script
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }

        It 'emits github-format annotations on failure' {
            $body = @'
  - name: bad_counter
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: cap_gate
        when: "{{ bad_counter.output.cap_reached == true }}"
      - to: pr_fixer

  - name: cap_gate
    type: human_gate
    prompt: cap
    options:
      - label: continue
        route: pr_fixer
  - name: pr_fixer
    type: agent
    model: claude-sonnet-4.6
    prompt: fix
    routes:
      - to: bad_counter
'@
            $dir = Write-Workflow -Body $body
            $out = & pwsh -NoProfile -File $script:LintScript -WorkflowsDir $dir -Format github 2>&1
            $LASTEXITCODE | Should -Be 1
            ($out -join "`n") | Should -Match '::error file=.+M10-catchall-into-cycle'
        }
    }
}
