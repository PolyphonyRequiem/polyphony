#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Canonical wrapper for `conductor run apex-driver@polyphony` — the polyphony SDLC entry point.

.DESCRIPTION
    Constructs the full conductor invocation with all required `--input` flags and the
    six `--metadata` (`-m`) fields the dashboard / observation filer / post-mortem skills
    consume. Validates that the worktree contains a `.twig/` directory (preflight gate
    addressing AB#3010 symptom), resolves org/project/repo from `.twig/config`, and
    launches conductor with the `--web` dashboard. By default opens conductor in a new
    PowerShell console window (TTY-attached) so gate agents can read stdin and the
    operator's launching shell stays free. Pass `-NoDetach` to run synchronously in
    the current shell instead.

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

# Always use foreground --web. We previously tried --web-bg for the
# detached path, but conductor's bg_runner spawns the workflow child
# with stdin=DEVNULL (cli/bg_runner.py:172,178,210). Any agent that
# reads stdin — most notably gate agents waiting for the operator's
# answer fallback — hits EOFError immediately on startup, kills the
# subworkflow, and cascades up through for_each_item_failed. The web
# dashboard's "Answer" button never gets a listener because the gate
# agent died before it could register one. See AB#3071 dogfood
# 2026-05-11 (PR #300 regression).
#
# The detached path below spawns conductor inside a *new* PowerShell
# console window so it inherits a real TTY (stdin attached). That
# satisfies foreground --web's TTY requirement without blocking the
# operator's launching shell.
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

# ─── Pin gh identity (github platform only) ──────────────────────────────────

# Auto-detect whoever is logged into gh at this moment, capture that user's
# token, validate it against the GitHub API, then pin the validated identity
# into GH_TOKEN + GH_HOST for the entire conductor + polyphony subprocess
# tree. This protects against two concrete failure modes:
#
#   1. Competing-worker auth slippage. Another agent in a sibling worktree
#      can call `gh auth switch` mid-run, flipping the active gh user out
#      from under us. Without pinning, every subsequent gh call inside
#      conductor re-resolves the active user — and if the new active user
#      has no token cached for github.com, gh falls through to DPAPI in a
#      non-TTY context and hangs the per-attempt timeout (60s × 3 retries).
#      Diagnosed during the AB#3066 dogfood (2026-05-09).
#
#   2. Stale or wrong-scope token. By validating once at launch, an
#      expired/insufficient-scope token surfaces as a clear fail-fast at
#      startup with the exact remediation command, instead of as 60s gh
#      hangs on every PR-poll cycle hours later.
#
# Identity is auto-detected (whoever is active right now) — no hardcoded
# username, no flag required from the caller. The diagnostic emit prints the
# resolved login so the operator can spot a wrong-account launch immediately.
#
# Skipped on -Platform ado (no gh needed) and on -DryRun (no subprocess
# calls during dry-run).
if ($Platform -eq 'github') {
    . (Join-Path $PSScriptRoot 'Resolve-GhIdentity.ps1')
    try {
        $identity = Resolve-GhIdentity
    } catch {
        # Re-throw with launcher context so the operator knows where the
        # failure originated. The inner exception already carries the
        # actionable remediation command.
        throw "[polyphony-sdlc] gh identity probe failed:`n$($_.Exception.Message)"
    }
    $env:GH_TOKEN = $identity.Token
    $env:GH_HOST  = 'github.com'
    Write-Host "[polyphony-sdlc] gh identity pinned: user='$($identity.User)' source=$($identity.Source) token_len=$($identity.TokenLength)" -ForegroundColor Cyan
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

$logDir = Join-Path ([System.IO.Path]::GetTempPath()) 'polyphony-sdlc-runs'
[void](New-Item -ItemType Directory -Path $logDir -Force)
$logTimestamp = (Get-Date -Format 'yyyyMMdd-HHmmss')
$transcriptLog = Join-Path $logDir "apex-${ApexId}-${logTimestamp}-transcript.log"

# Spawn conductor inside a new PowerShell console window. The new console
# gives conductor a real TTY (stdin attached), which is required for both:
#   1. foreground --web mode itself (Rich/Textual rendering),
#   2. gate agents that fall back to stdin when no web answer arrives.
#
# Why not Start-Process conductor directly with -RedirectStandard*?
#   Start-Process redirected stdin → conductor's gate agents EOFError on
#   startup (same failure shape as --web-bg's stdin=DEVNULL).
#
# Why not -WindowStyle Hidden?
#   No console handles → conductor (Rich) hangs at 0% CPU on first output
#   flush, dashboard port never opens.
#
# Why -NoExit?
#   Keeps the new window open after conductor exits so the operator can
#   read the final stack trace / exit code without losing it.
#
# The launching shell stays free; the operator can minimize the new
# window. Output is also tee'd to a transcript file under
# %TEMP%\polyphony-sdlc-runs\ for post-mortem.

# Build the conductor command line as a single string. Quote individual
# args defensively in case any value contains whitespace or quotes.
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
