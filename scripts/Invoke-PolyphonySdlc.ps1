#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Canonical wrapper for `conductor run apex-driver@polyphony` — the polyphony SDLC entry point.

.DESCRIPTION
    Constructs the full conductor invocation with all required `--input` flags and the
    six `--metadata` (`-m`) fields the dashboard / observation filer / post-mortem skills
    consume. Validates that the worktree contains a `.twig/` directory (preflight gate
    addressing AB#3010 symptom), resolves org/project/repo from `.twig/config`, and
    launches conductor detached with `--web` so the dashboard streams the run.

    Closes AB#3011. Replaces the prose recipe at .github/skills/polyphony-sdlc/SKILL.md.

    NOTE: The original AB#3011 spec referenced `polyphony-full@polyphony` and a
    `-UserPlanPath` parameter. polyphony-full was deleted (PR #147); apex-driver
    (`apex-driver@polyphony`) is the canonical SDLC entry point as of PRs #146/#149/#150/#152.
    UserPlanPath is dropped — apex-driver does not consume it. Intent now defaults to `new`
    and the typed enum is enforced via `ValidateSet`.

.PARAMETER ApexId
    Apex (run-root) work item ID. Required.

.PARAMETER Intent
    Apex-driver intent enum: `new` | `resume` | `replan`. Default: `new`.

.PARAMETER WorktreeRoot
    Worktree directory to run conductor from. Default: current directory.

.PARAMETER Platform
    PR platform override (`ado` | `github`). Default: auto-detected from
    `git remote get-url origin` of the worktree — `github.com` URLs route
    to `github`, `dev.azure.com` / `*.visualstudio.com` URLs route to `ado`.
    Falls back to `ado` when the remote is unrecognized or absent.

    NOTE: this is the **PR** platform threaded through to lifecycle
    sub-workflows (which is conflated with apex-driver's `platform` input
    today). Work-item tracker is independently configured via `.twig/config`.

.PARAMETER Repository
    Repository identifier passed to the lifecycle sub-workflows. Default:
    auto-derived from the git remote URL — `<owner>/<name>` for GitHub,
    `<repo>` for ADO. Required (and validated downstream) only on the
    ADO leg. Override when the auto-detection picks the wrong value or
    the remote is non-standard.

.PARAMETER GitRepo
    Source-of-truth git repo path (NOT the worktree). Default: parent of WorktreeRoot
    if WorktreeRoot ends in `<repo>-<id>`; otherwise WorktreeRoot itself.

.PARAMETER NoDetach
    Run conductor in the foreground (don't Start-Process). Useful for debugging the
    invocation. Default: detached.

.PARAMETER DryRun
    Print the resolved command and exit without executing. Returns the JSON envelope
    that would otherwise be emitted on launch.

.EXAMPLE
    ./scripts/Invoke-PolyphonySdlc.ps1 -ApexId 2919 -Intent new
    Launches apex-driver against work item 2919 from the current worktree.

.EXAMPLE
    ./scripts/Invoke-PolyphonySdlc.ps1 -ApexId 2919 -DryRun | ConvertFrom-Json
    Prints the resolved command without launching.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ApexId,

    [ValidateSet('new', 'resume', 'replan')]
    [string]$Intent = 'new',

    [string]$WorktreeRoot = (Get-Location).Path,

    [ValidateSet('ado', 'github')]
    [string]$Platform,

    [string]$Repository,

    [string]$GitRepo,

    [switch]$NoDetach,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ─── Preflight ────────────────────────────────────────────────────────────────

if (-not (Test-Path $WorktreeRoot -PathType Container)) {
    throw "WorktreeRoot does not exist or is not a directory: $WorktreeRoot"
}

$WorktreeRoot = (Resolve-Path $WorktreeRoot).Path
$twigDir = Join-Path $WorktreeRoot '.twig'
if (-not (Test-Path $twigDir -PathType Container)) {
    throw @"
Worktree preflight FAILED.
Path:    $WorktreeRoot
Missing: $twigDir

apex-driver requires a .twig/ directory in the worktree (twig is its work-item provider).
Standard worktree setup:
    git worktree add -b sdlc/<ID> ../polyphony-<ID> main
    cd ../polyphony-<ID>
    Copy-Item -Recurse ../polyphony/.twig .twig
    twig set <ID>
    twig sync
"@
}

$twigConfigPath = Join-Path $twigDir 'config'
if (-not (Test-Path $twigConfigPath -PathType Leaf)) {
    throw "Worktree preflight FAILED. .twig/ exists but config file missing: $twigConfigPath"
}

$twigConfig = Get-Content $twigConfigPath -Raw | ConvertFrom-Json
if (-not $twigConfig.organization -or -not $twigConfig.project) {
    throw "twig config at $twigConfigPath is missing 'organization' or 'project'."
}

# ─── Resolve metadata ─────────────────────────────────────────────────────────

$projectUrl = 'https://dev.azure.com/{0}/{1}' -f $twigConfig.organization, $twigConfig.project
$worktreeName = Split-Path -Leaf $WorktreeRoot

if (-not $GitRepo) {
    # Convention: worktree is at <parent>/<repo>-<id>; source repo is at <parent>/<repo>.
    # If the worktree doesn't follow the convention, fall back to the worktree itself.
    $parent = Split-Path -Parent $WorktreeRoot
    $repoCandidate = if ($worktreeName -match '^(?<repo>[^-]+(?:-[^-\d]+)*)-\d+$') {
        Join-Path $parent $Matches.repo
    } else {
        $WorktreeRoot
    }
    $GitRepo = if (Test-Path (Join-Path $repoCandidate '.git')) { $repoCandidate } else { $WorktreeRoot }
}

# Auto-detect platform + repository from git remote URL of the worktree.
# Detection lives here (not in apex-driver) because the lifecycle sub-workflows
# treat `platform` + `repository` as opaque inputs — the wrapper is the only
# layer that knows about the actual git remote.
$detectedPlatform = $null
$detectedRepository = $null
$remoteUrl = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $remoteUrl = (& git -C $WorktreeRoot remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteUrl)) {
        $remoteUrl = $null
        $global:LASTEXITCODE = 0
    }
}

if ($remoteUrl) {
    switch -Regex ($remoteUrl) {
        # GitHub https / ssh
        '^(?:https?://github\.com/|git@github\.com:)(?<owner>[^/]+)/(?<name>[^/]+?)(?:\.git)?/?$' {
            $detectedPlatform = 'github'
            $detectedRepository = "$($Matches.owner)/$($Matches.name)"
            break
        }
        # ADO modern: https://dev.azure.com/<org>/<project>/_git/<repo>
        '^https?://(?:[^@/]+@)?dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/?#]+?)(?:\.git)?/?$' {
            $detectedPlatform = 'ado'
            $detectedRepository = $Matches.repo
            break
        }
        # ADO legacy: https://<org>.visualstudio.com/<project>/_git/<repo>
        '^https?://(?:[^@/]+@)?(?<org>[^.]+)\.visualstudio\.com/(?<project>[^/]+)/_git/(?<repo>[^/?#]+?)(?:\.git)?/?$' {
            $detectedPlatform = 'ado'
            $detectedRepository = $Matches.repo
            break
        }
        # ADO ssh: git@ssh.dev.azure.com:v3/<org>/<project>/<repo>
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

# ─── Build the command ───────────────────────────────────────────────────────

$conductorArgs = @(
    'run', 'apex-driver@polyphony'
    '--web'
    '--input', "apex_id=$ApexId"
    '--input', "intent=$Intent"
    '--input', "platform=$Platform"
    '--input', "organization=$($twigConfig.organization)"
    '--input', "project=$($twigConfig.project)"
    # apex-driver `repository` defaults to "" and is consumed only by ADO PR verbs.
    # Auto-derived from `git remote get-url origin` (GitHub: `<owner>/<name>`;
    # ADO: `<repo>`); -Repository overrides; falls back to "" when undetectable.
    '--input', "repository=$Repository"
    '-m', "tracker=ado"
    '-m', "project_url=$projectUrl"
    '-m', "git_repo=$GitRepo"
    '-m', "workitem_id=$ApexId"
    '-m', "worktree_name=$worktreeName"
    '-m', "cwd=$WorktreeRoot"
)

$resolved = [pscustomobject]@{
    success        = $true
    workflow       = 'apex-driver@polyphony'
    apex_id        = $ApexId
    intent         = $Intent
    platform       = $Platform
    repository     = $Repository
    remote_url     = $remoteUrl
    worktree_root  = $WorktreeRoot
    git_repo       = $GitRepo
    project_url    = $projectUrl
    command        = "conductor $($conductorArgs -join ' ')"
    args           = $conductorArgs
    detached       = -not $NoDetach
}

if ($DryRun) {
    $resolved | Add-Member -NotePropertyName dry_run -NotePropertyValue $true
    $resolved | ConvertTo-Json -Depth 5
    return
}

# ─── Launch conductor ────────────────────────────────────────────────────────

# Pre-resolve GH_TOKEN from the active gh auth so conductor's gh subprocesses
# don't fight Windows Credential Manager / DPAPI in a non-TTY context.
# Empirically (AB#3065 dogfood, 2026-05-09): gh CLI invocations from conductor's
# Start-Process -WindowStyle Hidden subprocess hang at 60s × 3 retries when DPAPI
# tries to surface a credential prompt that has nowhere to render. The same gh
# call from an interactive shell returns in ~500ms. Setting GH_TOKEN bypasses
# the keyring entirely and the subprocess gets the token via env. Idempotent —
# caller's pre-set GH_TOKEN wins.
if (-not $env:GH_TOKEN -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    try {
        $token = (& gh auth token --hostname github.com 2>$null | Out-String).Trim()
        if ($token -and $token -notmatch '\s') {
            $env:GH_TOKEN = $token
        }
    } catch {
        # Silent — caller can set GH_TOKEN explicitly if gh isn't available
        # or has no token. The conductor's own gh failure will surface clearly.
    }
}

# Verify conductor is on PATH so we fail fast with a clear message instead of
# Start-Process surfacing a generic "no such file" later.
if (-not (Get-Command conductor -ErrorAction SilentlyContinue)) {
    throw "conductor is not on PATH. Install it before invoking apex-driver."
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

$proc = Start-Process -FilePath 'conductor' `
    -ArgumentList $conductorArgs `
    -WorkingDirectory $WorktreeRoot `
    -WindowStyle Hidden `
    -PassThru

$resolved | Add-Member -NotePropertyName pid -NotePropertyValue $proc.Id
$resolved | Add-Member -NotePropertyName note -NotePropertyValue 'Conductor launched detached. Watch the dashboard at http://localhost:* (URL emitted by conductor on startup).'
$resolved | ConvertTo-Json -Depth 5
