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
        $result.blocked | Should -BeFalse
        $result.work_item_id | Should -Be 100
        $result.blocking_items.Count | Should -Be 0
    }

    It 'Returns a descriptive message' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.message | Should -Be 'No predecessor links found'
    }

    It 'Returns zero counts when no predecessors' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.ready_count | Should -Be 0
        $result.total_count | Should -Be 0
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
        $result.blocked | Should -BeFalse
        $result.blocking_items.Count | Should -Be 0
    }

    It 'Returns correct ready and total counts' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.ready_count | Should -Be 1
        $result.total_count | Should -Be 1
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
        $result.blocked | Should -BeTrue
        $result.blocking_items.Count | Should -Be 1
    }

    It 'Includes blocking item details' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.blocking_items[0].id | Should -Be 201
        $result.blocking_items[0].state | Should -Be 'To Do'
    }

    It 'Reports correct ready and total counts' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.ready_count | Should -Be 1
        $result.total_count | Should -Be 2
    }
}

Describe 'dependency-check.ps1 — multiple blocked predecessors' {

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
                        url = 'https://dev.azure.com/org/project/_apis/wit/workItems/301'
                        attributes = @{ name = 'Predecessor' }
                    },
                    @{
                        rel = 'System.LinkTypes.Dependency-Reverse'
                        url = 'https://dev.azure.com/org/project/_apis/wit/workItems/302'
                        attributes = @{ name = 'Predecessor' }
                    },
                    @{
                        rel = 'System.LinkTypes.Dependency-Reverse'
                        url = 'https://dev.azure.com/org/project/_apis/wit/workItems/303'
                        attributes = @{ name = 'Predecessor' }
                    }
                )
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '100' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{ id = 301; fields = @{ 'System.Title' = 'Active 1'; 'System.State' = 'Doing' } } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '301' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{ id = 302; fields = @{ 'System.Title' = 'Done One'; 'System.State' = 'Closed' } } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '302' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{ id = 303; fields = @{ 'System.Title' = 'Active 2'; 'System.State' = 'New' } } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '303' }
    }

    It 'Reports all blocking predecessors' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.blocked | Should -BeTrue
        $result.blocking_items.Count | Should -Be 2
        $result.ready_count | Should -Be 1
        $result.total_count | Should -Be 3
    }

    It 'Recognizes Closed as terminal state' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $blockedIds = @($result.blocking_items | ForEach-Object { $_.id })
        $blockedIds | Should -Contain 301
        $blockedIds | Should -Contain 303
        $blockedIds | Should -Not -Contain 302
    }
}

Describe 'dependency-check.ps1 — predecessor with id field instead of url' {

    BeforeEach {
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{
                id = 100
                fields = @{ 'System.Title' = 'Test'; 'System.State' = 'Doing' }
                relations = @(
                    @{
                        rel = 'System.LinkTypes.Dependency-Reverse'
                        id = 400
                        attributes = @{ name = 'Predecessor' }
                    }
                )
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '100' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{ id = 400; fields = @{ 'System.Title' = 'ID-based Pred'; 'System.State' = 'Doing' } } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '400' }
    }

    It 'Resolves predecessor via id field fallback' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.blocked | Should -BeTrue
        $result.blocking_items[0].id | Should -Be 400
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
        $result.blocked | Should -BeFalse
        $result.error | Should -BeTrue
    }

    It 'Returns zero counts on error' {
        $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
        $result.ready_count | Should -Be 0
        $result.total_count | Should -Be 0
    }
}

Describe 'dependency-check.ps1 — failed predecessor fetch treated as blocking' {

    BeforeEach {
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
        Mock twig {
            $global:LASTEXITCODE = 0
            @{
                id = 100
                fields = @{ 'System.Title' = 'Test'; 'System.State' = 'Doing' }
                relations = @(
                    @{
                        rel = 'System.LinkTypes.Dependency-Reverse'
                        url = 'https://dev.azure.com/org/project/_apis/wit/workItems/500'
                        attributes = @{ name = 'Predecessor' }
                    }
                )
            } | ConvertTo-Json -Depth 5
        } -ParameterFilter { $args -contains 'show' -and $args -contains '100' }
        Mock twig {
            $global:LASTEXITCODE = 1
            $null
        } -ParameterFilter { $args -contains 'show' -and $args -contains '500' }
    }

    It 'Treats unfetchable predecessor as blocking' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.blocked | Should -BeTrue
        $result.blocking_items[0].id | Should -Be 500
        $result.blocking_items[0].title | Should -Be 'Unknown (failed to fetch)'
        $result.blocking_items[0].state | Should -Be 'Unknown'
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
        $result.PSObject.Properties.Name | Should -Contain 'blocked'
        $result.PSObject.Properties.Name | Should -Contain 'status'
        $result.PSObject.Properties.Name | Should -Contain 'work_item_id'
        $result.PSObject.Properties.Name | Should -Contain 'blocking_items'
        $result.PSObject.Properties.Name | Should -Contain 'ready_count'
        $result.PSObject.Properties.Name | Should -Contain 'total_count'
        $result.PSObject.Properties.Name | Should -Contain 'message'
    }

    It 'status is a valid enum value' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.status | Should -BeIn @('blocked', 'not_blocked')
    }

    It 'blocked is a boolean' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.blocked | Should -BeOfType [bool]
    }

    It 'ready_count and total_count are numeric' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.ready_count | Should -BeOfType [long]
        $result.total_count | Should -BeOfType [long]
    }
}

# ── Output schema compatibility verification (#2779) ─────────────────────────

Describe 'dependency-check.ps1 — output schema compatibility (#2779)' {

    BeforeEach {
        Mock twig { $global:LASTEXITCODE = 0; '{}' } -ParameterFilter { $args -contains 'sync' }
    }

    Context 'Required schema keys — all 7 from implement-pg.yaml' {

        BeforeEach {
            Mock twig {
                $global:LASTEXITCODE = 0
                '{"id":100,"fields":{"System.Title":"Test","System.State":"Doing"}}'
            } -ParameterFilter { $args -contains 'show' }
        }

        It 'Contains all 7 required top-level keys' {
            $requiredKeys = @(
                'blocked', 'status', 'work_item_id', 'blocking_items',
                'ready_count', 'total_count', 'message'
            )

            $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name

            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json

            # Boolean
            $result.blocked | Should -BeOfType [bool]

            # String — routed on by implement-pg.yaml: {{ dependency_check.output.status == "blocked" }}
            $result.status | Should -BeOfType [string]

            # Integer
            $result.work_item_id | Should -BeOfType [long]
            $result.ready_count | Should -BeOfType [long]
            $result.total_count | Should -BeOfType [long]

            # String
            $result.message | Should -BeOfType [string]
        }
    }

    Context 'status field compatibility — implement-pg.yaml routing' {

        It 'Returns status=not_blocked when no blockers (routes to reducer_issue)' {
            Mock twig {
                $global:LASTEXITCODE = 0
                '{"id":100,"fields":{"System.Title":"Test","System.State":"Doing"}}'
            } -ParameterFilter { $args -contains 'show' }

            $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
            $result.status | Should -Be 'not_blocked'
        }

        It 'Returns status=blocked when blockers exist (routes to dependency_gate)' {
            Mock twig {
                $global:LASTEXITCODE = 0
                @{
                    id = 100
                    fields = @{ 'System.Title' = 'Test'; 'System.State' = 'Doing' }
                    relations = @(
                        @{
                            rel = 'System.LinkTypes.Dependency-Reverse'
                            url = 'https://dev.azure.com/org/project/_apis/wit/workItems/201'
                            attributes = @{ name = 'Predecessor' }
                        }
                    )
                } | ConvertTo-Json -Depth 5
            } -ParameterFilter { $args -contains 'show' -and $args -contains '100' }
            Mock twig {
                $global:LASTEXITCODE = 0
                @{ id = 201; fields = @{ 'System.Title' = 'Blocker'; 'System.State' = 'To Do' } } | ConvertTo-Json -Depth 5
            } -ParameterFilter { $args -contains 'show' -and $args -contains '201' }

            $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
            $result.status | Should -Be 'blocked'
        }

        It 'status is always one of the expected enum values' {
            Mock twig {
                $global:LASTEXITCODE = 0
                '{"id":100,"fields":{"System.Title":"Test","System.State":"Doing"}}'
            } -ParameterFilter { $args -contains 'show' }

            $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
            $result.status | Should -BeIn @('blocked', 'not_blocked')
        }
    }

    Context 'blocking_items sub-object schema' {

        It 'Each blocking item has id, title, and state sub-keys' {
            Mock twig {
                $global:LASTEXITCODE = 0
                @{
                    id = 100
                    fields = @{ 'System.Title' = 'Test'; 'System.State' = 'Doing' }
                    relations = @(
                        @{
                            rel = 'System.LinkTypes.Dependency-Reverse'
                            url = 'https://dev.azure.com/org/project/_apis/wit/workItems/201'
                            attributes = @{ name = 'Predecessor' }
                        }
                    )
                } | ConvertTo-Json -Depth 5
            } -ParameterFilter { $args -contains 'show' -and $args -contains '100' }
            Mock twig {
                $global:LASTEXITCODE = 0
                @{ id = 201; fields = @{ 'System.Title' = 'Blocker'; 'System.State' = 'To Do' } } | ConvertTo-Json -Depth 5
            } -ParameterFilter { $args -contains 'show' -and $args -contains '201' }

            $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
            $item = $result.blocking_items[0]
            $itemKeys = $item.PSObject.Properties.Name
            $itemKeys | Should -Contain 'id'
            $itemKeys | Should -Contain 'title'
            $itemKeys | Should -Contain 'state'
        }
    }

    Context 'Error path — maintains schema on failure' {

        It 'Error output preserves all required top-level keys' {
            Mock twig {
                $global:LASTEXITCODE = 1
                $null
            } -ParameterFilter { $args -contains 'show' }

            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $requiredKeys = @('blocked', 'status', 'work_item_id', 'blocking_items', 'ready_count', 'total_count', 'message')
            foreach ($key in $requiredKeys) {
                $result.PSObject.Properties.Name | Should -Contain $key -Because "error path must preserve key '$key'"
            }
        }
    }
}
