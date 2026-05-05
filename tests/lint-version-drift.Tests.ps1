BeforeAll {
    $script:LintScript = Join-Path $PSScriptRoot 'lint-version-drift.ps1'

    function Set-WorkflowFile {
        param([string]$Dir, [string]$Name, [string]$Version)
        $content = @"
workflow:
  name: $Name
  version: "$Version"
  description: test
"@
        Set-Content (Join-Path $Dir "$Name.yaml") $content
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
        param([string]$RegistryRoot)
        $output = pwsh -NoProfile -File $script:LintScript -RegistryRoot $RegistryRoot 2>&1
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
}
