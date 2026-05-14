<#
.SYNOPSIS
    CI lint — bans the bareword Jinja boolean trap in `command: pwsh` script
    bodies inside conductor workflow YAMLs.

.DESCRIPTION
    Rooted in AB#3156 Bug 1 (PR #354 / commit e48a31d). A pwsh script in
    plan-level.yaml had:

        $hasTopics = {{ (architect.output.research_needs.topics
                         | default([]) | length > 0) | string | lower }}

    Jinja renders `true` / `false` (Python bool → lowercase string). PowerShell
    parses bareword `true` as a cmdlet name, finds nothing, errors silently —
    `$hasTopics` ends up `$null`. Serialized to JSON as `null`; downstream
    Jinja then evaluates `null != false` and routes to the wrong branch.
    Killed an AB#3127 dogfood relaunch.

    The fix is to quote the rendered token and string-compare it:

        $hasTopics = '{{ (...) | string | lower }}' -eq 'true'

    The result is `[bool]` regardless of what Jinja emits. Same pattern works
    for any value (string, int, bool) where the safe form is to wrap the
    Jinja render in a string literal: `'{{ ... }}'`, `"{{ ... }}"`, or
    `@('{{ a }}', '{{ b }}')`.

    This lint scans every `.conductor/registry/workflows/*.yaml`. For every
    `command: pwsh` agent it walks the `args:` list, splits each string arg
    into lines, and flags any line containing:

        $<var> = {{ ... }}                      # bareword Jinja render

    The intentional "bareword Jinja render" case (e.g. an integer rendered
    directly into an arithmetic expression) can be opted out via a comment
    marker on the same or preceding line:

        # bareword-ok: integer render — pr_number is always int
        $prNumber = {{ poll_status.output.pr_number }}

    Use sparingly — defense-in-depth says cast and let the cast fail loudly
    instead.

.PARAMETER WorkflowsDir
    Directory of workflow YAMLs to scan. Defaults to
    `<repo>/.conductor/registry/workflows`.

.PARAMETER Format
    Output format: `human` (default) or `github` (Actions annotations).

    Exits 0 if clean, 1 if any violations are found, 2 on configuration error.
#>
[CmdletBinding()]
param(
    [string] $WorkflowsDir,
    [ValidateSet('human', 'github')]
    [string] $Format = 'human'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $WorkflowsDir) {
    $WorkflowsDir = Join-Path $repoRoot '.conductor/registry/workflows'
}

if (-not (Test-Path $WorkflowsDir)) {
    Write-Host "SKIP: workflows dir not found: $WorkflowsDir" -ForegroundColor Yellow
    exit 0
}

# ── Module dependency ────────────────────────────────────────────────────
if (-not (Get-Module -ListAvailable -Name powershell-yaml)) {
    Write-Host "FATAL: the powershell-yaml module is required by lint-pwsh-jinja-bareword.ps1." -ForegroundColor Red
    Write-Host "Install with: Install-Module -Name powershell-yaml -Force -SkipPublisherCheck -Scope CurrentUser" -ForegroundColor Cyan
    exit 2
}
Import-Module powershell-yaml -ErrorAction Stop

# ── Detection regex ──────────────────────────────────────────────────────
# Match `$<var> = {{ ... }}` where the `{{` is NOT preceded by a quote
# character. The negative form is always `=\s*\{\{`; the safe form has
# `=\s*['"]\{\{` (or any other non-whitespace char between `=` and `{{`),
# which `\s*\{\{` cannot match.
$BarewordPattern = '\$\w+\s*=\s*\{\{[^}]+\}\}'
$WhitelistPattern = '#\s*bareword-ok\s*:'

# ── Helpers ──────────────────────────────────────────────────────────────
function Split-IntoLines {
    <#
        Split a script body into lines, normalizing CRLF. Each returned
        element is one logical script line as the YAML literal-block scalar
        preserved it. We do NOT split on `;` — the whitelist marker check
        must be able to look at the previous *line*, and a `;` inside a
        comment must not start a new "statement" for whitelist purposes.
        Multiple violations on the same line are still found because the
        regex scan uses [regex]::Matches per line.

        Always returns an array (the leading `,` defeats PowerShell's
        single-element unwrap, which would otherwise turn the result into a
        bare string and make `$lines.Count` report the string length).
    #>
    param([string] $Body)
    if ([string]::IsNullOrEmpty($Body)) { return ,@() }
    $arr = ($Body -replace "`r`n", "`n") -split "`n"
    return ,$arr
}

function Find-FileLine {
    <#
        Search a file's raw line array for the first line (at or after
        $StartIndex) whose trimmed text contains the trimmed needle.
        Returns the 1-based line number, or 0 if not found. Used to map
        a violation back to its source line in the YAML file.
    #>
    param(
        [string[]] $Lines,
        [int] $StartIndex,
        [string] $Needle
    )
    $needleTrim = $Needle.Trim()
    if ([string]::IsNullOrEmpty($needleTrim)) { return 0 }
    for ($i = $StartIndex; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim().Contains($needleTrim)) {
            return $i + 1
        }
    }
    return 0
}

function Find-AgentLine {
    param([string[]] $Lines, [string] $AgentName)
    $escaped = [regex]::Escape($AgentName)
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match "^\s*-\s+name:\s+$escaped\s*$") {
            return $i
        }
    }
    return 0
}

# ── Main scan ────────────────────────────────────────────────────────────
$yamlFiles = @(Get-ChildItem -Path $WorkflowsDir -Filter '*.yaml' -File)
$violations = @()

foreach ($file in $yamlFiles) {
    $rawText = Get-Content -LiteralPath $file.FullName -Raw
    $rawLines = @(Get-Content -LiteralPath $file.FullName)

    try {
        $yaml = ConvertFrom-Yaml $rawText
    } catch {
        Write-Host "FATAL: failed to parse $($file.Name) as YAML: $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }

    if ($null -eq $yaml -or -not $yaml.ContainsKey('agents')) { continue }

    foreach ($agent in $yaml['agents']) {
        if ($null -eq $agent) { continue }
        if (-not $agent.ContainsKey('command')) { continue }
        if ([string]$agent['command'] -ne 'pwsh') { continue }
        if (-not $agent.ContainsKey('args')) { continue }

        $agentName = if ($agent.ContainsKey('name')) { [string]$agent['name'] } else { '<unnamed>' }
        $agentLineIdx = Find-AgentLine -Lines $rawLines -AgentName $agentName

        foreach ($arg in $agent['args']) {
            if ($null -eq $arg) { continue }
            if ($arg -isnot [string]) { continue }

            # Tracks the raw-arg lines so we can spot a `# bareword-ok:`
            # marker on the immediately preceding line.
            $lines = Split-IntoLines -Body $arg
            for ($k = 0; $k -lt $lines.Count; $k++) {
                $line = $lines[$k]
                if ($line -match $WhitelistPattern) { continue }
                if ($k -gt 0 -and $lines[$k - 1] -match $WhitelistPattern) { continue }

                $matchList = [regex]::Matches($line, $BarewordPattern)
                foreach ($m in $matchList) {
                    # `=\s*\{\{` matches both `= {{` (unsafe) and we need to
                    # confirm the char before `{{` (after the `=`) is whitespace,
                    # not a quote — defensive check in case the [^}]+ inside
                    # absorbed something weird.
                    $matchText = $m.Value
                    $bracesIdx = $matchText.IndexOf('{{')
                    if ($bracesIdx -gt 0) {
                        $charBefore = $matchText[$bracesIdx - 1]
                        if ($charBefore -eq "'" -or $charBefore -eq '"') { continue }
                    }

                    $fileLine = Find-FileLine -Lines $rawLines -StartIndex $agentLineIdx -Needle $matchText
                    $violations += [PSCustomObject]@{
                        File      = $file.Name
                        Line      = $fileLine
                        Agent     = $agentName
                        Snippet   = $matchText.Trim()
                    }
                }
            }
        }
    }
}

# ── Report ───────────────────────────────────────────────────────────────
if ($violations.Count -eq 0) {
    Write-Host "[OK] lint-pwsh-jinja-bareword passed ($($yamlFiles.Count) workflow(s) scanned)" -ForegroundColor Green
    exit 0
}

$message = "PowerShell bareword Jinja render — Jinja bool/string `true`/`false` parses as a cmdlet name and silently nulls the variable. Wrap in quotes and string-compare: " + "``" + "`$x = '{{ ... }}' -eq 'true'" + "``" + " (or for non-bool: " + "``" + "`$x = '{{ ... }}'" + "``" + " / " + "``" + "[int]'{{ ... }}'" + "``" + "). See AB#3156 / PR #354."

if ($Format -eq 'github') {
    foreach ($v in $violations) {
        $msg = "lint-pwsh-jinja-bareword [$($v.Agent)] $($v.Snippet) — $message"
        # GitHub annotations are single-line.
        $msg = $msg -replace "`r?`n", ' '
        $relPath = ".conductor/registry/workflows/$($v.File)"
        if ($v.Line -gt 0) {
            Write-Output "::error file=$relPath,line=$($v.Line)::$msg"
        } else {
            Write-Output "::error file=$relPath::$msg"
        }
    }
} else {
    Write-Host "`n[FAIL] lint-pwsh-jinja-bareword failed ($($violations.Count) violation(s)):`n" -ForegroundColor Red
    foreach ($v in $violations) {
        $loc = if ($v.Line -gt 0) { "$($v.File):$($v.Line)" } else { "$($v.File):?" }
        Write-Host "  $loc  [agent: $($v.Agent)]" -ForegroundColor Red
        Write-Host "    $($v.Snippet)" -ForegroundColor DarkGray
    }
    Write-Host "`nFix: $message" -ForegroundColor Yellow
    Write-Host "Or, for an intentional bareword render (rare — e.g. integer in arithmetic), add" -ForegroundColor Yellow
    Write-Host "  # bareword-ok: <reason>" -ForegroundColor Yellow
    Write-Host "on the line above the assignment.`n" -ForegroundColor Yellow
}

exit 1
