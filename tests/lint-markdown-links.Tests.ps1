BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-markdown-links.ps1'
}

Describe 'lint-markdown-links.ps1' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
            "lint-md-links-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:DocsDir = Join-Path $script:TempRoot 'docs'
        New-Item $script:DocsDir -ItemType Directory -Force | Out-Null
    }

    AfterEach {
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Clean inputs (exit 0)' {

        It 'Passes when docs/ directory is missing entirely' {
            Remove-Item $script:DocsDir -Recurse -Force
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes when docs/ directory is empty' {
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes for a doc with no links at all' {
            Set-Content (Join-Path $script:DocsDir 'clean.md') @'
# Clean Doc

Some prose with no links.

## Section Two

More prose.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes for valid relative file links' {
            New-Item (Join-Path $script:DocsDir 'sub') -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $script:DocsDir 'sub' 'target.md') '# Target'
            Set-Content (Join-Path $script:DocsDir 'source.md') @'
# Source

See [target](sub/target.md) for details.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes for valid anchor links within the same file' {
            Set-Content (Join-Path $script:DocsDir 'anchors.md') @'
# Top

See [section two](#section-two) below.

## Section Two

Content here.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes for HTTPS URLs (not validated for reachability)' {
            Set-Content (Join-Path $script:DocsDir 'urls.md') @'
# URLs

See [GitHub](https://github.com) and [PR #250](https://github.com/PolyphonyRequiem/polyphony/pull/250).
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes for valid file + anchor combination' {
            New-Item (Join-Path $script:DocsDir 'sub') -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $script:DocsDir 'sub' 'ref.md') @'
# Reference

## Details

Content.
'@
            Set-Content (Join-Path $script:DocsDir 'source.md') @'
# Source

See [details](sub/ref.md#details).
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Ignores links inside fenced code blocks' {
            Set-Content (Join-Path $script:DocsDir 'code.md') @'
# Code

```
See [broken](nonexistent.md) link inside code.
```

Normal prose.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes when -Files targets a file with valid links' {
            Set-Content (Join-Path $script:DocsDir 'good.md') @'
# Good

No links here.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot `
                -Files @('docs/good.md') 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }
    }

    Context 'Violations (exit 1)' {

        It 'Fails on broken relative file link' {
            Set-Content (Join-Path $script:DocsDir 'broken.md') @'
# Broken

See [missing](nonexistent.md) for info.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'nonexistent\.md'
            $text | Should -Match 'file not found'
        }

        It 'Fails on broken anchor link' {
            Set-Content (Join-Path $script:DocsDir 'bad-anchor.md') @'
# Doc

See [missing section](#does-not-exist).

## Real Section

Content.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'does-not-exist'
            $text | Should -Match 'anchor.*not found'
        }

        It 'Fails on file + anchor where anchor is wrong' {
            New-Item (Join-Path $script:DocsDir 'sub') -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $script:DocsDir 'sub' 'target.md') @'
# Target

## Existing Section

Content.
'@
            Set-Content (Join-Path $script:DocsDir 'source.md') @'
# Source

See [details](sub/target.md#wrong-anchor).
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'wrong-anchor'
        }

        It 'Reports line number for broken link' {
            Set-Content (Join-Path $script:DocsDir 'lines.md') @'
# Line Test

First line.

See [broken](missing.md) here.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'lines\.md:5'
        }

        It 'Reports multiple broken links in one file' {
            Set-Content (Join-Path $script:DocsDir 'multi.md') @'
# Multi

See [first](missing1.md) and [second](missing2.md).
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'missing1\.md'
            $text | Should -Match 'missing2\.md'
        }

        It 'Fails when -Files targets a nonexistent file' {
            $output = & $script:LintScript -RepoRoot $script:TempRoot `
                -Files @('docs/nope.md') 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'does not exist'
        }
    }

    Context 'Inbound link checking' {

        It 'Passes when inbound links resolve correctly' {
            New-Item (Join-Path $script:DocsDir 'troubleshooting') -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $script:DocsDir 'troubleshooting' 'target.md') '# Target'
            Set-Content (Join-Path $script:DocsDir 'index.md') @'
# Index

See [troubleshooting](troubleshooting/target.md) for help.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot `
                -InboundTarget 'troubleshooting/target.md' 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Fails when an inbound link points to a missing file' {
            Set-Content (Join-Path $script:DocsDir 'index.md') @'
# Index

See [troubleshooting](troubleshooting/missing.md) for help.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot `
                -InboundTarget 'troubleshooting/missing.md' 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'inbound link'
            $text | Should -Match 'file not found'
        }
    }

    Context 'GitHub Actions output format' {

        It 'Emits ::error annotations under -Format github' {
            Set-Content (Join-Path $script:DocsDir 'gha.md') @'
# GHA

See [broken](missing.md).
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot -Format github 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match '::error file=docs/gha\.md,line=\d+::'
        }
    }

    Context 'Real repo verification' {

        It 'launcher-gh-auth.md has no broken links' {
            # This test runs against the real repo to verify the doc
            $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
            $targetFile = 'docs/troubleshooting/launcher-gh-auth.md'
            $fullPath = Join-Path $repoRoot $targetFile

            if (-not (Test-Path $fullPath)) {
                Set-ItResult -Skipped -Because 'launcher-gh-auth.md not present on this branch'
                return
            }

            $output = & $script:LintScript -RepoRoot $repoRoot `
                -Files @($targetFile) `
                -InboundTarget 'troubleshooting/launcher-gh-auth.md' 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }
}
