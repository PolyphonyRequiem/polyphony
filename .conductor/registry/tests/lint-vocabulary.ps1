<#
.SYNOPSIS
    CI lint — enforces the authoritative glossary's forbidden terms.

.DESCRIPTION
    Reads the top blockquoted "Forbidden terms" section from docs/glossary.md
    and scans Polyphony source, scripts, docs, and tests for forbidden
    vocabulary. The glossary is authoritative: the lint derives its forbidden
    term list and guidance dynamically from that file rather than hard-coding
    the rules here.

    Markdown files are scanned line-by-line, except inside fenced code blocks
    tagged `text`, `console`, `output`, or `diff`; those fences represent
    literal historical output rather than authored vocabulary.

    The deferred legacy specs at docs/proposals/polyphony-journal.md and
    docs/proposals/conductor-failure-model.md emit a single warning per file by
    default. Pass -Strict to fail on those deferred-spec warnings too.

    Exit codes:
      0 = clean (or deferred warnings only without -Strict)
      1 = forbidden-term violations found
      2 = configuration error (missing/invalid glossary, invalid root, etc.)

    # TODO: wire into ci.yml after the mechanical rename pass lands

.PARAMETER Root
    Repository root to scan. Defaults to discovery via `git rev-parse
    --show-toplevel` from the script directory.

.PARAMETER OutputFormat
    Output format: `human` (default) or `json`.

.PARAMETER Strict
    Fail on deferred-spec warnings from the two grandfathered proposal docs.

.OUTPUTS
    Human-readable findings or a JSON object describing violations, warnings,
    scanned files, forbidden terms, and elapsed time.
#>
[CmdletBinding()]
param(
    [string]$Root,

    [ValidateSet('human', 'json')]
    [string]$OutputFormat = 'human',

    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$regexOptions = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
$deferredSpecFiles = @(
    'docs/proposals/polyphony-journal.md'
)
$skippedFenceLanguages = @('text', 'console', 'output', 'diff')

function Write-ConfigurationError {
    param(
        [Parameter(Mandatory)] [string]$Message,
        [string]$GlossaryPath,
        [string]$RootPath
    )

    $payload = [ordered]@{
        tool          = 'lint-vocabulary'
        status        = 'configuration_error'
        error         = $Message
        glossary_path = $GlossaryPath
        root          = $RootPath
    }

    [Console]::Error.WriteLine("lint-vocabulary: configuration error: $Message")
    if ($OutputFormat -eq 'json') {
        $payload | ConvertTo-Json -Depth 6
    }

    exit 2
}

function Get-DisplayPath {
    param(
        [Parameter(Mandatory)] [string]$BasePath,
        [Parameter(Mandatory)] [string]$FullPath
    )

    return ([System.IO.Path]::GetRelativePath($BasePath, $FullPath)) -replace '\\', '/'
}

function Get-RepositoryRoot {
    param([string]$ProvidedRoot)

    if (-not [string]::IsNullOrWhiteSpace($ProvidedRoot)) {
        try {
            return (Resolve-Path -LiteralPath $ProvidedRoot).Path
        } catch {
            throw "Root path '$ProvidedRoot' does not exist."
        }
    }

    $gitOutput = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null
    $gitExitCode = $LASTEXITCODE
    $gitRoot = $gitOutput | Select-Object -First 1
    if ($gitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot)) {
        throw "Root not provided and 'git rev-parse --show-toplevel' failed from '$PSScriptRoot'. Pass -Root <repo> explicitly."
    }

    return $gitRoot.Trim()
}

function Get-ReplacementHint {
    param([Parameter(Mandatory)] [string]$Guidance)

    if ($Guidance -match '\buse\s+`([^`]+)`') {
        return $Matches[1]
    }

    $quotedValues = [regex]::Matches($Guidance, '`([^`]+)`')
    if ($quotedValues.Count -gt 0) {
        return $quotedValues[0].Groups[1].Value
    }

    return $Guidance.Trim().TrimEnd('.')
}

function New-ForbiddenTermSpec {
    param(
        [Parameter(Mandatory)] [string]$Term,
        [Parameter(Mandatory)] [string]$Guidance,
        [Parameter(Mandatory)] [string]$Replacement
    )

    if ($Term.StartsWith('*_')) {
        # Token-suffix: `*_dispatch` matches `<word>_dispatch` only at end of an
        # identifier — i.e. preceded by [A-Za-z0-9] and not followed by another
        # [A-Za-z0-9_]. This prevents false positives like `items_dispatched_count`
        # (where `_dispatch` is mid-identifier, not a true suffix).
        $literalSuffix = $Term.Substring(1)
        $escaped = [regex]::Escape($literalSuffix)
        $pattern = "(?<=[A-Za-z0-9])$escaped(?![A-Za-z0-9_])"
        $regex = [regex]::new($pattern, $regexOptions)
        $kind = 'token-suffix'
    } elseif ($Term.EndsWith('_*')) {
        # Token-prefix snake_case: `primary_*` / `terminal_*` matches
        # `<prefix>_<word>` only at start of an identifier — preceded by
        # non-[A-Za-z0-9_] (or start of line) and immediately followed by [A-Za-z].
        # Case-insensitive: also catches `Primary_`, `PRIMARY_`, `Terminal_`.
        # PascalCase compound forms (e.g. `PrimaryId`, `TerminalState`) are
        # deliberately NOT caught here — see the dedicated `<Name>*` rule below.
        # Why split? The snake_case `terminal_*` is a workflow-node-name
        # convention and the snake_case form is the only locus to police; the
        # English-noun usage of "terminal" in lifecycle-event variables
        # (`$terminalKinds`, `$hasTerminalReady`) is semantically distinct and
        # must NOT be falsely flagged. Domain-noun renames that should ALSO
        # cover PascalCase (like `Primary` → `Root`) get an explicit second
        # bullet using `<Name>*` shape.
        $literalPrefix = $Term.Substring(0, $Term.Length - 1)
        $escaped = [regex]::Escape($literalPrefix)
        $pattern = "(?<![A-Za-z0-9_])$escaped(?=[A-Za-z])"
        $regex = [regex]::new($pattern, $regexOptions)
        $kind = 'token-prefix'
    } elseif ($Term -cmatch '^[A-Z][a-z]+\*$') {
        # PascalCase token-prefix: `Primary*` matches `Primary[A-Z]` exactly
        # at the start of an identifier. CASE-SENSITIVE — `primary` does NOT
        # match this rule (the lowercase form is covered by `primary_*` above).
        # The trailing uppercase requirement (`(?=[A-Z])`) means we catch
        # PascalCase compounds like `PrimaryId`, `PrimaryRouter` but skip the
        # bare PascalCase word `Primary` (which would have no uppercase boundary).
        $literalPrefix = $Term.Substring(0, $Term.Length - 1)  # e.g. 'Primary'
        $escaped = [regex]::Escape($literalPrefix)
        $pattern = "(?<![A-Za-z0-9_])$escaped(?=[A-Z])"
        $regex = [regex]::new($pattern, [System.Text.RegularExpressions.RegexOptions]::Compiled)
        $kind = 'pascal-prefix'
    } elseif ($Term.EndsWith('*')) {
        # Legacy literal-prefix (substring match) — kept for backwards compat
        # with any future bullet that uses bare `prefix*` (no underscore).
        $literalPrefix = $Term.Substring(0, $Term.Length - 1)
        $pattern = [regex]::Escape($literalPrefix)
        $regex = [regex]::new($pattern, $regexOptions)
        $kind = 'literal-prefix'
    } elseif ($Term.StartsWith('_') -or $Term.EndsWith('_')) {
        $pattern = [regex]::Escape($Term)
        $regex = [regex]::new($pattern, $regexOptions)
        $kind = 'literal'
    } else {
        $escaped = [regex]::Escape($Term)
        # Treat underscores and punctuation as identifier boundaries so
        # `apex_root` and `wave_dispatch_loop` are caught, but `apexes` is not.
        # For pure-alpha terms, also treat PascalCase transitions as a boundary
        # so tokens like `ApexId` are caught without matching `apexes`.
        if ($Term -match '^[A-Za-z]+$') {
            # Pure-alpha terms match at THREE positions:
            # 1. `(?<![A-Za-z0-9])apex(?![A-Za-z0-9])` — full-word boundary,
            #    matches snake_case and bare-word usage (`apex`, `apex_x`,
            #    `_apex`). Does not match `apexes`.
            # 2. `(?<![A-Za-z0-9])apex(?=(?-i:[A-Z]))` — PascalCase compound at
            #    identifier START (case-insensitive overall but the lookahead
            #    forces a literal uppercase boundary). Catches `ApexId`,
            #    `ApexDriver` but not `apexx`.
            # 3. `(?<=(?-i:[a-z0-9]))(?-i:Apex)(?![A-Za-z0-9])` — PascalCase
            #    compound at identifier SUFFIX. Catches `ResetApex`,
            #    `EdgeGraphWave`, `InitApex` (preceded by lowercase, then the
            #    capitalized form of the term at the end of an identifier).
            #    Without this alternative, suffix usages slip through the lint
            #    and the rename script can't see them either — a bug class
            #    discovered during the AB#3259 mechanical rename when 84
            #    `ResetApex`/`EdgeGraphWave`-style identifiers shipped past the
            #    original rule unchanged.
            $titleFirst = ([char]::ToUpper($Term[0])) + $Term.Substring(1).ToLowerInvariant()
            $titleEscaped = [regex]::Escape($titleFirst)
            $suffixPattern = "(?<=(?-i:[a-z0-9]))(?-i:$titleEscaped)(?![A-Za-z0-9])"
            $pattern = "(?<![A-Za-z0-9])$escaped(?![A-Za-z0-9])|(?<![A-Za-z0-9])$escaped(?=(?-i:[A-Z]))|$suffixPattern"
            $kind = 'identifier-boundary+pascal-case'
        } else {
            $pattern = "(?<![A-Za-z0-9])$escaped(?![A-Za-z0-9])"
            $kind = 'identifier-boundary'
        }
        $regex = [regex]::new($pattern, $regexOptions)
    }

    return [PSCustomObject]@{
        Term        = $Term
        Guidance    = $Guidance
        Replacement = $Replacement
        MatchKind   = $kind
        Regex       = $regex
    }
}

function Get-ForbiddenTermSpecs {
    param([Parameter(Mandatory)] [string]$GlossaryPath)

    if (-not (Test-Path -LiteralPath $GlossaryPath)) {
        throw "Expected '$GlossaryPath' to exist and contain a blockquoted '**Forbidden terms**' section near the top of the glossary."
    }

    $lines = Get-Content -LiteralPath $GlossaryPath
    $headerIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*>\s*\*\*Forbidden terms\*\*') {
            $headerIndex = $i
            break
        }
    }

    if ($headerIndex -lt 0) {
        throw "Expected '$GlossaryPath' to contain a blockquoted '**Forbidden terms**' heading with lines like '> - `apex` — use `root`'."
    }

    $entries = @()
    for ($i = $headerIndex + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -notmatch '^\s*>') {
            break
        }

        if ($line -match '^\s*>\s*$') {
            continue
        }

        if ($line -match '^\s*>\s*-\s*(.+)$') {
            $entries += [PSCustomObject]@{
                LineNumber = $i + 1
                Text       = $Matches[1]
            }
            continue
        }

        if ($entries.Count -gt 0) {
            break
        }
    }

    if ($entries.Count -eq 0) {
        throw "Expected one or more blockquoted bullet lines immediately after the '**Forbidden terms**' heading in '$GlossaryPath'."
    }

    $specs = @()
    foreach ($entry in $entries) {
        if ($entry.Text -notmatch '^(?<terms>.+?)(?:\s+—\s+|\s+--\s+)(?<guidance>.+)$') {
            throw (("Malformed forbidden-terms bullet at '{0}:{1}'. Expected '> - `term` — explanation'.") -f $GlossaryPath, $entry.LineNumber)
        }

        $termSource = $Matches['terms']
        $termHead = ($termSource -split '\s*\(')[0]
        $termMatches = [regex]::Matches($termHead, '`([^`]+)`')
        if ($termMatches.Count -eq 0) {
            throw (("Malformed forbidden-terms bullet at '{0}:{1}'. Expected at least one backticked term before the explanation.") -f $GlossaryPath, $entry.LineNumber)
        }

        $guidance = $Matches['guidance'].Trim()
        $replacement = Get-ReplacementHint -Guidance $guidance
        foreach ($termMatch in $termMatches) {
            $specs += New-ForbiddenTermSpec -Term $termMatch.Groups[1].Value -Guidance $guidance -Replacement $replacement
        }
    }

    if ($specs.Count -eq 0) {
        throw "No forbidden terms were parsed from '$GlossaryPath'. Expected at least one backticked term in the forbidden-terms block."
    }

    return $specs
}

function Get-PathDisposition {
    param([Parameter(Mandatory)] [string]$RelativePath)

    $path = $RelativePath -replace '\\', '/'

    if ($path -eq 'docs/glossary.md') { return 'skip' }
    if ($path -eq 'CHANGELOG.md') { return 'skip' }
    # The vocab lint and its Pester tests intentionally contain forbidden
    # terms (the lint as regex literals; the tests as detection fixtures).
    # Skipping prevents self-flags and prevents the mechanical rename pass
    # from rewriting the lint's own pattern strings or the test's assertion
    # fixtures — a real bug class hit during the AB#3259 rename when the
    # apply-rename pass corrupted both files.
    if ($path -eq '.conductor/registry/tests/lint-vocabulary.ps1') { return 'skip' }
    if ($path -eq '.conductor/registry/tests/lint-vocabulary.Tests.ps1') { return 'skip' }
    if ($path -like 'tests/fixtures/lint-vocabulary/*') { return 'skip' }
    if ($path -like 'tests/harness/*') { return 'skip' }
    if ($path -match '(^|/)(\.git|bin|obj|node_modules)(/|$)') { return 'skip' }
    if ($path -match '(^|/)\.polyphony-config(/|$)') { return 'skip' }
    if ($path -match '/runs/' -or $path -match '-runs/') { return 'skip' }
    if ($path -in $deferredSpecFiles) { return 'deferred' }

    return 'scan'
}

function Get-ScanFiles {
    param([Parameter(Mandatory)] [string]$RepoRoot)

    $allFiles = @()

    $workflowDir = Join-Path $RepoRoot '.conductor\registry\workflows'
    if (Test-Path -LiteralPath $workflowDir) {
        $allFiles += Get-ChildItem -LiteralPath $workflowDir -File -Filter '*.yaml' -ErrorAction SilentlyContinue
    }

    foreach ($relativeDir in @('.conductor\registry\scripts', 'scripts')) {
        $fullDir = Join-Path $RepoRoot $relativeDir
        if (-not (Test-Path -LiteralPath $fullDir)) { continue }
        $allFiles += Get-ChildItem -LiteralPath $fullDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.ps1' }
    }

    $polyphonySrcDir = Join-Path $RepoRoot 'src\Polyphony'
    if (Test-Path -LiteralPath $polyphonySrcDir) {
        $allFiles += Get-ChildItem -LiteralPath $polyphonySrcDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.cs', '.csproj' }
    }

    $docsDir = Join-Path $RepoRoot 'docs'
    if (Test-Path -LiteralPath $docsDir) {
        $allFiles += Get-ChildItem -LiteralPath $docsDir -Recurse -File -Filter '*.md' -ErrorAction SilentlyContinue
    }

    $testsDir = Join-Path $RepoRoot 'tests'
    if (Test-Path -LiteralPath $testsDir) {
        $allFiles += Get-ChildItem -LiteralPath $testsDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.ps1' }
    }

    $deduped = @{}
    foreach ($file in $allFiles) {
        $relativePath = Get-DisplayPath -BasePath $RepoRoot -FullPath $file.FullName
        if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }

        $disposition = Get-PathDisposition -RelativePath $relativePath
        if ($disposition -eq 'skip') { continue }
        if (-not $deduped.ContainsKey($relativePath)) {
            $deduped[$relativePath] = [PSCustomObject]@{
                FullPath     = $file.FullName
                RelativePath = $relativePath
                Disposition  = $disposition
            }
        }
    }

    return @($deduped.Values | Sort-Object RelativePath)
}

function Get-LineMatches {
    param(
        [Parameter(Mandatory)] [string]$RelativePath,
        [Parameter(Mandatory)] [AllowEmptyString()] [string[]]$Lines,
        [Parameter(Mandatory)] $TermSpecs
    )

    $findings = @()
    $isMarkdown = [System.IO.Path]::GetExtension($RelativePath) -eq '.md'
    $insideFence = $false
    $skipFenceContent = $false

    for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
        $line = $Lines[$lineIndex]

        if ($isMarkdown -and $line -match '^\s*```+\s*([A-Za-z0-9_-]+)?\s*$') {
            if (-not $insideFence) {
                $insideFence = $true
                $language = if ($Matches[1]) { $Matches[1].ToLowerInvariant() } else { '' }
                $skipFenceContent = $language -in $skippedFenceLanguages
            } else {
                $insideFence = $false
                $skipFenceContent = $false
            }
            continue
        }

        if ($isMarkdown -and $insideFence -and $skipFenceContent) {
            continue
        }

        foreach ($termSpec in $TermSpecs) {
            foreach ($regexMatch in $termSpec.Regex.Matches($line)) {
                $findings += [PSCustomObject]@{
                    File        = $RelativePath
                    Line        = $lineIndex + 1
                    Column      = $regexMatch.Index + 1
                    Length      = $regexMatch.Length
                    Match       = $regexMatch.Value
                    Term        = $termSpec.Term
                    Guidance    = $termSpec.Guidance
                    Replacement = $termSpec.Replacement
                }
            }
        }
    }

    if ($findings.Count -eq 0) {
        return @()
    }

    $selected = @()
    $seenStarts = @{}
    foreach ($finding in ($findings | Sort-Object Line, Column, @{ Expression = 'Length'; Descending = $true }, Term)) {
        $key = "$($finding.Line)|$($finding.Column)"
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        if ($seenStarts.ContainsKey($key)) { continue }
        $seenStarts[$key] = $true
        $selected += $finding
    }

    return @($selected)
}

try {
    $resolvedRoot = Get-RepositoryRoot -ProvidedRoot $Root
} catch {
    Write-ConfigurationError -Message $_.Exception.Message -RootPath $Root
}

$glossaryPath = Join-Path $resolvedRoot 'docs\glossary.md'

try {
    $termSpecs = Get-ForbiddenTermSpecs -GlossaryPath $glossaryPath
} catch {
    Write-ConfigurationError -Message $_.Exception.Message -GlossaryPath $glossaryPath -RootPath $resolvedRoot
}

$scanFiles = Get-ScanFiles -RepoRoot $resolvedRoot
$violations = @()
$warnings = @()
$termCounts = @{}

foreach ($file in $scanFiles) {
    $lines = Get-Content -LiteralPath $file.FullPath
    $lineFindings = Get-LineMatches -RelativePath $file.RelativePath -Lines $lines -TermSpecs $termSpecs
    if ($lineFindings.Count -eq 0) { continue }

    if ($file.Disposition -eq 'deferred') {
        $warnings += [PSCustomObject]@{
            File         = $file.RelativePath
            Message      = 'pending vocab pass per AB#3259'
            MatchCount   = $lineFindings.Count
            MatchedTerms = @($lineFindings | ForEach-Object Term | Sort-Object -Unique)
        }
        continue
    }

    foreach ($finding in $lineFindings) {
        $violations += $finding
        if ([string]::IsNullOrWhiteSpace($finding.Term)) { continue }
        if ($termCounts.ContainsKey($finding.Term)) {
            $termCounts[$finding.Term] += 1
        } else {
            $termCounts[$finding.Term] = 1
        }
    }
}

$stopwatch.Stop()
$effectiveFailure = ($violations.Count -gt 0) -or ($Strict -and $warnings.Count -gt 0)
$termCountObjects = @(
    foreach ($entry in $termCounts.GetEnumerator() | Sort-Object Key) {
        [PSCustomObject]@{
            term  = $entry.Key
            count = $entry.Value
        }
    }
)

if ($OutputFormat -eq 'json') {
    $payload = [ordered]@{
        tool             = 'lint-vocabulary'
        root             = $resolvedRoot
        glossary_path    = 'docs/glossary.md'
        strict           = [bool]$Strict
        scanned_files    = $scanFiles.Count
        violation_count  = $violations.Count
        warning_count    = $warnings.Count
        duration_ms      = [int][Math]::Round($stopwatch.Elapsed.TotalMilliseconds)
        forbidden_terms  = @($termSpecs | ForEach-Object {
            [PSCustomObject]@{
                term        = $_.Term
                replacement = $_.Replacement
                guidance    = $_.Guidance
                match_kind  = $_.MatchKind
            }
        })
        violations       = @($violations | ForEach-Object {
            [PSCustomObject]@{
                file        = $_.File
                line        = $_.Line
                column      = $_.Column
                match       = $_.Match
                term        = $_.Term
                replacement = $_.Replacement
                guidance    = $_.Guidance
                message     = "forbidden term '$($_.Term)' — $($_.Guidance) (AB#3259)"
            }
        })
        warnings         = @($warnings | ForEach-Object {
            [PSCustomObject]@{
                file          = $_.File
                message       = $_.Message
                matched_terms = $_.MatchedTerms
                match_count   = $_.MatchCount
                strict_failure = [bool]$Strict
            }
        })
        term_counts      = $termCountObjects
        exit_code        = if ($effectiveFailure) { 1 } else { 0 }
    }

    $payload | ConvertTo-Json -Depth 8
    if ($effectiveFailure) {
        exit 1
    }

    exit 0
}

foreach ($violation in $violations) {
    Write-Output "$($violation.File):$($violation.Line):$($violation.Column): forbidden term '$($violation.Term)' — $($violation.Guidance) (AB#3259)"
}

foreach ($warning in $warnings) {
    $severity = if ($Strict) { 'error' } else { 'warning' }
    Write-Output (("{0}: {1}: {2}") -f $warning.File, $severity, $warning.Message)
}

$state = if ($effectiveFailure) {
    'FAIL'
} elseif ($warnings.Count -gt 0) {
    'WARN'
} else {
    'OK'
}

Write-Output "[$state] lint-vocabulary: $($violations.Count) violation(s), $($warnings.Count) warning(s), $($scanFiles.Count) file(s) scanned in $([int][Math]::Round($stopwatch.Elapsed.TotalMilliseconds))ms"
if ($effectiveFailure) {
    exit 1
}

exit 0
