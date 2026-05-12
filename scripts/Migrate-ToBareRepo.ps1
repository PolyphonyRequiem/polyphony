#requires -Version 7.0

<#
.SYNOPSIS
    Migrate the operator's polyphony clone to the bare-repo + per-run worktree
    layout described in docs/per-run-worktree-layout.md (AB#3085).

.DESCRIPTION
    Converts ~/projects/polyphony (a normal clone with embedded .git) into the
    layout the SDLC orchestrator requires:

        ~/projects/polyphony.git/         bare repo (objects + refs only)
        ~/projects/polyphony/             main worktree, ALWAYS on `main`
        ~/projects/polyphony-runs/        per-apex worktrees live here

    Strategy: fresh-clone (no in-place .git surgery). The script bare-clones
    from the operator's existing remote, adds a fresh main worktree, then
    swaps the operator path with the new clone in a single rename pair. The
    legacy clone is renamed to `<OperatorPath>.legacy/` (NEVER deleted) so
    that hooks, IDE config, stashes, untracked files, and local-only branches
    can be salvaged manually by the operator.

    Without `-Commit`, the script runs in DRY-RUN mode: it prints the plan
    (as copy-pastable commands), reports any risk factors, and exits 0.
    With `-Commit`, it executes the plan after gating on risk + path
    conflicts. `-Force` overrides the risk gate but NEVER overrides the
    path-conflict or extra-worktree refusals.

    Re-running on an already-migrated layout is a no-op success. Partial
    states (bare exists but operator path is wrong) are detected and reported
    as failures with remediation pointers.

.PARAMETER OperatorPath
    Path to the existing operator clone. Default: `~/projects/polyphony`.

.PARAMETER RemoteUrl
    Git remote URL to clone from. When omitted, auto-detected from the
    operator clone's `origin`. Required if the operator clone is missing or
    has no `origin`.

.PARAMETER Commit
    Without this switch, the script runs in DRY-RUN mode and exits 0 after
    printing the plan. With this switch, the script executes the migration
    after gating on risk + path conflicts.

.PARAMETER Force
    Overrides the risk-material refusal in `-Commit` mode (uncommitted
    changes, stashes, untracked files, local-only branches). DOES NOT
    override path-conflict or extra-worktree refusals — those are always
    fatal because they would silently break operator state.

.OUTPUTS
    Writes a human-readable plan / progress / next-steps narrative to the
    host. Exit codes:
      0  success or already-migrated or dry-run
      1  preflight failure (git missing, OperatorPath malformed)
      2  risk material present without -Force
      3  target path conflict (bare/legacy/new/runs exist non-emptily)
      4  execution failure (clone, worktree add, or move failed)

.EXAMPLE
    PS> .\scripts\Migrate-ToBareRepo.ps1
    Dry-run: prints plan + risk report.

.EXAMPLE
    PS> .\scripts\Migrate-ToBareRepo.ps1 -Commit
    Executes migration. Refuses if risk material present.

.EXAMPLE
    PS> .\scripts\Migrate-ToBareRepo.ps1 -Commit -Force
    Executes migration with risk material acknowledged. Path conflicts and
    extra worktrees still refuse.
#>
param(
    [string]$OperatorPath = (Join-Path $HOME 'projects/polyphony'),
    [string]$RemoteUrl = '',
    [switch]$Commit,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# ── Exit code constants ──────────────────────────────────────────────────────
$script:EXIT_SUCCESS = 0
$script:EXIT_PREFLIGHT_FAILURE = 1
$script:EXIT_RISK_MATERIAL = 2
$script:EXIT_PATH_CONFLICT = 3
$script:EXIT_EXECUTION_FAILURE = 4

# ── Helpers ──────────────────────────────────────────────────────────────────

function Get-NormalizedPath {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char]'\', [char]'/')
}

function Get-DerivedPaths {
    param([Parameter(Mandatory)][string]$OperatorPath)
    $op = Get-NormalizedPath $OperatorPath
    $parent = Split-Path -Parent $op
    $leaf = Split-Path -Leaf $op
    return [pscustomobject]@{
        OperatorPath = $op
        ParentDir    = $parent
        BarePath     = Join-Path $parent "$leaf.git"
        LegacyPath   = Join-Path $parent "$leaf.legacy"
        NewPath      = Join-Path $parent "$leaf.new"
        RunsRoot     = Join-Path $parent "$leaf-runs"
    }
}

function Invoke-Git {
    <#
    .DESCRIPTION
        Invoke git with an argument array (never string concatenation). Returns
        a PSCustomObject with ExitCode, Stdout, Stderr. Does not throw — caller
        decides how to handle non-zero exit.
    #>
    param([Parameter(Mandatory)][string[]]$Arguments)
    $stdout = & git @Arguments 2>&1
    $exit = $LASTEXITCODE
    # & captures combined output via 2>&1; for our purposes, treat all of it as
    # stdout-or-stderr and let the caller pattern-match. Most git diagnostics
    # we care about live in stderr but the merged form is sufficient.
    return [pscustomobject]@{
        ExitCode = $exit
        Output   = ($stdout -join "`n")
    }
}

function Test-GitAvailable {
    return $null -ne (Get-Command git -ErrorAction SilentlyContinue)
}

function Test-IsBareRepository {
    <#
    .DESCRIPTION
        Probe via `--git-dir`, NEVER via cwd discovery. From a linked worktree
        of a bare repo, cwd discovery returns false (the worktree-specific
        gitdir is non-bare). And `safe.bareRepository=explicit` rejects
        `git -C <bare>` entirely. Both modes work via `--git-dir=<path>`.
    #>
    param([Parameter(Mandatory)][string]$BarePath)
    if (-not (Test-Path -LiteralPath $BarePath)) { return $false }
    $r = Invoke-Git @('--git-dir', $BarePath, 'rev-parse', '--is-bare-repository')
    if ($r.ExitCode -ne 0) { return $false }
    return ($r.Output.Trim() -eq 'true')
}

function Get-WorktreeList {
    <#
    .DESCRIPTION
        Return [pscustomobject[]] with Path, Head, Branch for every worktree
        registered against $RepoArg ('-C <path>' for normal repo, '--git-dir
        <bare>' for bare). Caller passes the right args.
    #>
    param([Parameter(Mandatory)][string[]]$RepoArgs)
    $gitArgs = $RepoArgs + @('worktree', 'list', '--porcelain')
    $r = Invoke-Git $gitArgs
    if ($r.ExitCode -ne 0) { return @() }

    $entries = @()
    $current = $null
    foreach ($line in ($r.Output -split "`r?`n")) {
        if ($line -match '^worktree (.+)$') {
            if ($current) { $entries += $current }
            $current = [pscustomobject]@{
                Path   = $matches[1]
                Head   = ''
                Branch = ''
            }
        } elseif ($line -match '^HEAD (.+)$' -and $current) {
            $current.Head = $matches[1]
        } elseif ($line -match '^branch (.+)$' -and $current) {
            $current.Branch = $matches[1]
        }
    }
    if ($current) { $entries += $current }
    return $entries
}

function Test-MigrationAlreadyComplete {
    <#
    .DESCRIPTION
        Returns a status object describing whether the target layout is already
        in place. Possible Status values: 'complete', 'partial', 'absent'.
        On 'partial', Reasons[] explains what's missing.
    #>
    param([Parameter(Mandatory)][pscustomobject]$Paths)

    $bareExists = Test-Path -LiteralPath $Paths.BarePath
    $opExists = Test-Path -LiteralPath $Paths.OperatorPath
    $runsExists = Test-Path -LiteralPath $Paths.RunsRoot

    if (-not $bareExists -and -not $opExists -and -not $runsExists) {
        return [pscustomobject]@{ Status = 'absent'; Reasons = @() }
    }

    if (-not $bareExists) {
        return [pscustomobject]@{ Status = 'absent'; Reasons = @() }
    }

    $reasons = @()

    if (-not (Test-IsBareRepository -BarePath $Paths.BarePath)) {
        $reasons += "$($Paths.BarePath) exists but is not a bare repository."
    }

    if (-not $opExists) {
        $reasons += "Operator path $($Paths.OperatorPath) is missing."
    } else {
        # Operator path exists: must be a worktree of the bare AND on `main`.
        $worktrees = Get-WorktreeList -RepoArgs @('--git-dir', $Paths.BarePath)
        $opNorm = Get-NormalizedPath $Paths.OperatorPath
        $found = $worktrees | Where-Object { (Get-NormalizedPath $_.Path) -eq $opNorm }
        if (-not $found) {
            $reasons += "Operator path $($Paths.OperatorPath) is not a registered worktree of bare $($Paths.BarePath)."
        } elseif ($found.Branch -ne 'refs/heads/main') {
            $reasons += "Operator worktree is on $($found.Branch); expected refs/heads/main."
        }
    }

    if ($reasons.Count -eq 0 -and -not $runsExists) {
        # Runs root is the only thing missing — benign, will be created on -Commit.
        return [pscustomobject]@{ Status = 'complete'; Reasons = @('runs_root_missing') }
    }

    if ($reasons.Count -eq 0) {
        return [pscustomobject]@{ Status = 'complete'; Reasons = @() }
    }

    return [pscustomobject]@{ Status = 'partial'; Reasons = $reasons }
}

function Test-OperatorClone {
    <#
    .DESCRIPTION
        Validate that $OperatorPath is the root of a non-bare clone with an
        embedded .git directory (NOT a worktree gitfile, NOT a subdirectory).
        Returns Ok=$true on success; Ok=$false + Reason on failure.
    #>
    param([Parameter(Mandatory)][string]$OperatorPath)

    if (-not (Test-Path -LiteralPath $OperatorPath)) {
        return [pscustomobject]@{ Ok = $false; Reason = "Operator path $OperatorPath does not exist." }
    }

    $gitDirPath = Join-Path $OperatorPath '.git'
    if (-not (Test-Path -LiteralPath $gitDirPath)) {
        return [pscustomobject]@{ Ok = $false; Reason = "$OperatorPath has no .git directory (not a git working copy)." }
    }

    # .git must be a DIRECTORY (worktree gitfiles are files referencing the common-dir).
    $gitItem = Get-Item -LiteralPath $gitDirPath -Force
    if (-not $gitItem.PSIsContainer) {
        return [pscustomobject]@{ Ok = $false; Reason = "$OperatorPath/.git is a file, not a directory — appears to be a linked worktree, not a primary clone. Migrate the primary clone instead." }
    }

    $r = Invoke-Git @('-C', $OperatorPath, 'rev-parse', '--show-toplevel')
    if ($r.ExitCode -ne 0) {
        return [pscustomobject]@{ Ok = $false; Reason = "git rev-parse --show-toplevel failed in ${OperatorPath}: $($r.Output)" }
    }
    $toplevel = Get-NormalizedPath $r.Output.Trim()
    $opNorm = Get-NormalizedPath $OperatorPath
    if ($toplevel -ne $opNorm) {
        return [pscustomobject]@{ Ok = $false; Reason = "$OperatorPath is a subdirectory of repo at $toplevel; pass the repo root via -OperatorPath." }
    }

    $r2 = Invoke-Git @('-C', $OperatorPath, 'rev-parse', '--is-bare-repository')
    if ($r2.ExitCode -ne 0 -or $r2.Output.Trim() -ne 'false') {
        return [pscustomobject]@{ Ok = $false; Reason = "$OperatorPath is already a bare repository or rev-parse failed: $($r2.Output)" }
    }

    return [pscustomobject]@{ Ok = $true; Reason = '' }
}

function Get-RiskFactors {
    <#
    .DESCRIPTION
        Inspect the operator clone for state that will be moved to .legacy/
        but NOT carried into the new bare layout. Returns a structured report.

        Note: extra worktrees are reported but actually treated as a HARD
        REFUSAL by Get-PathConflicts, since they will silently break.
    #>
    param([Parameter(Mandatory)][string]$OperatorPath)

    $report = [pscustomobject]@{
        UncommittedChanges = @()
        Stashes            = @()
        UntrackedCount     = 0
        LocalOnlyBranches  = @()
        ExtraWorktrees     = @()
    }

    $r = Invoke-Git @('-C', $OperatorPath, 'status', '--porcelain')
    if ($r.ExitCode -eq 0) {
        $lines = ($r.Output -split "`r?`n") | Where-Object { $_ -ne '' }
        $untracked = $lines | Where-Object { $_ -match '^\?\?' }
        $tracked = $lines | Where-Object { $_ -notmatch '^\?\?' }
        $report.UncommittedChanges = @($tracked)
        $report.UntrackedCount = @($untracked).Count
    }

    $r = Invoke-Git @('-C', $OperatorPath, 'stash', 'list')
    if ($r.ExitCode -eq 0) {
        $report.Stashes = @(($r.Output -split "`r?`n") | Where-Object { $_ -ne '' })
    }

    # Local branches NOT also at origin/{name} or origin/HEAD targets.
    $r = Invoke-Git @('-C', $OperatorPath, 'for-each-ref', '--format=%(refname:short)', 'refs/heads/')
    if ($r.ExitCode -eq 0) {
        $localBranches = ($r.Output -split "`r?`n") | Where-Object { $_ -ne '' }
        $localOnly = @()
        foreach ($b in $localBranches) {
            $check = Invoke-Git @('-C', $OperatorPath, 'rev-parse', '--verify', '--quiet', "refs/remotes/origin/$b")
            if ($check.ExitCode -ne 0) {
                $localOnly += $b
            }
        }
        $report.LocalOnlyBranches = $localOnly
    }

    $worktrees = Get-WorktreeList -RepoArgs @('-C', $OperatorPath)
    $opNorm = Get-NormalizedPath $OperatorPath
    $report.ExtraWorktrees = @($worktrees | Where-Object { (Get-NormalizedPath $_.Path) -ne $opNorm })

    return $report
}

function Get-PathConflicts {
    <#
    .DESCRIPTION
        Returns a list of fatal conflicts that block migration: existing
        bare/legacy/new paths, or a non-empty runs root. Empty runs-root is
        OK (treated as a no-op create later). Extra worktrees are also fatal
        and reported here so callers don't accidentally let -Force bypass them.
    #>
    param(
        [Parameter(Mandatory)][pscustomobject]$Paths,
        [Parameter(Mandatory)][pscustomobject]$RiskFactors
    )
    $conflicts = @()
    if (Test-Path -LiteralPath $Paths.BarePath)   { $conflicts += "Bare path already exists: $($Paths.BarePath)" }
    if (Test-Path -LiteralPath $Paths.LegacyPath) { $conflicts += "Legacy path already exists: $($Paths.LegacyPath)" }
    if (Test-Path -LiteralPath $Paths.NewPath)    { $conflicts += "Staging path already exists: $($Paths.NewPath)" }
    if (Test-Path -LiteralPath $Paths.RunsRoot) {
        $contents = Get-ChildItem -LiteralPath $Paths.RunsRoot -Force -ErrorAction SilentlyContinue
        if ($contents) {
            $conflicts += "Runs root already exists and is non-empty: $($Paths.RunsRoot)"
        }
    }
    if ($RiskFactors.ExtraWorktrees.Count -gt 0) {
        $list = ($RiskFactors.ExtraWorktrees | ForEach-Object { "  - $($_.Path) (branch: $($_.Branch))" }) -join "`n"
        $conflicts += @"
Operator clone has linked worktrees other than itself:
$list
Migrating would silently break these (their gitfiles point at the soon-to-be-renamed common-dir).
Remove them first: ``git -C "$($Paths.OperatorPath)" worktree remove <path>`` then ``git -C "$($Paths.OperatorPath)" worktree prune``.
"@
    }
    return $conflicts
}

function Resolve-RemoteUrl {
    param(
        [string]$Explicit,
        [string]$OperatorPath
    )
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { return $Explicit }
    if (-not (Test-Path -LiteralPath $OperatorPath)) { return $null }
    $r = Invoke-Git @('-C', $OperatorPath, 'remote', 'get-url', 'origin')
    if ($r.ExitCode -ne 0) { return $null }
    return $r.Output.Trim()
}

function Format-Plan {
    param([Parameter(Mandatory)][pscustomobject]$Paths, [Parameter(Mandatory)][string]$RemoteUrl)
    return @"
Plan (copy-pastable):
  git clone --bare "$RemoteUrl" "$($Paths.BarePath)"
  git --git-dir="$($Paths.BarePath)" worktree add "$($Paths.NewPath)" main
  Move-Item -LiteralPath "$($Paths.OperatorPath)" -Destination "$($Paths.LegacyPath)"
  Move-Item -LiteralPath "$($Paths.NewPath)" -Destination "$($Paths.OperatorPath)"
  New-Item -ItemType Directory -Path "$($Paths.RunsRoot)" -Force | Out-Null
"@
}

function Format-RiskReport {
    param([Parameter(Mandatory)][pscustomobject]$Risk, [Parameter(Mandatory)][string]$LegacyPath)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("Risk material in operator clone (will be preserved under $LegacyPath but NOT carried into the new bare layout — copy manually if needed):")
    [void]$sb.AppendLine("  Uncommitted (tracked) changes : $($Risk.UncommittedChanges.Count)")
    [void]$sb.AppendLine("  Untracked files               : $($Risk.UntrackedCount)")
    [void]$sb.AppendLine("  Stashes                       : $($Risk.Stashes.Count)")
    [void]$sb.AppendLine("  Local-only branches           : $($Risk.LocalOnlyBranches.Count)")
    if ($Risk.LocalOnlyBranches.Count -gt 0) {
        [void]$sb.AppendLine("    Branches:")
        foreach ($b in $Risk.LocalOnlyBranches) {
            [void]$sb.AppendLine("      - $b")
            [void]$sb.AppendLine("        Import: git --git-dir=`"<bare>`" fetch `"<legacy>/.git`" $b`:$b")
        }
    }
    return $sb.ToString().TrimEnd()
}

function Test-HasRiskMaterial {
    param([Parameter(Mandatory)][pscustomobject]$Risk)
    return ($Risk.UncommittedChanges.Count -gt 0) `
        -or ($Risk.UntrackedCount -gt 0) `
        -or ($Risk.Stashes.Count -gt 0) `
        -or ($Risk.LocalOnlyBranches.Count -gt 0)
}

function Invoke-MigrationExecution {
    <#
    .DESCRIPTION
        Performs the destructive phase. Validates the new bare + worktree
        BEFORE moving the operator clone. Wraps the move pair in try/catch
        with rollback. Tracks paths created so failure cleanup never deletes
        pre-existing user data.
    #>
    param(
        [Parameter(Mandatory)][pscustomobject]$Paths,
        [Parameter(Mandatory)][string]$RemoteUrl
    )

    $createdPaths = @()
    $opMoved = $false

    try {
        # 1. Bare clone.
        Write-Host "  [1/7] git clone --bare $RemoteUrl -> $($Paths.BarePath)" -ForegroundColor Cyan
        $r = Invoke-Git @('clone', '--bare', $RemoteUrl, $Paths.BarePath)
        if ($r.ExitCode -ne 0) { throw "git clone --bare failed: $($r.Output)" }
        $createdPaths += $Paths.BarePath

        # 2. Validate bare.
        Write-Host "  [2/7] Validating bare repository" -ForegroundColor Cyan
        if (-not (Test-IsBareRepository -BarePath $Paths.BarePath)) {
            throw "Cloned $($Paths.BarePath) but it is not bare per --is-bare-repository."
        }

        # 3. Add main worktree at staging path.
        Write-Host "  [3/7] git worktree add $($Paths.NewPath) main" -ForegroundColor Cyan
        $r = Invoke-Git @('--git-dir', $Paths.BarePath, 'worktree', 'add', $Paths.NewPath, 'main')
        if ($r.ExitCode -ne 0) { throw "git worktree add failed: $($r.Output)" }
        $createdPaths += $Paths.NewPath

        # 4. Validate new worktree.
        Write-Host "  [4/7] Validating new worktree" -ForegroundColor Cyan
        $r = Invoke-Git @('-C', $Paths.NewPath, 'rev-parse', '--show-toplevel')
        if ($r.ExitCode -ne 0) { throw "Validation: rev-parse --show-toplevel failed in $($Paths.NewPath): $($r.Output)" }
        $tl = Get-NormalizedPath $r.Output.Trim()
        if ($tl -ne (Get-NormalizedPath $Paths.NewPath)) {
            throw "Validation: new worktree toplevel $tl != expected $($Paths.NewPath)."
        }
        $r = Invoke-Git @('-C', $Paths.NewPath, 'branch', '--show-current')
        if ($r.ExitCode -ne 0 -or $r.Output.Trim() -ne 'main') {
            throw "Validation: new worktree is on '$($r.Output.Trim())', expected 'main'."
        }

        # 5. Atomic-as-possible swap. Track $opMoved so rollback knows what to undo.
        Write-Host "  [5/7] Swapping operator path with new worktree" -ForegroundColor Cyan
        Move-Item -LiteralPath $Paths.OperatorPath -Destination $Paths.LegacyPath
        $opMoved = $true
        try {
            Move-Item -LiteralPath $Paths.NewPath -Destination $Paths.OperatorPath
        } catch {
            # Rollback: put operator clone back.
            Write-Host "  ! Second move failed; attempting rollback" -ForegroundColor Yellow
            try {
                Move-Item -LiteralPath $Paths.LegacyPath -Destination $Paths.OperatorPath -ErrorAction Stop
                $opMoved = $false
                throw "Second move failed and operator clone was restored. New worktree remains at $($Paths.NewPath); inspect and remove manually. Original error: $_"
            } catch {
                throw @"
CRITICAL: Second move failed AND rollback failed.
Manual recovery commands:
  Move-Item -LiteralPath "$($Paths.LegacyPath)" -Destination "$($Paths.OperatorPath)"
  Remove-Item -Recurse -Force -LiteralPath "$($Paths.NewPath)"
  Remove-Item -Recurse -Force -LiteralPath "$($Paths.BarePath)"
Original error: $_
"@
            }
        }

        # 6. Repair the worktree pointers (gitfile + bare's worktrees/*/gitdir)
        #    after the rename — both ends still reference $NewPath. `worktree
        #    repair` is idempotent and the canonical fix for moved worktrees.
        Write-Host "  [6/7] git worktree repair $($Paths.OperatorPath)" -ForegroundColor Cyan
        $r = Invoke-Git @('--git-dir', $Paths.BarePath, 'worktree', 'repair', $Paths.OperatorPath)
        if ($r.ExitCode -ne 0) { throw "git worktree repair failed: $($r.Output)" }

        # 7. Ensure runs-root exists.
        Write-Host "  [7/7] Ensuring runs root: $($Paths.RunsRoot)" -ForegroundColor Cyan
        if (-not (Test-Path -LiteralPath $Paths.RunsRoot)) {
            New-Item -ItemType Directory -Path $Paths.RunsRoot -Force | Out-Null
            $createdPaths += $Paths.RunsRoot
        }

        return $true
    } catch {
        # Best-effort cleanup of paths we created on this run, IFF the operator
        # clone is still in place (i.e. we haven't reached the destructive moves
        # successfully). If $opMoved is true and we're in this catch, the upper
        # rethrow already happened — don't touch the swap state.
        if (-not $opMoved) {
            Write-Host "  ! Execution failed; cleaning up paths created on this run" -ForegroundColor Yellow
            foreach ($p in $createdPaths) {
                if (Test-Path -LiteralPath $p) {
                    Remove-Item -Recurse -Force -LiteralPath $p -ErrorAction SilentlyContinue
                }
            }
        }
        throw
    }
}

function Format-NextSteps {
    param([Parameter(Mandatory)][pscustomobject]$Paths)
    return @"

Migration complete.

  Bare repo     : $($Paths.BarePath)
  Main worktree : $($Paths.OperatorPath)  (always on main)
  Runs root     : $($Paths.RunsRoot)
  Legacy clone  : $($Paths.LegacyPath)  (NOT deleted)

Next steps:
  1. Re-run publish-local.ps1 if you have polyphony installed locally.
  2. The legacy clone preserves: git hooks (.git/hooks/), IDE config (.vscode, .idea, ...),
     stashes, untracked files, and local-only branches. Copy any you need:
       Copy-Item -Recurse "$($Paths.LegacyPath)/.vscode" "$($Paths.OperatorPath)/" -ErrorAction SilentlyContinue
       Copy-Item -Recurse "$($Paths.LegacyPath)/.git/hooks" "$($Paths.BarePath)/hooks" -ErrorAction SilentlyContinue
  3. Once you have everything you need from the legacy clone:
       Remove-Item -Recurse -Force "$($Paths.LegacyPath)"
  4. Verify with: polyphony state preflight --work-item <N>  (bare_repo check should PASS)
"@
}

function Invoke-Migration {
    param(
        [string]$OperatorPath,
        [string]$RemoteUrl,
        [switch]$Commit,
        [switch]$Force
    )

    Write-Host "Migrate-ToBareRepo (AB#3085) — operator path: $OperatorPath" -ForegroundColor Green

    if (-not (Test-GitAvailable)) {
        Write-Host "ERROR: git not found on PATH." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    $paths = Get-DerivedPaths -OperatorPath $OperatorPath

    # Phase 0: already-migrated detection.
    $migState = Test-MigrationAlreadyComplete -Paths $paths
    if ($migState.Status -eq 'complete') {
        if ($migState.Reasons -contains 'runs_root_missing') {
            if ($Commit) {
                Write-Host "Already migrated; creating missing runs root: $($paths.RunsRoot)" -ForegroundColor Green
                New-Item -ItemType Directory -Path $paths.RunsRoot -Force | Out-Null
            } else {
                Write-Host "Already migrated, but runs root missing: $($paths.RunsRoot). Re-run with -Commit to create it." -ForegroundColor Yellow
            }
        } else {
            Write-Host "Already migrated. Bare at $($paths.BarePath); main worktree at $($paths.OperatorPath); runs root at $($paths.RunsRoot)." -ForegroundColor Green
        }
        return $script:EXIT_SUCCESS
    }

    if ($migState.Status -eq 'partial') {
        Write-Host "ERROR: Partial migration state detected. Manual cleanup required:" -ForegroundColor Red
        foreach ($r in $migState.Reasons) { Write-Host "  - $r" -ForegroundColor Red }
        Write-Host "See docs/per-run-worktree-layout.md for the canonical layout." -ForegroundColor Red
        return $script:EXIT_PATH_CONFLICT
    }

    # Phase 0b: validate operator clone.
    $opCheck = Test-OperatorClone -OperatorPath $paths.OperatorPath
    if (-not $opCheck.Ok) {
        Write-Host "ERROR: $($opCheck.Reason)" -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    # Phase 0c: resolve remote URL.
    $remote = Resolve-RemoteUrl -Explicit $RemoteUrl -OperatorPath $paths.OperatorPath
    if (-not $remote) {
        Write-Host "ERROR: Could not auto-detect remote URL from origin in $($paths.OperatorPath). Pass -RemoteUrl explicitly." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    # Phase 1: risk scan.
    $risk = Get-RiskFactors -OperatorPath $paths.OperatorPath
    Write-Host ""
    Write-Host (Format-RiskReport -Risk $risk -LegacyPath $paths.LegacyPath)

    # Phase 2: plan emission + path conflict scan.
    $conflicts = Get-PathConflicts -Paths $paths -RiskFactors $risk
    Write-Host ""
    Write-Host (Format-Plan -Paths $paths -RemoteUrl $remote)

    if ($conflicts.Count -gt 0) {
        Write-Host ""
        Write-Host "ERROR: Path conflicts (must resolve manually; -Force does NOT bypass these):" -ForegroundColor Red
        foreach ($c in $conflicts) { Write-Host $c -ForegroundColor Red }
        return $script:EXIT_PATH_CONFLICT
    }

    # Phase 3: -Commit gate.
    if (-not $Commit) {
        Write-Host ""
        Write-Host "Dry-run complete. Re-run with -Commit to perform this migration." -ForegroundColor Green
        return $script:EXIT_SUCCESS
    }

    if ((Test-HasRiskMaterial -Risk $risk) -and -not $Force) {
        Write-Host ""
        Write-Host "ERROR: Risk material present (see report above). Re-run with -Commit -Force to proceed (legacy clone WILL be preserved at $($paths.LegacyPath))." -ForegroundColor Red
        return $script:EXIT_RISK_MATERIAL
    }

    # Phase 4: execute.
    Write-Host ""
    Write-Host "Executing migration..." -ForegroundColor Green
    try {
        [void](Invoke-MigrationExecution -Paths $paths -RemoteUrl $remote)
    } catch {
        Write-Host ""
        Write-Host "ERROR: Migration failed: $_" -ForegroundColor Red
        return $script:EXIT_EXECUTION_FAILURE
    }

    Write-Host (Format-NextSteps -Paths $paths) -ForegroundColor Green
    return $script:EXIT_SUCCESS
}

# ── Entry point: execute only when invoked directly, not when dot-sourced ──
if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-Migration -OperatorPath $OperatorPath -RemoteUrl $RemoteUrl -Commit:$Commit -Force:$Force)
}
