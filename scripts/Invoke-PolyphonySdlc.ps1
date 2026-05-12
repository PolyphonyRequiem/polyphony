#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Canonical wrapper for `conductor run apex-driver@polyphony` — the polyphony
    SDLC entry point. Enforces the AB#3085 bare-repo + per-run-worktree layout.

.DESCRIPTION
    The launcher self-derives the conductor's worktree from
    `polyphony worktree init-apex --apex {ApexId}`, which produces (and reports)
    a worktree at `{runs_root}/apex-{N}/feature-{N}/`. The operator's cwd is
    expected to be the canonical main worktree (or any worktree of the bare
    repo); the conductor never runs in the main worktree.

    Two production bugs the new contract eliminates by construction:
      1. Hijack — the previous launcher defaulted WorktreeRoot to (Get-Location).
         Running from `~/projects/polyphony` (the main worktree) made conductor
         hammer main directly. The new contract refuses any WorktreeRoot that
         resolves to or inside the main-worktree path.
      2. Cross-contamination — the previous shared-worktree model meant
         parallel SDLC runs collided on `git stash` / `worktree dirty`. The new
         contract puts each apex run in its own per-apex tree under
         `{runs_root}/apex-{N}/`.

    See docs/per-run-worktree-layout.md for the layout contract and
    scripts/Migrate-ToBareRepo.ps1 for migrating an existing non-bare clone.

    Closes AB#3011 (the original wrapper) and AB#3098 (this rework).

.PARAMETER ApexId
    Apex (run-root) work item ID. Required.

.PARAMETER Intent
    Apex-driver intent enum: `new` | `resume` | `replan`. Default: `new`.
    `-Intent resume` refuses to dispatch when init-apex reports outcome=created
    (no prior state to resume).

.PARAMETER WorktreeRoot
    OPTIONAL EXPERT OVERRIDE of the conductor's worktree path. When supplied,
    must canonicalize to the same path init-apex returns; otherwise the launcher
    refuses with a clear message. The normal flow is to omit this and let the
    launcher self-derive.

.PARAMETER Platform
    PR platform override (`ado` | `github`). Default: auto-detected from
    `git remote get-url origin` of the main worktree.

.PARAMETER Repository
    Repository identifier passed to the lifecycle sub-workflows. Default:
    auto-derived from the git remote URL (GitHub: `<owner>/<name>`; ADO:
    `<repo>`).

.PARAMETER GitRepo
    Source-of-truth git repo path metadata field. Default: the canonical main
    worktree path returned by init-apex (no longer derived from the WorktreeRoot
    sibling-name heuristic, which is wrong under the new layout).

.PARAMETER NoDetach
    Run conductor in the foreground (don't Start-Process). Useful for debugging
    the invocation. Default: detached into a new console window.

.PARAMETER DryRun
    Print the resolved command and exit without executing. Calls
    `polyphony worktree init-apex --dry-run` so no worktrees are created.
    Returns the JSON envelope that would otherwise be emitted on launch.

.PARAMETER SkipLayoutCheck
    ESCAPE HATCH for legacy non-bare layouts. Skips the bare-repo preflight.
    Strongly discouraged; intended for transition-period operators who have
    not yet run scripts/Migrate-ToBareRepo.ps1. Will be removed once all
    operators have migrated.

.EXAMPLE
    cd ~/projects/polyphony
    ./scripts/Invoke-PolyphonySdlc.ps1 -ApexId 3085 -Intent new
    Bootstraps `~/projects/polyphony-runs/apex-3085/feature-3085/` and launches
    apex-driver against work item 3085 inside it.

.EXAMPLE
    ./scripts/Invoke-PolyphonySdlc.ps1 -ApexId 3085 -DryRun | ConvertFrom-Json
    Resolves the worktree path + conductor command without creating worktrees
    or launching anything.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ApexId,

    [ValidateSet('new', 'resume', 'replan')]
    [string]$Intent = 'new',

    [string]$WorktreeRoot,

    [ValidateSet('ado', 'github')]
    [string]$Platform,

    [string]$Repository,

    [string]$GitRepo,

    [switch]$NoDetach,

    [switch]$DryRun,

    [switch]$SkipLayoutCheck
)

$ErrorActionPreference = 'Stop'

# ─── Constants ────────────────────────────────────────────────────────────────

$script:LayoutDoc        = 'docs/per-run-worktree-layout.md'
$script:MigrationScript  = 'scripts/Migrate-ToBareRepo.ps1'

# ─── Helper: canonical path comparison (boundary-aware, OS-aware) ─────────────

function Get-CanonicalPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    return $full.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-IsSameOrInside {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Container
    )
    $cand = Get-CanonicalPath $Candidate
    $cont = Get-CanonicalPath $Container
    $cmp = if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        [System.StringComparison]::OrdinalIgnoreCase
    } else {
        [System.StringComparison]::Ordinal
    }
    if ([string]::Equals($cand, $cont, $cmp)) { return $true }
    $sep = [System.IO.Path]::DirectorySeparatorChar
    return $cand.StartsWith($cont + $sep, $cmp)
}

# ─── Helper: invoke polyphony and parse JSON result ───────────────────────────

function Invoke-PolyphonyJson {
    param([Parameter(Mandatory)][string[]]$ArgList)
    $stdout = & polyphony @ArgList 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
        throw "[polyphony-sdlc] polyphony $($ArgList -join ' ') exited $exit. Output:`n$stdout"
    }
    try {
        return ($stdout | ConvertFrom-Json)
    } catch {
        throw "[polyphony-sdlc] Could not parse JSON from polyphony $($ArgList -join ' ').`nOutput: $stdout`nParse error: $($_.Exception.Message)"
    }
}

# ─── Phase 1: cwd is a worktree of a git repo ────────────────────────────────

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "[polyphony-sdlc] git is not on PATH. Install git before invoking apex-driver."
}

$cwd = (Get-Location).Path
$commonDir = & git rev-parse --path-format=absolute --git-common-dir 2>&1
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commonDir)) {
    $global:LASTEXITCODE = 0
    throw @"
[polyphony-sdlc] Cwd is not inside a git repository.
Cwd: $cwd

Launch from a worktree of the polyphony bare repo (the canonical main worktree
is the conventional choice). See $script:LayoutDoc.
"@
}
$commonDir = $commonDir.Trim()

# ─── Phase 2: bare-repo layout preflight ─────────────────────────────────────

# Inline check (2 git calls) is faster than `polyphony state preflight`, which
# drags in GH-auth + dotnet SDK probes and is the wrong granularity for a
# fail-fast launcher gate. This duplicates the bare_repo check in
# StateCommands.cs (Name="bare_repo"); both paths converge on the same
# remediation (per docs/per-run-worktree-layout.md + Migrate-ToBareRepo.ps1).
if (-not $SkipLayoutCheck) {
    $isBare = & git --git-dir $commonDir rev-parse --is-bare-repository 2>&1
    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
        throw @"
[polyphony-sdlc] Could not probe bare-repo state of common-dir.
Common-dir: $commonDir
Output:     $isBare

This usually means the common-dir is corrupted. See $script:LayoutDoc.
"@
    }
    if ($isBare.Trim().ToLower() -ne 'true') {
        throw @"
[polyphony-sdlc] Repo-layout preflight FAILED.
Common-dir: $commonDir
This is a non-bare clone (legacy layout).

The AB#3085 SDLC orchestration requires the bare-repo + per-run-worktree
layout to prevent the hijack and cross-contamination bugs by construction.

To migrate:
    ./$script:MigrationScript            # dry-run (default) — see what would happen
    ./$script:MigrationScript -Commit    # execute (with risk-material refusal)

For background, see $script:LayoutDoc.

To bypass this gate (NOT recommended, transition period only):
    ./scripts/Invoke-PolyphonySdlc.ps1 -ApexId $ApexId -SkipLayoutCheck
"@
    }
}

# ─── Phase 3: invoke init-apex (--dry-run if -DryRun) ────────────────────────

# init-apex self-derives the apex worktree path from the common-dir. It MUST
# run from a worktree of the bare repo (the operator's cwd is fine; init-apex
# does not refuse cwd-inside-main because the launcher legitimately calls it
# from main during bootstrap).
$initArgs = @('worktree', 'init-apex', '--apex', $ApexId)
if ($DryRun) { $initArgs += '--dry-run' }
$initResult = Invoke-PolyphonyJson -ArgList $initArgs

if ($initResult.outcome -eq 'failed') {
    $remediation = switch ($initResult.reason) {
        'branch_in_use' {
            "The branch is checked out in another worktree. Run 'git worktree list' " +
            "to find it; either remove that worktree (`git worktree remove <path>`) or " +
            "use -Intent resume to attach to the existing apex run."
        }
        'path_exists_wrong_branch' {
            "The apex worktree exists but is on the wrong branch. Inspect the path " +
            "and either remove it (`git worktree remove $($initResult.worktree_path)`) " +
            "or check out the expected branch in place."
        }
        'path_exists_not_worktree' {
            "Path exists but isn't a registered worktree (likely a leftover directory). " +
            "Remove it manually: Remove-Item -Recurse '$($initResult.worktree_path)'."
        }
        'remote_branch_exists' {
            "Local branch '$($initResult.branch)' is missing but origin/$($initResult.branch) " +
            "exists. Fetch and create the local branch first:`n" +
            "    git --git-dir $commonDir fetch origin '$($initResult.branch):$($initResult.branch)'"
        }
        'common_dir_unavailable' { "See $script:LayoutDoc." }
        default                  { '' }
    }
    throw @"
[polyphony-sdlc] worktree init-apex failed.
Reason: $($initResult.reason)
Error:  $($initResult.error)
$remediation
"@
}

$derivedWorktree = $initResult.worktree_path
$mainWorktree    = $initResult.main_worktree_path
$runsRoot        = $initResult.runs_root
$expectedBranch  = $initResult.branch
$initOutcome     = $initResult.outcome

if (-not $derivedWorktree -or -not $mainWorktree) {
    throw "[polyphony-sdlc] init-apex returned outcome=$initOutcome but did not populate worktree_path / main_worktree_path. Update polyphony to a version that supports the new fields."
}

# ─── Phase 4: hijack-refusal (defense in depth) ──────────────────────────────

# init-apex's PathBoundary check already prevents the derived path from being
# the main worktree, but the launcher checks again because nothing legitimate
# should ever pass.
if (Test-IsSameOrInside -Candidate $derivedWorktree -Container $mainWorktree) {
    throw @"
[polyphony-sdlc] HIJACK REFUSAL — derived apex worktree is or is inside the main worktree.
  Derived worktree: $derivedWorktree
  Main worktree:    $mainWorktree

This is a defense-in-depth check; init-apex should have already refused.
File a bug against polyphony if you see this. See $script:LayoutDoc.
"@
}

# ─── Phase 5: -WorktreeRoot override validation ──────────────────────────────

if ($PSBoundParameters.ContainsKey('WorktreeRoot')) {
    if (-not (Test-Path $WorktreeRoot)) {
        # Allow a non-existent override only when init-apex would have created
        # the same path. The path must canonicalize to the derived path.
    }
    $canonicalOverride = Get-CanonicalPath $WorktreeRoot
    $canonicalDerived  = Get-CanonicalPath $derivedWorktree
    $cmp = if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        [System.StringComparison]::OrdinalIgnoreCase
    } else {
        [System.StringComparison]::Ordinal
    }
    if (-not [string]::Equals($canonicalOverride, $canonicalDerived, $cmp)) {
        throw @"
[polyphony-sdlc] -WorktreeRoot override does not match the canonical apex worktree.
  Override:  $canonicalOverride
  Canonical: $canonicalDerived

The canonical worktree for apex $ApexId is determined by the bare repo's layout
(see $script:LayoutDoc). The override was added so advanced operators could
verify their assumptions; if your intent was to use the canonical path, omit
-WorktreeRoot.
"@
    }
}

$WorktreeRoot = $derivedWorktree

# ─── Phase 6: intent semantics — refuse 'created' on resume ──────────────────

if ($Intent -eq 'resume' -and $initOutcome -eq 'created') {
    throw @"
[polyphony-sdlc] -Intent resume refused: init-apex reported outcome=created.
  Apex worktree: $WorktreeRoot
  Branch:        $expectedBranch

There is no prior state to resume — the apex worktree was just created. If you
intended to start a new run, re-invoke with -Intent new (the default).
"@
}

# ─── Phase 7: copy .twig/ from main worktree if missing in apex worktree ─────

# .twig/ is gitignored, so a freshly-created worktree from `git worktree add`
# does not have it. The launcher copies it once from main; subsequent launches
# (resume) reuse the apex worktree's existing .twig/ unchanged.
$mainTwigDir = Join-Path $mainWorktree '.twig'
$apexTwigDir = Join-Path $WorktreeRoot '.twig'
$twigSourceForConfig = $null

if (Test-Path $apexTwigDir -PathType Container) {
    $twigSourceForConfig = $apexTwigDir
} elseif (Test-Path $mainTwigDir -PathType Container) {
    if (-not $DryRun) {
        # On a real launch, copy main's .twig/ into the apex worktree so twig
        # commands inside the apex have local state. Skip-if-exists; never
        # clobber operator-curated state in an existing apex worktree.
        if (Test-Path $WorktreeRoot -PathType Container) {
            Copy-Item -LiteralPath $mainTwigDir -Destination $apexTwigDir -Recurse -Force
            $twigSourceForConfig = $apexTwigDir
        } else {
            # Apex worktree path doesn't exist yet (init-apex returned a path
            # it would have created in non-dry-run; shouldn't happen here).
            throw "[polyphony-sdlc] init-apex reported outcome=$initOutcome but worktree path '$WorktreeRoot' does not exist. Aborting."
        }
    } else {
        # DryRun: read .twig/config from main since we can't copy.
        $twigSourceForConfig = $mainTwigDir
    }
} else {
    throw @"
[polyphony-sdlc] No .twig/ directory found in main worktree.
Main worktree: $mainWorktree

apex-driver requires twig for work-item context. Bootstrap twig in the main
worktree first:
    cd $mainWorktree
    twig init <organization> <project>
    twig set $ApexId
"@
}

$twigConfigPath = Join-Path $twigSourceForConfig 'config'
if (-not (Test-Path $twigConfigPath -PathType Leaf)) {
    throw "[polyphony-sdlc] .twig/ exists at $twigSourceForConfig but config file is missing."
}
$twigConfig = Get-Content $twigConfigPath -Raw | ConvertFrom-Json
if (-not $twigConfig.organization -or -not $twigConfig.project) {
    throw "[polyphony-sdlc] twig config at $twigConfigPath is missing 'organization' or 'project'."
}

# ─── Phase 8: assert-clean (skip on -DryRun) ─────────────────────────────────

if (-not $DryRun) {
    $cleanResult = Invoke-PolyphonyJson -ArgList @(
        'worktree', 'assert-clean',
        '--path', $WorktreeRoot,
        '--expected-branch', $expectedBranch
    )
    if (-not $cleanResult.ok) {
        $remediation = switch ($cleanResult.reason) {
            'dirty' {
                $dirtyList = if ($cleanResult.dirty_paths) {
                    "`n    " + (($cleanResult.dirty_paths) -join "`n    ")
                } else { '' }
                "Working tree has uncommitted changes. Inspect and resolve:$dirtyList`n" +
                "    cd '$WorktreeRoot'`n" +
                "    git status"
            }
            'wrong_branch' {
                "Worktree is on branch '$($cleanResult.current_branch)', not '$expectedBranch'.`n" +
                "    cd '$WorktreeRoot'`n" +
                "    git checkout $expectedBranch"
            }
            'git_operation_in_progress' {
                "A git operation (rebase/merge/cherry-pick/bisect) is mid-flight in the worktree. " +
                "Complete or abort it before launching."
            }
            'not_a_worktree' {
                "Path is not a registered git worktree. init-apex should have ensured this; " +
                "re-run the launcher to retry, or remove the path manually."
            }
            'path_missing' {
                "Path does not exist. init-apex should have ensured this; " +
                "re-run the launcher to retry."
            }
            default { '' }
        }
        throw @"
[polyphony-sdlc] assert-clean refused dispatch.
  Path:   $WorktreeRoot
  Reason: $($cleanResult.reason)
$remediation
"@
    }
}

# ─── Phase 9: derive metadata ────────────────────────────────────────────────

$projectUrl = 'https://dev.azure.com/{0}/{1}' -f $twigConfig.organization, $twigConfig.project
$worktreeName = Split-Path -Leaf $WorktreeRoot

if (-not $GitRepo) {
    # Default to the canonical main worktree (NOT the apex worktree, which is
    # a per-run scratch space). Downstream consumers (dashboard, observation,
    # post-mortem skills) treat git_repo as the source-of-truth checkout.
    $GitRepo = $mainWorktree
}

# Auto-detect platform + repository from git remote URL of the MAIN WORKTREE.
# The previous launcher detected from $WorktreeRoot, which was wrong under
# the new layout (apex worktree shares the bare's remotes anyway, but reading
# from main is the intentful choice — main is the canonical source-of-truth).
$detectedPlatform   = $null
$detectedRepository = $null
$remoteUrl = & git -C $mainWorktree remote get-url origin 2>&1
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteUrl)) {
    $remoteUrl = $null
    $global:LASTEXITCODE = 0
}

if ($remoteUrl) {
    switch -Regex ($remoteUrl) {
        '^(?:https?://github\.com/|git@github\.com:)(?<owner>[^/]+)/(?<name>[^/]+?)(?:\.git)?/?$' {
            $detectedPlatform = 'github'
            $detectedRepository = "$($Matches.owner)/$($Matches.name)"
            break
        }
        '^https?://(?:[^@/]+@)?dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/?#]+?)(?:\.git)?/?$' {
            $detectedPlatform = 'ado'
            $detectedRepository = $Matches.repo
            break
        }
        '^https?://(?:[^@/]+@)?(?<org>[^.]+)\.visualstudio\.com/(?<project>[^/]+)/_git/(?<repo>[^/?#]+?)(?:\.git)?/?$' {
            $detectedPlatform = 'ado'
            $detectedRepository = $Matches.repo
            break
        }
        '^git@ssh\.dev\.azure\.com:v\d+/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/?#]+?)(?:\.git)?/?$' {
            $detectedPlatform = 'ado'
            $detectedRepository = $Matches.repo
            break
        }
    }
}

if (-not $Platform) {
    $Platform = if ($detectedPlatform) { $detectedPlatform } else { 'ado' }
}

if (-not $PSBoundParameters.ContainsKey('Repository')) {
    $Repository = if ($detectedRepository) { $detectedRepository } else { '' }
}

# ─── Phase 10: build the conductor command ───────────────────────────────────

$conductorArgs = @(
    'run', 'apex-driver@polyphony'
    '--web'
    '--input', "apex_id=$ApexId"
    '--input', "intent=$Intent"
    '--input', "platform=$Platform"
    '--input', "organization=$($twigConfig.organization)"
    '--input', "project=$($twigConfig.project)"
    '--input', "repository=$Repository"
    '-m', "tracker=ado"
    '-m', "project_url=$projectUrl"
    '-m', "git_repo=$GitRepo"
    '-m', "workitem_id=$ApexId"
    '-m', "worktree_name=$worktreeName"
    '-m', "cwd=$WorktreeRoot"
)

$resolved = [pscustomobject]@{
    success            = $true
    workflow           = 'apex-driver@polyphony'
    apex_id            = $ApexId
    intent             = $Intent
    platform           = $Platform
    repository         = $Repository
    remote_url         = $remoteUrl
    worktree_root      = $WorktreeRoot
    main_worktree_path = $mainWorktree
    runs_root          = $runsRoot
    apex_root          = $initResult.apex_root
    branch             = $expectedBranch
    init_apex_outcome  = $initOutcome
    git_repo           = $GitRepo
    project_url        = $projectUrl
    command            = "conductor $($conductorArgs -join ' ')"
    args               = $conductorArgs
    detached           = -not $NoDetach
    layout_check_skipped = [bool]$SkipLayoutCheck
}

if ($DryRun) {
    $resolved | Add-Member -NotePropertyName dry_run -NotePropertyValue $true
    $resolved | ConvertTo-Json -Depth 5
    return
}

# ─── Pin gh identity (github platform only) ──────────────────────────────────

# (Pattern unchanged from PR #250: auto-detect active gh user, validate, pin
# GH_TOKEN+GH_HOST for the conductor subprocess tree. See repo memory
# 'launcher gh probe'.)
if ($Platform -eq 'github') {
    . (Join-Path $PSScriptRoot 'Resolve-GhIdentity.ps1')
    try {
        $identity = Resolve-GhIdentity
    } catch {
        throw "[polyphony-sdlc] gh identity probe failed:`n$($_.Exception.Message)"
    }
    $env:GH_TOKEN = $identity.Token
    $env:GH_HOST  = 'github.com'
    Write-Host "[polyphony-sdlc] gh identity pinned: user='$($identity.User)' source=$($identity.Source) token_len=$($identity.TokenLength)" -ForegroundColor Cyan
}

# ─── Launch conductor ────────────────────────────────────────────────────────

if (-not (Get-Command conductor -ErrorAction SilentlyContinue)) {
    throw "[polyphony-sdlc] conductor is not on PATH. Install it before invoking apex-driver."
}

if ($NoDetach) {
    Push-Location $WorktreeRoot
    try {
        & conductor @conductorArgs
        $exit = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    $resolved | Add-Member -NotePropertyName exit_code -NotePropertyValue $exit
    $resolved | ConvertTo-Json -Depth 5
    return
}

$logDir = Join-Path ([System.IO.Path]::GetTempPath()) 'polyphony-sdlc-runs'
[void](New-Item -ItemType Directory -Path $logDir -Force)
$logTimestamp = (Get-Date -Format 'yyyyMMdd-HHmmss')
$transcriptLog = Join-Path $logDir "apex-${ApexId}-${logTimestamp}-transcript.log"

# Spawn conductor inside a new PowerShell console window. Pattern unchanged
# from PR #302 — TTY-attached so gate agents can read stdin and the
# operator's launching shell stays free.
$conductorCmd = 'conductor ' + (($conductorArgs | ForEach-Object {
    if ($_ -match '[\s"'']') { "'" + ($_ -replace "'", "''") + "'" }
    else { $_ }
}) -join ' ')

$childCommand = @"
Set-Location -LiteralPath '$($WorktreeRoot.Replace("'","''"))'
`$Host.UI.RawUI.WindowTitle = 'polyphony-sdlc apex=$ApexId'
Write-Host '[polyphony-sdlc] Worktree: $WorktreeRoot' -ForegroundColor Cyan
Write-Host '[polyphony-sdlc] Command : $conductorCmd' -ForegroundColor Cyan
Write-Host '[polyphony-sdlc] Transcript: $transcriptLog' -ForegroundColor Cyan
Write-Host ''
Start-Transcript -LiteralPath '$transcriptLog' -Append | Out-Null
try {
    $conductorCmd
    `$exit = `$LASTEXITCODE
} finally {
    Stop-Transcript | Out-Null
}
Write-Host ''
Write-Host "[polyphony-sdlc] conductor exited with code: `$exit" -ForegroundColor Yellow
Write-Host '[polyphony-sdlc] Window kept open (-NoExit). Close manually.' -ForegroundColor DarkGray
"@

$proc = Start-Process -FilePath 'pwsh' `
    -ArgumentList @('-NoExit', '-NoProfile', '-Command', $childCommand) `
    -WorkingDirectory $WorktreeRoot `
    -PassThru

$resolved | Add-Member -NotePropertyName pid -NotePropertyValue $proc.Id
$resolved | Add-Member -NotePropertyName transcript_log -NotePropertyValue $transcriptLog
$resolved | Add-Member -NotePropertyName note -NotePropertyValue "Conductor launched in a new PowerShell window (TTY-attached). Dashboard URL appears in that window's first few lines. Tail transcript: Get-Content -Wait '$transcriptLog'"
$resolved | ConvertTo-Json -Depth 5
