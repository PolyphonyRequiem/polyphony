BeforeAll {
    $script:LintScriptPath  = Join-Path $PSScriptRoot 'lint-jinja-resolver.ps1'
    $script:FixturesDir     = Join-Path $PSScriptRoot 'lint' 'fixtures'
    $script:SyntheticDir    = Join-Path $script:FixturesDir 'workflows'
    $script:RegistryFixture = Join-Path $script:FixturesDir 'verb-output-schemas.json'
    $script:RealWorkflowsDir= Join-Path $PSScriptRoot '..' '.conductor' 'registry' 'workflows'
    $script:RealAllowlist   = Join-Path $PSScriptRoot 'lint-jinja-resolver.allowlist.yaml'

    function Invoke-Lint {
        param(
            [string] $WorkflowsDir,
            [string] $OnlyFile,
            [switch] $Pedantic,
            [switch] $FailOnWarnings,
            [string] $RegistryPath = $script:RegistryFixture,
            [string] $AllowlistPath = ''
        )
        $args = @('-NoProfile', '-File', $script:LintScriptPath, '-WorkflowsDir', $WorkflowsDir, '-RegistryPath', $RegistryPath, '-Format', 'human')
        if ($OnlyFile)        { $args += @('-OnlyFile', $OnlyFile) }
        if ($Pedantic)        { $args += '-Pedantic' }
        if ($FailOnWarnings)  { $args += '-FailOnWarnings' }
        if ($AllowlistPath)   { $args += @('-AllowlistPath', $AllowlistPath) }
        $output = pwsh @args 2>&1
        return @{ Output = ($output -join "`n"); ExitCode = $global:LASTEXITCODE }
    }

    function New-TempAllowlist {
        param([string] $Body)
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "lint-jinja-allowlist-$([guid]::NewGuid().ToString('N').Substring(0,8)).yaml"
        Set-Content -LiteralPath $path -Value $Body -Encoding UTF8
        return $path
    }
}

Describe 'lint-jinja-resolver.ps1 — diagnostic taxonomy' {

    Context 'JINJA001 — missing field' {
        It 'flags a reference to a field that does not exist on the verb result' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA001-missing-field.yaml'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'JINJA001'
            $r.Output | Should -Match "field 'nonexistent_field' does not exist"
            $r.Output | Should -Match 'Polyphony.PlanDeriveAncestorChainResult'
        }
    }

    Context 'JINJA002 — omit-when-null reference' {
        It 'warns on an unguarded reference to an omit-when-null field' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA002-omit-when-null-unguarded.yaml'
            $r.ExitCode | Should -Be 0   # warnings only
            $r.Output | Should -Match 'JINJA002'
            $r.Output | Should -Match "'derive_chain.output.error' has can_omit_when_null=true"
        }

        It 'fails when -FailOnWarnings is set and a JINJA002 warning fires' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA002-omit-when-null-unguarded.yaml' -FailOnWarnings
            $r.ExitCode | Should -Be 1
        }

        It 'is silent when the same field is guarded — exhaustively checks all four guard forms' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA002-omit-when-null-guarded.yaml' -FailOnWarnings
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match 'JINJA002'
        }
    }

    Context 'JINJA003 — unknown step' {
        It 'flags a reference to a step id that is not declared in the workflow' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA003-unknown-step.yaml'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'JINJA003'
            $r.Output | Should -Match "no step named 'ghost_step'"
        }

        It 'does not flag workflow.input.* (workflow-builtin)' {
            $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "jinja-builtin-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            New-Item -ItemType Directory -Path $tmp -Force | Out-Null
            try {
                @'
workflow:
  name: builtin
  entry_point: a
agents:
  - name: a
    type: agent
    model: claude-haiku-4.5
    prompt: |
      Item: {{ workflow.input.work_item_id }}.
'@ | Set-Content -LiteralPath (Join-Path $tmp 'builtin.yaml')
                $r = Invoke-Lint -WorkflowsDir $tmp
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Not -Match 'JINJA003'
            } finally {
                Remove-Item -LiteralPath $tmp -Recurse -Force
            }
        }
    }

    Context 'JINJA004 — non-polyphony step' {
        It 'is suppressed by default (summary count only)' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA004-non-polyphony.yaml'
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match '\[WARNING\].*JINJA004'
            $r.Output | Should -Match 'JINJA004 suppressed:\s+1'
        }

        It 'surfaces per-reference warnings under -Pedantic' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA004-non-polyphony.yaml' -Pedantic
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'JINJA004'
            $r.Output | Should -Match "step 'pwsh_step'"
        }
    }

    Context 'JINJA005 — invalid path traversal' {
        It 'flags descending through a scalar leaf' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA005-scalar-descent.yaml'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'JINJA005'
            $r.Output | Should -Match 'scalar leaf'
        }

        It 'flags bare attribute access on a list (e.g. foo.items.name)' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA005-list-bare-attribute.yaml'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'JINJA005'
            $r.Output | Should -Match "cannot access '\.name' on a list"
        }

        It 'allows list[N] indexing, | first, and | length' {
            $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "jinja-list-ok-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            New-Item -ItemType Directory -Path $tmp -Force | Out-Null
            try {
                @'
workflow:
  name: list-ok
  entry_point: derive_chain
agents:
  - name: derive_chain
    type: script
    command: polyphony
    args: ["plan", "derive-ancestor-chain", "{{ workflow.input.work_item_id }}", "{{ workflow.input.work_item_id }}"]
    routes:
      - to: c
  - name: c
    type: agent
    model: claude-haiku-4.5
    prompt: |
      {% if derive_chain.output is defined %}
      length: {{ derive_chain.output.ancestor_chain | length }}
      first:  {{ derive_chain.output.ancestor_chain | first }}
      idx0:   {{ derive_chain.output.ancestor_chain[0] }}
      {% endif %}
'@ | Set-Content -LiteralPath (Join-Path $tmp 'ok.yaml')
                $r = Invoke-Lint -WorkflowsDir $tmp
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Not -Match 'JINJA005'
            } finally {
                Remove-Item -LiteralPath $tmp -Recurse -Force
            }
        }
    }

    Context 'Clean fixture' {
        It 'emits zero diagnostics under -Pedantic -FailOnWarnings' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'clean.yaml' -Pedantic -FailOnWarnings
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match '\[ERROR\]|\[WARNING\]'
        }
    }
}

Describe 'lint-jinja-resolver.ps1 — guard detection' {

    BeforeAll {
        function New-GuardFixture {
            param([string] $Body)
            $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "jinja-guard-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            New-Item -ItemType Directory -Path $tmp -Force | Out-Null
            $yaml = @"
workflow:
  name: guard-test
  entry_point: derive_chain
agents:
  - name: derive_chain
    type: script
    command: polyphony
    args: ["plan", "derive-ancestor-chain", "{{ workflow.input.work_item_id }}", "{{ workflow.input.work_item_id }}"]
    routes:
      - to: c
  - name: c
    type: agent
    model: claude-haiku-4.5
    prompt: |
$Body
"@
            Set-Content -LiteralPath (Join-Path $tmp 'guard.yaml') -Value $yaml
            return $tmp
        }
    }

    It 'recognizes intra-expression "and is defined" guard (rubber-duck #1)' {
        $tmp = New-GuardFixture -Body '      Result: {{ derive_chain.output.error is defined and derive_chain.output.error == "x" }}.'
        try {
            $r = Invoke-Lint -WorkflowsDir $tmp -FailOnWarnings
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match 'JINJA002'
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }

    It 'recognizes intra-expression "is not defined or" guard (rubber-duck #1)' {
        $tmp = New-GuardFixture -Body '      Result: {{ derive_chain.output.error is not defined or derive_chain.output.error == "" }}.'
        try {
            $r = Invoke-Lint -WorkflowsDir $tmp -FailOnWarnings
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match 'JINJA002'
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }

    It 'recognizes the X.output is defined zero-prefix block guard (rubber-duck #2)' {
        $tmp = New-GuardFixture -Body @'
      {% if derive_chain.output is defined %}
      e: {{ derive_chain.output.error }}
      r: {{ derive_chain.output.root_id }}
      {% endif %}
'@
        try {
            $r = Invoke-Lint -WorkflowsDir $tmp -FailOnWarnings
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match 'JINJA002'
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }

    It 'still flags the field reference when only a sibling field is guarded' {
        $tmp = New-GuardFixture -Body @'
      {% if derive_chain.output.root_id is defined %}
      bare: {{ derive_chain.output.error }}
      {% endif %}
'@
        try {
            $r = Invoke-Lint -WorkflowsDir $tmp
            $r.Output | Should -Match 'JINJA002'
            $r.Output | Should -Match 'derive_chain.output.error'
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }
}

Describe 'lint-jinja-resolver.ps1 — verb resolution' {

    It 'parses polyphony <group> <command> from args[0..1]' {
        # Implicit — exercised by every fixture above. Verify the failure mode:
        # if args has only one positional token, we still attempt resolution.
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "jinja-toplevel-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        try {
            @'
workflow:
  name: toplevel
  entry_point: h
agents:
  - name: h
    type: script
    command: polyphony
    args: ["health"]
    routes:
      - to: c
  - name: c
    type: agent
    model: claude-haiku-4.5
    prompt: |
      {{ h.output.bogus_field }}
'@ | Set-Content -LiteralPath (Join-Path $tmp 'tl.yaml')
            $r = Invoke-Lint -WorkflowsDir $tmp
            # Either JINJA001 (if 'health' verb is in registry) or JINJA004
            # (verb not registered). Either way, the lint did not throw.
            $r.ExitCode | Should -BeIn @(0, 1)
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }

    It 'classifies pwsh / twig / gh script steps as non-polyphony (JINJA004)' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "jinja-pwsh-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        try {
            @'
workflow:
  name: nonpoly
  entry_point: t
agents:
  - name: t
    type: script
    command: twig
    args: ["sync"]
    routes:
      - to: c
  - name: c
    type: agent
    model: claude-haiku-4.5
    prompt: |
      {{ t.output.foo }}
'@ | Set-Content -LiteralPath (Join-Path $tmp 'np.yaml')
            $r = Invoke-Lint -WorkflowsDir $tmp -Pedantic
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'JINJA004'
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }

    It 'recognizes conductor-injected script-step output fields (exit_code, stdout, stderr)' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "jinja-cond-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        try {
            @'
workflow:
  name: cond
  entry_point: derive_chain
agents:
  - name: derive_chain
    type: script
    command: polyphony
    args: ["plan", "derive-ancestor-chain", "{{ workflow.input.work_item_id }}", "{{ workflow.input.work_item_id }}"]
    routes:
      - to: c
        when: "{{ derive_chain.output.exit_code == 0 }}"
      - to: $end
  - name: c
    type: agent
    model: claude-haiku-4.5
    prompt: |
      ok
'@ | Set-Content -LiteralPath (Join-Path $tmp 'ec.yaml')
            $r = Invoke-Lint -WorkflowsDir $tmp -FailOnWarnings
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match '\[ERROR\]|\[WARNING\]'
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }
}

Describe 'lint-jinja-resolver.ps1 — output formats' {

    It 'emits ::error workflow commands in -Format github' {
        $output = pwsh -NoProfile -File $script:LintScriptPath `
            -WorkflowsDir $script:SyntheticDir `
            -OnlyFile 'JINJA001-missing-field.yaml' `
            -RegistryPath $script:RegistryFixture `
            -Format github 2>&1
        $combined = ($output -join "`n")
        $combined | Should -Match '::error file=.*JINJA001-missing-field\.yaml.*JINJA001'
    }

    It 'emits ::warning workflow commands for warnings' {
        $output = pwsh -NoProfile -File $script:LintScriptPath `
            -WorkflowsDir $script:SyntheticDir `
            -OnlyFile 'JINJA002-omit-when-null-unguarded.yaml' `
            -RegistryPath $script:RegistryFixture `
            -Format github 2>&1
        $combined = ($output -join "`n")
        $combined | Should -Match '::warning file=.*JINJA002'
    }
}

Describe 'lint-jinja-resolver.ps1 — registry / configuration' {

    It 'exits 2 with a clear remediation message when the registry is missing' {
        $missing = Join-Path ([System.IO.Path]::GetTempPath()) "no-such-registry-$([guid]::NewGuid().ToString('N').Substring(0,8)).json"
        $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -RegistryPath $missing
        $r.ExitCode | Should -Be 2
        $r.Output | Should -Match 'dotnet build'
        $r.Output | Should -Match 'verb-output-schemas'
    }

    It 'exits 2 when the allowlist exceeds the size cap' {
        $entries = (1..16) | ForEach-Object {
            "  - file: f.yaml`n    code: JINJA001`n    reference: x.output.y$_`n    issue: '#999'"
        }
        $body = "suppress:`n" + ($entries -join "`n")
        $alPath = New-TempAllowlist -Body $body
        try {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -AllowlistPath $alPath
            $r.ExitCode | Should -Be 2
            $r.Output | Should -Match 'cap of 15'
        } finally { Remove-Item -LiteralPath $alPath -Force }
    }

    It 'exits 2 when an allowlist entry is missing required fields' {
        $body = @'
suppress:
  - file: f.yaml
    code: JINJA001
'@
        $alPath = New-TempAllowlist -Body $body
        try {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -AllowlistPath $alPath
            $r.ExitCode | Should -Be 2
            $r.Output | Should -Match "missing required field"
        } finally { Remove-Item -LiteralPath $alPath -Force }
    }

    It 'suppresses a diagnostic that exactly matches an allowlist entry' {
        # The lint records the file path relative to cwd of the child pwsh
        # process. We compute that path here so the allowlist entry matches
        # what the lint will emit.
        $relPath = (Resolve-Path -LiteralPath (Join-Path $script:SyntheticDir 'JINJA001-missing-field.yaml') -Relative).Replace('\', '/')
        if ($relPath.StartsWith('./')) { $relPath = $relPath.Substring(2) }
        $body = @"
suppress:
  - file: $relPath
    code: JINJA001
    reference: derive_chain.output.nonexistent_field
    issue: '#999'
"@
        $alPath = New-TempAllowlist -Body $body
        try {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'JINJA001-missing-field.yaml' -AllowlistPath $alPath
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'allowlist suppressed:\s+1'
        } finally { Remove-Item -LiteralPath $alPath -Force }
    }
}

Describe 'lint-jinja-resolver.ps1 — verb invocation diagnostics (CR+CRL)' {

    Context 'VERB001 — unknown verb' {
        It 'flags a polyphony invocation whose verb path is not in the registry' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'VERB001-unknown-verb.yaml'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'VERB001'
            $r.Output | Should -Match "unknown polyphony verb 'plan this-verb-does-not-exist'"
        }
    }

    Context 'VERB002 — unknown CLI flag' {
        It 'flags a flag that does not appear in the verb inputs[]' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'VERB002-unknown-flag.yaml'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'VERB002'
            $r.Output | Should -Match "unknown flag '--bogus-flag'"
            $r.Output | Should -Match "branch close-scope"
        }
    }

    Context 'VERB003 — missing required input' {
        It 'flags a required input that is threaded by neither --flag nor positional' {
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'VERB003-missing-required.yaml'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'VERB003'
            $r.Output | Should -Match "does not thread required input '--work-item'"
        }

        It 'does NOT flag when the required input is supplied positionally (CAF binding semantics)' {
            # ConsoleAppFramework binds positional args to declared params in
            # order. The lint must mirror that: a single bare positional after
            # the verb path satisfies the first required slot.
            $r = Invoke-Lint -WorkflowsDir $script:SyntheticDir -OnlyFile 'VERB003-positional-ok.yaml'
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Not -Match 'VERB003'
        }
    }
}

Describe 'lint-jinja-resolver.ps1 — real workflow corpus' {

    It 'runs over every .conductor/registry/workflows yaml without errors (allowlist-aware)' {
        # The lint must exit 0 on the real corpus when run with the project's
        # checked-in allowlist. Genuine bugs surfaced during development are
        # captured in the allowlist with tracking issue links.
        $r = Invoke-Lint `
            -WorkflowsDir $script:RealWorkflowsDir `
            -RegistryPath $script:RegistryFixture `
            -AllowlistPath $script:RealAllowlist
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'PASS: no errors'
    }
}
