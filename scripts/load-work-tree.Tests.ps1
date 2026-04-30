BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'load-work-tree.ps1'
    $script:HelpersPath = Join-Path $PSScriptRoot 'lib' 'pg-helpers.ps1'

    # Load pg-helpers directly for unit-testing its functions
    . $script:HelpersPath

    # Define placeholder functions for external commands so Pester can mock them.
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

# ── pg-helpers.ps1 unit tests ─────────────────────────────────────────────────

Describe 'Get-PGTag' {

    It 'Returns $null for empty string' {
        Get-PGTag -Tags '' | Should -BeNullOrEmpty
    }

    It 'Returns $null for $null' {
        Get-PGTag -Tags $null | Should -BeNullOrEmpty
    }

    It 'Extracts PG-1 from semicolon-separated tags' {
        Get-PGTag -Tags 'twig; PG-1; backend' | Should -Be 'PG-1'
    }

    It 'Extracts PG-10 from tags' {
        Get-PGTag -Tags 'PG-10; feature' | Should -Be 'PG-10'
    }

    It 'Returns first PG tag when multiple exist' {
        Get-PGTag -Tags 'PG-2; PG-3' | Should -Be 'PG-2'
    }

    It 'Returns $null when no PG tags present' {
        Get-PGTag -Tags 'twig; backend; feature' | Should -BeNullOrEmpty
    }

    It 'Handles single PG tag with no semicolons' {
        Get-PGTag -Tags 'PG-5' | Should -Be 'PG-5'
    }
}

Describe 'Group-ByPG' {

    It 'Groups implementable-only items into implementable_ids' {
        $items = @(
            [pscustomobject]@{ work_item_id = 100; tags = 'PG-1'; capabilities = @('implementable') }
        )
        $result = Group-ByPG -items $items
        $result.Count | Should -Be 1
        $result['PG-1'].implementable_ids | Should -Contain 100
        $result['PG-1'].container_ids | Should -BeNullOrEmpty
    }

    It 'Groups plannable items into container_ids' {
        $items = @(
            [pscustomobject]@{ work_item_id = 50; tags = 'PG-1'; capabilities = @('plannable') }
        )
        $result = Group-ByPG -items $items
        $result['PG-1'].container_ids | Should -Contain 50
        $result['PG-1'].implementable_ids | Should -BeNullOrEmpty
    }

    It 'Groups items with both capabilities into container_ids' {
        $items = @(
            [pscustomobject]@{ work_item_id = 75; tags = 'PG-1'; capabilities = @('plannable', 'implementable') }
        )
        $result = Group-ByPG -items $items
        $result['PG-1'].container_ids | Should -Contain 75
        $result['PG-1'].implementable_ids | Should -BeNullOrEmpty
    }

    It 'Skips items with no PG tag' {
        $items = @(
            [pscustomobject]@{ work_item_id = 10; tags = 'twig'; capabilities = @('implementable') }
        )
        $result = Group-ByPG -items $items
        $result.Count | Should -Be 0
    }

    It 'Groups items into separate PGs' {
        $items = @(
            [pscustomobject]@{ work_item_id = 1; tags = 'PG-1'; capabilities = @('implementable') }
            [pscustomobject]@{ work_item_id = 2; tags = 'PG-2'; capabilities = @('implementable') }
        )
        $result = Group-ByPG -items $items
        $result.Count | Should -Be 2
        $result['PG-1'].implementable_ids | Should -Contain 1
        $result['PG-2'].implementable_ids | Should -Contain 2
    }

    It 'Returns empty ordered hashtable when no items' {
        $result = Group-ByPG -items @()
        $result.Count | Should -Be 0
    }
}

# ── load-work-tree.ps1 integration tests ──────────────────────────────────────

Describe 'load-work-tree.ps1 — PG structure and PR groups (#2661)' {

    BeforeEach {
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock git { 'https://github.com/TestOrg/TestRepo.git' } -ParameterFilter { $args -contains 'get-url' }

        # Default hierarchy: Epic with 2 Issues, each with Tasks. PG-tagged.
        Mock polyphony {
            @'
{
  "work_item_id": 42,
  "title": "Test Epic",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": [
    {
      "work_item_id": 100,
      "title": "Issue One",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "Doing",
      "tags": "PG-1; twig",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task A",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "To Do",
          "tags": "PG-1",
          "children": []
        },
        {
          "work_item_id": 201,
          "title": "Task B",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "To Do",
          "tags": "PG-1",
          "children": []
        }
      ]
    },
    {
      "work_item_id": 101,
      "title": "Issue Two",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "To Do",
      "tags": "PG-2",
      "children": [
        {
          "work_item_id": 300,
          "title": "Task C",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "To Do",
          "tags": "PG-2",
          "children": []
        }
      ]
    }
  ]
}
'@
        } -ParameterFilter { $args -contains 'hierarchy' }
    }

    Context 'work_tree structure' {

        It 'Includes epic_id from hierarchy root' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.epic_id | Should -Be 42
        }

        It 'Includes epic_title from hierarchy root' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.epic_title | Should -Be 'Test Epic'
        }

        It 'Includes epic_type from hierarchy root' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.epic_type | Should -Be 'Epic'
        }

        It 'Builds issues array from hierarchy children' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.issues.Count | Should -Be 2
        }

        It 'Issue has correct fields' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $issue = $result.work_tree.issues[0]
            $issue.id | Should -Be 100
            $issue.title | Should -Be 'Issue One'
            $issue.state | Should -Be 'Doing'
            $issue.type | Should -Be 'Issue'
        }

        It 'Issue includes task_count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.issues[0].task_count | Should -Be 2
            $result.work_tree.issues[1].task_count | Should -Be 1
        }

        It 'Issue includes tasks array with correct fields' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $tasks = $result.work_tree.issues[0].tasks
            $tasks.Count | Should -Be 2
            $tasks[0].id | Should -Be 200
            $tasks[0].title | Should -Be 'Task A'
            $tasks[0].state | Should -Be 'To Do'
        }
    }

    Context 'PR groups with PG tags' {

        It 'Produces pr_groups array' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups | Should -Not -BeNullOrEmpty
        }

        It 'Creates one PR group per PG tag' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups.Count | Should -Be 2
        }

        It 'Sorts PG keys numerically' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].name | Should -Be 'PG-1'
            $result.pr_groups[1].name | Should -Be 'PG-2'
        }

        It 'Maps implementable_ids to task_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $pg1 = $result.pr_groups[0]
            $pg1.task_ids | Should -Contain 200
            $pg1.task_ids | Should -Contain 201
        }

        It 'Maps container_ids to issue_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $pg1 = $result.pr_groups[0]
            $pg1.issue_ids | Should -Contain 100
        }

        It 'Generates branch_name_suggestion' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].branch_name_suggestion | Should -Be 'feature/pg-1'
            $result.pr_groups[1].branch_name_suggestion | Should -Be 'feature/pg-2'
        }

        It 'Includes repo_slug from git remote' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.repo_slug | Should -Be 'TestOrg/TestRepo'
        }
    }

    Context 'PG numeric sorting' {

        It 'Sorts PG-1, PG-2, PG-10 in numeric order' {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Sort Test",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": [
    {
      "work_item_id": 10, "title": "I10", "type": "Issue",
      "capabilities": ["plannable"], "state": "Doing", "tags": "PG-10", "children": []
    },
    {
      "work_item_id": 1, "title": "I1", "type": "Issue",
      "capabilities": ["plannable"], "state": "Doing", "tags": "PG-1", "children": []
    },
    {
      "work_item_id": 2, "title": "I2", "type": "Issue",
      "capabilities": ["plannable"], "state": "Doing", "tags": "PG-2", "children": []
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].name | Should -Be 'PG-1'
            $result.pr_groups[1].name | Should -Be 'PG-2'
            $result.pr_groups[2].name | Should -Be 'PG-10'
        }
    }

    Context 'PG-1 fallback (no PG tags)' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "No Tags Epic!",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": [
    {
      "work_item_id": 100, "title": "Issue", "type": "Issue",
      "capabilities": ["plannable"], "state": "Doing",
      "children": [
        {
          "work_item_id": 200, "title": "Task", "type": "Task",
          "capabilities": ["implementable"], "state": "To Do", "children": []
        }
      ]
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Creates single PG-1 group when no PG tags exist' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups.Count | Should -Be 1
            $result.pr_groups[0].name | Should -Be 'PG-1'
        }

        It 'Puts implementable-only items in task_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].task_ids | Should -Contain 200
        }

        It 'Puts plannable items in issue_ids' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $pg1 = $result.pr_groups[0]
            $pg1.issue_ids | Should -Contain 42
            $pg1.issue_ids | Should -Contain 100
        }

        It 'Generates branch name from epic title' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].branch_name_suggestion | Should -Be 'feature/pg-1-no-tags-epic'
        }

        It 'Emits a warning about no PG tags' {
            $result = & $script:ScriptPath -WorkItemId 42 3>&1
            $warnings = @($result | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
            $warnings.Count | Should -Be 1
            $warnings[0].Message | Should -BeLike '*No PG tags*'
        }
    }

    Context 'Branch name truncation' {

        It 'Truncates branch_name_suggestion to 60 characters' {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "This Is A Very Long Epic Title That Should Cause Branch Name Truncation When Combined With PG Prefix",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": []
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }

            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.pr_groups[0].branch_name_suggestion.Length | Should -BeLessOrEqual 60
        }
    }

    Context 'Capability-based classification (no type literals)' {

        It 'Does not contain type literal strings in script' {
            $content = Get-Content $script:ScriptPath -Raw
            $content | Should -Not -Match "'Epic'|'Issue'|'Task'"
        }

        It 'pg-helpers does not contain type literal strings' {
            $content = Get-Content $script:HelpersPath -Raw
            $content | Should -Not -Match "'Epic'|'Issue'|'Task'"
        }
    }

    Context 'Empty hierarchy' {

        It 'Handles root with no children gracefully' {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Empty",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": []
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }

            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.work_tree.issues.Count | Should -Be 0
            $result.pr_groups.Count | Should -Be 1
            $result.pr_groups[0].name | Should -Be 'PG-1'
        }
    }
}
