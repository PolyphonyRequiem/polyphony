BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'depth-guard.ps1'
}

Describe 'depth-guard.ps1 — allowed scenarios' {

    It 'Returns allowed=true when depth is 0 and max_depth is 6' {
        $result = & $script:ScriptPath -Depth 0 -MaxDepth 6 | ConvertFrom-Json
        $result.allowed | Should -BeTrue
        $result.depth | Should -Be 0
        $result.max_depth | Should -Be 6
        $result.remaining | Should -Be 6
        $result.message | Should -BeLike '*within limit*'
    }

    It 'Returns allowed=true when depth is below max_depth' {
        $result = & $script:ScriptPath -Depth 3 -MaxDepth 6 | ConvertFrom-Json
        $result.allowed | Should -BeTrue
        $result.remaining | Should -Be 3
    }

    It 'Returns allowed=true when depth is one below max_depth' {
        $result = & $script:ScriptPath -Depth 5 -MaxDepth 6 | ConvertFrom-Json
        $result.allowed | Should -BeTrue
        $result.remaining | Should -Be 1
    }
}

Describe 'depth-guard.ps1 — disallowed scenarios' {

    It 'Returns allowed=false when depth equals max_depth' {
        $result = & $script:ScriptPath -Depth 6 -MaxDepth 6 | ConvertFrom-Json
        $result.allowed | Should -BeFalse
        $result.remaining | Should -Be 0
        $result.message | Should -BeLike '*reached maximum*'
    }

    It 'Returns allowed=false when depth exceeds max_depth' {
        $result = & $script:ScriptPath -Depth 10 -MaxDepth 6 | ConvertFrom-Json
        $result.allowed | Should -BeFalse
        $result.remaining | Should -Be 0
    }
}

Describe 'depth-guard.ps1 — default max_depth' {

    It 'Uses default max_depth of 6 when not specified' {
        $result = & $script:ScriptPath -Depth 0 | ConvertFrom-Json
        $result.max_depth | Should -Be 6
        $result.allowed | Should -BeTrue
    }
}

Describe 'depth-guard.ps1 — exit code' {

    It 'Always exits 0 even when not allowed' {
        & $script:ScriptPath -Depth 10 -MaxDepth 3 | Out-Null
        # Pure PowerShell scripts don't set $LASTEXITCODE (stays $null).
        # Verify it did NOT exit non-zero.
        $LASTEXITCODE | Should -BeIn @($null, 0)
    }
}

Describe 'depth-guard.ps1 — output shape' {

    It 'Returns valid JSON with all required fields' {
        $result = & $script:ScriptPath -Depth 2 -MaxDepth 4 | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'allowed'
        $result.PSObject.Properties.Name | Should -Contain 'depth'
        $result.PSObject.Properties.Name | Should -Contain 'max_depth'
        $result.PSObject.Properties.Name | Should -Contain 'remaining'
        $result.PSObject.Properties.Name | Should -Contain 'message'
    }
}

# ── Output schema compatibility verification (#2779) ─────────────────────────

Describe 'depth-guard.ps1 — output schema compatibility (#2779)' {

    Context 'Required schema keys — all 5 from plan-level.yaml' {

        It 'Contains all 5 required top-level keys' {
            $requiredKeys = @('allowed', 'depth', 'max_depth', 'remaining', 'message')

            $result = & $script:ScriptPath -Depth 1 -MaxDepth 6 | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name

            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath -Depth 1 -MaxDepth 6 | ConvertFrom-Json

            # Boolean — routed on by plan-level.yaml: {{ depth_guard.output.allowed == true }}
            $result.allowed | Should -BeOfType [bool]

            # Integer
            $result.depth | Should -BeOfType [long]
            $result.max_depth | Should -BeOfType [long]
            $result.remaining | Should -BeOfType [long]

            # String
            $result.message | Should -BeOfType [string]
        }
    }

    Context 'allowed field compatibility — plan-level.yaml routing' {

        It 'Returns allowed=true when under limit (routes to type_loader)' {
            $result = & $script:ScriptPath -Depth 0 -MaxDepth 6 | ConvertFrom-Json
            $result.allowed | Should -BeTrue
        }

        It 'Returns allowed=false when at limit (routes to depth_exceeded_gate)' {
            $result = & $script:ScriptPath -Depth 6 -MaxDepth 6 | ConvertFrom-Json
            $result.allowed | Should -BeFalse
        }

        It 'Returns allowed=false when over limit (routes to depth_exceeded_gate)' {
            $result = & $script:ScriptPath -Depth 10 -MaxDepth 6 | ConvertFrom-Json
            $result.allowed | Should -BeFalse
        }
    }

    Context 'Schema stability — same keys in allowed and disallowed paths' {

        It 'Allowed output has identical key set to disallowed output' {
            $allowed = & $script:ScriptPath -Depth 0 -MaxDepth 6 | ConvertFrom-Json
            $disallowed = & $script:ScriptPath -Depth 6 -MaxDepth 6 | ConvertFrom-Json

            $allowedKeys = @($allowed.PSObject.Properties.Name | Sort-Object)
            $disallowedKeys = @($disallowed.PSObject.Properties.Name | Sort-Object)
            $allowedKeys | Should -Be $disallowedKeys
        }
    }
}
