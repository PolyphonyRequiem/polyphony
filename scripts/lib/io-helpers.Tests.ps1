BeforeAll {
    . (Join-Path $PSScriptRoot 'io-helpers.ps1')
}

Describe 'Write-StderrWarning' {

    BeforeEach {
        # Redirect Console.Error globally for the duration of each test so
        # WARNING: lines do not pollute the surrounding test runner output.
        $script:errCapture = New-Object System.IO.StringWriter
        $script:origErr = [Console]::Error
        [Console]::SetError($script:errCapture)
    }

    AfterEach {
        [Console]::SetError($script:origErr)
        $script:errCapture.Dispose()
    }

    It 'Writes the message with WARNING: prefix to Console.Error' {
        Write-StderrWarning -Message 'sample notice'
        $script:errCapture.ToString().TrimEnd() | Should -Be 'WARNING: sample notice'
    }

    It 'Does not emit anything to stdout' {
        $stdout = Write-StderrWarning -Message 'should not appear on stdout'
        $stdout | Should -BeNullOrEmpty
    }

    It 'Does not emit a PowerShell Warning stream record' {
        # Stream 3 (warning) must NOT be polluted — that is the whole point of
        # this helper vs. Write-Warning. Under `pwsh -File`, stream 3 lands on
        # stdout and silently breaks JSON parsing in conductor.
        $captured = & {
            Write-StderrWarning -Message 'sample notice'
        } 3>&1
        @($captured | Where-Object { $_ -is [System.Management.Automation.WarningRecord] }).Count | Should -Be 0
    }

    It 'Accepts -Message positionally' {
        Write-StderrWarning 'positional message'
        $script:errCapture.ToString().TrimEnd() | Should -Be 'WARNING: positional message'
    }
}

# Note on the idempotent dot-source guard in io-helpers.ps1:
#   Verified indirectly by load-work-tree.Tests.ps1 ('Emits a warning about no
#   PG tags') and feature-pr-creator.Tests.ps1 ('Warns but continues when
#   workspace_hint differs from supplied branch'). Both tests pre-declare
#   `function global:Write-StderrWarning { ... }` in BeforeAll, then
#   `Mock Write-StderrWarning {}` inside the It block. If the script's
#   dot-source of io-helpers.ps1 redefined the function in script scope, the
#   mock would never be hit and the tests would fail. They pass — proving the
#   guard does what we need.
