BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-reset-docs.ps1'
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
}

Describe 'lint-reset-docs.ps1' {

    Context 'Real repo validation (AB#3180 acceptance criteria)' {

        It 'Passes all structural checks against the live repo' {
            $output = & $script:LintScript -RepoRoot $script:RepoRoot 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Host ($output | Out-String)
            }
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Detects missing documentation' {

        BeforeEach {
            $global:LASTEXITCODE = 0
            $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
                "lint-reset-docs-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            New-Item (Join-Path $script:TempRoot 'docs') -ItemType Directory -Force | Out-Null
        }

        AfterEach {
            Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'Fails when polyphony-reset.md is missing' {
            # Create minimal stubs for other docs to isolate the failure
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-cli-reference.md') 'polyphony reset scrub'
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-skills-index.md') 'polyphony-reset'
            Set-Content (Join-Path $script:TempRoot 'docs/glossary.md') "polyphony reset`nPolyphonyTag DU"
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-state-effects-catalog.md') 'polyphony reset'

            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'polyphony-reset\.md.*file not found'
        }

        It 'Fails when polyphony-reset.md is missing required sections' {
            # Create a minimal reset doc missing the remediation pattern
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-reset.md') @'
# polyphony reset

## Synopsis

polyphony reset --root-id N --force --dry-run

## Scrub scope

### ADO tags

feature/ worktree branches

comment-archive.json sidecar

PolyphonyTag DU

branch-model polyphony-tags per-run-worktree-layout

JSON output contract

confirm gate
'@
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-cli-reference.md') 'polyphony reset scrub'
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-skills-index.md') 'polyphony-reset'
            Set-Content (Join-Path $script:TempRoot 'docs/glossary.md') "polyphony reset`nPolyphonyTag DU"
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-state-effects-catalog.md') 'polyphony reset'

            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'remediation pattern'
        }

        It 'Fails when CLI reference does not mention reset' {
            # Create full reset doc but empty CLI reference
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-reset.md') @'
# polyphony reset

## Scrub scope

### ADO tags

feature/ worktree branches

--root-id --force --dry-run

comment-archive.json sidecar

## The remediation pattern

PolyphonyTag DU

branch-model polyphony-tags per-run-worktree-layout

JSON output contract

confirm gate
'@
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-cli-reference.md') 'some other verb'
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-skills-index.md') 'polyphony-reset'
            Set-Content (Join-Path $script:TempRoot 'docs/glossary.md') "polyphony reset`nPolyphonyTag DU"
            Set-Content (Join-Path $script:TempRoot 'docs/polyphony-state-effects-catalog.md') 'polyphony reset'

            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'polyphony-cli-reference\.md'
        }
    }
}
