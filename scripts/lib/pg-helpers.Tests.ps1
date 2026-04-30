BeforeAll {
    $script:HelpersPath = Join-Path $PSScriptRoot 'pg-helpers.ps1'
    . $script:HelpersPath
}

# ── Get-PGTag ─────────────────────────────────────────────────────────────────

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

    It 'Extracts multi-digit PG tag' {
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

    It 'Trims whitespace around PG tag' {
        Get-PGTag -Tags '  PG-3  ' | Should -Be 'PG-3'
    }

    It 'Ignores non-PG tags that contain digits' {
        Get-PGTag -Tags 'v2; release-3; sprint-1' | Should -BeNullOrEmpty
    }

    It 'Matches PG tag with trailing text' {
        Get-PGTag -Tags 'PG-1-extra; other' | Should -Be 'PG-1-extra'
    }
}

# ── Group-ByPG ────────────────────────────────────────────────────────────────

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

    It 'Groups items with both capabilities and no children into implementable_ids (issue-as-task)' {
        $items = @(
            [pscustomobject]@{ work_item_id = 75; tags = 'PG-1'; capabilities = @('plannable', 'implementable'); children = @() }
        )
        $result = Group-ByPG -items $items
        $result['PG-1'].implementable_ids | Should -Contain 75
        $result['PG-1'].container_ids | Should -BeNullOrEmpty
    }

    It 'Groups items with both capabilities and children into container_ids' {
        $child = [pscustomobject]@{ work_item_id = 76; tags = 'PG-1'; capabilities = @('implementable') }
        $items = @(
            [pscustomobject]@{ work_item_id = 75; tags = 'PG-1'; capabilities = @('plannable', 'implementable'); children = @($child) }
        )
        $result = Group-ByPG -items $items
        $result['PG-1'].container_ids | Should -Contain 75
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

    It 'Accumulates multiple items in same PG' {
        $items = @(
            [pscustomobject]@{ work_item_id = 1; tags = 'PG-1'; capabilities = @('implementable') }
            [pscustomobject]@{ work_item_id = 2; tags = 'PG-1'; capabilities = @('implementable') }
            [pscustomobject]@{ work_item_id = 3; tags = 'PG-1'; capabilities = @('plannable') }
        )
        $result = Group-ByPG -items $items
        $result.Count | Should -Be 1
        $result['PG-1'].implementable_ids.Count | Should -Be 2
        $result['PG-1'].container_ids.Count | Should -Be 1
    }

    It 'Skips items with empty tags' {
        $items = @(
            [pscustomobject]@{ work_item_id = 99; tags = ''; capabilities = @('implementable') }
        )
        $result = Group-ByPG -items $items
        $result.Count | Should -Be 0
    }

    It 'Preserves insertion order (ordered hashtable)' {
        $items = @(
            [pscustomobject]@{ work_item_id = 1; tags = 'PG-3'; capabilities = @('implementable') }
            [pscustomobject]@{ work_item_id = 2; tags = 'PG-1'; capabilities = @('implementable') }
        )
        $result = Group-ByPG -items $items
        $keys = @($result.Keys)
        $keys[0] | Should -Be 'PG-3'
        $keys[1] | Should -Be 'PG-1'
    }

    It 'Does not use type-name checks' {
        $content = Get-Content $script:HelpersPath -Raw
        $content | Should -Not -Match "'Epic'|'Issue'|'Task'"
    }
}

# ── File-level acceptance criteria ────────────────────────────────────────────

Describe 'pg-helpers.ps1 acceptance' {

    It 'Is parseable PowerShell' {
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            $script:HelpersPath, [ref]$null, [ref]$errors)
        $errors.Count | Should -Be 0
    }

    It 'Is independently dot-sourceable (no external dependencies)' {
        # Re-dot-source in a clean scope to verify no dependency errors
        { . $script:HelpersPath } | Should -Not -Throw
    }

    It 'Exports Get-PGTag function' {
        Get-Command Get-PGTag | Should -Not -BeNullOrEmpty
    }

    It 'Exports Group-ByPG function' {
        Get-Command Group-ByPG | Should -Not -BeNullOrEmpty
    }
}
