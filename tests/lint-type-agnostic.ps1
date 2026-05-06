<#
.SYNOPSIS
    CI lint — ensures the repo is free of hardcoded work-item type-name literals.
.DESCRIPTION
    Scans selected surfaces of the repo for ADO work-item type-name literals
    (Epic, Issue, Task, User Story, Bug, Feature) used as code or prose
    identifiers. Type-name literals violate P5 (type-agnostic workflow
    structure) — type names belong in process-config.yaml at runtime, never
    baked into shipping artifacts.

    Surfaces:
        scripts — production *.ps1 under scripts/ (excludes *.Tests.ps1).
                  Comment lines (first non-whitespace is '#') are skipped to
                  preserve historic exemption for code comments.
        yaml    — workflow definitions under .conductor/registry/workflows/.
                  Every line is scanned (including comment lines), since
                  workflow YAML comments are spec-bearing and must follow
                  the same rule as everything else.
        skills  — agent-loadable skill content under .github/skills/.
                  Every line is scanned. Markdown comments (rare) are not
                  syntactically distinct, so no comment-skip applies.
        docs    — repo documentation under docs/. Every line is scanned.
        all     — all of the above (default).

    Allowlist:
        tests/lint-type-agnostic.allowlist.yaml (optional). Two sections:
            skip_files          — repo-relative path globs to exclude entirely.
            allowed_substrings  — regex patterns. Each match on a line is
                                  removed from the line BEFORE the type-name
                                  detector runs. So a line with both a legit
                                  reference (e.g. "Feature branch") and an
                                  illegit one (e.g. "Issue") still fails —
                                  only the legit substring is masked out.
        Missing allowlist file = empty allowlist (no skips). Schema errors
        in the file abort the lint with exit 2.

    Exit codes: 0 clean, 1 violations found, 2 configuration error.
#>
[CmdletBinding()]
param(
    [ValidateSet('scripts', 'yaml', 'skills', 'docs', 'all')]
    [string]$Surface = 'all',

    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string]$AllowlistPath = (Join-Path $PSScriptRoot 'lint-type-agnostic.allowlist.yaml')
)
$ErrorActionPreference = 'Stop'

# ADO work-item type names that must not appear as literals anywhere.
$typeNames = @('Epic', 'Issue', 'Task', 'User Story', 'Bug', 'Feature')
$typePattern = '\b(' + ($typeNames -join '|') + ')\b'

# ── Allowlist parsing ────────────────────────────────────────────────────
# Minimal hand-rolled parser for our tiny two-section format. Avoids a hard
# dependency on the powershell-yaml module (not guaranteed in CI). Strict —
# rejects malformed input rather than silently dropping it.
function Read-Allowlist {
    param([string]$Path)

    $allowlist = [PSCustomObject]@{
        SkipFiles         = @()
        AllowedSubstrings = @()
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return $allowlist
    }

    $skipFiles = New-Object System.Collections.Generic.List[string]
    $allowed = New-Object System.Collections.Generic.List[string]
    $section = $null
    $lineNo = 0

    foreach ($raw in (Get-Content -LiteralPath $Path)) {
        $lineNo++
        $trimmed = $raw.TrimEnd()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.TrimStart().StartsWith('#')) {
            continue
        }

        # Section header: "key:" or "key: []" at column 0
        if ($trimmed -match '^([A-Za-z_][A-Za-z0-9_]*):\s*(\[\s*\])?\s*$') {
            $section = $Matches[1]
            if ($section -notin @('skip_files', 'allowed_substrings')) {
                throw "lint allowlist (${Path}:${lineNo}): unknown section '$section'. Allowed: skip_files, allowed_substrings."
            }
            continue
        }

        # List item: "  - \"value\"" or "  - 'value'"
        if ($trimmed -match '^\s+-\s+"((?:[^"\\]|\\.)*)"\s*$' -or $trimmed -match "^\s+-\s+'((?:[^'\\]|\\.)*)'\s*$") {
            if ($null -eq $section) {
                throw "lint allowlist (${Path}:${lineNo}): list item before any section header."
            }
            $value = $Matches[1] -replace '\\"', '"' -replace "\\'", "'"
            switch ($section) {
                'skip_files'         { $skipFiles.Add($value) }
                'allowed_substrings' { $allowed.Add($value) }
            }
            continue
        }

        throw "lint allowlist (${Path}:${lineNo}): unrecognized syntax — '$trimmed'. Expected 'section:' or '  - `"value`"'."
    }

    # Validate every allowed_substring as a compilable regex.
    foreach ($pat in $allowed) {
        try { [void][regex]::new($pat) }
        catch { throw "lint allowlist (${Path}): invalid regex in allowed_substrings — '$pat': $($_.Exception.Message)" }
    }

    $allowlist.SkipFiles = $skipFiles.ToArray()
    $allowlist.AllowedSubstrings = $allowed.ToArray()
    return $allowlist
}

# ── Path helpers ─────────────────────────────────────────────────────────
function ConvertTo-RepoRelative {
    param([string]$AbsolutePath, [string]$Root)
    $rootFull = (Resolve-Path -LiteralPath $Root).Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $absFull = (Resolve-Path -LiteralPath $AbsolutePath).Path
    if ($absFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        $rel = $absFull.Substring($rootFull.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    } else {
        $rel = $absFull
    }
    # Normalize to forward slashes for cross-platform glob matching.
    return $rel -replace '\\', '/'
}

function Test-Skipped {
    param([string]$RelPath, [string[]]$Globs)
    foreach ($g in $Globs) {
        if ($RelPath -like $g) { return $true }
    }
    return $false
}

# ── Surface scanners ─────────────────────────────────────────────────────
function Get-SurfaceFiles {
    param([string]$SurfaceName, [string]$Root)

    switch ($SurfaceName) {
        'scripts' {
            $dir = Join-Path $Root 'scripts'
            if (-not (Test-Path -LiteralPath $dir)) { return @() }
            return @(Get-ChildItem -LiteralPath $dir -Filter '*.ps1' -Recurse -File |
                Where-Object { $_.Name -notmatch '\.Tests\.ps1$' })
        }
        'yaml' {
            $dir = Join-Path $Root '.conductor/registry/workflows'
            if (-not (Test-Path -LiteralPath $dir)) { return @() }
            return @(Get-ChildItem -LiteralPath $dir -Filter '*.yaml' -Recurse -File)
        }
        'skills' {
            $dir = Join-Path $Root '.github/skills'
            if (-not (Test-Path -LiteralPath $dir)) { return @() }
            return @(Get-ChildItem -LiteralPath $dir -Filter '*.md' -Recurse -File)
        }
        'docs' {
            $dir = Join-Path $Root 'docs'
            if (-not (Test-Path -LiteralPath $dir)) { return @() }
            return @(Get-ChildItem -LiteralPath $dir -Filter '*.md' -Recurse -File)
        }
        default { throw "Unknown surface '$SurfaceName'." }
    }
}

function Test-SkipCommentLine {
    param([string]$SurfaceName, [string]$Line)
    # Scripts: preserve historic exemption for comment lines (#-prefixed).
    # YAML: workflow comments are spec-bearing — scan them too.
    if ($SurfaceName -eq 'scripts') {
        return $Line.TrimStart().StartsWith('#')
    }
    return $false
}

function Find-Violations {
    param(
        [string]$SurfaceName,
        [string]$Root,
        [PSCustomObject]$Allowlist
    )

    $files = Get-SurfaceFiles -SurfaceName $SurfaceName -Root $Root
    $violations = New-Object System.Collections.Generic.List[object]
    $scanned = 0

    foreach ($file in $files) {
        $rel = ConvertTo-RepoRelative -AbsolutePath $file.FullName -Root $Root
        if (Test-Skipped -RelPath $rel -Globs $Allowlist.SkipFiles) { continue }
        $scanned++

        $lineNo = 0
        foreach ($rawLine in (Get-Content -LiteralPath $file.FullName)) {
            $lineNo++
            if (Test-SkipCommentLine -SurfaceName $SurfaceName -Line $rawLine) { continue }

            # Mask allowed substrings before running type-name detection.
            $masked = $rawLine
            foreach ($pat in $Allowlist.AllowedSubstrings) {
                $masked = [regex]::Replace($masked, $pat, '')
            }

            # Case-sensitive type-name detection on the masked line.
            $matchInfo = [regex]::Matches($masked, $typePattern)
            if ($matchInfo.Count -gt 0) {
                $violations.Add([PSCustomObject]@{
                    Surface  = $SurfaceName
                    RelPath  = $rel
                    LineNo   = $lineNo
                    Line     = $rawLine.Trim()
                    Hits     = @($matchInfo | ForEach-Object { $_.Value }) -join ', '
                })
            }
        }
    }

    return [PSCustomObject]@{
        Scanned    = $scanned
        Violations = $violations.ToArray()
    }
}

# ── Main ─────────────────────────────────────────────────────────────────
try {
    $allowlist = Read-Allowlist -Path $AllowlistPath
} catch {
    Write-Host "lint configuration error: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$surfacesToScan = if ($Surface -eq 'all') { @('scripts', 'yaml', 'skills', 'docs') } else { @($Surface) }
$totalScanned = 0
$allViolations = New-Object System.Collections.Generic.List[object]

foreach ($s in $surfacesToScan) {
    $result = Find-Violations -SurfaceName $s -Root $RepoRoot -Allowlist $allowlist
    $totalScanned += $result.Scanned
    foreach ($v in $result.Violations) { $allViolations.Add($v) }
}

if ($allViolations.Count -gt 0) {
    Write-Host "FAIL: $($allViolations.Count) type-name literal(s) found (violates P5)" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $allViolations) {
        Write-Host ("  [{0}] {1}:{2} ({3}): {4}" -f $v.Surface, $v.RelPath, $v.LineNo, $v.Hits, $v.Line) -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'Fix: replace type-name literals with polyphony facet/hierarchy queries,' -ForegroundColor Cyan
    Write-Host '     or — if the reference is legitimate terminology — add a precise' -ForegroundColor Cyan
    Write-Host '     pattern to tests/lint-type-agnostic.allowlist.yaml.' -ForegroundColor Cyan
    exit 1
}

if ($totalScanned -eq 0) {
    Write-Host "No files scanned for surface '$Surface'." -ForegroundColor Yellow
    exit 0
}

Write-Host ("PASS: No type-name literals found ({0} file(s) scanned across surface '{1}')" -f $totalScanned, $Surface) -ForegroundColor Green
exit 0
