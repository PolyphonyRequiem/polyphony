#requires -Version 7.0

<#
.SYNOPSIS
    Reconcile the operator's bare-repo + worktree layout after a remote
    squash-merge (#419). Sister script to scripts/Migrate-ToBareRepo.ps1
    (convert-existing-clone) and scripts/Bootstrap-BareRepo.ps1
    (fresh-clone). This one runs AFTER a PR merges to clean up:

      1. Squash-merge divergence — local `main` is 1-ahead/N-behind
         `origin/main` with identical content. Resets local main to
         origin/main when (and only when) the content diff is empty.
      2. Stale local branches — local branches whose remote tracking ref
         was pruned by `fetch --prune` AND whose tip is merged into the
         reconciled main. Both conditions required; either alone is unsafe.

.DESCRIPTION
    Without `-Commit`, the script runs in DRY-RUN mode: classifies what
    it WOULD do (no-op / fast-forward / squash-reset / refuse) for the
    main branch, enumerates prunable / preserved local branches, and
    exits 0. With `-Commit`, it executes.

    Identifies the main worktree (the one on $MainBranch) and refuses if
    there are zero or more than one — never picks first.

    Refuses if the main worktree is dirty (uncommitted or untracked
    files) BEFORE any destructive operation.

    Reconciles against `origin/<MainBranch>` only. If your main tracks
    another remote, fetch + reconcile manually.

    Works on Windows with `safe.bareRepository=explicit` set globally:
    every git invocation against the bare uses `--git-dir=<bare>` and
    never `git -C <bare>`.

.PARAMETER WorktreePath
    Path inside a worktree of the bare repo. The script resolves the
    bare via `git -C $WorktreePath rev-parse --path-format=absolute
    --git-common-dir`. Default: (Get-Location).

.PARAMETER MainBranch
    Branch to reconcile. Default: bare's symbolic HEAD (typically `main`).

.PARAMETER NoPrune
    Skip the stale-branch cleanup. Default behavior: prune branches that
    are BOTH `[gone]` upstream AND merged into reconciled main.

.PARAMETER Commit
    Without this switch, the script runs in DRY-RUN mode and exits 0
    after printing the classification + plan. With this switch, executes.

.OUTPUTS
    Human-readable plan / progress / summary narrative. Exit codes:
      0  success or no-op or dry-run
      1  preflight failure (git missing, not a worktree, common-dir not
         bare, zero/multiple main worktrees, origin/<MainBranch> missing)
      3  refusal (dirty main worktree, local-ahead with real commits,
         diverged content)
      4  execution failure (fetch / reset / branch -D failed)

.EXAMPLE
    PS> .\Sync-BareRepo.ps1
    Dry-run from cwd. Prints what reconcile + prune would do.

.EXAMPLE
    PS> .\Sync-BareRepo.ps1 -Commit
    Executes from cwd.

.EXAMPLE
    PS> .\Sync-BareRepo.ps1 -NoPrune -Commit
    Reconcile main only; leave stale local branches alone.
#>
param(
    [string]$WorktreePath = '',
    [string]$MainBranch = '',
    [switch]$NoPrune,
    [switch]$Commit
)

# ── Exit code constants ──────────────────────────────────────────────────────
$script:EXIT_SUCCESS = 0
$script:EXIT_PREFLIGHT_FAILURE = 1
$script:EXIT_REFUSED = 3
$script:EXIT_EXECUTION_FAILURE = 4

# ── Main-branch action constants (what reconcile would do) ──────────────────
$script:MAIN_NOOP            = 'noop'
$script:MAIN_FAST_FORWARD    = 'fast_forward'
$script:MAIN_SQUASH_RESET    = 'squash_reset'
$script:MAIN_REFUSE_DIRTY    = 'refuse_dirty_worktree'
$script:MAIN_REFUSE_AHEAD    = 'refuse_local_ahead'
$script:MAIN_REFUSE_DIVERGED = 'refuse_diverged_content'

# ── Helpers ──────────────────────────────────────────────────────────────────

function Get-NormalizedPath {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char]'\', [char]'/')
}

function Invoke-Git {
    <#
    .DESCRIPTION
        Invoke git with an argument array. Returns PSCustomObject with
        ExitCode + Output (stderr merged). Does not throw.
    #>
    param([Parameter(Mandatory)][string[]]$Arguments)
    $stdout = & git @Arguments 2>&1
    $exit = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $exit
        Output   = ($stdout -join "`n")
    }
}

function Test-GitAvailable {
    return $null -ne (Get-Command git -ErrorAction SilentlyContinue)
}

function Resolve-BarePath {
    <#
    .DESCRIPTION
        From a worktree path, resolve its absolute git-common-dir. Returns
        $null if $WorktreePath isn't inside any git worktree. Uses the
        absolute path format so the result converges across linked
        worktrees (per the stored 'git common-dir resolution' memory).
    #>
    param([Parameter(Mandatory)][string]$WorktreePath)
    $r = Invoke-Git @('-C', $WorktreePath, 'rev-parse', '--path-format=absolute', '--git-common-dir')
    if ($r.ExitCode -ne 0) { return $null }
    $p = $r.Output.Trim()
    if ([string]::IsNullOrWhiteSpace($p)) { return $null }
    return Get-NormalizedPath $p
}

function Test-IsBareRepository {
    <#
    .DESCRIPTION
        Probe via `--git-dir`, never via cwd discovery. From a linked
        worktree, cwd discovery returns false even when the underlying
        repo IS bare. Both modes work via `--git-dir=<path>`.
    #>
    param([Parameter(Mandatory)][string]$BarePath)
    if (-not (Test-Path -LiteralPath $BarePath)) { return $false }
    $r = Invoke-Git @('--git-dir', $BarePath, 'rev-parse', '--is-bare-repository')
    if ($r.ExitCode -ne 0) { return $false }
    return ($r.Output.Trim() -eq 'true')
}

function Get-DefaultBranchFromBare {
    param([Parameter(Mandatory)][string]$BarePath)
    $r = Invoke-Git @('--git-dir', $BarePath, 'symbolic-ref', '--short', 'HEAD')
    if ($r.ExitCode -ne 0) { return $null }
    $b = $r.Output.Trim()
    if ([string]::IsNullOrWhiteSpace($b)) { return $null }
    return $b
}

function Get-WorktreeList {
    <#
    .DESCRIPTION
        Returns [pscustomobject[]] with Path + Branch for every worktree
        registered against the bare. Branch is empty for detached HEADs.
    #>
    param([Parameter(Mandatory)][string]$BarePath)
    $r = Invoke-Git @('--git-dir', $BarePath, 'worktree', 'list', '--porcelain')
    if ($r.ExitCode -ne 0) { return @() }
    $out = @()
    $cur = $null
    foreach ($line in ($r.Output -split "`n")) {
        $line = $line.TrimEnd("`r")
        if ($line -match '^worktree (.+)$') {
            if ($null -ne $cur) { $out += $cur }
            $cur = [pscustomobject]@{
                Path   = Get-NormalizedPath $Matches[1]
                Branch = ''
            }
        } elseif ($line -match '^branch refs/heads/(.+)$' -and $null -ne $cur) {
            $cur.Branch = $Matches[1]
        }
    }
    if ($null -ne $cur) { $out += $cur }
    return $out
}

function Find-MainWorktree {
    <#
    .DESCRIPTION
        Returns the worktree on $MainBranch. Zero or multiple matches are
        the caller's problem — returns:
          0 matches → @{ Status='none';     Worktrees=@() }
          1 match   → @{ Status='ok';       Worktree=...; Worktrees=@(...) }
          N matches → @{ Status='multiple'; Worktrees=@(...) }
    #>
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$MainBranch
    )
    $all = Get-WorktreeList -BarePath $BarePath
    $matches = @($all | Where-Object { $_.Branch -eq $MainBranch })
    if ($matches.Count -eq 0) {
        return [pscustomobject]@{ Status = 'none'; Worktrees = $matches }
    }
    if ($matches.Count -gt 1) {
        return [pscustomobject]@{ Status = 'multiple'; Worktrees = $matches }
    }
    return [pscustomobject]@{ Status = 'ok'; Worktree = $matches[0]; Worktrees = $matches }
}

function Test-WorktreeClean {
    <#
    .DESCRIPTION
        True iff $WorktreePath has no uncommitted or untracked changes.
        Uses `status --porcelain` (any non-empty output = dirty).
    #>
    param([Parameter(Mandatory)][string]$WorktreePath)
    $r = Invoke-Git @('-C', $WorktreePath, 'status', '--porcelain')
    if ($r.ExitCode -ne 0) { return $false }
    return [string]::IsNullOrWhiteSpace($r.Output)
}

function Test-RefExists {
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$Ref
    )
    $r = Invoke-Git @('--git-dir', $BarePath, 'rev-parse', '--verify', "$Ref^{commit}")
    return ($r.ExitCode -eq 0)
}

function Test-IsAncestor {
    <#
    .DESCRIPTION
        True iff $Ancestor is an ancestor of $Descendant. Wrapper around
        `git merge-base --is-ancestor` exit codes (0 = is ancestor, 1 =
        not, other = error).
    #>
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$Ancestor,
        [Parameter(Mandatory)][string]$Descendant
    )
    $r = Invoke-Git @('--git-dir', $BarePath, 'merge-base', '--is-ancestor', $Ancestor, $Descendant)
    return ($r.ExitCode -eq 0)
}

function Test-TreesEqual {
    <#
    .DESCRIPTION
        True iff the two refs have identical tree content (safe condition
        for `reset --hard` to swap one for the other). Uses
        `diff --quiet --exit-code` so we branch on exit code, not output
        parsing.
    #>
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$A,
        [Parameter(Mandatory)][string]$B
    )
    $r = Invoke-Git @('--git-dir', $BarePath, 'diff', '--quiet', '--exit-code', $A, $B)
    return ($r.ExitCode -eq 0)
}

function Get-MainAction {
    <#
    .DESCRIPTION
        Classify what to do with the main branch. Returns a [pscustomobject]
        with Action (one of $script:MAIN_*) + Reason.
    #>
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$MainBranch,
        [Parameter(Mandatory)][string]$MainWorktreePath
    )
    if (-not (Test-WorktreeClean -WorktreePath $MainWorktreePath)) {
        return [pscustomobject]@{
            Action = $script:MAIN_REFUSE_DIRTY
            Reason = "Main worktree '$MainWorktreePath' has uncommitted or untracked changes. Commit, stash, or discard them before re-running."
        }
    }
    $local  = "refs/heads/$MainBranch"
    $remote = "refs/remotes/origin/$MainBranch"
    $rLocal  = Invoke-Git @('--git-dir', $BarePath, 'rev-parse', '--verify', $local)
    $rRemote = Invoke-Git @('--git-dir', $BarePath, 'rev-parse', '--verify', $remote)
    if ($rLocal.ExitCode -ne 0 -or $rRemote.ExitCode -ne 0) {
        # Caller should have preflighted this; defensive.
        return [pscustomobject]@{
            Action = $script:MAIN_REFUSE_DIVERGED
            Reason = "Could not resolve local '$local' or remote '$remote' after fetch."
        }
    }
    $sLocal  = $rLocal.Output.Trim()
    $sRemote = $rRemote.Output.Trim()
    if ($sLocal -eq $sRemote) {
        return [pscustomobject]@{
            Action = $script:MAIN_NOOP
            Reason = "Local $MainBranch already equals origin/$MainBranch ($sLocal)."
        }
    }
    if (Test-IsAncestor -BarePath $BarePath -Ancestor $local -Descendant $remote) {
        return [pscustomobject]@{
            Action = $script:MAIN_FAST_FORWARD
            Reason = "Local $MainBranch is behind origin/$MainBranch — fast-forward."
        }
    }
    if (Test-IsAncestor -BarePath $BarePath -Ancestor $remote -Descendant $local) {
        return [pscustomobject]@{
            Action = $script:MAIN_REFUSE_AHEAD
            Reason = "Local $MainBranch is ahead of origin/$MainBranch with commits not on the remote. Push or rebase manually."
        }
    }
    # Diverged: trees equal -> squash-merge case; trees differ -> refuse.
    if (Test-TreesEqual -BarePath $BarePath -A $local -B $remote) {
        return [pscustomobject]@{
            Action = $script:MAIN_SQUASH_RESET
            Reason = "Local $MainBranch and origin/$MainBranch have diverged history but identical content (squash-merge case)."
        }
    }
    return [pscustomobject]@{
        Action = $script:MAIN_REFUSE_DIVERGED
        Reason = "Local $MainBranch diverges in content from origin/$MainBranch. Resolve manually (rebase or merge)."
    }
}

function Get-PrunableBranches {
    <#
    .DESCRIPTION
        Enumerate local branches that are safe to delete: BOTH
          - upstream tracking ref is `[gone]` (was pruned by fetch)
          - branch is an ancestor of $MainBranch (its commits are in main)
        Branches checked out in any worktree are returned as 'skipped'
        rather than 'prunable'.

        Returns a [pscustomobject] with Prunable + Skipped + Preserved
        arrays. Each entry has Name + Reason.

        Uses NUL-delimited for-each-ref format to avoid collisions with
        branch names containing pipes or whitespace.
    #>
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$MainBranch
    )
    $prunable = @()
    $skipped = @()
    $preserved = @()

    $worktrees = Get-WorktreeList -BarePath $BarePath
    $checkedOut = @{}
    foreach ($w in $worktrees) {
        if (-not [string]::IsNullOrWhiteSpace($w.Branch)) {
            $checkedOut[$w.Branch] = $w.Path
        }
    }

    # NUL-delimited fields: name, upstream, track
    $fmt = '%(refname:short)%00%(upstream:short)%00%(upstream:track)'
    $r = Invoke-Git @('--git-dir', $BarePath, 'for-each-ref', "--format=$fmt", 'refs/heads/')
    if ($r.ExitCode -ne 0) {
        return [pscustomobject]@{ Prunable = $prunable; Skipped = $skipped; Preserved = $preserved }
    }
    foreach ($line in ($r.Output -split "`n")) {
        $line = $line.TrimEnd("`r")
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split "`0"
        if ($parts.Count -lt 3) { continue }
        $name = $parts[0]
        $upstream = $parts[1]
        $track = $parts[2]
        if ($name -eq $MainBranch) { continue }
        $isGone = ($track -like '*[[]gone[]]*')
        if (-not $isGone) {
            # Has live upstream or no upstream at all — leave alone.
            $preserved += [pscustomobject]@{
                Name = $name
                Reason = if ([string]::IsNullOrWhiteSpace($upstream)) { "no upstream configured" } else { "upstream '$upstream' still exists" }
            }
            continue
        }
        # Upstream is gone. Now: is the branch merged into main?
        $merged = Test-IsAncestor -BarePath $BarePath -Ancestor "refs/heads/$name" -Descendant "refs/heads/$MainBranch"
        if (-not $merged) {
            $preserved += [pscustomobject]@{
                Name = $name
                Reason = "upstream gone, BUT branch has commits not in $MainBranch — refusing to delete unmerged work"
            }
            continue
        }
        if ($checkedOut.ContainsKey($name)) {
            $skipped += [pscustomobject]@{
                Name = $name
                Reason = "checked out in worktree '$($checkedOut[$name])'"
            }
            continue
        }
        $prunable += [pscustomobject]@{
            Name = $name
            Reason = "upstream gone + merged into $MainBranch"
        }
    }
    return [pscustomobject]@{ Prunable = $prunable; Skipped = $skipped; Preserved = $preserved }
}

function Format-Plan {
    param(
        [Parameter(Mandatory)][pscustomobject]$MainCls,
        [Parameter(Mandatory)][pscustomobject]$Prune,
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$MainBranch,
        [Parameter(Mandatory)][string]$MainWorktreePath,
        [switch]$NoPrune
    )
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("Main reconcile: $($MainCls.Action)")
    [void]$sb.AppendLine("  $($MainCls.Reason)")
    switch ($MainCls.Action) {
        'fast_forward' {
            [void]$sb.AppendLine("  Would run: git -C `"$MainWorktreePath`" merge --ff-only origin/$MainBranch")
        }
        'squash_reset' {
            [void]$sb.AppendLine("  Would run: git -C `"$MainWorktreePath`" reset --hard origin/$MainBranch")
        }
    }
    [void]$sb.AppendLine("")
    if ($NoPrune) {
        [void]$sb.AppendLine("Prune merged local branches: SKIPPED (-NoPrune)")
    } else {
        [void]$sb.AppendLine("Prune merged local branches:")
        if ($Prune.Prunable.Count -eq 0) {
            [void]$sb.AppendLine("  (none)")
        } else {
            foreach ($b in $Prune.Prunable) {
                [void]$sb.AppendLine("  - $($b.Name)  ($($b.Reason))")
            }
        }
        if ($Prune.Skipped.Count -gt 0) {
            [void]$sb.AppendLine("  Skipped (would prune but cannot):")
            foreach ($b in $Prune.Skipped) {
                [void]$sb.AppendLine("    - $($b.Name)  ($($b.Reason))")
            }
        }
        if ($Prune.Preserved.Count -gt 0) {
            [void]$sb.AppendLine("  Preserved (not eligible):")
            foreach ($b in $Prune.Preserved) {
                [void]$sb.AppendLine("    - $($b.Name)  ($($b.Reason))")
            }
        }
    }
    return $sb.ToString().TrimEnd()
}

function Invoke-MainReconcile {
    <#
    .DESCRIPTION
        Execute the main-branch action. Returns $true on success or
        throws on failure.
    #>
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$MainBranch,
        [Parameter(Mandatory)][string]$MainWorktreePath,
        [Parameter(Mandatory)][string]$Action
    )
    switch ($Action) {
        'noop' { return $true }
        'fast_forward' {
            Write-Host "  [main] git -C $MainWorktreePath merge --ff-only origin/$MainBranch" -ForegroundColor Cyan
            $r = Invoke-Git @('-C', $MainWorktreePath, 'merge', '--ff-only', "origin/$MainBranch")
            if ($r.ExitCode -ne 0) { throw "git merge --ff-only failed: $($r.Output)" }
            return $true
        }
        'squash_reset' {
            Write-Host "  [main] git -C $MainWorktreePath reset --hard origin/$MainBranch" -ForegroundColor Cyan
            $r = Invoke-Git @('-C', $MainWorktreePath, 'reset', '--hard', "origin/$MainBranch")
            if ($r.ExitCode -ne 0) { throw "git reset --hard failed: $($r.Output)" }
            return $true
        }
        default {
            throw "Internal: Invoke-MainReconcile called with non-executable action '$Action'."
        }
    }
}

function Invoke-PruneBranches {
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [pscustomobject[]]$Prunable = @()
    )
    $deleted = @()
    foreach ($b in $Prunable) {
        Write-Host "  [prune] git --git-dir=$BarePath branch -D $($b.Name)" -ForegroundColor Cyan
        $r = Invoke-Git @('--git-dir', $BarePath, 'branch', '-D', $b.Name)
        if ($r.ExitCode -ne 0) {
            throw "git branch -D $($b.Name) failed: $($r.Output)"
        }
        $deleted += $b.Name
    }
    return $deleted
}

function Format-Summary {
    param(
        [Parameter(Mandatory)][string]$MainAction,
        [string[]]$Pruned = @(),
        [pscustomobject[]]$Skipped = @(),
        [switch]$NoPrune
    )
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Sync complete.")
    [void]$sb.AppendLine("  Main : $MainAction")
    if ($NoPrune) {
        [void]$sb.AppendLine("  Prune: SKIPPED (-NoPrune)")
    } else {
        [void]$sb.AppendLine("  Pruned: $($Pruned.Count)")
        foreach ($n in $Pruned) {
            [void]$sb.AppendLine("    - $n")
        }
        if ($Skipped.Count -gt 0) {
            [void]$sb.AppendLine("  Skipped: $($Skipped.Count)")
            foreach ($b in $Skipped) {
                [void]$sb.AppendLine("    - $($b.Name)  ($($b.Reason))")
            }
        }
    }
    return $sb.ToString().TrimEnd()
}

function Invoke-Sync {
    param(
        [string]$WorktreePath,
        [string]$MainBranch,
        [switch]$NoPrune,
        [switch]$Commit
    )

    Write-Host "Sync-BareRepo (#419) — worktree: $WorktreePath" -ForegroundColor Green

    if (-not (Test-GitAvailable)) {
        Write-Host "ERROR: git not found on PATH." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    if ([string]::IsNullOrWhiteSpace($WorktreePath)) {
        $WorktreePath = (Get-Location).Path
    }
    if (-not (Test-Path -LiteralPath $WorktreePath -PathType Container)) {
        Write-Host "ERROR: -WorktreePath '$WorktreePath' is not a directory." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }
    $WorktreePath = Get-NormalizedPath $WorktreePath

    $bare = Resolve-BarePath -WorktreePath $WorktreePath
    if ($null -eq $bare) {
        Write-Host "ERROR: '$WorktreePath' is not inside a git worktree." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }
    if (-not (Test-IsBareRepository -BarePath $bare)) {
        Write-Host "ERROR: common-dir '$bare' is not a bare repository. This script supports the bare-repo + worktree layout only." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    if ([string]::IsNullOrWhiteSpace($MainBranch)) {
        $MainBranch = Get-DefaultBranchFromBare -BarePath $bare
        if ([string]::IsNullOrWhiteSpace($MainBranch)) {
            Write-Host "ERROR: bare '$bare' has no symbolic HEAD; pass -MainBranch." -ForegroundColor Red
            return $script:EXIT_PREFLIGHT_FAILURE
        }
    }

    # Identify main worktree before doing anything destructive.
    $mw = Find-MainWorktree -BarePath $bare -MainBranch $MainBranch
    switch ($mw.Status) {
        'none' {
            Write-Host "ERROR: no worktree of bare '$bare' is on '$MainBranch'. Cannot reconcile main." -ForegroundColor Red
            return $script:EXIT_PREFLIGHT_FAILURE
        }
        'multiple' {
            Write-Host "ERROR: multiple worktrees on '$MainBranch':" -ForegroundColor Red
            foreach ($w in $mw.Worktrees) { Write-Host "  - $($w.Path)" -ForegroundColor Red }
            Write-Host "Refusing to pick one — resolve manually." -ForegroundColor Red
            return $script:EXIT_PREFLIGHT_FAILURE
        }
    }
    $mainWorktree = $mw.Worktree.Path

    Write-Host ""
    Write-Host "Resolved:" -ForegroundColor Cyan
    Write-Host "  Bare repo     : $bare"
    Write-Host "  Main branch   : $MainBranch"
    Write-Host "  Main worktree : $mainWorktree"
    Write-Host ""

    # Fetch (needs to happen even in dry-run so we classify on the latest
    # remote state; --dry-run on fetch wouldn't update tracking refs and
    # would defeat the purpose of the classification).
    Write-Host "Fetching: git --git-dir=$bare fetch --all --prune" -ForegroundColor Cyan
    $rFetch = Invoke-Git @('--git-dir', $bare, 'fetch', '--all', '--prune')
    if ($rFetch.ExitCode -ne 0) {
        Write-Host "ERROR: fetch failed: $($rFetch.Output)" -ForegroundColor Red
        return $script:EXIT_EXECUTION_FAILURE
    }

    if (-not (Test-RefExists -BarePath $bare -Ref "refs/heads/$MainBranch")) {
        Write-Host "ERROR: local branch '$MainBranch' has no commit." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }
    if (-not (Test-RefExists -BarePath $bare -Ref "refs/remotes/origin/$MainBranch")) {
        Write-Host "ERROR: 'origin/$MainBranch' does not exist after fetch. The remote may have renamed or deleted the branch." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    $mainCls = Get-MainAction -BarePath $bare -MainBranch $MainBranch -MainWorktreePath $mainWorktree

    if (-not $NoPrune) {
        $prune = Get-PrunableBranches -BarePath $bare -MainBranch $MainBranch
    } else {
        $prune = [pscustomobject]@{ Prunable = @(); Skipped = @(); Preserved = @() }
    }

    Write-Host ""
    Write-Host (Format-Plan -MainCls $mainCls -Prune $prune -BarePath $bare -MainBranch $MainBranch -MainWorktreePath $mainWorktree -NoPrune:$NoPrune)
    Write-Host ""

    $refused = ($mainCls.Action -eq $script:MAIN_REFUSE_DIRTY) `
            -or ($mainCls.Action -eq $script:MAIN_REFUSE_AHEAD) `
            -or ($mainCls.Action -eq $script:MAIN_REFUSE_DIVERGED)
    if ($refused) {
        Write-Host "REFUSED: $($mainCls.Action). Fix the condition above and re-run." -ForegroundColor Red
        return $script:EXIT_REFUSED
    }

    if (-not $Commit) {
        Write-Host "DRY-RUN: re-run with -Commit to execute." -ForegroundColor Yellow
        return $script:EXIT_SUCCESS
    }

    try {
        [void](Invoke-MainReconcile -BarePath $bare -MainBranch $MainBranch -MainWorktreePath $mainWorktree -Action $mainCls.Action)
        $pruned = @()
        if (-not $NoPrune -and $prune.Prunable.Count -gt 0) {
            $pruned = Invoke-PruneBranches -BarePath $bare -Prunable $prune.Prunable
        }
    } catch {
        Write-Host ""
        Write-Host "ERROR: $_" -ForegroundColor Red
        return $script:EXIT_EXECUTION_FAILURE
    }

    Write-Host (Format-Summary -MainAction $mainCls.Action -Pruned $pruned -Skipped $prune.Skipped -NoPrune:$NoPrune)
    return $script:EXIT_SUCCESS
}

# ── Entry point: execute only when invoked directly, not when dot-sourced ──
if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-Sync -WorktreePath $WorktreePath -MainBranch $MainBranch -NoPrune:$NoPrune -Commit:$Commit)
}
