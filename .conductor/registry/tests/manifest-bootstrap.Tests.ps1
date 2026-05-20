# Pester tests for .conductor/registry/scripts/manifest-bootstrap.ps1 (GH #166).
#
# Covers the platform_project drift validation added under GH #166 plus
# regression coverage for the existing reuse / fresh / root-mismatch paths.
#
# The script under test invokes `polyphony manifest read|init`. We pass
# `-PolyphonyExe <full-path-to-stub.ps1>` so the script resolves the stub
# via `Get-Command` and invokes it via `& $PolyphonyExe @Arguments`. The
# stub branches on $env:POLYPHONY_STUB_MODE and emits JSON matching the
# real ManifestReadResult / ManifestInitResult shapes documented in
# `src/Polyphony/Models/ManifestResults.cs`.

BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..' 'scripts' 'manifest-bootstrap.ps1'

    function script:New-PolyphonyStub {
        param([string]$Name = "manifest-bootstrap-stub-${PID}-$([guid]::NewGuid().ToString('N').Substring(0,8))")

        $stubDir = Join-Path ([System.IO.Path]::GetTempPath()) $Name
        New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
        $stub = Join-Path $stubDir 'polyphony.ps1'

        # The stub reads $env:POLYPHONY_STUB_MODE to decide which fixture
        # response to emit. Argv distinguishes read vs init. All fixtures
        # set $LASTEXITCODE deterministically via explicit `exit`.
        $stubBody = @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Argv)

$mode = $env:POLYPHONY_STUB_MODE
$verb = if ($Argv.Length -ge 2) { $Argv[1] } else { '' }
$storedPlatformProject = if ($env:POLYPHONY_STUB_STORED_PLATFORM_PROJECT) {
    $env:POLYPHONY_STUB_STORED_PLATFORM_PROJECT
} else {
    'dev.azure.com/StoredOrg/StoredProj'
}
$rootId = if ($env:POLYPHONY_STUB_ROOT_ID) { [int]$env:POLYPHONY_STUB_ROOT_ID } else { 999 }

function EmitJson($obj) {
    $obj | ConvertTo-Json -Compress -Depth 8
}

if ($verb -eq 'read') {
    switch ($mode) {
        'read_ok' {
            EmitJson @{
                path = '.polyphony/run.yaml'
                manifest = @{
                    schema = 1
                    root_id = $rootId
                    platform_project = $storedPlatformProject
                    created_at = '2026-01-01T00:00:00Z'
                    created_by = 'stub'
                    branch_model_version = 1
                    plan_generations = @{}
                    topology_hash = 'sha256:deadbeef'
                    merge_groups = @()
                }
                computed_topology_hash = 'sha256:deadbeef'
                topology_hash_matches = $true
            }
            exit 0
        }
        'read_not_found' {
            EmitJson @{ error_code = 'manifest_not_found'; error = 'no manifest at expected path' }
            exit 1
        }
        'read_root_mismatch' {
            EmitJson @{
                error_code = 'manifest_root_mismatch'
                error = "manifest root_id $rootId does not match apex"
                manifest_root_id = $rootId
            }
            exit 1
        }
        default {
            [Console]::Error.WriteLine("stub: unknown POLYPHONY_STUB_MODE '$mode' on read")
            exit 99
        }
    }
}

if ($verb -eq 'init') {
    switch ($mode) {
        'read_not_found' {
            # When the read path returned not_found, init follows and succeeds.
            # Read the requested platform-project off argv for round-trip fidelity.
            $platformIdx = [array]::IndexOf($Argv, '--platform-project')
            $platform = if ($platformIdx -ge 0 -and $platformIdx + 1 -lt $Argv.Length) { $Argv[$platformIdx + 1] } else { '' }
            EmitJson @{
                path = '.polyphony/run.yaml'
                root_id = $rootId
                platform_project = $platform
                created = $true
                created_by = 'stub'
                topology_hash = 'sha256:deadbeef'
            }
            exit 0
        }
        default {
            [Console]::Error.WriteLine("stub: init not expected for mode '$mode'")
            exit 99
        }
    }
}

[Console]::Error.WriteLine("stub: unknown verb '$verb'")
exit 99
'@

        Set-Content -Path $stub -Value $stubBody -Encoding UTF8
        return $stub
    }

    function script:Invoke-Bootstrap {
        param(
            [int]$ApexId,
            [string]$Organization = '',
            [string]$Project = '',
            [string]$PolyphonyExe,
            [string]$Mode,
            [string]$StoredPlatformProject = 'dev.azure.com/StoredOrg/StoredProj',
            [int]$StubRootId = 999
        )

        $env:POLYPHONY_STUB_MODE = $Mode
        $env:POLYPHONY_STUB_STORED_PLATFORM_PROJECT = $StoredPlatformProject
        $env:POLYPHONY_STUB_ROOT_ID = "$StubRootId"
        try {
            $argList = @('-NoProfile', '-File', $script:Script, '-ApexId', "$ApexId", '-PolyphonyExe', $PolyphonyExe)
            if ($Organization) { $argList += @('-Organization', $Organization) }
            if ($Project)      { $argList += @('-Project', $Project) }
            $out = pwsh @argList 2>&1
            return ($out | Out-String).Trim() | ConvertFrom-Json
        }
        finally {
            Remove-Item Env:POLYPHONY_STUB_MODE -ErrorAction SilentlyContinue
            Remove-Item Env:POLYPHONY_STUB_STORED_PLATFORM_PROJECT -ErrorAction SilentlyContinue
            Remove-Item Env:POLYPHONY_STUB_ROOT_ID -ErrorAction SilentlyContinue
        }
    }
}

Describe 'manifest-bootstrap.ps1 (GH #166: platform_project drift validation)' {

    Context 'Reuse path: validation enabled (Organization + Project both supplied)' {

        It 'Emits action=reused and validation=checked when platform_project matches' {
            $stub = New-PolyphonyStub
            try {
                $env = Invoke-Bootstrap -ApexId 42 -Organization 'StoredOrg' -Project 'StoredProj' `
                    -PolyphonyExe $stub -Mode 'read_ok' `
                    -StoredPlatformProject 'dev.azure.com/StoredOrg/StoredProj' -StubRootId 42

                $env.success | Should -BeTrue
                $env.action | Should -Be 'reused'
                $env.platform_project | Should -Be 'dev.azure.com/StoredOrg/StoredProj'
                $env.platform_project_validation | Should -Be 'checked'
                $env.error_code | Should -BeNullOrEmpty
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'Treats org/project as case-insensitive when comparing platform_project' {
            $stub = New-PolyphonyStub
            try {
                # Stored as one case, invoked as another — must still match.
                $env = Invoke-Bootstrap -ApexId 42 -Organization 'storedorg' -Project 'STOREDPROJ' `
                    -PolyphonyExe $stub -Mode 'read_ok' `
                    -StoredPlatformProject 'dev.azure.com/StoredOrg/StoredProj' -StubRootId 42

                $env.success | Should -BeTrue
                $env.action | Should -Be 'reused'
                $env.platform_project_validation | Should -Be 'checked'
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'Rejects with manifest_platform_project_mismatch when invocation differs from stored' {
            $stub = New-PolyphonyStub
            try {
                $env = Invoke-Bootstrap -ApexId 42 -Organization 'OtherOrg' -Project 'OtherProj' `
                    -PolyphonyExe $stub -Mode 'read_ok' `
                    -StoredPlatformProject 'dev.azure.com/StoredOrg/StoredProj' -StubRootId 42

                $env.success | Should -BeFalse
                $env.error_code | Should -Be 'manifest_platform_project_mismatch'
                $env.manifest_platform_project | Should -Be 'dev.azure.com/StoredOrg/StoredProj'
                $env.invocation_platform_project | Should -Be 'dev.azure.com/OtherOrg/OtherProj'
                $env.apex_id | Should -Be 42
                $env.error | Should -Match 'does not match invocation'
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Reuse path: validation disabled (both Organization + Project absent)' {

        It 'Emits action=reused and validation=skipped_absent without checking platform_project' {
            $stub = New-PolyphonyStub
            try {
                # Note: even though the stored platform_project would NOT match an
                # invocation against OtherOrg/OtherProj, the absence of args means
                # validation is intentionally skipped.
                $env = Invoke-Bootstrap -ApexId 42 -PolyphonyExe $stub -Mode 'read_ok' `
                    -StoredPlatformProject 'dev.azure.com/StoredOrg/StoredProj' -StubRootId 42

                $env.success | Should -BeTrue
                $env.action | Should -Be 'reused'
                $env.platform_project_validation | Should -Be 'skipped_absent'
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Reuse path: partial identity (exactly one of Organization/Project)' {

        It 'Rejects with invalid_inputs when Organization is supplied without Project' {
            $stub = New-PolyphonyStub
            try {
                $env = Invoke-Bootstrap -ApexId 42 -Organization 'OnlyOrg' `
                    -PolyphonyExe $stub -Mode 'read_ok' -StubRootId 42

                $env.success | Should -BeFalse
                $env.error_code | Should -Be 'invalid_inputs'
                $env.error | Should -Match 'organization and project'
                $env.apex_id | Should -Be 42
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'Rejects with invalid_inputs when Project is supplied without Organization' {
            $stub = New-PolyphonyStub
            try {
                $env = Invoke-Bootstrap -ApexId 42 -Project 'OnlyProj' `
                    -PolyphonyExe $stub -Mode 'read_ok' -StubRootId 42

                $env.success | Should -BeFalse
                $env.error_code | Should -Be 'invalid_inputs'
                $env.error | Should -Match 'organization and project'
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Regression: pre-existing routing preserved by the new validation block' {

        It 'Fresh init path still emits action=created when manifest absent' {
            $stub = New-PolyphonyStub
            try {
                $env = Invoke-Bootstrap -ApexId 42 -Organization 'FreshOrg' -Project 'FreshProj' `
                    -PolyphonyExe $stub -Mode 'read_not_found' -StubRootId 42

                $env.success | Should -BeTrue
                $env.action | Should -Be 'created'
                $env.platform_project | Should -Be 'dev.azure.com/FreshOrg/FreshProj'
                $env.root_id | Should -Be 42
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'manifest_root_mismatch error envelope still surfaces verbatim (AB#3067 guard)' {
            $stub = New-PolyphonyStub
            try {
                $env = Invoke-Bootstrap -ApexId 42 -Organization 'StoredOrg' -Project 'StoredProj' `
                    -PolyphonyExe $stub -Mode 'read_root_mismatch' -StubRootId 99

                $env.success | Should -BeFalse
                $env.error_code | Should -Be 'manifest_root_mismatch'
                $env.manifest_root_id | Should -Be 99
                $env.apex_id | Should -Be 42
            }
            finally { Remove-Item (Split-Path $stub -Parent) -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
