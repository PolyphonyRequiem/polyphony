BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $script:LintScript = Join-Path $PSScriptRoot 'lint-vocabulary.ps1'
    $script:FixtureRoot = Join-Path $script:RepoRoot 'tests\fixtures\lint-vocabulary'
    $script:RealGlossary = Join-Path $script:RepoRoot 'docs\glossary.md'

    $script:FixtureDestinations = @{
        'clean-workflow.yaml'          = '.conductor/registry/workflows/clean-workflow.yaml'
        'dirty-apex.yaml'             = '.conductor/registry/workflows/dirty-apex.yaml'
        'dirty-multiple-terms.cs'     = 'src/Polyphony/DirtyMultipleTerms.cs'
        'dirty-script.ps1'            = 'scripts/dirty-script.ps1'
        'clean-doc-with-code-fence.md' = 'docs/clean-doc-with-code-fence.md'
        'dirty-doc-with-code-fence.md' = 'docs/dirty-doc-with-code-fence.md'
    }

    function script:Write-RepoFile {
        param(
            [Parameter(Mandatory)] [string]$Root,
            [Parameter(Mandatory)] [string]$RelativePath,
            [Parameter(Mandatory)] [string]$Content
        )

        $path = Join-Path $Root $RelativePath
        $parent = Split-Path $path -Parent
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Set-Content -Path $path -Value $Content -Encoding utf8
        return $path
    }

    function script:New-TestRepo {
        param(
            [ValidateSet('real', 'missing', 'custom')]
            [string]$GlossaryMode = 'real',
            [string]$GlossaryContent,
            [string[]]$Fixtures = @(),
            [hashtable]$Files = @{}
        )

        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root -Force | Out-Null

        switch ($GlossaryMode) {
            'real' {
                $docsDir = Join-Path $root 'docs'
                New-Item -ItemType Directory -Path $docsDir -Force | Out-Null
                Copy-Item -LiteralPath $script:RealGlossary -Destination (Join-Path $docsDir 'glossary.md')
            }
            'custom' {
                Write-RepoFile -Root $root -RelativePath 'docs/glossary.md' -Content $GlossaryContent | Out-Null
            }
            'missing' {
            }
        }

        foreach ($fixture in $Fixtures) {
            $destination = $script:FixtureDestinations[$fixture]
            if (-not $destination) {
                throw "No fixture destination mapping found for '$fixture'."
            }

            $targetPath = Join-Path $root $destination
            $targetParent = Split-Path $targetPath -Parent
            if (-not (Test-Path -LiteralPath $targetParent)) {
                New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
            }

            Copy-Item -LiteralPath (Join-Path $script:FixtureRoot $fixture) -Destination $targetPath
        }

        foreach ($relativePath in $Files.Keys) {
            Write-RepoFile -Root $root -RelativePath $relativePath -Content $Files[$relativePath] | Out-Null
        }

        return $root
    }

    function script:Invoke-Lint {
        param(
            [Parameter(Mandatory)] [string]$Root,
            [string[]]$Arguments = @()
        )

        $output = & pwsh -NoProfile -File $script:LintScript -Root $Root @Arguments 2>&1
        return [PSCustomObject]@{
            ExitCode = $LASTEXITCODE
            Output   = ($output -join "`n")
        }
    }

    function script:Get-CurrentForbiddenTerms {
        $lines = Get-Content -LiteralPath $script:RealGlossary
        $headerIndex = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*>\s*\*\*Forbidden terms\*\*') {
                $headerIndex = $i
                break
            }
        }

        if ($headerIndex -lt 0) {
            throw 'Unable to find forbidden terms heading in docs/glossary.md.'
        }

        $terms = [System.Collections.Generic.List[string]]::new()
        for ($i = $headerIndex + 1; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -notmatch '^\s*>') { break }
            if ($line -match '^\s*>\s*-\s*(?<text>.+)$') {
                $text = $Matches['text']
                if ($text -match '^(?<left>.+?)(?:\s+—\s+|\s+--\s+)(?<right>.+)$') {
                    $termHead = ($Matches['left'] -split '\s*\(')[0]
                    foreach ($match in [regex]::Matches($termHead, '`([^`]+)`')) {
                        $terms.Add($match.Groups[1].Value)
                    }
                }
            }
        }

        return @($terms)
    }

    function script:Get-SampleContentForTerm {
        param([Parameter(Mandatory)] [string]$Term)

        switch ($Term) {
            'primary_*' { return '$x = "primary_completer"' }
            'Primary*' { return '$x = "PrimaryId"' }
            'terminal_*' { return '$x = "terminal_abort_run"' }
            '*_dispatch' { return '$x = "plan_level_dispatch"' }
            default { return ('$x = ''{0}''' -f $Term) }
        }
    }

    $script:CurrentForbiddenTerms = Get-CurrentForbiddenTerms
}

Describe 'lint-vocabulary.ps1' {

    It 'passes on a clean repo with clean fixtures only' {
        $repo = New-TestRepo -Fixtures @('clean-workflow.yaml', 'clean-doc-with-code-fence.md')

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match '\[OK\]'
        $result.Output | Should -Not -Match 'forbidden term'
    }

    It 'detects each forbidden glossary term once in a single-line file' {
        $repo = New-TestRepo
        $counter = 0
        foreach ($term in $script:CurrentForbiddenTerms) {
            $relativePath = ('scripts/term-{0:d2}.ps1' -f $counter)
            Write-RepoFile -Root $repo -RelativePath $relativePath -Content (Get-SampleContentForTerm -Term $term) | Out-Null
            $counter += 1
        }

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        $payload.violation_count | Should -Be $script:CurrentForbiddenTerms.Count
        foreach ($term in $script:CurrentForbiddenTerms) {
            @($payload.violations | Where-Object term -eq $term).Count | Should -Be 1
        }
    }

    It 'allows root_completer even though root_* is the canonical replacement' {
        $repo = New-TestRepo -Files @{
            'scripts/root-completer.ps1' = '$x = "root_completer"'
        }

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 0
    }

    It 'does not match the plural English word apexes' {
        $repo = New-TestRepo -Files @{
            'docs/plurals.md' = 'Historical note about apexes only.'
        }

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 0
    }

    It 'detects APEX and Apex case-insensitively' {
        $repo = New-TestRepo -Files @{
            'scripts/case.ps1' = @'
$upper = "APEX"
$title = "Apex"
'@
        }

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        $payload.violation_count | Should -Be 2
        @($payload.violations | Where-Object term -eq 'apex').Count | Should -Be 2
    }

    It 'flags pure-alpha terms as PascalCase suffix (ResetApex, EdgeGraphWave, PlanCascade)' {
        # Mirror to the existing PascalCase-prefix coverage (ApexId, RootDriver).
        # Without this, the rename script cannot see suffix usages either — see
        # the AB#3259 incident note in `New-ForbiddenTermSpec`.
        $repo = New-TestRepo -Files @{
            'src/Polyphony/Suffix.cs' = @'
public sealed class Suffix {
    public int ResetApex { get; set; }
    public string EdgeGraphWave { get; set; } = "";
    public string PlanCascade { get; set; } = "";
}
'@
        }

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        @($payload.violations | Where-Object term -eq 'apex').Count | Should -BeGreaterOrEqual 1
        @($payload.violations | Where-Object term -eq 'wave').Count | Should -BeGreaterOrEqual 1
        @($payload.violations | Where-Object term -eq 'cascade').Count | Should -BeGreaterOrEqual 1
    }

    It 'does not flag PascalCase suffix when the preceding boundary is non-lowercase' {
        # `XApex` (uppercase X before Apex) is ambiguous and not a legal
        # PascalCase compound shape; we only catch lowercase-before-uppercase
        # boundaries. Also: `apexes` (plural) and bare `apex` already covered
        # by other tests but reasserted here for completeness.
        $repo = New-TestRepo -Files @{
            'docs/notes.md' = @'
Historical reference to XApex pattern (not a real identifier).
Plural form apexes is also not flagged.
'@
        }

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 0
    }

    It 'flags <prefix>_* only at start of identifier (token-prefix boundary)' {
        # primary_completer + terminal_abort_run at start of identifier → flagged.
        # something_primary_blah and a_terminal_state (mid-identifier) → NOT flagged.
        $repo = New-TestRepo -Files @{
            'scripts/boundaries.ps1' = @'
$a = "primary_completer"
$b = "terminal_abort_run"
$c = "something_primary_blah"
$d = "a_terminal_state"
'@
        }

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        $payload.violation_count | Should -Be 2
        @($payload.violations | Where-Object term -eq 'primary_*').Count | Should -Be 1
        @($payload.violations | Where-Object term -eq 'terminal_*').Count | Should -Be 1
    }

    It 'flags Primary* in PascalCase compounds (case-sensitive, identifier start)' {
        # PrimaryId, PrimaryRouter at start of PascalCase compound → flagged
        # (mirrors how `apex` rule catches `ApexId`). aPrimaryThing not flagged
        # (mid-identifier). $primaryThing (lowercase 'p') not flagged either —
        # snake_case is policed by the separate `primary_*` rule.
        $repo = New-TestRepo -Files @{
            'src/Polyphony/Boundaries.cs' = @'
public sealed class Boundaries {
    public int PrimaryId { get; set; }
    public string PrimaryRouter { get; set; } = "";
    public string aPrimaryThing { get; set; } = "";
    public string primary { get; set; } = "";
}
'@
        }

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        $payload.violation_count | Should -Be 2
        @($payload.violations | Where-Object term -eq 'Primary*').Count | Should -Be 2
    }

    It 'does not flag terminalKinds or similar lifecycle vocab (terminal_* is snake_case only)' {
        # The glossary `terminal_*` rule polices a workflow-node-name prefix
        # convention. PowerShell variables that use "terminal" as an English
        # word for end-of-lifecycle (`$terminalKinds`, `$hasTerminalReady`)
        # are semantically distinct and must NOT be flagged.
        $repo = New-TestRepo -Files @{
            'scripts/lifecycle.ps1' = @'
$terminalKinds = @('item_satisfied')
$hasTerminalReady = $true
$terminalDecisions = @()
'@
        }

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 0
    }

    It 'flags *_dispatch only at end of identifier (token-suffix boundary)' {
        # plan_level_dispatch + actionable_dispatch at end of identifier → flagged.
        # items_dispatched_count, has_dispatch_failures, dispatched_items (mid-identifier
        # or no leading underscore) → NOT flagged.
        $repo = New-TestRepo -Files @{
            'scripts/boundaries.ps1' = @'
$a = "plan_level_dispatch"
$b = "actionable_dispatch"
$c = "items_dispatched_count"
$d = "has_dispatch_failures"
$e = "dispatched_items"
'@
        }

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        $payload.violation_count | Should -Be 2
        @($payload.violations | Where-Object term -eq '*_dispatch').Count | Should -Be 2
    }

    It 'skips docs/glossary.md itself even though it contains forbidden terms' {
        $repo = New-TestRepo

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match '\[OK\]'
    }

    It 'skips forbidden terms inside text fences but not inside yaml fences' {
        $repo = New-TestRepo -Fixtures @('clean-doc-with-code-fence.md', 'dirty-doc-with-code-fence.md')

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        $payload.violation_count | Should -Be 1
        $payload.violations[0].file | Should -Be 'docs/dirty-doc-with-code-fence.md'
    }

    It 'returns exit code 1 when forbidden terms are found' {
        $repo = New-TestRepo -Fixtures @('dirty-apex.yaml')

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'dirty-apex\.yaml:4:'
    }

    It 'returns exit code 2 with a helpful error when the glossary is missing' {
        $repo = New-TestRepo -GlossaryMode missing -Fixtures @('clean-workflow.yaml')

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 2
        $result.Output | Should -Match 'configuration error'
        $result.Output | Should -Match 'docs\\glossary\.md|docs/glossary\.md'
    }

    It 'returns exit code 2 with a helpful error when the glossary format changes unexpectedly' {
        $repo = New-TestRepo -GlossaryMode custom -GlossaryContent @'
# Polyphony glossary

> **Forbidden terms**
> apex - use root
'@

        $result = Invoke-Lint -Root $repo

        $result.ExitCode | Should -Be 2
        $result.Output | Should -Match 'Malformed forbidden-terms bullet|Expected at least one backticked term|Expected one or more blockquoted bullet lines'
    }

    It 'emits parseable JSON with the expected shape' {
        $repo = New-TestRepo -Fixtures @('dirty-apex.yaml')

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        $payload.tool | Should -Be 'lint-vocabulary'
        $payload.root | Should -Be $repo
        $payload.glossary_path | Should -Be 'docs/glossary.md'
        $payload.scanned_files | Should -BeGreaterThan 0
        $payload.violation_count | Should -Be 2
        $payload.warning_count | Should -Be 0
        $payload.violations[0].PSObject.Properties.Name | Should -Contain 'file'
        $payload.violations[0].PSObject.Properties.Name | Should -Contain 'term'
        $payload.violations[0].PSObject.Properties.Name | Should -Contain 'replacement'
        @($payload.term_counts).Count | Should -BeGreaterThan 0
    }

    It 'scans fixture files across workflows, scripts, C#, and docs' {
        $repo = New-TestRepo -Fixtures @(
            'dirty-apex.yaml',
            'dirty-multiple-terms.cs',
            'dirty-script.ps1',
            'dirty-doc-with-code-fence.md'
        )

        $result = Invoke-Lint -Root $repo -Arguments @('-OutputFormat', 'json')

        $result.ExitCode | Should -Be 1
        $payload = $result.Output | ConvertFrom-Json
        @($payload.violations | Select-Object -ExpandProperty file -Unique) | Should -Contain '.conductor/registry/workflows/dirty-apex.yaml'
        @($payload.violations | Select-Object -ExpandProperty file -Unique) | Should -Contain 'src/Polyphony/DirtyMultipleTerms.cs'
        @($payload.violations | Select-Object -ExpandProperty file -Unique) | Should -Contain 'scripts/dirty-script.ps1'
        @($payload.violations | Select-Object -ExpandProperty file -Unique) | Should -Contain 'docs/dirty-doc-with-code-fence.md'
    }
}
