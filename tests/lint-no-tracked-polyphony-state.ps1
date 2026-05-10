<#
.SYNOPSIS
    CI lint — fails if the worktree-tracked `.polyphony/` directory reappears.

.DESCRIPTION
    Post-Rev 4.2 (PRs #251/#252/#254/#255) the polyphony per-root state
    (`run.yaml`, `run.lock`) lives EXCLUSIVELY at
    `<git-common-dir>/polyphony/<root_id>/` and must never be tracked in
    the worktree. The legacy `.polyphony/` directory is in `.gitignore`,
    but `.gitignore` does NOT prevent already-tracked paths from being
    committed nor does it prevent `git add -f` overrides.

    This lint walks the index (via `git ls-files`) and fails if any path
    under `.polyphony/` is tracked. The check is fast (single git plumbing
    call) and catches:

      - A revert/rebase that resurrects the deleted file.
      - A pre-Rev-4.2 binary that writes `.polyphony/run.yaml` and a
        contributor who runs `git add -f` to commit it.
      - A worktree carry-over (the original AB#3067 dogfood failure
        symptom) being mistakenly committed.

.PARAMETER RepoRoot
    Repository root. Default: parent of this script.

.OUTPUTS
    Exit 0 — clean (no `.polyphony/` paths tracked).
    Exit 1 — at least one `.polyphony/` path is tracked. Prints the
             offending paths and remediation guidance.
#>

[CmdletBinding()]
param(
    [string] $RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot '.git'))) {
    Write-Host "FATAL: $RepoRoot does not contain a .git directory" -ForegroundColor Red
    exit 2
}

Push-Location -LiteralPath $RepoRoot
try {
    # `git ls-files .polyphony/` lists every tracked path under the
    # directory. Empty output means nothing is tracked — the desired
    # state. `--error-unmatch` is intentionally NOT used (it would
    # error when zero paths match, which is the success case here).
    $tracked = git ls-files .polyphony/ 2>$null
} finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "FATAL: 'git ls-files .polyphony/' failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit 2
}

$paths = @($tracked | Where-Object { $_ -and $_.Trim() })

if ($paths.Count -eq 0) {
    Write-Host "PASS: no `.polyphony/` paths tracked in the worktree (Rev 4.2 invariant holds)" -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "FAIL: Rev 4.2 invariant violated — `.polyphony/` paths are tracked:" -ForegroundColor Red
foreach ($p in $paths) {
    Write-Host "  - $p" -ForegroundColor Red
}
Write-Host ""
Write-Host "Per Rev 4.2 (PRs #251/#252/#254/#255), polyphony per-root state lives" -ForegroundColor Cyan
Write-Host "EXCLUSIVELY at <git-common-dir>/polyphony/<root_id>/. The worktree" -ForegroundColor Cyan
Write-Host "`.polyphony/` directory must never be committed." -ForegroundColor Cyan
Write-Host ""
Write-Host "Remediation:" -ForegroundColor Cyan
Write-Host "  1. Confirm `.polyphony/` is in `.gitignore`." -ForegroundColor Cyan
Write-Host "  2. Untrack the offending paths:" -ForegroundColor Cyan
Write-Host "       git rm --cached -r .polyphony/" -ForegroundColor Cyan
Write-Host "  3. Verify your local polyphony binary is post-Rev-4.2 (PR #255 or later)." -ForegroundColor Cyan
Write-Host "     A pre-Rev-4.2 binary will keep regenerating the worktree state file." -ForegroundColor Cyan
Write-Host ""
exit 1
