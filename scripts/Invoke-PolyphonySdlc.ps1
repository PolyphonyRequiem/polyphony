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
    PR platform override. Default: read from `.twig/config` (always `ado` for now).

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

    [string]$Platform,

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

if (-not $Platform) {
    # Currently the only supported work-item provider is ADO; PR platform is a
    # separate axis handled by pr_platform_router. apex-driver's `platform` input
    # threads through to lifecycle workflows verbatim.
    $Platform = 'ado'
}

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

# ─── Build the command ───────────────────────────────────────────────────────

$conductorArgs = @(
    'run', 'apex-driver@polyphony'
    '--web'
    '--input', "apex_id=$ApexId"
    '--input', "intent=$Intent"
    '--input', "platform=$Platform"
    '--input', "organization=$($twigConfig.organization)"
    '--input', "project=$($twigConfig.project)"
    # apex-driver `repository` defaults to "" and is consumed only by ADO PR verbs;
    # we pass the worktree name as the repo identifier (overridable).
    '--input', "repository=$worktreeName"
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
