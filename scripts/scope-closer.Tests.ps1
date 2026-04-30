BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'scope-closer.ps1'
    $script:HelpersPath = Join-Path $PSScriptRoot 'lib' 'pg-helpers.ps1'

    . $script:HelpersPath

    function global:polyphony { }
    function global:twig { }
}

AfterAll {
    Remove-Item Function:\polyphony -ErrorAction SilentlyContinue
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
}

# Default hierarchy fixture (inline in mocks — $script: vars not visible in mock scriptblocks)
# Epic 42 → Issue 100 (PG-1, Doing) → Tasks 200 (PG-1, Doing), 201 (PG-1, Done)
#         → Issue 101 (PG-2, To Do) → Task 300 (PG-2, To Do)

Describe 'scope-closer.ps1 — polyphony validate transition check (#2647)' {

    BeforeEach {
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock twig { } -ParameterFilter { $args -contains 'set' }
        Mock twig { } -ParameterFilter { $args -contains 'state' }

        Mock polyphony {
            @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1; twig","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Doing","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"To Do","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}]}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }

        # Default: validate returns valid with target_state Done
        Mock polyphony {
            '{"work_item_id":0,"event":"implementation_complete","is_valid":true,"target_state":"Done","message":"Transition allowed"}'
        } -ParameterFilter { $args -contains 'validate' }
    }

    Context 'Valid transitions' {

        It 'Closes non-Done items in the specified PG when validate returns valid' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.pg_name | Should -Be 'PG-1'
            $result.total_closed | Should -Be 2
            $result.total_failed | Should -Be 0
            $result.closed_items.Count | Should -Be 2
        }

        It 'Includes work item id and target_state in closed_items' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $closedIds = @($result.closed_items | ForEach-Object { $_.id })
            $closedIds | Should -Contain 200
            $closedIds | Should -Contain 100
            $result.closed_items[0].target_state | Should -Be 'Done'
        }

        It 'Skips items already in Done state' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $closedIds = @($result.closed_items | ForEach-Object { $_.id })
            $closedIds | Should -Not -Contain 201
        }

        It 'Calls twig state with target_state from validate result' {
            & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | Out-Null
            Should -Invoke twig -ParameterFilter { $args -contains 'state' } -Times 2
        }

        It 'Calls twig set before twig state for each item' {
            & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | Out-Null
            Should -Invoke twig -ParameterFilter { $args -contains 'set' } -Times 2
        }
    }

    Context 'Failed transitions' {

        It 'Records items in failed_closures when validate returns invalid' {
            Mock polyphony {
                '{"work_item_id":0,"event":"implementation_complete","is_valid":false,"target_state":"","message":"Not in InProgress category"}'
            } -ParameterFilter { $args -contains 'validate' }

            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.total_closed | Should -Be 0
            $result.total_failed | Should -Be 2
            $result.failed_closures.Count | Should -Be 2
        }

        It 'Includes reason from validate message in failed_closures' {
            Mock polyphony {
                '{"work_item_id":0,"event":"implementation_complete","is_valid":false,"target_state":"","message":"Not in InProgress category"}'
            } -ParameterFilter { $args -contains 'validate' }

            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.failed_closures[0].reason | Should -Be 'Not in InProgress category'
        }

        It 'Does not call twig state for invalid transitions' {
            Mock polyphony {
                '{"work_item_id":0,"event":"implementation_complete","is_valid":false,"target_state":"","message":"Invalid"}'
            } -ParameterFilter { $args -contains 'validate' }

            & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | Out-Null
            Should -Invoke twig -ParameterFilter { $args -contains 'state' } -Times 0
        }
    }

    Context 'Mixed valid and invalid transitions' {

        It 'Separates valid and invalid items correctly' {
            # First call (item 200) → valid, second call (item 100) → invalid
            $script:validateCallCount = 0
            Mock polyphony {
                $script:validateCallCount++
                if ($script:validateCallCount -eq 1) {
                    '{"work_item_id":200,"event":"implementation_complete","is_valid":true,"target_state":"Done","message":"OK"}'
                } else {
                    '{"work_item_id":100,"event":"implementation_complete","is_valid":false,"target_state":"","message":"Children not complete"}'
                }
            } -ParameterFilter { $args -contains 'validate' }

            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.total_closed | Should -Be 1
            $result.total_failed | Should -Be 1
        }
    }

    Context 'PG scoping' {

        It 'Only closes items in the specified PG' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-2' | ConvertFrom-Json
            $result.pg_name | Should -Be 'PG-2'
            $closedIds = @($result.closed_items | ForEach-Object { $_.id })
            $closedIds | Should -Contain 300
            $closedIds | Should -Contain 101
            $closedIds | Should -Not -Contain 200
            $closedIds | Should -Not -Contain 100
        }

        It 'Returns empty results for unknown PG' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-99' | ConvertFrom-Json
            $result.total_closed | Should -Be 0
            $result.total_failed | Should -Be 0
            $result.closed_items | Should -BeNullOrEmpty
            $result.failed_closures | Should -BeNullOrEmpty
        }
    }

    Context 'All items already Done' {

        It 'Returns zero closed and zero failed when all items are Done' {
            Mock polyphony {
                '{"work_item_id":42,"title":"Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue","type":"Issue","capabilities":["plannable"],"state":"Done","tags":"PG-1","children":[{"work_item_id":200,"title":"Task","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]}]}'
            } -ParameterFilter { $args -contains 'hierarchy' }

            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.total_closed | Should -Be 0
            $result.total_failed | Should -Be 0
            Should -Invoke polyphony -ParameterFilter { $args -contains 'validate' } -Times 0
        }
    }

    Context 'Calls polyphony validate with correct event' {

        It 'Passes implementation_complete as the event to polyphony validate' {
            & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | Out-Null
            Should -Invoke polyphony -ParameterFilter {
                $args -contains 'validate' -and $args -contains 'implementation_complete'
            } -Times 2
        }

        It 'Passes the correct work item id to polyphony validate' {
            & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | Out-Null
            Should -Invoke polyphony -ParameterFilter {
                $args -contains 'validate' -and $args -contains 200
            } -Times 1
        }
    }

    Context 'Output schema' {

        It 'Output contains all required fields' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Contain 'pg_name'
            $result.PSObject.Properties.Name | Should -Contain 'closed_items'
            $result.PSObject.Properties.Name | Should -Contain 'failed_closures'
            $result.PSObject.Properties.Name | Should -Contain 'total_closed'
            $result.PSObject.Properties.Name | Should -Contain 'total_failed'
        }

        It 'closed_items and failed_closures are arrays even when empty' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-99' | ConvertFrom-Json
            $raw = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-99'
            $raw | Should -Match '"closed_items":\s*\[\s*\]'
            $raw | Should -Match '"failed_closures":\s*\[\s*\]'
        }
    }

    Context 'Error handling' {

        It 'Returns error JSON when polyphony hierarchy fails' {
            Mock polyphony { throw 'hierarchy failed' } -ParameterFilter { $args -contains 'hierarchy' }

            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' 2>$null | ConvertFrom-Json
            $result.error | Should -Not -BeNullOrEmpty
        }
    }
}
