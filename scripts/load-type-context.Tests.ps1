BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'load-type-context.ps1'

    # Define placeholder functions for external commands so Pester can mock them.
    function global:twig { }
}

AfterAll {
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
}

Describe 'load-type-context.ps1 — successful load' {

    BeforeEach {
        # Set up a temp .conductor directory structure
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-type-context-test-$(Get-Random)"
        New-Item -ItemType Directory -Path (Join-Path $script:TempDir 'work-item-types/templates') -Force | Out-Null

        # Create type definition file
        Set-Content -Path (Join-Path $script:TempDir 'work-item-types/issue.md') -Value 'An Issue is a unit of work.'

        # Create template file
        Set-Content -Path (Join-Path $script:TempDir 'work-item-types/templates/issue-template.md') -Value '## Template Content'

        # Create process-config.yaml
        $processConfig = @"
process_template: Basic

types:
  Issue:
    capabilities: [plannable, implementable]
    filing_eligible: true
    max_nesting_depth: 1
    decomposition_guidance: |
      Decompose into Tasks when scope exceeds a single PG.
  Task:
    capabilities: [implementable]
    filing_eligible: true
"@
        Set-Content -Path (Join-Path $script:TempDir 'process-config.yaml') -Value $processConfig

        Mock twig {
            $global:LASTEXITCODE = 0
            '{"id":42,"title":"Test Issue","type":"Issue","state":"Doing"}'
        } -ParameterFilter { $args -contains 'show' }
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Returns correct type name from work item' {
        $result = & $script:ScriptPath -WorkItemId 42 -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.type | Should -Be 'Issue'
    }

    It 'Returns the type definition content' {
        $result = & $script:ScriptPath -WorkItemId 42 -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.definition | Should -BeLike '*Issue is a unit of work*'
    }

    It 'Returns the template content when present' {
        $result = & $script:ScriptPath -WorkItemId 42 -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.template | Should -BeLike '*Template Content*'
    }

    It 'Returns decomposition guidance from process-config.yaml' {
        $result = & $script:ScriptPath -WorkItemId 42 -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.decomposition_guidance | Should -BeLike '*Decompose into Tasks*'
    }

    It 'Returns all required fields in the output' {
        $result = & $script:ScriptPath -WorkItemId 42 -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'type'
        $result.PSObject.Properties.Name | Should -Contain 'definition'
        $result.PSObject.Properties.Name | Should -Contain 'template'
        $result.PSObject.Properties.Name | Should -Contain 'decomposition_guidance'
    }
}

Describe 'load-type-context.ps1 — missing template' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-type-context-test-$(Get-Random)"
        New-Item -ItemType Directory -Path (Join-Path $script:TempDir 'work-item-types/templates') -Force | Out-Null
        Set-Content -Path (Join-Path $script:TempDir 'work-item-types/task.md') -Value 'A Task is an atomic unit of work.'

        $processConfig = @"
process_template: Basic

types:
  Task:
    capabilities: [implementable]
    filing_eligible: true
"@
        Set-Content -Path (Join-Path $script:TempDir 'process-config.yaml') -Value $processConfig

        Mock twig {
            $global:LASTEXITCODE = 0
            '{"id":99,"title":"Test Task","type":"Task","state":"To Do"}'
        } -ParameterFilter { $args -contains 'show' }
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Returns empty string for template when template file is missing' {
        $result = & $script:ScriptPath -WorkItemId 99 -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.template | Should -Be ''
    }

    It 'Still returns the type definition even without a template' {
        $result = & $script:ScriptPath -WorkItemId 99 -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.definition | Should -BeLike '*Task is an atomic*'
    }
}

Describe 'load-type-context.ps1 — missing definition' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-type-context-test-$(Get-Random)"
        New-Item -ItemType Directory -Path (Join-Path $script:TempDir 'work-item-types') -Force | Out-Null

        Mock twig {
            $global:LASTEXITCODE = 0
            '{"id":42,"title":"Unknown","type":"Feature","state":"To Do"}'
        } -ParameterFilter { $args -contains 'show' }
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Exits non-zero when type definition file is not found' {
        $output = & $script:ScriptPath -WorkItemId 42 -ConfigPath $script:TempDir 2>$null
        $LASTEXITCODE | Should -Be 1
    }

    It 'Returns error JSON when type definition file is not found' {
        $result = & $script:ScriptPath -WorkItemId 42 -ConfigPath $script:TempDir 2>$null | ConvertFrom-Json
        $result.error | Should -BeLike '*not found*'
        $result.type | Should -Be ''
    }
}

Describe 'load-type-context.ps1 — twig show failure' {

    BeforeEach {
        Mock twig {
            $global:LASTEXITCODE = 1
            $null
        } -ParameterFilter { $args -contains 'show' }
    }

    It 'Exits non-zero when twig show fails' {
        & $script:ScriptPath -WorkItemId 999 2>$null | Out-Null
        $LASTEXITCODE | Should -Be 1
    }

    It 'Returns error JSON when twig show fails' {
        $result = & $script:ScriptPath -WorkItemId 999 2>$null | ConvertFrom-Json
        $result.error | Should -BeLike '*Failed to retrieve*'
        $result.type | Should -Be ''
    }
}
