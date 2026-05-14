BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-bare-cross-branch-refs.ps1'
}

Describe 'lint-bare-cross-branch-refs.ps1' {

    BeforeEach {
        $global:LASTEXITCODE = 0
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
            "lint-bare-refs-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:WorkflowsDir = Join-Path $script:TempRoot '.conductor/registry/workflows'
        $script:PromptsDir   = Join-Path $script:TempRoot '.conductor/registry/prompts'
        New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
        New-Item $script:PromptsDir   -ItemType Directory -Force | Out-Null
    }

    AfterEach {
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Negative cases — lint fails (exit 1)' {

        It 'Flags `git log A..B` with bare branch names' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Run: git log --oneline feature/3127..mg/3127_pg-3127
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'feature/3127'
            ($output | Out-String) | Should -Match 'mg/3127_pg-3127'
        }

        It 'Flags `git diff A...B` with bare templated branch names' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Run: git diff feature/X...mg/Y --stat
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'feature/X'
            ($output | Out-String) | Should -Match 'mg/Y'
        }

        It 'Flags `git rev-parse mg/...` (single-arg slashed ref)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Verify: git rev-parse mg/3127_pg-3127
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'mg/3127_pg-3127'
        }

        It 'Flags Jinja-templated bare ranges (the AB#3157 reproducer shape)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  git log --oneline {{ workflow.input.feature_branch }}..mg/{{ workflow.input.root_id }}_{{ workflow.input.mg_path }}
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'feature_branch'
            ($output | Out-String) | Should -Match 'mg/'
        }

        It 'Flags multi-line markdown wrapping where the range continues onto the next line' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  - The diff (`git diff
    {{ workflow.input.target_branch }}..{{ workflow.input.feature_branch }}`).
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'target_branch'
            ($output | Out-String) | Should -Match 'feature_branch'
        }

        It 'Flags violations in prompt MD files too' {
            Set-Content (Join-Path $script:PromptsDir 'reviewer.md') @'
# Reviewer

Run `git log --oneline feature/X..mg/Y` to see the new commits.
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'reviewer\.md'
        }

        It 'Emits ::error annotations under -Format github' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  git log --oneline feature/X..mg/Y
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot -Format github 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match '::error file=\.conductor/registry/workflows/wf\.yaml,line=\d+::'
        }

        It 'Reports the line number where the range operator actually appears (multi-line case)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Run this command:
  git diff
    feature/X..mg/Y
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            # The `..` sits on line 4 of the temp file (1: prompt:, 2: comment, 3: git diff, 4: ...).
            ($output | Out-String) | Should -Match 'wf\.yaml:4'
        }
    }

    Context 'Positive cases — lint passes (exit 0)' {

        It 'Passes `git log --oneline origin/A..origin/B` (the canonical fix shape)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Run: git log --oneline origin/feature/3127..origin/mg/3127_pg-3127
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes `git fetch origin feature/X mg/Y` (verb not in flagged set)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Run: git fetch origin feature/3127 mg/3127_pg-3127
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes `git log --oneline HEAD~10..HEAD` (allow-listed bare refs)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Recent: git log --oneline HEAD~10..HEAD
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes `git log --oneline main` (single-arg, no slashes, allow-listed)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Recent: git log --oneline main
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes `git rev-parse origin/mg/...` (slashed ref with origin/ prefix)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  Verify: git rev-parse --verify origin/mg/{{ workflow.input.root_id }}_{{ workflow.input.mg_path }}
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes `git rev-parse $localRef` (PowerShell variable inside script body)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
script: |
  $localRef = "refs/heads/$branch"
  $exists = (git rev-parse --verify --quiet $localRef 2>$null) -ne $null
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes `git update-ref refs/heads/X refs/remotes/origin/X` (verb not in flagged set)' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
script: |
  git update-ref refs/heads/main refs/remotes/origin/main
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Passes when no scoped directories exist' {
            Remove-Item $script:WorkflowsDir -Recurse -Force
            Remove-Item $script:PromptsDir   -Recurse -Force
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Passes when scoped directories are empty' {
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 0
            $output | Should -BeNullOrEmpty
        }

        It 'Allows file/line prose in markdown — no false positive on slashed prose tokens after a ranged command' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
prompt: |
  - `git diff origin/A...origin/B --stat`
    for an aggregate file/line summary.
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Does not flag the verb in markdown prose like "git history"' {
            Set-Content (Join-Path $script:PromptsDir 'r.md') @'
- Examine git history via the gh tool.
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Whitelist marker (# bare-ref-ok)' {

        It 'Suppresses violation with same-line bare-ref-ok marker' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
script: |
  git log feature/X..mg/Y  # bare-ref-ok: reading the worktree's own branch
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Suppresses violation with preceding-line bare-ref-ok marker' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
script: |
  # bare-ref-ok: reading the worktree's own branch (HEAD already points here)
  git log feature/X..mg/Y
'@
            & $script:LintScript -RepoRoot $script:TempRoot
            $LASTEXITCODE | Should -Be 0
        }

        It 'Does NOT suppress when marker is two lines away' {
            Set-Content (Join-Path $script:WorkflowsDir 'wf.yaml') @'
script: |
  # bare-ref-ok: stale marker, two lines away
  $foo = 'bar'
  git log feature/X..mg/Y
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            ($output | Out-String) | Should -Match 'feature/X'
        }
    }

    Context 'Aggregation' {

        It 'Reports every violation across multiple files' {
            Set-Content (Join-Path $script:WorkflowsDir 'a.yaml') @'
prompt: |
  git log feature/X..mg/Y
'@
            Set-Content (Join-Path $script:WorkflowsDir 'b.yaml') @'
prompt: |
  git diff feature/A...mg/B
'@
            $output = & $script:LintScript -RepoRoot $script:TempRoot 2>&1
            $LASTEXITCODE | Should -Be 1
            $text = $output | Out-String
            $text | Should -Match 'a\.yaml'
            $text | Should -Match 'b\.yaml'
        }
    }
}
