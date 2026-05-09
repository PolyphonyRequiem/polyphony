<#
.SYNOPSIS
    Pester tests for tests/lint-lifecycle-router-coverage.ps1.

.DESCRIPTION
    Hermetic fixture-based tests. Constructs synthetic RequirementKind.cs
    and lifecycle-router.ps1 files in a temp directory and asserts the
    lint reports pass / fail / skip exactly as documented.
#>
[CmdletBinding()]
param()

BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-lifecycle-router-coverage.ps1'

    function script:New-Fixture {
        param(
            [string[]]$Kinds,
            [string]$RouterBody
        )

        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("lint-router-coverage-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $dir | Out-Null

        $kindsCs = New-Object System.Text.StringBuilder
        [void]$kindsCs.AppendLine('namespace Polyphony.Sdlc;')
        [void]$kindsCs.AppendLine('public static class RequirementKind {')
        foreach ($k in $Kinds) {
            $name = ($k -split '_' | ForEach-Object {
                if ($_.Length -eq 0) { '' }
                else { $_.Substring(0,1).ToUpper() + $_.Substring(1) }
            }) -join ''
            [void]$kindsCs.AppendLine("    public const string $name = `"$k`";")
        }
        [void]$kindsCs.AppendLine('}')

        $kindsPath  = Join-Path $dir 'RequirementKind.cs'
        $routerPath = Join-Path $dir 'lifecycle-router.ps1'
        Set-Content -LiteralPath $kindsPath  -Value $kindsCs.ToString() -NoNewline
        Set-Content -LiteralPath $routerPath -Value $RouterBody         -NoNewline

        return [PSCustomObject]@{
            Dir         = $dir
            KindsPath   = $kindsPath
            RouterPath  = $routerPath
        }
    }

    function script:Invoke-Lint {
        param(
            [PSCustomObject]$Fixture
        )
        $stdout = pwsh -NoProfile -File $script:LintScript `
            -RouterPath $Fixture.RouterPath `
            -RequirementKindPath $Fixture.KindsPath 2>&1
        return [PSCustomObject]@{
            Stdout   = ($stdout | Out-String)
            ExitCode = $LASTEXITCODE
        }
    }
}

Describe 'lint-lifecycle-router-coverage — pass cases' {

    It 'passes when every RequirementKind value is in one of the four kind arrays' {
        $fixture = script:New-Fixture `
            -Kinds @('plan_authored', 'action_satisfied', 'implementation_merged', 'item_satisfied') `
            -RouterBody @'
$planKinds = @('plan_authored')
$actionKinds = @('action_satisfied')
$implKinds = @('implementation_merged')
$terminalKinds = @('item_satisfied')
'@
        try {
            $r = script:Invoke-Lint -Fixture $fixture
            $r.ExitCode | Should -Be 0
            $r.Stdout   | Should -Match 'PASS: All 4 RequirementKind'
        } finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It 'passes when a kind is deliberately excluded via # router-skip: comment' {
        $fixture = script:New-Fixture `
            -Kinds @('plan_authored', 'computed_only_kind') `
            -RouterBody @'
# router-skip: computed_only_kind — reducer-derived only, never reported as `next`
$planKinds = @('plan_authored')
$actionKinds = @()
$implKinds = @()
$terminalKinds = @()
'@
        try {
            $r = script:Invoke-Lint -Fixture $fixture
            $r.ExitCode | Should -Be 0
            $r.Stdout   | Should -Match 'PASS:'
            $r.Stdout   | Should -Match "SKIP: ComputedOnlyKind = 'computed_only_kind'"
            $r.Stdout   | Should -Match 'reducer-derived only'
        } finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }
}

Describe 'lint-lifecycle-router-coverage — fail cases' {

    It 'fails when a fake RequirementKind value is added without router coverage' {
        # Mirrors the "introduce a fake new kind, assert lint flags missing
        # coverage" smoke test from the PR brief. The fixture lives entirely
        # in a temp dir — the real RequirementKind.cs is untouched.
        $fixture = script:New-Fixture `
            -Kinds @('plan_authored', 'fake_uncovered_kind') `
            -RouterBody @'
$planKinds = @('plan_authored')
$actionKinds = @()
$implKinds = @()
$terminalKinds = @()
'@
        try {
            $r = script:Invoke-Lint -Fixture $fixture
            $r.ExitCode | Should -Be 1
            $r.Stdout   | Should -Match "FakeUncoveredKind"
            $r.Stdout   | Should -Match "fake_uncovered_kind"
        } finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It 'fails with exit 2 when RequirementKind.cs cannot be found' {
        $r = pwsh -NoProfile -File $script:LintScript `
            -RouterPath $script:LintScript `
            -RequirementKindPath (Join-Path ([System.IO.Path]::GetTempPath()) 'does-not-exist-RequirementKind.cs') 2>&1
        $LASTEXITCODE | Should -Be 2
    }

    It 'fails with exit 2 when no RequirementKind values can be parsed (empty enum)' {
        $fixture = script:New-Fixture `
            -Kinds @() `
            -RouterBody @'
$planKinds = @()
$actionKinds = @()
$implKinds = @()
$terminalKinds = @()
'@
        try {
            $r = script:Invoke-Lint -Fixture $fixture
            $r.ExitCode | Should -Be 2
            $r.Stdout   | Should -Match 'no RequirementKind values parsed'
        } finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }

    It 'fails when the router docstring mentions a kind but the kind arrays do not' {
        # Docstring references must NOT count as coverage — the body's
        # array assignments are authoritative.
        $fixture = script:New-Fixture `
            -Kinds @('plan_authored') `
            -RouterBody @'
<#
.SYNOPSIS
    Mentions plan_authored in docs but does not include it in any array.
#>
$planKinds = @()
$actionKinds = @()
$implKinds = @()
$terminalKinds = @()
'@
        try {
            $r = script:Invoke-Lint -Fixture $fixture
            $r.ExitCode | Should -Be 1
        } finally {
            Remove-Item -Recurse -Force $fixture.Dir -ErrorAction SilentlyContinue
        }
    }
}

Describe 'lint-lifecycle-router-coverage — real repo' {

    It 'passes against the real RequirementKind.cs and lifecycle-router.ps1' {
        $stdout = pwsh -NoProfile -File $script:LintScript 2>&1
        $LASTEXITCODE | Should -Be 0 -Because (
            "the real lifecycle-router.ps1 must cover every RequirementKind value; output:`n$($stdout | Out-String)")
    }
}
