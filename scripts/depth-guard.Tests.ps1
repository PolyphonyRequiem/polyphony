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
