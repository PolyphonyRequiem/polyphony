BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-type-agnostic.ps1'
}

Describe 'lint-type-agnostic.ps1' {

    BeforeEach {
        # Pester mocks don't update $LASTEXITCODE; reset between tests so
        # leftover state from a prior test can't bleed into this one.
        $global:LASTEXITCODE = 0

        # Create a temp scripts/ directory structure for each test
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:ScriptsDir = Join-Path $script:TempRoot 'scripts'
        $script:TestsDir = Join-Path $script:TempRoot 'tests'
        $script:WorkflowsDir = Join-Path $script:TempRoot '.conductor' 'registry' 'workflows'
        New-Item $script:ScriptsDir -ItemType Directory -Force | Out-Null
        New-Item $script:TestsDir -ItemType Directory -Force | Out-Null
        New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
        $script:SkillsDir = Join-Path $script:TempRoot '.github' 'skills'
        $script:DocsDir = Join-Path $script:TempRoot 'docs'
        New-Item $script:SkillsDir -ItemType Directory -Force | Out-Null
        New-Item $script:DocsDir -ItemType Directory -Force | Out-Null

        # Copy lint script into temp tests/ so $PSScriptRoot/.. resolves to
        # the temp repo root (where scripts/ and .conductor/ live).
        Copy-Item $script:LintScript (Join-Path $script:TestsDir 'lint-type-agnostic.ps1')
        $script:TempLintScript = Join-Path $script:TestsDir 'lint-type-agnostic.ps1'
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
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes when no .ps1 files exist in scripts/' {
            # scripts/ directory exists but is empty
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores type names in comment lines' {
            Set-Content (Join-Path $script:ScriptsDir 'commented.ps1') @'
# Handle Epic items here
# Issue-as-task: plannable+implementable pattern
$x = 1
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores lowercase variants (feature, task, issue, bug)' {
            Set-Content (Join-Path $script:ScriptsDir 'lowercase.ps1') @'
$branch = "feature/$slug"
$task = Get-NextItem
$issue = $null
$bug = $false
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Ignores .Tests.ps1 files' {
            Set-Content (Join-Path $script:ScriptsDir 'router.Tests.ps1') @'
$json = '{"type":"Epic","state":"Doing"}'
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Violations detected (exit 1)' {

        It 'Fails when a script contains a quoted Epic literal' {
            Set-Content (Join-Path $script:ScriptsDir 'bad.ps1') @'
if ($item.Type -eq 'Epic') { $x = 1 }
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when a script contains Issue in a regex pattern' {
            Set-Content (Join-Path $script:ScriptsDir 'detect.ps1') @'
if ($content -match 'Issue\s*\|') { $matched = $true }
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails when a script contains User Story literal' {
            Set-Content (Join-Path $script:ScriptsDir 'check.ps1') @'
$types = @('User Story', 'Bug')
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails for Task used as a type comparison' {
            Set-Content (Join-Path $script:ScriptsDir 'route.ps1') @'
if ($item.Type -eq "Task") { Do-Something }
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails for Bug type literal' {
            Set-Content (Join-Path $script:ScriptsDir 'triage.ps1') @'
$isBug = $item.type -eq 'Bug'
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Fails for Feature type literal' {
            Set-Content (Join-Path $script:ScriptsDir 'classify.ps1') @'
$isFeature = $item.type -eq 'Feature'
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
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
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Ignores .Tests.ps1 in subdirectories' {
            $libDir = Join-Path $script:ScriptsDir 'lib'
            New-Item $libDir -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $libDir 'helpers.Tests.ps1') @'
$json = '{"type":"Epic"}'
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'YAML surface' {

        It 'Passes when no .conductor/registry/workflows directory exists' {
            Remove-Item $script:WorkflowsDir -Recurse -Force
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Detects type names in YAML body lines' {
            Set-Content (Join-Path $script:WorkflowsDir 'bad.yaml') @'
- name: router
  description: Routes Epic items to the right place
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Detects type names in YAML comment lines (no comment-skip for yaml)' {
            Set-Content (Join-Path $script:WorkflowsDir 'commented.yaml') @'
# This workflow handles Epic items
key: value
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Ignores type names in YAML comments via allowlist' {
            Set-Content (Join-Path $script:WorkflowsDir 'allowed.yaml') @'
# Feature branch creator (legitimate terminology)
key: value
'@
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files: []
allowed_substrings:
  - 'Feature\s+branch'
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Subdirectory YAMLs are scanned' {
            $sub = Join-Path $script:WorkflowsDir 'subdir'
            New-Item $sub -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $sub 'bad.yaml') @'
key: Epic value
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 1
        }
    }

    Context 'All surface (default)' {

        It 'Default Surface is all (scans both scripts and yaml)' {
            Set-Content (Join-Path $script:ScriptsDir 'clean.ps1') @'
$x = 1
'@
            Set-Content (Join-Path $script:WorkflowsDir 'bad.yaml') @'
key: Epic value
'@
            # No -Surface arg → default 'all' → must catch the yaml violation
            $output = pwsh -NoProfile -File $script:TempLintScript 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Passes when both surfaces are clean' {
            Set-Content (Join-Path $script:ScriptsDir 'clean.ps1') @'
$x = 1
'@
            Set-Content (Join-Path $script:WorkflowsDir 'clean.yaml') @'
key: value
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface all 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Default Surface all also catches skill violations' {
            Set-Content (Join-Path $script:SkillsDir 'bad-skill.md') @'
# Some Skill
This skill describes Epic handling.
'@
            $output = pwsh -NoProfile -File $script:TempLintScript 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'skills'
        }
    }

    Context 'Skills surface' {

        It 'Passes when no .github/skills directory exists' {
            Remove-Item $script:SkillsDir -Recurse -Force
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface skills 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Detects type names in skill markdown' {
            Set-Content (Join-Path $script:SkillsDir 'a-skill.md') @'
# A Skill
Use Issue handling here.
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface skills 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Recurses into nested skill subdirectories' {
            $sub = Join-Path $script:SkillsDir 'sub-skill' 'references'
            New-Item $sub -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $sub 'ref.md') @'
Discussion of Task semantics.
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface skills 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Honors file-level skip for a skill' {
            Set-Content (Join-Path $script:SkillsDir 'archival.md') @'
This intentionally describes Epic / Issue / Task vocabulary.
'@
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files:
  - '.github/skills/archival.md'
allowed_substrings: []
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface skills 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Docs surface' {

        It 'Passes when no docs directory exists' {
            Remove-Item $script:DocsDir -Recurse -Force
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface docs 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Detects type names in doc markdown when not skipped' {
            Set-Content (Join-Path $script:DocsDir 'design.md') @'
# Design

The system handles Epic items differently.
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface docs 2>&1
            $LASTEXITCODE | Should -Be 1
        }

        It 'Honors a directory-glob skip rule (docs/**)' {
            Set-Content (Join-Path $script:DocsDir 'reference.md') @'
The schema example shows Epic / Issue / Task hierarchies.
'@
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files:
  - 'docs/**'
allowed_substrings: []
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface docs 2>&1
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Allowlist mechanics' {

        It 'Missing allowlist file is OK (degrades to no skips)' {
            # No allowlist file in $script:TestsDir
            Set-Content (Join-Path $script:ScriptsDir 'clean.ps1') @'
$x = 1
'@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'skip_files glob excludes a YAML file from scanning' {
            Set-Content (Join-Path $script:WorkflowsDir 'noisy.yaml') @'
key: This file mentions Epic intentionally
'@
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files:
  - '.conductor/registry/workflows/noisy.yaml'
allowed_substrings: []
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Allowed substring is masked, but other type names on same line still fail' {
            # The line has both legit "Feature branch" and illegit "Issue".
            # Substring removal masks "Feature branch" — Issue must still trigger.
            Set-Content (Join-Path $script:WorkflowsDir 'mixed.yaml') @'
description: Feature branch for Issue creator
'@
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files: []
allowed_substrings:
  - 'Feature\s+branch'
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output -join "`n") | Should -Match 'Issue'
        }

        It 'Single-quoted YAML strings preserve regex backslashes' {
            # Validate the parser handles single-quoted YAML scalars correctly
            # — backslashes are literal in single-quoted YAML, so '\s+' stays
            # as the two-char regex-meaningful sequence. This guards against
            # the parser regression where `\\s` was passed through verbatim.
            Set-Content (Join-Path $script:WorkflowsDir 'whitespace.yaml') @'
description: Feature   branch with multiple spaces
'@
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files: []
allowed_substrings:
  - 'Feature\s+branch'
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface yaml 2>&1
            $LASTEXITCODE | Should -Be 0
        }

        It 'Allowlist with unknown section fails with exit 2' {
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files: []
mystery_section:
  - 'foo'
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 2
        }

        It 'Allowlist with malformed line fails with exit 2' {
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files: []
allowed_substrings:
  this-is-not-a-list-item
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 2
        }

        It 'Allowlist with invalid regex fails with exit 2' {
            Set-Content (Join-Path $script:TestsDir 'lint-type-agnostic.allowlist.yaml') @"
skip_files: []
allowed_substrings:
  - '['
"@
            $output = pwsh -NoProfile -File $script:TempLintScript -Surface scripts 2>&1
            $LASTEXITCODE | Should -Be 2
        }
    }
}

