BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-type-agnostic.ps1'
}

Describe 'lint-type-agnostic.ps1' {

    BeforeEach {
        # Create a temp scripts/ directory structure for each test
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:ScriptsDir = Join-Path $script:TempRoot 'scripts'
        $script:TestsDir = Join-Path $script:TempRoot 'tests'
        New-Item $script:ScriptsDir -ItemType Directory -Force | Out-Null
        New-Item $script:TestsDir -ItemType Directory -Force | Out-Null

        # Copy lint script into temp tests/ so it resolves scripts/ via relative path
        Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-type-agnostic.ps1')
    }

    AfterEach {
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Clean scripts (exit 0)' {

        It 'Passes when scripts use facets instead of type names' {
            Set-Content (Join-Path $script:ScriptsDir 'clean.ps1') @'
$isImplementable = $item.facets -contains 'implementable'
$isContainer = $item.facets -contains 'plannable'
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes when no .ps1 files exist in scripts/' {
            # scripts/ directory exists but is empty
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores type names in comment lines' {
            Set-Content (Join-Path $script:ScriptsDir 'commented.ps1') @'
# Handle Epic items here
# Issue-as-task: plannable+implementable pattern
$x = 1
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores lowercase variants (feature, task, issue, bug)' {
            Set-Content (Join-Path $script:ScriptsDir 'lowercase.ps1') @'
$branch = "feature/$slug"
$task = Get-NextItem
$issue = $null
$bug = $false
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores .Tests.ps1 files' {
            Set-Content (Join-Path $script:ScriptsDir 'router.Tests.ps1') @'
$json = '{"type":"Epic","state":"Doing"}'
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Violations detected (exit 1)' {

        It 'Fails when a script contains a quoted Epic literal' {
            Set-Content (Join-Path $script:ScriptsDir 'bad.ps1') @'
if ($item.Type -eq 'Epic') { $x = 1 }
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when a script contains Issue in a regex pattern' {
            Set-Content (Join-Path $script:ScriptsDir 'detect.ps1') @'
if ($content -match 'Issue\s*\|') { $matched = $true }
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when a script contains User Story literal' {
            Set-Content (Join-Path $script:ScriptsDir 'check.ps1') @'
$types = @('User Story', 'Bug')
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails for Task used as a type comparison' {
            Set-Content (Join-Path $script:ScriptsDir 'route.ps1') @'
if ($item.Type -eq "Task") { Do-Something }
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails for Bug type literal' {
            Set-Content (Join-Path $script:ScriptsDir 'triage.ps1') @'
$isBug = $item.type -eq 'Bug'
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails for Feature type literal' {
            Set-Content (Join-Path $script:ScriptsDir 'classify.ps1') @'
$isFeature = $item.type -eq 'Feature'
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }
    }

    Context 'Subdirectory scanning' {

        It 'Detects violations in scripts/lib/' {
            $libDir = Join-Path $script:ScriptsDir 'lib'
            New-Item $libDir -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $libDir 'helpers.ps1') @'
$isEpic = $node.type -eq 'Epic'
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Ignores .Tests.ps1 in subdirectories' {
            $libDir = Join-Path $script:ScriptsDir 'lib'
            New-Item $libDir -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $libDir 'helpers.Tests.ps1') @'
$json = '{"type":"Epic"}'
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-type-agnostic.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }
}

