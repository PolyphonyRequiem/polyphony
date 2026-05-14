<#
.SYNOPSIS
    CI lint — bans bare cross-branch git refs in workflow YAMLs and prompt MDs.

.DESCRIPTION
    Catches the AB#3157 bug class: in the bare-repo + per-run worktree model
    (AB#3085), all worktrees share `.git/refs`. When `gh pr merge` updates a
    branch on the remote, only `refs/remotes/origin/<branch>` is touched —
    the local `refs/heads/<branch>` stays at the SHA the worktree was created
    with. Any agent prompt or workflow script that issues

        git log --oneline {{ feature_branch }}..mg/{{ root_id }}_{{ mg_path }}

    resolves both names against the stale local ref-store and observes the
    stale state — the AB#3127 dogfood Run #3 reviewer emitted six
    consecutive false-positive `empty_merge_group_structural_violation`
    cycles for exactly this reason.

    PR #358 tactically patched two sites (`scope_reviewer` +
    `user_acceptance` in `implement-merge-group.yaml`) and added explicit
    `fetch_for_*` script nodes upstream of each. The PR body's audit named
    two more candidates documented-safe-for-now (`feature-pr.yaml`
    `remediation_planner` and `implement-merge-group.yaml`
    `primary_reviewer`). This lint catches the whole class mechanically so
    new sites can't regress in.

    Detection rule:
        Scope: `.conductor/registry/{workflows,prompts}/**/*.{yaml,md}`.
        Treats files as plain text (does NOT parse YAML structure) so that
        script bodies, agent prompts, and human-gate prompt MDs are all
        scanned uniformly.

        Walks each occurrence of one of the flagged git verbs (`log`,
        `diff`, `merge-base`, `rev-list`, `rev-parse`, `cherry`,
        `cherry-pick`, `range-diff`) and inspects its argument tail. The
        tail extends across continuation lines until the next blank line
        or the next git verb, so multi-line markdown wrapping (e.g.
        ``\`git diff\n  A..B\``) is handled.

        Within a tail:

        1. Range operands. Any `<left>..<right>` or `<left>...<right>`
           where either side is not prefixed with `origin/`,
           `refs/remotes/`, `refs/tags/`, `tags/`, is not in the bare
           allow-list (`HEAD`, `FETCH_HEAD`, `ORIG_HEAD`, `MERGE_HEAD`,
           `main`, `master`, with `~N`/`^N` suffixes), and is not a
           PowerShell variable (`$x`) or a SHA-like blob, is flagged.

        2. Single-arg slashed refs. Any non-flag, ref-shaped token
           (word/word, template fragments allowed) that is not in the
           allow-list is flagged. Catches `git rev-parse mg/3127_pg-3127`
           etc. Pure punctuation and prose noise are filtered out.

    Allow-list of commands NOT scanned:
        - `git fetch …` — bare names are correct here; verb omitted from
          the flagged set.
        - `git update-ref refs/heads/X refs/remotes/origin/X` — explicit
          ref-store ops; verb omitted from the flagged set.

    Whitelist marker:
        `# bare-ref-ok: <reason>` on the same line as the offending
        command, or the immediately preceding non-empty line, suppresses
        the violation. Use sparingly — only when there is a genuine
        reason (e.g. reading the local branch the script ITSELF is on,
        which the worktree guarantees fresh).

    Output: `plain` (default) emits a human-readable report. `github`
    emits GitHub Actions annotations (`::error file=…,line=…::message`).

    Exit codes: 0 clean, 1 violations found, 2 configuration error.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string[]]$Roots = @(
        '.conductor/registry/workflows',
        '.conductor/registry/prompts'
    ),

    [ValidateSet('plain', 'github')]
    [string]$Format = 'plain'
)

$ErrorActionPreference = 'Stop'

# ── Constants ────────────────────────────────────────────────────────────
$script:GitVerbPattern = '\bgit\s+(log|diff|merge-base|rev-list|rev-parse|cherry|cherry-pick|range-diff)\b'
$script:GitVerbRegex = [regex]::new($script:GitVerbPattern)
$script:WhitelistPattern = '#\s*bare-ref-ok\s*:'

# Operand fragment: a Jinja template (`{{ … }}`) or a path-component
# fragment — `.` is excluded from the bare alternative so the regex
# never eats the `..` separator. Concatenations like `mg/{{ a }}_{{ b }}`
# match by the `+` quantifier on the alternation.
# Operand fragment: a Jinja template (`{{ … }}`) or a path-component
# fragment — `.` is excluded from the bare alternative so the regex
# never eats the `..` separator. Concatenations like `mg/{{ a }}_{{ b }}`
# match by the `+` quantifier on the alternation. `~` and `^` are
# included so HEAD~N / HEAD^N range operands match. Atomic group
# prevents catastrophic backtracking when scanning long tails that
# don't contain a range operator.
$script:OperandFragment = '(?>(?:\{\{[^}]{1,200}\}\}|[A-Za-z0-9_/~^\-]+)+)'
$script:RangePattern = '(' + $script:OperandFragment + ')(\.\.\.?)(' + $script:OperandFragment + ')'
$script:RangeRegex = [regex]::new($script:RangePattern, [System.Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(5))
$script:BlankLineRegex = [regex]::new("(`r?`n)[ `t]*(`r?`n)")

# Single-arg ref-shape: word/word with template fragments allowed, no
# whitespace, no surrounding punctuation. `/` alone, `foo/`, `/bar`,
# `foo / bar` — all rejected.
$script:RefShapePattern = '^(?:\{\{[^}]+\}\}|[A-Za-z0-9_\-])+(?:/(?:\{\{[^}]+\}\}|[A-Za-z0-9_\-])+)+$'

$script:AllowedBareRefs = @(
    'HEAD', 'FETCH_HEAD', 'ORIG_HEAD', 'MERGE_HEAD', 'CHERRY_PICK_HEAD',
    'main', 'master'
)

# ── Helpers ──────────────────────────────────────────────────────────────
function Test-RefAllowed {
    param([string]$Ref)

    if ([string]::IsNullOrWhiteSpace($Ref)) { return $true }

    # Strip ~N / ^N suffixes (HEAD~10, HEAD^2, etc.).
    $base = $Ref -replace '[~^].*$', ''

    if ($base -in $script:AllowedBareRefs) { return $true }
    if ($base -match '^origin/') { return $true }
    if ($base -match '^refs/remotes/') { return $true }
    if ($base -match '^refs/tags/') { return $true }
    if ($base -match '^tags/') { return $true }
    # PowerShell variable reference inside a script body (e.g. $localRef).
    if ($base -match '^\$') { return $true }
    # SHA-like (7-40 hex chars).
    if ($base -match '^[0-9a-f]{7,40}$') { return $true }

    return $false
}

function Get-LineNumberAtOffset {
    param([string]$Content, [int]$Offset)
    if ($Offset -le 0) { return 1 }
    $head = $Content.Substring(0, [Math]::Min($Offset, $Content.Length))
    return ([regex]::Matches($head, "`n")).Count + 1
}

function Get-LineForOffset {
    param([string[]]$Lines, [int]$LineNumber)
    $idx = $LineNumber - 1
    if ($idx -lt 0 -or $idx -ge $Lines.Count) { return '' }
    return $Lines[$idx]
}

function Get-CleanArgTokens {
    # Tokens after the git verb. Strip trailing punctuation that PowerShell /
    # shell / markdown syntax tends to leave attached (parens, semicolons,
    # backticks, commas, redirect operators).
    param([string]$Tail)

    $tokens = $Tail -split '\s+' | Where-Object { $_ -ne '' }
    foreach ($t in $tokens) {
        $clean = $t -replace '[`;,)\]\}>]+$', ''
        $clean = $clean -replace '^[`(\[\{<]+', ''
        if ($clean -ne '') { $clean }
    }
}

# ── Main ─────────────────────────────────────────────────────────────────
$allViolations = @()

foreach ($rel in $Roots) {
    $rootPath = Join-Path $RepoRoot $rel
    if (-not (Test-Path $rootPath)) { continue }

    $files = Get-ChildItem -LiteralPath $rootPath -Recurse -File `
                           -Include '*.yaml', '*.yml', '*.md' `
                           -ErrorAction SilentlyContinue
    foreach ($file in $files) {
        $relPath = ([System.IO.Path]::GetRelativePath($RepoRoot, $file.FullName)) `
                    -replace '\\', '/'

        $content = [System.IO.File]::ReadAllText($file.FullName)
        if ([string]::IsNullOrEmpty($content)) { continue }
        $lines = $content -split "`r?`n"

        $verbMatches = [regex]::Matches($content, $script:GitVerbPattern)
        foreach ($vm in $verbMatches) {
            $verbLine = Get-LineNumberAtOffset -Content $content -Offset $vm.Index
            $verbLineText = Get-LineForOffset -Lines $lines -LineNumber $verbLine

            # Whitelist gates: same line OR the immediately preceding
            # (non-empty would be ideal but adjacent suffices for the
            # common "comment above the command" pattern).
            if ($verbLineText -match $script:WhitelistPattern) { continue }
            $prevLineText = Get-LineForOffset -Lines $lines -LineNumber ($verbLine - 1)
            if ($prevLineText -match $script:WhitelistPattern) { continue }

            # Build the command tail: from the end of the verb match to the
            # next blank line or the next git verb, whichever comes first.
            # This stitches multi-line markdown (e.g. `\`git diff\n  A..B\``)
            # without losing line-number fidelity for reporting.
            $startPos = $vm.Index + $vm.Length
            $tailEnd = $content.Length

            $blank = $script:BlankLineRegex.Match($content, $startPos)
            if ($blank.Success) { $tailEnd = $blank.Index }

            $nextVerb = $script:GitVerbRegex.Match($content, $startPos)
            if ($nextVerb.Success -and $nextVerb.Index -lt $tailEnd) {
                $tailEnd = $nextVerb.Index
            }

            $tail = $content.Substring($startPos, $tailEnd - $startPos)

            # 1. Range operands.
            $rangeOperandsSeen = New-Object System.Collections.Generic.List[string]
            $rangeMatches = $script:RangeRegex.Matches($tail)
            foreach ($m in $rangeMatches) {
                $left = $m.Groups[1].Value
                $op = $m.Groups[2].Value
                $right = $m.Groups[3].Value

                $rangeOperandsSeen.Add($left) | Out-Null
                $rangeOperandsSeen.Add($right) | Out-Null

                $leftBad = -not (Test-RefAllowed $left)
                $rightBad = -not (Test-RefAllowed $right)
                if (-not ($leftBad -or $rightBad)) { continue }

                $absoluteOffset = $startPos + $m.Index
                $matchLine = Get-LineNumberAtOffset -Content $content -Offset $absoluteOffset
                $matchLineText = Get-LineForOffset -Lines $lines -LineNumber $matchLine

                $bad = @()
                if ($leftBad) { $bad += $left }
                if ($rightBad) { $bad += $right }

                $allViolations += @{
                    File    = $relPath
                    Line    = $matchLine
                    Kind    = 'range'
                    Operand = ($bad -join ', ')
                    Detail  = "$left$op$right"
                    Context = $matchLineText.Trim()
                }
            }

            # 2. Single-arg slashed refs.
            #    Restricted to the verb's OWN line so multi-line tail
            #    stitching (used for range continuations) doesn't leak
            #    prose tokens like `file/line` from the next line into
            #    the single-arg pass. Genuine single-arg ref-store ops
            #    like `git rev-parse mg/3127_pg-3127` always sit on the
            #    verb's line.
            $verbLineTailStart = $vm.Index + $vm.Length
            $verbLineEnd = $content.IndexOf("`n", $verbLineTailStart)
            if ($verbLineEnd -lt 0) { $verbLineEnd = $content.Length }
            $verbLineTail = $content.Substring($verbLineTailStart, $verbLineEnd - $verbLineTailStart)

            $tokenMatches = [regex]::Matches($verbLineTail, '\S+')
            foreach ($tm in $tokenMatches) {
                $rawTok = $tm.Value
                $tok = $rawTok -replace '[`;,)\]\}>]+$', ''
                $tok = $tok -replace '^[`(\[\{<]+', ''

                if ($tok -eq '') { continue }
                if ($tok.StartsWith('-')) { continue }
                if ($tok -match '\.\.') { continue }
                if ($tok -notmatch '/') { continue }
                if ($tok -notmatch $script:RefShapePattern) { continue }
                if (Test-RefAllowed $tok) { continue }

                # Skip if this token is a fragment of a range operand the
                # range pass already inspected (e.g. whitespace-split
                # `mg/{{` from a `mg/{{ ... }}..HEAD` range).
                $isFragment = $false
                foreach ($op in $rangeOperandsSeen) {
                    if ($op.Contains($tok)) { $isFragment = $true; break }
                }
                if ($isFragment) { continue }

                $absoluteOffset = $verbLineTailStart + $tm.Index
                $matchLine = Get-LineNumberAtOffset -Content $content -Offset $absoluteOffset
                $matchLineText = Get-LineForOffset -Lines $lines -LineNumber $matchLine

                $allViolations += @{
                    File    = $relPath
                    Line    = $matchLine
                    Kind    = 'single'
                    Operand = $tok
                    Detail  = $tok
                    Context = $matchLineText.Trim()
                }
            }
        }
    }
}

# Dedupe (same file/line/kind/operand can fire from overlapping verb-tail
# spans when commands sit close together).
$deduped = @()
$seen = @{}
foreach ($v in $allViolations) {
    $key = "$($v.File)|$($v.Line)|$($v.Kind)|$($v.Operand)"
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    $deduped += $v
}
$allViolations = $deduped

if ($allViolations.Count -eq 0) { exit 0 }

$baseMessage = 'bare cross-branch git ref — prefix with `origin/` (and ensure a `git fetch origin <branch>` runs upstream in the workflow). See AB#3157 / PR #358 for the bug class.'

if ($Format -eq 'github') {
    foreach ($v in $allViolations) {
        $msg = "$baseMessage Offending operand(s): $($v.Operand). If this is intentional (e.g. reading the worktree's own branch), add `# bare-ref-ok: <reason>` on the same or preceding line."
        Write-Output "::error file=$($v.File),line=$($v.Line)::$msg"
    }
} else {
    Write-Output ''
    Write-Output 'lint-bare-cross-branch-refs: violations found'
    Write-Output '---------------------------------------------'
    foreach ($v in $allViolations) {
        Write-Output "  $($v.File):$($v.Line) — $($v.Kind): $($v.Operand)"
        Write-Output "    in: $($v.Context)"
    }
    Write-Output ''
    Write-Output $baseMessage
    Write-Output ''
    Write-Output 'Whitelist a genuine local-only ref with `# bare-ref-ok: <reason>` on the same or preceding line.'
    Write-Output ''
}

exit 1
