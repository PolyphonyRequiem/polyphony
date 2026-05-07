#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build polyphony and install the binary so PATH-resolution lands on the just-built artifact.

.DESCRIPTION
    Publishes polyphony to ~/.twig/bin (the canonical install location) and additionally
    copies to wherever `polyphony` currently resolves on PATH if that location differs.
    Verifies post-install that `(Get-Command polyphony).Source` points at a binary whose
    timestamp matches the just-built artifact; fails loudly otherwise.

    Closes AB#3012 — silent stale-binary symptom: publishing to .twig\bin alone does not
    update an earlier-PATH binary at .local\bin (or wherever the user installed previously),
    so subsequent `polyphony` invocations continue running the stale build.

.PARAMETER Configuration
    dotnet build configuration. Default: Release.

.PARAMETER NoVerify
    Skip the post-install staleness check. Use only when troubleshooting the verifier itself.
#>
param(
    [string]$Configuration = "Release",
    [switch]$NoVerify
)

$ErrorActionPreference = 'Stop'

$projectPath  = Join-Path $PSScriptRoot "src/Polyphony/Polyphony.csproj"
$primaryDest  = Join-Path $env:USERPROFILE ".twig/bin"
$primaryExe   = Join-Path $primaryDest 'polyphony.exe'

if (-not (Test-Path $primaryDest)) {
    New-Item -ItemType Directory -Path $primaryDest -Force | Out-Null
}

Write-Host "Building Polyphony ($Configuration)..." -ForegroundColor Cyan
dotnet publish $projectPath -c $Configuration -o $primaryDest
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

Write-Host "Published to: $primaryDest" -ForegroundColor Green
$builtMtime = (Get-Item $primaryExe).LastWriteTimeUtc

# Resolve every polyphony.exe currently on PATH (Get-Command -All returns them in PATH
# order, so [0] is the one PowerShell would actually invoke). Copy the freshly-built
# binary on top of any stale one earlier in the search order.
$resolved = @(Get-Command polyphony -All -ErrorAction SilentlyContinue |
    Where-Object { $_.Source } |
    Select-Object -ExpandProperty Source -Unique)

if (-not $resolved) {
    Write-Warning "polyphony is not on PATH. Add '$primaryDest' (or another shim location) to PATH."
    Write-Host "Installed binary: $primaryExe" -ForegroundColor Green
    return
}

$active = $resolved[0]
foreach ($path in $resolved) {
    if ($path -ieq $primaryExe) { continue }
    Write-Host "Mirroring to PATH-resolvable location: $path" -ForegroundColor Cyan
    try {
        Copy-Item -Path $primaryExe -Destination $path -Force
    } catch {
        Write-Warning "Failed to mirror $primaryExe -> $path : $($_.Exception.Message)"
    }
}

if ($NoVerify) {
    Write-Host "Skipping post-install staleness check (-NoVerify)." -ForegroundColor Yellow
    return
}

$activeMtime = (Get-Item $active).LastWriteTimeUtc
$drift = [Math]::Abs(($activeMtime - $builtMtime).TotalSeconds)
if ($drift -gt 5) {
    throw @"
Stale-binary check FAILED.
PATH resolves polyphony to: $active
That binary's mtime ($activeMtime UTC) does not match the just-built artifact at $primaryExe ($builtMtime UTC). Drift: ${drift}s.

Either an earlier-PATH location holds a stale build that publish-local could not overwrite (locked? permissions?), or the resolved path is not the one we copied to. Run `Get-Command polyphony -All` to inspect the search order.
"@
}

Write-Host "PATH resolution: $active" -ForegroundColor Green
Write-Host "Built mtime:     $builtMtime UTC" -ForegroundColor Green
Write-Host "Resolved mtime:  $activeMtime UTC (drift ${drift}s, OK)" -ForegroundColor Green
