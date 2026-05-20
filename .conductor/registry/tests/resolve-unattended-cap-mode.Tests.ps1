# Pester tests for .conductor/registry/scripts/resolve-unattended-cap-mode.ps1
# (AB#3186 — wire policy.unattended.cap_mode through cap-hit gates).
#
# Strategy mirrors manifest-bootstrap.Tests.ps1: a temp-dir stub
# polyphony.ps1 is passed via -PolyphonyExe; the stub branches on
# $env:POLYPHONY_STUB_MODE to emit scripted `policy load` envelopes.

BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..' 'scripts' 'resolve-unattended-cap-mode.ps1'

    function script:New-PolyphonyStub {
        param([string]$Name = "resolve-cap-mode-stub-${PID}-$([guid]::NewGuid().ToString('N').Substring(0,8))")

        $stubDir = Join-Path ([System.IO.Path]::GetTempPath()) $Name
        New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
        $stub = Join-Path $stubDir 'polyphony.ps1'

        $stubBody = @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Argv)

$mode = $env:POLYPHONY_STUB_MODE
$verb = if ($Argv.Length -ge 2) { "$($Argv[0]) $($Argv[1])" } else { '' }

function EmitJson($obj) {
    $obj | ConvertTo-Json -Compress -Depth 8
}

if ($verb -ne 'policy load') {
    [Console]::Error.WriteLine("stub: unexpected verb '$verb'")
    exit 99
}

switch ($mode) {
    'manual' {
        EmitJson @{ unattended = @{ cap_mode = 'manual' } }
        exit 0
    }
    'auto_proceed' {
        EmitJson @{ unattended = @{ cap_mode = 'auto_proceed' } }
        exit 0
    }
    'auto_fail' {
        EmitJson @{ unattended = @{ cap_mode = 'auto_fail' } }
        exit 0
    }
    'missing_cap_mode' {
        # unattended present, cap_mode field absent — should fall back to default.
        EmitJson @{ unattended = @{ acceptance_mode = 'manual' } }
        exit 0
    }
    'null_cap_mode' {
        EmitJson @{ unattended = @{ cap_mode = $null } }
        exit 0
    }
    'missing_unattended' {
        # unattended absent entirely — should fall back to default.
        EmitJson @{ approvals = @{ max_revision_cycles = 5 } }
        exit 0
    }
    'unknown_value' {
        EmitJson @{ unattended = @{ cap_mode = 'auto_yolo' } }
        exit 0
    }
    'malformed_json' {
        Write-Output 'this is { not :: json'
        exit 0
    }
    'empty_stdout' {
        # Print nothing but exit 0 — should be treated as error.
        exit 0
    }
    'cli_failure' {
        [Console]::Error.WriteLine('polyphony exploded')
        exit 1
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
        param([string]$StubPath, [string]$Mode)
        try {
            $env:POLYPHONY_STUB_MODE = $Mode
            $raw = & pwsh -NoProfile -File $script:Script -PolyphonyExe $StubPath 2>$null
            if (-not $raw) { throw "resolver emitted empty output (stub mode=$Mode)" }
            return ($raw | ConvertFrom-Json)
        }
        finally {
            Remove-Item Env:POLYPHONY_STUB_MODE -ErrorAction SilentlyContinue
        }
    }
}

Describe 'resolve-unattended-cap-mode.ps1' {

    Context 'happy path — each allowed cap_mode value' {

        It "returns cap_mode='manual' source='policy' when policy says manual" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'manual'
            $result.cap_mode     | Should -Be 'manual'
            $result.source       | Should -Be 'policy'
            $result.policy_error | Should -Be ''
        }

        It "returns cap_mode='auto_proceed' source='policy'" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'auto_proceed'
            $result.cap_mode     | Should -Be 'auto_proceed'
            $result.source       | Should -Be 'policy'
            $result.policy_error | Should -Be ''
        }

        It "returns cap_mode='auto_fail' source='policy'" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'auto_fail'
            $result.cap_mode     | Should -Be 'auto_fail'
            $result.source       | Should -Be 'policy'
            $result.policy_error | Should -Be ''
        }
    }

    Context 'default fallback — policy present but cap_mode absent' {

        It "falls back to cap_mode='manual' source='default' when cap_mode field is missing" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'missing_cap_mode'
            $result.cap_mode     | Should -Be 'manual'
            $result.source       | Should -Be 'default'
            $result.policy_error | Should -Be ''
        }

        It "falls back to cap_mode='manual' source='default' when cap_mode is null" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'null_cap_mode'
            $result.cap_mode     | Should -Be 'manual'
            $result.source       | Should -Be 'default'
            $result.policy_error | Should -Be ''
        }

        It "falls back to cap_mode='manual' source='default' when unattended block is absent" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'missing_unattended'
            $result.cap_mode     | Should -Be 'manual'
            $result.source       | Should -Be 'default'
            $result.policy_error | Should -Be ''
        }
    }

    Context 'error path — surfaces policy_error for manual-gate diagnostics' {

        It "returns source='error' with explanatory policy_error when cap_mode is not in the allowed enum" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'unknown_value'
            $result.cap_mode     | Should -Be 'manual'
            $result.source       | Should -Be 'error'
            $result.policy_error | Should -Match "'auto_yolo'"
            $result.policy_error | Should -Match 'manual'
            $result.policy_error | Should -Match 'auto_proceed'
            $result.policy_error | Should -Match 'auto_fail'
        }

        It "returns source='error' on malformed JSON output" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'malformed_json'
            $result.cap_mode     | Should -Be 'manual'
            $result.source       | Should -Be 'error'
            $result.policy_error | Should -Match 'JSON parse'
        }

        It "returns source='error' when policy load exits non-zero" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'cli_failure'
            $result.cap_mode     | Should -Be 'manual'
            $result.source       | Should -Be 'error'
            $result.policy_error | Should -Match 'exited 1'
        }

        It "returns source='error' on empty stdout" {
            $stub = script:New-PolyphonyStub
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'empty_stdout'
            $result.cap_mode     | Should -Be 'manual'
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
            $result = script:Invoke-Resolver -StubPath $stub -Mode 'manual'
            $names = ($result.PSObject.Properties.Name | Sort-Object)
            $names | Should -Be @('cap_mode', 'policy_error', 'source')
        }
    }
}
