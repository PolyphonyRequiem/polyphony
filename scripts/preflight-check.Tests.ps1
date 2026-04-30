BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'preflight-check.ps1'

    # Define placeholder functions for external commands so Pester can mock them.
    function global:twig { }
    function global:gh { }
    function global:git { }
    function global:polyphony { }
    function global:dotnet { }
}

AfterAll {
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
    Remove-Item Function:\gh -ErrorAction SilentlyContinue
    Remove-Item Function:\git -ErrorAction SilentlyContinue
    Remove-Item Function:\polyphony -ErrorAction SilentlyContinue
    Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
}

Describe 'preflight-check.ps1 — output shape' {

    BeforeEach {
        # Default mocks — all checks pass. Must set $global:LASTEXITCODE = 0
        # because Pester function mocks don't set LASTEXITCODE like real exes.
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestOrg"}' } -ParameterFilter { $args -contains 'organization' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestProject"}' } -ParameterFilter { $args -contains 'project' }
        Mock twig { $global:LASTEXITCODE = 0; '{"id":42,"title":"Test Item","state":"Doing"}' } -ParameterFilter { $args -contains 'show' }
        Mock gh { $global:LASTEXITCODE = 0; 'Logged in' } -ParameterFilter { $args -contains 'status' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock dotnet { $global:LASTEXITCODE = 0; '10.0.100' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Returns valid JSON with all required top-level fields' {
        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'ready'
        $result.PSObject.Properties.Name | Should -Contain 'summary'
        $result.PSObject.Properties.Name | Should -Contain 'required_checks'
        $result.PSObject.Properties.Name | Should -Contain 'advisory_checks'
        $result.PSObject.Properties.Name | Should -Contain 'failed_count'
        $result.PSObject.Properties.Name | Should -Contain 'warning_count'
        $result.PSObject.Properties.Name | Should -Contain 'details'
    }

    It 'Returns ready=true when all required checks pass' {
        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeTrue
        $result.failed_count | Should -Be 0
    }

    It 'Returns details with work_item_id, ado_org, ado_project' {
        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.details.work_item_id | Should -Be 42
        $result.details.ado_org | Should -Be 'TestOrg'
        $result.details.ado_project | Should -Be 'TestProject'
    }
}

Describe 'preflight-check.ps1 — required checks' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestOrg"}' } -ParameterFilter { $args -contains 'organization' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestProject"}' } -ParameterFilter { $args -contains 'project' }
        Mock twig { $global:LASTEXITCODE = 0; '{"id":42,"title":"Test Item","state":"Doing"}' } -ParameterFilter { $args -contains 'show' }
        Mock gh { $global:LASTEXITCODE = 0; 'Logged in' } -ParameterFilter { $args -contains 'status' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock dotnet { $global:LASTEXITCODE = 0; '10.0.100' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Fails when not in a git repository' {
        Mock git { $global:LASTEXITCODE = 128; $null } -ParameterFilter { $args -contains '--show-toplevel' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $result.failed_count | Should -BeGreaterOrEqual 1
        $gitCheck = $result.required_checks | Where-Object { $_.name -eq 'git_repo' }
        $gitCheck.passed | Should -BeFalse
    }

    It 'Fails when twig CLI is not available' {
        Mock twig { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $twigCheck = $result.required_checks | Where-Object { $_.name -eq 'twig_cli' }
        $twigCheck.passed | Should -BeFalse
    }

    It 'Fails when twig config is missing org' {
        Mock twig { '{"info":""}' } -ParameterFilter { $args -contains 'organization' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $configCheck = $result.required_checks | Where-Object { $_.name -eq 'twig_config' }
        $configCheck.passed | Should -BeFalse
    }

    It 'Fails when work item is not accessible' {
        Mock twig { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains 'show' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $adoCheck = $result.required_checks | Where-Object { $_.name -eq 'ado_access' }
        $adoCheck.passed | Should -BeFalse
    }

    It 'Has exactly 4 required checks' {
        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.required_checks.Count | Should -Be 4
    }
}

Describe 'preflight-check.ps1 — advisory checks' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestOrg"}' } -ParameterFilter { $args -contains 'organization' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestProject"}' } -ParameterFilter { $args -contains 'project' }
        Mock twig { $global:LASTEXITCODE = 0; '{"id":42,"title":"Test Item","state":"Doing"}' } -ParameterFilter { $args -contains 'show' }
        Mock gh { $global:LASTEXITCODE = 0; 'Logged in' } -ParameterFilter { $args -contains 'status' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock dotnet { $global:LASTEXITCODE = 0; '10.0.100' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Warns when gh CLI is not authenticated' {
        Mock gh { $global:LASTEXITCODE = 1; 'not logged in' } -ParameterFilter { $args -contains 'status' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeTrue
        $result.warning_count | Should -BeGreaterOrEqual 1
        $ghCheck = $result.advisory_checks | Where-Object { $_.name -eq 'gh_auth' }
        $ghCheck.passed | Should -BeFalse
        $ghCheck.remediation | Should -Not -BeNullOrEmpty
    }

    It 'Warns when polyphony CLI is not found' {
        Mock polyphony { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeTrue
        $polyCheck = $result.advisory_checks | Where-Object { $_.name -eq 'polyphony_cli' }
        $polyCheck.passed | Should -BeFalse
        $polyCheck.remediation | Should -Not -BeNullOrEmpty
    }

    It 'Warns when dotnet SDK is not found' {
        Mock dotnet { $global:LASTEXITCODE = 1; $null } -ParameterFilter { $args -contains '--version' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.ready | Should -BeTrue
        $dotnetCheck = $result.advisory_checks | Where-Object { $_.name -eq 'dotnet_sdk' }
        $dotnetCheck.passed | Should -BeFalse
        $dotnetCheck.remediation | Should -Not -BeNullOrEmpty
    }

    It 'Has exactly 3 advisory checks' {
        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.advisory_checks.Count | Should -Be 3
    }
}

Describe 'preflight-check.ps1 — summary messages' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestOrg"}' } -ParameterFilter { $args -contains 'organization' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestProject"}' } -ParameterFilter { $args -contains 'project' }
        Mock twig { $global:LASTEXITCODE = 0; '{"id":42,"title":"Test Item","state":"Doing"}' } -ParameterFilter { $args -contains 'show' }
        Mock gh { $global:LASTEXITCODE = 0; 'Logged in' } -ParameterFilter { $args -contains 'status' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock dotnet { $global:LASTEXITCODE = 0; '10.0.100' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Shows success summary when all checks pass' {
        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.summary | Should -Be 'All preflight checks passed.'
    }

    It 'Shows warning summary when required pass but advisory fails' {
        Mock gh { $global:LASTEXITCODE = 1; 'not logged in' } -ParameterFilter { $args -contains 'status' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.summary | Should -BeLike '*advisory warning*'
        $result.ready | Should -BeTrue
    }

    It 'Shows failure summary when required checks fail' {
        Mock git { $global:LASTEXITCODE = 128; $null } -ParameterFilter { $args -contains '--show-toplevel' }

        $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
        $result.summary | Should -BeLike '*required check*failed*'
        $result.ready | Should -BeFalse
    }
}

Describe 'preflight-check.ps1 — error handling' {

    BeforeEach {
        Mock git { $global:LASTEXITCODE = 0; '/repo/root' } -ParameterFilter { $args -contains '--show-toplevel' }
        Mock twig { $global:LASTEXITCODE = 0; 'twig 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestOrg"}' } -ParameterFilter { $args -contains 'organization' }
        Mock twig { $global:LASTEXITCODE = 0; '{"info":"TestProject"}' } -ParameterFilter { $args -contains 'project' }
        Mock twig { $global:LASTEXITCODE = 0; '{"id":42,"title":"Test Item","state":"Doing"}' } -ParameterFilter { $args -contains 'show' }
        Mock gh { $global:LASTEXITCODE = 0; 'Logged in' } -ParameterFilter { $args -contains 'status' }
        Mock polyphony { $global:LASTEXITCODE = 0; 'polyphony 1.0.0' } -ParameterFilter { $args -contains '--version' }
        Mock dotnet { $global:LASTEXITCODE = 0; '10.0.100' } -ParameterFilter { $args -contains '--version' }
    }

    It 'Returns ready=false with error on unexpected exception' {
        # Force an error by making ado-helpers throw
        Mock twig { throw 'Unexpected error in config' } -ParameterFilter { $args -contains 'organization' }

        $result = & $script:ScriptPath -WorkItemId 42 2>$null | ConvertFrom-Json
        $result.ready | Should -BeFalse
        $result.details.error | Should -Not -BeNullOrEmpty
    }
}
