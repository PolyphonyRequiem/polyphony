BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'child-router.ps1'

    function global:polyphony { }
}

AfterAll {
    Remove-Item Function:\polyphony -ErrorAction SilentlyContinue
}

Describe 'child-router.ps1 — plannable children found' {

    BeforeEach {
        Mock polyphony {
            [ordered]@{
                work_item_id   = 100
                title          = 'Epic'
                work_item_type = 'Epic'
                capabilities   = @('plannable')
                children       = @(
                    [ordered]@{
                        work_item_id   = 201
                        title          = 'Issue A'
                        work_item_type = 'Issue'
                        capabilities   = @('plannable')
                    },
                    [ordered]@{
                        work_item_id   = 202
                        title          = 'Task B'
                        work_item_type = 'Task'
                        capabilities   = @('implementable')
                    },
                    [ordered]@{
                        work_item_id   = 203
                        title          = 'Issue C'
                        work_item_type = 'Issue'
                        capabilities   = @('plannable', 'implementable')
                    }
                )
            } | ConvertTo-Json -Depth 4
        } -ParameterFilter { $args -contains 'hierarchy' }
    }

    It 'Returns has_plannable_children=true when plannable children exist' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.has_plannable_children | Should -BeTrue
    }

    It 'Returns only children with plannable capability' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.count | Should -Be 2
        $result.plannable_children[0].id | Should -Be 201
        $result.plannable_children[0].type | Should -Be 'Issue'
        $result.plannable_children[0].title | Should -Be 'Issue A'
        $result.plannable_children[1].id | Should -Be 203
        $result.plannable_children[1].type | Should -Be 'Issue'
        $result.plannable_children[1].title | Should -Be 'Issue C'
    }

    It 'Sets parent_id to the input work item ID' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.parent_id | Should -Be 100
    }
}

Describe 'child-router.ps1 — no plannable children' {

    BeforeEach {
        Mock polyphony {
            [ordered]@{
                work_item_id   = 100
                title          = 'Issue'
                work_item_type = 'Issue'
                capabilities   = @('plannable')
                children       = @(
                    [ordered]@{
                        work_item_id   = 301
                        title          = 'Task X'
                        work_item_type = 'Task'
                        capabilities   = @('implementable')
                    },
                    [ordered]@{
                        work_item_id   = 302
                        title          = 'Task Y'
                        work_item_type = 'Task'
                        capabilities   = @('implementable')
                    }
                )
            } | ConvertTo-Json -Depth 4
        } -ParameterFilter { $args -contains 'hierarchy' }
    }

    It 'Returns has_plannable_children=false when no plannable children' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.has_plannable_children | Should -BeFalse
    }

    It 'Returns empty plannable_children array' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.plannable_children | Should -HaveCount 0
    }

    It 'Returns count of 0' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.count | Should -Be 0
    }
}

Describe 'child-router.ps1 — no children at all' {

    BeforeEach {
        Mock polyphony {
            [ordered]@{
                work_item_id   = 100
                title          = 'Leaf Item'
                work_item_type = 'Task'
                capabilities   = @('implementable')
            } | ConvertTo-Json -Depth 4
        } -ParameterFilter { $args -contains 'hierarchy' }
    }

    It 'Returns has_plannable_children=false for items with no children' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.has_plannable_children | Should -BeFalse
        $result.plannable_children | Should -HaveCount 0
    }
}

Describe 'child-router.ps1 — polyphony hierarchy failure' {

    BeforeEach {
        Mock polyphony { $null } -ParameterFilter {
            $args -contains 'hierarchy'
        }
    }

    It 'Returns error JSON with has_plannable_children=false when hierarchy fails' {
        $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
        $result.has_plannable_children | Should -BeFalse
        $result.plannable_children | Should -HaveCount 0
        $result.error | Should -BeLike '*Failed to retrieve hierarchy*'
    }

    It 'Exits 0 even on error (routing is condition-based)' {
        & $script:ScriptPath -WorkItemId 999 | Out-Null
        $LASTEXITCODE | Should -BeIn @($null, 0)
    }
}

Describe 'child-router.ps1 — output shape' {

    BeforeEach {
        Mock polyphony {
            [ordered]@{
                work_item_id   = 100
                title          = 'Epic'
                work_item_type = 'Epic'
                capabilities   = @('plannable')
                children       = @(
                    [ordered]@{
                        work_item_id   = 201
                        title          = 'Issue A'
                        work_item_type = 'Issue'
                        capabilities   = @('plannable')
                    }
                )
            } | ConvertTo-Json -Depth 4
        } -ParameterFilter { $args -contains 'hierarchy' }
    }

    It 'Returns valid JSON with all required fields' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'has_plannable_children'
        $result.PSObject.Properties.Name | Should -Contain 'plannable_children'
        $result.PSObject.Properties.Name | Should -Contain 'parent_id'
        $result.PSObject.Properties.Name | Should -Contain 'count'
    }

    It 'Each plannable child has id, type, and title fields' {
        $result = & $script:ScriptPath -WorkItemId 100 | ConvertFrom-Json
        $child = $result.plannable_children[0]
        $child.PSObject.Properties.Name | Should -Contain 'id'
        $child.PSObject.Properties.Name | Should -Contain 'type'
        $child.PSObject.Properties.Name | Should -Contain 'title'
    }
}
