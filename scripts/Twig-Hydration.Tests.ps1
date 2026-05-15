Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

BeforeAll {
    . (Join-Path $PSScriptRoot 'Twig-Hydration.ps1')

    function New-TempDir {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "twighyd-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        return $tmp
    }

    function Write-File {
        param([string]$Path, [string]$Content = '')
        $dir = Split-Path -Parent $Path
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Set-Content -LiteralPath $Path -Value $Content -NoNewline
    }
}

Describe 'Copy-MissingTwigEntries — recursive missing-only semantics' {

    It 'Copies a missing top-level file from source to destination' {
        $src = New-TempDir
        $dst = New-TempDir
        Write-File (Join-Path $src 'prompt.json') '{"item":"5"}'

        Copy-MissingTwigEntries -SourceRoot $src -DestinationRoot $dst

        Test-Path (Join-Path $dst 'prompt.json') -PathType Leaf | Should -BeTrue
        Get-Content (Join-Path $dst 'prompt.json') -Raw | Should -Be '{"item":"5"}'
    }

    It 'Copies a whole missing directory subtree from source' {
        $src = New-TempDir
        $dst = New-TempDir
        Write-File (Join-Path $src 'org/proj/twig.db') 'binary-blob'
        Write-File (Join-Path $src 'org/proj/twig.db-wal') 'wal'

        Copy-MissingTwigEntries -SourceRoot $src -DestinationRoot $dst

        Test-Path (Join-Path $dst 'org/proj/twig.db')     -PathType Leaf | Should -BeTrue
        Test-Path (Join-Path $dst 'org/proj/twig.db-wal') -PathType Leaf | Should -BeTrue
        Get-Content (Join-Path $dst 'org/proj/twig.db') -Raw | Should -Be 'binary-blob'
    }

    It 'Does NOT overwrite existing destination files' {
        $src = New-TempDir
        $dst = New-TempDir
        Write-File (Join-Path $src 'org/proj/twig.db') 'main-version'
        Write-File (Join-Path $dst 'org/proj/twig.db') 'apex-version'

        Copy-MissingTwigEntries -SourceRoot $src -DestinationRoot $dst

        Get-Content (Join-Path $dst 'org/proj/twig.db') -Raw | Should -Be 'apex-version'
    }

    It 'Recursively fills missing leaves inside a partially-existing directory' {
        # Bug case from cloudvault §0: apex has .twig/<org>/ from a prior run
        # but the workspace DB is missing inside it.
        $src = New-TempDir
        $dst = New-TempDir
        Write-File (Join-Path $src 'org/proj/twig.db') 'fresh-db'
        Write-File (Join-Path $src 'org/proj/sibling') 'sibling-content'
        # Apex already has an empty <org>/<proj>/ directory
        New-Item -ItemType Directory -Path (Join-Path $dst 'org/proj') -Force | Out-Null

        Copy-MissingTwigEntries -SourceRoot $src -DestinationRoot $dst

        Test-Path (Join-Path $dst 'org/proj/twig.db') -PathType Leaf | Should -BeTrue
        Get-Content (Join-Path $dst 'org/proj/twig.db') -Raw | Should -Be 'fresh-db'
        Get-Content (Join-Path $dst 'org/proj/sibling') -Raw | Should -Be 'sibling-content'
    }

    It 'Skips ExcludeAtRoot entries at the top level only' {
        $src = New-TempDir
        $dst = New-TempDir
        Write-File (Join-Path $src 'config') 'main-config'
        Write-File (Join-Path $src 'org/proj/config') 'nested-config-not-skipped'
        Write-File (Join-Path $dst 'config') 'apex-config'

        Copy-MissingTwigEntries -SourceRoot $src -DestinationRoot $dst -ExcludeAtRoot @('config')

        Get-Content (Join-Path $dst 'config') -Raw | Should -Be 'apex-config'
        # Nested file named 'config' should still be copied (rule applies at root only).
        Test-Path (Join-Path $dst 'org/proj/config') -PathType Leaf | Should -BeTrue
        Get-Content (Join-Path $dst 'org/proj/config') -Raw | Should -Be 'nested-config-not-skipped'
    }

    It 'Returns silently when source does not exist' {
        $dst = New-TempDir
        { Copy-MissingTwigEntries -SourceRoot (Join-Path $dst 'nonexistent-source') -DestinationRoot $dst } |
            Should -Not -Throw
    }

    It 'Creates destination directory if it does not exist' {
        $src = New-TempDir
        $tmpRoot = New-TempDir
        $dst = Join-Path $tmpRoot 'will-be-created'
        Write-File (Join-Path $src 'prompt.json') 'x'

        Copy-MissingTwigEntries -SourceRoot $src -DestinationRoot $dst

        Test-Path $dst -PathType Container | Should -BeTrue
        Test-Path (Join-Path $dst 'prompt.json') -PathType Leaf | Should -BeTrue
    }
}

Describe 'Assert-ApexTwigWorkspace — fail-fast invariant' {

    It 'Returns silently when the workspace DB is present' {
        $apexTwig = New-TempDir
        Write-File (Join-Path $apexTwig 'microsoft/OS/twig.db') 'real-db'

        { Assert-ApexTwigWorkspace `
            -ApexTwigDir $apexTwig `
            -Organization 'microsoft' `
            -Project 'OS' `
            -ApexId 12345 `
            -MainWorktree 'C:\fake\main'
        } | Should -Not -Throw
    }

    It 'Throws with operator remediation when the workspace DB is missing' {
        $apexTwig = New-TempDir
        # No DB at all.

        { Assert-ApexTwigWorkspace `
            -ApexTwigDir $apexTwig `
            -Organization 'microsoft' `
            -Project 'OS' `
            -ApexId 12345 `
            -MainWorktree 'C:\fake\main'
        } | Should -Throw -ExpectedMessage '*Apex twig workspace is missing its DB*'
    }

    It 'Throws when only the parent directory exists but DB itself is missing (cloudvault §0 case)' {
        $apexTwig = New-TempDir
        New-Item -ItemType Directory -Path (Join-Path $apexTwig 'microsoft/OS') -Force | Out-Null

        { Assert-ApexTwigWorkspace `
            -ApexTwigDir $apexTwig `
            -Organization 'microsoft' `
            -Project 'OS' `
            -ApexId 12345 `
            -MainWorktree 'C:\fake\main'
        } | Should -Throw -ExpectedMessage '*missing*twig.db*'
    }

    It 'Surfaces ApexId, Organization, Project, MainWorktree in the remediation message' {
        $apexTwig = New-TempDir

        try {
            Assert-ApexTwigWorkspace `
                -ApexTwigDir $apexTwig `
                -Organization 'cv-org' `
                -Project 'CV-Proj' `
                -ApexId 62286666 `
                -MainWorktree 'C:\projects\cv\main'
            throw 'expected throw'
        } catch {
            $_.Exception.Message | Should -Match 'cv-org/CV-Proj'
            $_.Exception.Message | Should -Match '62286666'
            $_.Exception.Message | Should -Match 'C:\\projects\\cv\\main'
            $_.Exception.Message | Should -Match 'twig init cv-org CV-Proj'
            $_.Exception.Message | Should -Match 'twig set 62286666'
        }
    }
}
