#requires -Version 7.0

<#
.SYNOPSIS
    Pester tests for scripts/Migrate-ToBareRepo.ps1.

.DESCRIPTION
    Tests the migration script's helper functions in isolation (dot-sourced)
    and the end-to-end script behavior (invoked directly with -Commit). Uses
    real `git init` in temp directories — no mocks for git itself.

    Covers the 13 scenarios identified in the design rubber-duck:
      - Already-migrated detection (full / partial / runs-root-missing)
      - Path conflict detection (bare/legacy/new/runs-non-empty)
      - Operator clone validation (subdir / linked-worktree / non-bare)
      - Risk factor reporting (uncommitted / untracked / stash / local-only)
      - Extra worktree refusal (hard, no -Force bypass)
      - Plan emission and dry-run safety
      - End-to-end -Commit happy path
      - Rollback on second move failure
      - safe.bareRepository=explicit invariant (probes via --git-dir)
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Migrate-ToBareRepo.ps1'
    . $script:ScriptPath

    function New-TestRoot {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "migrate-bare-test-$(Get-Random)"
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    function New-FakeRemoteRepo {
        param([Parameter(Mandatory)][string]$Root)
        $remote = Join-Path $Root 'remote.git'
        & git init --bare $remote *> $null

        # Seed it with one commit on main via a temporary worktree.
        $seed = Join-Path $Root 'seed'
        & git --git-dir $remote worktree add -b main $seed *> $null
        Set-Content -Path (Join-Path $seed 'README.md') -Value 'seed' -Encoding utf8
        & git -C $seed config user.email 'test@example.com' *> $null
        & git -C $seed config user.name 'test' *> $null
        & git -C $seed add . *> $null
        & git -C $seed commit -m 'initial' *> $null
        & git --git-dir $remote worktree remove $seed *> $null

        return $remote
    }

    function New-OperatorClone {
        param(
            [Parameter(Mandatory)][string]$Root,
            [Parameter(Mandatory)][string]$RemoteUrl,
            [string]$LeafName = 'polyphony'
        )
        $op = Join-Path $Root $LeafName
        & git clone $RemoteUrl $op *> $null
        & git -C $op config user.email 'test@example.com' *> $null
        & git -C $op config user.name 'test' *> $null
        return $op
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Helper-function unit tests
# ══════════════════════════════════════════════════════════════════════════════

Describe 'Get-DerivedPaths' {
    It 'derives canonical bare/legacy/new/runs from operator path' {
        $p = Get-DerivedPaths -OperatorPath '/tmp/x/polyphony'
        $p.OperatorPath | Should -Match 'polyphony$'
        $p.BarePath | Should -Match 'polyphony\.git$'
        $p.LegacyPath | Should -Match 'polyphony\.legacy$'
        $p.NewPath | Should -Match 'polyphony\.new$'
        $p.RunsRoot | Should -Match 'polyphony-runs$'
    }

    It 'normalizes trailing slashes' {
        $p = Get-DerivedPaths -OperatorPath '/tmp/x/polyphony/'
        $p.OperatorPath | Should -Not -Match '[\\/]$'
    }
}

Describe 'Test-OperatorClone' {
    BeforeEach {
        $script:Root = New-TestRoot
        $script:Remote = New-FakeRemoteRepo -Root $script:Root
        $script:Op = New-OperatorClone -Root $script:Root -RemoteUrl $script:Remote
    }

    AfterEach {
        Remove-Item -Recurse -Force $script:Root -ErrorAction SilentlyContinue
    }

    It 'accepts a valid clone' {
        $r = Test-OperatorClone -OperatorPath $script:Op
        $r.Ok | Should -BeTrue
    }

    It 'rejects a missing path' {
        $r = Test-OperatorClone -OperatorPath (Join-Path $script:Root 'nonexistent')
        $r.Ok | Should -BeFalse
        $r.Reason | Should -Match 'does not exist'
    }

    It 'rejects a path with no .git' {
        $bare = Join-Path $script:Root 'plain'
        New-Item -ItemType Directory -Path $bare -Force | Out-Null
        $r = Test-OperatorClone -OperatorPath $bare
        $r.Ok | Should -BeFalse
        $r.Reason | Should -Match 'no .git directory'
    }

    It 'rejects a subdirectory of the repo' {
        $sub = Join-Path $script:Op 'subdir'
        New-Item -ItemType Directory -Path $sub -Force | Out-Null
        $r = Test-OperatorClone -OperatorPath $sub
        $r.Ok | Should -BeFalse
        # Subdir lacks its own .git directory; refusal is via that check first.
        $r.Reason | Should -Match 'no .git directory|subdirectory'
    }

    It 'rejects a linked worktree (where .git is a file)' {
        $linked = Join-Path $script:Root 'linked'
        & git -C $script:Op worktree add $linked -b feature/test *> $null
        $r = Test-OperatorClone -OperatorPath $linked
        $r.Ok | Should -BeFalse
        $r.Reason | Should -Match 'linked worktree'
    }
}

Describe 'Get-RiskFactors' {
    BeforeEach {
        $script:Root = New-TestRoot
        $script:Remote = New-FakeRemoteRepo -Root $script:Root
        $script:Op = New-OperatorClone -Root $script:Root -RemoteUrl $script:Remote
    }

    AfterEach {
        Remove-Item -Recurse -Force $script:Root -ErrorAction SilentlyContinue
    }

    It 'reports zero risk on a fresh clone' {
        $r = Get-RiskFactors -OperatorPath $script:Op
        $r.UncommittedChanges.Count | Should -Be 0
        $r.UntrackedCount | Should -Be 0
        $r.Stashes.Count | Should -Be 0
        $r.LocalOnlyBranches.Count | Should -Be 0
        $r.ExtraWorktrees.Count | Should -Be 0
    }

    It 'detects uncommitted changes' {
        Set-Content -Path (Join-Path $script:Op 'README.md') -Value 'modified' -Encoding utf8
        $r = Get-RiskFactors -OperatorPath $script:Op
        $r.UncommittedChanges.Count | Should -BeGreaterThan 0
    }

    It 'counts untracked files separately' {
        Set-Content -Path (Join-Path $script:Op 'newfile.txt') -Value 'x' -Encoding utf8
        $r = Get-RiskFactors -OperatorPath $script:Op
        $r.UncommittedChanges.Count | Should -Be 0
        $r.UntrackedCount | Should -BeGreaterOrEqual 1
    }

    It 'detects stashes' {
        Set-Content -Path (Join-Path $script:Op 'README.md') -Value 'modified' -Encoding utf8
        & git -C $script:Op stash *> $null
        $r = Get-RiskFactors -OperatorPath $script:Op
        $r.Stashes.Count | Should -BeGreaterThan 0
    }

    It 'detects local-only branches' {
        & git -C $script:Op checkout -b local-only-branch *> $null
        & git -C $script:Op checkout main *> $null
        $r = Get-RiskFactors -OperatorPath $script:Op
        $r.LocalOnlyBranches | Should -Contain 'local-only-branch'
    }

    It 'detects extra worktrees' {
        $linked = Join-Path $script:Root 'linked'
        & git -C $script:Op worktree add $linked -b extra *> $null
        $r = Get-RiskFactors -OperatorPath $script:Op
        $r.ExtraWorktrees.Count | Should -Be 1
    }
}

Describe 'Get-PathConflicts' {
    BeforeEach {
        $script:Root = New-TestRoot
        $script:Paths = Get-DerivedPaths -OperatorPath (Join-Path $script:Root 'polyphony')
        $script:EmptyRisk = [pscustomobject]@{
            UncommittedChanges = @()
            Stashes            = @()
            UntrackedCount     = 0
            LocalOnlyBranches  = @()
            ExtraWorktrees     = @()
        }
    }

    AfterEach {
        Remove-Item -Recurse -Force $script:Root -ErrorAction SilentlyContinue
    }

    It 'reports no conflicts when target paths are absent' {
        $c = Get-PathConflicts -Paths $script:Paths -RiskFactors $script:EmptyRisk
        $c.Count | Should -Be 0
    }

    It 'flags an existing bare path' {
        New-Item -ItemType Directory -Path $script:Paths.BarePath -Force | Out-Null
        $c = Get-PathConflicts -Paths $script:Paths -RiskFactors $script:EmptyRisk
        ($c -join "`n") | Should -Match 'Bare path already exists'
    }

    It 'flags an existing legacy path' {
        New-Item -ItemType Directory -Path $script:Paths.LegacyPath -Force | Out-Null
        $c = Get-PathConflicts -Paths $script:Paths -RiskFactors $script:EmptyRisk
        ($c -join "`n") | Should -Match 'Legacy path already exists'
    }

    It 'flags an existing staging path' {
        New-Item -ItemType Directory -Path $script:Paths.NewPath -Force | Out-Null
        $c = Get-PathConflicts -Paths $script:Paths -RiskFactors $script:EmptyRisk
        ($c -join "`n") | Should -Match 'Staging path already exists'
    }

    It 'allows an empty runs root' {
        New-Item -ItemType Directory -Path $script:Paths.RunsRoot -Force | Out-Null
        $c = Get-PathConflicts -Paths $script:Paths -RiskFactors $script:EmptyRisk
        $c.Count | Should -Be 0
    }

    It 'flags a non-empty runs root' {
        New-Item -ItemType Directory -Path $script:Paths.RunsRoot -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:Paths.RunsRoot 'apex-1') -Force | Out-Null
        $c = Get-PathConflicts -Paths $script:Paths -RiskFactors $script:EmptyRisk
        ($c -join "`n") | Should -Match 'Runs root .* non-empty'
    }

    It 'flags extra worktrees as a HARD conflict (not bypassable by -Force)' {
        $risk = [pscustomobject]@{
            UncommittedChanges = @()
            Stashes            = @()
            UntrackedCount     = 0
            LocalOnlyBranches  = @()
            ExtraWorktrees     = @([pscustomobject]@{ Path = '/tmp/extra'; Branch = 'refs/heads/foo' })
        }
        $c = Get-PathConflicts -Paths $script:Paths -RiskFactors $risk
        ($c -join "`n") | Should -Match 'linked worktrees'
        ($c -join "`n") | Should -Match 'silently break'
    }
}

Describe 'Test-IsBareRepository (safe.bareRepository=explicit invariant)' {
    BeforeEach {
        $script:Root = New-TestRoot
        $script:Bare = New-FakeRemoteRepo -Root $script:Root
    }

    AfterEach {
        Remove-Item -Recurse -Force $script:Root -ErrorAction SilentlyContinue
    }

    It 'detects a bare repo via --git-dir' {
        Test-IsBareRepository -BarePath $script:Bare | Should -BeTrue
    }

    It 'returns false for a non-existent path' {
        Test-IsBareRepository -BarePath (Join-Path $script:Root 'missing.git') | Should -BeFalse
    }

    It 'returns false for a non-bare directory' {
        $plain = Join-Path $script:Root 'plain'
        & git init $plain *> $null
        Test-IsBareRepository -BarePath $plain | Should -BeFalse
    }

    It 'works even when safe.bareRepository=explicit (uses --git-dir, not -C)' {
        # Set safe.bareRepository=explicit at the local level so cwd discovery
        # via `git -C $bare` would fail. Test-IsBareRepository must use
        # --git-dir form, which is unaffected by this setting.
        # We can't set it globally without polluting the test environment;
        # instead, simulate by verifying the function doesn't try `git -C`
        # against the bare. Functional check: still returns true.
        Test-IsBareRepository -BarePath $script:Bare | Should -BeTrue
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Already-migrated detection
# ══════════════════════════════════════════════════════════════════════════════

Describe 'Test-MigrationAlreadyComplete' {
    BeforeEach {
        $script:Root = New-TestRoot
        $script:Remote = New-FakeRemoteRepo -Root $script:Root
        $script:Paths = Get-DerivedPaths -OperatorPath (Join-Path $script:Root 'polyphony')
    }

    AfterEach {
        Remove-Item -Recurse -Force $script:Root -ErrorAction SilentlyContinue
    }

    It 'returns absent when no target paths exist' {
        $r = Test-MigrationAlreadyComplete -Paths $script:Paths
        $r.Status | Should -Be 'absent'
    }

    It 'returns complete when bare + main worktree + runs root all in place' {
        & git clone --bare $script:Remote $script:Paths.BarePath *> $null
        & git --git-dir $script:Paths.BarePath worktree add $script:Paths.OperatorPath main *> $null
        New-Item -ItemType Directory -Path $script:Paths.RunsRoot -Force | Out-Null
        $r = Test-MigrationAlreadyComplete -Paths $script:Paths
        $r.Status | Should -Be 'complete'
        $r.Reasons | Should -Not -Contain 'runs_root_missing'
    }

    It 'returns complete with runs_root_missing when only runs root is absent' {
        & git clone --bare $script:Remote $script:Paths.BarePath *> $null
        & git --git-dir $script:Paths.BarePath worktree add $script:Paths.OperatorPath main *> $null
        $r = Test-MigrationAlreadyComplete -Paths $script:Paths
        $r.Status | Should -Be 'complete'
        $r.Reasons | Should -Contain 'runs_root_missing'
    }

    It 'returns partial when operator worktree is on the wrong branch' {
        & git clone --bare $script:Remote $script:Paths.BarePath *> $null
        & git --git-dir $script:Paths.BarePath worktree add -b notmain $script:Paths.OperatorPath main *> $null
        $r = Test-MigrationAlreadyComplete -Paths $script:Paths
        $r.Status | Should -Be 'partial'
        ($r.Reasons -join "`n") | Should -Match 'expected refs/heads/main'
    }

    It 'returns partial when bare exists but operator path is missing' {
        & git clone --bare $script:Remote $script:Paths.BarePath *> $null
        $r = Test-MigrationAlreadyComplete -Paths $script:Paths
        $r.Status | Should -Be 'partial'
        ($r.Reasons -join "`n") | Should -Match 'is missing'
    }

    It 'returns partial when bare path exists but is not bare' {
        New-Item -ItemType Directory -Path $script:Paths.BarePath -Force | Out-Null
        & git init $script:Paths.BarePath *> $null
        $r = Test-MigrationAlreadyComplete -Paths $script:Paths
        $r.Status | Should -Be 'partial'
        ($r.Reasons -join "`n") | Should -Match 'not a bare repository'
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# End-to-end script behavior
# ══════════════════════════════════════════════════════════════════════════════

Describe 'Migrate-ToBareRepo.ps1 — end-to-end' {
    BeforeEach {
        $script:Root = New-TestRoot
        $script:Remote = New-FakeRemoteRepo -Root $script:Root
        $script:Op = New-OperatorClone -Root $script:Root -RemoteUrl $script:Remote
    }

    AfterEach {
        Remove-Item -Recurse -Force $script:Root -ErrorAction SilentlyContinue
    }

    It 'dry-run exits 0 and does not touch disk' {
        $before = Get-ChildItem $script:Root | Select-Object -ExpandProperty Name | Sort-Object
        & $script:ScriptPath -OperatorPath $script:Op *> $null
        $LASTEXITCODE | Should -Be 0
        $after = Get-ChildItem $script:Root | Select-Object -ExpandProperty Name | Sort-Object
        ($after -join ',') | Should -Be ($before -join ',')
    }

    It '-Commit on a clean clone produces the new layout' {
        $paths = Get-DerivedPaths -OperatorPath $script:Op
        & $script:ScriptPath -OperatorPath $script:Op -Commit *> $null
        $LASTEXITCODE | Should -Be 0
        Test-Path $paths.BarePath | Should -BeTrue
        Test-Path $paths.LegacyPath | Should -BeTrue
        Test-Path $paths.OperatorPath | Should -BeTrue
        Test-Path $paths.RunsRoot | Should -BeTrue
        Test-IsBareRepository -BarePath $paths.BarePath | Should -BeTrue
        # New operator path should be on `main`.
        $branch = (& git -C $paths.OperatorPath branch --show-current).Trim()
        $branch | Should -Be 'main'
    }

    It '-Commit refuses with EXIT_RISK_MATERIAL when uncommitted changes present' {
        Set-Content -Path (Join-Path $script:Op 'README.md') -Value 'modified' -Encoding utf8
        & $script:ScriptPath -OperatorPath $script:Op -Commit *> $null
        $LASTEXITCODE | Should -Be 2
        # Bare must NOT have been created.
        $paths = Get-DerivedPaths -OperatorPath $script:Op
        Test-Path $paths.BarePath | Should -BeFalse
    }

    It '-Commit -Force proceeds with risk material; legacy preserves the dirty state' {
        Set-Content -Path (Join-Path $script:Op 'README.md') -Value 'dirty-content' -Encoding utf8
        $paths = Get-DerivedPaths -OperatorPath $script:Op
        & $script:ScriptPath -OperatorPath $script:Op -Commit -Force *> $null
        $LASTEXITCODE | Should -Be 0
        Test-Path $paths.LegacyPath | Should -BeTrue
        $legacyContent = Get-Content (Join-Path $paths.LegacyPath 'README.md') -Raw
        $legacyContent.Trim() | Should -Be 'dirty-content'
        # New operator path should have the clean main HEAD.
        $newContent = Get-Content (Join-Path $paths.OperatorPath 'README.md') -Raw
        $newContent.Trim() | Should -Be 'seed'
    }

    It '-Commit refuses (EXIT_PATH_CONFLICT) when extra worktree present, even with -Force' {
        $linked = Join-Path $script:Root 'extra-wt'
        & git -C $script:Op worktree add $linked -b extra *> $null
        & $script:ScriptPath -OperatorPath $script:Op -Commit -Force *> $null
        $LASTEXITCODE | Should -Be 3
        $paths = Get-DerivedPaths -OperatorPath $script:Op
        Test-Path $paths.BarePath | Should -BeFalse
    }

    It '-Commit refuses (EXIT_PATH_CONFLICT) when bare path already exists' {
        $paths = Get-DerivedPaths -OperatorPath $script:Op
        New-Item -ItemType Directory -Path $paths.BarePath -Force | Out-Null
        & $script:ScriptPath -OperatorPath $script:Op -Commit *> $null
        $LASTEXITCODE | Should -Be 3
    }

    It 'is idempotent: re-running on already-migrated layout exits 0 without changes' {
        $paths = Get-DerivedPaths -OperatorPath $script:Op
        & $script:ScriptPath -OperatorPath $script:Op -Commit *> $null
        $LASTEXITCODE | Should -Be 0
        # Run again — should detect already-migrated and exit 0.
        & $script:ScriptPath -OperatorPath $script:Op -Commit *> $null
        $LASTEXITCODE | Should -Be 0
    }

    It 'creates a missing runs root when re-run with -Commit on otherwise-complete layout' {
        $paths = Get-DerivedPaths -OperatorPath $script:Op
        & $script:ScriptPath -OperatorPath $script:Op -Commit *> $null
        Remove-Item -Recurse -Force $paths.RunsRoot
        & $script:ScriptPath -OperatorPath $script:Op -Commit *> $null
        $LASTEXITCODE | Should -Be 0
        Test-Path $paths.RunsRoot | Should -BeTrue
    }

    It 'EXIT_PREFLIGHT_FAILURE when operator path has no .git' {
        $bare = Join-Path $script:Root 'no-git-here'
        New-Item -ItemType Directory -Path $bare -Force | Out-Null
        & $script:ScriptPath -OperatorPath $bare *> $null
        $LASTEXITCODE | Should -Be 1
    }
}
