BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'preflight-lite.ps1'

    # Define placeholder functions for external commands so Pester can mock them.
    function global:git { }
    function global:twig { }
    function global:polyphony { }
}

AfterAll {
    Remove-Item Function:\git -ErrorAction SilentlyContinue
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
    Remove-Item Function:\polyphony -ErrorAction SilentlyContinue
}

Describe 'preflight-lite.ps1 — output shape' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Returns valid JSON with all required top-level fields' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'ready'
        $result.PSObject.Properties.Name | Should -Contain 'summary'
        $result.PSObject.Properties.Name | Should -Contain 'checks'
        $result.PSObject.Properties.Name | Should -Contain 'failed_count'
    }

    It 'Has exactly 3 checks' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.checks.Count | Should -Be 3
    }

    It 'Each check has name, passed, and detail fields' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        foreach ($check in $result.checks) {
            $check.PSObject.Properties.Name | Should -Contain 'name'
            $check.PSObject.Properties.Name | Should -Contain 'passed'
            $check.PSObject.Properties.Name | Should -Contain 'detail'
        }
    }
}

Describe 'preflight-lite.ps1 — all checks pass' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Returns ready=true when all checks pass' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.ready | Should -BeTrue
        $result.failed_count | Should -Be 0
    }

    It 'Shows success summary when all checks pass' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.summary | Should -Be 'All preflight lite checks passed.'
    }

    It 'All individual check results have passed=true' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        foreach ($check in $result.checks) {
            $check.passed | Should -BeTrue
        }
    }
}

Describe 'preflight-lite.ps1 — individual check failures' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Fails git_repo check when not in a git repository' {
        Mock git { $global:LASTEXITCODE = 128; $null } -ParameterFilter { $args -contains '--show-toplevel' }

        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $gitCheck = $result.checks | Where-Object { $_.name -eq 'git_repo' }
        $gitCheck.passed | Should -BeFalse
        $gitCheck.remediation | Should -Not -BeNullOrEmpty
    }

    It 'Fails twig_cli check when twig is not available' {
        Mock twig { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }

        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $twigCheck = $result.checks | Where-Object { $_.name -eq 'twig_cli' }
        $twigCheck.passed | Should -BeFalse
        $twigCheck.remediation | Should -Not -BeNullOrEmpty
    }

    It 'Fails polyphony_cli check when polyphony is not available' {
        Mock polyphony { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }

        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $polyCheck = $result.checks | Where-Object { $_.name -eq 'polyphony_cli' }
        $polyCheck.passed | Should -BeFalse
        $polyCheck.remediation | Should -Not -BeNullOrEmpty
    }

    It 'Reports failed_count=1 when exactly one check fails' {
        Mock twig { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }

        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.failed_count | Should -Be 1
    }
}

Describe 'preflight-lite.ps1 — all checks fail' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 128; $null } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }
        Mock polyphony { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }
    }

    It 'Returns ready=false when all checks fail' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.ready | Should -BeFalse
    }

    It 'Reports failed_count=3 when all checks fail' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.failed_count | Should -Be 3
    }

    It 'Shows failure summary when checks fail' {
        $result = & $script:ScriptPath | ConvertFrom-Json
        $result.summary | Should -BeLike '*failed*'
    }
}

Describe 'preflight-lite.ps1 — error handling' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Returns ready=false with error summary on unexpected exception' {
        Mock git { throw 'Unexpected git failure' } -ParameterFilter { $args -contains '--show-toplevel' }

        $result = & $script:ScriptPath 2>$null | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $result.summary | Should -BeLike 'Preflight lite error:*'
        $result.failed_count | Should -Be 1
        $result.checks | Should -BeNullOrEmpty
    }
}

# ── Output schema compatibility verification (#2779) ─────────────────────────

Describe 'preflight-lite.ps1 — output schema compatibility (#2779)' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
    }

    Context 'Required schema keys — all 4 from twig-sdlc-v2-implement.yaml / twig-sdlc-v2-planning.yaml' {

        It 'Contains all 4 required top-level keys' {
            $requiredKeys = @('ready', 'summary', 'checks', 'failed_count')

            $result = & $script:ScriptPath | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name

            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath | ConvertFrom-Json

            # Boolean — routed on by twig-sdlc-v2-implement.yaml: {{ preflight_lite.output.ready == true }}
            $result.ready | Should -BeOfType [bool]

            # String
            $result.summary | Should -BeOfType [string]

            # Integer
            $result.failed_count | Should -BeOfType [long]

            # Array
            $result.PSObject.Properties.Name | Should -Contain 'checks'
        }

        It 'checks entries contain required sub-keys' {
            $result = & $script:ScriptPath | ConvertFrom-Json
            foreach ($check in $result.checks) {
                $checkKeys = $check.PSObject.Properties.Name
                $checkKeys | Should -Contain 'name'
                $checkKeys | Should -Contain 'passed'
                $checkKeys | Should -Contain 'detail'
            }
        }
    }

    Context 'ready field compatibility — dual consumer routing' {

        It 'Returns ready=true when all checks pass (routes to load_work_tree / plan_level)' {
            $result = & $script:ScriptPath | ConvertFrom-Json
            $result.ready | Should -BeTrue
        }

        It 'Returns ready=false when a check fails (routes to preflight_lite_gate)' {
            Mock twig { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }

            $result = & $script:ScriptPath | ConvertFrom-Json
            $result.ready | Should -BeFalse
        }
    }

    Context 'Error path — maintains schema on failure' {

        It 'Error output preserves all required top-level keys' {
            Mock git { throw 'Fatal error' } -ParameterFilter { $args -contains '--show-toplevel' }

            $result = & $script:ScriptPath 2>$null | ConvertFrom-Json
            $requiredKeys = @('ready', 'summary', 'checks', 'failed_count')
            foreach ($key in $requiredKeys) {
                $result.PSObject.Properties.Name | Should -Contain $key -Because "error path must preserve key '$key'"
            }
        }
    }
}
