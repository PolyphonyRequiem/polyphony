<#
.SYNOPSIS
    One-shot operator install for polyphony on Windows.

.DESCRIPTION
    Downloads the latest polyphony binary release, the launcher scripts, and
    the polyphony-runtime + polyphony-bootstrap copilot CLI skills. Installs
    the binary + launcher into ~/.polyphony/bin/ and the skills into
    ~/.copilot/skills/ as user-globals so any future copilot session — in
    any cwd, on any repo — auto-discovers them.

    Idempotent: safe to re-run; overwrites in place.

    Recommended invocation:
        iex (irm https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/install.ps1)

.PARAMETER Version
    Pin to a specific release tag (e.g. "v2.4.0"). Defaults to "latest".

.NOTES
    What this script does NOT do:
      - install git, pwsh, or conductor — it warns if they're missing
        but never invokes a package manager. Operators should install
        these intentionally.
      - register polyphony with conductor — prints the command to run
        but does not execute it.
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest'
)
$ErrorActionPreference = 'Stop'

Write-Host "==> polyphony installer" -ForegroundColor Cyan

# ── Prereq check (warn-only) ─────────────────────────────────────────────────
$missing = @()
foreach ($cmd in 'git', 'pwsh') {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { $missing += $cmd }
}
# conductor is optional at install time but required to run workflows
$conductorPresent = [bool](Get-Command conductor -ErrorAction SilentlyContinue)

if ($missing) {
    Write-Host ""
    Write-Host "WARN: missing required commands: $($missing -join ', ')" -ForegroundColor Yellow
    Write-Host "      install them before running polyphony:" -ForegroundColor Yellow
    if ($missing -contains 'git')  { Write-Host "        git:  https://git-scm.com/download/win" -ForegroundColor Yellow }
    if ($missing -contains 'pwsh') { Write-Host "        pwsh: https://github.com/PowerShell/PowerShell" -ForegroundColor Yellow }
}
if (-not $conductorPresent) {
    Write-Host ""
    Write-Host "WARN: 'conductor' not found on PATH." -ForegroundColor Yellow
    Write-Host "      install with: pip install `"git+https://github.com/microsoft/conductor.git@main`"" -ForegroundColor Yellow
}

# ── Resolve release tag ──────────────────────────────────────────────────────
if ($Version -eq 'latest') {
    Write-Host "==> resolving latest release..." -ForegroundColor Cyan
    $rel = Invoke-RestMethod 'https://api.github.com/repos/PolyphonyRequiem/polyphony/releases/latest'
    $tag = $rel.tag_name
} else {
    $tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
}
$ver = $tag -replace '^v',''
Write-Host "    using release: $tag" -ForegroundColor Gray

# ── Download + verify binary ─────────────────────────────────────────────────
$rid = 'win-x64'
$asset = "polyphony-$ver-$rid.exe"
$base = "https://github.com/PolyphonyRequiem/polyphony/releases/download/$tag"
$installDir = Join-Path $env:USERPROFILE '.polyphony\bin'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# Migration warning: the canonical location moved from ~/.twig/bin to
# ~/.polyphony/bin. If a legacy install is still present, surface it so the
# operator can clean up after PATH is updated.
$legacyInstall = Join-Path $env:USERPROFILE '.twig\bin\polyphony.exe'
if (Test-Path $legacyInstall) {
    Write-Host ""
    Write-Host "==> NOTE: legacy install found at $legacyInstall" -ForegroundColor Yellow
    Write-Host "    The canonical install location is now ~/.polyphony/bin/." -ForegroundColor Yellow
    Write-Host "    After this install, verify ``Get-Command polyphony`` resolves to" -ForegroundColor Yellow
    Write-Host "    the new location, then remove the legacy copy:" -ForegroundColor Yellow
    Write-Host "      Remove-Item '$legacyInstall'" -ForegroundColor Yellow
    Write-Host ""
}

$tempBin = Join-Path ([IO.Path]::GetTempPath()) $asset
$tempSha = "$tempBin.sha256"

Write-Host "==> downloading binary ($asset)..." -ForegroundColor Cyan
Invoke-WebRequest -Uri "$base/$asset" -OutFile $tempBin
Invoke-WebRequest -Uri "$base/$asset.sha256" -OutFile $tempSha

$expected = (Get-Content $tempSha).Split(' ')[0]
$actual = (Get-FileHash $tempBin -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) {
    Remove-Item $tempBin, $tempSha -Force -ErrorAction SilentlyContinue
    throw "SHA256 mismatch for $asset (expected=$expected actual=$actual). Refusing to install."
}
Move-Item $tempBin (Join-Path $installDir 'polyphony.exe') -Force
Remove-Item $tempSha -Force

# ── Download launcher scripts ────────────────────────────────────────────────
# THE GAP: launcher scripts aren't bundled as release assets yet — fetched
# from main HEAD. Drifts vs binary version; acceptable until next release.
# After that ships, switch $launcherBase to "$base".
$launcherBase = 'https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/scripts'
Write-Host "==> downloading launcher scripts..." -ForegroundColor Cyan
foreach ($name in 'Invoke-PolyphonySdlc.ps1', 'Resolve-GhIdentity.ps1', 'Twig-Hydration.ps1', 'Migrate-ToBareRepo.ps1', 'Bootstrap-BareRepo.ps1', 'Sync-BareRepo.ps1', 'bootstrap-conductor.ps1') {
    $dest = Join-Path $installDir $name
    Invoke-WebRequest -Uri "$launcherBase/$name" -OutFile $dest
    Unblock-File -Path $dest
}

# ── Verify launcher scripts landed (defensive — Invoke-WebRequest with
# $ErrorActionPreference='Stop' throws on HTTP errors but silent partial
# downloads have been observed against transient GitHub raw 502s) ──────────
$expectedLaunchers = @(
    'Invoke-PolyphonySdlc.ps1',
    'Resolve-GhIdentity.ps1',
    'Twig-Hydration.ps1',
    'Migrate-ToBareRepo.ps1',
    'Bootstrap-BareRepo.ps1',
    'Sync-BareRepo.ps1',
    'bootstrap-conductor.ps1'
)
$missingLaunchers = @()
foreach ($name in $expectedLaunchers) {
    $p = Join-Path $installDir $name
    if (-not (Test-Path $p) -or (Get-Item $p).Length -lt 100) {
        $missingLaunchers += $name
    }
}
if ($missingLaunchers) {
    throw "launcher download incomplete: missing/truncated $($missingLaunchers -join ', ') under $installDir. Refusing to leave a half-installed environment."
}

# ── Ensure ~/.polyphony/bin on PATH ─────────────────────────────────────────
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$installDir*") {
    Write-Host "==> adding $installDir to User PATH..." -ForegroundColor Cyan
    [Environment]::SetEnvironmentVariable('Path', "$installDir;$userPath", 'User')
    $env:Path = "$installDir;$env:Path"
} else {
    $env:Path = "$installDir;$env:Path"
}

# ── Install both copilot skills user-global ─────────────────────────────────
$skillsDir = Join-Path $env:USERPROFILE '.copilot\skills'
New-Item -ItemType Directory -Force -Path $skillsDir | Out-Null
$skillBase = 'https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/.github/skills'

Write-Host "==> installing copilot skills (polyphony-runtime, polyphony-bootstrap)..." -ForegroundColor Cyan
foreach ($skill in 'polyphony-runtime', 'polyphony-bootstrap') {
    $skillDir = Join-Path $skillsDir $skill
    New-Item -ItemType Directory -Force -Path $skillDir | Out-Null
    Invoke-WebRequest -Uri "$skillBase/$skill/SKILL.md" -OutFile (Join-Path $skillDir 'SKILL.md')
}
$tmplDir = Join-Path $skillsDir 'polyphony-runtime\templates'
New-Item -ItemType Directory -Force -Path $tmplDir | Out-Null
Invoke-WebRequest -Uri "$skillBase/polyphony-runtime/templates/target-repo-stub.md" -OutFile (Join-Path $tmplDir 'target-repo-stub.md')

# ── Verify install resolved correctly ────────────────────────────────────────
$resolved = (Get-Command polyphony -ErrorAction Stop).Source
$expectedPath = Join-Path $installDir 'polyphony.exe'
if ($resolved -ne $expectedPath) {
    throw "polyphony resolves to '$resolved', expected '$expectedPath'. Check PATH ordering with ``Get-Command polyphony -All``."
}
Write-Host ""
Write-Host "==> install complete" -ForegroundColor Green
polyphony --version

# ── Next steps ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
if ($missing -or -not $conductorPresent) {
    Write-Host "  1. Install missing prereqs (see warnings above)." -ForegroundColor Gray
}
Write-Host "  - Register polyphony with conductor (one-time, per machine):" -ForegroundColor Gray
Write-Host "      conductor registry add polyphony PolyphonyRequiem/polyphony" -ForegroundColor Gray
Write-Host "  - Open a copilot CLI session in any repo and ask:" -ForegroundColor Gray
Write-Host "      'set up polyphony for this repo'   (triggers polyphony-bootstrap)" -ForegroundColor Gray
Write-Host "      'run polyphony for work item N'    (triggers polyphony-runtime)" -ForegroundColor Gray
