BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'task-router.ps1'
    $script:HelpersPath = Join-Path $PSScriptRoot 'lib' 'pg-helpers.ps1'

    . $script:HelpersPath

    function global:polyphony { }
    function global:twig { }
    function global:git { }
}

AfterAll {
    Remove-Item Function:\polyphony -ErrorAction SilentlyContinue
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
    Remove-Item Function:\git -ErrorAction SilentlyContinue
}

# Default hierarchy fixture:
# Epic 42 → Issue 100 (PG-1, Doing) → Tasks 200 (PG-1, To Do), 201 (PG-1, Done)
#         → Issue 101 (PG-2, To Do) → Task 300 (PG-2, To Do)

Describe 'task-router.ps1 — task routing with polyphony hierarchy (#2664)' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock twig { } -ParameterFilter { $args -contains 'set' }
        Mock twig { } -ParameterFilter { $args -contains 'state' }
        Mock git { 'pg-1/42-test-epic' } -ParameterFilter { $args -contains '--show-current' }
        Mock polyphony {
            @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1; twig","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"To Do","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}]}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }
        Mock polyphony {
            '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-test-epic","pg_branch":"pg-{n}/42-test-epic"}}'
        } -ParameterFilter { $args -contains 'route' }
    }

    Context 'implement_task — selects first non-Done implementable item' {

        It 'Returns implement_task action for the first non-Done item in PG-1' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.action | Should -Be 'implement_task'
            $result.task_id | Should -Be 200
            $result.task_title | Should -Be 'Task A'
        }

        It 'Reports correct remaining_count (non-Done items)' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.remaining_count | Should -Be 1
        }

        It 'Returns correct current_pg' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-1'
        }

        It 'Transitions the selected item to Doing via twig' {
            & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | Out-Null
            Should -Invoke twig -ParameterFilter { $args -contains 'set' -and $args -contains 200 }
            Should -Invoke twig -ParameterFilter { $args -contains 'state' -and $args -contains 'Doing' }
        }
    }

    Context 'Plannable ancestor — issue_id and issue_title derivation' {

        It 'Derives issue_id from nearest plannable ancestor' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.issue_id | Should -Be 100
            $result.issue_title | Should -Be 'Issue One'
        }

        It 'Derives issue from PG-2 ancestor' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-2' | ConvertFrom-Json
            $result.issue_id | Should -Be 101
            $result.issue_title | Should -Be 'Issue Two'
        }
    }

    Context 'Branch name derivation' {

        It 'Uses current branch when it matches expected workspace_hint branch' {
            Mock git { 'pg-1/42-test-epic' } -ParameterFilter { $args -contains '--show-current' }
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.branch_name | Should -Be 'pg-1/42-test-epic'
        }

        It 'Falls back to workspace_hint branch when current branch does not match' {
            Mock git { 'main' } -ParameterFilter { $args -contains '--show-current' }
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.branch_name | Should -Be 'pg-1/42-test-epic'
        }
    }

    Context 'all_tasks_done — all implementable items in PG are Done' {

        BeforeEach {
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Returns all_tasks_done when every implementable item is Done' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.action | Should -Be 'all_tasks_done'
            $result.remaining_count | Should -Be 0
        }

        It 'Does not invoke twig set or twig state' {
            & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | Out-Null
            Should -Not -Invoke twig -ParameterFilter { $args -contains 'set' }
            Should -Not -Invoke twig -ParameterFilter { $args -contains 'state' }
        }

        It 'Returns safe defaults for task/issue fields' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.task_id | Should -Be 0
            $result.task_title | Should -Be ''
            $result.issue_id | Should -Be 0
            $result.issue_title | Should -Be ''
            $result.branch_name | Should -Be ''
        }
    }

    Context 'Fallback 1 — implementable children under PG-tagged containers' {

        BeforeEach {
            # Tasks have no PG tag, but their parent container does
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"","children":[]}]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Finds implementable items via parent container PG tag' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.action | Should -Be 'implement_task'
            $result.task_id | Should -Be 200
        }
    }

    Context 'Fallback 2 — issue-as-task (plannable+implementable, no children)' {

        BeforeEach {
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Self-contained Issue","type":"Issue","capabilities":["plannable","implementable"],"state":"To Do","tags":"PG-1","children":[]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Treats plannable+implementable items with no children as tasks' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.action | Should -Be 'implement_task'
            $result.task_id | Should -Be 100
        }
    }

    Context 'Final fallback — all implementable items when no PG match' {

        BeforeEach {
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"","children":[]}]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Falls back to all implementable items when no PG tag matches' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-99' | ConvertFrom-Json
            $result.action | Should -Be 'implement_task'
            $result.task_id | Should -Be 200
        }
    }

    Context 'No type-name literals' {

        It 'Does not contain type-name string literals' {
            $content = Get-Content $script:ScriptPath -Raw
            $content | Should -Not -Match "'Epic'"
            $content | Should -Not -Match "'Issue'"
            $content | Should -Not -Match "'Task'"
            $content | Should -Not -Match '"Epic"'
            $content | Should -Not -Match '"Issue"'
            $content | Should -Not -Match '"Task"'
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

# ── Output schema compatibility verification (#2664) ─────────────────────────

Describe 'task-router.ps1 — output schema compatibility (#2664)' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock twig { } -ParameterFilter { $args -contains 'set' }
        Mock twig { } -ParameterFilter { $args -contains 'state' }
        Mock git { 'pg-1/42-test-epic' } -ParameterFilter { $args -contains '--show-current' }
        Mock polyphony {
            @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1; twig","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"To Do","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}]}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }
        Mock polyphony {
            '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-test-epic","pg_branch":"pg-{n}/42-test-epic"}}'
        } -ParameterFilter { $args -contains 'route' }
    }

    Context 'Required schema keys — all 8 from reference' {

        It 'Contains all 8 required top-level keys' {
            $requiredKeys = @(
                'action', 'task_id', 'task_title', 'issue_id', 'issue_title',
                'remaining_count', 'current_pg', 'branch_name'
            )
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name
            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json

            # Strings
            $result.action | Should -BeOfType [string]
            $result.task_title | Should -BeOfType [string]
            $result.issue_title | Should -BeOfType [string]
            $result.current_pg | Should -BeOfType [string]
            $result.branch_name | Should -BeOfType [string]

            # Integers
            $result.task_id | Should -BeOfType [long]
            $result.issue_id | Should -BeOfType [long]
            $result.remaining_count | Should -BeOfType [long]
        }
    }

    Context 'Scenario — implement_task with populated fields' {

        It 'Produces valid JSON with all fields populated' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json

            $result.action | Should -Be 'implement_task'
            $result.task_id | Should -BeGreaterThan 0
            $result.task_title | Should -Not -BeNullOrEmpty
            $result.issue_id | Should -BeGreaterThan 0
            $result.issue_title | Should -Not -BeNullOrEmpty
            $result.remaining_count | Should -BeGreaterThan 0
            $result.current_pg | Should -Be 'PG-1'
            $result.branch_name | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Scenario — all_tasks_done has safe defaults' {

        BeforeEach {
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Done","tags":"PG-1","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Returns safe defaults for all fields' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.action | Should -Be 'all_tasks_done'
            $result.task_id | Should -Be 0
            $result.task_title | Should -Be ''
            $result.issue_id | Should -Be 0
            $result.issue_title | Should -Be ''
            $result.remaining_count | Should -Be 0
            $result.branch_name | Should -Be ''
        }
    }
}

# ── Workspace hint integration verification (#2666) ──────────────────────────

Describe 'task-router.ps1 — workspace_hint branch naming (#2666)' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock twig { } -ParameterFilter { $args -contains 'set' }
        Mock twig { } -ParameterFilter { $args -contains 'state' }
        Mock git { 'main' } -ParameterFilter { $args -contains '--show-current' }
        Mock polyphony {
            @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-1","children":[]}]}]}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }
        Mock polyphony {
            '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-test-epic","pg_branch":"pg-{n}/42-test-epic"}}'
        } -ParameterFilter { $args -contains 'route' }
    }

    Context 'Uses workspace_hint pg_branch with {n} substitution' {

        It 'Derives branch from pg_branch template for PG-1' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.branch_name | Should -Be 'pg-1/42-test-epic'
        }

        It 'Derives branch from pg_branch template for PG-3' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-3' | ConvertFrom-Json
            $result.branch_name | Should -Be 'pg-3/42-test-epic'
        }

        It 'Prefers current branch when it matches expected workspace_hint branch' {
            Mock git { 'pg-1/42-test-epic' } -ParameterFilter { $args -contains '--show-current' }
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.branch_name | Should -Be 'pg-1/42-test-epic'
        }
    }

    Context 'Manual fallback when workspace_hint is null' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":null}'
            } -ParameterFilter { $args -contains 'route' }
        }

        It 'Falls back to manual slug when no workspace_hint' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.branch_name | Should -BeLike 'feature/42-pg-1*'
        }
    }

    Context 'Manual fallback when polyphony route fails' {

        BeforeEach {
            Mock polyphony { throw 'route failed' } -ParameterFilter { $args -contains 'route' }
        }

        It 'Falls back to manual slug when route throws' {
            $result = & $script:ScriptPath -WorkItemId 42 -PGName 'PG-1' | ConvertFrom-Json
            $result.branch_name | Should -BeLike 'feature/42-pg-1*'
        }
    }

    Context 'Type literal audit' {

        It 'Contains zero type-name string literals in task-router.ps1' {
            $content = Get-Content $script:ScriptPath -Raw
            $content | Should -Not -Match "'Epic'"
            $content | Should -Not -Match "'Issue'"
            $content | Should -Not -Match "'Task'"
        }
    }
}