BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-psscriptroot-paths.ps1'
}

Describe 'lint-psscriptroot-paths.ps1' {

    BeforeEach {
        # Create a temp scripts/ directory structure for each test
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-path-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:ScriptsDir = Join-Path $script:TempRoot 'scripts'
        $script:TestsDir = Join-Path $script:TempRoot 'tests'
        New-Item $script:ScriptsDir -ItemType Directory -Force | Out-Null
        New-Item $script:TestsDir -ItemType Directory -Force | Out-Null

        # Copy lint script into temp tests/ so it resolves scripts/ via relative path
        Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1')
    }

    AfterEach {
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Clean scripts (exit 0)' {

        It 'Passes when all dot-sources use $PSScriptRoot' {
            Set-Content (Join-Path $script:ScriptsDir 'clean.ps1') @'
. "$PSScriptRoot/lib/helpers.ps1"
. "$PSScriptRoot/invoke-gh.ps1"
$x = 1
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes when no .ps1 files exist in scripts/' {
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores comment lines with absolute paths' {
            Set-Content (Join-Path $script:ScriptsDir 'commented.ps1') @'
# This file used to be at C:\old\path\script.ps1
# . "C:\hardcoded\helpers.ps1"
$x = 1
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores .Tests.ps1 files' {
            Set-Content (Join-Path $script:ScriptsDir 'router.Tests.ps1') @'
. "C:\hardcoded\helpers.ps1"
$json = '{"path":"C:\\absolute"}'
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes with Join-Path using $PSScriptRoot' {
            Set-Content (Join-Path $script:ScriptsDir 'joined.ps1') @'
$path = Join-Path $PSScriptRoot 'lib' 'helpers.ps1'
. $path
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes with $env:USERPROFILE (environment variable, not hardcoded)' {
            Set-Content (Join-Path $script:ScriptsDir 'envpath.ps1') @'
$outputDir = Join-Path $env:USERPROFILE ".twig/bin"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Dot-source violations (exit 1)' {

        It 'Fails when dot-source uses a relative path without $PSScriptRoot' {
            Set-Content (Join-Path $script:ScriptsDir 'bad-relative.ps1') @'
. "./lib/helpers.ps1"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when dot-source uses an absolute path' {
            Set-Content (Join-Path $script:ScriptsDir 'bad-absolute.ps1') @'
. "C:\scripts\lib\helpers.ps1"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }
    }

    Context 'Absolute path violations (exit 1)' {

        It 'Fails when a hardcoded Windows drive path is used in code' {
            Set-Content (Join-Path $script:ScriptsDir 'hardcoded-win.ps1') @'
$config = Get-Content "C:\config\settings.json"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when a Unix absolute path is used' {
            Set-Content (Join-Path $script:ScriptsDir 'hardcoded-unix.ps1') @'
$config = Get-Content "/home/user/.config/settings.json"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when a UNC path is used' {
            Set-Content (Join-Path $script:ScriptsDir 'hardcoded-unc.ps1') @'
$share = "\\Server\Share\file.txt"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }
    }

    Context 'Subdirectory scanning' {

        It 'Detects violations in scripts/lib/' {
            $libDir = Join-Path $script:ScriptsDir 'lib'
            New-Item $libDir -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $libDir 'bad-helper.ps1') @'
. "./other-helper.ps1"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Ignores .Tests.ps1 in subdirectories' {
            $libDir = Join-Path $script:ScriptsDir 'lib'
            New-Item $libDir -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $libDir 'helpers.Tests.ps1') @'
. "C:\test-fixtures\helpers.ps1"
'@
            $output = pwsh -NoProfile -File (Join-Path $script:TestsDir 'lint-psscriptroot-paths.ps1') 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }
}
