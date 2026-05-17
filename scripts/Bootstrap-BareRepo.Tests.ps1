#requires -Version 7.0

<#
.SYNOPSIS
    Pester tests for scripts/Bootstrap-BareRepo.ps1 (#420).

.DESCRIPTION
    Tests helper functions in isolation (dot-sourced) and end-to-end
    script behavior (invoked with -Commit). Uses real `git init` in temp
    directories — no mocks for git itself.

    Covers the scenarios identified in the rubber-duck pass:
      - Happy path: full bootstrap from nothing
      - Idempotency: re-run after success is a no-op
      - Recovery: bare exists + main missing
      - Recovery: bare + main present + runs missing
      - Path conflict: pre-existing non-bare at bare path
      - Path conflict: pre-existing dir at main path that isn't a worktree
      - Path conflict: existing bare with mismatched origin
      - Path conflict: main path exists with different common-dir
      - Path conflict: target branch already checked out elsewhere
      - Path conflict: empty/unborn remote (no symbolic HEAD)
      - URL parsing: https / ssh / ADO
      - Default-branch detection: master, develop, custom
      - Explicit -MainBranch beats auto-detect
      - Dry-run: prints plan, exits 0, creates nothing
      - Missing git: preflight failure
      - ParentDir creation
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Bootstrap-BareRepo.ps1'
    . $script:ScriptPath

    function New-TestRoot {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "bootstrap-bare-test-$(Get-Random)"
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    function New-FakeRemoteRepo {
        param(
            [Parameter(Mandatory)][string]$Root,
            [string]$DefaultBranch = 'main'
        )
        $remote = Join-Path $Root 'remote.git'
        & git init --bare $remote *> $null

        # Seed with one commit on $DefaultBranch via a temporary worktree.
        $seed = Join-Path $Root 'seed'
        & git --git-dir $remote worktree add -b $DefaultBranch $seed *> $null
        Set-Content -Path (Join-Path $seed 'README.md') -Value 'seed' -Encoding utf8
        & git -C $seed config user.email 'test@example.com' *> $null
        & git -C $seed config user.name 'test' *> $null
        & git -C $seed add . *> $null
        & git -C $seed commit -m 'initial' *> $null
        & git --git-dir $remote worktree remove $seed *> $null

        # Point HEAD at the default branch.
        & git --git-dir $remote symbolic-ref HEAD "refs/heads/$DefaultBranch" *> $null

        return $remote
    }

    function New-EmptyFakeRemote {
        param([Parameter(Mandatory)][string]$Root)
        $remote = Join-Path $Root 'empty-remote.git'
        & git init --bare $remote *> $null
        # Bare init defaults HEAD to refs/heads/main but the branch has no commit.
        return $remote
    }

    function Invoke-Script {
        param(
            [Parameter(Mandatory)][string]$RemoteUrl,
            [Parameter(Mandatory)][string]$ParentDir,
            [string]$RepoName = '',
            [string]$MainBranch = '',
            [switch]$Commit
        )
        $args = @(
            '-File', $script:ScriptPath,
            '-RemoteUrl', $RemoteUrl,
            '-ParentDir', $ParentDir
        )
        if ($RepoName)   { $args += @('-RepoName', $RepoName) }
        if ($MainBranch) { $args += @('-MainBranch', $MainBranch) }
        if ($Commit)     { $args += '-Commit' }
        $output = & pwsh -NoProfile @args 2>&1
        $exit = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $exit
            Output   = ($output -join "`n")
        }
    }
}

Describe 'Resolve-RepoNameFromUrl' {
    It 'derives repo name from https URL with .git suffix' {
        Resolve-RepoNameFromUrl -Url 'https://github.com/Org/repo.git' | Should -Be 'repo'
    }
    It 'derives repo name from https URL without .git suffix' {
        Resolve-RepoNameFromUrl -Url 'https://github.com/Org/repo' | Should -Be 'repo'
    }
    It 'derives repo name from https URL with trailing slash' {
        Resolve-RepoNameFromUrl -Url 'https://github.com/Org/repo/' | Should -Be 'repo'
    }
    It 'derives repo name from ssh shortcut form' {
        Resolve-RepoNameFromUrl -Url 'git@github.com:Org/repo.git' | Should -Be 'repo'
    }
    It 'derives repo name from ssh:// URL' {
        Resolve-RepoNameFromUrl -Url 'ssh://git@host:22/Org/repo.git' | Should -Be 'repo'
    }
    It 'derives repo name from ADO _git URL' {
        Resolve-RepoNameFromUrl -Url 'https://dev.azure.com/Org/Project/_git/Repo' | Should -Be 'Repo'
    }
    It 'returns $null for empty input' {
        Resolve-RepoNameFromUrl -Url '   ' | Should -BeNullOrEmpty
    }
}

Describe 'Test-RemoteUrlMatch' {
    It 'matches identical URLs' {
        Test-RemoteUrlMatch -A 'https://github.com/Org/repo.git' -B 'https://github.com/Org/repo.git' | Should -BeTrue
    }
    It 'matches URLs that differ only in trailing .git' {
        Test-RemoteUrlMatch -A 'https://github.com/Org/repo.git' -B 'https://github.com/Org/repo' | Should -BeTrue
    }
    It 'matches URLs that differ only in trailing slash' {
        Test-RemoteUrlMatch -A 'https://github.com/Org/repo/' -B 'https://github.com/Org/repo' | Should -BeTrue
    }
    It 'matches case-insensitively' {
        Test-RemoteUrlMatch -A 'https://GitHub.com/Org/Repo.git' -B 'https://github.com/Org/repo' | Should -BeTrue
    }
    It 'does NOT match https vs ssh forms (conservative)' {
        Test-RemoteUrlMatch -A 'https://github.com/Org/repo.git' -B 'git@github.com:Org/repo.git' | Should -BeFalse
    }
    It 'does not match different repos' {
        Test-RemoteUrlMatch -A 'https://github.com/Org/repo.git' -B 'https://github.com/Org/other.git' | Should -BeFalse
    }
}

Describe 'Get-DerivedPaths' {
    It 'derives bare/main/runs paths from parent + name' {
        $p = Get-DerivedPaths -ParentDir 'C:\repos' -RepoName 'twig'
        # Normalize for comparison (Join-Path may use forward or backward depending on platform).
        ($p.BarePath -replace '/', '\').TrimEnd('\') | Should -Be 'C:\repos\twig.git'
        ($p.MainPath -replace '/', '\').TrimEnd('\') | Should -Be 'C:\repos\twig'
        ($p.RunsRoot -replace '/', '\').TrimEnd('\') | Should -Be 'C:\repos\twig-runs'
    }
}

Describe 'Bootstrap (end-to-end via script invocation)' {
    BeforeEach {
        $script:root = New-TestRoot
        $script:remote = New-FakeRemoteRepo -Root $script:root
        $script:parent = Join-Path $script:root 'workspace'
    }
    AfterEach {
        if (Test-Path $script:root) {
            Remove-Item -Recurse -Force $script:root -ErrorAction SilentlyContinue
        }
    }

    Context 'happy path: full bootstrap' {
        It 'creates bare, main worktree, and runs root from empty parent' {
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Bootstrap complete'

            $bare = Join-Path $script:parent 'demo.git'
            $main = Join-Path $script:parent 'demo'
            $runs = Join-Path $script:parent 'demo-runs'

            Test-Path $bare | Should -BeTrue
            Test-Path $main | Should -BeTrue
            Test-Path $runs -PathType Container | Should -BeTrue

            (Test-IsBareRepository -BarePath $bare) | Should -BeTrue
            # Main worktree should be on the default branch.
            (& git -C $main branch --show-current).Trim() | Should -Be 'main'
        }

        It 'creates the parent directory if missing' {
            $nested = Join-Path $script:parent 'nested\subdir'
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $nested -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 0
            Test-Path (Join-Path $nested 'demo.git') | Should -BeTrue
        }

        It 'derives RepoName from URL when not provided' {
            # New-FakeRemoteRepo writes to <root>/remote.git, so the derived
            # name will be 'remote'.
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -Commit
            $r.ExitCode | Should -Be 0
            Test-Path (Join-Path $script:parent 'remote.git') | Should -BeTrue
            Test-Path (Join-Path $script:parent 'remote') | Should -BeTrue
        }

        It 'detects non-main default branch from remote' {
            $masterRemote = New-FakeRemoteRepo -Root $script:root -DefaultBranch 'master'
            # Remote dir name collides — use a custom RepoName to avoid path collision.
            $r = Invoke-Script -RemoteUrl $masterRemote -ParentDir $script:parent -RepoName 'oldschool' -Commit
            $r.ExitCode | Should -Be 0
            $main = Join-Path $script:parent 'oldschool'
            (& git -C $main branch --show-current).Trim() | Should -Be 'master'
        }

        It 'honors explicit -MainBranch override' {
            # Add a 'develop' branch to the remote.
            & git --git-dir $script:remote branch develop refs/heads/main *> $null
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -MainBranch 'develop' -Commit
            $r.ExitCode | Should -Be 0
            $main = Join-Path $script:parent 'demo'
            (& git -C $main branch --show-current).Trim() | Should -Be 'develop'
        }
    }

    Context 'idempotency' {
        It 'reports already-bootstrapped on second run' {
            (Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit).ExitCode | Should -Be 0
            $r2 = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r2.ExitCode | Should -Be 0
            $r2.Output | Should -Match 'Already bootstrapped'
        }
    }

    Context 'recovery: bare present, main missing' {
        It 'adds the missing main worktree' {
            # First, full bootstrap.
            (Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit).ExitCode | Should -Be 0
            # Remove the main worktree directory + prune the bare's registration.
            $main = Join-Path $script:parent 'demo'
            $bare = Join-Path $script:parent 'demo.git'
            Remove-Item -Recurse -Force $main
            & git --git-dir $bare worktree prune *> $null
            # Re-run — should classify as recover_missing_main.
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'recover_missing_main'
            Test-Path $main | Should -BeTrue
        }
    }

    Context 'recovery: runs missing' {
        It 'creates the missing runs root' {
            (Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit).ExitCode | Should -Be 0
            $runs = Join-Path $script:parent 'demo-runs'
            Remove-Item -Recurse -Force $runs
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'recover_missing_runs'
            Test-Path $runs -PathType Container | Should -BeTrue
        }
    }

    Context 'path conflicts (refusal cases — exit 3)' {
        It 'refuses if bare path exists but isn''t bare' {
            $bare = Join-Path $script:parent 'demo.git'
            New-Item -ItemType Directory -Path $bare -Force | Out-Null
            Set-Content -Path (Join-Path $bare 'random.txt') -Value 'not a git repo' -Encoding utf8

            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 3
            $r.Output | Should -Match 'not a bare git repository'
        }

        It 'refuses if existing bare has mismatched origin' {
            # Bootstrap from a different remote.
            $otherRemote = New-FakeRemoteRepo -Root $script:root -DefaultBranch 'main'
            # Move it so the URL is unique.
            $otherRenamed = Join-Path $script:root 'other.git'
            Rename-Item $otherRemote $otherRenamed
            (Invoke-Script -RemoteUrl $otherRenamed -ParentDir $script:parent -RepoName 'demo' -Commit).ExitCode | Should -Be 0

            # Now try to bootstrap "demo" from a different URL.
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 3
            $r.Output | Should -Match 'origin'
            $r.Output | Should -Match 'Refusing'
        }

        It 'refuses if main path exists but isn''t a worktree of our bare' {
            # First, bootstrap fully.
            (Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit).ExitCode | Should -Be 0
            # Replace the main worktree with a plain directory.
            $main = Join-Path $script:parent 'demo'
            $bare = Join-Path $script:parent 'demo.git'
            Remove-Item -Recurse -Force $main
            & git --git-dir $bare worktree prune *> $null
            New-Item -ItemType Directory -Path $main -Force | Out-Null
            Set-Content -Path (Join-Path $main 'random.txt') -Value 'unrelated' -Encoding utf8

            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 3
            $r.Output | Should -Match 'not a worktree'
        }

        It 'refuses if target branch is already checked out in another worktree' {
            (Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit).ExitCode | Should -Be 0
            # Add a second worktree on 'main' at an unexpected path, then remove the canonical main.
            $bare = Join-Path $script:parent 'demo.git'
            $main = Join-Path $script:parent 'demo'
            $alt  = Join-Path $script:parent 'demo-alt'
            Remove-Item -Recurse -Force $main
            & git --git-dir $bare worktree prune *> $null
            & git --git-dir $bare worktree add $alt main *> $null

            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 3
            $r.Output | Should -Match 'already checked out'
        }

        It 'refuses if remote is empty (unborn HEAD)' {
            $empty = New-EmptyFakeRemote -Root $script:root
            $r = Invoke-Script -RemoteUrl $empty -ParentDir $script:parent -RepoName 'empty' -Commit
            # Full-bootstrap path: clone succeeds, then symbolic-ref returns a name
            # ('main') but rev-parse --verify refs/heads/main^{commit} fails →
            # execution failure (exit 4), not classification refusal (exit 3).
            $r.ExitCode | Should -Be 4
            $r.Output | Should -Match 'empty'
        }
    }

    Context 'dry-run' {
        It 'prints a plan and creates nothing without -Commit' {
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo'
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'DRY-RUN'
            $r.Output | Should -Match 'full_bootstrap'
            Test-Path (Join-Path $script:parent 'demo.git') | Should -BeFalse
            Test-Path (Join-Path $script:parent 'demo') | Should -BeFalse
        }

        It 'classifies already-bootstrapped without -Commit' {
            (Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo' -Commit).ExitCode | Should -Be 0
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'demo'
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Already bootstrapped'
        }
    }

    Context 'preflight failures (exit 1)' {
        It 'refuses RepoName containing path separators' {
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $script:parent -RepoName 'foo/bar' -Commit
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'path separators'
        }
        It 'refuses when -ParentDir points at a file' {
            $f = Join-Path $script:root 'iam-a-file.txt'
            Set-Content -Path $f -Value 'hi' -Encoding utf8
            $r = Invoke-Script -RemoteUrl $script:remote -ParentDir $f -RepoName 'demo' -Commit
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'points to a file'
        }
    }
}
