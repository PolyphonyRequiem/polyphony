# Twig-Hydration.ps1
# Dot-sourced by Invoke-PolyphonySdlc.ps1 and by its Pester tests.
# Provides `Copy-MissingTwigEntries` and `Assert-ApexTwigWorkspace` for
# hydrating a per-apex worktree's `.twig/` from the main worktree's `.twig/`
# without overwriting any operator-curated state.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Recursively copy missing entries from $SourceRoot into $DestinationRoot.
# Existing destination files/directories are NEVER overwritten — resume safety
# is the design constraint. At the top level, $ExcludeAtRoot names are skipped.
# Below the top level, all missing children are filled in (catches partial
# directories from interrupted prior runs).
function Copy-MissingTwigEntries {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [string[]]$ExcludeAtRoot = @()
    )
    if (-not (Test-Path $SourceRoot -PathType Container)) { return }
    if (-not (Test-Path $DestinationRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    }
    Get-ChildItem -LiteralPath $SourceRoot -Force | ForEach-Object {
        if ($ExcludeAtRoot -contains $_.Name) { return }
        $dst = Join-Path $DestinationRoot $_.Name
        if ($_.PSIsContainer) {
            if (-not (Test-Path $dst -PathType Container)) {
                # Whole subtree missing — bulk recursive copy is fine.
                Copy-Item -LiteralPath $_.FullName -Destination $dst -Recurse -Force
            } else {
                # Subtree partially exists — recurse to fill missing leaves.
                Copy-MissingTwigEntries -SourceRoot $_.FullName -DestinationRoot $dst
            }
        } else {
            if (-not (Test-Path $dst)) {
                Copy-Item -LiteralPath $_.FullName -Destination $dst -Force
            }
        }
    }
}

# Verify the apex `.twig/` workspace has the DB file twig will look for at
# DI-resolution time. Throws with operator remediation if missing.
function Assert-ApexTwigWorkspace {
    param(
        [Parameter(Mandatory)][string]$ApexTwigDir,
        [Parameter(Mandatory)][string]$Organization,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][int]   $ApexId,
        [Parameter(Mandatory)][string]$MainWorktree
    )
    $expectedDb = Join-Path $ApexTwigDir (Join-Path $Organization (Join-Path $Project 'twig.db'))
    if (-not (Test-Path $expectedDb -PathType Leaf)) {
        throw @"
[polyphony-sdlc] Apex twig workspace is missing its DB:
  $expectedDb

Main worktree's .twig/ also lacks the workspace DB for $Organization/$Project,
so the launcher cannot hydrate the apex worktree from it.

Bootstrap twig in the main worktree first:
    cd $MainWorktree
    twig init $Organization $Project
    twig set $ApexId

Then re-run the launcher.
"@
    }
}
