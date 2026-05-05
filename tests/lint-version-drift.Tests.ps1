BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-version-drift.ps1'

    function Set-WorkflowFile {
        param(
            [string]$Dir,
            [string]$Name,
            [string]$Version,
            # Defaults to $Version (the bundled = self-required invariant).
            # Pass $null to omit the metadata block entirely; pass a
            # different SemVer to simulate metadata drift.
            [object]$MinPolyphonyVersion = '__default__'
        )
        if ($MinPolyphonyVersion -eq '__default__') {
            $MinPolyphonyVersion = $Version
        }
        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.AppendLine('workflow:')
        [void]$sb.AppendLine("  name: $Name")
        [void]$sb.AppendLine("  version: `"$Version`"")
        [void]$sb.AppendLine('  description: test')
        if ($null -ne $MinPolyphonyVersion) {
            [void]$sb.AppendLine('  metadata:')
            [void]$sb.AppendLine("    min_polyphony_version: `"$MinPolyphonyVersion`"")
        }
        Set-Content (Join-Path $Dir "$Name.yaml") $sb.ToString()
    }

    function Set-IndexFile {
        param([string]$Dir, [hashtable]$Entries)
        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.AppendLine('workflows:')
        foreach ($name in $Entries.Keys) {
            $entry = $Entries[$name]
            [void]$sb.AppendLine("  ${name}:")
            [void]$sb.AppendLine("    description: test")
            [void]$sb.AppendLine("    path: workflows/$name.yaml")
            $vers = ($entry.Versions | ForEach-Object { "`"$_`"" }) -join ', '
            [void]$sb.AppendLine("    versions: [$vers]")
        }
        Set-Content (Join-Path $Dir 'index.yaml') $sb.ToString()
    }

    function Invoke-Lint {
        param(
            [string]$RegistryRoot,
            [string]$RequiredVersion
        )
        $cmdArgs = @('-NoProfile', '-File', $script:LintScript, '-RegistryRoot', $RegistryRoot)
        if ($RequiredVersion) {
            $cmdArgs += @('-RequiredVersion', $RequiredVersion)
        }
        $output = pwsh @cmdArgs 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String) }
    }
}

Describe 'lint-version-drift.ps1' {

    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "lint-version-drift-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $script:RegistryRoot = Join-Path $script:TempRoot '.conductor' 'registry'
        $script:WorkflowsDir = Join-Path $script:RegistryRoot 'workflows'
        New-Item $script:WorkflowsDir -ItemType Directory -Force | Out-Null
    }

    AfterEach {
        Remove-Item $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Happy path' {

        It 'Passes when YAML version matches index last entry and all bundled aligned' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.0.0'
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'two' -Version '1.0.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0') }
                two = @{ Versions = @('1.0.0') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'aligned at version 1.0.0'
        }

        It 'Uses the LAST element of versions: [...] (append-only manifest)' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.2.3'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0', '1.1.0', '1.2.3') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'PASS.*one\.yaml.*1\.2\.3'
        }
    }

    Context 'Drift detection' {

        It 'Fails when workflow.version disagrees with index last entry' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.1.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match "workflow\.version '1\.1\.0' != index\.yaml last version '1\.0\.0'"
        }

        It 'Fails when one workflow disagrees with the bundle (bundled-SemVer invariant)' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.0.0'
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'two' -Version '1.1.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0') }
                two = @{ Versions = @('1.1.0') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'bundled-SemVer invariant broken'
        }

        It 'Fails when YAML has no workflow.version declared' {
            $noVer = @"
workflow:
  name: one
  description: missing version
"@
            Set-Content (Join-Path $script:WorkflowsDir 'one.yaml') $noVer
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{ one = @{ Versions = @('1.0.0') } }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'no workflow\.version declared'
        }

        It 'Fails when YAML has no entry in index.yaml' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'orphan-yaml' -Version '1.0.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{ }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'no entry in index\.yaml'
        }

        It 'Fails when index.yaml has entry but no YAML on disk' {
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                'orphan-index' = @{ Versions = @('1.0.0') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match "index\.yaml entry 'orphan-index' has no YAML on disk"
        }
    }

    Context 'min_polyphony_version invariant' {

        It 'Fails when YAML omits workflow.metadata.min_polyphony_version' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.0.0' -MinPolyphonyVersion $null
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{ one = @{ Versions = @('1.0.0') } }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'no workflow\.metadata\.min_polyphony_version declared'
        }

        It 'Fails when min_polyphony_version disagrees with workflow.version (bundled = self-required)' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.2.0' -MinPolyphonyVersion '1.1.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{ one = @{ Versions = @('1.2.0') } }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match "metadata\.min_polyphony_version '1\.1\.0' != workflow\.version '1\.2\.0'"
        }

        It 'Passes when min_polyphony_version matches workflow.version' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.2.3' -MinPolyphonyVersion '1.2.3'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{ one = @{ Versions = @('1.2.3') } }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'min_polyphony_version aligned'
        }
    }

    Context 'Skip behaviour' {

        It 'Skips gracefully when registry root does not exist' {
            $missing = Join-Path $script:TempRoot 'no-such-registry'
            $r = Invoke-Lint -RegistryRoot $missing
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'SKIP'
        }

        It 'Skips gracefully when no .yaml files exist in workflows/' {
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{}
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'SKIP'
        }
    }

    Context '-RequiredVersion (release-time gate)' {

        It 'Passes when shared workflow.version equals -RequiredVersion' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.0.1'
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'two' -Version '1.0.1'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0', '1.0.1') }
                two = @{ Versions = @('1.0.0', '1.0.1') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot -RequiredVersion '1.0.1'
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'matches required 1\.0\.1'
        }

        It 'Fails when shared workflow.version does not equal -RequiredVersion' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.0.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot -RequiredVersion '9.9.9'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match "does not match required version '9\.9\.9'"
        }

        It 'Without -RequiredVersion still emits the original PASS line' {
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.0.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot
            $r.ExitCode | Should -Be 0
            $r.Output | Should -Match 'aligned at version 1\.0\.0'
            $r.Output | Should -Not -Match 'required'
        }

        It 'Stops at internal-drift failures before checking -RequiredVersion' {
            # If internal invariants 1-5 fail, the script must exit before
            # the -RequiredVersion check — otherwise we could see a
            # confusing "matches required" line on top of a real failure.
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'one' -Version '1.0.0'
            Set-WorkflowFile -Dir $script:WorkflowsDir -Name 'two' -Version '1.1.0'
            Set-IndexFile -Dir $script:RegistryRoot -Entries @{
                one = @{ Versions = @('1.0.0') }
                two = @{ Versions = @('1.1.0') }
            }
            $r = Invoke-Lint -RegistryRoot $script:RegistryRoot -RequiredVersion '1.0.0'
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'bundled-SemVer invariant broken'
            $r.Output | Should -Not -Match 'matches required'
        }
    }
}
