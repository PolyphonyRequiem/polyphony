# Pester tests for .conductor/registry/scripts/resolve-research-max-loops.ps1
# (research-loop-policy — wire policy.research.defaults.max_research_loops
# through the research_loop_counter in plan-level.yaml).
#
# Strategy mirrors resolve-research-policy.Tests.ps1: a temp-dir stub
# polyphony.ps1 is passed via -PolyphonyExe; the stub branches on
# $env:POLYPHONY_STUB_MODE to emit scripted `policy resolve` envelopes
# that exercise every branch of the resolver helper.

BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..' 'scripts' 'resolve-research-max-loops.ps1'

    function script:New-PolyphonyStub {
        param([string]$Name = "resolve-research-max-loops-stub-${PID}-$([guid]::NewGuid().ToString('N').Substring(0,8))")

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

if ($verb -ne 'policy resolve') {
    [Console]::Error.WriteLine("stub: unexpected verb '$verb'")
    exit 99
}

switch ($mode) {
    'loops_3' {
        EmitJson @{ domain = 'research'; scope = 'default'; max_research_loops = 3 }
        exit 0
    }
    'loops_5' {
        EmitJson @{ domain = 'research'; scope = 'default'; max_research_loops = 5 }
        exit 0
    }
    'loops_zero' {
        # Zero is legal — disables research entirely (cap_reached on first request).
        EmitJson @{ domain = 'research'; scope = 'default'; max_research_loops = 0 }
        exit 0
    }
    'missing_field' {
        EmitJson @{ domain = 'research'; scope = 'default'; mode = 'auto'; escalation_cap = 1 }
        exit 0
    }
    'null_field' {
        EmitJson @{ domain = 'research'; scope = 'default'; max_research_loops = $null }
        exit 0
    }
    'negative' {
        EmitJson @{ domain = 'research'; scope = 'default'; max_research_loops = -1 }
        exit 0
    }
    'string_value' {
        EmitJson @{ domain = 'research'; scope = 'default'; max_research_loops = 'three' }
        exit 0
    }
    'malformed_json' {
        Write-Output 'not json at all {'
        exit 0
    }
    'empty_output' {
        Write-Output ''
        exit 0
    }
    'nonzero_exit' {
        Write-Output '{"error":"boom"}'
        exit 2
    }
    default {
        [Console]::Error.WriteLine("stub: unknown mode '$mode'")
        exit 98
    }
}
'@
        Set-Content -Path $stub -Value $stubBody -Encoding UTF8
        return $stub
    }

    function script:Invoke-Helper {
        param([string]$Mode, [string]$StubPath)
        $env:POLYPHONY_STUB_MODE = $Mode
        try {
            $raw = pwsh -NoProfile -File $script:Script -PolyphonyExe $StubPath -Scope 'default'
            return $raw | ConvertFrom-Json
        }
        finally {
            Remove-Item Env:\POLYPHONY_STUB_MODE -ErrorAction SilentlyContinue
        }
    }
}

Describe 'resolve-research-max-loops.ps1' {
    BeforeAll {
        $script:Stub = New-PolyphonyStub
    }

    AfterAll {
        if (Test-Path $script:Stub) {
            Remove-Item -Path (Split-Path $script:Stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context 'happy path' {
        It 'returns max_research_loops=3 with source=policy when policy emits 3' {
            $result = Invoke-Helper -Mode 'loops_3' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'policy'
            $result.policy_error | Should -Be ''
        }

        It 'returns max_research_loops=5 with source=policy when policy emits 5' {
            $result = Invoke-Helper -Mode 'loops_5' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 5
            $result.source | Should -Be 'policy'
        }

        It 'accepts max_research_loops=0 (disables research entirely)' {
            $result = Invoke-Helper -Mode 'loops_zero' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 0
            $result.source | Should -Be 'policy'
            $result.policy_error | Should -Be ''
        }
    }

    Context 'missing / null field fallback' {
        It 'falls back to 3 with source=default when field is absent' {
            $result = Invoke-Helper -Mode 'missing_field' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'default'
            $result.policy_error | Should -Be ''
        }

        It 'falls back to 3 with source=default when field is null' {
            $result = Invoke-Helper -Mode 'null_field' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'default'
        }
    }

    Context 'invalid values trigger error envelope' {
        It 'rejects negative values with source=error' {
            $result = Invoke-Helper -Mode 'negative' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'error'
            $result.policy_error | Should -Match 'negative'
        }

        It 'rejects non-integer values with source=error' {
            $result = Invoke-Helper -Mode 'string_value' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'error'
            $result.policy_error | Should -Match 'not a non-negative integer'
        }
    }

    Context 'CLI failure modes' {
        It 'falls back to 3 with source=error on malformed JSON' {
            $result = Invoke-Helper -Mode 'malformed_json' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'error'
            $result.policy_error | Should -Match 'parse failed'
        }

        It 'falls back to 3 with source=error on empty output' {
            $result = Invoke-Helper -Mode 'empty_output' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'error'
            $result.policy_error | Should -Match 'empty output'
        }

        It 'falls back to 3 with source=error on non-zero CLI exit' {
            $result = Invoke-Helper -Mode 'nonzero_exit' -StubPath $script:Stub
            $result.max_research_loops | Should -Be 3
            $result.source | Should -Be 'error'
            $result.policy_error | Should -Match 'exited'
        }
    }

    Context 'always-exits-0 contract' {
        It 'always exits 0 regardless of policy resolve outcome' {
            foreach ($mode in 'loops_3','missing_field','negative','malformed_json','nonzero_exit') {
                $env:POLYPHONY_STUB_MODE = $mode
                try {
                    pwsh -NoProfile -File $script:Script -PolyphonyExe $script:Stub -Scope 'default' | Out-Null
                    $LASTEXITCODE | Should -Be 0 -Because "mode=$mode must still exit 0 (routing is condition-based)"
                }
                finally {
                    Remove-Item Env:\POLYPHONY_STUB_MODE -ErrorAction SilentlyContinue
                }
            }
        }
    }
}
