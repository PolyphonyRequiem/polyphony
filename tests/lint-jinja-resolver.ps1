<#
.SYNOPSIS
    CI lint — resolves Jinja2 `{{ <step>.output.<path> }}` references in workflow YAMLs against the verb output schema registry.

.DESCRIPTION
    Implements docs/decisions/jinja-resolver-lint.md (#175). For each workflow under
    `.conductor/registry/workflows/*.yaml`, the lint:

      1. Builds a step_id -> verb map from the workflow's `agents:` array.
         A `type: script` step with `command: polyphony` and `args: ["<group>", "<command>", ...]`
         is a polyphony verb; everything else (pwsh / twig / gh / agent / sub-workflow)
         has no schema in the registry.

      2. Walks every Jinja2-evaluated string field (prompt, args entries, command,
         when, output mappings, input_mapping) and extracts every reference of the
         form `{{ <id>.output.<path> }}`.

      3. For each reference, resolves <id> against the step map and walks <path>
         through the registry's type graph, emitting one of:

           JINJA001  Error    field doesn't exist on the verb's result type
           JINJA002  Warning  omit-when-null field is referenced without a guard
           JINJA003  Error    <id> doesn't refer to any step in this workflow
           JINJA004  Warning  step is not a polyphony verb (suppressed by default;
                              opt in with -Pedantic)
           JINJA005  Error    path walks through a scalar leaf, or a list/map
                              without an index/method/filter

    Output:
      -Format human   colourized table (default; for local runs)
      -Format github  ::error / ::warning workflow commands for CI annotation

    Exit codes:
      0  clean (or warnings only)
      1  one or more errors found (or -FailOnWarnings and any warning)
      2  configuration error (missing registry, malformed YAML, bad allowlist)

.PARAMETER WorkflowsDir
    Directory of workflow YAMLs to scan. Default: .conductor/registry/workflows/

.PARAMETER RegistryPath
    Path to verb-output-schemas.json. Default: artifacts/verb-output-schemas.json
    (the path the registry source generator writes to). When that file is
    missing the lint will fall back to tests/lint/fixtures/verb-output-schemas.json
    if -UseFixtureRegistry is set; otherwise it exits 2 with a clear remediation
    message.

.PARAMETER Format
    Output format: 'human' (default) or 'github'.

.PARAMETER Pedantic
    Emit JINJA004 (non-polyphony step) warnings per-reference. Default suppresses
    them and prints a single summary count.

.PARAMETER FailOnWarnings
    Treat warnings as errors for the exit-code decision.

.PARAMETER OnlyFile
    If supplied, only that workflow file (basename match) is linted. Useful for
    local debugging.

.PARAMETER UseFixtureRegistry
    Use the checked-in registry fixture under tests/lint/fixtures/. Used by the
    Pester suite for hermetic synthetic-fixture tests.

.PARAMETER AllowlistPath
    Path to the allowlist YAML. Default: tests/lint-jinja-resolver.allowlist.yaml.
    Hard-failed (exit 2) if the allowlist exceeds 15 entries — the suppression
    mechanism is for tracked-bug deferral, not a parallel source of truth.
#>
[CmdletBinding()]
param(
    [string] $WorkflowsDir,
    [string] $RegistryPath,
    [ValidateSet('human', 'github')]
    [string] $Format = 'human',
    [switch] $Pedantic,
    [switch] $FailOnWarnings,
    [string] $OnlyFile,
    [switch] $UseFixtureRegistry,
    [string] $AllowlistPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $WorkflowsDir) {
    $WorkflowsDir = Join-Path $repoRoot '.conductor/registry/workflows'
}
if (-not $AllowlistPath) {
    $AllowlistPath = Join-Path $PSScriptRoot 'lint-jinja-resolver.allowlist.yaml'
}

# Allowlist max — see ADR § "Allowlist size cap".
$AllowlistMaxEntries = 15

# Workflow-builtin identifiers that are never step refs (skip resolution entirely).
# `workflow.input.X`, `workflow.dir`, `workflow.name`, etc. are conductor-injected.
$BuiltinIdentifiers = @('workflow', 'env', 'self', 'loop', 'true', 'false', 'none', 'null')

# Recognized list/map attribute access — not a "walk through" requiring index.
$ListAttrs = @('length', 'first', 'last', 'count')
$MapAttrs  = @('keys', 'values', 'items', 'length', 'count')

# Conductor-injected fields on every `type: script` step's output envelope.
# These are added by the conductor runtime, not by the polyphony verb itself,
# so they're always valid regardless of what the verb's result DTO declares.
# Source: conductor's process-step output envelope spec.
$ConductorScriptOutputFields = @('exit_code', 'stdout', 'stderr')

function Resolve-TypeRef {
    # The registry's type_ref strings sometimes carry a trailing `?` nullability
    # annotation (e.g. `Polyphony.PrPollMetadata?`) while the types-map keys are
    # always unannotated. Strip the suffix before looking up.
    param([string] $TypeRef)
    if ([string]::IsNullOrEmpty($TypeRef)) { return $null }
    return $TypeRef.TrimEnd('?')
}

function Test-ResultIsMap {
    # Some verbs (e.g. `plan load-guidance` returning Dictionary<string,string>)
    # have a result type that IS a map; the registry's source generator records
    # CLR-internal members (`Count`, `Keys`, `Values`, `Comparer`) as fields
    # rather than recognizing the type as a map. Detect by CLR-name shape and
    # treat top-level access as a map lookup with open keys.
    param([string] $TypeRef)
    if ([string]::IsNullOrEmpty($TypeRef)) { return $false }
    return ($TypeRef -match '^System\.Collections\.Generic\.(?:I|)(?:Read[Oo]nly)?Dictionary<')
}

# ── Module dependency ──────────────────────────────────────────────────
if (-not (Get-Module -ListAvailable -Name powershell-yaml)) {
    Write-Host "FATAL: the powershell-yaml module is required by lint-jinja-resolver.ps1." -ForegroundColor Red
    Write-Host "Install with: Install-Module -Name powershell-yaml -Force -SkipPublisherCheck -Scope CurrentUser" -ForegroundColor Cyan
    exit 2
}
Import-Module powershell-yaml -ErrorAction Stop

# ── Registry resolution ────────────────────────────────────────────────
function Resolve-RegistryPath {
    param([string] $Explicit, [switch] $UseFixture)
    if ($Explicit) { return $Explicit }
    if ($UseFixture) {
        return (Join-Path $repoRoot 'tests/lint/fixtures/verb-output-schemas.json')
    }
    return (Join-Path $repoRoot 'artifacts/verb-output-schemas.json')
}

function Read-Registry {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "" 
        Write-Host "FATAL: verb-output-schemas registry not found at:" -ForegroundColor Red
        Write-Host "  $Path" -ForegroundColor Red
        Write-Host ""
        Write-Host "Run 'dotnet build src/Polyphony/Polyphony.csproj' to generate it," -ForegroundColor Cyan
        Write-Host "then re-run the lint. (CI does this automatically; local first-run" -ForegroundColor Cyan
        Write-Host "after a fresh checkout requires the build.) Alternatively pass" -ForegroundColor Cyan
        Write-Host "-UseFixtureRegistry to lint against the checked-in fixture under" -ForegroundColor Cyan
        Write-Host "tests/lint/fixtures/." -ForegroundColor Cyan
        exit 2
    }
    try {
        return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 32)
    } catch {
        Write-Host "FATAL: failed to parse $Path as JSON: $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
}

# ── Allowlist ──────────────────────────────────────────────────────────
function Read-Allowlist {
    param([string] $Path)
    $entries = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $Path)) { return ,$entries }
    try {
        $raw = ConvertFrom-Yaml (Get-Content -LiteralPath $Path -Raw)
    } catch {
        Write-Host "FATAL: allowlist parse error ($Path): $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
    if ($null -eq $raw) { return ,$entries }
    if (-not $raw.ContainsKey('suppress')) {
        Write-Host "FATAL: allowlist must have a top-level 'suppress:' list." -ForegroundColor Red
        exit 2
    }
    foreach ($e in $raw['suppress']) {
        foreach ($k in @('file', 'code', 'reference', 'issue')) {
            if (-not $e.ContainsKey($k) -or [string]::IsNullOrWhiteSpace([string]$e[$k])) {
                Write-Host "FATAL: allowlist entry missing required field '$k': $($e | ConvertTo-Json -Compress)" -ForegroundColor Red
                exit 2
            }
        }
        $entries.Add([PSCustomObject]@{
            File      = [string]$e['file']
            Code      = [string]$e['code']
            Reference = [string]$e['reference']
            Issue     = [string]$e['issue']
        })
    }
    if ($entries.Count -gt $AllowlistMaxEntries) {
        Write-Host "FATAL: allowlist has $($entries.Count) entries, exceeding the cap of $AllowlistMaxEntries." -ForegroundColor Red
        Write-Host "Clean up stale suppressions or amend the cap by ADR." -ForegroundColor Cyan
        exit 2
    }
    return ,$entries
}

function Test-Suppressed {
    param(
        [Parameter(Mandatory)] $Entries,
        [Parameter(Mandatory)] [string] $RelFile,
        [Parameter(Mandatory)] [string] $Code,
        [Parameter(Mandatory)] [string] $Reference
    )
    foreach ($e in $Entries) {
        if ($e.File -eq $RelFile -and $e.Code -eq $Code -and $e.Reference -eq $Reference) {
            return $true
        }
    }
    return $false
}

# ── Step → verb extraction ─────────────────────────────────────────────
function Get-StepsFromYaml {
    <#
        Returns an ordered list of step descriptors from a parsed workflow YAML.
        Each descriptor: { Name, Type, Verb, IsPolyphony, Line, RawOrder }.
        Verb is null for non-polyphony steps. Line is the 1-based line number
        where the step's `name:` (or `id:`) appears in the original YAML text;
        used for diagnostic location.
    #>
    param(
        [Parameter(Mandatory)] $Yaml,
        $RawLines
    )

    if ($null -eq $RawLines) { $RawLines = @() }
    $steps = New-Object System.Collections.Generic.List[object]
    if (-not $Yaml.ContainsKey('agents')) { return ,$steps }

    $order = 0
    foreach ($a in $Yaml['agents']) {
        if ($null -eq $a -or -not $a.ContainsKey('name')) { continue }
        $name = [string]$a['name']
        $type = if ($a.ContainsKey('type')) { [string]$a['type'] } else { 'agent' }
        $verb = $null
        $isPoly = $false

        if ($type -eq 'script' -and $a.ContainsKey('command')) {
            $cmd = [string]$a['command']
            if ($cmd -eq 'polyphony' -and $a.ContainsKey('args')) {
                # First two non-flag args are <group> <command>; some verbs are
                # top-level (no group) -- detect by checking the registry, but for
                # now compose both candidates and the resolver tries them.
                $argTokens = @($a['args'] | ForEach-Object { [string]$_ })
                $positional = @($argTokens | Where-Object { $_ -and -not $_.StartsWith('-') -and -not $_.StartsWith('{{') -and -not $_.StartsWith('"{{') })
                if ($positional.Count -ge 2) {
                    $verb = "$($positional[0]) $($positional[1])"
                } elseif ($positional.Count -eq 1) {
                    $verb = $positional[0]
                }
                $isPoly = $true
            }
        }

        # Find the line number of the step's `name:` in the raw YAML.
        $line = 0
        $needle = "- name: $name"
        for ($i = 0; $i -lt $RawLines.Count; $i++) {
            if ($RawLines[$i] -match "^\s*-\s+name:\s+$([regex]::Escape($name))\s*$") {
                $line = $i + 1
                break
            }
        }

        $steps.Add([PSCustomObject]@{
            Name        = $name
            Type        = $type
            Verb        = $verb
            IsPolyphony = $isPoly
            Line        = $line
            Order       = $order++
        })
    }
    return ,$steps
}

# ── Jinja2 reference extraction with line tracking ─────────────────────
function Get-JinjaReferences {
    <#
        Parse a Jinja-evaluated string field and return all `<id>.output.<path>`
        references plus the guard context (whether the reference is wrapped in
        `default()`, an enclosing `{% if X is defined %}` block, or part of an
        intra-expression `is defined` and/or chain).

        Each result: { Id, Path (string array), GuardForms (string[]), LineOffset
        (lines into the field from its starting line), Snippet }.
    #>
    param(
        [Parameter(Mandatory)] [string] $Text
    )

    $refs = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrEmpty($Text)) { return ,$refs }

    # Block-scope guard stack — list of guard descriptors that are currently
    # active at this point in the linear scan. Each guard: { Id, Prefix, Depth }
    # where Prefix is the dot-joined path after .output. (empty string == "any
    # X.output.* descendant").
    $guardStack = New-Object System.Collections.Generic.Stack[object]
    # Tracks every {% if ... %} we open so we can balance with {% endif %}. Not
    # every {% if %} produces a guard (some test other things) — null entries
    # are pushed so the depth bookkeeping stays right.
    $blockStack = New-Object System.Collections.Generic.Stack[object]

    # Tokenize Jinja blocks ({% ... %}), expressions ({{ ... }}), and skip the
    # rest. Walk the text once, tracking line offsets.
    $i = 0
    $len = $Text.Length
    $lineOffset = 0

    while ($i -lt $len) {
        $newlineIdx = $Text.IndexOf("`n", $i)
        $stmtStart  = $Text.IndexOf('{%', $i)
        $exprStart  = $Text.IndexOf('{{', $i)

        $next = -1
        $kind = $null
        foreach ($cand in @(@($stmtStart, 'stmt'), @($exprStart, 'expr'), @($newlineIdx, 'nl'))) {
            if ($cand[0] -ge 0 -and ($next -lt 0 -or $cand[0] -lt $next)) {
                $next = $cand[0]; $kind = $cand[1]
            }
        }

        if ($next -lt 0) { break }
        $i = $next

        if ($kind -eq 'nl') {
            $lineOffset++
            $i++
            continue
        }

        if ($kind -eq 'stmt') {
            $end = $Text.IndexOf('%}', $i)
            if ($end -lt 0) { break }
            $body = $Text.Substring($i + 2, $end - $i - 2).Trim()
            # Count newlines inside the block so $lineOffset stays accurate.
            $inner = $Text.Substring($i, $end + 2 - $i)
            $innerNewlines = ([regex]::Matches($inner, "`n")).Count

            if ($body -match '^if\b\s*(.+)$') {
                $cond = $Matches[1].Trim()
                $guard = Parse-IfGuard -Condition $cond
                if ($guard) {
                    $guardStack.Push($guard)
                    $blockStack.Push($guard)
                } else {
                    $blockStack.Push($null)
                }
            } elseif ($body -match '^elif\b\s*(.+)$') {
                # Replace the top guard with the elif's guard (the `if` branch
                # is no longer active here).
                if ($blockStack.Count -gt 0) {
                    $popped = $blockStack.Pop()
                    if ($popped -ne $null) { [void]$guardStack.Pop() }
                }
                $cond = $Matches[1].Trim()
                $guard = Parse-IfGuard -Condition $cond
                if ($guard) {
                    $guardStack.Push($guard)
                    $blockStack.Push($guard)
                } else {
                    $blockStack.Push($null)
                }
            } elseif ($body -match '^else\b') {
                # Drop any guard from the matching if; else has no guard.
                if ($blockStack.Count -gt 0) {
                    $popped = $blockStack.Pop()
                    if ($popped -ne $null) { [void]$guardStack.Pop() }
                    $blockStack.Push($null)
                }
            } elseif ($body -match '^endif\b') {
                if ($blockStack.Count -gt 0) {
                    $popped = $blockStack.Pop()
                    if ($popped -ne $null) { [void]$guardStack.Pop() }
                }
            } elseif ($body -match '^for\b') {
                # Loop blocks introduce variable bindings but we don't need to
                # track them — references inside loops still resolve normally.
                $blockStack.Push($null)
            } elseif ($body -match '^endfor\b') {
                if ($blockStack.Count -gt 0) { [void]$blockStack.Pop() }
            }

            $lineOffset += $innerNewlines
            $i = $end + 2
            continue
        }

        # kind == 'expr'
        $end = $Text.IndexOf('}}', $i)
        if ($end -lt 0) { break }
        $exprBody = $Text.Substring($i + 2, $end - $i - 2)
        $exprStartLine = $lineOffset
        $innerNewlines = ([regex]::Matches($exprBody, "`n")).Count

        # Per-expression guards from intra-expression `... is defined and/or ...` patterns.
        $exprGuards = Get-IntraExpressionGuards -Expression $exprBody

        # Per-expression "default()" guard: applies to ALL refs in this expression
        # (lazy approximation — Jinja's default() in a pipe affects only its left
        # operand, but the dominant idiom is one ref per expression).
        $hasDefaultPipe = ($exprBody -match '\bdefault\s*\(')

        # Extract every <id>.output.<path> reference. Tail terminates on whitespace,
        # operator, pipe, paren, bracket, comma, or end-of-string.
        $refRegex = '(?<![\w\.])(?<id>[A-Za-z_]\w*)\.output(?<tail>(?:\.[A-Za-z_]\w*|\[[^\]]+\])+)'
        foreach ($m in [regex]::Matches($exprBody, $refRegex)) {
            $id = $m.Groups['id'].Value
            $tail = $m.Groups['tail'].Value
            $path = ConvertTo-PathSegments -Tail $tail

            # Detect "is the test, not the consumption":
            $isTestForm = $false
            $testRegion = $exprBody.Substring($m.Index + $m.Length)
            if ($testRegion -match '^\s*is\s+(not\s+)?(defined|none)\b') {
                $isTestForm = $true
            }

            # Active guards = block-scope stack + intra-expression guards.
            $effectiveGuards = New-Object System.Collections.Generic.List[object]
            foreach ($g in $guardStack) { $effectiveGuards.Add($g) }
            foreach ($g in $exprGuards) { $effectiveGuards.Add($g) }
            if ($hasDefaultPipe) {
                $effectiveGuards.Add([PSCustomObject]@{ Kind = 'default-pipe'; Id = $id; Prefix = '' })
            }
            if ($isTestForm) {
                $effectiveGuards.Add([PSCustomObject]@{ Kind = 'is-defined-test'; Id = $id; Prefix = '' })
            }

            $refs.Add([PSCustomObject]@{
                Id          = $id
                Path        = $path
                Tail        = $tail
                LineOffset  = $exprStartLine
                IsTestForm  = $isTestForm
                Guards      = $effectiveGuards
                Snippet     = "{{$exprBody}}".Trim()
            })
        }

        $lineOffset += $innerNewlines
        $i = $end + 2
    }

    return ,$refs
}

function Parse-IfGuard {
    <#
        Inspect an `if` condition for an `is defined` / `is not none` test that
        creates a guard. Returns a guard object or $null.

        Recognized forms:
          X is defined
          X.output is defined
          X.output.foo.bar is defined
          X is not none
          X.output is not none
    #>
    param([string] $Condition)
    # Look for the *first* `is defined` / `is not none` test on a path.
    if ($Condition -match '(?<![\w\.])(?<id>[A-Za-z_]\w*)(?<tail>(?:\.[A-Za-z_]\w*)*)\s+is\s+(?:not\s+)?(?:defined|none|not\s+none)\b') {
        $id = $Matches['id']
        $tail = $Matches['tail']
        # Strip a leading `.output` from the tail to get the prefix.
        $prefix = ''
        if ($tail.StartsWith('.output')) {
            $prefix = $tail.Substring('.output'.Length).TrimStart('.')
        } elseif ($tail -ne '') {
            # Guard is on something that isn't `<id>.output.<path>` — e.g. a loop
            # variable. Don't treat as a guard for `<id>.output.*`.
            return $null
        }
        return [PSCustomObject]@{
            Kind   = 'block-if'
            Id     = $id
            Prefix = $prefix
        }
    }
    return $null
}

function Get-IntraExpressionGuards {
    <#
        Find every `<id>(.path)? is (not )?(defined|none)` test inside the
        expression and return guard descriptors. The presence of `and` / `or`
        elsewhere in the expression is taken as license to treat the test as
        guarding sibling references — Jinja short-circuits both, and the
        idioms in the wild rely on it.
    #>
    param([string] $Expression)
    $guards = New-Object System.Collections.Generic.List[object]
    $regex = '(?<![\w\.])(?<id>[A-Za-z_]\w*)(?<tail>(?:\.[A-Za-z_]\w*)*)\s+is\s+(?:not\s+)?(?:defined|none|not\s+none)\b'
    foreach ($m in [regex]::Matches($Expression, $regex)) {
        $id = $m.Groups['id'].Value
        $tail = $m.Groups['tail'].Value
        $prefix = ''
        if ($tail.StartsWith('.output')) {
            $prefix = $tail.Substring('.output'.Length).TrimStart('.')
        } elseif ($tail -ne '') {
            continue
        }
        $guards.Add([PSCustomObject]@{
            Kind   = 'expr-is-defined'
            Id     = $id
            Prefix = $prefix
        })
    }
    return ,$guards
}

function ConvertTo-PathSegments {
    <#
        Tail is the substring after `<id>.output`, like ".foo.bar[0].baz". Returns
        an ordered array of segment descriptors: { Kind = 'attr'|'index'; Value }.
    #>
    param([string] $Tail)
    $segs = New-Object System.Collections.Generic.List[object]
    $i = 0
    while ($i -lt $Tail.Length) {
        if ($Tail[$i] -eq '.') {
            $j = $i + 1
            while ($j -lt $Tail.Length -and ($Tail[$j] -match '[A-Za-z0-9_]')) { $j++ }
            $segs.Add([PSCustomObject]@{ Kind = 'attr'; Value = $Tail.Substring($i + 1, $j - $i - 1) })
            $i = $j
        } elseif ($Tail[$i] -eq '[') {
            $j = $Tail.IndexOf(']', $i)
            if ($j -lt 0) { break }
            $segs.Add([PSCustomObject]@{ Kind = 'index'; Value = $Tail.Substring($i + 1, $j - $i - 1).Trim() })
            $i = $j + 1
        } else {
            $i++
        }
    }
    return ,$segs.ToArray()
}

# ── Reference resolution against the registry ──────────────────────────
function Test-GuardsCover {
    param(
        [Parameter(Mandatory)] $Guards,
        [Parameter(Mandatory)] [string] $Id,
        $PathPrefixes  # '', 'foo', 'foo.bar', ...
    )
    if ($null -eq $PathPrefixes) { $PathPrefixes = @('') }
    foreach ($g in $Guards) {
        if ($g.Id -ne $Id) { continue }
        # Empty prefix on the guard => guards every X.output.* descendant.
        if ([string]::IsNullOrEmpty($g.Prefix)) { return $true }
        foreach ($p in $PathPrefixes) {
            if ($p -eq $g.Prefix -or $p.StartsWith($g.Prefix + '.')) { return $true }
        }
    }
    return $false
}

function Resolve-Reference {
    <#
        Walks a single reference's path against the registry and returns a list of
        diagnostic descriptors (may be empty). Each diagnostic: { Code, Severity,
        Message, RefPathSoFar }.
    #>
    param(
        [Parameter(Mandatory)] $Reference,
        [Parameter(Mandatory)] $StepLookup,
        [Parameter(Mandatory)] $Registry,
        [Parameter(Mandatory)] [string] $WorkflowName
    )

    $diagnostics = New-Object System.Collections.Generic.List[object]
    $id = $Reference.Id

    if ($BuiltinIdentifiers -contains $id) {
        return ,$diagnostics
    }

    if (-not $StepLookup.ContainsKey($id)) {
        $diagnostics.Add([PSCustomObject]@{
            Code     = 'JINJA003'
            Severity = 'error'
            Message  = "reference to '$id.output.*' but no step named '$id' is declared in workflow '$WorkflowName'."
        })
        return ,$diagnostics
    }
    $step = $StepLookup[$id]

    if (-not $step.IsPolyphony) {
        $diagnostics.Add([PSCustomObject]@{
            Code     = 'JINJA004'
            Severity = 'warning'
            Message  = "step '$id' (type: $($step.Type)$(if($step.Verb){', verb: '+$step.Verb})) is not a polyphony verb in the registry; cannot verify '$id.output$($Reference.Tail)'."
        })
        return ,$diagnostics
    }

    if (-not $Registry.verbs.PSObject.Properties.Name -contains $step.Verb) {
        $diagnostics.Add([PSCustomObject]@{
            Code     = 'JINJA004'
            Severity = 'warning'
            Message  = "step '$id' invokes verb '$($step.Verb)' which is not in the registry; cannot verify path."
        })
        return ,$diagnostics
    }

    $verbInfo = $Registry.verbs.($step.Verb)
    $resultType = Resolve-TypeRef -TypeRef $verbInfo.result_type
    if (-not $Registry.types.PSObject.Properties.Name -contains $resultType) {
        $diagnostics.Add([PSCustomObject]@{
            Code     = 'JINJA004'
            Severity = 'warning'
            Message  = "verb '$($step.Verb)' result type '$resultType' is not in the registry's types map; cannot verify path."
        })
        return ,$diagnostics
    }

    # If the result type is a Dictionary<...>, treat the top-level as a map.
    # The registry-generation pass records CLR introspection fields on these
    # types (Count/Keys/Values/Comparer) which would mask the actual map
    # semantics — bypass them here.
    $initialContext = if (Test-ResultIsMap -TypeRef $resultType) {
        # The verb's result is a map of string->string (only Dictionary<string,X>
        # cases land here today; assume scalar values until the registry carries
        # the value type explicitly for these top-level cases).
        [PSCustomObject]@{ Kind = 'map'; ValueKind = 'scalar'; ValueTypeRef = $null }
    } else {
        [PSCustomObject]@{ Kind = 'object'; TypeRef = $resultType }
    }

    # Walk the path. Maintain a "type context" describing what we're walking
    # through — initial context is the verb result type's field list (or a
    # synthetic map context for Dictionary-typed verb results).
    $context = $initialContext
    $accumPath = New-Object System.Collections.Generic.List[string]
    $unguardedOmitDiag = $null  # First omit-when-null hit; only one JINJA002 per ref.

    foreach ($seg in $Reference.Path) {
        if ($seg.Kind -eq 'attr') {
            $name = $seg.Value
            switch ($context.Kind) {
                'object' {
                    $type = $Registry.types.($context.TypeRef)
                    $field = $type.fields | Where-Object { $_.name -eq $name } | Select-Object -First 1

                    # Conductor-injected output fields on script steps. Only valid
                    # at the verb-result level (the first segment), and only on
                    # `type: script` steps.
                    if (-not $field -and $accumPath.Count -eq 0 -and $step.Type -eq 'script' -and $ConductorScriptOutputFields -contains $name) {
                        $accumPath.Add($name) | Out-Null
                        $context = [PSCustomObject]@{ Kind = 'scalar' }
                        continue
                    }

                    if (-not $field) {
                        $diagnostics.Add([PSCustomObject]@{
                            Code     = 'JINJA001'
                            Severity = 'error'
                            Message  = "field '$name' does not exist on '$($context.TypeRef)' (verb: $($step.Verb)). Path so far: $id.output$(if($accumPath.Count){'.'+($accumPath -join '.')})"
                        })
                        return ,$diagnostics
                    }
                    $accumPath.Add($name) | Out-Null
                    if ($field.can_omit_when_null -and -not $unguardedOmitDiag) {
                        # Build prefixes that would cover this access: '', and every prefix
                        # ending at and below this field name.
                        $prefixes = @('')
                        for ($k = 1; $k -le $accumPath.Count; $k++) {
                            $prefixes += ($accumPath[0..($k-1)] -join '.')
                        }
                        if (-not (Test-GuardsCover -Guards $Reference.Guards -Id $id -PathPrefixes $prefixes)) {
                            $unguardedOmitDiag = [PSCustomObject]@{
                                Code     = 'JINJA002'
                                Severity = 'warning'
                                Message  = "'$id.output.$($accumPath -join '.')' has can_omit_when_null=true and is not guarded. Wrap in '{% if $id.output.$($accumPath -join '.') is defined %}', '{% if $id.output is defined %}', or pipe through '| default(...)'."
                            }
                        }
                    }
                    # Advance context to the field's type.
                    if ($field.kind -eq 'object') {
                        $nextRef = Resolve-TypeRef -TypeRef $field.type_ref
                        if (-not $nextRef -or -not ($Registry.types.PSObject.Properties.Name -contains $nextRef)) {
                            # Walk continues but we can't validate further. Demote
                            # the rest of the path to JINJA004 and stop.
                            $diagnostics.Add([PSCustomObject]@{
                                Code     = 'JINJA004'
                                Severity = 'warning'
                                Message  = "type '$($field.type_ref)' (referenced by '$id.output.$($accumPath -join '.')') is not in the registry's types map; cannot verify deeper path."
                            })
                            if ($unguardedOmitDiag) { $diagnostics.Add($unguardedOmitDiag) }
                            return ,$diagnostics
                        }
                        $context = [PSCustomObject]@{ Kind = 'object'; TypeRef = $nextRef }
                    } elseif ($field.kind -eq 'list') {
                        $context = [PSCustomObject]@{
                            Kind          = 'list'
                            ElementKind   = $field.element_kind
                            ElementTypeRef= (Resolve-TypeRef -TypeRef $field.element_type_ref)
                        }
                    } elseif ($field.kind -eq 'map') {
                        $context = [PSCustomObject]@{
                            Kind          = 'map'
                            ValueKind     = $field.value_kind
                            ValueTypeRef  = (Resolve-TypeRef -TypeRef $field.value_type_ref)
                        }
                    } else {
                        $context = [PSCustomObject]@{ Kind = 'scalar' }
                    }
                }
                'list' {
                    if ($ListAttrs -contains $name) {
                        # `length`, `first`, etc. — terminate or, for `first`/`last`, descend to element.
                        if ($name -in @('first', 'last')) {
                            if ($context.ElementKind -eq 'object') {
                                $context = [PSCustomObject]@{ Kind = 'object'; TypeRef = $context.ElementTypeRef }
                            } else {
                                $context = [PSCustomObject]@{ Kind = 'scalar' }
                            }
                        } else {
                            $context = [PSCustomObject]@{ Kind = 'scalar' }
                        }
                        $accumPath.Add($name) | Out-Null
                    } else {
                        $diagnostics.Add([PSCustomObject]@{
                            Code     = 'JINJA005'
                            Severity = 'error'
                            Message  = "cannot access '.$name' on a list ('$id.output.$($accumPath -join '.')'). Use '[N]', '| first', '| last', or '{% for x in ... %}'."
                        })
                        return ,$diagnostics
                    }
                }
                'map' {
                    if ($MapAttrs -contains $name) {
                        $context = [PSCustomObject]@{ Kind = 'scalar' }
                    } else {
                        # Treat as a key lookup; advance to value type.
                        if ($context.ValueKind -eq 'object') {
                            $context = [PSCustomObject]@{ Kind = 'object'; TypeRef = $context.ValueTypeRef }
                        } else {
                            $context = [PSCustomObject]@{ Kind = 'scalar' }
                        }
                    }
                    $accumPath.Add($name) | Out-Null
                }
                'scalar' {
                    $diagnostics.Add([PSCustomObject]@{
                        Code     = 'JINJA005'
                        Severity = 'error'
                        Message  = "cannot access '.$name' through a scalar leaf at '$id.output.$($accumPath -join '.')'."
                    })
                    return ,$diagnostics
                }
                default {
                    return ,$diagnostics
                }
            }
        } elseif ($seg.Kind -eq 'index') {
            switch ($context.Kind) {
                'list' {
                    if ($context.ElementKind -eq 'object') {
                        $context = [PSCustomObject]@{ Kind = 'object'; TypeRef = $context.ElementTypeRef }
                    } else {
                        $context = [PSCustomObject]@{ Kind = 'scalar' }
                    }
                    $accumPath.Add("[$($seg.Value)]") | Out-Null
                }
                'map' {
                    if ($context.ValueKind -eq 'object') {
                        $context = [PSCustomObject]@{ Kind = 'object'; TypeRef = $context.ValueTypeRef }
                    } else {
                        $context = [PSCustomObject]@{ Kind = 'scalar' }
                    }
                    $accumPath.Add("[$($seg.Value)]") | Out-Null
                }
                default {
                    $diagnostics.Add([PSCustomObject]@{
                        Code     = 'JINJA005'
                        Severity = 'error'
                        Message  = "cannot index into '$id.output.$($accumPath -join '.')' — not a list or map."
                    })
                    return ,$diagnostics
                }
            }
        }
    }

    if ($unguardedOmitDiag) {
        $diagnostics.Add($unguardedOmitDiag)
    }

    return ,$diagnostics
}

# ── Per-workflow walk ──────────────────────────────────────────────────
function Get-JinjaFields {
    <#
        Yield every Jinja-evaluated string field in a workflow YAML, with its
        approximate line number in the source. Returns: { Path, Value, Line }.
        The Path is a slash-joined breadcrumb for diagnostic context.
    #>
    param(
        [Parameter(Mandatory)] $Yaml,
        $RawLines
    )
    if ($null -eq $RawLines) { $RawLines = @() }

    $fields = New-Object System.Collections.Generic.List[object]

    function Add-Field {
        param([string] $Pth, $Val, [string[]] $Lines, [System.Collections.Generic.List[object]] $Out)
        if ($null -eq $Val) { return }
        if ($Val -is [string] -and $Val -match '\{\{') {
            # Find a line containing a distinctive substring of the value.
            $line = 0
            $needle = ($Val -split "`n")[0].Trim()
            if ($needle.Length -gt 8) {
                $tag = $needle.Substring(0, [Math]::Min(40, $needle.Length))
                for ($i = 0; $i -lt $Lines.Count; $i++) {
                    if ($Lines[$i].Contains($tag)) { $line = $i + 1; break }
                }
            }
            $Out.Add([PSCustomObject]@{ Path = $Pth; Value = $Val; Line = $line })
        }
    }

    # Top-level workflow output mappings.
    if ($Yaml.ContainsKey('workflow') -and $Yaml['workflow'] -is [hashtable]) {
        if ($Yaml['workflow'].ContainsKey('output')) {
            foreach ($k in $Yaml['workflow']['output'].Keys) {
                Add-Field "workflow.output.$k" $Yaml['workflow']['output'][$k] $RawLines $fields
            }
        }
    }
    if ($Yaml.ContainsKey('output')) {
        foreach ($k in $Yaml['output'].Keys) {
            Add-Field "output.$k" $Yaml['output'][$k] $RawLines $fields
        }
    }

    if ($Yaml.ContainsKey('agents')) {
        foreach ($a in $Yaml['agents']) {
            if ($null -eq $a -or -not $a.ContainsKey('name')) { continue }
            $aname = [string]$a['name']
            foreach ($k in @('prompt', 'command')) {
                if ($a.ContainsKey($k)) { Add-Field "agents/$aname/$k" $a[$k] $RawLines $fields }
            }
            if ($a.ContainsKey('args')) {
                $idx = 0
                foreach ($arg in $a['args']) {
                    Add-Field "agents/$aname/args[$idx]" $arg $RawLines $fields
                    $idx++
                }
            }
            if ($a.ContainsKey('routes')) {
                $idx = 0
                foreach ($r in $a['routes']) {
                    if ($r -is [hashtable] -and $r.ContainsKey('when')) {
                        Add-Field "agents/$aname/routes[$idx].when" $r['when'] $RawLines $fields
                    }
                    $idx++
                }
            }
            if ($a.ContainsKey('output')) {
                foreach ($k in $a['output'].Keys) {
                    $v = $a['output'][$k]
                    if ($v -is [hashtable] -and $v.ContainsKey('value')) {
                        Add-Field "agents/$aname/output/$k.value" $v['value'] $RawLines $fields
                    } elseif ($v -is [string]) {
                        Add-Field "agents/$aname/output/$k" $v $RawLines $fields
                    }
                }
            }
            if ($a.ContainsKey('input_mapping')) {
                foreach ($k in $a['input_mapping'].Keys) {
                    Add-Field "agents/$aname/input_mapping/$k" $a['input_mapping'][$k] $RawLines $fields
                }
            }
        }
    }

    return ,$fields
}

# ── Diagnostic emission ────────────────────────────────────────────────
function Emit-Diagnostic {
    param(
        [Parameter(Mandatory)] [string] $Format,
        [Parameter(Mandatory)] [string] $RelFile,
        [Parameter(Mandatory)] [int] $Line,
        [Parameter(Mandatory)] [string] $Code,
        [Parameter(Mandatory)] [string] $Severity,
        [Parameter(Mandatory)] [string] $Message,
        [string] $Snippet
    )
    if ($Format -eq 'github') {
        $cmd = if ($Severity -eq 'error') { 'error' } else { 'warning' }
        $line = if ($Line -gt 0) { $Line } else { 1 }
        Write-Host "::${cmd} file=${RelFile},line=${line}::${Code}: ${Message}"
    } else {
        $colour = if ($Severity -eq 'error') { 'Red' } else { 'Yellow' }
        $tag = "[$($Severity.ToUpper())]"
        Write-Host "  $tag ${RelFile}:${Line} $Code  $Message" -ForegroundColor $colour
        if ($Snippet) {
            Write-Host "      in: $Snippet" -ForegroundColor DarkGray
        }
    }
}

# ── Main ───────────────────────────────────────────────────────────────
$registryPath = Resolve-RegistryPath -Explicit $RegistryPath -UseFixture:$UseFixtureRegistry
$registry = Read-Registry -Path $registryPath
$allowlist = Read-Allowlist -Path $AllowlistPath

if (-not (Test-Path -LiteralPath $WorkflowsDir)) {
    Write-Host "SKIP: workflows dir not found: $WorkflowsDir" -ForegroundColor Yellow
    exit 0
}

$yamlFiles = @(Get-ChildItem -LiteralPath $WorkflowsDir -Filter '*.yaml' -File)
if ($OnlyFile) {
    $yamlFiles = $yamlFiles | Where-Object { $_.Name -eq $OnlyFile }
}

$totalErrors = 0
$totalWarnings = 0
$totalSuppressedNonPoly = 0  # JINJA004 references skipped in non-pedantic mode.
$totalRefs = 0
$totalSuppressedAllowlist = 0

if ($Format -eq 'human') {
    Write-Host ""
    Write-Host "Jinja2 Resolver Lint" -ForegroundColor Cyan
    Write-Host "  registry:  $registryPath"
    Write-Host "  workflows: $WorkflowsDir"
    Write-Host ""
}

foreach ($file in $yamlFiles) {
    $rel = (Resolve-Path -LiteralPath $file.FullName -Relative).Replace('\', '/')
    if ($rel.StartsWith('./')) { $rel = $rel.Substring(2) }
    $rawText = Get-Content -LiteralPath $file.FullName -Raw
    $rawLines = @($rawText -split "`r?`n")
    try {
        $yaml = ConvertFrom-Yaml $rawText
    } catch {
        Write-Host "FATAL: failed to parse $rel as YAML: $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
    if ($null -eq $yaml) { continue }

    $steps = Get-StepsFromYaml -Yaml $yaml -RawLines $rawLines
    $stepLookup = @{}
    foreach ($s in $steps) { $stepLookup[$s.Name] = $s }

    $workflowName = if ($yaml.ContainsKey('workflow') -and $yaml['workflow'].ContainsKey('name')) { [string]$yaml['workflow']['name'] } else { $file.BaseName }

    $fields = Get-JinjaFields -Yaml $yaml -RawLines $rawLines

    foreach ($f in $fields) {
        $refs = Get-JinjaReferences -Text $f.Value
        foreach ($r in $refs) {
            $totalRefs++
            $diags = Resolve-Reference -Reference $r -StepLookup $stepLookup -Registry $registry -WorkflowName $workflowName
            foreach ($d in $diags) {
                $absLine = if ($f.Line -gt 0) { $f.Line + $r.LineOffset } else { 0 }
                $referenceText = "$($r.Id).output$($r.Tail)"

                # Allowlist suppression.
                if (Test-Suppressed -Entries $allowlist -RelFile $rel -Code $d.Code -Reference $referenceText) {
                    $totalSuppressedAllowlist++
                    continue
                }

                # JINJA004 default-suppress.
                if ($d.Code -eq 'JINJA004' -and -not $Pedantic) {
                    $totalSuppressedNonPoly++
                    continue
                }

                if ($d.Severity -eq 'error') { $totalErrors++ } else { $totalWarnings++ }
                Emit-Diagnostic -Format $Format -RelFile $rel -Line $absLine -Code $d.Code -Severity $d.Severity -Message $d.Message -Snippet $r.Snippet
            }
        }
    }
}

# ── Summary ────────────────────────────────────────────────────────────
if ($Format -eq 'human') {
    Write-Host ""
    Write-Host "Summary" -ForegroundColor Cyan
    Write-Host "  workflows scanned:     $($yamlFiles.Count)"
    Write-Host "  output references:     $totalRefs"
    Write-Host "  errors:                $totalErrors"
    Write-Host "  warnings:              $totalWarnings"
    Write-Host "  JINJA004 suppressed:   $totalSuppressedNonPoly  (use -Pedantic to surface)"
    Write-Host "  allowlist suppressed:  $totalSuppressedAllowlist"
    Write-Host ""
    Write-Host "Not checked by this lint (see ADR § 'What this lint does NOT catch'):" -ForegroundColor DarkGray
    Write-Host "  - control-flow availability (whether the producing step ran)" -ForegroundColor DarkGray
    Write-Host "  - sub-workflow output cross-resolution" -ForegroundColor DarkGray
    Write-Host "  - agent-step declared output: shape vs. actual emit" -ForegroundColor DarkGray
    Write-Host "  - terminal envelope conformance" -ForegroundColor DarkGray
    Write-Host ""
    if ($totalErrors -gt 0) {
        Write-Host "FAIL: $totalErrors error(s)." -ForegroundColor Red
    } elseif ($totalWarnings -gt 0 -and $FailOnWarnings) {
        Write-Host "FAIL: $totalWarnings warning(s) (-FailOnWarnings is set)." -ForegroundColor Red
    } else {
        Write-Host "PASS: no errors." -ForegroundColor Green
    }
}

if ($totalErrors -gt 0) { exit 1 }
if ($FailOnWarnings -and $totalWarnings -gt 0) { exit 1 }
exit 0
