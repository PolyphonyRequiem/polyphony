<#
.SYNOPSIS
    CI lint — every RequirementKind is covered by lifecycle-router.ps1.

.DESCRIPTION
    The lifecycle-router script (.conductor/registry/scripts/lifecycle-router.ps1)
    classifies dispatchable items by intersecting their `next_kinds` with
    four named kind sets:

      $planKinds, $actionKinds, $implKinds, $terminalKinds

    Any RequirementKind value that is not a member of one of these four
    sets falls into `classification_indeterminate` — a silent drop that
    surfaces only when an item happens to have that kind ready. To catch
    drift before it bites in production, this lint asserts that every
    `public const string` value in `src/Polyphony/Sdlc/RequirementKind.cs`
    is named in one of the kind arrays.

    A kind may be deliberately excluded by adding a comment of the form
    `# router-skip: <reason>` near the kind reference (see escape hatch).

    Exit codes: 0 clean, 1 violations found, 2 configuration error.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of $PSScriptRoot.

.PARAMETER RouterPath
    Override path to lifecycle-router.ps1 (testing seam).

.PARAMETER RequirementKindPath
    Override path to RequirementKind.cs (testing seam).
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string]$RouterPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path '.conductor/registry/scripts/lifecycle-router.ps1'),

    [string]$RequirementKindPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'src/Polyphony/Sdlc/RequirementKind.cs')
)

$ErrorActionPreference = 'Stop'

# ── Parsers ──────────────────────────────────────────────────────────────

# Extracts every `public const string Name = "value";` pair from
# RequirementKind.cs. Returns a list of [PSCustomObject]@{ Name; Value }.
function script:Get-RequirementKindValues {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "RequirementKind source not found at: $Path"
    }

    $content = Get-Content -LiteralPath $Path -Raw
    $pattern = 'public\s+const\s+string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*"(?<value>[^"]+)"\s*;'
    $matches = [regex]::Matches($content, $pattern)

    $values = New-Object System.Collections.Generic.List[object]
    foreach ($m in $matches) {
        $values.Add([PSCustomObject]@{
            Name  = $m.Groups['name'].Value
            Value = $m.Groups['value'].Value
        })
    }
    return ,$values.ToArray()
}

# Extracts the union of values referenced in lifecycle-router.ps1's
# kind-set arrays ($planKinds, $actionKinds, $implKinds, $terminalKinds).
# Also collects "router-skip" annotations so a kind can be deliberately
# excluded with a justification comment.
function script:Get-RouterCoverage {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "lifecycle-router script not found at: $Path"
    }

    $content = Get-Content -LiteralPath $Path -Raw

    # Strip the comment-based help block so the docstring's kind references
    # (which are documentation, not classification) don't spuriously satisfy
    # coverage. The body's array assignments are authoritative.
    $body = $content -replace '(?ms)^<#.*?#>',''

    $kindArrayNames = @('planKinds', 'actionKinds', 'implKinds', 'terminalKinds')
    $covered = New-Object System.Collections.Generic.HashSet[string]
    $coverageMap = @{}

    foreach ($arr in $kindArrayNames) {
        # Match `$arrName = @( 'a', 'b' )` allowing multi-line and either
        # single or double quotes around each value.
        $pattern = '\$' + $arr + '\s*=\s*@\(\s*(?<body>[^)]*)\)'
        $m = [regex]::Match($body, $pattern)
        if (-not $m.Success) {
            continue
        }
        $arrayBody = $m.Groups['body'].Value
        $values = [regex]::Matches($arrayBody, "['""](?<v>[a-zA-Z_][a-zA-Z0-9_]*)['""]")
        foreach ($vm in $values) {
            $value = $vm.Groups['v'].Value
            $null = $covered.Add($value)
            if (-not $coverageMap.ContainsKey($value)) {
                $coverageMap[$value] = @()
            }
            $coverageMap[$value] += $arr
        }
    }

    # Collect explicit skip annotations: lines containing
    #   # router-skip: <kind-value> — <reason>
    # The value must be a bare token; the reason is free-text.
    $skipped = New-Object System.Collections.Generic.HashSet[string]
    $skipMap = @{}
    $skipPattern = '#\s*router-skip:\s*(?<v>[a-zA-Z_][a-zA-Z0-9_]*)\b\s*(?:[-—:]\s*(?<reason>.+))?'
    foreach ($sm in [regex]::Matches($content, $skipPattern)) {
        $value = $sm.Groups['v'].Value
        $null = $skipped.Add($value)
        $skipMap[$value] = $sm.Groups['reason'].Value.Trim()
    }

    return [PSCustomObject]@{
        Covered     = $covered
        CoverageMap = $coverageMap
        Skipped     = $skipped
        SkipMap     = $skipMap
    }
}

# ── Main ─────────────────────────────────────────────────────────────────

$kinds = $null
$coverage = $null
try {
    $kinds = script:Get-RequirementKindValues -Path $RequirementKindPath
    $coverage = script:Get-RouterCoverage -Path $RouterPath
} catch {
    Write-Host "lint configuration error: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

if ($kinds.Count -eq 0) {
    Write-Host "lint configuration error: no RequirementKind values parsed from $RequirementKindPath" -ForegroundColor Red
    exit 2
}

$violations = New-Object System.Collections.Generic.List[object]
foreach ($k in $kinds) {
    if ($coverage.Covered.Contains($k.Value)) { continue }
    if ($coverage.Skipped.Contains($k.Value)) { continue }
    $violations.Add([PSCustomObject]@{
        Name  = $k.Name
        Value = $k.Value
    })
}

if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) RequirementKind value(s) not covered by lifecycle-router.ps1" -ForegroundColor Red
    Write-Host ''
    foreach ($v in $violations) {
        Write-Host ("  RequirementKind.{0} = '{1}' is not in any of `$planKinds / `$actionKinds / `$implKinds / `$terminalKinds" -f $v.Name, $v.Value) -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'Fix one of:' -ForegroundColor Cyan
    Write-Host '  (a) Add the kind value to the appropriate array in lifecycle-router.ps1' -ForegroundColor Cyan
    Write-Host '      and a matching `when:` clause in apex-item-dispatch.yaml so the' -ForegroundColor Cyan
    Write-Host '      classifier returns a real lifecycle workflow rather than' -ForegroundColor Cyan
    Write-Host '      classification_indeterminate.' -ForegroundColor Cyan
    Write-Host '  (b) Annotate the script with a `# router-skip: <value> — <reason>`' -ForegroundColor Cyan
    Write-Host '      comment if the kind is deliberately excluded (e.g. computed-only,' -ForegroundColor Cyan
    Write-Host '      never reported as `next` by polyphony state next-ready).' -ForegroundColor Cyan
    exit 1
}

Write-Host ("PASS: All {0} RequirementKind value(s) covered by lifecycle-router.ps1" -f $kinds.Count) -ForegroundColor Green
foreach ($k in $kinds) {
    if ($coverage.Skipped.Contains($k.Value)) {
        $reason = $coverage.SkipMap[$k.Value]
        if ([string]::IsNullOrWhiteSpace($reason)) { $reason = '(no reason given)' }
        Write-Host ("  SKIP: {0} = '{1}' — {2}" -f $k.Name, $k.Value, $reason) -ForegroundColor DarkYellow
    } else {
        $arrays = ($coverage.CoverageMap[$k.Value] | Sort-Object -Unique) -join ', '
        Write-Host ("  OK:   {0} = '{1}' (in `${2})" -f $k.Name, $k.Value, $arrays) -ForegroundColor DarkGreen
    }
}
exit 0
