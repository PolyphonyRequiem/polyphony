BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'load-agent-guidance.ps1'
}

Describe 'load-agent-guidance.ps1 — directory absent' {

    It 'Returns empty JSON object when agent-guidance directory does not exist' {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-test-$(Get-Random)"
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        try {
            $result = & $script:ScriptPath -ConfigPath $tempDir
            $result | Should -Be '{}'
        }
        finally {
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Returns empty JSON object when ConfigPath itself does not exist' {
        $fakePath = Join-Path ([System.IO.Path]::GetTempPath()) "nonexistent-$(Get-Random)"
        $result = & $script:ScriptPath -ConfigPath $fakePath
        $result | Should -Be '{}'
    }
}

Describe 'load-agent-guidance.ps1 — single file present' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-test-$(Get-Random)"
        $guidanceDir = Join-Path $script:TempDir 'agent-guidance'
        New-Item -ItemType Directory -Path $guidanceDir -Force | Out-Null
        Set-Content -Path (Join-Path $guidanceDir 'architect.md') -Value '# Architect Role'
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Returns JSON with the role key matching the filename' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'architect'
    }

    It 'Returns the file content as the value' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.architect | Should -BeLike '*Architect Role*'
    }
}

Describe 'load-agent-guidance.ps1 — multiple files' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-test-$(Get-Random)"
        $guidanceDir = Join-Path $script:TempDir 'agent-guidance'
        New-Item -ItemType Directory -Path $guidanceDir -Force | Out-Null
        Set-Content -Path (Join-Path $guidanceDir 'architect.md') -Value '# Architect guidance'
        Set-Content -Path (Join-Path $guidanceDir 'coder.md') -Value '# Coder guidance'
        Set-Content -Path (Join-Path $guidanceDir 'reviewer.md') -Value '# Reviewer guidance'
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Returns all three role keys' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'architect'
        $result.PSObject.Properties.Name | Should -Contain 'coder'
        $result.PSObject.Properties.Name | Should -Contain 'reviewer'
    }

    It 'Each role contains its respective content' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.architect | Should -BeLike '*Architect guidance*'
        $result.coder | Should -BeLike '*Coder guidance*'
        $result.reviewer | Should -BeLike '*Reviewer guidance*'
    }

    It 'Returns exactly three keys' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.PSObject.Properties.Name.Count | Should -Be 3
    }
}

Describe 'load-agent-guidance.ps1 — custom ConfigPath' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-custom-$(Get-Random)"
        $guidanceDir = Join-Path $script:TempDir 'agent-guidance'
        New-Item -ItemType Directory -Path $guidanceDir -Force | Out-Null
        Set-Content -Path (Join-Path $guidanceDir 'planner.md') -Value '# Planner guidance'
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Uses the custom config path to find guidance files' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'planner'
        $result.planner | Should -BeLike '*Planner guidance*'
    }
}

Describe 'load-agent-guidance.ps1 — extensible role names' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-ext-$(Get-Random)"
        $guidanceDir = Join-Path $script:TempDir 'agent-guidance'
        New-Item -ItemType Directory -Path $guidanceDir -Force | Out-Null
        Set-Content -Path (Join-Path $guidanceDir 'security-auditor.md') -Value '# Security auditor guidance'
        Set-Content -Path (Join-Path $guidanceDir 'devops-engineer.md') -Value '# DevOps engineer guidance'
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Handles arbitrary role names beyond the three core roles' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Contain 'security-auditor'
        $result.PSObject.Properties.Name | Should -Contain 'devops-engineer'
    }
}

Describe 'load-agent-guidance.ps1 — empty directory' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-empty-$(Get-Random)"
        $guidanceDir = Join-Path $script:TempDir 'agent-guidance'
        New-Item -ItemType Directory -Path $guidanceDir -Force | Out-Null
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Returns empty JSON object when directory exists but contains no .md files' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir
        $result | Should -Be '{}'
    }
}

Describe 'load-agent-guidance.ps1 — ignores non-md files' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-nonmd-$(Get-Random)"
        $guidanceDir = Join-Path $script:TempDir 'agent-guidance'
        New-Item -ItemType Directory -Path $guidanceDir -Force | Out-Null
        Set-Content -Path (Join-Path $guidanceDir 'architect.md') -Value '# Architect'
        Set-Content -Path (Join-Path $guidanceDir 'readme.txt') -Value 'Should be ignored'
        Set-Content -Path (Join-Path $guidanceDir 'config.yaml') -Value 'ignored: true'
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Only includes .md files in the output' {
        $result = & $script:ScriptPath -ConfigPath $script:TempDir | ConvertFrom-Json
        $result.PSObject.Properties.Name | Should -Be @('architect')
    }
}

Describe 'load-agent-guidance.ps1 — output is valid JSON' {

    BeforeEach {
        $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "load-ag-json-$(Get-Random)"
        $guidanceDir = Join-Path $script:TempDir 'agent-guidance'
        New-Item -ItemType Directory -Path $guidanceDir -Force | Out-Null
        Set-Content -Path (Join-Path $guidanceDir 'coder.md') -Value "# Coder`nWrite clean code."
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Outputs valid parseable JSON' {
        $raw = & $script:ScriptPath -ConfigPath $script:TempDir
        { $raw | ConvertFrom-Json } | Should -Not -Throw
    }
}
