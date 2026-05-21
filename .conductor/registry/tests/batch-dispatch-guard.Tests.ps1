# wave-dispatch-guard.Tests.ps1
#
# Unit tests for .conductor/registry/scripts/wave-dispatch-guard.ps1.
# The guard is a routing-style helper invoked by apex-driver
# (`reset_wave_failure_flags` / -Op clear) and by apex-wave-dispatch
# (`check_prior_wave_status` / -Op check, `record_wave_failure_flag` /
# -Op record). It owns the per-root sentinel store under
# `<git-common-dir>/polyphony/<root_id>/wave-failures/`.
#
# These tests exercise the actual script in a throwaway git worktree so
# the `git rev-parse --path-format=absolute --git-common-dir` invocation
# returns a real, predictable path. We use a high root_id (>= 90000000)
# to avoid colliding with any live or stale apex run sentinels.

BeforeAll {
    $script:GuardScript = Resolve-Path "$PSScriptRoot/../scripts/wave-dispatch-guard.ps1"
    $script:GuardScript | Should -Exist

    # Spin up a throwaway git repo so git rev-parse resolves to a
    # predictable common-dir. Each test class gets a fresh worktree to
    # keep cleanup simple.
    $script:WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) "wave-dispatch-guard-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:WorkRoot -Force | Out-Null
    Push-Location $script:WorkRoot
    try {
        git init --quiet 2>&1 | Out-Null
        # init creates .git/ — that IS the common dir for a non-worktree repo.
        git config user.email 'test@example.com' 2>&1 | Out-Null
        git config user.name  'wave-dispatch-guard-tests' 2>&1 | Out-Null
    }
    finally {
        Pop-Location
    }
    $script:OriginalLocation = (Get-Location).Path
    Set-Location $script:WorkRoot

    # Define the helper at script scope so every Describe/It block can
    # resolve it. Pester runs each Describe in its own scope; functions
    # defined at file scope are NOT visible inside the blocks.
    function script:Invoke-Guard {
        param(
            [Parameter(Mandatory)] [string] $Op,
            [Parameter(Mandatory)] [int] $RootId,
            [int] $WaveIndex = -1,
            [string] $Reason = ''
        )
        $args = @('-NoProfile', '-File', $script:GuardScript.Path, '-Op', $Op, '-RootId', $RootId)
        if ($WaveIndex -ge 0) { $args += @('-WaveIndex', $WaveIndex) }
        if ($Reason)          { $args += @('-Reason', $Reason) }
        $raw = & pwsh @args
        return ($raw | ConvertFrom-Json)
    }
}

AfterAll {
    Set-Location $script:OriginalLocation
    if (Test-Path $script:WorkRoot) {
        Remove-Item -Recurse -Force $script:WorkRoot -ErrorAction SilentlyContinue
    }
}

Describe 'wave-dispatch-guard.ps1 — clear op' {
    It 'returns ok envelope with cleared=true when no flag dir exists' {
        $env = Invoke-Guard -Op clear -RootId 90000001
        $env.op           | Should -Be 'clear'
        $env.root_id      | Should -Be 90000001
        $env.cleared      | Should -BeTrue
        $env.error_code   | Should -BeNullOrEmpty
        $env.flag_dir     | Should -Match 'wave-failures'
    }

    It 'wipes existing flag files when the dir exists' {
        Invoke-Guard -Op record -RootId 90000002 -WaveIndex 0 -Reason 'failure' | Out-Null
        Invoke-Guard -Op record -RootId 90000002 -WaveIndex 1 -Reason 'failure' | Out-Null
        $checkBefore = Invoke-Guard -Op check -RootId 90000002
        $checkBefore.blocked | Should -BeTrue

        $env = Invoke-Guard -Op clear -RootId 90000002
        $env.cleared | Should -BeTrue

        $checkAfter = Invoke-Guard -Op check -RootId 90000002
        $checkAfter.blocked | Should -BeFalse
    }
}

Describe 'wave-dispatch-guard.ps1 — check op' {
    It 'reports blocked=false when no flag dir exists' {
        Invoke-Guard -Op clear -RootId 90000010 | Out-Null
        $env = Invoke-Guard -Op check -RootId 90000010
        $env.op           | Should -Be 'check'
        $env.blocked      | Should -BeFalse
        $env.first_reason | Should -BeNullOrEmpty
    }

    It 'reports blocked=true with the oldest flag basename as first_reason' {
        Invoke-Guard -Op clear -RootId 90000011 | Out-Null
        Invoke-Guard -Op record -RootId 90000011 -WaveIndex 0 -Reason 'failure' | Out-Null
        Start-Sleep -Milliseconds 50
        Invoke-Guard -Op record -RootId 90000011 -WaveIndex 2 -Reason 'failure' | Out-Null

        $env = Invoke-Guard -Op check -RootId 90000011
        $env.blocked      | Should -BeTrue
        $env.first_reason | Should -Be 'wave-0-failure'
    }
}

Describe 'wave-dispatch-guard.ps1 — record op' {
    It 'creates the flag dir on first record' {
        Invoke-Guard -Op clear -RootId 90000020 | Out-Null
        $env = Invoke-Guard -Op record -RootId 90000020 -WaveIndex 0 -Reason 'failure'
        $env.recorded  | Should -BeTrue
        $env.flag_path | Should -Match 'wave-0-failure\.flag$'
        Test-Path $env.flag_path | Should -BeTrue
    }

    It 'embeds wave_index, reason, and a timestamp in the flag payload' {
        Invoke-Guard -Op clear -RootId 90000021 | Out-Null
        $env = Invoke-Guard -Op record -RootId 90000021 -WaveIndex 7 -Reason 'failure'
        # ConvertFrom-Json eagerly coerces ISO 8601 to [DateTime] in
        # PowerShell 7+; verify the field is present and round-trips
        # to a real datetime rather than asserting a string regex.
        $payload = (Get-Content -LiteralPath $env.flag_path -Raw) | ConvertFrom-Json
        $payload.wave_index  | Should -Be 7
        $payload.reason      | Should -Be 'failure'
        $payload.recorded_at | Should -Not -BeNullOrEmpty
        ([DateTime]$payload.recorded_at).Year | Should -BeGreaterThan 2024
    }

    It 'sanitizes path-traversal attempts in the reason filename' {
        Invoke-Guard -Op clear -RootId 90000022 | Out-Null
        $evil = '../../etc/passwd; rm -rf ~'
        $env = Invoke-Guard -Op record -RootId 90000022 -WaveIndex 0 -Reason $evil
        $env.recorded  | Should -BeTrue
        # The flag file must live INSIDE the per-root flag dir, not
        # outside it. Resolve and assert containment.
        $flagDirResolved = (Resolve-Path -LiteralPath $env.flag_dir).Path
        $flagPathResolved = (Resolve-Path -LiteralPath $env.flag_path).Path
        $flagPathResolved.StartsWith($flagDirResolved, [StringComparison]::OrdinalIgnoreCase) | Should -BeTrue
        # And the basename must not contain `/` or `\`.
        (Split-Path -Leaf $env.flag_path) | Should -Not -Match '[/\\]'
    }

    It 'uses "unspecified" when reason is empty' {
        Invoke-Guard -Op clear -RootId 90000023 | Out-Null
        $env = Invoke-Guard -Op record -RootId 90000023 -WaveIndex 0
        $env.recorded  | Should -BeTrue
        $env.flag_path | Should -Match 'wave-0-unspecified\.flag$'
    }
}

Describe 'wave-dispatch-guard.ps1 — cross-root isolation' {
    It 'flags written for root A do not block checks for root B' {
        Invoke-Guard -Op clear -RootId 90000030 | Out-Null
        Invoke-Guard -Op clear -RootId 90000031 | Out-Null
        Invoke-Guard -Op record -RootId 90000030 -WaveIndex 0 -Reason 'failure' | Out-Null

        $envA = Invoke-Guard -Op check -RootId 90000030
        $envB = Invoke-Guard -Op check -RootId 90000031
        $envA.blocked | Should -BeTrue
        $envB.blocked | Should -BeFalse
    }
}

Describe 'wave-dispatch-guard.ps1 — envelope shape' {
    It 'always emits the same top-level keys regardless of op' {
        $expectedKeys = @('op', 'root_id', 'flag_dir', 'blocked', 'first_reason',
                          'cleared', 'recorded', 'flag_path', 'error_code', 'error_message')
        foreach ($op in @('clear', 'check', 'record')) {
            $env = if ($op -eq 'record') {
                Invoke-Guard -Op $op -RootId 90000040 -WaveIndex 0 -Reason 'failure'
            } else {
                Invoke-Guard -Op $op -RootId 90000040
            }
            $env | Should -Not -BeNullOrEmpty
            foreach ($k in $expectedKeys) {
                $env.PSObject.Properties.Name | Should -Contain $k
            }
        }
    }
}
