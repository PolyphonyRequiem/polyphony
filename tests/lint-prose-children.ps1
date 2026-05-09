<#
.SYNOPSIS
    CI lint — catches plan files that declare children in prose only.

.DESCRIPTION
    Scans `plans/*.md` for the false-satisfied bug class first surfaced by the
    AB#3064 dogfood: an architect emits a plan whose body lists child work
    items under a `## Child(ren) …` heading but whose YAML front-matter
    declares neither `apex_facets:` (the explicit-indivisibility marker) nor
    a structured `children:` block. In that shape `polyphony plan
    seed-children` historically stamped the `polyphony:planned` tag with no
    children created, the requirement-derivation observer treated the apex
    as `children_seeded=Satisfied`, and the apex terminated as satisfied
    without ever doing the work.

    `polyphony plan seed-children` already refuses this case at run-time
    (PR #225 / F3) by returning a structured error with a clear remediation
    hint. This lint is the upstream defense — it catches the same class of
    bug at PR-author time, on the plan PR diff, before the workflow even
    hits the seeder.

    Detection rule:
        For each plan file:
        1. Detect prose-children indicator: a markdown heading whose text
           starts with `Child` or `Children` (case-insensitive).
        2. If indicator present, REQUIRE at least one of:
             a. `apex_facets:` key in YAML front-matter.
             b. `children:` key in YAML front-matter.
        3. Otherwise emit a violation pointing at the heading.

    The disjunction matches F4 spec: either declaration constitutes
    explicit author intent. A plan that lists children only in prose, with
    no front-matter signal, is the exact bug case.

    Output: `plain` (default) emits a human-readable report. `github` emits
    GitHub Actions annotations (`::error file=…,line=…::message`).

    Exit codes: 0 clean, 1 violations found, 2 configuration error.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string]$PlansDir = 'plans',

    [ValidateSet('plain', 'github')]
    [string]$Format = 'plain'
)

$ErrorActionPreference = 'Stop'

# ── Front-matter parsing ─────────────────────────────────────────────────
# Returns @{ FrontMatterEndLine = <int 1-based, 0 if no front matter>;
#            Lines = string[] (raw lines, line N at index N-1) }.
function Read-PlanFile {
    param([string]$Path)

    $lines = Get-Content -LiteralPath $Path
    if ($null -eq $lines) { $lines = @() }
    elseif ($lines -isnot [array]) { $lines = @($lines) }

    $fmEnd = 0
    if ($lines.Count -ge 1 -and $lines[0] -match '^---\s*$') {
        for ($i = 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^---\s*$') {
                $fmEnd = $i + 1  # 1-based line number of closing fence
                break
            }
        }
    }

    return @{
        FrontMatterEndLine = $fmEnd
        Lines = $lines
    }
}

function Test-FrontMatterDeclaresIntent {
    param([string[]]$Lines, [int]$FrontMatterEndLine)

    if ($FrontMatterEndLine -lt 2) { return $false }

    # Front-matter body is lines 2..($FrontMatterEndLine - 1) inclusive
    # (1-based), i.e. indices 1..($FrontMatterEndLine - 2).
    for ($i = 1; $i -lt ($FrontMatterEndLine - 1); $i++) {
        if ($Lines[$i] -match '^\s*(apex_facets|children)\s*:') {
            return $true
        }
    }
    return $false
}

function Find-ProseChildrenHeadings {
    param([string[]]$Lines, [int]$FrontMatterEndLine)

    $headings = @()
    $startIndex = if ($FrontMatterEndLine -gt 0) { $FrontMatterEndLine } else { 0 }

    for ($i = $startIndex; $i -lt $Lines.Count; $i++) {
        # Markdown heading whose text starts with "Child" or "Children"
        # (case-insensitive). Word boundary keeps "Childhood" out.
        if ($Lines[$i] -match '^#{1,6}\s+Child(ren)?\b') {
            $headings += @{
                LineNumber = $i + 1  # 1-based
                Text = $Lines[$i].Trim()
            }
        }
    }
    return $headings
}

# ── Main ────────────────────────────────────────────────────────────────
$plansRoot = Join-Path $RepoRoot $PlansDir

if (-not (Test-Path $plansRoot)) {
    # No plans/ directory is not a configuration error — fresh repo, or the
    # convention isn't in use here. Emit nothing, succeed.
    exit 0
}

$planFiles = Get-ChildItem -LiteralPath $plansRoot -Filter '*.md' -File `
                           -ErrorAction SilentlyContinue
if (-not $planFiles) { exit 0 }

$violations = @()

foreach ($file in $planFiles) {
    $relative = ([System.IO.Path]::GetRelativePath($RepoRoot, $file.FullName)) `
                 -replace '\\', '/'
    $parsed = Read-PlanFile -Path $file.FullName
    $headings = Find-ProseChildrenHeadings `
                  -Lines $parsed.Lines `
                  -FrontMatterEndLine $parsed.FrontMatterEndLine

    if ($headings.Count -eq 0) { continue }

    $hasIntent = Test-FrontMatterDeclaresIntent `
                   -Lines $parsed.Lines `
                   -FrontMatterEndLine $parsed.FrontMatterEndLine

    if ($hasIntent) { continue }

    foreach ($h in $headings) {
        $violations += @{
            File = $relative
            Line = $h.LineNumber
            Heading = $h.Text
        }
    }
}

if ($violations.Count -eq 0) { exit 0 }

$message = "declares children in prose without front-matter signal — add ``apex_facets:`` (indivisible) or a structured ``children:`` block to the YAML front-matter, OR remove the prose section if children should not exist."

if ($Format -eq 'github') {
    foreach ($v in $violations) {
        # Single-line annotation; literal newlines would break the format.
        $msg = "$($v.Heading): $message"
        Write-Output "::error file=$($v.File),line=$($v.Line)::$msg"
    }
} else {
    Write-Output ''
    Write-Output 'lint-prose-children: violations found'
    Write-Output '------------------------------------'
    foreach ($v in $violations) {
        Write-Output "  $($v.File):$($v.Line) — $($v.Heading)"
    }
    Write-Output ''
    Write-Output "Each plan file above $message"
    Write-Output ''
    Write-Output 'Background: see PR #225 (F3, strict seed-children) and'
    Write-Output 'docs/decisions/architect-children-contract-audit.md.'
    Write-Output ''
}

exit 1
