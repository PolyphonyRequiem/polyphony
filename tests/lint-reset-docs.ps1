<#
.SYNOPSIS
    CI lint — validates polyphony-reset.md cross-references and structural
    completeness.

.DESCRIPTION
    Ensures the polyphony-reset.md documentation satisfies the AB#3180
    acceptance criteria:

    1. The reset verb is documented with scrub scope, flags, UX, and archive
       sidecar location.
    2. The remediation pattern is explicitly described.
    3. Cross-references to existing tag/branch docs exist.
    4. The CLI reference (polyphony-cli-reference.md) includes a reset entry.
    5. The skills index (polyphony-skills-index.md) references the reset doc.
    6. The glossary (glossary.md) includes reset and PolyphonyTag DU entries.
    7. The state-effects catalog includes a reset entry.

    Exit codes: 0 clean, 1 violations found.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$violations = @()

function Assert-FileContains {
    param(
        [string]$File,
        [string]$Pattern,
        [string]$Description
    )
    $path = Join-Path $RepoRoot $File
    if (-not (Test-Path $path)) {
        $script:violations += "$File — file not found ($Description)"
        return
    }
    $content = Get-Content -LiteralPath $path -Raw
    if ($content -notmatch $Pattern) {
        $script:violations += "$File — missing: $Description"
    }
}

# ── 1. polyphony-reset.md exists and has required sections ────────────────

$resetDoc = Join-Path $RepoRoot 'docs/polyphony-reset.md'
if (-not (Test-Path $resetDoc)) {
    $violations += 'docs/polyphony-reset.md — file not found'
} else {
    $content = Get-Content -LiteralPath $resetDoc -Raw

    # Scrub scope section
    if ($content -notmatch '(?i)## Scrub scope') {
        $violations += 'docs/polyphony-reset.md — missing "Scrub scope" section'
    }

    # Flags: --root-id, --force, --dry-run
    foreach ($flag in @('--root-id', '--force', '--dry-run')) {
        if ($content -notmatch [regex]::Escape($flag)) {
            $violations += "docs/polyphony-reset.md — missing flag: $flag"
        }
    }

    # Confirmation gate UX
    if ($content -notmatch '(?i)confirm') {
        $violations += 'docs/polyphony-reset.md — missing confirmation gate documentation'
    }

    # Archive sidecar location
    if ($content -notmatch 'comment-archive\.json') {
        $violations += 'docs/polyphony-reset.md — missing comment-archive sidecar location'
    }

    # Remediation pattern
    if ($content -notmatch '(?i)## The remediation pattern') {
        $violations += 'docs/polyphony-reset.md — missing "The remediation pattern" section'
    }

    # PolyphonyTag DU reference
    if ($content -notmatch '(?i)PolyphonyTag') {
        $violations += 'docs/polyphony-reset.md — missing PolyphonyTag DU reference'
    }

    # Cross-reference to branch model
    if ($content -notmatch 'branch-model') {
        $violations += 'docs/polyphony-reset.md — missing cross-reference to branch-model.md'
    }

    # Cross-reference to polyphony-tags.md
    if ($content -notmatch 'polyphony-tags') {
        $violations += 'docs/polyphony-reset.md — missing cross-reference to polyphony-tags.md'
    }

    # Cross-reference to per-run-worktree-layout.md
    if ($content -notmatch 'per-run-worktree-layout') {
        $violations += 'docs/polyphony-reset.md — missing cross-reference to per-run-worktree-layout.md'
    }

    # JSON output contract
    if ($content -notmatch '(?i)JSON output') {
        $violations += 'docs/polyphony-reset.md — missing JSON output contract'
    }

    # ADO tags scrub scope
    if ($content -notmatch '(?i)ADO tags') {
        $violations += 'docs/polyphony-reset.md — missing ADO tags scrub scope'
    }

    # Branches scrub scope
    if ($content -notmatch 'feature/') {
        $violations += 'docs/polyphony-reset.md — missing branch patterns in scrub scope'
    }

    # Worktrees scrub scope
    if ($content -notmatch '(?i)worktree') {
        $violations += 'docs/polyphony-reset.md — missing worktree scrub scope'
    }
}

# ── 2. CLI reference includes reset ──────────────────────────────────────

Assert-FileContains `
    -File 'docs/polyphony-cli-reference.md' `
    -Pattern '(?i)polyphony reset' `
    -Description 'reset verb entry'

Assert-FileContains `
    -File 'docs/polyphony-cli-reference.md' `
    -Pattern '(?i)reset.*scrub' `
    -Description 'reset verb description in verbs-at-a-glance'

# ── 3. Skills index references reset doc ─────────────────────────────────

Assert-FileContains `
    -File 'docs/polyphony-skills-index.md' `
    -Pattern 'polyphony-reset' `
    -Description 'cross-reference to polyphony-reset.md'

# ── 4. Glossary includes reset and PolyphonyTag entries ──────────────────

Assert-FileContains `
    -File 'docs/glossary.md' `
    -Pattern '(?i)polyphony reset' `
    -Description 'Reset (polyphony reset) glossary entry'

Assert-FileContains `
    -File 'docs/glossary.md' `
    -Pattern '(?i)PolyphonyTag DU' `
    -Description 'PolyphonyTag DU glossary entry'

# ── 5. State effects catalog includes reset ──────────────────────────────

Assert-FileContains `
    -File 'docs/polyphony-state-effects-catalog.md' `
    -Pattern '(?i)polyphony reset' `
    -Description 'reset verb state-effects entry'

# ── Report ───────────────────────────────────────────────────────────────

if ($violations.Count -eq 0) { exit 0 }

Write-Output ''
Write-Output 'lint-reset-docs: violations found'
Write-Output '-----------------------------------'
foreach ($v in $violations) {
    Write-Output "  $v"
}
Write-Output ''
Write-Output "Total: $($violations.Count) violation(s)"
Write-Output ''

exit 1
