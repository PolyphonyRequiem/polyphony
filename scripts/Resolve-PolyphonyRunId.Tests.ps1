<#
Tests for scripts/Resolve-PolyphonyRunId.ps1 — the W1 launcher helpers
(AB#3275). Verifies pure-function behavior of:

  - Read-PolyphonyManifestRunId  (parses raw YAML)
  - Test-PolyphonyRunIdWellFormed (mirrors RunIdMint.IsWellFormed)
  - New-PolyphonyRunId            (mints ULIDs matching the C# shape)
  - Resolve-PolyphonyRunId        (manifest > env > mint precedence)
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Resolve-PolyphonyRunId.ps1')

    function New-TmpDir {
        $d = Join-Path ([System.IO.Path]::GetTempPath()) "runid-test-$([System.Guid]::NewGuid().ToString('N').Substring(0, 12))"
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        return $d
    }
}

Describe 'New-PolyphonyRunId' {

    It 'Emits a 26-character string' {
        (New-PolyphonyRunId).Length | Should -Be 26
    }

    It 'Emits only Crockford-base32 characters (no I/L/O/U)' {
        $id = New-PolyphonyRunId
        $id | Should -Match '^[0-9A-HJKMNP-TV-Z]{26}$'
    }

    It 'Produces distinct ids across many calls' {
        $ids = 1..50 | ForEach-Object { New-PolyphonyRunId }
        ($ids | Sort-Object -Unique).Count | Should -Be 50
    }

    It 'Encodes the timestamp prefix lexicographically (older < newer)' {
        $older = New-PolyphonyRunId -TimestampMs 1000
        $newer = New-PolyphonyRunId -TimestampMs 2000000
        ($older.Substring(0, 10)) | Should -BeLessThan ($newer.Substring(0, 10))
    }

    It 'Rejects out-of-range timestamps' {
        { New-PolyphonyRunId -TimestampMs ([long]0xFFFFFFFFFFFFF) } | Should -Throw
    }
}

Describe 'Test-PolyphonyRunIdWellFormed' {

    It 'Accepts a freshly minted ULID' {
        Test-PolyphonyRunIdWellFormed (New-PolyphonyRunId) | Should -BeTrue
    }

    It 'Accepts a known-good Crockford ULID' {
        Test-PolyphonyRunIdWellFormed '01JZ7P0X9KZBKQR4N3FVMT8YWA' | Should -BeTrue
    }

    It 'Rejects strings of the wrong length' {
        Test-PolyphonyRunIdWellFormed 'TOO-SHORT' | Should -BeFalse
        Test-PolyphonyRunIdWellFormed ('A' * 27)  | Should -BeFalse
    }

    It 'Rejects strings containing disallowed letters (I / L / O / U)' {
        Test-PolyphonyRunIdWellFormed '01JZ7P0X9KZBKQR4N3FVMT8YWI' | Should -BeFalse  # ends in I
        Test-PolyphonyRunIdWellFormed '01JZ7P0X9KZBKQR4N3FVMT8YWO' | Should -BeFalse  # ends in O
    }

    It 'Rejects $null and empty string' {
        Test-PolyphonyRunIdWellFormed $null | Should -BeFalse
        Test-PolyphonyRunIdWellFormed ''    | Should -BeFalse
    }
}

Describe 'Read-PolyphonyManifestRunId' {

    BeforeEach { $script:tmp = New-TmpDir }
    AfterEach  { Remove-Item -Recurse -Force $script:tmp -ErrorAction SilentlyContinue }

    It 'Returns the run_id when the key is present' {
        $f = Join-Path $script:tmp 'run.yaml'
        "schema: 2`nroot_id: 1234`nrun_id: 01JZ7P0X9KZBKQR4N3FVMT8YWA`n" | Set-Content -Path $f
        Read-PolyphonyManifestRunId -ManifestPath $f | Should -Be '01JZ7P0X9KZBKQR4N3FVMT8YWA'
    }

    It 'Returns $null when the file does not exist' {
        Read-PolyphonyManifestRunId -ManifestPath (Join-Path $script:tmp 'nope.yaml') | Should -BeNullOrEmpty
    }

    It 'Returns $null when the key is missing' {
        $f = Join-Path $script:tmp 'run.yaml'
        "schema: 1`nroot_id: 1234`n" | Set-Content -Path $f
        Read-PolyphonyManifestRunId -ManifestPath $f | Should -BeNullOrEmpty
    }

    It 'Returns $null when the value is literal "null" or "~"' {
        $f = Join-Path $script:tmp 'run.yaml'
        "schema: 2`nrun_id: null`n" | Set-Content -Path $f
        Read-PolyphonyManifestRunId -ManifestPath $f | Should -BeNullOrEmpty

        "schema: 2`nrun_id: ~`n" | Set-Content -Path $f
        Read-PolyphonyManifestRunId -ManifestPath $f | Should -BeNullOrEmpty
    }

    It 'Strips matched quotes around the value' {
        $f = Join-Path $script:tmp 'run.yaml'
        "schema: 2`nrun_id: `"01JZ7P0X9KZBKQR4N3FVMT8YWA`"`n" | Set-Content -Path $f
        Read-PolyphonyManifestRunId -ManifestPath $f | Should -Be '01JZ7P0X9KZBKQR4N3FVMT8YWA'
    }

    It 'Ignores nested run_id: inside multi-line collections' {
        $f = Join-Path $script:tmp 'run.yaml'
        # Top-level key absent; an indented `run_id:` inside `rebases:` must not match.
        "schema: 2`nrebases:`n  - branch: mg/1-data`n    run_id: SHOULD-NOT-MATCH`n" | Set-Content -Path $f
        Read-PolyphonyManifestRunId -ManifestPath $f | Should -BeNullOrEmpty
    }
}

Describe 'Resolve-PolyphonyRunId' {

    BeforeEach {
        $script:tmp = New-TmpDir
        Remove-Item Env:POLYPHONY_RUN_ID -ErrorAction SilentlyContinue
    }
    AfterEach {
        Remove-Item -Recurse -Force $script:tmp -ErrorAction SilentlyContinue
        Remove-Item Env:POLYPHONY_RUN_ID -ErrorAction SilentlyContinue
    }

    It 'Source=manifest when a manifest run_id exists' {
        $f = Join-Path $script:tmp 'run.yaml'
        "schema: 2`nrun_id: 01JZ7P0X9KZBKQR4N3FVMT8YWA`n" | Set-Content -Path $f
        $r = Resolve-PolyphonyRunId -ManifestPath $f
        $r.RunId  | Should -Be '01JZ7P0X9KZBKQR4N3FVMT8YWA'
        $r.Source | Should -Be 'manifest'
    }

    It 'Source=minted when no manifest exists' {
        $r = Resolve-PolyphonyRunId -ManifestPath (Join-Path $script:tmp 'absent.yaml')
        $r.RunId  | Should -Match '^[0-9A-HJKMNP-TV-Z]{26}$'
        $r.Source | Should -Be 'minted'
    }

    It 'Source=external when POLYPHONY_RUN_ID is exported' {
        $env:POLYPHONY_RUN_ID = '01JZ7P0X9KZBKQR4N3FVMT8YEX'
        $r = Resolve-PolyphonyRunId -ManifestPath $null
        $r.RunId  | Should -Be '01JZ7P0X9KZBKQR4N3FVMT8YEX'
        $r.Source | Should -Be 'external'
    }

    It 'External env beats manifest when -RespectExisting is the default' {
        $f = Join-Path $script:tmp 'run.yaml'
        "schema: 2`nrun_id: 01JZ7P0X9KZBKQR4N3FVMT8YWA`n" | Set-Content -Path $f
        $env:POLYPHONY_RUN_ID = '01JZ7P0X9KZBKQR4N3FVMT8YEX'
        $r = Resolve-PolyphonyRunId -ManifestPath $f
        $r.RunId  | Should -Be '01JZ7P0X9KZBKQR4N3FVMT8YEX'
        $r.Source | Should -Be 'external'
    }

    It '-RespectExisting:$false bypasses the env and reads the manifest' {
        $f = Join-Path $script:tmp 'run.yaml'
        "schema: 2`nrun_id: 01JZ7P0X9KZBKQR4N3FVMT8YWA`n" | Set-Content -Path $f
        $env:POLYPHONY_RUN_ID = '01JZ7P0X9KZBKQR4N3FVMT8YEX'
        $r = Resolve-PolyphonyRunId -ManifestPath $f -RespectExisting:$false
        $r.RunId  | Should -Be '01JZ7P0X9KZBKQR4N3FVMT8YWA'
        $r.Source | Should -Be 'manifest'
    }
}
