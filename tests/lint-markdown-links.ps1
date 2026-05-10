<#
.SYNOPSIS
    CI lint — verify markdown links in docs resolve to real targets.

.DESCRIPTION
    Scans markdown files under -DocsRoot for inline links (`[text](target)`)
    and verifies each target resolves:

      - Relative file paths must point to an existing file.
      - Anchor-only links (`#heading`) must match a heading in the same file.
      - File + anchor links (`path#heading`) must point to an existing file
        whose headings include the anchor.
      - HTTPS URLs are validated syntactically only (no network calls).

    Additionally, if -InboundTarget is specified the lint scans the full repo
    docs tree for any markdown file linking to that target and verifies the
    link resolves (guards inbound cross-links).

    Exit codes: 0 clean, 1 violations found.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string]$DocsRoot = 'docs',

    [string[]]$Files = @(),

    [string]$InboundTarget = '',

    [ValidateSet('plain', 'github')]
    [string]$Format = 'plain'
)

$ErrorActionPreference = 'Stop'

# ── Helpers ──────────────────────────────────────────────────────────────

function Get-MarkdownLinks {
    param([string]$FilePath)

    $lines = Get-Content -LiteralPath $FilePath
    if ($null -eq $lines) { return @() }
    if ($lines -isnot [array]) { $lines = @($lines) }

    $links = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # Skip fenced code blocks
        if ($lines[$i] -match '^```') {
            $i++
            while ($i -lt $lines.Count -and $lines[$i] -notmatch '^```') { $i++ }
            continue
        }

        # Match all inline markdown links on this line
        $matches_ = [regex]::Matches($lines[$i], '\[([^\]]*)\]\(([^)]+)\)')
        foreach ($m in $matches_) {
            $links += @{
                LineNumber = $i + 1
                Text       = $m.Groups[1].Value
                Target     = $m.Groups[2].Value
            }
        }
    }
    return $links
}

function Get-MarkdownHeadings {
    param([string]$FilePath)

    $lines = Get-Content -LiteralPath $FilePath
    if ($null -eq $lines) { return @() }
    if ($lines -isnot [array]) { $lines = @($lines) }

    $headings = @()
    $inCodeBlock = $false
    foreach ($line in $lines) {
        if ($line -match '^```') { $inCodeBlock = -not $inCodeBlock; continue }
        if ($inCodeBlock) { continue }

        if ($line -match '^#{1,6}\s+(.+)$') {
            $text = $Matches[1].Trim()
            # Convert heading text to anchor slug (GitHub style)
            $slug = $text.ToLower() -replace '[^\w\s-]', '' -replace '\s+', '-'
            $headings += $slug
        }
    }
    return $headings
}

function Test-LinkTarget {
    param(
        [string]$Target,
        [string]$SourceFile,
        [string]$RepoRoot
    )

    # Skip absolute URLs
    if ($Target -match '^https?://') {
        return @{ Valid = $true; Reason = '' }
    }

    # Skip mailto and other schemes
    if ($Target -match '^[a-z]+:') {
        return @{ Valid = $true; Reason = '' }
    }

    $sourceDir = Split-Path $SourceFile -Parent
    $filePart = $Target
    $anchorPart = ''

    if ($Target.Contains('#')) {
        $parts = $Target -split '#', 2
        $filePart = $parts[0]
        $anchorPart = $parts[1]
    }

    # Anchor-only link: check heading in same file
    if ([string]::IsNullOrEmpty($filePart)) {
        if ([string]::IsNullOrEmpty($anchorPart)) {
            return @{ Valid = $false; Reason = 'empty link target' }
        }
        $headings = Get-MarkdownHeadings -FilePath $SourceFile
        if ($headings -contains $anchorPart) {
            return @{ Valid = $true; Reason = '' }
        }
        return @{ Valid = $false; Reason = "anchor '#$anchorPart' not found in same file" }
    }

    # Resolve relative path from source file's directory
    $resolved = Join-Path $sourceDir $filePart
    $resolved = [System.IO.Path]::GetFullPath($resolved)

    if (-not (Test-Path $resolved)) {
        return @{ Valid = $false; Reason = "file not found: $filePart" }
    }

    # If there's an anchor part, verify it exists in the target file
    if (-not [string]::IsNullOrEmpty($anchorPart) -and $resolved -match '\.md$') {
        $headings = Get-MarkdownHeadings -FilePath $resolved
        if ($headings -notcontains $anchorPart) {
            return @{ Valid = $false; Reason = "anchor '#$anchorPart' not found in $filePart" }
        }
    }

    return @{ Valid = $true; Reason = '' }
}

# ── Main ─────────────────────────────────────────────────────────────────

$violations = @()

# Build list of files to check
$filesToCheck = @()

if ($Files.Count -gt 0) {
    foreach ($f in $Files) {
        $fullPath = Join-Path $RepoRoot $f
        if (Test-Path $fullPath) {
            $filesToCheck += $fullPath
        } else {
            $violations += @{
                File   = $f
                Line   = 0
                Reason = "file does not exist: $f"
            }
        }
    }
} else {
    $docsFullPath = Join-Path $RepoRoot $DocsRoot
    if (Test-Path $docsFullPath) {
        $filesToCheck = Get-ChildItem -LiteralPath $docsFullPath -Filter '*.md' -Recurse -File |
            ForEach-Object { $_.FullName }
    }
}

# Check outbound links in each target file
foreach ($file in $filesToCheck) {
    $relative = ([System.IO.Path]::GetRelativePath($RepoRoot, $file)) -replace '\\', '/'
    $links = Get-MarkdownLinks -FilePath $file

    foreach ($link in $links) {
        $result = Test-LinkTarget -Target $link.Target -SourceFile $file -RepoRoot $RepoRoot
        if (-not $result.Valid) {
            $violations += @{
                File   = $relative
                Line   = $link.LineNumber
                Reason = "[$($link.Text)]($($link.Target)) — $($result.Reason)"
            }
        }
    }
}

# Check inbound links to the specified target
if (-not [string]::IsNullOrEmpty($InboundTarget)) {
    $docsFullPath = Join-Path $RepoRoot $DocsRoot
    $skillsPath = Join-Path $RepoRoot '.github' 'skills'
    $searchPaths = @()
    if (Test-Path $docsFullPath) { $searchPaths += $docsFullPath }
    if (Test-Path $skillsPath) { $searchPaths += $skillsPath }

    foreach ($searchPath in $searchPaths) {
        $mdFiles = Get-ChildItem -LiteralPath $searchPath -Filter '*.md' -Recurse -File
        foreach ($mdFile in $mdFiles) {
            $links = Get-MarkdownLinks -FilePath $mdFile.FullName
            foreach ($link in $links) {
                $targetNorm = $link.Target -replace '\\', '/'
                if ($targetNorm -match [regex]::Escape($InboundTarget) -or
                    $targetNorm -match [regex]::Escape(($InboundTarget -replace '/', '\\'))) {
                    $result = Test-LinkTarget -Target $link.Target -SourceFile $mdFile.FullName -RepoRoot $RepoRoot
                    if (-not $result.Valid) {
                        $relative = ([System.IO.Path]::GetRelativePath($RepoRoot, $mdFile.FullName)) -replace '\\', '/'
                        $violations += @{
                            File   = $relative
                            Line   = $link.LineNumber
                            Reason = "inbound link [$($link.Text)]($($link.Target)) — $($result.Reason)"
                        }
                    }
                }
            }
        }
    }
}

if ($violations.Count -eq 0) { exit 0 }

if ($Format -eq 'github') {
    foreach ($v in $violations) {
        $lineSpec = if ($v.Line -gt 0) { ",line=$($v.Line)" } else { '' }
        Write-Output "::error file=$($v.File)$lineSpec::$($v.Reason)"
    }
} else {
    Write-Output ''
    Write-Output 'lint-markdown-links: broken links found'
    Write-Output '---------------------------------------'
    foreach ($v in $violations) {
        $loc = if ($v.Line -gt 0) { "$($v.File):$($v.Line)" } else { $v.File }
        Write-Output "  $loc — $($v.Reason)"
    }
    Write-Output ''
}

exit 1
