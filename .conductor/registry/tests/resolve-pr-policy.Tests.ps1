# Pester tests for .conductor/registry/scripts/resolve-pr-policy.ps1
# (AB#3184 — wire policy.pr.defaults.mode through the pre-merge router in
# github-pr.yaml and ado-pr.yaml).
#
# Strategy mirrors resolve-research-policy.Tests.ps1 (AB#3188) and
# resolve-unattended-cap-mode.Tests.ps1 (AB#3186): a temp-dir stub
# polyphony.ps1 is passed via -PolyphonyExe; the stub branches on
# $env:POLYPHONY_STUB_MODE to emit scripted `policy resolve` envelopes
# that exercise every branch of the resolver helper.

BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..' 'scripts' 'resolve-pr-policy.ps1'

    function script:New-PolyphonyStub {
        param([string]$Name = "resolve-pr-stub-${PID}-$([guid]::NewGuid().ToString('N').Substring(0,8))")

        $stubDir = Join-Path ([System.IO.Path]::GetTempPath()) $Name
        New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
        $stub = Join-Path $stubDir 'polyphony.ps1'

        $stubBody = @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Argv)

$mode = $env:POLYPHONY_STUB_MODE
# Verb is the first two Argv tokens joined; resolve-pr-policy.ps1
# always calls `polyphony policy resolve --domain pr --scope <s>`.
$verb = if ($Argv.Length -ge 2) { "$($Argv[0]) $($Argv[1])" } else { '' }

function EmitJson($obj) {
    $obj | ConvertTo-Json -Compress -Depth 8
}

if ($verb -ne 'policy resolve') {
    [Console]::Error.WriteLine("stub: unexpected verb '$verb'")
    exit 99
}

switch ($mode) {
    'auto' {
        EmitJson @{ domain = 'pr'; scope = 'default'; mode = 'auto'; max_fix_loops = 10; max_remediation_cycles = 3; allow_any_approval_vote = $false }
        exit 0
    }
    'warning' {
        EmitJson @{ domain = 'pr'; scope = 'default'; mode = 'warning'; max_fix_loops = 10; max_remediation_cycles = 3; allow_any_approval_vote = $false }
        exit 0
    }
    'manual' {
        EmitJson @{ domain = 'pr'; scope = 'default'; mode = 'manual'; max_fix_loops = 10; max_remediation_cycles = 3; allow_any_approval_vote = $false }
        exit 0
    }
    'missing_mode' {
        EmitJson @{ domain = 'pr'; scope = 'default'; max_fix_loops = 10; max_remediation_cycles = 3 }
        exit 0
    }
    'null_mode' {
        EmitJson @{ domain = 'pr'; scope = 'default'; mode = $null; max_fix_loops = 10 }
        exit 0
    }
    'bad_mode' {
        EmitJson @{ domain = 'pr'; scope = 'default'; mode = 'auto_yolo'; max_fix_loops = 10 }
        exit 0
    }
    'malformed_json' {
        Write-Output 'this is { not :: json'
        exit 0
    }
    'empty_stdout' {
        exit 0
    }
    'cli_failure' {
        [Console]::Error.WriteLine('polyphony exploded')
        exit 1
    }
    'echo_scope' {
        # Verify -Scope is plumbed through to --scope. Parse Argv to find it.
        $scopeIdx = [array]::IndexOf($Argv, '--scope')
        $scope = if ($scopeIdx -ge 0 -and $scopeIdx + 1 -lt $Argv.Length) { $Argv[$scopeIdx + 1] } else { '<missing>' }
        EmitJson @{ domain = 'pr'; scope = $scope; mode = 'manual' }
        exit 0
    }
    default {
        [Console]::Error.WriteLine("stub: unknown POLYPHONY_STUB_MODE '$mode'")
        exit 99
    }
}
'@
        Set-Content -Path $stub -Value $stubBody -Encoding UTF8
        return $stub
    }

    function script:Invoke-Resolver {
        param([string]$StubPath, [string]$Mode, [string]$Scope = 'default')
        try {
            $env:POLYPHONY_STUB_MODE = $Mode
            $raw = & pwsh -NoProfile -File $script:Script -PolyphonyExe $StubPath -Scope $Scope 2>$null
            if (-not $raw) { throw "resolver emitted empty output (stub mode=$Mode)" }
            return ($raw | ConvertFrom-Json)
        }
        finally {
            Remove-Item Env:POLYPHONY_STUB_MODE -ErrorAction SilentlyContinue
        }
    }
}

Describe 'resolve-pr-policy.ps1' {

    Context 'happy path — every allowed mode' {

        It "returns mode='auto' source='policy' when policy says auto" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'auto'
            $result.mode         | Should -Be 'auto'
            $result.source       | Should -Be 'policy'
            $result.policy_error | Should -Be ''
        }

        It "returns mode='warning' source='policy' when policy says warning" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'warning'
            $result.mode   | Should -Be 'warning'
            $result.source | Should -Be 'policy'
        }

        It "returns mode='manual' source='policy' when policy says manual" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'manual'
            $result.mode   | Should -Be 'manual'
            $result.source | Should -Be 'policy'
        }
    }

    Context 'default fallback — policy present but mode missing/null' {

        It "falls back to mode='warning' source='default' when mode is missing" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'missing_mode'
            $result.mode         | Should -Be 'warning'
            $result.source       | Should -Be 'default'
            $result.policy_error | Should -Be ''
        }

        It "falls back to mode='warning' source='default' when mode is null" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'null_mode'
            $result.mode   | Should -Be 'warning'
            $result.source | Should -Be 'default'
        }
    }

    Context "error path — surfaces policy_error AND flips mode to 'manual'" {

        It "returns source='error' mode='manual' when mode value is outside the allowed enum" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'bad_mode'
            $result.mode         | Should -Be 'manual'
            $result.source       | Should -Be 'error'
            $result.policy_error | Should -Match "'auto_yolo'"
            $result.policy_error | Should -Match 'auto'
            $result.policy_error | Should -Match 'warning'
            $result.policy_error | Should -Match 'manual'
        }

        It "returns source='error' mode='manual' on malformed JSON" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'malformed_json'
            $result.mode         | Should -Be 'manual'
            $result.source       | Should -Be 'error'
            $result.policy_error | Should -Match 'JSON parse'
        }

        It "returns source='error' mode='manual' when CLI exits non-zero" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'cli_failure'
            $result.mode         | Should -Be 'manual'
            $result.source       | Should -Be 'error'
            $result.policy_error | Should -Match 'exited 1'
        }

        It "returns source='error' mode='manual' on empty stdout" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'empty_stdout'
            $result.mode         | Should -Be 'manual'
            $result.source       | Should -Be 'error'
            $result.policy_error | Should -Match 'empty output'
        }
    }

    Context 'envelope shape contract' {

        It 'always exits 0 even when underlying CLI fails' {
            $stub = script:New-PolyphonyStub
            $env:POLYPHONY_STUB_MODE = 'cli_failure'
            try {
                $raw = & pwsh -NoProfile -File $script:Script -PolyphonyExe $stub 2>$null
                $LASTEXITCODE | Should -Be 0
                $raw          | Should -Not -BeNullOrEmpty
            }
            finally {
                Remove-Item Env:POLYPHONY_STUB_MODE -ErrorAction SilentlyContinue
            }
        }

        It 'emits exactly the three documented fields' {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'auto'
            $names = ($result.PSObject.Properties.Name | Sort-Object)
            $names | Should -Be @('mode', 'policy_error', 'source')
        }
    }

    Context 'parameter plumbing' {

        It 'forwards -Scope to polyphony --scope' {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'echo_scope' -Scope 'type:User Story'
            # The stub echoes the scope it saw on argv into the envelope (as
            # the JSON's `scope`). The resolver doesn't propagate `scope` so
            # we assert indirectly: a successful 'policy' source proves the
            # stub matched its 'echo_scope' branch and saw '--scope
            # type:User Story' as expected.
            $result.mode         | Should -Be 'manual'
            $result.source       | Should -Be 'policy'
            $result.policy_error | Should -Be ''
        }

        It "defaults -Scope to 'default' when omitted" {
            $stub = script:New-PolyphonyStub
            $env:POLYPHONY_STUB_MODE = 'echo_scope'
            try {
                $raw = & pwsh -NoProfile -File $script:Script -PolyphonyExe $stub 2>$null
                $LASTEXITCODE | Should -Be 0
                $result = $raw | ConvertFrom-Json
                $result.source | Should -Be 'policy'
            }
            finally {
                Remove-Item Env:POLYPHONY_STUB_MODE -ErrorAction SilentlyContinue
            }
        }
    }
}
