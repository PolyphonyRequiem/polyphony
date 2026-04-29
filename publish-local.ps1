#!/usr/bin/env pwsh
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot "src/Polyphony/Polyphony.csproj"
$outputDir = Join-Path $env:USERPROFILE ".twig/bin"

if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

Write-Host "Building Polyphony ($Configuration)..." -ForegroundColor Cyan
dotnet publish $projectPath -c $Configuration -o $outputDir --no-restore

Write-Host "Published to: $outputDir" -ForegroundColor Green
Write-Host "Binary: $(Join-Path $outputDir 'polyphony.exe')" -ForegroundColor Green
