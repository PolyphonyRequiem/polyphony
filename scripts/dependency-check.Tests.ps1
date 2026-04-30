BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'dependency-check.ps1'

    # Define placeholder functions for external commands so Pester can mock them.
    function global:twig { }
}

AfterAll {
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
}

Describe 'dependency-check.ps1 — no predecessor links' {

    BeforeEach {
        Mock twig {
            $global:LASTEXITCODE = 0
            '{"id":100,"fields":{"System.Title":"Test Item","System.State":"Doing"}}'
        } -ParameterFilter { $args -contains 'show' }
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
    }

    It 'Returns not_blocked when no relations exist' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.status | Should -Be 'not_blocked'
        $result.work_item_id | Should -Be 100
        $result.blocking_items.Count | Should -Be 0
    }

    It 'Returns a descriptive message' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.message | Should -Be 'No predecessor links found'
    }
}

Describe 'dependency-check.ps1 — all predecessors complete' {

    BeforeEach {
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{
                id = 100
                fields = @{ 'System.Title' = 'Test Item'; 'System.State' = 'Doing' }
                relations = @(
                    @{
                        rel = 'System.LinkTypes.Dependency-Reverse'
                        url = 'https://dev.azure.com/org/project/_apis/wit/workItems/200'
                        attributes = @{ name = 'Predecessor' }
                    }
                )
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '100' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{
                id = 200
                fields = @{ 'System.Title' = 'Predecessor Item'; 'System.State' = 'Done' }
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '200' }
    }

    It 'Returns not_blocked when all predecessors are Done' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.status | Should -Be 'not_blocked'
        $result.blocking_items.Count | Should -Be 0
    }
}

Describe 'dependency-check.ps1 — blocked by incomplete predecessor' {

    BeforeEach {
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{
                id = 100
                fields = @{ 'System.Title' = 'Test Item'; 'System.State' = 'Doing' }
                relations = @(
                    @{
                        rel = 'System.LinkTypes.Dependency-Reverse'
                        url = 'https://dev.azure.com/org/project/_apis/wit/workItems/201'
                        attributes = @{ name = 'Predecessor' }
                    },
                    @{
                        rel = 'System.LinkTypes.Dependency-Reverse'
                        url = 'https://dev.azure.com/org/project/_apis/wit/workItems/202'
                        attributes = @{ name = 'Predecessor' }
                    }
                )
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '100' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{
                id = 201
                fields = @{ 'System.Title' = 'Blocked Pred'; 'System.State' = 'To Do' }
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '201' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{
                id = 202
                fields = @{ 'System.Title' = 'Done Pred'; 'System.State' = 'Done' }
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '202' }
    }

    It 'Returns blocked when a predecessor is not in terminal state' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.status | Should -Be 'blocked'
        $result.blocking_items.Count | Should -Be 1
    }

    It 'Includes blocking item details' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.blocking_items[0].id | Should -Be 201
        $result.blocking_items[0].state | Should -Be 'To Do'
    }
}

Describe 'dependency-check.ps1 — error handling' {

    BeforeEach {
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
        Mock twig {
            $global:LASTEXITCODE = 1
            $null
        } -ParameterFilter { $args -contains 'show' }
    }

    It 'Returns not_blocked with error flag when fetch fails' {
        $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
        $result.status | Should -Be 'not_blocked'
        $result.error | Should -BeTrue
    }
}

Describe 'dependency-check.ps1 — output shape' {

    BeforeEach {
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
        Mock twig {
            $global:LASTEXITCODE = 0
            '{"id":100,"fields":{"System.Title":"Test","System.State":"Doing"}}'
        } -ParameterFilter { $args -contains 'show' }
    }

    It 'Returns valid JSON with all required fields' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'status'
        $result.PSObject.Properties.Name | Should -Contain 'work_item_id'
        $result.PSObject.Properties.Name | Should -Contain 'blocking_items'
        $result.PSObject.Properties.Name | Should -Contain 'message'
    }

    It 'status is a valid enum value' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.status | Should -BeIn @('blocked', 'not_blocked')
    }
}
