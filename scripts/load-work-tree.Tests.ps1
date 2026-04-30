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
        Mock gh { $global:LASTEXITCODE = 0; $null }

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

# ── PG completion status and final JSON output tests (#2662) ──────────────────

Describe 'load-work-tree.ps1 — PG completion and output schema (#2662)' {

    BeforeEach {
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock git { 'https://github.com/TestOrg/TestRepo.git' } -ParameterFilter { $args -contains 'get-url' }
        Mock gh { $global:LASTEXITCODE = 0; $null }

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
      "state": "Done",
      "tags": "PG-1; twig",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task A",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "Done",
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

    Context 'Output schema keys' {

        It 'Contains all required top-level keys' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Contain 'work_tree'
            $result.PSObject.Properties.Name | Should -Contain 'pr_groups'
            $result.PSObject.Properties.Name | Should -Contain 'completed_pgs'
            $result.PSObject.Properties.Name | Should -Contain 'pending_pgs'
            $result.PSObject.Properties.Name | Should -Contain 'next_pg'
            $result.PSObject.Properties.Name | Should -Contain 'pgs_needing_reconciliation'
            $result.PSObject.Properties.Name | Should -Contain 'total_tasks'
            $result.PSObject.Properties.Name | Should -Contain 'total_issues'
            $result.PSObject.Properties.Name | Should -Contain 'tagged_items'
            $result.PSObject.Properties.Name | Should -Contain 'untagged_items'
        }

        It 'Computes total_tasks as implementable-only count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.total_tasks | Should -Be 2
        }

        It 'Computes total_issues as plannable count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.total_issues | Should -Be 3
        }

        It 'Computes tagged_items count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.tagged_items | Should -Be 4
        }

        It 'Computes untagged_items count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.untagged_items | Should -Be 1
        }
    }

    Context 'No merged PRs — all PGs pending' {

        It 'Marks all PGs as not completed' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups | ForEach-Object { $_.completed | Should -Be $false }
        }

        It 'Sets merged_pr to 0 for all PGs' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups | ForEach-Object { $_.merged_pr | Should -Be 0 }
        }

        It 'Lists all PGs as pending' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pending_pgs | Should -Contain 'PG-1'
            $result.pending_pgs | Should -Contain 'PG-2'
            $result.completed_pgs.Count | Should -Be 0
        }

        It 'Sets next_pg to first pending PG' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.next_pg | Should -Be 'PG-1'
        }

        It 'Returns empty pgs_needing_reconciliation' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pgs_needing_reconciliation.Count | Should -Be 0
        }
    }

    Context 'Tagged PG completed via merged PR' {

        BeforeEach {
            Mock gh {
                $global:LASTEXITCODE = 0
                '[{"number":10,"headRefName":"feature/pg-1","mergedAt":"2026-01-01T00:00:00Z"}]'
            }
        }

        It 'Marks PG-1 as completed when merged PR matches branch' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].completed | Should -Be $true
            $result.pr_groups[0].merged_pr | Should -Be 10
        }

        It 'Leaves PG-2 as pending (no matching merged PR)' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[1].completed | Should -Be $false
            $result.pr_groups[1].merged_pr | Should -Be 0
        }

        It 'Lists PG-1 in completed_pgs and PG-2 in pending_pgs' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.completed_pgs | Should -Contain 'PG-1'
            $result.pending_pgs | Should -Contain 'PG-2'
        }

        It 'Sets next_pg to PG-2' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.next_pg | Should -Be 'PG-2'
        }
    }

    Context 'Reconciliation — stale Doing tasks in completed PG' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Recon Epic",
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
      "tags": "PG-1",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task Stale",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "Doing",
          "tags": "PG-1",
          "children": []
        },
        {
          "work_item_id": 201,
          "title": "Task Done",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "Done",
          "tags": "PG-1",
          "children": []
        }
      ]
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }

            Mock gh {
                $global:LASTEXITCODE = 0
                '[{"number":5,"headRefName":"feature/pg-1","mergedAt":"2026-01-01T00:00:00Z"}]'
            }
        }

        It 'Detects non_done_task_ids in completed PG' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].non_done_task_ids | Should -Contain 200
        }

        It 'Detects stale_doing_task_ids in completed PG' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].stale_doing_task_ids | Should -Contain 200
        }

        It 'Detects non_done_issue_ids in completed PG' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].non_done_issue_ids | Should -Contain 100
        }

        It 'Sets needs_reconciliation to true' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].needs_reconciliation | Should -Be $true
        }

        It 'Includes PG in pgs_needing_reconciliation' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pgs_needing_reconciliation.Count | Should -Be 1
            $result.pgs_needing_reconciliation[0].name | Should -Be 'PG-1'
        }
    }

    Context 'Fallback PG-1 completion requires merged PR AND all Done' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Fallback Epic",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Done",
  "children": [
    {
      "work_item_id": 100,
      "title": "Issue",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "Done",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "Done",
          "children": []
        }
      ]
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Marks fallback PG-1 as completed when merged PR AND all items Done' {
            Mock gh {
                $global:LASTEXITCODE = 0
                '[{"number":1,"headRefName":"feature/pg-1-fallback-epic","mergedAt":"2026-01-01T00:00:00Z"}]'
            }
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.pr_groups[0].completed | Should -Be $true
        }

        It 'Does not mark fallback PG-1 as completed when items not all Done' {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Fallback Epic",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": [
    {
      "work_item_id": 100,
      "title": "Issue",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "Doing",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "To Do",
          "children": []
        }
      ]
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }

            Mock gh {
                $global:LASTEXITCODE = 0
                '[{"number":1,"headRefName":"feature/pg-1-fallback-epic","mergedAt":"2026-01-01T00:00:00Z"}]'
            }
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.pr_groups[0].completed | Should -Be $false
        }
    }

    Context 'All PGs completed — next_pg is empty' {

        BeforeEach {
            Mock gh {
                $global:LASTEXITCODE = 0
                @'
[
  {"number":10,"headRefName":"feature/pg-1","mergedAt":"2026-01-01T00:00:00Z"},
  {"number":11,"headRefName":"feature/pg-2","mergedAt":"2026-01-02T00:00:00Z"}
]
'@
            }
        }

        It 'Sets next_pg to empty string when all PGs completed' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.next_pg | Should -Be ''
        }

        It 'Lists all PGs as completed' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.completed_pgs | Should -Contain 'PG-1'
            $result.completed_pgs | Should -Contain 'PG-2'
            $result.pending_pgs.Count | Should -Be 0
        }
    }

    Context 'Completed PG with all items Done — no reconciliation needed' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Clean Epic",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": [
    {
      "work_item_id": 100,
      "title": "Issue",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "Done",
      "tags": "PG-1",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "Done",
          "tags": "PG-1",
          "children": []
        }
      ]
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }

            Mock gh {
                $global:LASTEXITCODE = 0
                '[{"number":5,"headRefName":"feature/pg-1","mergedAt":"2026-01-01T00:00:00Z"}]'
            }
        }

        It 'Sets needs_reconciliation to false when all items Done' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].needs_reconciliation | Should -Be $false
        }

        It 'Returns empty reconciliation arrays' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].non_done_task_ids.Count | Should -Be 0
            $result.pr_groups[0].stale_doing_task_ids.Count | Should -Be 0
            $result.pr_groups[0].non_done_issue_ids.Count | Should -Be 0
        }
    }

    Context 'Error handling' {

        It 'Returns error JSON when script fails' {
            Mock polyphony { throw "hierarchy failed" } -ParameterFilter { $args -contains 'hierarchy' }
            $output = & $script:ScriptPath -WorkItemId 42 2>$null
            $result = $output | ConvertFrom-Json
            $result.error | Should -Not -BeNullOrEmpty
        }
    }
}

# ── Output schema compatibility verification (#2640) ─────────────────────────

Describe 'load-work-tree.ps1 — output schema compatibility (#2640)' {

    BeforeEach {
        Mock twig { } -ParameterFilter { $args -contains 'sync' }
        Mock git { 'https://github.com/TestOrg/TestRepo.git' } -ParameterFilter { $args -contains 'get-url' }
        Mock gh { $global:LASTEXITCODE = 0; $null }
    }

    Context 'Required schema keys — all 10 from reference' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Schema Epic",
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
      "tags": "PG-1",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task A",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "To Do",
          "tags": "PG-1",
          "children": []
        }
      ]
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Contains all 10 required top-level keys' {
            $requiredKeys = @(
                'work_tree', 'pr_groups', 'completed_pgs', 'pending_pgs',
                'next_pg', 'pgs_needing_reconciliation',
                'total_tasks', 'total_issues', 'tagged_items', 'untagged_items'
            )

            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name

            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json

            # Object
            $result.work_tree | Should -Not -BeNullOrEmpty
            $result.work_tree.PSObject.Properties.Name | Should -Not -BeNullOrEmpty

            # Arrays (empty JSON arrays may deserialize as $null in PowerShell)
            $result.PSObject.Properties.Name | Should -Contain 'pr_groups'
            $result.PSObject.Properties.Name | Should -Contain 'completed_pgs'
            $result.PSObject.Properties.Name | Should -Contain 'pending_pgs'
            $result.PSObject.Properties.Name | Should -Contain 'pgs_needing_reconciliation'

            # String
            $result.next_pg | Should -BeOfType [string]

            # Integers
            $result.total_tasks | Should -BeOfType [long]
            $result.total_issues | Should -BeOfType [long]
            $result.tagged_items | Should -BeOfType [long]
            $result.untagged_items | Should -BeOfType [long]
        }

        It 'work_tree contains required sub-keys' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $wtKeys = $result.work_tree.PSObject.Properties.Name
            $wtKeys | Should -Contain 'epic_id'
            $wtKeys | Should -Contain 'epic_title'
            $wtKeys | Should -Contain 'epic_type'
            $wtKeys | Should -Contain 'issues'
        }

        It 'pr_groups entries contain required fields' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $pg = $result.pr_groups[0]
            $pgKeys = $pg.PSObject.Properties.Name
            $pgKeys | Should -Contain 'name'
            $pgKeys | Should -Contain 'task_ids'
            $pgKeys | Should -Contain 'issue_ids'
            $pgKeys | Should -Contain 'branch_name_suggestion'
            $pgKeys | Should -Contain 'completed'
            $pgKeys | Should -Contain 'merged_pr'
            $pgKeys | Should -Contain 'needs_reconciliation'
            $pgKeys | Should -Contain 'non_done_task_ids'
            $pgKeys | Should -Contain 'stale_doing_task_ids'
            $pgKeys | Should -Contain 'non_done_issue_ids'
        }
    }

    Context 'Scenario 1 — Epic root (3-tier hierarchy with PG tags)' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Epic Root",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": [
    {
      "work_item_id": 100,
      "title": "Issue Alpha",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "Doing",
      "tags": "PG-1; twig",
      "children": [
        {
          "work_item_id": 200,
          "title": "Task X",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "Done",
          "tags": "PG-1",
          "children": []
        },
        {
          "work_item_id": 201,
          "title": "Task Y",
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
      "title": "Issue Beta",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "To Do",
      "tags": "PG-2",
      "children": [
        {
          "work_item_id": 300,
          "title": "Task Z",
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

        It 'Sets work_tree.epic_id to root work_item_id' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.epic_id | Should -Be 42
        }

        It 'Sets work_tree.epic_title to root title' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.epic_title | Should -Be 'Epic Root'
        }

        It 'Sets work_tree.epic_type to root type' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.epic_type | Should -Be 'Epic'
        }

        It 'Builds issues array from children' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.issues.Count | Should -Be 2
        }

        It 'Issues contain nested tasks array' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.work_tree.issues[0].tasks.Count | Should -Be 2
            $result.work_tree.issues[1].tasks.Count | Should -Be 1
        }

        It 'Creates two PR groups matching PG tags' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups.Count | Should -Be 2
            $result.pr_groups[0].name | Should -Be 'PG-1'
            $result.pr_groups[1].name | Should -Be 'PG-2'
        }

        It 'PG-1 task_ids contains implementable items' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].task_ids | Should -Contain 200
            $result.pr_groups[0].task_ids | Should -Contain 201
        }

        It 'PG-1 issue_ids contains container items' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pr_groups[0].issue_ids | Should -Contain 100
        }

        It 'Computes correct total_tasks count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.total_tasks | Should -Be 3
        }

        It 'Computes correct total_issues count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.total_issues | Should -Be 3
        }

        It 'Computes correct tagged_items count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.tagged_items | Should -Be 5
        }

        It 'Computes correct untagged_items count' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.untagged_items | Should -Be 1
        }

        It 'Lists all PGs in pending_pgs when no merges' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pending_pgs | Should -Contain 'PG-1'
            $result.pending_pgs | Should -Contain 'PG-2'
            $result.completed_pgs.Count | Should -Be 0
        }

        It 'Sets next_pg to first pending PG' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.next_pg | Should -Be 'PG-1'
        }

        It 'Returns empty pgs_needing_reconciliation' {
            $result = & $script:ScriptPath -WorkItemId 42 | ConvertFrom-Json
            $result.pgs_needing_reconciliation.Count | Should -Be 0
        }
    }

    Context 'Scenario 2 — Issue root (2-tier hierarchy)' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 500,
  "title": "Issue Root",
  "type": "Issue",
  "capabilities": ["plannable"],
  "state": "Doing",
  "tags": "PG-1",
  "children": [
    {
      "work_item_id": 501,
      "title": "Task Under Issue",
      "type": "Task",
      "capabilities": ["implementable"],
      "state": "To Do",
      "tags": "PG-1",
      "children": []
    },
    {
      "work_item_id": 502,
      "title": "Task Two Under Issue",
      "type": "Task",
      "capabilities": ["implementable"],
      "state": "Done",
      "tags": "PG-1",
      "children": []
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Sets work_tree.epic_id to issue root id' {
            $result = & $script:ScriptPath -WorkItemId 500 | ConvertFrom-Json
            $result.work_tree.epic_id | Should -Be 500
        }

        It 'Sets work_tree.epic_type to Issue' {
            $result = & $script:ScriptPath -WorkItemId 500 | ConvertFrom-Json
            $result.work_tree.epic_type | Should -Be 'Issue'
        }

        It 'Builds issues array from direct children' {
            $result = & $script:ScriptPath -WorkItemId 500 | ConvertFrom-Json
            $result.work_tree.issues.Count | Should -Be 2
        }

        It 'Contains all required top-level keys' {
            $requiredKeys = @(
                'work_tree', 'pr_groups', 'completed_pgs', 'pending_pgs',
                'next_pg', 'pgs_needing_reconciliation',
                'total_tasks', 'total_issues', 'tagged_items', 'untagged_items'
            )
            $result = & $script:ScriptPath -WorkItemId 500 | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name
            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present for Issue root"
            }
        }

        It 'Produces valid pr_groups for 2-tier hierarchy' {
            $result = & $script:ScriptPath -WorkItemId 500 | ConvertFrom-Json
            $result.pr_groups.Count | Should -BeGreaterOrEqual 1
            $result.pr_groups[0].name | Should -Be 'PG-1'
        }
    }

    Context 'Scenario 3 — Task root (leaf, single item)' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 999,
  "title": "Leaf Task",
  "type": "Task",
  "capabilities": ["implementable"],
  "state": "To Do",
  "tags": "PG-1",
  "children": []
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Sets work_tree.epic_id to task root id' {
            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $result.work_tree.epic_id | Should -Be 999
        }

        It 'Sets work_tree.epic_type to Task' {
            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $result.work_tree.epic_type | Should -Be 'Task'
        }

        It 'Builds empty issues array for leaf node' {
            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $result.work_tree.issues.Count | Should -Be 0
        }

        It 'Contains all required top-level keys' {
            $requiredKeys = @(
                'work_tree', 'pr_groups', 'completed_pgs', 'pending_pgs',
                'next_pg', 'pgs_needing_reconciliation',
                'total_tasks', 'total_issues', 'tagged_items', 'untagged_items'
            )
            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name
            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present for Task root"
            }
        }

        It 'Produces valid pr_groups for single leaf item' {
            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $result.pr_groups.Count | Should -BeGreaterOrEqual 1
        }

        It 'Sets total_tasks for leaf task with implementable capability' {
            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $result.total_tasks | Should -Be 1
        }

        It 'Sets total_issues to 0 for leaf task' {
            $result = & $script:ScriptPath -WorkItemId 999 | ConvertFrom-Json
            $result.total_issues | Should -Be 0
        }
    }

    Context 'Scenario 4 — No PG tags (PG-1 fallback)' {

        BeforeEach {
            Mock polyphony {
                @'
{
  "work_item_id": 42,
  "title": "Untagged Epic",
  "type": "Epic",
  "capabilities": ["plannable"],
  "state": "Doing",
  "children": [
    {
      "work_item_id": 100,
      "title": "Untagged Issue",
      "type": "Issue",
      "capabilities": ["plannable"],
      "state": "Doing",
      "children": [
        {
          "work_item_id": 200,
          "title": "Untagged Task",
          "type": "Task",
          "capabilities": ["implementable"],
          "state": "To Do",
          "children": []
        }
      ]
    }
  ]
}
'@
            } -ParameterFilter { $args -contains 'hierarchy' }
        }

        It 'Creates single PG-1 fallback group' {
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.pr_groups.Count | Should -Be 1
            $result.pr_groups[0].name | Should -Be 'PG-1'
        }

        It 'Fallback PG-1 task_ids contains implementable-only items' {
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.pr_groups[0].task_ids | Should -Contain 200
        }

        It 'Fallback PG-1 issue_ids contains plannable items' {
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.pr_groups[0].issue_ids | Should -Contain 42
            $result.pr_groups[0].issue_ids | Should -Contain 100
        }

        It 'Contains all required top-level keys in fallback mode' {
            $requiredKeys = @(
                'work_tree', 'pr_groups', 'completed_pgs', 'pending_pgs',
                'next_pg', 'pgs_needing_reconciliation',
                'total_tasks', 'total_issues', 'tagged_items', 'untagged_items'
            )
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name
            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present in PG-1 fallback"
            }
        }

        It 'Reports 0 tagged_items and correct untagged_items count' {
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $result.tagged_items | Should -Be 0
            $result.untagged_items | Should -Be 3
        }

        It 'Fallback PG-1 has all pr_group fields' {
            $result = & $script:ScriptPath -WorkItemId 42 3>$null | ConvertFrom-Json
            $pg = $result.pr_groups[0]
            $pgKeys = $pg.PSObject.Properties.Name
            $pgKeys | Should -Contain 'name'
            $pgKeys | Should -Contain 'task_ids'
            $pgKeys | Should -Contain 'issue_ids'
            $pgKeys | Should -Contain 'branch_name_suggestion'
            $pgKeys | Should -Contain 'completed'
            $pgKeys | Should -Contain 'merged_pr'
            $pgKeys | Should -Contain 'needs_reconciliation'
        }
    }

    Context 'P5 compliance — zero type name literals' {

        It 'load-work-tree.ps1 contains no hardcoded type literals' {
            $matches = Select-String "'Epic'|'Issue'|'Task'" $script:ScriptPath
            $matches | Should -BeNullOrEmpty -Because 'capability-based classification must not use type name literals'
        }

        It 'pg-helpers.ps1 contains no hardcoded type literals' {
            $matches = Select-String "'Epic'|'Issue'|'Task'" $script:HelpersPath
            $matches | Should -BeNullOrEmpty -Because 'shared helper library must not use type name literals'
        }
    }

    Context 'Error handler output format' {

        It 'Error output contains error key' {
            Mock polyphony { throw 'Simulated hierarchy failure' } -ParameterFilter { $args -contains 'hierarchy' }
            $output = & $script:ScriptPath -WorkItemId 42 2>$null
            $json = $output | ConvertFrom-Json
            $json.PSObject.Properties.Name | Should -Contain 'error'
        }

        It 'Error output has non-empty error message' {
            Mock polyphony { throw 'Simulated hierarchy failure' } -ParameterFilter { $args -contains 'hierarchy' }
            $output = & $script:ScriptPath -WorkItemId 42 2>$null
            $json = $output | ConvertFrom-Json
            $json.error | Should -Not -BeNullOrEmpty
        }

        It 'Error output is valid JSON' {
            Mock polyphony { throw 'Simulated hierarchy failure' } -ParameterFilter { $args -contains 'hierarchy' }
            $output = & $script:ScriptPath -WorkItemId 42 2>$null
            { $output | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'Error output sets non-zero exit code' {
            Mock polyphony { throw 'Simulated hierarchy failure' } -ParameterFilter { $args -contains 'hierarchy' }
            & $script:ScriptPath -WorkItemId 42 2>$null | Out-Null
            $LASTEXITCODE | Should -Be 1
        }
    }
}
