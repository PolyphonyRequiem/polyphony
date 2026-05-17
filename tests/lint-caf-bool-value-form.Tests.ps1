BeforeAll {
    $script:LintScriptPath  = Join-Path $PSScriptRoot 'lint-caf-bool-value-form.ps1'
    $script:FixturesDir     = Join-Path $PSScriptRoot 'lint' 'fixtures'
    $script:RegistryFixture = Join-Path $script:FixturesDir 'verb-output-schemas.json'

    function Invoke-Lint {
        param(
            [string] $WorkflowsDir,
            [string] $RegistryPath = $script:RegistryFixture
        )
        $args = @('-NoProfile', '-File', $script:LintScriptPath,
                  '-WorkflowsDir', $WorkflowsDir,
                  '-RegistryPath', $RegistryPath,
                  '-Format', 'human')
        $output = pwsh @args 2>&1
        return @{ Output = ($output -join "`n"); ExitCode = $global:LASTEXITCODE }
    }

    function New-TempWorkflowDir {
        param([hashtable] $Files)
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) "lint-cafbool-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $dir | Out-Null
        foreach ($name in $Files.Keys) {
            Set-Content -LiteralPath (Join-Path $dir $name) -Value $Files[$name] -Encoding UTF8
        }
        return $dir
    }
}

Describe 'lint-caf-bool-value-form.ps1' {

    Context 'CAFBOOL001 — bool flag with value form' {
        It 'flags a workflow that pairs a bool flag with "false"' {
            # The test relies on a verb in the fixture registry that
            # still has clr_type: bool for some flag. The fixture
            # carries `pr merge-impl-pr --admin` as bool (default false)
            # — workflow passing `--admin true` would be the bug.
            $dir = New-TempWorkflowDir @{
                'wf.yaml' = @'
workflow:
  name: test
agents:
  - name: bad_step
    type: script
    command: polyphony
    args:
      - "pr"
      - "merge-impl-pr"
      - "--root-id"
      - "100"
      - "--item-id"
      - "200"
      - "--mg-path"
      - "core"
      - "--admin"
      - "true"
'@
            }
            try {
                $r = Invoke-Lint -WorkflowsDir $dir
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'CAFBOOL001'
                $r.Output | Should -Match '--admin'
                $r.Output | Should -Match 'true'
                $r.Output | Should -Match 'StringBoolArg'
            } finally { Remove-Item -Recurse -Force $dir }
        }

        It 'does NOT flag a bare bool flag (no value follows)' {
            $dir = New-TempWorkflowDir @{
                'wf.yaml' = @'
workflow:
  name: test
agents:
  - name: good_step
    type: script
    command: polyphony
    args:
      - "pr"
      - "merge-impl-pr"
      - "--root-id"
      - "100"
      - "--item-id"
      - "200"
      - "--mg-path"
      - "core"
      - "--admin"
'@
            }
            try {
                $r = Invoke-Lint -WorkflowsDir $dir
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Not -Match 'CAFBOOL001'
            } finally { Remove-Item -Recurse -Force $dir }
        }

        It 'does NOT flag a string-shimmed flag (clr_type: string with true/false default)' {
            # Per the fixture surgery in this PR, pr merge-impl-pr's
            # --delete-branch is now declared as string. Passing
            # "--delete-branch false" is the supported shape — must be silent.
            $dir = New-TempWorkflowDir @{
                'wf.yaml' = @'
workflow:
  name: test
agents:
  - name: shimmed_step
    type: script
    command: polyphony
    args:
      - "pr"
      - "merge-impl-pr"
      - "--root-id"
      - "100"
      - "--item-id"
      - "200"
      - "--mg-path"
      - "core"
      - "--delete-branch"
      - "false"
'@
            }
            try {
                $r = Invoke-Lint -WorkflowsDir $dir
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Not -Match 'CAFBOOL001'
            } finally { Remove-Item -Recurse -Force $dir }
        }

        It 'is silent when the flag is not in the verb schema (VERB002 handles it)' {
            $dir = New-TempWorkflowDir @{
                'wf.yaml' = @'
workflow:
  name: test
agents:
  - name: unknown_flag_step
    type: script
    command: polyphony
    args:
      - "pr"
      - "merge-impl-pr"
      - "--definitely-not-a-flag"
      - "false"
'@
            }
            try {
                $r = Invoke-Lint -WorkflowsDir $dir
                # CAFBOOL001 defers to VERB002; lint is clean here.
                $r.ExitCode | Should -Be 0
            } finally { Remove-Item -Recurse -Force $dir }
        }

        It 'is silent when the command is not polyphony (twig/pwsh/gh)' {
            $dir = New-TempWorkflowDir @{
                'wf.yaml' = @'
workflow:
  name: test
agents:
  - name: twig_step
    type: script
    command: twig
    args:
      - "state"
      - "--admin"
      - "true"
'@
            }
            try {
                $r = Invoke-Lint -WorkflowsDir $dir
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Not -Match 'CAFBOOL001'
            } finally { Remove-Item -Recurse -Force $dir }
        }

        It 'flags case-insensitive TRUE / FALSE / False' {
            $dir = New-TempWorkflowDir @{
                'wf.yaml' = @'
workflow:
  name: test
agents:
  - name: caps_step
    type: script
    command: polyphony
    args:
      - "pr"
      - "merge-impl-pr"
      - "--root-id"
      - "100"
      - "--item-id"
      - "200"
      - "--mg-path"
      - "core"
      - "--admin"
      - "TRUE"
'@
            }
            try {
                $r = Invoke-Lint -WorkflowsDir $dir
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'CAFBOOL001'
            } finally { Remove-Item -Recurse -Force $dir }
        }
    }

    Context 'Real workflow corpus' {
        It 'runs over every .conductor/registry/workflows yaml with zero findings' {
            $realDir = Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'workflows'
            $r = Invoke-Lint -WorkflowsDir $realDir -RegistryPath (Join-Path $PSScriptRoot '..' 'artifacts' 'verb-output-schemas.json')
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match 'CAFBOOL001'
        }
    }

    Context 'Configuration errors' {
        It 'exits 2 when the registry is missing' {
            $dir = New-TempWorkflowDir @{ 'wf.yaml' = "workflow:`n  name: empty`nagents: []`n" }
            try {
                $r = Invoke-Lint -WorkflowsDir $dir -RegistryPath '/no/such/path.json'
                $r.ExitCode | Should -Be 2
                $r.Output | Should -Match 'not found'
            } finally { Remove-Item -Recurse -Force $dir }
        }
    }
}
