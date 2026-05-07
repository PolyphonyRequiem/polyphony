BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-conductor-validate.ps1'
}

Describe 'lint-conductor-validate.ps1' {

    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-conductor-validate-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:WorkflowsDir = Join-Path $script:TempRoot '.conductor' 'registry' 'workflows'
        $script:TestsDir = Join-Path $script:TempRoot 'tests'
        $script:MockBinDir = Join-Path $script:TempRoot 'mockbin'
        New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
        New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
        New-Item $script:MockBinDir -ItemType Directory -Force | Out-Null

        # Copy lint script into temp tests/ so it resolves workflows/ via relative path
        Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-conductor-validate.ps1')

        # Create platform-specific mock conductor that uses MOCK_FAIL marker
        if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            Set-Content (Join-Path $script:MockBinDir 'conductor.cmd') @'
@echo off
if not "%1"=="validate" (
    echo conductor: unknown command %1 1>&2
    exit /b 1
)
if "%~2"=="" (
    echo conductor: no file specified 1>&2
    exit /b 1
)
findstr /C:"MOCK_FAIL" "%~2" >nul 2>&1
if not errorlevel 1 (
    echo Error: validation failed for %~2 1>&2
    exit /b 1
)
echo OK: %~2
exit /b 0
'@
        } else {
            $mockPath = Join-Path $script:MockBinDir 'conductor'
            Set-Content $mockPath @'
#!/bin/sh
if [ "$1" != "validate" ]; then echo "conductor: unknown command $1" >&2; exit 1; fi
if [ -z "$2" ]; then echo "conductor: no file specified" >&2; exit 1; fi
if grep -q "MOCK_FAIL" "$2" 2>/dev/null; then echo "Error: validation failed for $2" >&2; exit 1; fi
echo "OK: $2"
exit 0
'@
            chmod +x $mockPath
        }

        # Prepend mock bin to PATH so lint script finds mock conductor
        $script:OriginalPath = $env:PATH
        $env:PATH = "$($script:MockBinDir)$([System.IO.Path]::PathSeparator)$($env:PATH)"
    }

    AfterEach {
        $env:PATH = $script:OriginalPath
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'All YAMLs valid (exit 0)' {

        It 'Passes when all 9 YAMLs pass conductor validate' {
            $yamlNames = @(
                'twig-sdlc-v2-full.yaml', 'twig-sdlc-v2-planning.yaml', 'twig-sdlc-v2-implement.yaml',
                'plan-level.yaml', 'implement-pg.yaml', 'feature-pr.yaml',
                'github-pr.yaml', 'ado-pr.yaml', 'close-out.yaml'
            )
            foreach ($name in $yamlNames) {
                Set-Content (Join-Path $script:WorkflowsDir $name) "workflow: { name: $($name -replace '\.yaml$','') }"
            }
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-conductor-validate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Reports per-YAML pass status in output' {
            $yamlNames = @('first.yaml', 'second.yaml', 'third.yaml')
            foreach ($name in $yamlNames) {
                Set-Content (Join-Path $script:WorkflowsDir $name) "workflow: { name: test }"
            }
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-conductor-validate.ps1') 2>&1
            $text = $output | Out-String
            $text | Should -Match 'PASS.*first\.yaml'
            $text | Should -Match 'PASS.*second\.yaml'
            $text | Should -Match 'PASS.*third\.yaml'
        }
    }

    Context 'Failures detected (exit 1)' {

        It 'Fails when a YAML has invalid syntax (malformed YAML)' {
            Set-Content (Join-Path $script:WorkflowsDir 'valid.yaml') "workflow: { name: valid }"
            Set-Content (Join-Path $script:WorkflowsDir 'malformed.yaml') "MOCK_FAIL: malformed yaml content { {{"
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-conductor-validate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when a YAML references a non-existent agent' {
            Set-Content (Join-Path $script:WorkflowsDir 'bad-ref.yaml') "MOCK_FAIL: references non-existent agent"
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-conductor-validate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Output identifies which specific YAML(s) failed' {
            Set-Content (Join-Path $script:WorkflowsDir 'good.yaml') "workflow: { name: good }"
            Set-Content (Join-Path $script:WorkflowsDir 'broken.yaml') "MOCK_FAIL: invalid"
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-conductor-validate.ps1') 2>&1
            $text = $output | Out-String
            $text | Should -Match 'FAIL.*broken\.yaml'
            $text | Should -Match 'PASS.*good\.yaml'
        }
    }

    Context 'Edge cases' {

        It 'Fails (exit 1) when workflows directory exists but is empty' {
            # Empty workflows dir is a regression — silent SKIP would mask real bugs.
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-conductor-validate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'FAIL.*No \.yaml files'
        }

        It 'Fails (exit 1) when workflows directory is missing entirely' {
            Remove-Item $script:WorkflowsDir -Recurse -Force
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-conductor-validate.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'FAIL.*directory not found'
        }
    }
}
