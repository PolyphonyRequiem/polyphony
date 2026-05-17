#requires -Version 7.0

<#
.SYNOPSIS
    Bootstrap a fresh-clone polyphony repo into the bare-repo + worktree
    layout described in docs/per-run-worktree-layout.md (#420).

.DESCRIPTION
    Sister script to scripts/Migrate-ToBareRepo.ps1: that one converts an
    EXISTING operator clone, this one starts from nothing.

    Produces the canonical layout for a fresh onboarding:

        <ParentDir>/<RepoName>.git/     bare repo (objects + refs only)
        <ParentDir>/<RepoName>/         main worktree, ALWAYS on the default branch
        <ParentDir>/<RepoName>-runs/    per-apex run worktrees live here (empty at bootstrap)

    Strategy: bare-clone the remote, query its HEAD (or use -MainBranch),
    add a worktree at <ParentDir>/<RepoName>/ on that branch, and create
    an empty <ParentDir>/<RepoName>-runs/ alongside.

    Without `-Commit`, the script runs in DRY-RUN mode: classifies what it
    WOULD do (full bootstrap / recovery / no-op / refusal), prints the plan
    or refusal reason, and exits 0. With `-Commit`, it executes.

    Re-running on a fully-bootstrapped layout is a no-op success. Partial
    states (bare present, main worktree missing) are recovered narrowly
    after verifying the existing bare's origin URL matches `-RemoteUrl`.
    Any identity mismatch (bare's origin != -RemoteUrl, or main path is
    a worktree of a different bare) is a hard refusal — no `-Force`
    override exists.

    Works on Windows with `safe.bareRepository=explicit` set globally:
    every git invocation against the bare uses `--git-dir=<bare>` and
    never `git -C <bare>`.

.PARAMETER RemoteUrl
    Git remote URL to clone from. Required. Examples:
      https://github.com/Org/repo.git
      git@github.com:Org/repo.git
      https://dev.azure.com/Org/Project/_git/Repo

.PARAMETER ParentDir
    Directory under which the layout is created. Default: ~/projects.
    Created if missing.

.PARAMETER RepoName
    Override the derived repository name. Default: leaf of the URL with
    trailing `.git` stripped (e.g. `Org/repo.git` -> `repo`).

.PARAMETER MainBranch
    Override the default-branch detection. Default: queried post-clone via
    `git --git-dir=<bare> symbolic-ref --short HEAD` (whatever the remote's
    HEAD points at).

.PARAMETER Commit
    Without this switch, the script runs in DRY-RUN mode and exits 0 after
    printing the classification + plan. With this switch, the script
    executes.

.OUTPUTS
    Human-readable plan / progress / next-steps narrative. Exit codes:
      0  success or already-bootstrapped or dry-run
      1  preflight failure (git missing, RemoteUrl invalid, parent unusable)
      3  path conflict (existing bare with mismatched origin; main path
         exists but isn't a valid worktree of the target bare; runs path
         exists but isn't a directory; remote is empty / unborn HEAD)
      4  execution failure (clone, worktree add, or mkdir failed)

.EXAMPLE
    PS> .\Bootstrap-BareRepo.ps1 -RemoteUrl https://github.com/PolyphonyRequiem/twig.git
    Dry-run for the canonical onboarding case under ~/projects.

.EXAMPLE
    PS> .\Bootstrap-BareRepo.ps1 -RemoteUrl https://github.com/PolyphonyRequiem/twig.git -Commit
    Executes: clones bare, adds main worktree, creates runs root.

.EXAMPLE
    PS> .\Bootstrap-BareRepo.ps1 -RemoteUrl https://example/foo.git -ParentDir D:\repos -MainBranch develop -Commit
    Custom parent + explicit branch override.
#>
param(
    [string]$RemoteUrl = '',
    [string]$ParentDir = (Join-Path $HOME 'projects'),
    [string]$RepoName = '',
    [string]$MainBranch = '',
    [switch]$Commit
)

# ── Exit code constants ──────────────────────────────────────────────────────
$script:EXIT_SUCCESS = 0
$script:EXIT_PREFLIGHT_FAILURE = 1
$script:EXIT_PATH_CONFLICT = 3
$script:EXIT_EXECUTION_FAILURE = 4

# ── Classification constants ────────────────────────────────────────────────
# What Get-BootstrapClassification returns in .Action.
$script:ACTION_FULL_BOOTSTRAP   = 'full_bootstrap'
$script:ACTION_ALREADY_DONE     = 'already_bootstrapped'
$script:ACTION_RECOVER_MAIN     = 'recover_missing_main'
$script:ACTION_RECOVER_RUNS     = 'recover_missing_runs'
$script:ACTION_REFUSED          = 'refused'

# ── Helpers ──────────────────────────────────────────────────────────────────

function Get-NormalizedPath {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char]'\', [char]'/')
}

function Invoke-Git {
    <#
    .DESCRIPTION
        Invoke git with an argument array (never string concatenation).
        Returns a PSCustomObject with ExitCode + Output (stderr merged in).
        Does not throw — caller decides how to handle non-zero exit.
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

function Resolve-RepoNameFromUrl {
    <#
    .DESCRIPTION
        Derive a repository name from a clone URL. Handles:
          https://github.com/Org/repo            -> repo
          https://github.com/Org/repo.git        -> repo
          git@github.com:Org/repo.git            -> repo
          ssh://git@host:22/Org/repo.git         -> repo
          https://dev.azure.com/Org/Project/_git/Repo -> Repo
          C:\path\to\local-bare.git              -> local-bare   (test/file URLs)
        Returns $null if no usable leaf can be extracted.
    #>
    param([Parameter(Mandatory)][string]$Url)
    $u = $Url.Trim()
    if ([string]::IsNullOrWhiteSpace($u)) { return $null }
    # Strip trailing path separators.
    $u = $u.TrimEnd('/', '\')
    # ssh-shortcut form: `user@host:Org/repo`. Detect by `user@host:` pattern
    # (contains `@` BEFORE the first `:`). This avoids misidentifying a
    # Windows drive letter (`C:`) as an ssh separator.
    if ($u -notmatch '://') {
        $i = $u.IndexOf(':')
        $at = $u.IndexOf('@')
        if ($i -gt 0 -and $at -gt 0 -and $at -lt $i) {
            $u = $u.Substring(0, $i) + '/' + $u.Substring($i + 1)
        }
    }
    # Split on both forward and back slash to support local paths.
    $leaf = ($u -split '[\\/]')[-1]
    if ([string]::IsNullOrWhiteSpace($leaf)) { return $null }
    if ($leaf.EndsWith('.git')) { $leaf = $leaf.Substring(0, $leaf.Length - 4) }
    if ([string]::IsNullOrWhiteSpace($leaf)) { return $null }
    return $leaf
}

function Test-RemoteUrlMatch {
    <#
    .DESCRIPTION
        Conservative URL equivalence: strip trailing slash + trailing `.git`
        and compare case-insensitively. Deliberately does NOT map
        https <-> ssh forms — those are semantically equivalent to humans
        but plumbing-equivalent to nobody. If the operator runs with one
        form and the bare was cloned with another, surface that as a
        mismatch and let them re-pass the right URL.
    #>
    param(
        [Parameter(Mandatory)][string]$A,
        [Parameter(Mandatory)][string]$B
    )
    $norm = {
        param($s)
        $t = $s.Trim().TrimEnd('/')
        if ($t.EndsWith('.git')) { $t = $t.Substring(0, $t.Length - 4) }
        return $t
    }
    return ((& $norm $A).ToLowerInvariant() -eq (& $norm $B).ToLowerInvariant())
}

function Get-DerivedPaths {
    param(
        [Parameter(Mandatory)][string]$ParentDir,
        [Parameter(Mandatory)][string]$RepoName
    )
    $parent = Get-NormalizedPath $ParentDir
    return [pscustomobject]@{
        ParentDir = $parent
        BarePath  = Join-Path $parent "$RepoName.git"
        MainPath  = Join-Path $parent $RepoName
        RunsRoot  = Join-Path $parent "$RepoName-runs"
    }
}

function Test-IsBareRepository {
    <#
    .DESCRIPTION
        Probe via `--git-dir`, NEVER via cwd discovery — `safe.bareRepository=
        explicit` rejects `git -C <bare>` entirely, and from a linked
        worktree cwd discovery returns false even when the underlying repo
        IS bare. Both modes work via `--git-dir=<path>`.
    #>
    param([Parameter(Mandatory)][string]$BarePath)
    if (-not (Test-Path -LiteralPath $BarePath)) { return $false }
    $r = Invoke-Git @('--git-dir', $BarePath, 'rev-parse', '--is-bare-repository')
    if ($r.ExitCode -ne 0) { return $false }
    return ($r.Output.Trim() -eq 'true')
}

function Get-BareOriginUrl {
    param([Parameter(Mandatory)][string]$BarePath)
    $r = Invoke-Git @('--git-dir', $BarePath, 'remote', 'get-url', 'origin')
    if ($r.ExitCode -ne 0) { return $null }
    return $r.Output.Trim()
}

function Get-DefaultBranchFromBare {
    <#
    .DESCRIPTION
        After bare clone, the bare's HEAD is a symbolic ref to the remote's
        default branch. Returns the short branch name (e.g. 'main') or
        $null if HEAD is not a symbolic ref (unborn / empty remote).
    #>
    param([Parameter(Mandatory)][string]$BarePath)
    $r = Invoke-Git @('--git-dir', $BarePath, 'symbolic-ref', '--short', 'HEAD')
    if ($r.ExitCode -ne 0) { return $null }
    $b = $r.Output.Trim()
    if ([string]::IsNullOrWhiteSpace($b)) { return $null }
    return $b
}

function Test-BranchHasCommit {
    <#
    .DESCRIPTION
        Verify the branch resolves to a commit object. Guards against the
        "symbolic-ref returns a name but the ref doesn't exist" case (empty
        remote / unborn HEAD).
    #>
    param(
        [Parameter(Mandatory)][string]$BarePath,
        [Parameter(Mandatory)][string]$Branch
    )
    $ref = "refs/heads/$Branch^{commit}"
    $r = Invoke-Git @('--git-dir', $BarePath, 'rev-parse', '--verify', $ref)
    return ($r.ExitCode -eq 0)
}

function Get-WorktreeList {
    <#
    .DESCRIPTION
        Returns [pscustomobject[]] with Path, Head, Branch for every worktree
        registered against the bare repo at $BarePath.
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
                Head   = ''
                Branch = ''
            }
        } elseif ($line -match '^HEAD (.+)$' -and $null -ne $cur) {
            $cur.Head = $Matches[1]
        } elseif ($line -match '^branch refs/heads/(.+)$' -and $null -ne $cur) {
            $cur.Branch = $Matches[1]
        }
    }
    if ($null -ne $cur) { $out += $cur }
    return $out
}

function Test-MainWorktreeBackedByBare {
    <#
    .DESCRIPTION
        Determine whether $MainPath is a worktree of $BarePath. Cross-checks
        BOTH directions:
          1. From $MainPath: git-common-dir resolves to $BarePath
          2. From $BarePath: worktree list contains $MainPath
        Both must agree.
    #>
    param(
        [Parameter(Mandatory)][string]$MainPath,
        [Parameter(Mandatory)][string]$BarePath
    )
    if (-not (Test-Path -LiteralPath $MainPath)) { return $false }
    # Direction 1: from main, where does git think the common-dir is?
    $r = Invoke-Git @('-C', $MainPath, 'rev-parse', '--path-format=absolute', '--git-common-dir')
    if ($r.ExitCode -ne 0) { return $false }
    $commonDir = Get-NormalizedPath $r.Output.Trim()
    if ($commonDir -ne (Get-NormalizedPath $BarePath)) { return $false }
    # Direction 2: bare's worktree list must include main.
    $worktrees = Get-WorktreeList -BarePath $BarePath
    $normMain = Get-NormalizedPath $MainPath
    foreach ($w in $worktrees) {
        if ($w.Path -eq $normMain) { return $true }
    }
    return $false
}

function Get-BootstrapClassification {
    <#
    .DESCRIPTION
        Classify the current state of $Paths relative to a bootstrap for
        $RemoteUrl. Returns a [pscustomobject] with:
          Action       — one of $script:ACTION_*
          Reason       — short string explaining the classification
          MainBranch   — the branch we'd put the main worktree on (after
                         consulting bare HEAD or -MainBranch override),
                         when knowable WITHOUT executing destructive ops.
                         Empty for the full-bootstrap case (we don't know
                         the remote's HEAD yet) unless caller passed an
                         explicit override.
    #>
    param(
        [Parameter(Mandatory)][pscustomobject]$Paths,
        [Parameter(Mandatory)][string]$RemoteUrl,
        [string]$MainBranchOverride = ''
    )

    $baExists  = Test-Path -LiteralPath $Paths.BarePath
    $mainExists = Test-Path -LiteralPath $Paths.MainPath
    $runsExists = Test-Path -LiteralPath $Paths.RunsRoot

    if (-not $baExists -and -not $mainExists -and -not $runsExists) {
        return [pscustomobject]@{
            Action = $script:ACTION_FULL_BOOTSTRAP
            Reason = "No prior layout at $($Paths.ParentDir)/$([System.IO.Path]::GetFileName($Paths.MainPath))*."
            MainBranch = $MainBranchOverride
        }
    }

    # ParentDir has *something* — every present piece must satisfy invariants
    # or we refuse. We never delete prior state.

    if ($baExists) {
        if (-not (Test-IsBareRepository -BarePath $Paths.BarePath)) {
            return [pscustomobject]@{
                Action = $script:ACTION_REFUSED
                Reason = "$($Paths.BarePath) exists but is not a bare git repository. Refusing to touch it."
                MainBranch = ''
            }
        }
        $bareOrigin = Get-BareOriginUrl -BarePath $Paths.BarePath
        if ($null -eq $bareOrigin) {
            return [pscustomobject]@{
                Action = $script:ACTION_REFUSED
                Reason = "Bare $($Paths.BarePath) exists but has no `origin` remote — cannot verify identity. Refusing."
                MainBranch = ''
            }
        }
        if (-not (Test-RemoteUrlMatch -A $bareOrigin -B $RemoteUrl)) {
            return [pscustomobject]@{
                Action = $script:ACTION_REFUSED
                Reason = "Bare $($Paths.BarePath) origin is `'$bareOrigin`' but -RemoteUrl is `'$RemoteUrl`'. Refusing to attach a worktree to a different repository."
                MainBranch = ''
            }
        }

        # Bare is ours. Decide the branch (override > bare HEAD).
        $branch = if (-not [string]::IsNullOrWhiteSpace($MainBranchOverride)) { $MainBranchOverride }
                  else { Get-DefaultBranchFromBare -BarePath $Paths.BarePath }
        if ([string]::IsNullOrWhiteSpace($branch)) {
            return [pscustomobject]@{
                Action = $script:ACTION_REFUSED
                Reason = "Bare $($Paths.BarePath) has no symbolic HEAD (empty/unborn remote). Push at least one commit and re-run, or pass -MainBranch."
                MainBranch = ''
            }
        }
        if (-not (Test-BranchHasCommit -BarePath $Paths.BarePath -Branch $branch)) {
            return [pscustomobject]@{
                Action = $script:ACTION_REFUSED
                Reason = "Branch '$branch' has no commit in bare $($Paths.BarePath). Push a commit on '$branch' and re-run."
                MainBranch = ''
            }
        }

        if ($mainExists) {
            if (-not (Test-MainWorktreeBackedByBare -MainPath $Paths.MainPath -BarePath $Paths.BarePath)) {
                return [pscustomobject]@{
                    Action = $script:ACTION_REFUSED
                    Reason = "$($Paths.MainPath) exists but is not a worktree of $($Paths.BarePath). Refusing to overwrite."
                    MainBranch = ''
                }
            }
            # Verify the main worktree is on the expected branch.
            $worktrees = Get-WorktreeList -BarePath $Paths.BarePath
            $normMain = Get-NormalizedPath $Paths.MainPath
            $mainWt = $worktrees | Where-Object { $_.Path -eq $normMain } | Select-Object -First 1
            if ($null -ne $mainWt -and -not [string]::IsNullOrWhiteSpace($mainWt.Branch) -and $mainWt.Branch -ne $branch) {
                return [pscustomobject]@{
                    Action = $script:ACTION_REFUSED
                    Reason = "$($Paths.MainPath) is on '$($mainWt.Branch)' but expected '$branch'. Refusing — fix the checkout manually or pass -MainBranch."
                    MainBranch = ''
                }
            }
            # Main is valid and on the expected branch. Runs missing?
            if (-not $runsExists) {
                return [pscustomobject]@{
                    Action = $script:ACTION_RECOVER_RUNS
                    Reason = "Bare + main present and valid; runs root $($Paths.RunsRoot) missing."
                    MainBranch = $branch
                }
            }
            if (-not (Test-Path -LiteralPath $Paths.RunsRoot -PathType Container)) {
                return [pscustomobject]@{
                    Action = $script:ACTION_REFUSED
                    Reason = "$($Paths.RunsRoot) exists but is not a directory."
                    MainBranch = ''
                }
            }
            return [pscustomobject]@{
                Action = $script:ACTION_ALREADY_DONE
                Reason = "Bare, main worktree, and runs root all present and valid."
                MainBranch = $branch
            }
        }

        # Bare is ours, main is missing. Already-checked-out elsewhere?
        $worktrees = Get-WorktreeList -BarePath $Paths.BarePath
        foreach ($w in $worktrees) {
            if ($w.Branch -eq $branch) {
                return [pscustomobject]@{
                    Action = $script:ACTION_REFUSED
                    Reason = "Branch '$branch' is already checked out in worktree '$($w.Path)' against bare $($Paths.BarePath). Refusing to add a duplicate."
                    MainBranch = ''
                }
            }
        }
        if ($runsExists -and -not (Test-Path -LiteralPath $Paths.RunsRoot -PathType Container)) {
            return [pscustomobject]@{
                Action = $script:ACTION_REFUSED
                Reason = "$($Paths.RunsRoot) exists but is not a directory."
                MainBranch = ''
            }
        }
        return [pscustomobject]@{
            Action = $script:ACTION_RECOVER_MAIN
            Reason = "Bare $($Paths.BarePath) present and ours; main worktree $($Paths.MainPath) missing."
            MainBranch = $branch
        }
    }

    # Bare is missing but something else at MainPath / RunsRoot exists —
    # refuse rather than collide on commit.
    if ($mainExists) {
        return [pscustomobject]@{
            Action = $script:ACTION_REFUSED
            Reason = "$($Paths.MainPath) exists but bare $($Paths.BarePath) does not. Refusing to overwrite a pre-existing directory."
            MainBranch = ''
        }
    }
    if ($runsExists -and -not (Test-Path -LiteralPath $Paths.RunsRoot -PathType Container)) {
        return [pscustomobject]@{
            Action = $script:ACTION_REFUSED
            Reason = "$($Paths.RunsRoot) exists but is not a directory."
            MainBranch = ''
        }
    }
    # Bare missing, main missing, runs may exist as a (possibly empty) dir — that's fine.
    return [pscustomobject]@{
        Action = $script:ACTION_FULL_BOOTSTRAP
        Reason = if ($runsExists) { "Runs root already exists; bare + main will be created." } else { "No prior layout." }
        MainBranch = $MainBranchOverride
    }
}

function Format-Plan {
    param(
        [Parameter(Mandatory)][pscustomobject]$Paths,
        [Parameter(Mandatory)][string]$RemoteUrl,
        [Parameter(Mandatory)][string]$Action,
        [string]$MainBranch = ''
    )
    $branchLabel = if ([string]::IsNullOrWhiteSpace($MainBranch)) { '<remote-default>' } else { $MainBranch }
    switch ($Action) {
        'full_bootstrap' { return @"
Plan: FULL BOOTSTRAP
  git clone --bare "$RemoteUrl" "$($Paths.BarePath)"
  git --git-dir="$($Paths.BarePath)" worktree add "$($Paths.MainPath)" $branchLabel
  New-Item -ItemType Directory -Path "$($Paths.RunsRoot)" -Force | Out-Null
"@ }
        'recover_missing_main' { return @"
Plan: RECOVER (add missing main worktree)
  git --git-dir="$($Paths.BarePath)" fetch --prune origin
  git --git-dir="$($Paths.BarePath)" worktree add "$($Paths.MainPath)" $branchLabel
  New-Item -ItemType Directory -Path "$($Paths.RunsRoot)" -Force | Out-Null  # if missing
"@ }
        'recover_missing_runs' { return @"
Plan: RECOVER (create missing runs root)
  New-Item -ItemType Directory -Path "$($Paths.RunsRoot)" -Force | Out-Null
"@ }
        default { return "(no plan: action '$Action')" }
    }
}

function Invoke-BootstrapExecution {
    <#
    .DESCRIPTION
        Execute the action determined by Get-BootstrapClassification.
        Cleans up paths it created on this invocation if any step fails.
        Never deletes paths it found pre-existing.
    #>
    param(
        [Parameter(Mandatory)][pscustomobject]$Paths,
        [Parameter(Mandatory)][string]$RemoteUrl,
        [Parameter(Mandatory)][string]$Action,
        [string]$MainBranchOverride = ''
    )

    $createdPaths = @()
    try {
        if ($Action -eq $script:ACTION_FULL_BOOTSTRAP) {
            Write-Host "  [1/4] git clone --bare $RemoteUrl -> $($Paths.BarePath)" -ForegroundColor Cyan
            $r = Invoke-Git @('clone', '--bare', $RemoteUrl, $Paths.BarePath)
            if ($r.ExitCode -ne 0) { throw "git clone --bare failed: $($r.Output)" }
            $createdPaths += $Paths.BarePath

            Write-Host "  [2/4] Validating bare repository" -ForegroundColor Cyan
            if (-not (Test-IsBareRepository -BarePath $Paths.BarePath)) {
                throw "Cloned $($Paths.BarePath) but --is-bare-repository says false."
            }

            $branch = if (-not [string]::IsNullOrWhiteSpace($MainBranchOverride)) { $MainBranchOverride }
                      else { Get-DefaultBranchFromBare -BarePath $Paths.BarePath }
            if ([string]::IsNullOrWhiteSpace($branch)) {
                throw "Remote at $RemoteUrl appears empty (no symbolic HEAD in bare). Push at least one commit or pass -MainBranch."
            }
            if (-not (Test-BranchHasCommit -BarePath $Paths.BarePath -Branch $branch)) {
                throw "Branch '$branch' has no commit in the bare. Push a commit on '$branch' and re-run."
            }

            Write-Host "  [3/4] git worktree add $($Paths.MainPath) $branch" -ForegroundColor Cyan
            $r = Invoke-Git @('--git-dir', $Paths.BarePath, 'worktree', 'add', $Paths.MainPath, $branch)
            if ($r.ExitCode -ne 0) { throw "git worktree add failed: $($r.Output)" }
            $createdPaths += $Paths.MainPath

            Write-Host "  [4/4] Ensuring runs root: $($Paths.RunsRoot)" -ForegroundColor Cyan
            if (-not (Test-Path -LiteralPath $Paths.RunsRoot)) {
                New-Item -ItemType Directory -Path $Paths.RunsRoot -Force | Out-Null
                $createdPaths += $Paths.RunsRoot
            }
        }
        elseif ($Action -eq $script:ACTION_RECOVER_MAIN) {
            $branch = if (-not [string]::IsNullOrWhiteSpace($MainBranchOverride)) { $MainBranchOverride }
                      else { Get-DefaultBranchFromBare -BarePath $Paths.BarePath }
            if ([string]::IsNullOrWhiteSpace($branch)) {
                throw "Bare $($Paths.BarePath) has no symbolic HEAD. Pass -MainBranch."
            }

            Write-Host "  [1/3] git --git-dir=$($Paths.BarePath) fetch --prune origin" -ForegroundColor Cyan
            $r = Invoke-Git @('--git-dir', $Paths.BarePath, 'fetch', '--prune', 'origin')
            if ($r.ExitCode -ne 0) { throw "git fetch failed: $($r.Output)" }

            if (-not (Test-BranchHasCommit -BarePath $Paths.BarePath -Branch $branch)) {
                throw "Branch '$branch' has no commit after fetch in bare $($Paths.BarePath)."
            }

            Write-Host "  [2/3] git worktree add $($Paths.MainPath) $branch" -ForegroundColor Cyan
            $r = Invoke-Git @('--git-dir', $Paths.BarePath, 'worktree', 'add', $Paths.MainPath, $branch)
            if ($r.ExitCode -ne 0) { throw "git worktree add failed: $($r.Output)" }
            $createdPaths += $Paths.MainPath

            Write-Host "  [3/3] Ensuring runs root: $($Paths.RunsRoot)" -ForegroundColor Cyan
            if (-not (Test-Path -LiteralPath $Paths.RunsRoot)) {
                New-Item -ItemType Directory -Path $Paths.RunsRoot -Force | Out-Null
                $createdPaths += $Paths.RunsRoot
            }
        }
        elseif ($Action -eq $script:ACTION_RECOVER_RUNS) {
            Write-Host "  [1/1] mkdir runs root: $($Paths.RunsRoot)" -ForegroundColor Cyan
            New-Item -ItemType Directory -Path $Paths.RunsRoot -Force | Out-Null
            $createdPaths += $Paths.RunsRoot
        }
        else {
            throw "Internal: Invoke-BootstrapExecution called with non-executable action '$Action'."
        }
        return $true
    } catch {
        Write-Host "  ! Execution failed; cleaning up paths created on this run" -ForegroundColor Yellow
        foreach ($p in $createdPaths) {
            if (Test-Path -LiteralPath $p) {
                Remove-Item -Recurse -Force -LiteralPath $p -ErrorAction SilentlyContinue
            }
        }
        throw
    }
}

function Format-NextSteps {
    param(
        [Parameter(Mandatory)][pscustomobject]$Paths,
        [Parameter(Mandatory)][string]$Branch
    )
    return @"

Bootstrap complete.

  Bare repo     : $($Paths.BarePath)
  Main worktree : $($Paths.MainPath)  (on $Branch)
  Runs root     : $($Paths.RunsRoot)  (empty; per-apex worktrees land here)

Next steps:
  1. cd "$($Paths.MainPath)"
  2. Initialize twig:
       twig init --org <ado-org> --project <ado-project>
  3. Bootstrap polyphony config:
       <path-to-polyphony>\scripts\bootstrap-conductor.ps1 -ProcessTemplate <template>
  4. Verify:
       polyphony state preflight --work-item <N>
"@
}

function Invoke-Bootstrap {
    param(
        [Parameter(Mandatory)][string]$RemoteUrl,
        [string]$ParentDir,
        [string]$RepoName,
        [string]$MainBranch,
        [switch]$Commit
    )

    Write-Host "Bootstrap-BareRepo (#420) — remote: $RemoteUrl" -ForegroundColor Green

    if (-not (Test-GitAvailable)) {
        Write-Host "ERROR: git not found on PATH." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    if ([string]::IsNullOrWhiteSpace($RemoteUrl)) {
        Write-Host "ERROR: -RemoteUrl is required and non-empty." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    if ([string]::IsNullOrWhiteSpace($RepoName)) {
        $RepoName = Resolve-RepoNameFromUrl -Url $RemoteUrl
        if ([string]::IsNullOrWhiteSpace($RepoName)) {
            Write-Host "ERROR: could not derive a repository name from '$RemoteUrl'. Pass -RepoName." -ForegroundColor Red
            return $script:EXIT_PREFLIGHT_FAILURE
        }
    }
    if ($RepoName -match '[\\/]') {
        Write-Host "ERROR: -RepoName must not contain path separators (got '$RepoName')." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }

    # Ensure ParentDir exists (or can be created). Refuse if the path points
    # at a file.
    if (Test-Path -LiteralPath $ParentDir -PathType Leaf) {
        Write-Host "ERROR: -ParentDir '$ParentDir' points to a file." -ForegroundColor Red
        return $script:EXIT_PREFLIGHT_FAILURE
    }
    if (-not (Test-Path -LiteralPath $ParentDir)) {
        if ($Commit) {
            try {
                New-Item -ItemType Directory -Path $ParentDir -Force | Out-Null
            } catch {
                Write-Host "ERROR: cannot create -ParentDir '$ParentDir': $_" -ForegroundColor Red
                return $script:EXIT_PREFLIGHT_FAILURE
            }
        }
        # Dry-run: don't create; just continue and compute the plan.
    }

    $paths = Get-DerivedPaths -ParentDir $ParentDir -RepoName $RepoName

    Write-Host ""
    Write-Host "Resolved layout:" -ForegroundColor Cyan
    Write-Host "  Repo name     : $RepoName"
    Write-Host "  Parent dir    : $($paths.ParentDir)"
    Write-Host "  Bare repo     : $($paths.BarePath)"
    Write-Host "  Main worktree : $($paths.MainPath)"
    Write-Host "  Runs root     : $($paths.RunsRoot)"
    if (-not [string]::IsNullOrWhiteSpace($MainBranch)) {
        Write-Host "  Main branch   : $MainBranch (override)"
    } else {
        Write-Host "  Main branch   : <remote default> (auto-detected from bare HEAD post-clone)"
    }
    Write-Host ""

    $cls = Get-BootstrapClassification -Paths $paths -RemoteUrl $RemoteUrl -MainBranchOverride $MainBranch
    Write-Host "Classification: $($cls.Action)" -ForegroundColor Cyan
    Write-Host "  $($cls.Reason)"

    if ($cls.Action -eq $script:ACTION_REFUSED) {
        Write-Host ""
        Write-Host "REFUSED. Resolve the conflict manually and re-run." -ForegroundColor Red
        return $script:EXIT_PATH_CONFLICT
    }

    if ($cls.Action -eq $script:ACTION_ALREADY_DONE) {
        Write-Host ""
        Write-Host "Already bootstrapped — nothing to do." -ForegroundColor Green
        Write-Host (Format-NextSteps -Paths $paths -Branch $cls.MainBranch)
        return $script:EXIT_SUCCESS
    }

    Write-Host ""
    Write-Host (Format-Plan -Paths $paths -RemoteUrl $RemoteUrl -Action $cls.Action -MainBranch $cls.MainBranch)
    Write-Host ""

    if (-not $Commit) {
        Write-Host "DRY-RUN: re-run with -Commit to execute." -ForegroundColor Yellow
        return $script:EXIT_SUCCESS
    }

    try {
        [void](Invoke-BootstrapExecution -Paths $paths -RemoteUrl $RemoteUrl -Action $cls.Action -MainBranchOverride $MainBranch)
    } catch {
        Write-Host ""
        Write-Host "ERROR: $_" -ForegroundColor Red
        return $script:EXIT_EXECUTION_FAILURE
    }

    # Re-resolve the branch for next-steps narrative.
    $finalBranch = if (-not [string]::IsNullOrWhiteSpace($MainBranch)) { $MainBranch }
                   else { Get-DefaultBranchFromBare -BarePath $paths.BarePath }
    if ([string]::IsNullOrWhiteSpace($finalBranch)) { $finalBranch = '<unknown>' }

    Write-Host (Format-NextSteps -Paths $paths -Branch $finalBranch)
    return $script:EXIT_SUCCESS
}

# ── Entry point: execute only when invoked directly, not when dot-sourced ──
if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-Bootstrap -RemoteUrl $RemoteUrl -ParentDir $ParentDir -RepoName $RepoName -MainBranch $MainBranch -Commit:$Commit)
}
