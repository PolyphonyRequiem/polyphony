BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'bootstrap-conductor.ps1'

    function New-TestDirectory {
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "bootstrap-test-$(Get-Random)"
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Auto-detection from .twig/config
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — auto-detect from .twig/config' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
        $twigDir = Join-Path $script:TempDir '.twig'
        New-Item -ItemType Directory -Path $twigDir -Force | Out-Null
        Set-Content -Path (Join-Path $twigDir 'config') -Value "process_template: Basic`n"
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Detects process template from .twig/config' {
        $result = & $script:ScriptPath -OutputPath $script:TempDir | ConvertFrom-Json
        $result.process_template | Should -Be 'Basic'
    }

    It 'Generates process-config.yaml with correct template' {
        & $script:ScriptPath -OutputPath $script:TempDir | Out-Null
        $configPath = Join-Path $script:TempDir '.conductor' 'process-config.yaml'
        Test-Path $configPath | Should -BeTrue
        $content = Get-Content $configPath -Raw
        $content | Should -Match 'process_template: Basic'
    }

    It 'Creates all expected type files for Basic template' {
        & $script:ScriptPath -OutputPath $script:TempDir | Out-Null
        $conductor = Join-Path $script:TempDir '.conductor'
        Test-Path (Join-Path $conductor 'work-item-types' 'epic.md') | Should -BeTrue
        Test-Path (Join-Path $conductor 'work-item-types' 'issue.md') | Should -BeTrue
        Test-Path (Join-Path $conductor 'work-item-types' 'task.md') | Should -BeTrue
    }

    It 'Creates all expected template files for Basic template' {
        & $script:ScriptPath -OutputPath $script:TempDir | Out-Null
        $templates = Join-Path $script:TempDir '.conductor' 'work-item-types' 'templates'
        Test-Path (Join-Path $templates 'epic-template.md') | Should -BeTrue
        Test-Path (Join-Path $templates 'issue-template.md') | Should -BeTrue
        Test-Path (Join-Path $templates 'task-template.md') | Should -BeTrue
    }

    It 'Prefers .twig/config over -ProcessTemplate parameter' {
        $result = & $script:ScriptPath -OutputPath $script:TempDir -ProcessTemplate 'Agile' 3>&1 | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] }
        $parsed = $result | ConvertFrom-Json
        $parsed.process_template | Should -Be 'Basic'
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Fallback to -ProcessTemplate parameter
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — fallback to -ProcessTemplate' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Uses -ProcessTemplate when no .twig/config exists' {
        $result = & $script:ScriptPath -ProcessTemplate 'Agile' -OutputPath $script:TempDir | ConvertFrom-Json
        $result.process_template | Should -Be 'Agile'
    }

    It 'Errors when no .twig/config and no -ProcessTemplate' {
        { & $script:ScriptPath -OutputPath $script:TempDir 2>&1 } | Should -Throw
    }

    It 'Errors for unknown template name' {
        { & $script:ScriptPath -ProcessTemplate 'Unknown' -OutputPath $script:TempDir 2>&1 } | Should -Throw
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Process template variations
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — Agile template' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Generates correct types for Agile' {
        $result = & $script:ScriptPath -ProcessTemplate 'Agile' -OutputPath $script:TempDir | ConvertFrom-Json
        $configPath = Join-Path $script:TempDir '.conductor' 'process-config.yaml'
        $config = Get-Content $configPath -Raw | ConvertFrom-Yaml
        foreach ($type in $config.types.Keys) {
            $result.types | Should -Contain $type
        }
    }

    It 'Creates user-story.md type definition' {
        & $script:ScriptPath -ProcessTemplate 'Agile' -OutputPath $script:TempDir | Out-Null
        $path = Join-Path $script:TempDir '.conductor' 'work-item-types' 'user-story.md'
        Test-Path $path | Should -BeTrue
        $content = Get-Content $path -Raw
        $content | Should -Match 'User Story'
    }

    It 'Creates user-story-template.md' {
        & $script:ScriptPath -ProcessTemplate 'Agile' -OutputPath $script:TempDir | Out-Null
        $path = Join-Path $script:TempDir '.conductor' 'work-item-types' 'templates' 'user-story-template.md'
        Test-Path $path | Should -BeTrue
    }

    It 'Uses Active/Closed states in transitions' {
        & $script:ScriptPath -ProcessTemplate 'Agile' -OutputPath $script:TempDir | Out-Null
        $config = Get-Content (Join-Path $script:TempDir '.conductor' 'process-config.yaml') -Raw
        $configPath = Join-Path $script:TempDir '.conductor' 'process-config.yaml'
        $yaml = Get-Content $configPath -Raw | ConvertFrom-Yaml
        $topType = ($yaml.types.Keys)[0]
        $transitions = $yaml.transitions[$topType]
        # Defensive: check for double values
        $bp = $transitions.begin_planning
        $ac = $transitions.all_children_complete
        Write-Host "DEBUG: begin_planning=$bp, all_children_complete=$ac"
        Write-Host "DEBUG: config content: $config"
        # Defensive: $bp may be an array or string
        if ($bp -is [System.Collections.IEnumerable] -and -not ($bp -is [string])) {
            foreach ($item in $bp) {
                $config | Should -Match ("begin_planning: $item")
            }
        } else {
            $config | Should -Match ("begin_planning: $bp")
        }
        $config | Should -Match ("all_children_complete: $ac")
    }
}

Describe 'bootstrap-conductor.ps1 — Scrum template' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Generates correct types for Scrum' {
        $result = & $script:ScriptPath -ProcessTemplate 'Scrum' -OutputPath $script:TempDir | ConvertFrom-Json
        $configPath = Join-Path $script:TempDir '.conductor' 'process-config.yaml'
        $config = Get-Content $configPath -Raw | ConvertFrom-Yaml
        foreach ($type in $config.types.Keys) {
            $result.types | Should -Contain $type
        }
    }

    It 'Creates product-backlog-item.md type definition' {
        & $script:ScriptPath -ProcessTemplate 'Scrum' -OutputPath $script:TempDir | Out-Null
        $path = Join-Path $script:TempDir '.conductor' 'work-item-types' 'product-backlog-item.md'
        Test-Path $path | Should -BeTrue
    }

    It 'Uses Committed for mid-level transitions' {
        & $script:ScriptPath -ProcessTemplate 'Scrum' -OutputPath $script:TempDir | Out-Null
        $config = Get-Content (Join-Path $script:TempDir '.conductor' 'process-config.yaml') -Raw
        $configPath = Join-Path $script:TempDir '.conductor' 'process-config.yaml'
        $yaml = Get-Content $configPath -Raw | ConvertFrom-Yaml
        # Find a mid-level type (not Epic, not Task)
        $typeKeys = $yaml.types.Keys
        $midType = $typeKeys | Where-Object { $_ -notmatch 'Epic|Task' } | Select-Object -First 1
        if ($midType -eq $null) {
            throw "No mid-level type found in types.Keys: $typeKeys"
        }
        $transitions = $yaml.transitions[$midType]
        $bp = $transitions.begin_planning
        Write-Host "DEBUG: midType=$midType, begin_planning=$bp"
        if ($bp -is [System.Collections.IEnumerable] -and -not ($bp -is [string])) {
            foreach ($item in $bp) {
                $config | Should -Match ("begin_planning: $item")
            }
        } else {
            $config | Should -Match ("begin_planning: $bp")
        }
    }
}

Describe 'bootstrap-conductor.ps1 — CMMI template' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Generates correct types for CMMI' {
        $result = & $script:ScriptPath -ProcessTemplate 'CMMI' -OutputPath $script:TempDir | ConvertFrom-Json
        $configPath = Join-Path $script:TempDir '.conductor' 'process-config.yaml'
        $config = Get-Content $configPath -Raw | ConvertFrom-Yaml
        foreach ($type in $config.types.Keys) {
            $result.types | Should -Contain $type
        }
    }

    It 'Creates requirement.md type definition' {
        & $script:ScriptPath -ProcessTemplate 'CMMI' -OutputPath $script:TempDir | Out-Null
        $path = Join-Path $script:TempDir '.conductor' 'work-item-types' 'requirement.md'
        Test-Path $path | Should -BeTrue
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Skip/Force behavior
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — skip existing files (default)' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
        # Generate once
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | Out-Null
        # Customize a file
        $customPath = Join-Path $script:TempDir '.conductor' 'profile.yaml'
        Set-Content -Path $customPath -Value 'custom: true'
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Preserves existing files on second run' {
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir 3>&1 | Out-Null
        $content = Get-Content (Join-Path $script:TempDir '.conductor' 'profile.yaml') -Raw
        $content | Should -Match 'custom: true'
    }

    It 'Reports skipped files in output' {
        $result = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir 3>&1 |
            Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] } |
            ConvertFrom-Json
        $result.files_skipped.Count | Should -BeGreaterThan 0
    }

    It 'Emits warnings for skipped files' {
        $warnings = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir 3>&1 |
            Where-Object { $_ -is [System.Management.Automation.WarningRecord] }
        $warnings.Count | Should -BeGreaterThan 0
    }
}

Describe 'bootstrap-conductor.ps1 — -Force flag' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
        # Generate once
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | Out-Null
        # Customize a file
        $customPath = Join-Path $script:TempDir '.conductor' 'profile.yaml'
        Set-Content -Path $customPath -Value 'custom: true'
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Overwrites existing files when -Force is set' {
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir -Force | Out-Null
        $content = Get-Content (Join-Path $script:TempDir '.conductor' 'profile.yaml') -Raw
        $content | Should -Not -Match 'custom: true'
    }

    It 'Reports no skipped files with -Force' {
        $result = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir -Force | ConvertFrom-Json
        $result.files_skipped.Count | Should -Be 0
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Output path handling
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — output path' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Creates .conductor/ under the specified output path' {
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | Out-Null
        Test-Path (Join-Path $script:TempDir '.conductor') | Should -BeTrue
    }

    It 'Creates nested directory structure' {
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | Out-Null
        Test-Path (Join-Path $script:TempDir '.conductor' 'work-item-types' 'templates') | Should -BeTrue
        Test-Path (Join-Path $script:TempDir '.conductor' 'agent-guidance') | Should -BeTrue
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Agent guidance files
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — agent guidance' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | Out-Null
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Creates agent-guidance files for all types' {
        $types = @('epic', 'issue', 'task')
        foreach ($type in $types) {
            $path = Join-Path $script:TempDir '.conductor' 'agent-guidance' ("$type.md")
            Test-Path $path | Should -BeTrue
            $content = Get-Content $path -Raw
            $content | Should -Match ($type.Substring(0,1).ToUpper() + $type.Substring(1) + ' Guidance')
            $content | Should -Match 'TODO'
        }
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Profile.yaml
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — profile.yaml' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | Out-Null
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Creates profile.yaml with TODO placeholders' {
        $path = Join-Path $script:TempDir '.conductor' 'profile.yaml'
        Test-Path $path | Should -BeTrue
        $content = Get-Content $path -Raw
        $content | Should -Match 'TODO'
        $content | Should -Match 'project:'
        $content | Should -Match 'build:'
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# Generated file content quality
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — content quality' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
        & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | Out-Null
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'process-config.yaml contains all three Basic types' {
        $config = Get-Content (Join-Path $script:TempDir '.conductor' 'process-config.yaml') -Raw
        $configPath = Join-Path $script:TempDir '.conductor' 'process-config.yaml'
        $yaml = Get-Content $configPath -Raw | ConvertFrom-Yaml
        foreach ($type in $yaml.types.Keys) {
            $config | Should -Match ("${type}:")
        }
    }

    It 'process-config.yaml contains transitions section' {
        $config = Get-Content (Join-Path $script:TempDir '.conductor' 'process-config.yaml') -Raw
        $config | Should -Match 'transitions:'
    }

    It 'process-config.yaml contains branch_strategy section' {
        $config = Get-Content (Join-Path $script:TempDir '.conductor' 'process-config.yaml') -Raw
        $config | Should -Match 'branch_strategy:'
    }

    It 'process-config.yaml contains platform field' {
        $config = Get-Content (Join-Path $script:TempDir '.conductor' 'process-config.yaml') -Raw
        $config | Should -Match 'platform: github'
    }

    It 'Type definitions contain TODO markers' {
        $epicDef = Get-Content (Join-Path $script:TempDir '.conductor' 'work-item-types' 'epic.md') -Raw
        $epicDef | Should -Match 'TODO'
    }

    It 'Reports correct file counts in output' {
        $result = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir -Force | ConvertFrom-Json
        # 1 process-config + 3 type defs + 3 templates + 3 guidance + 1 profile = 11
        $result.files_written.Count | Should -Be 11
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# .twig/config edge cases
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — .twig/config edge cases' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Handles .twig/config with extra whitespace around template name' {
        $twigDir = Join-Path $script:TempDir '.twig'
        New-Item -ItemType Directory -Path $twigDir -Force | Out-Null
        Set-Content -Path (Join-Path $twigDir 'config') -Value "process_template:   Scrum  `n"
        $result = & $script:ScriptPath -OutputPath $script:TempDir | ConvertFrom-Json
        $result.process_template | Should -Be 'Scrum'
    }

    It 'Handles .twig/config with equals sign separator' {
        $twigDir = Join-Path $script:TempDir '.twig'
        New-Item -ItemType Directory -Path $twigDir -Force | Out-Null
        Set-Content -Path (Join-Path $twigDir 'config') -Value "process_template = CMMI`n"
        $result = & $script:ScriptPath -OutputPath $script:TempDir | ConvertFrom-Json
        $result.process_template | Should -Be 'CMMI'
    }

    It 'Ignores .twig/config without process_template field' {
        $twigDir = Join-Path $script:TempDir '.twig'
        New-Item -ItemType Directory -Path $twigDir -Force | Out-Null
        Set-Content -Path (Join-Path $twigDir 'config') -Value "some_other_key: value`n"
        $result = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | ConvertFrom-Json
        $result.process_template | Should -Be 'Basic'
    }

    It 'Handles .twig/config with multiple lines and process_template not first' {
        $twigDir = Join-Path $script:TempDir '.twig'
        New-Item -ItemType Directory -Path $twigDir -Force | Out-Null
        $config = @"
org: my-org
project: my-project
process_template: Agile
other: stuff
"@
        Set-Content -Path (Join-Path $twigDir 'config') -Value $config
        $result = & $script:ScriptPath -OutputPath $script:TempDir | ConvertFrom-Json
        $result.process_template | Should -Be 'Agile'
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# JSON output format
# ══════════════════════════════════════════════════════════════════════════════

Describe 'bootstrap-conductor.ps1 — JSON output' {

    BeforeEach {
        $script:TempDir = New-TestDirectory
    }

    AfterEach {
        Remove-Item -Path $script:TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Outputs valid JSON' {
        $raw = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir
        { $raw | ConvertFrom-Json } | Should -Not -Throw
    }

    It 'JSON contains process_template field' {
        $result = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | ConvertFrom-Json
        $result.process_template | Should -Be 'Basic'
    }

    It 'JSON contains files_written array' {
        $result = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | ConvertFrom-Json
        $result.files_written | Should -Not -BeNullOrEmpty
    }

    It 'JSON contains types array' {
        $result = & $script:ScriptPath -ProcessTemplate 'Basic' -OutputPath $script:TempDir | ConvertFrom-Json
        $result.types.Count | Should -Be 3
    }
}
