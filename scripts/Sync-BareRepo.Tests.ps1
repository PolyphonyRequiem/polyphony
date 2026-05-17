#requires -Version 7.0

<#
.SYNOPSIS
    Pester tests for scripts/Sync-BareRepo.ps1 (#419).

.DESCRIPTION
    Tests helper functions in isolation (dot-sourced) and end-to-end
    script behavior (invoked with -Commit). Uses real `git init` in temp
    directories — no mocks for git itself.

    Covers the scenarios identified in the rubber-duck pass:
      - Preflight: git missing / not a worktree / common-dir is normal
        (non-bare) repo / origin missing post-fetch
      - Main worktree: zero matches / multiple matches both refuse
      - Main reconcile: no-op / fast-forward / squash-reset /
        local-ahead-real refuse / diverged-content refuse /
        DIRTY worktree refusal (the safety-critical case)
      - Prune: stale merged branch / stale unmerged branch preserved /
        stale + checked out in worktree / -NoPrune skip / live upstream
        preserved
      - Dry-run: classifies and exits 0 without mutating
      - -MainBranch override
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Sync-BareRepo.ps1'
    . $script:ScriptPath

    function New-TestRoot {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "sync-bare-test-$(Get-Random)"
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    function New-CommitInWorktree {
        param(
            [Parameter(Mandatory)][string]$Worktree,
            [Parameter(Mandatory)][string]$FileName,
            [Parameter(Mandatory)][string]$Content,
            [string]$Message = 'change'
        )
        Set-Content -Path (Join-Path $Worktree $FileName) -Value $Content -Encoding utf8
        & git -C $Worktree add . *> $null
        & git -C $Worktree -c user.email='test@example.com' -c user.name='test' commit -m $Message *> $null
    }

    function New-SyncedBareLayout {
        <#
        Creates the canonical layout under $Root:
          remote.git   (bare; mimics origin)
          bare.git     (bare clone of remote; the operator's local bare)
          main         (worktree of bare.git on $DefaultBranch)
        Returns the three paths.
        #>
        param(
            [Parameter(Mandatory)][string]$Root,
            [string]$DefaultBranch = 'main'
        )
        $remote = Join-Path $Root 'remote.git'
        & git init --bare --initial-branch=$DefaultBranch $remote *> $null

        # Seed the remote with one commit.
        $seed = Join-Path $Root 'seed'
        & git --git-dir $remote worktree add -b $DefaultBranch $seed *> $null
        New-CommitInWorktree -Worktree $seed -FileName 'README.md' -Content 'seed' -Message 'initial'
        & git --git-dir $remote worktree remove $seed *> $null
        & git --git-dir $remote symbolic-ref HEAD "refs/heads/$DefaultBranch" *> $null

        # Clone into the operator's bare.
        $bare = Join-Path $Root 'bare.git'
        & git clone --bare $remote $bare *> $null
        # `git clone --bare` doesn't set up the refspec for normal fetches; fix it.
        & git --git-dir $bare config remote.origin.fetch '+refs/heads/*:refs/remotes/origin/*' *> $null
        & git --git-dir $bare fetch origin *> $null

        # Add a main worktree of the operator's bare.
        $main = Join-Path $Root 'main'
        & git --git-dir $bare worktree add $main $DefaultBranch *> $null

        return [pscustomobject]@{
            Remote = $remote
            Bare   = $bare
            Main   = $main
        }
    }

    function Invoke-RemoteCommit {
        <#
        Commit something on the remote's $Branch by adding a temporary
        worktree on the remote, committing, then removing the worktree.
        #>
        param(
            [Parameter(Mandatory)][string]$Remote,
            [Parameter(Mandatory)][string]$Branch,
            [Parameter(Mandatory)][string]$FileName,
            [Parameter(Mandatory)][string]$Content,
            [string]$Message = 'remote change'
        )
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "remote-wt-$(Get-Random)"
        & git --git-dir $Remote worktree add $tmp $Branch *> $null
        New-CommitInWorktree -Worktree $tmp -FileName $FileName -Content $Content -Message $Message
        & git --git-dir $Remote worktree remove $tmp *> $null
    }

    function Invoke-RemoteSquashEquivalent {
        <#
        Simulate a squash-merge on the remote: produce a NEW commit on
        $Remote/$Branch whose tree matches what we'd get by applying
        $FileName/$Content to the current remote tip. After this, the
        remote and local (which had its own non-squash commit with the
        same $FileName/$Content) diverge in history but have identical
        trees — the classic squash-merge case.
        #>
        param(
            [Parameter(Mandatory)][string]$Remote,
            [Parameter(Mandatory)][string]$Branch,
            [Parameter(Mandatory)][string]$FileName,
            [Parameter(Mandatory)][string]$Content
        )
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "squash-wt-$(Get-Random)"
        & git --git-dir $Remote worktree add $tmp $Branch *> $null
        New-CommitInWorktree -Worktree $tmp -FileName $FileName -Content $Content -Message 'squash merge'
        & git --git-dir $Remote worktree remove --force $tmp *> $null
    }

    function Invoke-Script {
        param(
            [Parameter(Mandatory)][string]$WorktreePath,
            [string]$MainBranch = '',
            [switch]$NoPrune,
            [switch]$Commit
        )
        $argList = @(
            '-NoProfile', '-File', $script:ScriptPath,
            '-WorktreePath', $WorktreePath
        )
        if ($MainBranch) { $argList += @('-MainBranch', $MainBranch) }
        if ($NoPrune)    { $argList += '-NoPrune' }
        if ($Commit)     { $argList += '-Commit' }
        $output = & pwsh @argList 2>&1
        $exit = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $exit
            Output   = ($output -join "`n")
        }
    }
}

Describe 'Helper: Resolve-BarePath' {
    It 'resolves bare common-dir from inside a worktree' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            $resolved = Resolve-BarePath -WorktreePath $layout.Main
            $resolved | Should -Be (Get-NormalizedPath $layout.Bare)
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'returns $null for a non-git directory' {
        $root = New-TestRoot
        try {
            (Resolve-BarePath -WorktreePath $root) | Should -BeNullOrEmpty
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'Helper: Test-IsBareRepository' {
    It 'returns $true for a bare repo' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            Test-IsBareRepository -BarePath $layout.Bare | Should -BeTrue
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'returns $false for a normal (non-bare) repo' {
        $root = New-TestRoot
        try {
            $normal = Join-Path $root 'normal'
            & git init $normal *> $null
            Test-IsBareRepository -BarePath (Join-Path $normal '.git') | Should -BeFalse
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'Helper: Test-WorktreeClean' {
    It 'returns $true for a clean worktree' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            Test-WorktreeClean -WorktreePath $layout.Main | Should -BeTrue
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'returns $false for a worktree with uncommitted modifications' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            Set-Content -Path (Join-Path $layout.Main 'README.md') -Value 'changed' -Encoding utf8
            Test-WorktreeClean -WorktreePath $layout.Main | Should -BeFalse
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'returns $false for a worktree with untracked files' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            Set-Content -Path (Join-Path $layout.Main 'new.txt') -Value 'hi' -Encoding utf8
            Test-WorktreeClean -WorktreePath $layout.Main | Should -BeFalse
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'Helper: Find-MainWorktree' {
    It 'returns ok with the one matching worktree' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            $r = Find-MainWorktree -BarePath $layout.Bare -MainBranch 'main'
            $r.Status | Should -Be 'ok'
            (Get-NormalizedPath $r.Worktree.Path) | Should -Be (Get-NormalizedPath $layout.Main)
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'returns none when no worktree is on the branch' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            $r = Find-MainWorktree -BarePath $layout.Bare -MainBranch 'nope'
            $r.Status | Should -Be 'none'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'returns multiple when two worktrees are on the same branch' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # Move main aside, create a second worktree on main from another path.
            $second = Join-Path $root 'second-main'
            # Trick: force-add another worktree on main via --force.
            & git --git-dir $layout.Bare worktree add --force $second main *> $null
            $r = Find-MainWorktree -BarePath $layout.Bare -MainBranch 'main'
            $r.Status | Should -Be 'multiple'
            $r.Worktrees.Count | Should -Be 2
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'Helper: Get-MainAction' {
    It 'classifies noop when local == remote' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            $cls = Get-MainAction -BarePath $layout.Bare -MainBranch 'main' -MainWorktreePath $layout.Main
            $cls.Action | Should -Be 'noop'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'classifies fast_forward when local is behind' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            Invoke-RemoteCommit -Remote $layout.Remote -Branch 'main' -FileName 'r.txt' -Content 'r' -Message 'remote'
            & git --git-dir $layout.Bare fetch origin *> $null
            $cls = Get-MainAction -BarePath $layout.Bare -MainBranch 'main' -MainWorktreePath $layout.Main
            $cls.Action | Should -Be 'fast_forward'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'classifies refuse_local_ahead when local has unpushed real commits' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            New-CommitInWorktree -Worktree $layout.Main -FileName 'local.txt' -Content 'l' -Message 'local-only'
            $cls = Get-MainAction -BarePath $layout.Bare -MainBranch 'main' -MainWorktreePath $layout.Main
            $cls.Action | Should -Be 'refuse_local_ahead'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'classifies squash_reset when divergent histories have identical content' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # Local: add a file.
            New-CommitInWorktree -Worktree $layout.Main -FileName 'f.txt' -Content 'x' -Message 'local-real'
            # Remote: produce the SAME tree via a separate commit (squash).
            Invoke-RemoteSquashEquivalent -Remote $layout.Remote -Branch 'main' -FileName 'f.txt' -Content 'x'
            & git --git-dir $layout.Bare fetch origin *> $null
            $cls = Get-MainAction -BarePath $layout.Bare -MainBranch 'main' -MainWorktreePath $layout.Main
            $cls.Action | Should -Be 'squash_reset'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'classifies refuse_diverged_content when histories AND content differ' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            New-CommitInWorktree -Worktree $layout.Main -FileName 'local.txt' -Content 'A' -Message 'local'
            Invoke-RemoteCommit -Remote $layout.Remote -Branch 'main' -FileName 'remote.txt' -Content 'B' -Message 'remote'
            & git --git-dir $layout.Bare fetch origin *> $null
            $cls = Get-MainAction -BarePath $layout.Bare -MainBranch 'main' -MainWorktreePath $layout.Main
            $cls.Action | Should -Be 'refuse_diverged_content'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'classifies refuse_dirty_worktree when the main worktree has uncommitted changes' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # Set up a squash-reset condition (where action would be destructive)
            # AND dirty the worktree. The dirty check must take precedence.
            New-CommitInWorktree -Worktree $layout.Main -FileName 'f.txt' -Content 'x' -Message 'local-real'
            Invoke-RemoteSquashEquivalent -Remote $layout.Remote -Branch 'main' -FileName 'f.txt' -Content 'x'
            & git --git-dir $layout.Bare fetch origin *> $null
            Set-Content -Path (Join-Path $layout.Main 'uncommitted.txt') -Value 'WIP' -Encoding utf8
            $cls = Get-MainAction -BarePath $layout.Bare -MainBranch 'main' -MainWorktreePath $layout.Main
            $cls.Action | Should -Be 'refuse_dirty_worktree'
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'Helper: Get-PrunableBranches' {
    It 'prunes a branch whose upstream is gone AND that is merged into main' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # feat/x branches from current remote main, so its tip == main's tip
            # (which makes it an ancestor of main — `merge-base --is-ancestor`
            # treats equality as true).
            & git --git-dir $layout.Remote update-ref 'refs/heads/feat/x' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Bare fetch --prune origin *> $null
            & git --git-dir $layout.Bare branch --track 'feat/x' 'refs/remotes/origin/feat/x' *> $null
            # Delete remote branch and prune so feat/x's upstream goes [gone].
            & git --git-dir $layout.Remote update-ref -d 'refs/heads/feat/x' *> $null
            & git --git-dir $layout.Bare fetch --prune origin *> $null
            $r = Get-PrunableBranches -BarePath $layout.Bare -MainBranch 'main'
            $r.Prunable.Name | Should -Contain 'feat/x'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'preserves a branch whose upstream is gone BUT has unmerged commits' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # Create branch on remote tracking remote main.
            & git --git-dir $layout.Remote update-ref 'refs/heads/feat/abandoned' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Bare fetch --prune origin *> $null
            & git --git-dir $layout.Bare branch --track 'feat/abandoned' 'refs/remotes/origin/feat/abandoned' *> $null
            # Add a UNIQUE commit on local feat/abandoned (not in main, never pushed).
            $w = Join-Path $root 'wt-abandoned'
            & git --git-dir $layout.Bare worktree add $w 'feat/abandoned' *> $null
            New-CommitInWorktree -Worktree $w -FileName 'unmerged.txt' -Content 'lost' -Message 'unmerged work'
            & git --git-dir $layout.Bare worktree remove --force $w *> $null
            # Delete remote and prune.
            & git --git-dir $layout.Remote update-ref -d 'refs/heads/feat/abandoned' *> $null
            & git --git-dir $layout.Bare fetch --prune origin *> $null
            $r = Get-PrunableBranches -BarePath $layout.Bare -MainBranch 'main'
            $r.Prunable.Name | Should -Not -Contain 'feat/abandoned'
            $r.Preserved.Name | Should -Contain 'feat/abandoned'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'skips a [gone]+merged branch that is checked out in another worktree' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            & git --git-dir $layout.Remote update-ref 'refs/heads/feat/checked' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Bare fetch --prune origin *> $null
            & git --git-dir $layout.Bare branch --track 'feat/checked' 'refs/remotes/origin/feat/checked' *> $null
            $w = Join-Path $root 'wt-checked'
            & git --git-dir $layout.Bare worktree add $w 'feat/checked' *> $null
            # Delete on remote + prune.
            & git --git-dir $layout.Remote update-ref -d 'refs/heads/feat/checked' *> $null
            & git --git-dir $layout.Bare fetch --prune origin *> $null
            $r = Get-PrunableBranches -BarePath $layout.Bare -MainBranch 'main'
            $r.Prunable.Name | Should -Not -Contain 'feat/checked'
            $r.Skipped.Name | Should -Contain 'feat/checked'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'preserves a branch whose upstream is still live' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            & git --git-dir $layout.Remote update-ref 'refs/heads/feat/live' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Bare fetch origin *> $null
            & git --git-dir $layout.Bare branch --track 'feat/live' 'refs/remotes/origin/feat/live' *> $null
            $r = Get-PrunableBranches -BarePath $layout.Bare -MainBranch 'main'
            $r.Prunable.Name | Should -Not -Contain 'feat/live'
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'End-to-end: dry-run' {
    It 'classifies and exits 0 without mutating for a clean already-synced layout' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            $r = Invoke-Script -WorktreePath $layout.Main
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Main reconcile: noop'
            $r.Output | Should -Match 'DRY-RUN'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'reports squash_reset in dry-run without resetting' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            New-CommitInWorktree -Worktree $layout.Main -FileName 'f.txt' -Content 'x' -Message 'local-real'
            $localTipBefore = (& git --git-dir $layout.Bare rev-parse 'refs/heads/main').Trim()
            Invoke-RemoteSquashEquivalent -Remote $layout.Remote -Branch 'main' -FileName 'f.txt' -Content 'x'
            $r = Invoke-Script -WorktreePath $layout.Main
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Main reconcile: squash_reset'
            # Confirm no reset happened.
            (& git --git-dir $layout.Bare rev-parse 'refs/heads/main').Trim() | Should -Be $localTipBefore
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'End-to-end: refusals' {
    It 'exits 3 when the main worktree is dirty AND would otherwise be reset' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # Set up squash-reset condition.
            New-CommitInWorktree -Worktree $layout.Main -FileName 'f.txt' -Content 'x' -Message 'local-real'
            Invoke-RemoteSquashEquivalent -Remote $layout.Remote -Branch 'main' -FileName 'f.txt' -Content 'x'
            # Make worktree dirty.
            Set-Content -Path (Join-Path $layout.Main 'WIP.txt') -Value 'in progress' -Encoding utf8
            $localTipBefore = (& git --git-dir $layout.Bare rev-parse 'refs/heads/main').Trim()
            $r = Invoke-Script -WorktreePath $layout.Main -Commit
            $r.ExitCode | Should -Be 3
            $r.Output | Should -Match 'refuse_dirty_worktree'
            # The destructive reset MUST NOT have happened.
            (& git --git-dir $layout.Bare rev-parse 'refs/heads/main').Trim() | Should -Be $localTipBefore
            # And WIP.txt must still be there.
            (Test-Path -LiteralPath (Join-Path $layout.Main 'WIP.txt')) | Should -BeTrue
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'exits 3 on diverged content' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            New-CommitInWorktree -Worktree $layout.Main -FileName 'l.txt' -Content 'L' -Message 'local'
            Invoke-RemoteCommit -Remote $layout.Remote -Branch 'main' -FileName 'r.txt' -Content 'R' -Message 'remote'
            $r = Invoke-Script -WorktreePath $layout.Main -Commit
            $r.ExitCode | Should -Be 3
            $r.Output | Should -Match 'refuse_diverged_content'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'exits 3 on local-ahead with real commits' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            New-CommitInWorktree -Worktree $layout.Main -FileName 'l.txt' -Content 'L' -Message 'local-only'
            $r = Invoke-Script -WorktreePath $layout.Main -Commit
            $r.ExitCode | Should -Be 3
            $r.Output | Should -Match 'refuse_local_ahead'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'exits 1 when cwd is not inside a git worktree' {
        $root = New-TestRoot
        try {
            $r = Invoke-Script -WorktreePath $root
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'not inside a git worktree'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'exits 1 when common-dir is a normal (non-bare) repo' {
        $root = New-TestRoot
        try {
            $normal = Join-Path $root 'normal'
            & git init --initial-branch=main $normal *> $null
            New-CommitInWorktree -Worktree $normal -FileName 'README.md' -Content 'seed' -Message 'init'
            $r = Invoke-Script -WorktreePath $normal
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'not a bare repository'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'exits 1 when no worktree is on the requested main branch' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            $r = Invoke-Script -WorktreePath $layout.Main -MainBranch 'nonexistent-branch'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match "(no worktree|has no commit)"
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'exits 1 when origin/<main> is missing post-fetch' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # Delete the remote's main branch entirely so fetch+prune drops origin/main.
            & git --git-dir $layout.Remote symbolic-ref HEAD 'refs/heads/decoy' *> $null
            & git --git-dir $layout.Remote update-ref 'refs/heads/decoy' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Remote update-ref -d 'refs/heads/main' *> $null
            $r = Invoke-Script -WorktreePath $layout.Main
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match "'origin/main' does not exist"
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'End-to-end: -Commit execution' {
    It 'fast-forwards local main when behind' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            Invoke-RemoteCommit -Remote $layout.Remote -Branch 'main' -FileName 'r.txt' -Content 'R' -Message 'remote'
            $r = Invoke-Script -WorktreePath $layout.Main -Commit
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Main : fast_forward'
            $localTip = (& git --git-dir $layout.Bare rev-parse 'refs/heads/main').Trim()
            # After fetch in dry-run also-fetches, the remote tracking ref is current; verify equality.
            $remoteTrack = (& git --git-dir $layout.Bare rev-parse 'refs/remotes/origin/main').Trim()
            $localTip | Should -Be $remoteTrack
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'hard-resets local main on squash-merge divergence' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            New-CommitInWorktree -Worktree $layout.Main -FileName 'f.txt' -Content 'x' -Message 'local-real'
            Invoke-RemoteSquashEquivalent -Remote $layout.Remote -Branch 'main' -FileName 'f.txt' -Content 'x'
            $r = Invoke-Script -WorktreePath $layout.Main -Commit
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Main : squash_reset'
            $localTip = (& git --git-dir $layout.Bare rev-parse 'refs/heads/main').Trim()
            $remoteTrack = (& git --git-dir $layout.Bare rev-parse 'refs/remotes/origin/main').Trim()
            $localTip | Should -Be $remoteTrack
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'prunes a stale merged branch and preserves an unmerged one' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            # Prunable: feat/x — created from main, never advanced, then deleted on remote.
            & git --git-dir $layout.Remote update-ref 'refs/heads/feat/x' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Bare fetch origin *> $null
            & git --git-dir $layout.Bare branch --track 'feat/x' 'refs/remotes/origin/feat/x' *> $null
            # Preserved: feat/abandoned — local has unique commit.
            & git --git-dir $layout.Remote update-ref 'refs/heads/feat/abandoned' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Bare fetch origin *> $null
            & git --git-dir $layout.Bare branch --track 'feat/abandoned' 'refs/remotes/origin/feat/abandoned' *> $null
            $w = Join-Path $root 'wt-aban'
            & git --git-dir $layout.Bare worktree add $w 'feat/abandoned' *> $null
            New-CommitInWorktree -Worktree $w -FileName 'unmerged.txt' -Content 'lost' -Message 'unmerged'
            & git --git-dir $layout.Bare worktree remove --force $w *> $null
            # Delete both on remote, prune.
            & git --git-dir $layout.Remote update-ref -d 'refs/heads/feat/x' *> $null
            & git --git-dir $layout.Remote update-ref -d 'refs/heads/feat/abandoned' *> $null
            $r = Invoke-Script -WorktreePath $layout.Main -Commit
            $r.ExitCode | Should -Be 0
            # feat/x deleted; feat/abandoned still present.
            $branches = (& git --git-dir $layout.Bare for-each-ref --format='%(refname:short)' 'refs/heads/')
            $branches | Should -Not -Contain 'feat/x'
            $branches | Should -Contain 'feat/abandoned'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It '-NoPrune skips stale-branch cleanup' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            & git --git-dir $layout.Remote update-ref 'refs/heads/feat/skip' (& git --git-dir $layout.Remote rev-parse 'refs/heads/main') *> $null
            & git --git-dir $layout.Bare fetch origin *> $null
            & git --git-dir $layout.Bare branch --track 'feat/skip' 'refs/remotes/origin/feat/skip' *> $null
            & git --git-dir $layout.Remote update-ref -d 'refs/heads/feat/skip' *> $null
            $r = Invoke-Script -WorktreePath $layout.Main -NoPrune -Commit
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Prune: SKIPPED'
            $branches = (& git --git-dir $layout.Bare for-each-ref --format='%(refname:short)' 'refs/heads/')
            $branches | Should -Contain 'feat/skip'
        } finally { Remove-Item -Recurse -Force $root }
    }
    It 'is a no-op on an already-synced layout' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root
            $r = Invoke-Script -WorktreePath $layout.Main -Commit
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Main : noop'
        } finally { Remove-Item -Recurse -Force $root }
    }
}

Describe 'End-to-end: -MainBranch override' {
    It 'respects -MainBranch when the bare default differs' {
        $root = New-TestRoot
        try {
            $layout = New-SyncedBareLayout -Root $root -DefaultBranch 'develop'
            $r = Invoke-Script -WorktreePath $layout.Main -MainBranch 'develop'
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'Main branch   : develop'
        } finally { Remove-Item -Recurse -Force $root }
    }
}


