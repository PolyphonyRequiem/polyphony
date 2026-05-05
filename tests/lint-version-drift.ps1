<#
.SYNOPSIS
    CI lint — assert workflow YAML versions are aligned and match index.yaml.
.DESCRIPTION
    Enforces the bundled-SemVer invariant captured in
    docs/decisions/versioning-strategy.md:

      1. Every workflow YAML in .conductor/registry/workflows/ declares a
         non-empty `workflow.version: "X.Y.Z"`.
      2. Every workflow YAML's version equals the LAST element of its
         entry's `versions: [...]` array in .conductor/registry/index.yaml
         (the array is the append-only release manifest; the latest entry
         is always the current version).
      3. All workflow YAMLs declare the SAME `workflow.version`
         (bundled-SemVer invariant — one tag drives all artifacts).
      4. Every workflow YAML referenced in index.yaml exists on disk, and
         every YAML on disk has an index.yaml entry.
      5. Every workflow YAML declares `workflow.metadata.min_polyphony_version`
         AND that value equals the YAML's own `workflow.version` (bundled =
         self-required: a v1.2.3 workflow requires polyphony v1.2.3+).

    Skips gracefully if .conductor/registry/ is not laid out (e.g. on a
    branch that pre-dates the registry move).
.OUTPUTS
    Per-YAML PASS / FAIL lines on stdout. Exit 0 if all invariants hold,
    1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$RegistryRoot = (Join-Path $PSScriptRoot '..' '.conductor' 'registry')
)
$ErrorActionPreference = 'Stop'

$workflowsDir = Join-Path $RegistryRoot 'workflows'
$indexPath = Join-Path $RegistryRoot 'index.yaml'

if (-not (Test-Path $workflowsDir) -or -not (Test-Path $indexPath)) {
    Write-Host "SKIP: registry layout not found at $RegistryRoot" -ForegroundColor Yellow
    exit 0
}

$yamlFiles = @(Get-ChildItem $workflowsDir -Filter '*.yaml' -File)
$indexEntries = $null  # populated below

# Parse workflow.version from each YAML file.
# The version line appears at 2-space indent under `workflow:`. The naive
# regex is robust because workflow YAMLs only ever declare one top-level
# `workflow:` block.
function Get-WorkflowVersion {
    param([string]$Path)
    $lines = Get-Content $Path
    $inWorkflowBlock = $false
    foreach ($line in $lines) {
        if ($line -match '^workflow:\s*$') {
            $inWorkflowBlock = $true
            continue
        }
        if ($inWorkflowBlock -and $line -match '^[A-Za-z_]') {
            # Left the workflow block.
            return $null
        }
        if ($inWorkflowBlock -and $line -match '^\s\s+version:\s*[''"]?([^''"\s]+)[''"]?\s*$') {
            return $matches[1]
        }
    }
    return $null
}

# Parse workflow.metadata.min_polyphony_version from a YAML file.
# The metadata block is a 2-space-indented child of `workflow:`; the
# field is at 4-space indent under `metadata:`. Returns $null if absent.
function Get-WorkflowMinVersion {
    param([string]$Path)
    $lines = Get-Content $Path
    $inWorkflowBlock = $false
    $inMetadataBlock = $false
    foreach ($line in $lines) {
        if ($line -match '^workflow:\s*$') {
            $inWorkflowBlock = $true
            continue
        }
        if ($inWorkflowBlock -and $line -match '^[A-Za-z_]') {
            return $null
        }
        if ($inWorkflowBlock -and $line -match '^\s\s+metadata:\s*$') {
            $inMetadataBlock = $true
            continue
        }
        # Any other 2-space-indented field after metadata: closes the block.
        if ($inMetadataBlock -and $line -match '^\s\s[A-Za-z_]') {
            $inMetadataBlock = $false
        }
        if ($inMetadataBlock -and $line -match '^\s\s\s\s+min_polyphony_version:\s*[''"]?([^''"\s]+)[''"]?\s*$') {
            return $matches[1]
        }
    }
    return $null
}

# Parse per-workflow `versions: [...]` arrays from index.yaml.
# Returns a hashtable: workflow-name -> @{ Path = string; LastVersion = string }
function Get-IndexVersions {
    param([string]$Path)
    $entries = @{}
    $lines = Get-Content $Path
    $currentName = $null
    foreach ($line in $lines) {
        if ($line -match '^\s\s([a-zA-Z][a-zA-Z0-9_\-]*):\s*$') {
            $currentName = $matches[1]
            $entries[$currentName] = @{ Path = $null; LastVersion = $null }
            continue
        }
        if ($null -ne $currentName) {
            if ($line -match '^\s\s\s\s+path:\s*(.+?)\s*$') {
                $entries[$currentName].Path = $matches[1].Trim('"', "'")
            }
            elseif ($line -match '^\s\s\s\s+versions:\s*\[(.+)\]\s*$') {
                # @(...) forces array semantics so a single-element list
                # like ["1.0.0"] doesn't get unwrapped to a scalar string
                # (which would make [-1] index into characters).
                $items = @($matches[1].Split(',') | ForEach-Object { $_.Trim().Trim('"', "'") })
                $entries[$currentName].LastVersion = $items[-1]
            }
        }
    }
    return $entries
}

$indexEntries = Get-IndexVersions -Path $indexPath

# Empty-but-present registry — nothing to check.
if ($yamlFiles.Count -eq 0 -and $indexEntries.Count -eq 0) {
    Write-Host "SKIP: registry is empty (no YAMLs and no index entries)" -ForegroundColor Yellow
    exit 0
}

$failed = @()
$declaredVersions = @{}

foreach ($yaml in $yamlFiles) {
    $name = $yaml.BaseName
    $yamlVersion = Get-WorkflowVersion -Path $yaml.FullName
    $entry = $indexEntries[$name]

    if (-not $yamlVersion) {
        Write-Host "FAIL: $($yaml.Name) — no workflow.version declared" -ForegroundColor Red
        $failed += $yaml.Name
        continue
    }

    if (-not $entry) {
        Write-Host "FAIL: $($yaml.Name) — no entry in index.yaml" -ForegroundColor Red
        $failed += $yaml.Name
        continue
    }

    if (-not $entry.LastVersion) {
        Write-Host "FAIL: $($yaml.Name) — index.yaml entry has no versions: [...] array" -ForegroundColor Red
        $failed += $yaml.Name
        continue
    }

    if ($yamlVersion -ne $entry.LastVersion) {
        Write-Host "FAIL: $($yaml.Name) — workflow.version '$yamlVersion' != index.yaml last version '$($entry.LastVersion)'" -ForegroundColor Red
        $failed += $yaml.Name
        continue
    }

    $minVersion = Get-WorkflowMinVersion -Path $yaml.FullName
    if (-not $minVersion) {
        Write-Host "FAIL: $($yaml.Name) — no workflow.metadata.min_polyphony_version declared" -ForegroundColor Red
        $failed += $yaml.Name
        continue
    }
    if ($minVersion -ne $yamlVersion) {
        Write-Host "FAIL: $($yaml.Name) — metadata.min_polyphony_version '$minVersion' != workflow.version '$yamlVersion' (bundled = self-required)" -ForegroundColor Red
        $failed += $yaml.Name
        continue
    }

    Write-Host "PASS: $($yaml.Name) — version $yamlVersion (matches index, min_polyphony_version aligned)" -ForegroundColor Green
    $declaredVersions[$name] = $yamlVersion
}

# Check for orphan index entries (in index.yaml but no YAML on disk).
foreach ($entryName in $indexEntries.Keys) {
    if (-not ($yamlFiles | Where-Object { $_.BaseName -eq $entryName })) {
        Write-Host "FAIL: index.yaml entry '$entryName' has no YAML on disk" -ForegroundColor Red
        $failed += "index:$entryName"
    }
}

# Bundled-SemVer invariant: all declared versions match.
# @(...) forces array semantics so a single-value Sort-Object -Unique
# doesn't get unwrapped to a scalar string.
$uniqueVersions = @($declaredVersions.Values | Sort-Object -Unique)
if ($uniqueVersions.Count -gt 1) {
    Write-Host ''
    Write-Host "FAIL: bundled-SemVer invariant broken — multiple versions declared:" -ForegroundColor Red
    foreach ($v in $uniqueVersions) {
        $names = ($declaredVersions.GetEnumerator() | Where-Object { $_.Value -eq $v } | ForEach-Object { $_.Key }) -join ', '
        Write-Host "  $v : $names" -ForegroundColor Yellow
    }
    $failed += 'bundled-semver-invariant'
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "FAIL: $($failed.Count) version-drift violation(s) detected" -ForegroundColor Red
    exit 1
}

Write-Host "PASS: all $($yamlFiles.Count) workflow YAMLs aligned at version $($uniqueVersions[0])" -ForegroundColor Green
exit 0