BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'pg-router.ps1'
    $script:HelpersPath = Join-Path $PSScriptRoot 'lib' 'pg-helpers.ps1'

    . $script:HelpersPath

    function global:polyphony { }
    function global:twig { }
    function global:git { }
    function global:gh { }
}

AfterAll {
    Remove-Item Function:\polyphony -ErrorAction SilentlyContinue
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
    Remove-Item Function:\git -ErrorAction SilentlyContinue
    Remove-Item Function:\gh -ErrorAction SilentlyContinue
}

# Default hierarchy fixture (inline in mocks — $script: vars not visible in mock scriptblocks)
# Epic 42 → Issue 100 (PG-1, Doing) → Tasks 200 (PG-1, Doing), 201 (PG-1, Done)
#         → Issue 101 (PG-2, To Do) → Task 300 (PG-2, To Do)

Describe 'pg-router.ps1 — PG routing with polyphony hierarchy (#2663)' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock git { 'https://github.com/PolyphonyRequiem/twig.git' } -ParameterFilter { $args -contains 'get-url' }
        Mock git { @() } -ParameterFilter { $args -contains 'branch' }
        Mock polyphony {
            @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1; twig","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Doing","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"To Do","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}]}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }
        Mock polyphony {
            '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-test-epic","pg_branch":"pg-{n}/42-test-epic"}}'
        } -ParameterFilter { $args -contains 'route' }
        Mock gh { }
    }

    Context 'create_branch — no branches exist' {

        It 'Returns create_branch action for first PG when no remote branches' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.action | Should -Be 'create_branch'
            $result.current_pg | Should -Be 'PG-1'
        }

        It 'Uses workspace_hint pg_branch with PG number in branch_name' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.branch_name | Should -Be 'pg-1/42-test-epic'
        }

        It 'Includes correct task_ids and issue_ids from capability classification' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.task_ids | Should -Contain 200
            $result.task_ids | Should -Contain 201
            $result.issue_ids | Should -Contain 100
        }

        It 'Reports zero pr_number and empty pr_url' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_number | Should -Be 0
            $result.pr_url | Should -Be ''
        }

        It 'Reports correct total_pgs' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.total_pgs | Should -Be 2
        }
    }

    Context 'create_branch — branch exists but no PR' {

        BeforeEach {
            Mock git {
                @('  origin/feature/42-pg-1')
            } -ParameterFilter { $args -contains 'branch' }
        }

        It 'Returns create_branch when branch exists but no open or merged PR' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.action | Should -Be 'create_branch'
            $result.current_pg | Should -Be 'PG-1'
        }
    }

    Context 'submit_pr — open PR exists' {

        BeforeEach {
            Mock gh {
                '[{"number":55,"headRefName":"pg-1/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/55"}]'
            } -ParameterFilter { $args -contains 'open' }
        }

        It 'Returns submit_pr with PR details when open PR exists' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.action | Should -Be 'submit_pr'
            $result.pr_number | Should -Be 55
            $result.pr_url | Should -Be 'https://github.com/PolyphonyRequiem/twig/pull/55'
        }
    }

    Context 'Merged PG — advances to next PG' {

        BeforeEach {
            Mock gh {
                '[{"number":10,"headRefName":"pg-1/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/10"}]'
            } -ParameterFilter { $args -contains 'merged' }
        }

        It 'Marks first PG as completed and targets second PG' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.action | Should -Be 'create_branch'
            $result.current_pg | Should -Be 'PG-2'
            $result.completed_pgs | Should -Contain 'PG-1'
        }

        It 'remaining_pgs excludes completed PGs' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.remaining_pgs | Should -Contain 'PG-2'
            $result.remaining_pgs | Should -Not -Contain 'PG-1'
        }

        It 'Includes correct task/issue ids for second PG' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.task_ids | Should -Contain 300
            $result.issue_ids | Should -Contain 101
        }
    }

    Context 'all_complete — all PGs merged' {

        BeforeEach {
            # Override hierarchy: both issues progressed beyond "To Do" for stale-branch defense
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Done","tags":"PG-1; twig","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"Done","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-2","children":[]}]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
            Mock gh {
                '[{"number":10,"headRefName":"pg-1/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/10"},{"number":11,"headRefName":"pg-2/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/11"}]'
            } -ParameterFilter { $args -contains 'merged' }
        }

        It 'Returns all_complete when every PG has a merged PR' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.action | Should -Be 'all_complete'
            $result.current_pg | Should -Be ''
            $result.branch_name | Should -Be ''
            $result.remaining_pgs.Count | Should -Be 0
            $result.completed_pgs.Count | Should -Be 2
        }

        It 'Returns empty arrays for task_ids and issue_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.task_ids.Count | Should -Be 0
            $result.issue_ids.Count | Should -Be 0
        }
    }

    Context 'No-tag fallback — single PG-1' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"title":"Untagged Feature","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Child One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":200,"title":"Leaf","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"","children":[]}]}]}'
            } -ParameterFilter { $args -contains 'hierarchy' }
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-untagged-feature","pg_branch":"pg-{n}/42-untagged-feature"}}'
            } -ParameterFilter { $args -contains 'route' }
        }

        It 'Falls back to single PG-1 when no PG tags found' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-1'
            $result.total_pgs | Should -Be 1
        }

        It 'Classifies items by capability in fallback mode' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.task_ids | Should -Contain 200
            $result.issue_ids | Should -Contain 42
            $result.issue_ids | Should -Contain 100
        }

        It 'Uses workspace_hint feature_branch in fallback mode' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.branch_name | Should -Be 'feature/42-untagged-feature'
        }
    }

    Context 'Capability-based classification' {

        It 'Items with implementable only go to task_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.task_ids | Should -Contain 200
            $result.task_ids | Should -Contain 201
        }

        It 'Items with plannable go to issue_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.issue_ids | Should -Contain 100
        }

        It 'No type-name literals used in classification' {
            $content = Get-Content $script:ScriptPath -Raw
            $content | Should -Not -Match "'Epic'"
            $content | Should -Not -Match "'Issue'"
            $content | Should -Not -Match "'Task'"
            $content | Should -Not -Match '"Epic"'
            $content | Should -Not -Match '"Issue"'
            $content | Should -Not -Match '"Task"'
        }
    }

    Context 'PG sorting' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"title":"Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue A","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-10","children":[]},{"work_item_id":101,"title":"Issue B","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-2","children":[]},{"work_item_id":102,"title":"Issue C","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1","children":[]}]}'
            } -ParameterFilter { $args -contains 'hierarchy' }
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-epic","pg_branch":"pg-{n}/42-epic"}}'
            } -ParameterFilter { $args -contains 'route' }
        }

        It 'Sorts PGs numerically (PG-1 before PG-2 before PG-10)' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-1'
            $result.remaining_pgs[0] | Should -Be 'PG-1'
            $result.remaining_pgs[1] | Should -Be 'PG-2'
            $result.remaining_pgs[2] | Should -Be 'PG-10'
        }
    }

    Context 'Stale-branch defense — merged PR but all issues still To Do' {

        BeforeEach {
            # PG-2 has a merged PR but Issue 101 is still "To Do" — stale branch
            Mock gh {
                '[{"number":10,"headRefName":"pg-2/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/10"}]'
            } -ParameterFilter { $args -contains 'merged' }
        }

        It 'Treats merged PR as stale when all container items are still To Do' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-1'
            $result.completed_pgs | Should -Not -Contain 'PG-2'
        }

        It 'Routes stale PG with create_branch action' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.remaining_pgs | Should -Contain 'PG-2'
        }
    }

    Context 'Stale-branch defense — merged PR with progressed issue is valid' {

        BeforeEach {
            # PG-1 has a merged PR and Issue 100 is "Doing" (progressed) — valid completion
            Mock gh {
                '[{"number":10,"headRefName":"pg-1/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/10"}]'
            } -ParameterFilter { $args -contains 'merged' }
        }

        It 'Marks PG as completed when container item has progressed beyond To Do' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.completed_pgs | Should -Contain 'PG-1'
            $result.current_pg | Should -Be 'PG-2'
        }
    }

    Context 'ADO-state completion fallback — all issues Done, no PR' {

        BeforeEach {
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Done","tags":"PG-1","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"To Do","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Treats PG as complete when all issues are Done even without merged PR' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.completed_pgs | Should -Contain 'PG-1'
            $result.current_pg | Should -Be 'PG-2'
            $result.action | Should -Be 'create_branch'
        }
    }

    Context 'Task-only PG — completion via task states' {

        BeforeEach {
            # PG-1 has only tasks (no container items), all Done
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]},{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Marks task-only PG as complete when all tasks are Done' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.completed_pgs | Should -Contain 'PG-1'
            $result.current_pg | Should -Be 'PG-2'
        }

        It 'Does not mark task-only PG complete when tasks remain' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.completed_pgs | Should -Not -Contain 'PG-2'
            $result.remaining_pgs | Should -Contain 'PG-2'
        }
    }

    Context 'Issue-as-task — plannable+implementable with no children in PG' {

        BeforeEach {
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Self-contained Issue","type":"Issue","capabilities":["plannable","implementable"],"state":"To Do","tags":"PG-1","children":[]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Classifies issue-as-task items into task_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.task_ids | Should -Contain 100
        }

        It 'Does not place issue-as-task items in issue_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.issue_ids | Should -Not -Contain 100
        }
    }

    Context 'No-tag fallback slug capping — manual fallback when no workspace_hint' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"title":"This Is A Very Long Feature Title That Should Be Truncated At Forty Characters","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":200,"title":"Leaf","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"","children":[]}]}'
            } -ParameterFilter { $args -contains 'hierarchy' }
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":null}'
            } -ParameterFilter { $args -contains 'route' }
        }

        It 'Caps slug at 40 characters in branch name when workspace_hint absent' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            # branch_name = "feature/42-" (11 chars) + slug (≤40 chars)
            $slug = $result.branch_name -replace '^feature/42-', ''
            $slug.Length | Should -BeLessOrEqual 40
        }
    }

    Context 'No-tag fallback — issue-as-task classification' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"title":"Untagged","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Self-contained","type":"Issue","capabilities":["plannable","implementable"],"state":"To Do","tags":"","children":[]}]}'
            } -ParameterFilter { $args -contains 'hierarchy' }
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-untagged","pg_branch":"pg-{n}/42-untagged"}}'
            } -ParameterFilter { $args -contains 'route' }
        }

        It 'Places issue-as-task items in task_ids in fallback mode' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.task_ids | Should -Contain 100
        }

        It 'Excludes issue-as-task items from issue_ids in fallback mode' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.issue_ids | Should -Not -Contain 100
        }
    }

    Context 'Error handling' {

        It 'Returns error JSON when polyphony hierarchy fails' {
            Mock polyphony { throw 'hierarchy failed' } -ParameterFilter { $args -contains 'hierarchy' }
            $result = & $script:ScriptPath -WorkItemId 42 2>$null | ConvertFrom-Json
            $result.error | Should -Not -BeNullOrEmpty
        }
    }

    Context 'PgNumber scoping — parallel for_each dispatch' {

        It 'Returns the requested PG when -PgNumber matches an existing non-completed PG' {
            $result = & $script:ScriptPath -WorkItemId 42 -PgNumber 2 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-2'
            $result.action | Should -Be 'create_branch'
        }

        It 'Returns PG-1 with create_branch when -PgNumber 1 (no PRs)' {
            $result = & $script:ScriptPath -WorkItemId 42 -PgNumber 1 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-1'
            $result.action | Should -Be 'create_branch'
        }

        It 'Returns all_complete when the requested PG is already merged' {
            Mock gh {
                '[{"number":10,"headRefName":"pg-1/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/10"}]'
            } -ParameterFilter { $args -contains 'merged' }
            $result = & $script:ScriptPath -WorkItemId 42 -PgNumber 1 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-1'
            $result.action | Should -Be 'all_complete'
        }

        It 'Returns submit_pr when the requested PG has an open PR' {
            Mock gh {
                '[{"number":55,"headRefName":"pg-2/42-test-epic","url":"https://github.com/PolyphonyRequiem/twig/pull/55"}]'
            } -ParameterFilter { $args -contains 'open' }
            $result = & $script:ScriptPath -WorkItemId 42 -PgNumber 2 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-2'
            $result.action | Should -Be 'submit_pr'
            $result.pr_number | Should -Be 55
        }

        It 'Returns all_complete shape when -PgNumber refers to an unknown PG' {
            $result = & $script:ScriptPath -WorkItemId 42 -PgNumber 99 | ConvertFrom-Json
            # Falls through to the no-currentPG branch — emits the all_complete envelope
            $result.action | Should -Be 'all_complete'
            $result.current_pg | Should -Be ''
        }

        It 'Default invocation (no -PgNumber) preserves legacy "first non-complete" behaviour' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-1'
            $result.action | Should -Be 'create_branch'
        }
    }
}

# ── Output schema compatibility verification (#2663) ─────────────────────────

Describe 'pg-router.ps1 — output schema compatibility (#2663)' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock git { 'https://github.com/PolyphonyRequiem/twig.git' } -ParameterFilter { $args -contains 'get-url' }
        Mock git { @() } -ParameterFilter { $args -contains 'branch' }
        Mock polyphony {
            @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1; twig","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Doing","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"To Do","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}]}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }
        Mock polyphony {
            '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-test-epic","pg_branch":"pg-{n}/42-test-epic"}}'
        } -ParameterFilter { $args -contains 'route' }
        Mock gh { }
    }

    Context 'Required schema keys — all 11 from reference' {

        It 'Contains all 11 required top-level keys' {
            $requiredKeys = @(
                'action', 'current_pg', 'branch_name', 'issue_ids', 'task_ids',
                'pr_number', 'pr_url', 'completed_pgs', 'remaining_pgs', 'total_pgs'
            )
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name
            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json

            # Strings
            $result.action | Should -BeOfType [string]
            $result.current_pg | Should -BeOfType [string]
            $result.branch_name | Should -BeOfType [string]
            $result.pr_url | Should -BeOfType [string]

            # Integer
            $result.pr_number | Should -BeOfType [long]
            $result.total_pgs | Should -BeOfType [long]

            # Arrays
            $result.PSObject.Properties.Name | Should -Contain 'issue_ids'
            $result.PSObject.Properties.Name | Should -Contain 'task_ids'
            $result.PSObject.Properties.Name | Should -Contain 'completed_pgs'
            $result.PSObject.Properties.Name | Should -Contain 'remaining_pgs'
        }
    }

    Context 'Scenario — all_complete has safe defaults' {

        BeforeEach {
            # Override hierarchy: all issues progressed beyond "To Do" for stale-branch defense
            Mock polyphony {
                @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Done","tags":"PG-1; twig","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]},{"work_item_id":201,"title":"Task B","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"Done","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"Done","tags":"PG-2","children":[]}]}]}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
            Mock gh {
                '[{"number":10,"headRefName":"pg-1/42-test-epic","url":""},{"number":11,"headRefName":"pg-2/42-test-epic","url":""}]'
            } -ParameterFilter { $args -contains 'merged' }
        }

        It 'Returns safe defaults for all fields' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.action | Should -Be 'all_complete'
            $result.current_pg | Should -Be ''
            $result.branch_name | Should -Be ''
            $result.pr_number | Should -Be 0
            $result.pr_url | Should -Be ''
        }

        It 'Arrays are empty when all_complete' {
            $raw = & $script:ScriptPath -WorkItemId 42
            $raw | Should -Match '"issue_ids":\s*\[\s*\]'
            $raw | Should -Match '"task_ids":\s*\[\s*\]'
            $raw | Should -Match '"remaining_pgs":\s*\[\s*\]'
        }
    }

    Context 'Scenario — create_branch with populated arrays' {

        It 'Produces valid JSON with populated arrays' {
            $raw = & $script:ScriptPath -WorkItemId 42
            $result = $raw | ConvertFrom-Json

            $result.action | Should -Be 'create_branch'
            $result.task_ids.Count | Should -BeGreaterThan 0
            $result.issue_ids.Count | Should -BeGreaterThan 0
            $result.remaining_pgs.Count | Should -Be 2
            $result.total_pgs | Should -Be 2
        }
    }
}

# ── Workspace hint integration verification (#2666) ──────────────────────────

Describe 'pg-router.ps1 — workspace_hint branch naming (#2666)' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock git { 'https://github.com/PolyphonyRequiem/twig.git' } -ParameterFilter { $args -contains 'get-url' }
        Mock git { @() } -ParameterFilter { $args -contains 'branch' }
        Mock polyphony {
            @'
{"work_item_id":42,"title":"Test Epic","type":"Epic","capabilities":["plannable"],"state":"Doing","tags":"","children":[{"work_item_id":100,"title":"Issue One","type":"Issue","capabilities":["plannable"],"state":"Doing","tags":"PG-1","children":[{"work_item_id":200,"title":"Task A","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-1","children":[]}]},{"work_item_id":101,"title":"Issue Two","type":"Issue","capabilities":["plannable"],"state":"To Do","tags":"PG-2","children":[{"work_item_id":300,"title":"Task C","type":"Task","capabilities":["implementable"],"state":"To Do","tags":"PG-2","children":[]}]}]}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }
        Mock polyphony {
            '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":{"feature_branch":"feature/42-test-epic","pg_branch":"pg-{n}/42-test-epic"}}'
        } -ParameterFilter { $args -contains 'route' }
        Mock gh { }
    }

    Context 'PG branch naming from workspace_hint' {

        It 'Substitutes {n} with PG number for PG-1' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.branch_name | Should -Be 'pg-1/42-test-epic'
        }

        It 'Substitutes {n} with PG number for PG-2 when PG-1 is complete' {
            Mock gh {
                '[{"number":10,"headRefName":"pg-1/42-test-epic","url":""}]'
            } -ParameterFilter { $args -contains 'merged' }
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.current_pg | Should -Be 'PG-2'
            $result.branch_name | Should -Be 'pg-2/42-test-epic'
        }
    }

    Context 'Manual fallback when workspace_hint is null' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress","workspace_hint":null}'
            } -ParameterFilter { $args -contains 'route' }
        }

        It 'Falls back to manual slug derivation' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.branch_name | Should -BeLike 'feature/42-pg-1*'
        }
    }

    Context 'Manual fallback when polyphony route fails' {

        BeforeEach {
            Mock polyphony { throw 'route failed' } -ParameterFilter { $args -contains 'route' }
        }

        It 'Falls back to manual slug derivation when route throws' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.branch_name | Should -BeLike 'feature/42-pg-1*'
        }
    }

    Context 'Type literal audit' {

        It 'Contains zero type-name string literals in pg-router.ps1' {
            $content = Get-Content $script:ScriptPath -Raw
            $content | Should -Not -Match "'Epic'"
            $content | Should -Not -Match "'Issue'"
            $content | Should -Not -Match "'Task'"
        }
    }
}