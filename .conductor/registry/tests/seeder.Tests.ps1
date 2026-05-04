#requires -Version 7
<#
  Pester tests for scripts/seeder.ps1.

  Mocks the global `twig` function so the script's external invocations are
  intercepted in-process. Verifies marker-aware idempotency, title fallback,
  create path, tag-set behavior, and error rollup.
#>

BeforeAll {
    $script:SeederScript = Join-Path $PSScriptRoot '..' 'scripts' 'seeder.ps1'

    # Stub global twig so Mock works at the function level (Pester can't mock
    # native executables directly).
    function global:twig { }

    # Helper: drive the script and parse stdout JSON.
    function script:Invoke-Seeder {
        param(
            [Parameter(Mandatory)] [int]    $WorkItemId,
            [Parameter(Mandatory)] [object] $Tasks,
            [string] $PlannedTag = 'polyphony:planned'
        )
        # Force array semantics so a single-element list becomes a JSON array.
        $arr = @($Tasks)
        $tasksJson = ConvertTo-Json -InputObject $arr -Depth 10 -Compress
        if ($arr.Count -eq 0) { $tasksJson = '[]' }
        elseif ($arr.Count -eq 1 -and $tasksJson -notmatch '^\[') {
            $tasksJson = "[$tasksJson]"
        }
        $stdout = & $script:SeederScript -WorkItemId $WorkItemId -TasksJson $tasksJson -PlannedTag $PlannedTag -TwigCommand 'twig'
        return $stdout | ConvertFrom-Json
    }
}

Describe 'seeder.ps1' {

    BeforeEach {
        # Reset $LASTEXITCODE so a prior test (or the outer test runner) that
        # left it nonzero doesn't poison Invoke-Twig — the mocked `twig`
        # function does not set $LASTEXITCODE, so it inherits whatever was
        # there before.
        $global:LASTEXITCODE = 0
    }

    Context 'Empty parent (no existing children)' {

        BeforeEach {
            Mock twig {
                if ($args[0] -eq 'show' -and $args -contains '--tree') {
                    return '{"focus":{"id":100,"tags":""},"children":[]}'
                }
                if ($args[0] -eq 'show' -and $args -notcontains '--tree') {
                    return '{"id":100,"tags":""}'
                }
                if ($args[0] -eq 'new') {
                    return '{"id":201,"title":"created"}'
                }
                if ($args[0] -eq 'patch') {
                    return ''
                }
                throw "unexpected twig args: $($args -join ' ')"
            }
        }

        It 'Creates a single new child and embeds the marker' {
            $tasks = ,@{
                task_id = 'task-1'
                title = 'Implement X'
                type = 'Task'
                description = 'Do the thing.'
                acceptance_criteria = @('AC1', 'AC2')
            }

            $result = Invoke-Seeder -WorkItemId 100 -Tasks $tasks

            $result.seeded_count | Should -Be 1
            $result.reused_count | Should -Be 0
            $result.error_count  | Should -Be 0
            $result.planned_tag_set | Should -BeTrue

            # Verify the create call embedded the marker in --description.
            Should -Invoke twig -ParameterFilter {
                $args[0] -eq 'new' -and
                ($args -join ' ') -match 'polyphony:plan-task-id=task-1'
            }
        }

        It 'Sets planned tag when task list is empty (atomic case)' {
            $result = Invoke-Seeder -WorkItemId 100 -Tasks @()

            $result.task_count | Should -Be 0
            $result.seeded_count | Should -Be 0
            $result.error_count | Should -Be 0
            $result.planned_tag_set | Should -BeTrue
            Should -Invoke twig -ParameterFilter { $args[0] -eq 'patch' }
        }
    }

    Context 'Existing children with markers (re-seed)' {

        BeforeEach {
            $treeJson = @'
{
  "focus": {"id": 100, "tags": ""},
  "children": [
    {
      "id": 201,
      "title": "Implement X",
      "type": "Task",
      "fields": {"System.Description": "<p>Do X.</p>\n<!-- polyphony:plan-task-id=task-1 -->"}
    },
    {
      "id": 202,
      "title": "Test X",
      "type": "Task",
      "fields": {"System.Description": "<p>Test it.</p>\n<!-- polyphony:plan-task-id=task-2 -->"}
    }
  ]
}
'@
            Mock twig {
                if ($args[0] -eq 'show' -and $args -contains '--tree') { return $treeJson }
                if ($args[0] -eq 'show') { return '{"id":100,"tags":""}' }
                if ($args[0] -eq 'patch') { return '' }
                if ($args[0] -eq 'new') { return '{"id":999}' }
                throw "unexpected twig args: $($args -join ' ')"
            }
        }

        It 'Reuses both existing children by marker; creates nothing' {
            $tasks = @(
                @{ task_id = 'task-1'; title = 'Implement X'; type = 'Task'; description = 'Do X.' },
                @{ task_id = 'task-2'; title = 'Test X';      type = 'Task'; description = 'Test it.' }
            )

            $result = Invoke-Seeder -WorkItemId 100 -Tasks $tasks

            $result.seeded_count | Should -Be 0
            $result.reused_count | Should -Be 2
            $result.error_count  | Should -Be 0
            $result.reused_items[0].matched_by | Should -Be 'marker'
            Should -Invoke twig -Times 0 -ParameterFilter { $args[0] -eq 'new' }
        }

        It 'Reuses by marker, creates only the new task' {
            $tasks = @(
                @{ task_id = 'task-1'; title = 'Implement X'; type = 'Task'; description = 'Do X.' },
                @{ task_id = 'task-2'; title = 'Test X';      type = 'Task'; description = 'Test it.' },
                @{ task_id = 'task-3'; title = 'Document X';  type = 'Task'; description = 'Docs.' }
            )

            $result = Invoke-Seeder -WorkItemId 100 -Tasks $tasks

            $result.seeded_count | Should -Be 1
            $result.reused_count | Should -Be 2
            $result.error_count  | Should -Be 0
            Should -Invoke twig -Times 1 -ParameterFilter {
                $args[0] -eq 'new' -and ($args -join ' ') -match 'polyphony:plan-task-id=task-3'
            }
        }
    }

    Context 'Marker-damaged child (title fallback)' {

        BeforeEach {
            $treeJson = @'
{
  "focus": {"id": 100, "tags": ""},
  "children": [
    {
      "id": 201,
      "title": "Implement X",
      "type": "Task",
      "fields": {"System.Description": "<p>Marker was deleted by a human edit.</p>"}
    }
  ]
}
'@
            Mock twig {
                if ($args[0] -eq 'show' -and $args -contains '--tree') { return $treeJson }
                if ($args[0] -eq 'show') { return '{"id":100,"tags":""}' }
                if ($args[0] -eq 'patch') { return '' }
                if ($args[0] -eq 'new') { return '{"id":999}' }
                throw "unexpected twig args: $($args -join ' ')"
            }
        }

        It 'Falls back to title+type match and emits a warning' {
            $tasks = ,@{
                task_id = 'task-1'
                title = 'Implement X'
                type = 'Task'
                description = 'Do X.'
            }

            $result = Invoke-Seeder -WorkItemId 100 -Tasks $tasks

            $result.seeded_count | Should -Be 0
            $result.reused_count | Should -Be 1
            $result.reused_items[0].matched_by | Should -Be 'title'
            $result.warnings.Count | Should -BeGreaterThan 0
            $result.warnings[0] | Should -Match 'title fallback'
            Should -Invoke twig -Times 0 -ParameterFilter { $args[0] -eq 'new' }
        }
    }

    Context 'Tag merge with existing tags' {

        BeforeEach {
            Mock twig {
                if ($args[0] -eq 'show' -and $args -contains '--tree') {
                    return '{"focus":{"id":100,"tags":"existing-tag"},"children":[]}'
                }
                if ($args[0] -eq 'show') {
                    return '{"id":100,"tags":"existing-tag"}'
                }
                if ($args[0] -eq 'patch') { return '' }
                if ($args[0] -eq 'new')   { return '{"id":201}' }
                throw "unexpected twig args: $($args -join ' ')"
            }
        }

        It 'Merges polyphony:planned with existing tags, preserving them' {
            $result = Invoke-Seeder -WorkItemId 100 -Tasks @()

            $result.planned_tag_set | Should -BeTrue
            Should -Invoke twig -ParameterFilter {
                $args[0] -eq 'patch' -and
                ($args -join ' ') -match 'existing-tag' -and
                ($args -join ' ') -match 'polyphony:planned'
            }
        }

        It 'Does not re-patch when tag is already present' {
            Mock twig {
                if ($args[0] -eq 'show' -and $args -contains '--tree') {
                    return '{"focus":{"id":100,"tags":"polyphony:planned"},"children":[]}'
                }
                if ($args[0] -eq 'show') {
                    return '{"id":100,"tags":"polyphony:planned"}'
                }
                if ($args[0] -eq 'patch') { return '' }
                throw "unexpected twig args: $($args -join ' ')"
            }

            $result = Invoke-Seeder -WorkItemId 100 -Tasks @()

            $result.planned_tag_set | Should -BeTrue
            $result.planned_tag_already | Should -BeTrue
            Should -Invoke twig -Times 0 -ParameterFilter { $args[0] -eq 'patch' }
        }
    }

    Context 'Error handling' {

        It 'Records errors per failing create and skips tag-set' {
            Mock twig {
                if ($args[0] -eq 'show' -and $args -contains '--tree') {
                    return '{"focus":{"id":100,"tags":""},"children":[]}'
                }
                if ($args[0] -eq 'show') { return '{"id":100,"tags":""}' }
                if ($args[0] -eq 'new') {
                    $global:LASTEXITCODE = 1
                    return 'twig: API error'
                }
                if ($args[0] -eq 'patch') { return '' }
                throw "unexpected twig args: $($args -join ' ')"
            }

            $tasks = ,@{
                task_id = 'task-1'
                title = 'Implement X'
                type = 'Task'
                description = 'Do X.'
            }

            $result = Invoke-Seeder -WorkItemId 100 -Tasks $tasks

            $result.seeded_count | Should -Be 0
            $result.error_count  | Should -Be 1
            $result.errors[0].task_id | Should -Be 'task-1'
            $result.planned_tag_set | Should -BeFalse
            Should -Invoke twig -Times 0 -ParameterFilter { $args[0] -eq 'patch' }
        }

        It 'Reports per-task errors when a task is missing required fields' {
            Mock twig {
                if ($args[0] -eq 'show' -and $args -contains '--tree') {
                    return '{"focus":{"id":100,"tags":""},"children":[]}'
                }
                if ($args[0] -eq 'show') { return '{"id":100,"tags":""}' }
                throw "unexpected twig args: $($args -join ' ')"
            }

            $tasks = ,@{ task_id = 'task-1'; description = 'Missing title and type' }

            $result = Invoke-Seeder -WorkItemId 100 -Tasks $tasks

            $result.error_count | Should -Be 1
            $result.errors[0].error | Should -Match 'missing required title or type'
            $result.planned_tag_set | Should -BeFalse
        }
    }
}
