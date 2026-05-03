BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'detect-state.ps1'

    # Define placeholder functions for external commands so Pester can mock them.
    # PowerShell functions take precedence over external executables.
    function global:polyphony { }
    function global:twig { }
    function global:gh { }
    function global:git { }
}

AfterAll {
    Remove-Item Function:\polyphony -ErrorAction SilentlyContinue
    Remove-Item Function:\twig -ErrorAction SilentlyContinue
    Remove-Item Function:\gh -ErrorAction SilentlyContinue
    Remove-Item Function:\git -ErrorAction SilentlyContinue
}

Describe 'detect-state.ps1 — polyphony route integration (#2632)' {

    BeforeEach {
        # Default mocks for external commands
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock twig { } -ParameterFilter { $args -contains 'set' }
        Mock twig { } -ParameterFilter { $args -contains 'state' }

        # Default polyphony route mock
        Mock polyphony {
            '{"work_item_id":42,"phase":"needs_planning","action":"plan","message":"Needs planning.","workspace_hint":{"feature_branch":"feature/42-test","pg_branch":"feature/42-pg-1"}}'
        } -ParameterFilter { $args -contains 'route' }

        # Default polyphony validate mock
        Mock polyphony {
            '{"is_valid":false,"target_state":"","message":"Not valid"}'
        } -ParameterFilter { $args -contains 'validate' }

        # Default twig tree mock — epic with 2 children, 1 has grandchildren
        Mock twig {
            '{"id":42,"type":"Epic","state":"Doing","title":"Test Epic","children":[{"id":100,"type":"Issue","state":"Done","title":"Child 1","children":[{"id":200,"type":"Task","state":"Done","title":"Grandchild"}]},{"id":101,"type":"Issue","state":"To Do","title":"Child 2","children":[]}]}'
        } -ParameterFilter { $args -contains 'tree' }

        # Default git mocks
        Mock git { 'https://github.com/TestOrg/TestRepo.git' } -ParameterFilter { $args -contains 'get-url' }
        Mock git { } -ParameterFilter { $args -contains 'ls-remote' }

        # Default gh mock
        Mock gh { '[]' }
    }

    Context 'Polyphony route phase mapping' {

        It 'Sets phase from polyphony route result' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.phase | Should -Be 'needs_planning'
        }

        It 'Maps phase "in_progress" from route' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"In progress."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.phase | Should -Be 'in_progress'
        }

        It 'Maps phase "done" from route' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"done","action":"none","message":"Complete."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.phase | Should -Be 'done'
        }

        It 'Maps phase "ready_for_completion" from route' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"ready_for_completion","action":"close","message":"Ready."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.phase | Should -Be 'ready_for_completion'
        }
    }

    Context 'Implementation status mapping from action' {

        It 'Maps "plan" action to "not_started"' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_planning","action":"plan","message":"Plan."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'not_started'
        }

        It 'Maps "seed" action to "not_started"' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_seeding","action":"seed","message":"Seed."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'not_started'
        }

        It 'Maps "implement" action to "not_started"' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"ready_for_implementation","action":"implement","message":"Implement."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'not_started'
        }

        It 'Maps "monitor" action to "in_progress"' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"Monitor."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'in_progress'
        }

        It 'Maps "close" action to "done"' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"ready_for_completion","action":"close","message":"Close."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'done'
        }

        It 'Maps "none" action with "done" phase to "done"' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"done","action":"none","message":"Done."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'done'
        }

        It 'Maps "none" action with "removed" phase to "removed"' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"removed","action":"none","message":"Removed."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'removed'
        }

        It 'Falls through unknown action as literal value' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"unknown","action":"custom_action","message":"Custom."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'custom_action'
        }
    }

    Context 'Work item metadata from twig tree' {

        It 'Reads work_item_type from tree' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_item_type | Should -Be 'Epic'
        }

        It 'Reads work_item_state from tree' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_item_state | Should -Be 'Doing'
        }

        It 'Reads work_item_title from tree' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_item_title | Should -Be 'Test Epic'
        }

        It 'Defaults to empty string when tree fields are missing' {
            Mock twig {
                '{"id":42,"children":[]}'
            } -ParameterFilter { $args -contains 'tree' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_item_type | Should -Be ''
            $result.work_item_state | Should -Be ''
            $result.work_item_title | Should -Be ''
        }
    }

    Context 'Children analysis' {

        It 'Computes children_summary with correct counts' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $summary = $result.children_summary | ConvertFrom-Json
            $summary.total | Should -Be 2
            $summary.done | Should -Be 1
            $summary.doing | Should -Be 0
            $summary.todo | Should -Be 1
        }

        It 'Sets has_seeded_children true when children exist' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.has_seeded_children | Should -BeTrue
        }

        It 'Sets has_seeded_children false when no children' {
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Empty","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.has_seeded_children | Should -BeFalse
        }

        It 'Detects any_child_missing_tasks when non-Done child has no grandchildren' {
            # Default mock: child 101 is "To Do" with empty children array
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.any_child_missing_tasks | Should -BeTrue
        }

        It 'Sets any_child_missing_tasks false when all non-Done children have grandchildren' {
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Full","children":[{"id":100,"type":"Issue","state":"Doing","title":"C1","children":[{"id":200,"type":"Task","state":"To Do","title":"GC1"}]},{"id":101,"type":"Issue","state":"Doing","title":"C2","children":[{"id":201,"type":"Task","state":"To Do","title":"GC2"}]}]}'
            } -ParameterFilter { $args -contains 'tree' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.any_child_missing_tasks | Should -BeFalse
        }

        It 'Ignores Done children in missing-tasks check' {
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Test","children":[{"id":100,"type":"Issue","state":"Done","title":"DoneChild","children":[]}]}'
            } -ParameterFilter { $args -contains 'tree' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.any_child_missing_tasks | Should -BeFalse
        }
    }

    Context 'Seed status' {

        It 'Returns "unseeded" when no children' {
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Empty","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.seed_status | Should -Be 'unseeded'
        }

        It 'Returns "partial" when some children missing tasks' {
            # Default mock has child 101 with no grandchildren
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.seed_status | Should -Be 'partial'
        }

        It 'Returns "seeded" when all children have grandchildren' {
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Full","children":[{"id":100,"type":"Issue","state":"Doing","title":"C1","children":[{"id":200,"type":"Task","state":"To Do","title":"GC1"}]}]}'
            } -ParameterFilter { $args -contains 'tree' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.seed_status | Should -Be 'seeded'
        }
    }

    Context 'Workspace hint pass-through' {

        It 'Passes workspace_hint as a nested object in output' {
            # Nested object lets workflow YAMLs use
            # `{{ state_detector.output.workspace_hint.feature_branch }}`
            # directly without parsing a stringified JSON value.
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.workspace_hint.feature_branch | Should -Be 'feature/42-test'
            $result.workspace_hint.pg_branch | Should -Be 'feature/42-pg-1'
        }

        It 'Returns empty object when workspace_hint is null' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"done","action":"none","message":"Done."}'
            } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            # workspace_hint must remain present in the output for downstream
            # `state_detector.output.workspace_hint.feature_branch | default('')`
            # patterns to evaluate without StrictUndefined errors.
            $result.PSObject.Properties.Name | Should -Contain 'workspace_hint'
            # Empty object — feature_branch lookup yields $null, not a string.
            $result.workspace_hint.feature_branch | Should -BeNullOrEmpty
            $result.workspace_hint | Should -Not -BeOfType [string]
        }
    }

    Context 'Repo slug derivation' {

        It 'Derives repo slug from HTTPS remote URL' {
            Mock git { 'https://github.com/TestOrg/TestRepo.git' } -ParameterFilter { $args -contains 'get-url' }

            # Should not throw — slug is derived, not hardcoded
            { & $script:ScriptPath -WorkItemId 42 | Out-Null } | Should -Not -Throw
        }

        It 'Derives repo slug from SSH remote URL' {
            Mock git { 'git@github.com:TestOrg/TestRepo.git' } -ParameterFilter { $args -contains 'get-url' }

            { & $script:ScriptPath -WorkItemId 42 | Out-Null } | Should -Not -Throw
        }

        It 'Contains no hardcoded repo slug in script source' {
            $content = Get-Content $script:ScriptPath -Raw
            $content | Should -Not -Match 'PolyphonyRequiem/twig'
        }
    }

    Context 'Unmerged branches check' {

        It 'Keeps implementation_status as "in_progress" when open PRs exist' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"in_progress","action":"monitor","message":"Monitor.","workspace_hint":{"feature_branch":"feature/42-test","pg_branch":"feature/42-pg-1"}}'
            } -ParameterFilter { $args -contains 'route' }

            Mock git { 'abc123 refs/heads/feature/42-test' } -ParameterFilter { $args -contains 'ls-remote' }
            Mock gh { '[{"number":1}]' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'in_progress'
        }

        It 'Does not override "done" implementation_status even with open PRs' {
            Mock polyphony {
                '{"work_item_id":42,"phase":"ready_for_completion","action":"close","message":"Close.","workspace_hint":{"feature_branch":"feature/42-test","pg_branch":"feature/42-pg-1"}}'
            } -ParameterFilter { $args -contains 'route' }

            Mock git { 'abc123 refs/heads/feature/42-test' } -ParameterFilter { $args -contains 'ls-remote' }
            Mock gh { '[{"number":1}]' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'done'
        }
    }

    Context 'Output completeness' {

        It 'Includes all required output fields' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Contain 'work_item_id'
            $result.PSObject.Properties.Name | Should -Contain 'work_item_type'
            $result.PSObject.Properties.Name | Should -Contain 'work_item_state'
            $result.PSObject.Properties.Name | Should -Contain 'work_item_title'
            $result.PSObject.Properties.Name | Should -Contain 'intent'
            $result.PSObject.Properties.Name | Should -Contain 'phase'
            $result.PSObject.Properties.Name | Should -Contain 'has_plan'
            $result.PSObject.Properties.Name | Should -Contain 'plan_status'
            $result.PSObject.Properties.Name | Should -Contain 'plan_path'
            $result.PSObject.Properties.Name | Should -Contain 'plan_source'
            $result.PSObject.Properties.Name | Should -Contain 'has_seeded_children'
            $result.PSObject.Properties.Name | Should -Contain 'any_child_missing_tasks'
            $result.PSObject.Properties.Name | Should -Contain 'seed_status'
            $result.PSObject.Properties.Name | Should -Contain 'children_summary'
            $result.PSObject.Properties.Name | Should -Contain 'implementation_status'
            $result.PSObject.Properties.Name | Should -Contain 'workspace_hint'
            $result.PSObject.Properties.Name | Should -Contain 'intent_conflict'
            $result.PSObject.Properties.Name | Should -Contain 'needs_cleanup'
            $result.PSObject.Properties.Name | Should -Contain 'error'
        }

        It 'Outputs valid JSON' {
            $raw = & $script:ScriptPath -WorkItemId 42
            { $raw | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'Sets work_item_id to input parameter' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_item_id | Should -Be 42
        }
    }

    Context 'Plan discovery — explicit override (#2633)' {

        It 'Sets plan_status complete when -PlanPath file exists' {
            Mock Test-Path { $true } -ParameterFilter { $Path -like '*my-plan.md' }
            Mock Resolve-Path { [PSCustomObject]@{ Path = 'C:\plans\my-plan.md' } } -ParameterFilter { $Path -like '*my-plan.md' }

            $result = & $script:ScriptPath -WorkItemId 42 -PlanPath 'C:\plans\my-plan.md' | ConvertFrom-Json
            $result.plan_status | Should -Be 'complete'
            $result.plan_source | Should -Be 'explicit_override'
            $result.plan_path | Should -Be 'C:\plans\my-plan.md'
            $result.has_plan | Should -BeTrue
        }

        It 'Returns plan_status none when -PlanPath file does not exist' {
            Mock Test-Path { $false } -ParameterFilter { $Path -like '*missing.md' }

            $result = & $script:ScriptPath -WorkItemId 42 -PlanPath 'C:\plans\missing.md' | ConvertFrom-Json
            $result.plan_status | Should -Be 'none'
            $result.plan_source | Should -Be 'none'
            $result.plan_path | Should -Be ''
            $result.has_plan | Should -BeFalse
        }

        It 'Does not scan filesystem when explicit PlanPath is provided' {
            Mock Test-Path { $true } -ParameterFilter { $Path -like '*my-plan.md' }
            Mock Resolve-Path { [PSCustomObject]@{ Path = 'C:\plans\my-plan.md' } } -ParameterFilter { $Path -like '*my-plan.md' }
            Mock Get-ChildItem { throw 'Should not be called' } -ParameterFilter { $Path -like '*plan.md' }

            { & $script:ScriptPath -WorkItemId 42 -PlanPath 'C:\plans\my-plan.md' | Out-Null } | Should -Not -Throw
        }
    }

    Context 'Plan discovery — YAML frontmatter (#2633)' {

        It 'Discovers plan via work_item_id in YAML frontmatter' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\test.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "---`nwork_item_id: 42`n---`n# Plan content"
            }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'complete'
            $result.plan_source | Should -Be 'filesystem_fallback'
            $result.plan_path | Should -Be 'C:\repo\docs\projects\test.plan.md'
            $result.has_plan | Should -BeTrue
        }

        It 'Does not match when work_item_id differs' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\other.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "---`nwork_item_id: 999`n---`n# Other plan"
            }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'none'
            $result.has_plan | Should -BeFalse
        }
    }

    Context 'Plan discovery — legacy table metadata (#2633)' {

        It 'Discovers plan via Work Item table row' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\legacy.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "# Plan`n| **Work Item** | #42 |`n| Status | Active |"
            }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'complete'
            $result.plan_source | Should -Be 'filesystem_fallback'
            $result.plan_path | Should -Be 'C:\repo\docs\projects\legacy.plan.md'
            $result.has_plan | Should -BeTrue
        }

        It 'Discovers plan via Issue table row' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\issue.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "# Plan`n| **Issue** | #42 |`n| Priority | High |"
            }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'complete'
            $result.plan_source | Should -Be 'filesystem_fallback'
            $result.has_plan | Should -BeTrue
        }

        It 'Matches unbolded Work Item row' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\plain.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "# Plan`n| Work Item | #42 |`n| Status | Active |"
            }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'complete'
            $result.plan_source | Should -Be 'filesystem_fallback'
        }
    }

    Context 'Plan discovery — ambiguous and missing (#2633)' {

        It 'Sets plan_status ambiguous when multiple plan files match' {
            Mock Get-ChildItem {
                @(
                    [PSCustomObject]@{ FullName = 'C:\repo\docs\projects\plan-a.plan.md' },
                    [PSCustomObject]@{ FullName = 'C:\repo\docs\projects\plan-b.plan.md' }
                )
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "---`nwork_item_id: 42`n---`n# Plan"
            }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'ambiguous'
            $result.has_plan | Should -BeFalse
            $result.plan_path | Should -Be ''
        }

        It 'Returns plan_status none when no plan files exist' {
            Mock Get-ChildItem { @() } -ParameterFilter { $Path -like '*plan.md' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'none'
            $result.plan_source | Should -Be 'none'
            $result.has_plan | Should -BeFalse
        }

        It 'Returns plan_status none when plan files exist but none match' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\other.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "# Plan with no metadata"
            }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'none'
            $result.has_plan | Should -BeFalse
        }
    }

    Context 'Plan discovery — intent integration (#2633)' {

        It 'Sets intent_conflict true for new intent when has_plan is true' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\test.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "---`nwork_item_id: 42`n---`n# Plan"
            }

            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.has_plan | Should -BeTrue
            $result.intent_conflict | Should -BeTrue
        }

        It 'Sets needs_cleanup true for redo intent when has_plan is true' {
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\test.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }

            Mock Get-Content {
                "---`nwork_item_id: 42`n---`n# Plan"
            }

            # Use a tree with no children so only hasPlan triggers
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Test","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }

            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'redo' | ConvertFrom-Json
            $result.has_plan | Should -BeTrue
            $result.needs_cleanup | Should -BeTrue
        }
    }

    Context 'Zero type name literals' {

        It 'Does not contain hardcoded type name literals' {
            $content = Get-Content $script:ScriptPath -Raw
            # Must not contain type checks like -eq 'Epic' or -eq 'Issue'
            $content | Should -Not -Match "-eq\s+'Epic'"
            $content | Should -Not -Match "-eq\s+'Issue'"
            $content | Should -Not -Match "-eq\s+'Task'"
            $content | Should -Not -Match "-eq\s+'User Story'"
        }
    }

    Context 'Error handling' {

        It 'Returns error JSON when polyphony route fails' {
            Mock polyphony { throw 'Route command failed' } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 2>$null
            $LASTEXITCODE | Should -Be 1
            $json = $result | ConvertFrom-Json
            $json.error | Should -Not -BeNullOrEmpty
            $json.phase | Should -Be 'error'
        }
    }
}

Describe 'detect-state.ps1 — output schema compatibility (#2635)' {

    BeforeEach {
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock twig { } -ParameterFilter { $args -contains 'set' }
        Mock twig { } -ParameterFilter { $args -contains 'state' }

        Mock polyphony {
            '{"is_valid":false,"target_state":"","message":"Not valid"}'
        } -ParameterFilter { $args -contains 'validate' }

        Mock git { 'https://github.com/TestOrg/TestRepo.git' } -ParameterFilter { $args -contains 'get-url' }
        Mock git { } -ParameterFilter { $args -contains 'ls-remote' }
        Mock gh { '[]' }
        Mock Get-ChildItem { @() } -ParameterFilter { $Path -like '*plan.md' }
    }

    Context 'Required schema keys — all 18 from reference' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_planning","action":"plan","message":"Plan."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Test","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }
        }

        It 'Contains all 18 required keys' {
            $requiredKeys = @(
                'work_item_id', 'work_item_type', 'work_item_state', 'work_item_title',
                'intent', 'phase', 'has_plan', 'plan_status', 'plan_path', 'plan_source',
                'has_seeded_children', 'any_child_missing_tasks', 'seed_status',
                'children_summary', 'implementation_status', 'intent_conflict',
                'needs_cleanup', 'error'
            )

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name

            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json

            # Integer
            $result.work_item_id | Should -BeOfType [long]

            # Strings
            $result.work_item_type | Should -BeOfType [string]
            $result.work_item_state | Should -BeOfType [string]
            $result.work_item_title | Should -BeOfType [string]
            $result.intent | Should -BeOfType [string]
            $result.phase | Should -BeOfType [string]
            $result.plan_status | Should -BeOfType [string]
            $result.plan_path | Should -BeOfType [string]
            $result.plan_source | Should -BeOfType [string]
            $result.seed_status | Should -BeOfType [string]
            $result.children_summary | Should -BeOfType [string]
            $result.implementation_status | Should -BeOfType [string]
            $result.error | Should -BeOfType [string]

            # Booleans
            $result.has_plan | Should -BeOfType [bool]
            $result.has_seeded_children | Should -BeOfType [bool]
            $result.any_child_missing_tasks | Should -BeOfType [bool]
            $result.intent_conflict | Should -BeOfType [bool]
            $result.needs_cleanup | Should -BeOfType [bool]
        }
    }

    Context 'Scenario 1 — New work item (no children, no plan, intent=new)' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_planning","action":"plan","message":"Needs planning."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"To Do","title":"New Epic","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }
        }

        It 'Returns phase needs_planning' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.phase | Should -Be 'needs_planning'
        }

        It 'Returns has_plan false and plan_status none' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.has_plan | Should -BeFalse
            $result.plan_status | Should -Be 'none'
        }

        It 'Returns has_seeded_children false' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.has_seeded_children | Should -BeFalse
        }

        It 'Returns intent_conflict false (no existing state)' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.intent_conflict | Should -BeFalse
        }

        It 'Returns implementation_status not_started' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.implementation_status | Should -Be 'not_started'
        }
    }

    Context 'Scenario 2 — Work item with plan, no children (intent=resume)' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_seeding","action":"seed","message":"Needs seeding."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Planned Epic","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\test.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }
            Mock Get-Content {
                "---`nwork_item_id: 42`n---`n# Plan content"
            }
        }

        It 'Returns phase needs_seeding' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'resume' | ConvertFrom-Json
            $result.phase | Should -Be 'needs_seeding'
        }

        It 'Returns has_plan true with filesystem_fallback source' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'resume' | ConvertFrom-Json
            $result.has_plan | Should -BeTrue
            $result.plan_source | Should -Be 'filesystem_fallback'
            $result.plan_status | Should -Be 'complete'
        }

        It 'Returns has_seeded_children false' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'resume' | ConvertFrom-Json
            $result.has_seeded_children | Should -BeFalse
        }
    }

    Context 'Scenario 3 — Work item with children in progress (intent=resume)' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"ready_for_implementation","action":"implement","message":"Ready.","workspace_hint":{"feature_branch":"feature/42-test"}}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Active Epic","children":[{"id":100,"type":"Issue","state":"Doing","title":"Child 1","children":[{"id":200,"type":"Task","state":"To Do","title":"Task 1"}]},{"id":101,"type":"Issue","state":"To Do","title":"Child 2","children":[{"id":201,"type":"Task","state":"To Do","title":"Task 2"}]}]}'
            } -ParameterFilter { $args -contains 'tree' }
        }

        It 'Returns phase ready_for_implementation' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'resume' | ConvertFrom-Json
            $result.phase | Should -Be 'ready_for_implementation'
        }

        It 'Returns has_seeded_children true' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'resume' | ConvertFrom-Json
            $result.has_seeded_children | Should -BeTrue
        }

        It 'Returns seed_status seeded when all children have tasks' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'resume' | ConvertFrom-Json
            $result.seed_status | Should -Be 'seeded'
            $result.any_child_missing_tasks | Should -BeFalse
        }

        It 'Returns children_summary with correct counts' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'resume' | ConvertFrom-Json
            $summary = $result.children_summary | ConvertFrom-Json
            $summary.total | Should -Be 2
            $summary.doing | Should -Be 1
            $summary.todo | Should -Be 1
        }
    }

    Context 'Scenario 4 — Completed work item (all children Done)' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"done","action":"none","message":"Complete."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Done","title":"Done Epic","children":[{"id":100,"type":"Issue","state":"Done","title":"C1","children":[{"id":200,"type":"Task","state":"Done","title":"T1"}]},{"id":101,"type":"Issue","state":"Done","title":"C2","children":[{"id":201,"type":"Task","state":"Done","title":"T2"}]}]}'
            } -ParameterFilter { $args -contains 'tree' }
        }

        It 'Returns phase done' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.phase | Should -Be 'done'
        }

        It 'Returns implementation_status done' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.implementation_status | Should -Be 'done'
        }

        It 'Returns all children as done in summary' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $summary = $result.children_summary | ConvertFrom-Json
            $summary.total | Should -Be 2
            $summary.done | Should -Be 2
            $summary.doing | Should -Be 0
            $summary.todo | Should -Be 0
        }
    }

    Context 'Scenario 5 — Intent conflict (intent=new with existing children)' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"ready_for_implementation","action":"implement","message":"Ready."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Existing Epic","children":[{"id":100,"type":"Issue","state":"Doing","title":"Child","children":[{"id":200,"type":"Task","state":"To Do","title":"Task"}]}]}'
            } -ParameterFilter { $args -contains 'tree' }
        }

        It 'Returns intent_conflict true' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.intent_conflict | Should -BeTrue
        }

        It 'Preserves phase from polyphony route' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.phase | Should -Be 'ready_for_implementation'
        }

        It 'Returns needs_cleanup false (only intent_conflict is set)' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'new' | ConvertFrom-Json
            $result.needs_cleanup | Should -BeFalse
        }
    }

    Context 'Scenario 6 — Redo intent with existing plan' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_planning","action":"plan","message":"Plan."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Redo Epic","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }
            Mock Get-ChildItem {
                @([PSCustomObject]@{ FullName = 'C:\repo\docs\projects\redo.plan.md' })
            } -ParameterFilter { $Path -like '*plan.md' }
            Mock Get-Content {
                "---`nwork_item_id: 42`n---`n# Plan to redo"
            }
        }

        It 'Returns needs_cleanup true' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'redo' | ConvertFrom-Json
            $result.needs_cleanup | Should -BeTrue
        }

        It 'Returns has_plan true' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'redo' | ConvertFrom-Json
            $result.has_plan | Should -BeTrue
        }

        It 'Returns intent_conflict false (redo sets cleanup, not conflict)' {
            $result = & $script:ScriptPath -WorkItemId 42 -Intent 'redo' | ConvertFrom-Json
            $result.intent_conflict | Should -BeFalse
        }
    }

    Context 'Scenario 7 — Children missing tasks (issues with no grandchildren)' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_task_decomposition","action":"seed","message":"Tasks needed."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Unseeded Epic","children":[{"id":100,"type":"Issue","state":"To Do","title":"Issue 1","children":[]},{"id":101,"type":"Issue","state":"To Do","title":"Issue 2","children":[]}]}'
            } -ParameterFilter { $args -contains 'tree' }
        }

        It 'Returns phase needs_task_decomposition' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.phase | Should -Be 'needs_task_decomposition'
        }

        It 'Returns any_child_missing_tasks true' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.any_child_missing_tasks | Should -BeTrue
        }

        It 'Returns seed_status partial' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.seed_status | Should -Be 'partial'
        }

        It 'Returns has_seeded_children true (issues exist)' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.has_seeded_children | Should -BeTrue
        }
    }

    Context 'Scenario 8 — Ambiguous plans (multiple plan files match)' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_planning","action":"plan","message":"Plan."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Ambiguous Epic","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }
            Mock Get-ChildItem {
                @(
                    [PSCustomObject]@{ FullName = 'C:\repo\docs\projects\plan-a.plan.md' },
                    [PSCustomObject]@{ FullName = 'C:\repo\docs\projects\plan-b.plan.md' }
                )
            } -ParameterFilter { $Path -like '*plan.md' }
            Mock Get-Content {
                "---`nwork_item_id: 42`n---`n# Plan"
            }
        }

        It 'Returns plan_status ambiguous' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_status | Should -Be 'ambiguous'
        }

        It 'Returns has_plan false' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.has_plan | Should -BeFalse
        }

        It 'Returns empty plan_path' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.plan_path | Should -Be ''
        }
    }

    Context 'P5 compliance — zero type name literals' {

        It 'Contains no hardcoded type literals (Epic, Issue, Task)' {
            $matches = Select-String "'Epic'|'Issue'|'Task'" $script:ScriptPath
            $matches | Should -BeNullOrEmpty -Because 'P5 requires zero type name literals in scripts/detect-state.ps1'
        }
    }

    Context 'Error handler output format' {

        BeforeEach {
            Mock polyphony {
                '{"work_item_id":42,"phase":"needs_planning","action":"plan","message":"Plan."}'
            } -ParameterFilter { $args -contains 'route' }
            Mock twig {
                '{"id":42,"type":"Epic","state":"Doing","title":"Test","children":[]}'
            } -ParameterFilter { $args -contains 'tree' }
        }

        It 'Error output contains error, phase, and work_item_id keys' {
            Mock polyphony { throw 'Simulated failure' } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 2>$null
            $json = $result | ConvertFrom-Json
            $json.PSObject.Properties.Name | Should -Contain 'error'
            $json.PSObject.Properties.Name | Should -Contain 'phase'
            $json.PSObject.Properties.Name | Should -Contain 'work_item_id'
        }

        It 'Error output sets phase to error' {
            Mock polyphony { throw 'Simulated failure' } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 42 2>$null
            $json = $result | ConvertFrom-Json
            $json.phase | Should -Be 'error'
        }

        It 'Error output preserves work_item_id' {
            Mock polyphony { throw 'Simulated failure' } -ParameterFilter { $args -contains 'route' }

            $result = & $script:ScriptPath -WorkItemId 99 2>$null
            $json = $result | ConvertFrom-Json
            $json.work_item_id | Should -Be 99
        }

        It 'Error output sets non-zero exit code' {
            Mock polyphony { throw 'Simulated failure' } -ParameterFilter { $args -contains 'route' }

            & $script:ScriptPath -WorkItemId 42 2>$null | Out-Null
            $LASTEXITCODE | Should -Be 1
        }
    }
}
