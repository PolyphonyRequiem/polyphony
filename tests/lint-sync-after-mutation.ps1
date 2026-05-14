<#
.SYNOPSIS
    CI lint — every C# command-verb method that mutates twig state via
    `SetStateAsync` / `PatchFieldsAsync` must flush via `SyncAsync` before
    returning to its caller.

.DESCRIPTION
    Sister-pattern enforcement for the AB#3126/27/28/29 family of bugs
    (the "stale-cache, never-pushed" failure mode). `twig`'s mutation
    methods stage changes in twig's local cache + pending queue but do
    NOT push to ADO. A separate process reading via
    `IWorkItemRepository.GetByIdAsync` reads from ADO and sees the
    pre-mutation state. Workflows stall or route wrong.

    Convention (documented at `.github/skills/polyphony-cli-developer/
    SKILL.md` § "State-mutation durability"): every method body that
    calls `SetStateAsync` / `PatchFieldsAsync` MUST call `SyncAsync(ct)`
    before any `return` statement reachable from that mutation.

    This lint scans `src/Polyphony/Commands/*.cs` (every C# file under
    that directory, including `*.partial.cs`-style splits) and asserts:

      For each method body B:
        For each mutation call M in B at line L_m:
          Either B contains `SyncAsync(` at line L_s where L_s > L_m and
          no `return` statement exists in B between L_m and L_s,
          OR B contains a `finally { … SyncAsync(…) … }` block that
          executes after M (mutation in try, sync in finally).

    Whitelist marker (sparingly): `// sync-after-mutation-ok: <reason>`
    on the line containing the mutation call OR the method-declaration
    line. Use for internal helpers whose flush is owned by an
    already-flushing caller.

.PARAMETER RepoRoot
    Repository root. Default: parent of this script.

.PARAMETER Format
    `plain` (default) — human-readable. `github` — `::error` annotations
    consumed by GitHub Actions.

.OUTPUTS
    Exit 0 — no violations.
    Exit 1 — at least one violation. Prints offending file/line/method
             and remediation guidance.
    Exit 2 — configuration error (missing scan directory, parse failure).
#>

[CmdletBinding()]
param(
    [string] $RepoRoot,

    [ValidateSet('plain', 'github')]
    [string] $Format = 'plain',

    [string] $ScanDir = 'src/Polyphony/Commands'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$scanRoot = Join-Path $RepoRoot $ScanDir

if (-not (Test-Path -LiteralPath $scanRoot)) {
    Write-Host "FATAL: scan directory not found: $scanRoot" -ForegroundColor Red
    exit 2
}

# ── Source sanitization ──────────────────────────────────────────────────
# Replace string literals and comments with same-length runs of spaces so
# byte/line offsets remain valid for downstream lookups, but no `{`, `}`,
# `(`, `)`, `;` or keyword inside a string/comment can confuse the
# tokenizer. Newlines are preserved verbatim so line numbers stay aligned.
function ConvertTo-SanitizedCSharp {
    param([string] $Source)

    $sb = [System.Text.StringBuilder]::new($Source.Length)
    $len = $Source.Length
    $i = 0

    while ($i -lt $len) {
        $c = $Source[$i]
        $next = if ($i + 1 -lt $len) { $Source[$i + 1] } else { [char]0 }

        # Line comment: // … to end of line
        if ($c -eq '/' -and $next -eq '/') {
            while ($i -lt $len -and $Source[$i] -ne "`n") {
                [void]$sb.Append(' ')
                $i++
            }
            continue
        }

        # Block comment: /* … */
        if ($c -eq '/' -and $next -eq '*') {
            [void]$sb.Append('  ')
            $i += 2
            while ($i -lt $len) {
                if ($i + 1 -lt $len -and $Source[$i] -eq '*' -and $Source[$i + 1] -eq '/') {
                    [void]$sb.Append('  ')
                    $i += 2
                    break
                }
                if ($Source[$i] -eq "`n") {
                    [void]$sb.Append("`n")
                } else {
                    [void]$sb.Append(' ')
                }
                $i++
            }
            continue
        }

        # Verbatim string: @"…""…"
        if ($c -eq '@' -and $next -eq '"') {
            [void]$sb.Append('  ')
            $i += 2
            while ($i -lt $len) {
                if ($Source[$i] -eq '"') {
                    if ($i + 1 -lt $len -and $Source[$i + 1] -eq '"') {
                        [void]$sb.Append('  ')
                        $i += 2
                        continue
                    }
                    [void]$sb.Append(' ')
                    $i++
                    break
                }
                if ($Source[$i] -eq "`n") {
                    [void]$sb.Append("`n")
                } else {
                    [void]$sb.Append(' ')
                }
                $i++
            }
            continue
        }

        # Interpolated raw verbatim string $@" … " / @$" … "
        if (($c -eq '$' -and $next -eq '@') -or ($c -eq '@' -and $next -eq '$')) {
            $third = if ($i + 2 -lt $len) { $Source[$i + 2] } else { [char]0 }
            if ($third -eq '"') {
                [void]$sb.Append('   ')
                $i += 3
                while ($i -lt $len) {
                    if ($Source[$i] -eq '"') {
                        if ($i + 1 -lt $len -and $Source[$i + 1] -eq '"') {
                            [void]$sb.Append('  ')
                            $i += 2
                            continue
                        }
                        [void]$sb.Append(' ')
                        $i++
                        break
                    }
                    if ($Source[$i] -eq "`n") {
                        [void]$sb.Append("`n")
                    } else {
                        [void]$sb.Append(' ')
                    }
                    $i++
                }
                continue
            }
        }

        # Interpolated string $"…" — interpolation can contain `{` / `}` so
        # we have to handle braces correctly. For lint purposes we replace
        # the entire literal (including interpolation expressions) with
        # spaces; this means `{Foo()}` inside an interpolated string won't
        # confuse the brace counter. Simple approach: track the literal
        # boundary and an inner `{` depth.
        if ($c -eq '$' -and $next -eq '"') {
            [void]$sb.Append('  ')
            $i += 2
            $interpDepth = 0
            while ($i -lt $len) {
                $cur = $Source[$i]
                if ($interpDepth -eq 0) {
                    if ($cur -eq '\') {
                        [void]$sb.Append('  ')
                        $i += 2
                        continue
                    }
                    if ($cur -eq '{') {
                        if ($i + 1 -lt $len -and $Source[$i + 1] -eq '{') {
                            [void]$sb.Append('  ')
                            $i += 2
                            continue
                        }
                        $interpDepth++
                        [void]$sb.Append(' ')
                        $i++
                        continue
                    }
                    if ($cur -eq '"') {
                        [void]$sb.Append(' ')
                        $i++
                        break
                    }
                } else {
                    if ($cur -eq '{') { $interpDepth++ }
                    elseif ($cur -eq '}') { $interpDepth-- }
                }
                if ($cur -eq "`n") { [void]$sb.Append("`n") } else { [void]$sb.Append(' ') }
                $i++
            }
            continue
        }

        # Regular string: "…" (with \" escape)
        if ($c -eq '"') {
            [void]$sb.Append(' ')
            $i++
            while ($i -lt $len) {
                $cur = $Source[$i]
                if ($cur -eq '\') {
                    [void]$sb.Append('  ')
                    $i += 2
                    continue
                }
                if ($cur -eq '"') {
                    [void]$sb.Append(' ')
                    $i++
                    break
                }
                if ($cur -eq "`n") {
                    [void]$sb.Append("`n")
                } else {
                    [void]$sb.Append(' ')
                }
                $i++
            }
            continue
        }

        # Char literal: '…' (with \' escape)
        if ($c -eq "'") {
            [void]$sb.Append(' ')
            $i++
            while ($i -lt $len) {
                $cur = $Source[$i]
                if ($cur -eq '\') {
                    [void]$sb.Append('  ')
                    $i += 2
                    continue
                }
                if ($cur -eq "'") {
                    [void]$sb.Append(' ')
                    $i++
                    break
                }
                [void]$sb.Append(' ')
                $i++
            }
            continue
        }

        [void]$sb.Append($c)
        $i++
    }

    return $sb.ToString()
}

# ── Method-body extraction ───────────────────────────────────────────────
# A method body for our purposes: a `{ … }` block whose declaration line
# (the text from the previous statement-terminator up to but not including
# the `{`) looks like a C# method or constructor signature: contains both
# `(` and `)`, ends with `)` (possibly followed by base-call /
# generic-constraints), and is NOT a control-flow statement (`if`,
# `while`, `for`, `foreach`, `switch`, `catch`, `using`, `lock`, `fixed`)
# or a lambda (`=> {`).
#
# Property accessors (`get { … }`, `set { … }`) lack parens entirely so
# they're filtered out by the `(` / `)` requirement. Object initializers
# (`new Foo() { … }`) are filtered by the `new` keyword check.
#
# Returns array of @{
#   DeclLine    = 1-based line of the start of the declaration
#   BraceLine   = 1-based line of the opening `{`
#   EndLine     = 1-based line of the matching `}`
#   StartIndex  = char index of opening `{` in sanitized source
#   EndIndex    = char index of matching `}` in sanitized source
# }
function Get-MethodBodies {
    param([string] $SanitizedSource)

    $bodies = [System.Collections.Generic.List[object]]::new()
    $len = $SanitizedSource.Length
    $i = 0
    $depth = 0
    $statementStart = 0  # char index of the start of the current "statement"

    # Pre-compute line numbers for each char index
    $lineOf = [int[]]::new($len + 1)
    $line = 1
    for ($k = 0; $k -lt $len; $k++) {
        $lineOf[$k] = $line
        if ($SanitizedSource[$k] -eq "`n") { $line++ }
    }
    $lineOf[$len] = $line

    while ($i -lt $len) {
        $c = $SanitizedSource[$i]

        if ($c -eq '{') {
            $declText = $SanitizedSource.Substring($statementStart, $i - $statementStart)
            $isMethod = Test-IsMethodDecl -DeclText $declText

            if ($isMethod) {
                # Find matching close brace
                $localDepth = 1
                $j = $i + 1
                while ($j -lt $len -and $localDepth -gt 0) {
                    $cj = $SanitizedSource[$j]
                    if ($cj -eq '{') { $localDepth++ }
                    elseif ($cj -eq '}') { $localDepth-- }
                    if ($localDepth -gt 0) { $j++ }
                }
                if ($j -ge $len) {
                    # Unbalanced — bail with what we have
                    return $bodies
                }

                # Locate the method's signature line: the line containing
                # the LAST `(` at paren-depth 0 in $declText. That `(` is
                # the start of the parameter list and sits on the same
                # line as the method name.
                $sigParenIdx = -1
                $parenDepth = 0
                for ($q = ($i - 1); $q -ge $statementStart; $q--) {
                    $cq = $SanitizedSource[$q]
                    if ($cq -eq ')') { $parenDepth++ }
                    elseif ($cq -eq '(') {
                        if ($parenDepth -eq 0) {
                            $sigParenIdx = $q
                            break
                        }
                        $parenDepth--
                    }
                }
                $declLineIdx = if ($sigParenIdx -ge 0) { $sigParenIdx } else { $statementStart }

                $bodies.Add(@{
                    DeclLine   = $lineOf[$declLineIdx]
                    BraceLine  = $lineOf[$i]
                    EndLine    = $lineOf[$j]
                    StartIndex = $i
                    EndIndex   = $j
                })
                # Skip past the body to avoid re-scanning nested method-shaped
                # blocks (e.g. nested local functions). Local functions inside
                # a method body OWN their own flush requirement, but by
                # consuming the parent body as a whole we get coverage of the
                # parent. Local functions calling SetStateAsync without a
                # nearby SyncAsync would still be flagged via the parent's
                # body containing those tokens.
                $i = $j + 1
                $statementStart = $i
                continue
            } else {
                # Non-method block — descend into it but don't open a new
                # method body. Brace depth tracking happens via $depth so
                # that the next statement's start is reset correctly when
                # this block closes.
                $depth++
                $i++
                $statementStart = $i
                continue
            }
        }

        if ($c -eq '}') {
            if ($depth -gt 0) { $depth-- }
            $i++
            $statementStart = $i
            continue
        }

        if ($c -eq ';') {
            $i++
            $statementStart = $i
            continue
        }

        # `=>` arrow: an expression-bodied member or lambda. Treat the
        # arrow as a statement boundary so a subsequent `{` is judged on
        # the post-arrow text only (which begins with `=>` markers we
        # detect in Test-IsMethodDecl).
        $i++
    }

    return $bodies
}

function Test-IsMethodDecl {
    param([string] $DeclText)

    $t = $DeclText.Trim()
    if (-not $t) { return $false }
    if ($t -notmatch '\(') { return $false }
    if ($t -notmatch '\)') { return $false }

    # Must end with ')' optionally followed by base-call / generic
    # constraints / `where T : …` clauses.
    $tail = $t
    # Strip trailing constraint-style content after the final ')'.
    $lastParen = $tail.LastIndexOf(')')
    if ($lastParen -lt 0) { return $false }
    $afterParen = $tail.Substring($lastParen + 1).Trim()

    if ($afterParen) {
        # Allowed: `: base(...)`, `: this(...)`, `where T : …`, attributes.
        $allowed = '^(:\s*(base|this)\s*\([^)]*\)|where\s+\w+\s*:.*)$'
        if ($afterParen -notmatch $allowed) {
            return $false
        }
    }

    # Lambda body: `=> {` — the text before `{` ends in `=>`.
    if ($t -match '=>\s*$') { return $false }

    # Anonymous function: `delegate (…) {` — the text before `{` ends in `)`.
    if ($t -match '\bdelegate\s*\([^)]*\)\s*$') { return $false }

    # Object initializer: `new Foo(…) {` — same shape but begins with `new`.
    # Heuristic: if a `new` keyword appears WITHOUT a leading return-type
    # token, assume initializer. Method declarations always have an access
    # modifier or return type token before any `new` (and `new` as a
    # modifier `public new int Foo()` is rare and would still match).
    if ($t -match '(?:^|[\s=,(])new\s+\w[\w<>.,\s\[\]]*\s*\([^)]*\)\s*$') {
        return $false
    }

    # Control-flow blocks: `if (…) {`, `while (…) {`, etc.
    # The decl text starts with the keyword (after stripping leading
    # whitespace and any preceding statement delimiter).
    $head = ($t -split '\s+', 2)[0]
    $controlFlow = @('if', 'else', 'while', 'for', 'foreach', 'switch',
                     'catch', 'try', 'finally', 'using', 'lock', 'fixed',
                     'do', 'return', 'throw', 'unsafe', 'checked', 'unchecked')
    if ($controlFlow -contains $head) { return $false }

    # `else if (…) {` — head is `else`, already filtered above. Good.

    # Tuple deconstruction: `(a, b) = …` — has `(` and `)` but is an
    # assignment, not a method.
    if ($t -match '=\s*[^=]') {
        # An `=` outside a parameter default would indicate assignment.
        # Parameter defaults are inside parens; check `=` outside parens.
        $depth = 0
        $hasOuterAssign = $false
        for ($k = 0; $k -lt $t.Length; $k++) {
            $ch = $t[$k]
            if ($ch -eq '(') { $depth++ }
            elseif ($ch -eq ')') { $depth-- }
            elseif ($ch -eq '=' -and $depth -eq 0) {
                # Skip `=>`, `==`, `>=`, `<=`, `!=`
                $prev = if ($k -gt 0) { $t[$k - 1] } else { ' ' }
                $nxt  = if ($k + 1 -lt $t.Length) { $t[$k + 1] } else { ' ' }
                if ($nxt -eq '>' -or $nxt -eq '=' -or $prev -in @('=','<','>','!','+','-','*','/','%','&','|','^')) {
                    continue
                }
                $hasOuterAssign = $true
                break
            }
        }
        if ($hasOuterAssign) { return $false }
    }

    # Type declaration with primary constructor: `class Foo(...) {` /
    # `record Foo(...) {` / `struct Foo(...) {`. These have parens but
    # are NOT method bodies — they open a TYPE body whose contents are
    # member declarations.
    if ($t -match '\b(class|record|struct|interface|enum)\s+\w') {
        return $false
    }

    return $true
}

# ── Rule application ─────────────────────────────────────────────────────
# Per-body checks. $Lines is the original (un-sanitized) source split by
# line; $SanitizedLines is the sanitized version split the same way.
function Test-MethodBody {
    param(
        [object]   $Body,
        [string[]] $Lines,
        [string[]] $SanitizedLines
    )

    $declLine0   = $Body.DeclLine - 1
    $braceLine0  = $Body.BraceLine - 1
    $endLine0    = $Body.EndLine - 1

    # Whitelist on the declaration line(s): consider every line from
    # DeclLine through BraceLine inclusive (multi-line signatures).
    for ($k = $declLine0; $k -le $braceLine0; $k++) {
        if ($Lines[$k] -match '//\s*sync-after-mutation-ok\b') {
            return @()  # whole method whitelisted
        }
    }

    $bodyStart = $braceLine0 + 1
    $bodyEnd   = $endLine0 - 1
    if ($bodyStart -gt $bodyEnd) { return @() }

    # Collect line-level features from the SANITIZED body (so braces /
    # tokens inside strings can't confuse us). Comments are nuked too —
    # but the whitelist check above runs on the ORIGINAL line so the
    # `// sync-after-mutation-ok:` marker survives.

    $mutationLines = [System.Collections.Generic.List[int]]::new()
    $syncLines     = [System.Collections.Generic.List[int]]::new()
    $returnLines   = [System.Collections.Generic.List[int]]::new()

    # Track lambda nesting so we can attribute mutations / syncs / returns
    # correctly. A lambda body opens with `=>` followed (eventually) by
    # `{`. We track from the `=> {` boundary; everything inside until the
    # matching `}` is "in a lambda" and skipped.
    $lambdaDepth = 0
    $pendingArrow = $false  # set when we see `=>` and look for `{`

    # Per-line features need char-level brace tracking inside the line
    # itself, since `=>` and `{` may appear on the same line.
    for ($lineIdx = $bodyStart; $lineIdx -le $bodyEnd; $lineIdx++) {
        $sLine = $SanitizedLines[$lineIdx]
        $oLine = $Lines[$lineIdx]
        if ($null -eq $sLine) { continue }

        # Walk the line char-by-char to track lambda nesting.
        $colDepthDelta = 0
        $col = 0
        $startedInLambda = ($lambdaDepth -gt 0)
        $beforeAnyContent = $true
        $featurePositions = @{ Mutation = @(); Sync = @(); Return = @() }

        # Find tokens of interest in the line, but only count them when
        # NOT inside a lambda.
        $lineLen = $sLine.Length
        $tokenIdx = 0

        # Pre-locate token positions in the line via regex (on sanitized
        # text), then for each token determine if it's inside a lambda by
        # tracking braces up to that point.

        $tokenRegex = [regex]'\.(SetStateAsync|PatchFieldsAsync|SyncAsync)\s*\(|=>\s*\{?|(?<![A-Za-z_])return(?=\s|;|$)|\{|\}'
        $matches = $tokenRegex.Matches($sLine)

        foreach ($m in $matches) {
            $tok = $m.Value
            $pos = $m.Index

            # If we're entering this token while inside a lambda, skip
            # for feature purposes (but still update brace depth).
            $inLambdaNow = ($lambdaDepth -gt 0)

            if ($tok -match '^=>') {
                # `=>` arrow — opens a lambda body if followed by `{`.
                if ($tok -match '\{') {
                    $lambdaDepth++
                    $pendingArrow = $false
                } else {
                    $pendingArrow = $true
                }
                continue
            }

            if ($tok -eq '{') {
                if ($pendingArrow) {
                    $lambdaDepth++
                    $pendingArrow = $false
                }
                continue
            }

            if ($tok -eq '}') {
                if ($lambdaDepth -gt 0) { $lambdaDepth-- }
                continue
            }

            if ($inLambdaNow) {
                # Skip features inside lambdas — the parent body owns
                # the flush per the rule.
                continue
            }

            if ($tok -match '^\.(SetStateAsync|PatchFieldsAsync)\(') {
                # Honour per-line whitelist on the ORIGINAL line text.
                if ($oLine -match '//\s*sync-after-mutation-ok\b') { continue }
                $mutationLines.Add($lineIdx + 1) | Out-Null
                continue
            }

            if ($tok -match '^\.SyncAsync\(') {
                $syncLines.Add($lineIdx + 1) | Out-Null
                continue
            }

            if ($tok -eq 'return') {
                $returnLines.Add($lineIdx + 1) | Out-Null
                continue
            }
        }
    }

    if ($mutationLines.Count -eq 0) { return @() }

    # Look for `finally { … SyncAsync(…) … }` blocks within the body. If
    # any finally block in the same method contains a sync, every mutation
    # in the same method that lexically precedes the `finally` keyword is
    # considered flushed.
    $finallySyncLines = [System.Collections.Generic.List[int]]::new()
    $bodyText = ($SanitizedLines[$bodyStart..$bodyEnd] -join "`n")
    $bodyOffset = $bodyStart  # 0-based line offset

    # Walk the body looking for `finally` followed by a brace block.
    $finallyRegex = [regex]'\bfinally\b'
    foreach ($fm in $finallyRegex.Matches($bodyText)) {
        # Find the `{` after this match
        $idx = $fm.Index + $fm.Length
        while ($idx -lt $bodyText.Length -and $bodyText[$idx] -ne '{') { $idx++ }
        if ($idx -ge $bodyText.Length) { continue }

        # Match the closing brace
        $localDepth = 1
        $j = $idx + 1
        while ($j -lt $bodyText.Length -and $localDepth -gt 0) {
            $ch = $bodyText[$j]
            if ($ch -eq '{') { $localDepth++ }
            elseif ($ch -eq '}') { $localDepth-- }
            if ($localDepth -gt 0) { $j++ }
        }
        if ($j -ge $bodyText.Length) { continue }

        $finallyBlock = $bodyText.Substring($idx + 1, $j - $idx - 1)
        if ($finallyBlock -match '\.SyncAsync\s*\(') {
            # Compute the 1-based line of the `finally` keyword.
            $finallyKwLine = $bodyOffset + 1 + (($bodyText.Substring(0, $fm.Index) -split "`n").Count - 1)
            $finallySyncLines.Add($finallyKwLine) | Out-Null
        }
    }

    $violations = @()

    foreach ($mLine in $mutationLines) {
        # Try the lexical "sync after mutation, before any return" rule.
        $ok = $false
        $earliestSync = ($syncLines | Where-Object { $_ -gt $mLine } | Select-Object -First 1)
        $earliestReturn = ($returnLines | Where-Object { $_ -gt $mLine } | Select-Object -First 1)

        if ($null -ne $earliestSync) {
            if ($null -eq $earliestReturn -or $earliestSync -le $earliestReturn) {
                $ok = $true
            }
        }

        if (-not $ok) {
            # Try the try/finally exception: any finally-with-sync block
            # whose `finally` keyword is lexically AFTER the mutation
            # covers it (the mutation was inside the try, the finally
            # runs on the way out).
            $coveringFinally = ($finallySyncLines | Where-Object { $_ -gt $mLine } | Select-Object -First 1)
            if ($null -ne $coveringFinally) {
                $ok = $true
            }
        }

        if (-not $ok) {
            $violations += @{
                Line     = $mLine
                DeclLine = $Body.DeclLine
                Reason   = if ($syncLines.Count -eq 0) {
                    "method body has no SyncAsync call"
                } elseif ($null -ne $earliestReturn -and ($null -eq $earliestSync -or $earliestReturn -lt $earliestSync)) {
                    "return at line $earliestReturn precedes any SyncAsync after the mutation"
                } else {
                    "no SyncAsync call after mutation"
                }
            }
        }
    }

    return $violations
}

# ── Main scan ────────────────────────────────────────────────────────────
$files = Get-ChildItem -LiteralPath $scanRoot -Filter '*.cs' -File `
                        -ErrorAction SilentlyContinue |
            Sort-Object FullName

if (-not $files) {
    Write-Host "PASS: no C# files under $ScanDir" -ForegroundColor Green
    exit 0
}

$allViolations = [System.Collections.Generic.List[object]]::new()

foreach ($file in $files) {
    $relative = ([System.IO.Path]::GetRelativePath($RepoRoot, $file.FullName)) `
                  -replace '\\', '/'
    $source = [System.IO.File]::ReadAllText($file.FullName)
    $lines = [regex]::Split($source, "`r?`n")

    try {
        $sanitized = ConvertTo-SanitizedCSharp -Source $source
    } catch {
        Write-Host "FATAL: failed to sanitize $relative — $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
    $sanitizedLines = [regex]::Split($sanitized, "`r?`n")

    $bodies = Get-MethodBodies -SanitizedSource $sanitized

    foreach ($body in $bodies) {
        $bodyViolations = Test-MethodBody -Body $body `
                                          -Lines $lines `
                                          -SanitizedLines $sanitizedLines
        foreach ($v in $bodyViolations) {
            $allViolations.Add(@{
                File     = $relative
                Line     = $v.Line
                DeclLine = $v.DeclLine
                Reason   = $v.Reason
            }) | Out-Null
        }
    }
}

if ($allViolations.Count -eq 0) {
    Write-Host "PASS: every command-verb mutation is followed by SyncAsync" -ForegroundColor Green
    exit 0
}

if ($Format -eq 'github') {
    foreach ($v in $allViolations) {
        $msg = "sync-after-mutation: $($v.Reason). Add 'await twig.SyncAsync(ct).ConfigureAwait(false);' after the mutation, or whitelist with '// sync-after-mutation-ok: <reason>'."
        Write-Output "::error file=$($v.File),line=$($v.Line)::$msg"
    }
} else {
    Write-Host ""
    Write-Host "FAIL: sync-after-mutation violations found" -ForegroundColor Red
    Write-Host "------------------------------------------" -ForegroundColor Red
    foreach ($v in $allViolations) {
        Write-Host ("  {0}:{1} (method declared at line {2}) — {3}" -f $v.File, $v.Line, $v.DeclLine, $v.Reason) -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "Background:" -ForegroundColor Cyan
    Write-Host "  twig's SetStateAsync / PatchFieldsAsync stage changes in twig's" -ForegroundColor Cyan
    Write-Host "  local cache + pending queue but do NOT push to ADO. A separate" -ForegroundColor Cyan
    Write-Host "  process reading via IWorkItemRepository.GetByIdAsync will see" -ForegroundColor Cyan
    Write-Host "  the OLD state. Workflow stalls or routes wrong." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Remediation:" -ForegroundColor Cyan
    Write-Host "  1. Add 'await twig.SyncAsync(ct).ConfigureAwait(false);' after the" -ForegroundColor Cyan
    Write-Host "     mutation, before any reachable return." -ForegroundColor Cyan
    Write-Host "  2. For batched loops, do ONE post-loop sync guarded by a count > 0" -ForegroundColor Cyan
    Write-Host "     check." -ForegroundColor Cyan
    Write-Host "  3. If the method is a private helper called by an already-flushing" -ForegroundColor Cyan
    Write-Host "     caller, add '// sync-after-mutation-ok: called by <X>' on the" -ForegroundColor Cyan
    Write-Host "     mutation line OR the method declaration line." -ForegroundColor Cyan
    Write-Host ""
    Write-Host 'See .github/skills/polyphony-cli-developer/SKILL.md ("State-mutation durability").' -ForegroundColor Cyan
    Write-Host "AB#3126/27/28/29 family — PRs #339/340/341/356." -ForegroundColor Cyan
    Write-Host ""
}

exit 1
