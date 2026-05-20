BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-m04-routing-rules.ps1'

    # Each test builds a tiny synthetic workflow directory containing one
    # `mini.yaml` with the node(s) under test. The lint scans the whole
    # directory, so we never touch production YAMLs.
    function script:Write-Workflow {
        param(
            [Parameter(Mandatory)] [string]$Body
        )
        $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Set-Content -Path (Join-Path $dir 'mini.yaml') -Value $Body -Encoding utf8
        return $dir
    }

    function script:Invoke-Lint {
        param([Parameter(Mandatory)] [string]$Dir)
        $out = & pwsh -NoProfile -File $script:LintScript -WorkflowsDir $Dir -Format human 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = ($out -join "`n") }
    }
}

Describe 'lint-m04-routing-rules' {

    Context 'PASS cases' {

        It 'passes when every routes-bearing agent has a bare catch-all last' {
            $body = @'
agents:
  - name: router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: success_node
        when: "{{ router.output.phase == 'ok' }}"
      - to: error_gate
  - name: success_node
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
  - name: error_gate
    type: human_gate
    prompt: error
    options:
      - label: abort
        route: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS'
        }

        It 'passes when route target is a top-level for_each group name' {
            $body = @'
agents:
  - name: dispatcher
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: my_loop
        when: "{{ dispatcher.output.go == true }}"
      - to: my_loop
for_each:
  - name: my_loop
    type: for_each
    source: dispatcher.output.items
    as: item
    agent:
      name: worker
      type: workflow
      workflow: ./sub.yaml
      input_mapping:
        x: "{{ item }}"
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS'
        }

        It 'passes when route target is $end' {
            $body = @'
agents:
  - name: terminal
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }

        It 'passes on agents with no routes table (implicit $end)' {
            $body = @'
agents:
  - name: leaf
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }

        It 'passes on a human_gate with no routes table and valid option routes' {
            $body = @'
agents:
  - name: gate
    type: human_gate
    prompt: pick
    options:
      - label: yes
        route: next_node
      - label: no
        route: $end
  - name: next_node
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 0
        }
    }

    Context 'M4-001 invalid-route-target' {

        It 'fails when a route points at an unknown agent' {
            $body = @'
agents:
  - name: router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: ghost_node
        when: "{{ router.output.phase == 'x' }}"
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-001-invalid-route-target'
            $r.Output | Should -Match 'ghost_node'
        }
    }

    Context 'M4-002 catch-all-not-last' {

        It 'fails when a bare route appears before later routes (dead duplicate)' {
            $body = @'
agents:
  - name: router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: dest
        when: "{{ router.output.phase == 'ok' }}"
      - to: dest
      - to: dest
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-002-catch-all-not-last'
            $r.Output | Should -Match 'dead code'
        }

        It 'fails when a bare route precedes a different destination' {
            $body = @'
agents:
  - name: router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: success
        when: "{{ router.output.phase == 'ok' }}"
      - to: success
      - to: failure
  - name: success
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
  - name: failure
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-002-catch-all-not-last'
            $r.Output | Should -Match "Move the bare route to last position"
        }
    }

    Context 'M4-003 missing-catch-all' {

        It 'fails when routes cover all enum values but lack a bare catch-all' {
            $body = @'
agents:
  - name: router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: a
        when: "{{ router.output.phase == 'a' }}"
      - to: b
        when: "{{ router.output.phase == 'b' }}"
  - name: a
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
  - name: b
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-003-missing-catch-all'
        }

        It 'fails when routes cover only true/false but lack a bare catch-all' {
            $body = @'
agents:
  - name: guard
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: a
        when: "{{ guard.output.allowed == true }}"
      - to: b
        when: "{{ guard.output.allowed == false }}"
  - name: a
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
  - name: b
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-003-missing-catch-all'
        }
    }

    Context 'M4-004 routes-on-gate' {

        It 'fails when a human_gate has a routes table' {
            $body = @'
agents:
  - name: gate
    type: human_gate
    prompt: pick
    options:
      - label: yes
        route: $end
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-004-routes-on-gate'
        }
    }

    Context 'M4-005 when-true-not-last' {

        It 'fails when literal when: "true" appears before later routes' {
            $body = @'
agents:
  - name: router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: first
        when: "true"
      - to: second
        when: "{{ router.output.phase == 'x' }}"
      - to: first
  - name: first
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
  - name: second
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-005-when-true-not-last'
        }
    }

    Context 'M4-006 route-missing-to' {

        It 'fails when a route entry omits to:' {
            $body = @'
agents:
  - name: router
    type: script
    command: pwsh
    args: ['-NoProfile', '-Command', 'echo "{}"']
    routes:
      - when: "{{ router.output.phase == 'x' }}"
      - to: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-006-route-missing-to'
        }
    }

    Context 'M4-007 invalid-gate-option-route' {

        It 'fails when a human_gate option routes to an unknown node' {
            $body = @'
agents:
  - name: gate
    type: human_gate
    prompt: pick
    options:
      - label: yes
        route: ghost_node
      - label: no
        route: $end
'@
            $r = Invoke-Lint -Dir (Write-Workflow -Body $body)
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'M4-007-invalid-gate-option-route'
            $r.Output | Should -Match 'ghost_node'
        }
    }
}
