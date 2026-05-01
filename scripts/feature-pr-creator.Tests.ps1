BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'feature-pr-creator.ps1'

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

Describe 'feature-pr-creator.ps1 — successful PR creation' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { 'abc123  refs/heads/feature/42-test' } -ParameterFilter {
            $args -contains 'ls-remote'
        }
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'list'
        }
        Mock gh {
            $global:LASTEXITCODE = 0
            'https://github.com/owner/repo/pull/42'
        } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'create'
        }
        Mock polyphony { $null }
        Mock twig { $null }
    }

    It 'Creates PR and returns valid JSON with pr_number' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.pr_number | Should -Be 42
        $result.created | Should -BeTrue
    }

    It 'Returns pr_url from gh pr create output' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.pr_url | Should -Be 'https://github.com/owner/repo/pull/42'
    }

    It 'Auto-generates title when none provided' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.title | Should -BeLike 'feat: deliver work item*'
    }

    It 'Uses explicit --Title when provided' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' -Title 'Custom PR title' | ConvertFrom-Json
        $result.title | Should -Be 'Custom PR title'
    }
}

Describe 'feature-pr-creator.ps1 — title from twig tree' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { 'abc123  refs/heads/feature/42-epic' } -ParameterFilter {
            $args -contains 'ls-remote'
        }
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'list'
        }
        Mock gh {
            $global:LASTEXITCODE = 0
            'https://github.com/owner/repo/pull/99'
        } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'create'
        }
        Mock polyphony { $null }
        Mock twig {
            '{"title":"My Epic Feature","id":42}'
        }
    }

    It 'Uses twig tree title when no explicit title' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-epic' -TargetBranch 'main' | ConvertFrom-Json
        $result.title | Should -Be 'feat: My Epic Feature AB#42'
    }

    It 'Prefers explicit title over twig tree' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-epic' -TargetBranch 'main' -Title 'Override title' | ConvertFrom-Json
        $result.title | Should -Be 'Override title'
    }
}

Describe 'feature-pr-creator.ps1 — existing PR reuse' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { 'abc123  refs/heads/feature/42-test' } -ParameterFilter {
            $args -contains 'ls-remote'
        }
        Mock gh {
            $global:LASTEXITCODE = 0
            '[{"number":55,"url":"https://github.com/owner/repo/pull/55"}]'
        } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'list'
        }
        Mock polyphony { $null }
        Mock twig { $null }
    }

    It 'Reuses existing open PR instead of creating new one' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.pr_number | Should -Be 55
        $result.created | Should -BeFalse
    }

    It 'Returns correct pr_url for existing PR' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.pr_url | Should -Be 'https://github.com/owner/repo/pull/55'
    }

    It 'Includes description_summary indicating reuse' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.description_summary | Should -BeLike '*Reusing*'
    }
}

Describe 'feature-pr-creator.ps1 — branch does not exist' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { } -ParameterFilter { $args -contains 'ls-remote' }
        Mock polyphony { $null }
        Mock twig { $null }
    }

    It 'Exits non-zero when feature branch does not exist on remote' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/nonexistent' -TargetBranch 'main' | ConvertFrom-Json
        $result.created | Should -BeFalse
        $result.error | Should -BeLike "*does not exist*"
        $LASTEXITCODE | Should -Be 1
    }
}

Describe 'feature-pr-creator.ps1 — gh pr create failure' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { 'abc123  refs/heads/feature/42-test' } -ParameterFilter {
            $args -contains 'ls-remote'
        }
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'list'
        }
        Mock gh {
            $global:LASTEXITCODE = 1
            $null
        } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'create'
        }
        Mock polyphony { $null }
        Mock twig { $null }
    }

    It 'Exits non-zero when gh pr create returns no URL' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.created | Should -BeFalse
        $result.pr_number | Should -Be 0
        $result.error | Should -BeLike "*failed*"
        $LASTEXITCODE | Should -Be 1
    }
}

Describe 'feature-pr-creator.ps1 — workspace_hint validation' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { 'abc123  refs/heads/feature/42-test' } -ParameterFilter {
            $args -contains 'ls-remote'
        }
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'list'
        }
        Mock gh {
            $global:LASTEXITCODE = 0
            'https://github.com/owner/repo/pull/77'
        } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'create'
        }
        Mock twig { $null }
    }

    It 'Succeeds when workspace_hint feature_branch matches' {
        Mock polyphony {
            '{"workspace_hint":{"feature_branch":"feature/42-test","pg_branch":"pg-{n}/42-test"}}'
        }
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.created | Should -BeTrue
        $result.pr_number | Should -Be 77
    }

    It 'Warns but continues when workspace_hint differs from supplied branch' {
        Mock polyphony {
            '{"workspace_hint":{"feature_branch":"feature/42-other","pg_branch":"pg-{n}/42-other"}}'
        }
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' 3>&1
        $warnings = @($result | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
        $warnings.Count | Should -BeGreaterThan 0

        $json = @($result | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] }) -join ''
        $parsed = $json | ConvertFrom-Json
        $parsed.created | Should -BeTrue
    }

    It 'Continues gracefully when polyphony is unavailable' {
        Mock polyphony { throw 'polyphony not found' }
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.created | Should -BeTrue
    }
}

Describe 'feature-pr-creator.ps1 — output shape' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { 'abc123  refs/heads/feature/42-test' } -ParameterFilter {
            $args -contains 'ls-remote'
        }
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'list'
        }
        Mock gh {
            $global:LASTEXITCODE = 0
            'https://github.com/owner/repo/pull/10'
        } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'create'
        }
        Mock polyphony { $null }
        Mock twig { $null }
    }

    It 'Returns valid JSON with all required fields' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'pr_number'
        $result.PSObject.Properties.Name | Should -Contain 'pr_url'
        $result.PSObject.Properties.Name | Should -Contain 'created'
        $result.PSObject.Properties.Name | Should -Contain 'title'
        $result.PSObject.Properties.Name | Should -Contain 'description_summary'
    }

    It 'pr_number is numeric' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.pr_number | Should -BeOfType [long]
    }

    It 'created is boolean' {
        $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
        $result.created | Should -BeOfType [bool]
    }
}

# ── Output schema compatibility verification (#2779) ─────────────────────────

Describe 'feature-pr-creator.ps1 — output schema compatibility (#2779)' {

    BeforeEach {
        $env:GH_TOKEN = 'test-token'
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter { $args -contains 'auth' }
        Mock git { 'https://github.com/owner/repo.git' } -ParameterFilter {
            $args -contains 'remote'
        }
        Mock git { 'abc123  refs/heads/feature/42-test' } -ParameterFilter {
            $args -contains 'ls-remote'
        }
        Mock gh { $global:LASTEXITCODE = 0; '' } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'list'
        }
        Mock gh {
            $global:LASTEXITCODE = 0
            'https://github.com/owner/repo/pull/42'
        } -ParameterFilter {
            $args -contains 'pr' -and $args -contains 'create'
        }
        Mock polyphony { $null }
        Mock twig { $null }
    }

    Context 'Required schema keys — all 5 from feature-pr.yaml' {

        It 'Contains all 5 required top-level keys' {
            $requiredKeys = @(
                'pr_number', 'pr_url', 'title', 'description_summary', 'created'
            )

            $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
            $outputKeys = $result.PSObject.Properties.Name

            foreach ($key in $requiredKeys) {
                $outputKeys | Should -Contain $key -Because "required key '$key' must be present"
            }
        }

        It 'Uses correct value types for each key' {
            $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json

            # Integer — used by feature-pr.yaml: {{ feature_pr_creator.output.pr_number }}
            $result.pr_number | Should -BeOfType [long]

            # Strings — used by feature-pr.yaml: {{ feature_pr_creator.output.pr_url }}
            $result.pr_url | Should -BeOfType [string]
            $result.title | Should -BeOfType [string]
            $result.description_summary | Should -BeOfType [string]

            # Boolean
            $result.created | Should -BeOfType [bool]
        }
    }

    Context 'pr_url field compatibility — feature-pr.yaml workflow output' {

        It 'Returns non-empty pr_url on successful creation' {
            $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
            $result.pr_url | Should -Not -BeNullOrEmpty
        }

        It 'Returns pr_url for reused existing PR' {
            Mock gh {
                $global:LASTEXITCODE = 0
                '[{"number":55,"url":"https://github.com/owner/repo/pull/55"}]'
            } -ParameterFilter {
                $args -contains 'pr' -and $args -contains 'list'
            }

            $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/42-test' -TargetBranch 'main' | ConvertFrom-Json
            $result.pr_url | Should -Not -BeNullOrEmpty
            $result.pr_number | Should -Be 55
        }
    }

    Context 'Error path — maintains schema on failure' {

        It 'Error output preserves all required top-level keys' {
            Mock git { } -ParameterFilter { $args -contains 'ls-remote' }

            $result = & $script:ScriptPath -WorkItemId 42 -FeatureBranch 'feature/nonexistent' -TargetBranch 'main' | ConvertFrom-Json
            $requiredKeys = @('pr_number', 'pr_url', 'created')
            foreach ($key in $requiredKeys) {
                $result.PSObject.Properties.Name | Should -Contain $key -Because "error path must preserve key '$key'"
            }
        }
    }
}
