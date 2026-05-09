BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-prose-children.ps1'
}

Describe 'lint-prose-children.ps1' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
            "lint-prose-children-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:PlansDir = Join-Path $script:TempRoot 'plans'
        New-Item $script:PlansDir -ItemType Directory -Force | Out-Null
    }

    AfterEach {
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Clean inputs (exit 0)' {

        It 'Passes when plans/ directory is missing entirely' {
            Remove-Item $script:PlansDir -Recurse -Force
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes when plans/ directory is empty' {
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes when plan has prose children AND apex_facets front-matter' {
            Set-Content (Join-Path $script:PlansDir 'plan-1.md') @'
---
apex_facets: [implementable]
---

# Apex 1

## Child Issues

- task-1 — narrative explanation
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes when plan has prose children AND structured children: front-matter' {
            Set-Content (Join-Path $script:PlansDir 'plan-2.md') @'
---
children:
  - id: 100
    title: First child
---

# Apex 2

## Child Items

- (narrative)
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes when plan has no prose-children section regardless of front-matter' {
            Set-Content (Join-Path $script:PlansDir 'plan-3.md') @'
# Apex 3

## Strategic Objective

Some prose.

## Risks

- Various.
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Does not match Childhood (word-boundary defense)' {
            Set-Content (Join-Path $script:PlansDir 'plan-4.md') @'
# Apex 4

## Childhood Influences

Prose only — Childhood is not Child(ren).
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Recognizes apex_facets even with extra leading whitespace' {
            Set-Content (Join-Path $script:PlansDir 'plan-5.md') @'
---
   apex_facets:
     - implementable
---

## Child Issues

- task-1
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Violations (exit 1)' {

        It 'Fails on prose children with no front-matter at all' {
            Set-Content (Join-Path $script:PlansDir 'bad-1.md') @'
# Apex bad-1

## Child Issues

- task-1 — would-be child but prose only.
- task-2 — same.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'bad-1\.md'
            ($output | Out-String) | Should -Match 'Child Issues'
        }

        It 'Fails on prose children with empty front-matter (no apex_facets, no children:)' {
            Set-Content (Join-Path $script:PlansDir 'bad-2.md') @'
---
title: Apex bad-2
---

## Children

- some-child
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'bad-2\.md'
        }

        It 'Reports every violating heading in a single file' {
            Set-Content (Join-Path $script:PlansDir 'bad-3.md') @'
# Apex bad-3

## Child Issues

- task-1

## Child Tasks

- task-2
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'Child Issues'
            $text | Should -Match 'Child Tasks'
        }

        It 'Aggregates violations across multiple files' {
            Set-Content (Join-Path $script:PlansDir 'bad-a.md') @'
## Child Issues
- task-1
'@
            Set-Content (Join-Path $script:PlansDir 'bad-b.md') @'
## Children
- task-2
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'bad-a\.md'
            $text | Should -Match 'bad-b\.md'
        }

        It 'Front-matter terms inside the body do not count as intent' {
            Set-Content (Join-Path $script:PlansDir 'bad-4.md') @'
# Apex bad-4

> apex_facets: [implementable]    -- this is body prose, not front matter

## Child Issues

- task-1
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'bad-4\.md'
        }

        It 'Headings inside the front matter do not trigger detection' {
            # Pathological case: a `---` line with `## Child …` between
            # the fences shouldn't fire because we only scan the body.
            Set-Content (Join-Path $script:PlansDir 'edge-1.md') @'
---
notes: |
  ## Child Issues (just a comment in front matter)
apex_facets: [implementable]
---

# Apex edge-1

Body has no prose-children section.
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'GitHub Actions output format' {

        It 'Emits ::error annotations under -Format github' {
            Set-Content (Join-Path $script:PlansDir 'gha.md') @'
# Apex gha

## Child Issues

- task-1
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot -Format github 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match '::error file=plans/gha\.md,line=\d+::'
            $text | Should -Match 'Child Issues'
        }
    }
}
